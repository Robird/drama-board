using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DramaBoard.Playground.Cli;

/// <summary>Read-only character view over one proven host-journal prefix. Never loads model raw files or a writable world.</summary>
internal static class CharacterHistoryExporter {
    private static readonly JsonSerializerOptions Json = new() {
        Converters = { new JsonStringEnumConverter() },
        RespectRequiredConstructorParameters = true
    };

    public static void Export(string journalPath, string branchId, string actorId, TextWriter output,
        string? feedbackPath = null) {
        ArgumentNullException.ThrowIfNull(output);
        PlaygroundHostJournalSnapshot snapshot = PlaygroundHostJournal.Read(journalPath);
        IReadOnlyList<FeedbackRecord>? feedback = null;
        string? feedbackGap = null;
        if (feedbackPath is not null) {
            try {
                FeedbackJournal.Snapshot reports = FeedbackJournal.ReadSnapshot(feedbackPath);
                feedback = reports.Records;
                if (reports.IgnoredTailBytes > 0) {
                    feedbackGap = $"附属反馈尾部 {reports.IgnoredTailBytes} 字节未完成，未作为成功保存的反馈读取。";
                }
            } catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) {
                feedbackGap = "附属反馈文件不可读；人物主线仍来自宿主日志。";
            }
        }
        output.Write(RenderCore(snapshot, branchId, actorId, feedback, feedbackGap));
    }

    public static string Render(PlaygroundHostJournalSnapshot snapshot, string branchId, string actorId,
        IReadOnlyList<FeedbackRecord>? feedback = null) => RenderCore(snapshot, branchId, actorId, feedback, null);

    private static string RenderCore(PlaygroundHostJournalSnapshot snapshot, string branchId, string actorId,
        IReadOnlyList<FeedbackRecord>? feedback, string? feedbackGap) {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        var gaps = new List<string>();
        if (feedbackGap is not null) { gaps.Add(feedbackGap); }
        IReadOnlyList<PlaygroundHostRecord> selected = Select(snapshot, branchId, gaps);
        if (!selected.Any(record => record.ActorId == actorId || record.Kind == "run-start" && HasActor(record.Payload, actorId))) {
            throw new ArgumentException("选定分支的宿主日志中不存在此角色；不能从当前世界补造角色历史。", nameof(actorId));
        }
        PlaygroundHostRecord[] visible = selected.Where(record => record.ActorId == actorId ||
            record.ActorId is null && record.Kind is "run-start" or "run-end" or "branch-fork").OrderBy(record => record.Sequence).ToArray();
        var text = new StringBuilder("# 角色 History\n\n");
        text.Append("角色 ").Append(Literal(actorId)).Append(" · 分支 ").Append(Literal(branchId))
            .Append(" · JournalId ").Append(Literal(snapshot.JournalId)).AppendLine().AppendLine();
        text.AppendLine("追加顺序是宿主保存顺序，不等于模拟时间或因果。世界 E/S 为恢复权威；未见 Completed 不证明世界未执行。")
            .AppendLine("模型输入行仅证明输入已经构造并保存，不证明调用已开始或远端已收到。reasoning 仅取 Completion.PlainText，可能是明文、摘要或无可读内容。普通 Response 不自动等同角色对白。")
            .AppendLine("原始模型审计未验证；本导出不打开 raw 文件。宿主 history 是开发者视图，不自动交付其他角色或回灌记忆。").AppendLine();
        if (snapshot.DiscardedTailBytes > 0) {
            gaps.Add($"宿主日志尾部 {snapshot.DiscardedTailBytes} 字节未完成；只读忽略，原文件未修改，尾部结果未知。");
        }

        var openings = new Dictionary<(string?, string?, long), string>();
        var observations = new Dictionary<(string?, string?, long), string>();
        var items = new Dictionary<(string?, string?, long, long), string>();
        (string? Branch, string? Actor, long? Context)? group = null;
        foreach (PlaygroundHostRecord record in visible) {
            var nextGroup = (record.BranchId, record.ActorId, record.ContextSequence);
            if (group != nextGroup) {
                text.Append("## 来源分支 ").Append(Literal(record.BranchId)).Append(" · 角色 ").Append(Literal(record.ActorId))
                    .Append(" · Context ").Append(record.ContextSequence?.ToString(CultureInfo.InvariantCulture) ?? "—").AppendLine().AppendLine();
                group = nextGroup;
            }
            // Small stable evidence marker for diagnostics and smoke checks; no payload or provider-native data.
            text.Append("<!-- host-record ").Append(JsonSerializer.Serialize(new {
                record.Sequence, record.Kind, record.BranchId, record.ActorId, record.RunId, record.ContextSequence
            })).AppendLine(" -->");
            text.Append("### Sequence ").Append(record.Sequence).Append(" · ").Append(record.Kind).AppendLine().AppendLine();
            text.Append("RunId ").Append(Literal(record.RunId)).AppendLine().AppendLine();
            switch (record.Kind) {
                case "run-start":
                    RenderRunStart(record);
                    break;
                case "run-end":
                    text.Append("运行结束：").Append(Literal(String(record.Payload, "Reason")))
                        .Append("；完成 S ").Append(Literal(String(record.Payload, "StateCommitId"))).AppendLine().AppendLine();
                    break;
                case "branch-fork":
                    text.Append("分叉来源 ").Append(Literal(String(record.Payload, "SourceBranchId")))
                        .Append("；宿主前缀截至 Sequence ").Append(Number(record.Payload, "SourceThroughSequence"))
                        .Append("；来源 S ").Append(Literal(String(record.Payload, "SourceStateCommitId")))
                        .Append("；来源请求 ").Append(Literal(String(record.Payload, "SourceRequestId"))).AppendLine().AppendLine();
                    break;
                case "character-material":
                    RenderMaterial(record, Read<CharacterMaterialSnapshot>(record));
                    break;
                case "model-request":
                    RenderRequest(record, Read<ModelRequestSnapshot>(record));
                    break;
                case "model-response":
                case "model-failure":
                    RenderOutcome(record, Read<ModelOutcomeSnapshot>(record));
                    break;
                case "operation-received":
                    RenderReceived(record, Read<PlaygroundOperationReceipt>(record));
                    break;
                case "operation-completed":
                    RenderCompleted(record);
                    break;
                case "feedback-saved":
                    RenderFeedback(record);
                    break;
                default:
                    gaps.Add($"Sequence {record.Sequence} 的种类 {record.Kind} 没有阅读投影，未转储原 Payload。");
                    break;
            }
        }
        Diagnose(visible, gaps);
        text.AppendLine("## 证据缺口与覆盖范围").AppendLine();
        if (gaps.Count == 0) {
            text.AppendLine("在选定前缀内未发现可检查的配对缺口；这不证明覆盖全部世界历史、输出已交付或模型结论正确。");
        } else {
            foreach (string gap in gaps.Distinct(StringComparer.Ordinal)) { text.Append("- ").AppendLine(gap); }
        }
        return text.ToString();

        void RenderRunStart(PlaygroundHostRecord record) {
            JsonElement payload = record.Payload;
            bool? detailed = Boolean(payload, "DetailedRecording");
            text.Append("运行起点完成 S ").Append(Literal(String(payload, "StateCommitId")))
                .Append("；详细人物记录 ").Append(detailed == true ? "record" : detailed == false ? "off" : "未知").AppendLine().AppendLine();
            if (detailed != true) {
                gaps.Add($"运行 {Literal(record.RunId)} 未启用或未声明详细人物记录；观察及模型阅读可能缺失，不从 raw/当前世界补造。");
            }
            text.Append("原始模型审计定位（未验证）：").Append(Literal(String(payload, "AuditPath"))).AppendLine().AppendLine();
            if (Property(payload, "Actors") is { ValueKind: JsonValueKind.Array } actors) {
                foreach (JsonElement actor in actors.EnumerateArray().Where(actor => String(actor, "ActorId") == actorId)) {
                    text.Append("本次角色控制器 ").Append(Literal(String(actor, "Controller")))
                        .Append("；连接 ").Append(Literal(String(actor, "ConnectionId"))).AppendLine().AppendLine();
                }
            }
            text.AppendLine("覆盖从本次保存的边界开始；此前宿主历史未由这个 run-start 证明。").AppendLine();
        }

        void RenderMaterial(PlaygroundHostRecord record, CharacterMaterialSnapshot material) {
            if (record.ContextSequence != material.ContextSequence) {
                throw new InvalidDataException("Character material ContextSequence does not match its host envelope.");
            }
            text.Append("Context 状态 ").Append(material.Status).Append("；捕获完成 S ")
                .Append(Literal(material.StateCommitId)).AppendLine("（不是所有操作的原提交点）。").AppendLine();
            text.Append("捕获世界位置：lineage ").Append(Token(material.LineageId)).Append("；transition ")
                .Append(Token(material.TransitionCount)).Append("；tick ").Append(Token(material.TimeTicks))
                .AppendLine("；这些是捕获时的世界边界，不是下方冻结 Observation 的观察时间。").AppendLine();
            var identity = (record.BranchId, record.ActorId, material.ContextSequence);
            if (!observations.TryGetValue(identity, out string? observed) || observed != material.ObservationText) {
                text.AppendLine("#### Observation（捕获时冻结观察）").AppendLine();
                Fence(text, material.ObservationText);
                observations[identity] = material.ObservationText;
            }
            if (!openings.TryGetValue(identity, out string? opening) || opening != material.Opening.Text) {
                text.AppendLine(opening is null ? "#### Opening（已交付材料）" : "#### Opening（同一身份再次捕获，呈现变化）").AppendLine();
                if (material.Opening.PreviousAdvance is { } advance) {
                    text.Append("承接 Context ").Append(advance.SourceContextSequence).Append(" 的操作 ").Append(advance.OperationSequence)
                        .Append(" / ").Append(Literal(advance.OperationRecordId))
                        .AppendLine("；不据此将每项被动经历归因于该操作。").AppendLine();
                }
                text.Append("ExperienceSequences：").AppendLine(string.Join(", ", material.Opening.ExperienceSequences)).AppendLine();
                Fence(text, material.Opening.Text);
                openings[identity] = material.Opening.Text;
            }
            foreach (CharacterItemSnapshot item in material.Items) {
                var key = (record.BranchId, record.ActorId, material.ContextSequence, item.ItemSequence);
                if (items.TryGetValue(key, out string? prior) && prior == item.Text) { continue; }
                text.Append("#### Item ").Append(item.ItemSequence).AppendLine(prior is null ? "" : "（同一身份再次捕获，呈现变化）").AppendLine();
                text.Append("OperationSequence ").Append(item.OperationSequence?.ToString(CultureInfo.InvariantCulture) ?? "—")
                    .Append("；OperationRecordId ").Append(Literal(item.OperationRecordId))
                    .Append("；ExperienceSequences ").AppendLine(string.Join(", ", item.ExperienceSequences)).AppendLine();
                Fence(text, item.Text);
                items[key] = item.Text;
            }
            if (material.Status == "Closed") {
                text.AppendLine("Context 已关闭；尚未交付的经历未包含在这份材料中。").AppendLine();
            }
        }

        void RenderRequest(PlaygroundHostRecord record, ModelRequestSnapshot request) {
            if (request.Boundary is { } captured && (captured.BranchId != record.BranchId ||
                captured.ActorId != record.ActorId || captured.ContextSequence != record.ContextSequence)) {
                throw new InvalidDataException("Model request boundary does not match its host envelope.");
            }
            ModelIdentity(request.RequestId, request.CallNumber, request.ConnectionId, request.ModelId, request.ApiSpecId, request.RawRecordSequence);
            if (request.Boundary is { } boundary) {
                text.Append("决定前 S ").Append(Literal(boundary.StateCommitId)).Append("；transition ")
                    .Append(boundary.TransitionCount).Append("；tick ").Append(boundary.TimeTicks).AppendLine().AppendLine();
            }
            text.AppendLine("以下为构造并保存的实际输入阅读投影；Tail 是携带材料，既有 assistant 内容不是本次新输出。输入中的协议纠正不表示世界事件。").AppendLine();
            foreach (ModelMessageSnapshot message in request.Shared.Concat(request.Tail)) {
                text.Append("#### ").Append(message.Section).Append('[').Append(message.MessageIndex).Append("] · ")
                    .Append(message.Kind).AppendLine().AppendLine();
                if (message.Content is not null) { Fence(text, message.Content); }
                RenderBlocks(message.Blocks, carried: true);
                foreach (ModelToolResultSnapshot result in message.Results) {
                    text.Append("工具收据 ").Append(result.ResultIndex).Append(" · ").Append(Literal(result.ToolName))
                        .Append(" / ").Append(Literal(result.ToolCallId)).Append(" · ").Append(result.Status).AppendLine().AppendLine();
                    RenderBlocks(result.Blocks, carried: true);
                }
                if (message.ProjectionGap is not null) { ProjectionGap(message.ProjectionGap); }
            }
        }

        void RenderOutcome(PlaygroundHostRecord record, ModelOutcomeSnapshot outcome) {
            ModelIdentity(outcome.RequestId, outcome.CallNumber, outcome.ConnectionId, outcome.ModelId, outcome.ApiSpecId, outcome.RawRecordSequence);
            text.Append("阶段 ").Append(outcome.Stage).Append("；终止 ").Append(outcome.Termination?.Kind ?? "未捕获完整结果")
                .Append("；协议事实 ").Append(Literal(outcome.ProtocolError)).AppendLine().AppendLine();
            if (outcome.Invocation is { } invocation) {
                text.Append("实际调用来源 ").Append(Literal(invocation.ProviderId)).Append(" / ").Append(Literal(invocation.ApiSpecId))
                    .Append(" / ").Append(Literal(invocation.Model)).AppendLine().AppendLine();
            }
            if (outcome.Termination is { } termination) {
                text.Append("Provider reason ").Append(Literal(termination.ProviderReason)).Append("；detail ")
                    .Append(Literal(termination.Detail)).AppendLine().AppendLine();
            }
            if (outcome.Result is null) {
                text.AppendLine("完整模型结果未捕获；远端结果未知。没有可读内容不表示未思考。").AppendLine();
            } else {
                if (outcome.Termination?.Kind != "Completed") {
                    text.AppendLine("下列输出属于未成功完成的调用证据，不作为成功行动。").AppendLine();
                }
                RenderBlocks(outcome.Result.Blocks, carried: false);
                foreach (string error in outcome.Result.Errors ?? []) {
                    text.Append("Completion error：").Append(Literal(error)).AppendLine().AppendLine();
                }
                if (outcome.Result.Failure is { } failure) {
                    text.Append("Failure ").Append(failure.Kind).Append("；HTTP ").Append(failure.HttpStatusCode)
                        .Append("；provider code ").Append(Literal(failure.ProviderCode)).AppendLine().AppendLine();
                }
            }
            if (outcome.Usage is { } usage) {
                text.Append("Usage（null=未知）：uncached=").Append(Token(usage.UncachedInputTokens))
                    .Append("；cache-create=").Append(Token(usage.CacheCreationInputTokens)).Append("；cache-read=")
                    .Append(Token(usage.CacheReadInputTokens)).Append("；output=").Append(Token(usage.OutputTokens)).AppendLine().AppendLine();
            }
            if (outcome.ProposedOperation is { } operation) {
                text.Append("候选 OperationRecordId ").Append(Literal(operation.OperationRecordId)).Append(" / ToolCallId ")
                    .Append(Literal(operation.ToolCallId)).AppendLine("；仅模型候选，接收及提交以独立操作记录为准。").AppendLine();
                Fence(text, operation.RawInput);
                if (!visible.Any(candidate => candidate.BranchId == record.BranchId && candidate.Kind == "operation-received" &&
                    String(candidate.Payload, "OperationRecordId") == operation.OperationRecordId)) {
                    gaps.Add($"模型 Sequence {record.Sequence} 的候选操作未见本前缀 Received；不推断是否执行。");
                }
            }
        }

        void RenderReceived(PlaygroundHostRecord record, PlaygroundOperationReceipt receipt) {
            text.Append("已接收操作 ").Append(Literal(receipt.OperationRecordId)).Append("；batch ").Append(Literal(receipt.BatchId))
                .Append("；tool ").Append(Literal(receipt.ToolName)).Append(" / ").Append(Literal(receipt.ToolCallId)).AppendLine().AppendLine();
            Fence(text, receipt.RawInput);
            if (!visible.Any(candidate => candidate.BranchId == record.BranchId && candidate.Kind == "operation-completed" &&
                String(candidate.Payload, "OperationRecordId") == receipt.OperationRecordId)) {
                gaps.Add($"操作 {Literal(receipt.OperationRecordId)} 在选定前缀未见 Completed；不证明世界未执行。");
            }
        }

        void RenderCompleted(PlaygroundHostRecord record) {
            string id = RequiredString(record.Payload, "OperationRecordId");
            PlaygroundOperationResult result = Required(record.Payload, "Completed").Deserialize<PlaygroundOperationResult>(Json)
                ?? throw new InvalidDataException("An operation-completed payload is null.");
            text.Append("操作回应 ").Append(Literal(id)).Append("；状态 ").Append(result.Status).Append("；ExactResult=")
                .Append(result.ExactResult ? "true" : "false").AppendLine().AppendLine();
            Fence(text, result.Result);
            text.Append("WorldVersion ").Append(Literal(result.WorldVersion)).Append("；DomainEvent ")
                .Append(Literal(result.DomainEvent)).AppendLine().AppendLine();
            if (result.Status == PlaygroundOperationStatus.Interrupted) {
                text.AppendLine("Interrupted：未证实游戏提交，输入未自动重做。").AppendLine();
            } else if (string.IsNullOrWhiteSpace(result.DomainEvent)) {
                text.AppendLine("无领域事件引用：这是宿主操作回应，例如查询或元命令，不作为世界提交证明。").AppendLine();
            } else {
                text.AppendLine("回应带领域事件引用；成功/拒绝以保存结论为准，捕获 S 不替代原提交点。").AppendLine();
            }
            if (!result.ExactResult || Boolean(record.Payload, "Recovered") == true) {
                text.AppendLine("恢复补录：使用本次追加位置，ExactResult=false 时不保证重现原始回执文本。").AppendLine();
            }
            if (!visible.Any(candidate => candidate.BranchId == record.BranchId && candidate.Kind == "operation-received" &&
                String(candidate.Payload, "OperationRecordId") == id)) {
                gaps.Add($"Completed Sequence {record.Sequence} 在本前缀未见对应 Received。");
            }
        }

        void RenderFeedback(PlaygroundHostRecord record) {
            string id = RequiredString(record.Payload, "FeedbackId");
            text.Append("反馈已保存 ").Append(Literal(id)).AppendLine("；不等于回执已经展示、进入下次输入或远端收到。").AppendLine();
            FeedbackRecord[] matches = feedback?.Where(report => report.FeedbackId == id).ToArray() ?? [];
            if (matches.Length == 1 && FeedbackMatches(matches[0], record)) {
                Fence(text, matches[0].Text);
            } else {
                gaps.Add($"反馈 {Literal(id)} 的正文附件缺失、不可读或身份不一致；独立 --feedbacks 可另行查询。");
            }
        }

        static bool FeedbackMatches(FeedbackRecord report, PlaygroundHostRecord record) =>
            report.ActorId == record.ActorId && report.RunId == record.RunId && report.ContextSequence == record.ContextSequence &&
            (record.BranchId == "ephemeral" ? report.Boundary is null : report.Boundary is { } boundary &&
                boundary.BranchId == record.BranchId && boundary.ActorId == record.ActorId && boundary.ContextSequence == record.ContextSequence) &&
            (String(record.Payload, "RequestId") is not { } requestId || report.Model?.RequestId == requestId);

        void ModelIdentity(string id, int call, string? connection, string model, string? api, long? rawSequence) {
            text.Append("RequestId ").Append(Literal(id)).Append("；call ").Append(call).Append("；connection ")
                .Append(Literal(connection)).Append("；model ").Append(Literal(model)).Append("；API ").Append(Literal(api))
                .Append("；raw sequence ").Append(rawSequence?.ToString(CultureInfo.InvariantCulture) ?? "—").AppendLine("（未验证）。").AppendLine();
        }

        void RenderBlocks(IReadOnlyList<ModelBlockSnapshot> blocks, bool carried) {
            foreach (ModelBlockSnapshot block in blocks) {
                string prefix = carried ? "携带的既有内容 · " : "";
                text.Append("块 ").Append(block.BlockIndex).Append(" · ").Append(prefix);
                switch (block.Kind) {
                    case "Text":
                        text.AppendLine(carried ? "Text" : "Response").AppendLine();
                        Fence(text, block.Text ?? "");
                        break;
                    case "Reasoning":
                        text.AppendLine("Reasoning (PlainText)").AppendLine();
                        if (block.PlainText is null) {
                            text.AppendLine("此 reasoning 块没有可读 PlainText；不表示未思考，native 内容未投影。").AppendLine();
                        } else { Fence(text, block.PlainText); }
                        break;
                    case "ToolCall" when block.ToolCall is { } call:
                        text.Append("工具调用 ").Append(Literal(call.ToolName)).Append(" / ").Append(Literal(call.ToolCallId))
                            .Append(" · ").Append(call.InputKind).AppendLine().AppendLine();
                        Fence(text, call.RawInput);
                        break;
                    default:
                        text.AppendLine("投影缺口").AppendLine();
                        ProjectionGap(block.ProjectionGap ?? "Unsupported block kind " + block.Kind);
                        break;
                }
            }
        }

        void ProjectionGap(string gap) {
            text.Append("可读投影缺口：").Append(Literal(gap)).AppendLine("；未转储原对象。").AppendLine();
            gaps.Add("存在未支持的消息或多模态/内容块阅读投影；只保留类型及缺口。");
        }
    }

    private static IReadOnlyList<PlaygroundHostRecord> Select(PlaygroundHostJournalSnapshot snapshot, string branch,
        List<string> gaps) {
        var selected = new List<PlaygroundHostRecord>();
        var ancestry = new HashSet<string>(StringComparer.Ordinal);
        Collect(branch, long.MaxValue);
        return selected.OrderBy(record => record.Sequence).ToArray();

        void Collect(string current, long through) {
            if (!ancestry.Add(current)) { gaps.Add("分支来源形成循环，已停止继承祖先。"); return; }
            PlaygroundHostRecord[] own = snapshot.Records.Where(record => record.BranchId == current && record.Sequence <= through)
                .OrderBy(record => record.Sequence).ToArray();
            PlaygroundHostRecord[] forks = own.Where(record => record.Kind == "branch-fork" && record.ActorId is null).ToArray();
            if (forks.Length == 1) {
                PlaygroundHostRecord fork = forks[0];
                string? source = String(fork.Payload, "SourceBranchId");
                string? requestId = String(fork.Payload, "SourceRequestId");
                long? cut = Number(fork.Payload, "SourceThroughSequence");
                PlaygroundHostRecord[] requests = snapshot.Records.Where(record => record.Kind == "model-request" &&
                    record.BranchId == source && String(record.Payload, "RequestId") == requestId).ToArray();
                JsonElement? boundary = requests.Length == 1 ? Property(requests[0].Payload, "Boundary") : null;
                if (source is not null && source != current && cut is >= 0 && cut < fork.Sequence &&
                    !string.IsNullOrWhiteSpace(snapshot.JournalId) &&
                    String(fork.Payload, "SourceJournalId") == snapshot.JournalId && requests.Length == 1 &&
                    requests[0].Sequence == cut + 1 && requests[0].Sequence < fork.Sequence &&
                    boundary is { ValueKind: JsonValueKind.Object } sourceBoundary &&
                    String(sourceBoundary, "BranchId") == source && String(sourceBoundary, "ActorId") == requests[0].ActorId &&
                    Number(sourceBoundary, "ContextSequence") == requests[0].ContextSequence &&
                    String(sourceBoundary, "StateCommitId") is { } state && state == String(fork.Payload, "SourceStateCommitId")) {
                    Collect(source, Math.Min(through, cut.Value));
                } else {
                    gaps.Add("分叉来源/JournalId/请求切点不能共同验证；不猜测祖先宿主前缀。");
                }
            } else if (forks.Length > 1) {
                gaps.Add("同分支存在多个来源，未选择其一拼接祖先。");
            } else if (own.Any(record => record.Kind == "run-start" &&
                String(record.Payload, "ForkedFromCommitId") is not null)) {
                gaps.Add("此分支来自世界 fork，但无可证明的宿主日志切点；祖先宿主材料未继承。");
            }
            if (own.Length == 0) { gaps.Add("选定分支/祖先切点没有宿主记录，存在覆盖缺口。"); }
            selected.AddRange(own);
            ancestry.Remove(current);
        }
    }

    private static void Diagnose(IReadOnlyList<PlaygroundHostRecord> visible, List<string> gaps) {
        foreach (PlaygroundHostRecord request in visible.Where(record => record.Kind == "model-request")) {
            string? id = String(request.Payload, "RequestId");
            if (!visible.Any(record => record.BranchId == request.BranchId && record.RunId == request.RunId &&
                record.Kind is "model-response" or "model-failure" && String(record.Payload, "RequestId") == id)) {
                gaps.Add($"请求 Sequence {request.Sequence} 未见完整输出或失败记录，远端结果未知。");
            }
        }
        foreach (PlaygroundHostRecord outcome in visible.Where(record => record.Kind is "model-response" or "model-failure")) {
            if (!visible.Any(record => record.BranchId == outcome.BranchId && record.RunId == outcome.RunId &&
                record.Kind == "model-request" && String(record.Payload, "RequestId") == String(outcome.Payload, "RequestId"))) {
                gaps.Add($"模型结果 Sequence {outcome.Sequence} 未见对应保存的请求。");
            }
        }
        foreach (PlaygroundHostRecord start in visible.Where(record => record.Kind == "run-start")) {
            if (!visible.Any(record => record.Kind == "run-end" && record.RunId == start.RunId && record.BranchId == start.BranchId)) {
                gaps.Add($"运行 {Literal(start.RunId)} 缺 run-end，可能中断、仍运行或被分叉切点截断。");
            }
        }
    }

    private static bool HasActor(JsonElement payload, string actor) => Property(payload, "Actors") is { ValueKind: JsonValueKind.Array } actors &&
        actors.EnumerateArray().Any(entry => String(entry, "ActorId") == actor);
    private static T Read<T>(PlaygroundHostRecord record) => record.Payload.Deserialize<T>(Json)
        ?? throw new InvalidDataException("A host history payload is null.");
    private static JsonElement? Property(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out JsonElement property) ? property : null;
    private static JsonElement Required(JsonElement value, string name) => Property(value, name)
        ?? throw new InvalidDataException("A host history payload is missing " + name + ".");
    private static string RequiredString(JsonElement value, string name) => String(value, name)
        ?? throw new InvalidDataException("A host history payload is missing " + name + ".");
    private static string? String(JsonElement value, string name) => Property(value, name) is { ValueKind: JsonValueKind.String } property
        ? property.GetString() : null;
    private static long? Number(JsonElement value, string name) => Property(value, name) is { ValueKind: JsonValueKind.Number } property &&
        property.TryGetInt64(out long number) ? number : null;
    private static bool? Boolean(JsonElement value, string name) => Property(value, name) is { ValueKind: JsonValueKind.True } ? true
        : Property(value, name) is { ValueKind: JsonValueKind.False } ? false : null;
    private static string Literal(string? value) => CharacterContextRenderer.DataLiteral(value ?? "—");
    private static string Token(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "null";

    private static void Fence(StringBuilder output, string text) {
        int longest = 0, current = 0;
        foreach (char character in text) {
            current = character == '`' ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }
        string fence = new('`', Math.Max(3, longest + 1));
        output.Append(fence).AppendLine("text").AppendLine(text).AppendLine(fence).AppendLine();
    }
}
