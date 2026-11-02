using DramaBoard.CharacterGym;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Atelia.DurableGraph;
using DramaBoard.Playground;
using DramaBoard.Playground.Cli;

internal enum GymManagementKind { Branches, Feedbacks, Export, ForkFeedback, ForkRequest }

internal sealed record GymManagementOptions(GymManagementKind Kind, string? Actor = null,
    string? ExportPath = null, string? SourceId = null, string? ForkLabel = null);

/// <summary>Serial, explicit developer operations. Read-only modes never open a journal writer or game working copy.</summary>
internal static class GymCommands {
    private static readonly JsonSerializerOptions DisplayJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static void Run(string store, string? branch, GymManagementOptions options, TextWriter output, TextWriter? diagnostics = null) {
        ArgumentNullException.ThrowIfNull(output);
        if (!Directory.Exists(store)) { throw new ArgumentException("管理操作需要已有 --store。"); }
        string journalPath = GymSession.JournalPath(store);
        if (options.Kind == GymManagementKind.Branches) {
            foreach (PlaygroundBranchInfo item in PlaygroundDurableHistory.ListBranches(store)) {
                output.WriteLine(JsonSerializer.Serialize(item, DisplayJson));
            }
            return;
        }
        if (options.Kind == GymManagementKind.Feedbacks) {
            if (branch is not null) { branch = SelectBranch(store, branch); }
            FeedbackJournal.Snapshot snapshot = FeedbackJournal.ReadSnapshot(FeedbackSession.JournalPath(store));
            foreach (FeedbackRecord record in snapshot.Records.Where(record =>
                (branch is null || record.Boundary?.BranchId == branch) && (options.Actor is null || record.ActorId == options.Actor))) {
                output.WriteLine(JsonSerializer.Serialize(record, DisplayJson));
            }
            if (snapshot.IgnoredTailBytes > 0) {
                (diagnostics ?? Console.Error).WriteLine($"反馈文件末尾 {snapshot.IgnoredTailBytes} 字节不完整；已忽略，未修改原文件，不能据此确认该条保存成功。");
            }
            return;
        }
        if (options.Kind == GymManagementKind.Export) {
            string selected = SelectBranch(store, branch);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.Actor);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.ExportPath);
            // Render before creating the destination; a missing/corrupt source never leaves an empty success artifact.
            using var rendered = new StringWriter();
            GymTraceExporter.Export(journalPath, selected, options.Actor, rendered);
            using (var file = new FileStream(options.ExportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) {
                using var writer = new StreamWriter(file, new UTF8Encoding(false));
                writer.Write(rendered.ToString());
            }
            output.WriteLine($"已导出角色 {options.Actor} / 分支 {selected}：{options.ExportPath}");
            return;
        }
        if (options.Kind is not (GymManagementKind.ForkFeedback or GymManagementKind.ForkRequest)) {
            throw new ArgumentException("未知管理操作。");
        }
        Fork(store, journalPath, options, output);
    }

    private static string SelectBranch(string store, string? requested) {
        IReadOnlyList<PlaygroundBranchInfo> branches = PlaygroundDurableHistory.ListBranches(store);
        if (requested is not null) {
            string id = PlaygroundDurableHistory.FormatBranchId(PlaygroundDurableHistory.ParseBranchId(requested));
            if (branches.Any(branch => branch.BranchId == id)) { return id; }
            throw new ArgumentException("分支不存在；请先使用 --branches 查看。");
        }
        return branches.Count == 1 ? branches[0].BranchId
            : throw new ArgumentException("存档不是唯一分支；请使用 --branches 查看并显式指定 --branch。");
    }

    private static void Fork(string store, string journalPath, GymManagementOptions options, TextWriter output) {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ForkLabel);
        GymJournalSnapshot? snapshot = null;
        GymRecord? request = null;
        PlaygroundDecisionBoundary boundary;
        if (options.Kind == GymManagementKind.ForkFeedback) {
            FeedbackRecord[] matches = FeedbackJournal.Read(FeedbackSession.JournalPath(store))
                .Where(record => record.FeedbackId == options.SourceId).ToArray();
            if (matches.Length != 1) { throw new InvalidDataException($"需要唯一反馈 {options.SourceId}，实际找到 {matches.Length} 条。"); }
            FeedbackRecord feedback = matches[0];
            boundary = feedback.Boundary ?? throw new InvalidDataException("反馈没有持久决定前边界；未创建分支。");
            if (feedback.ActorId != boundary.ActorId || feedback.ContextSequence != boundary.ContextSequence) {
                throw new InvalidDataException("反馈与决定前边界的角色或 Context 不一致；未创建分支。");
            }
        } else {
            snapshot = GymJournal.Read(journalPath);
            request = Find(snapshot, "request", "RequestId", options.SourceId);
            boundary = Boundary(request);
            if (request.BranchId != boundary.BranchId || request.ActorId != boundary.ActorId) {
                throw new InvalidDataException("请求记录与边界身份不一致；未创建分支。");
            }
        }
        PlaygroundDecisionBoundary actual = PlaygroundDurableHistory.InspectDecisionBoundary(store,
            boundary.BranchId, boundary.StateCommitId, boundary.ActorId);
        if (actual != boundary) { throw new InvalidDataException("记录边界与此存档的完整决定前状态不一致；未创建分支。"); }

        PlaygroundBranchForkResult registered;
        try {
            registered = PlaygroundDurableHistory.Fork(store, boundary.BranchId, boundary.StateCommitId, boundary.ActorId, options.ForkLabel);
        } catch (RepositoryPublicationException error) {
            string candidate = error.TargetBranchId is { } id ? PlaygroundDurableHistory.FormatBranchId(id) : "未知";
            throw new IOException($"分支注册异常：{error.Outcome}；候选分支 {candidate}。未自动重试；请用 --branches 核查后决定下一步。", error);
        }
        // Registration is already durable. Logging and opening are separate outcomes; never re-register on failure.
        if (request is not null && snapshot is not null) {
            try {
                using GymJournal journal = GymJournal.Open(journalPath);
                if (journal.StoreId != snapshot.StoreId) { throw new InvalidDataException("Gym 日志在分支注册期间被替换。"); }
                journal.Append("branch-fork", null, registered.Branch.BranchId, null, new {
                    SourceBranchId = boundary.BranchId, SourceStateCommitId = boundary.StateCommitId,
                    SourceThroughSequence = request.Sequence - 1, SourceFeedbackId = (string?)null,
                    SourceRequestId = RequiredString(request.Payload, "RequestId")
                });
            } catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) {
                throw new IOException($"分支 {registered.Branch.BranchId} 已注册，但来源日志未能确认保存；没有打开或运行新分支。请用 --branches 核查，不要盲目重试 fork。", error);
            }
        }
        output.WriteLine($"分支已注册：{registered.Branch.BranchId}；来源 S：{boundary.StateCommitId}；未打开或运行。");
        output.WriteLine("后续使用相同 --store 和上述 --branch，人数沿用存档，重新指定控制器配置续跑。");
    }

    private static GymRecord Find(GymJournalSnapshot snapshot, string kind, string field, string id) {
        GymRecord[] matches = snapshot.Records.Where(record => record.Kind == kind &&
            record.Payload.ValueKind == JsonValueKind.Object && record.Payload.TryGetProperty(field, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String && value.GetString() == id).ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidDataException($"需要唯一的 {kind} 记录 {id}，实际找到 {matches.Length} 条。");
    }

    private static string RequiredString(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw new InvalidDataException($"Gym 记录缺少 {name}。");

    private static PlaygroundDecisionBoundary Boundary(GymRecord record) {
        try {
            if (record.Payload.ValueKind != JsonValueKind.Object || !record.Payload.TryGetProperty("Boundary", out JsonElement value)) {
                throw new InvalidDataException("Gym 记录缺少决定前边界。");
            }
            foreach (string name in new[] { "BranchId", "StateCommitId", "ActorId" }) { _ = RequiredString(value, name); }
            foreach (string name in new[] { "ContextSequence", "OperationSequence", "LineageId", "TransitionCount", "TimeTicks", "GameRevision" }) {
                if (!value.TryGetProperty(name, out JsonElement number) || number.ValueKind != JsonValueKind.Number || !number.TryGetInt64(out _)) {
                    throw new InvalidDataException($"Gym 决定前边界缺少有效的 {name}。");
                }
            }
            return value.Deserialize<PlaygroundDecisionBoundary>() ?? throw new InvalidDataException("Gym 决定前边界为空。");
        } catch (JsonException error) { throw new InvalidDataException("Gym 决定前边界格式无效。", error); }
    }
}
