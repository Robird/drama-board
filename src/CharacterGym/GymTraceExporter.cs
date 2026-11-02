using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DramaBoard.CharacterGym;

/// <summary>Read-only, actor-scoped presentation of recorded Gym evidence, never a reconstruction of game history.</summary>
public static class GymTraceExporter {
    private static readonly JsonSerializerOptions Json = new() {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void Export(string path, string branchId, string actorId, TextWriter writer) {
        ArgumentNullException.ThrowIfNull(writer);
        writer.Write(Render(GymJournal.Read(path), branchId, actorId));
    }

    public static string Render(GymJournalSnapshot snapshot, string branchId, string actorId) {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        var gaps = new List<string>();
        var records = new List<GymRecord>();
        var ancestry = new HashSet<string>(StringComparer.Ordinal);
        Collect(branchId, long.MaxValue);
        GymRecord[] visible = records.Where(record => record.ActorId == actorId ||
            (record.ActorId is null && record.Kind is "run-start" or "run-end" or "branch-fork"))
            .OrderBy(record => record.Sequence).ToArray();

        var output = new StringBuilder("# 角色实验轨迹\n\n");
        Fence(output, JsonSerializer.Serialize(new { snapshot.StoreId, BranchId = branchId, ActorId = actorId }, Json), "json");
        output.AppendLine("仅呈现已记录的角色材料与公开运行信息。宿主反馈是线索，响应不是游戏提交；未见记录不表示世界未执行。未启用记录的更早历史不可由当前 Context 反推。")
            .AppendLine("父分支只继承分叉切点及之前的记录；分叉引用中的请求/反馈若位于切点之后，只作为来源定位，不属于子分支轨迹。").AppendLine();
        if (snapshot.DiscardedTailBytes > 0) {
            gaps.Add($"日志尾部有 {snapshot.DiscardedTailBytes} 字节不完整记录；只读快照忽略它们，未修改原文件。");
        }
        if (visible.Length == 0) { gaps.Add("选定分支/角色没有可导出的记录。"); }

        foreach (GymRecord record in visible) {
            output.Append("## 记录 ").Append(record.Sequence).Append(" · ").Append(record.Kind).AppendLine().AppendLine();
            Fence(output, JsonSerializer.Serialize(new { record.FormatVersion, record.Sequence, record.Kind, record.RunId, record.BranchId, record.ActorId }, Json), "json");
            if (record.Kind == "request") {
                foreach (string field in new[] { "SystemPrompt", "OutputContract", "Shared", "Tail" }) {
                    if (Property(record.Payload, field) is not JsonElement value) { continue; }
                    output.Append("### ").Append(field).AppendLine("（阅读视图）").AppendLine();
                    var text = new StringBuilder();
                    ExtractText(value, text);
                    Fence(output, text.ToString(), "text");
                }
            }
            if (record.Kind == "perception" && Property(record.Payload, "Material") is JsonElement material) {
                output.AppendLine("### 可感知材料（不是实际模型输入）").AppendLine();
                var text = new StringBuilder();
                ExtractText(material, text);
                Fence(output, text.ToString(), "text");
            }
            if (record.Kind == "operation" && Property(record.Payload, "Result") is JsonElement result) {
                if (IsInterrupted(result)) {
                    output.AppendLine("宿主操作结果：Interrupted；未证实游戏提交。").AppendLine();
                } else if (!string.IsNullOrWhiteSpace(String(result, "DomainEvent"))) {
                    output.AppendLine("操作结果含领域事件引用；具体成功或拒绝见 Result。StateCommitId 是记录边界，不一定是原结果的提交位置。").AppendLine();
                } else {
                    output.AppendLine("宿主操作结果没有领域事件引用，不作为游戏提交证明（例如 Console 查询）。").AppendLine();
                }
                if (Property(result, "ExactResult") is { ValueKind: JsonValueKind.False }) {
                    output.AppendLine("恢复补录：ExactResult=false；这是补录的宿主结论，不保证重现原始回执文本。").AppendLine();
                }
            }
            output.AppendLine("### 完整原始 Payload").AppendLine();
            Fence(output, JsonSerializer.Serialize(record.Payload, Json), "json");
        }

        Diagnose(visible, gaps);
        output.AppendLine("## 证据缺口与未配对记录").AppendLine();
        if (gaps.Count == 0) {
            output.AppendLine("未发现下列可检查的关联缺口；这不保证日志覆盖所有游戏历史，也不保证模型结论正确。");
        } else {
            foreach (string gap in gaps) { output.Append("- ").AppendLine(gap); }
        }
        return output.ToString();

        void Collect(string branch, long through) {
            if (!ancestry.Add(branch)) {
                gaps.Add("分支来源形成循环；已停止继承祖先记录。");
                return;
            }
            GymRecord[] own = snapshot.Records.Where(r => r.BranchId == branch && r.Sequence <= through).OrderBy(r => r.Sequence).ToArray();
            GymRecord[] forks = own.Where(r => r.Kind == "branch-fork" && r.ActorId is null).ToArray();
            if (forks.Length == 1) {
                JsonElement payload = forks[0].Payload;
                string? source = String(payload, "SourceBranchId");
                JsonElement? cut = Property(payload, "SourceThroughSequence");
                if (source is not null && cut is { ValueKind: JsonValueKind.Number } && cut.Value.TryGetInt64(out long sequence) && sequence >= 0 && sequence < forks[0].Sequence) {
                    Collect(source, Math.Min(through, sequence));
                } else {
                    gaps.Add("分叉记录缺少有效来源分支/历史切点；不猜测祖先前缀。");
                }
            } else if (forks.Length > 1) {
                gaps.Add("同一分支存在多个分叉来源；不选择其中之一拼接祖先。");
            } else if (own.Any(r => r.Kind == "run-start" && Property(r.Payload, "ForkedFromCommitId") is { ValueKind: not JsonValueKind.Null })) {
                gaps.Add("运行记录表明该分支来自 fork，但缺少 branch-fork 证据；祖先轨迹未继承。");
            }
            if (own.Length == 0) { gaps.Add("分支来源在所选切点前没有保留记录，祖先证据可能缺失。"); }
            records.AddRange(own);
            ancestry.Remove(branch);
        }
    }

    private static void Diagnose(IReadOnlyList<GymRecord> records, List<string> gaps) {
        GymRecord[] requests = records.Where(r => r.Kind == "request").ToArray();
        GymRecord[] responses = records.Where(r => r.Kind == "response").ToArray();
        GymRecord[] operations = records.Where(r => r.Kind == "operation").ToArray();
        foreach (GymRecord request in requests) {
            string? id = String(request.Payload, "RequestId");
            if (id is null) { gaps.Add($"请求记录 {request.Sequence} 缺少 RequestId，无法配对响应。"); }
            else if (!responses.Any(r => String(r.Payload, "RequestId") == id)) { gaps.Add($"请求记录 {request.Sequence} 未见响应记录（可能中断或未保留）。"); }
        }
        foreach (GymRecord response in responses) {
            string? id = String(response.Payload, "RequestId");
            if (id is null || !requests.Any(r => String(r.Payload, "RequestId") == id)) {
                gaps.Add($"响应记录 {response.Sequence} 未见对应请求材料。");
            }
            if (Property(response.Payload, "Operation") is not { ValueKind: not JsonValueKind.Null } operation) { continue; }
            string? operationId = String(operation, "OperationRecordId");
            bool paired = operations.Any(r => (id is not null && String(r.Payload, "RequestId") == id) ||
                (operationId is not null && String(r.Payload, "OperationRecordId") == operationId));
            if (!paired) { gaps.Add($"响应记录 {response.Sequence} 含操作，但未见可配对结果记录；不能据此判断世界未执行。"); }
        }
        foreach (GymRecord operation in operations) {
            if (Property(operation.Payload, "Result") is JsonElement result && IsInterrupted(result)) {
                gaps.Add($"操作结果记录 {operation.Sequence} 标为 Interrupted，未证实游戏提交；配对结果不等于游戏事实。");
            }
        }
        foreach (GymRecord feedback in records.Where(r => r.Kind == "feedback")) {
            string? id = String(feedback.Payload, "FeedbackId");
            if (id is null || !records.Any(r => r.Kind == "feedback-receipt" && String(r.Payload, "FeedbackId") == id)) {
                gaps.Add($"反馈记录 {feedback.Sequence} 已记录，但未见回执记录；不能断言模型已收到回执。");
            }
        }
        foreach (GymRecord start in records.Where(r => r.Kind == "run-start")) {
            if (!records.Any(r => r.Kind == "run-end" && r.RunId == start.RunId)) { gaps.Add($"运行起点记录 {start.Sequence} 未见 run-end；可能仍在运行、中断或在分叉切点截断。"); }
        }
    }

    private static JsonElement? Property(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement property) ? property : null;
    private static string? String(JsonElement value, string name) => Property(value, name) is { ValueKind: JsonValueKind.String } property ? property.GetString() : null;

    // New adapters write audit status strings. Format v1 originally recorded Interrupted as 2;
    // keep that evidence readable without coupling the shared reader to a game's enum.
    private static bool IsInterrupted(JsonElement result) => String(result, "Status") == "Interrupted" ||
        (Property(result, "Status") is { ValueKind: JsonValueKind.Number } status &&
        status.TryGetInt32(out int number) && number == 2);

    private static void ExtractText(JsonElement value, StringBuilder text) {
        if (value.ValueKind == JsonValueKind.String) { text.AppendLine(value.GetString()); }
        else if (value.ValueKind == JsonValueKind.Array) { foreach (JsonElement item in value.EnumerateArray()) { ExtractText(item, text); } }
        else if (value.ValueKind == JsonValueKind.Object) {
            if (Property(value, "Content") is JsonElement content) { ExtractText(content, text); }
            else if (Property(value, "Text") is JsonElement body) { ExtractText(body, text); }
            else { text.AppendLine(JsonSerializer.Serialize(value, Json)); }
        } else { text.AppendLine(JsonSerializer.Serialize(value, Json)); }
    }

    // Inputs may contain Markdown fences. A longer delimiter preserves them as literal recorded text.
    private static void Fence(StringBuilder output, string text, string language) {
        int longest = 0, current = 0;
        foreach (char character in text) {
            current = character == '`' ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }
        string fence = new('`', Math.Max(3, longest + 1));
        output.Append(fence).AppendLine(language).AppendLine(text).AppendLine(fence).AppendLine();
    }
}
