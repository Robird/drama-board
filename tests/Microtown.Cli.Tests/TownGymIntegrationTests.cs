using System.Security.Cryptography;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.DurableGraph;
using DramaBoard.CharacterGym;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Microtown.Cli.Gym;
using DramaBoard.Microtown.Completion;

namespace DramaBoard.Microtown.Cli.Tests;

/// <summary>Real game publication and host evidence boundaries; Completion responses are fixtures, not model playtests.</summary>
public sealed class TownGymIntegrationTests {
    [Fact]
    public async Task Feedback_is_durable_before_followup_and_one_controller_keeps_resident_material_private() {
        using var files = new Files();
        string branch;
        var client = new Client(Feedback("a-private-obstacle"), Act("wait 1"),
            Feedback("b-private-obstacle"), Act("wait 1"));
        using (var history = files.Create()) {
            branch = TownDurableHistory.FormatBranchId(history.BranchId);
            using var session = new TownGymSession(files.Store, history, new(Model: "fixture", FeedbackEnabled: true));
            session.ReconcileCommitted();
            client.BeforeResponse = (number, request) => {
                if (number is not (2 or 4)) { return; }
                GymJournalSnapshot durable = files.Read();
                string actor = number == 2 ? "a" : "b";
                GymRecord feedback = Assert.Single(durable.Records, r => r.Kind == "feedback" && r.ActorId == actor);
                GymRecord receipt = Assert.Single(durable.Records, r => r.Kind == "feedback-receipt" && r.ActorId == actor);
                GymRecord[] requests = durable.Records.Where(r => r.Kind == "request" && r.ActorId == actor).ToArray();
                Assert.Equal(2, requests.Length);
                Assert.True(feedback.Sequence < receipt.Sequence && receipt.Sequence < requests[1].Sequence);
                Assert.NotEqual(Text(requests[0], "RequestId"), Text(requests[1], "RequestId"));
                Assert.Equal(requests[0].Payload.GetProperty("Boundary").GetRawText(), requests[1].Payload.GetProperty("Boundary").GetRawText());
                Assert.Equal(TownDurableHistory.FormatCommitId(history.CompletedStateCommitId),
                    requests[1].Payload.GetProperty("Boundary").GetProperty("StateCommitId").GetString());
                Assert.Equal(Text(requests[0], "RequestId"), Text(feedback, "RequestId"));
                Assert.Equal(requests[0].Sequence, feedback.Payload.GetProperty("RequestSequence").GetInt64());
                Assert.Equal(2, request.TailMessages.Length);
                Assert.IsType<ActionMessage>(request.TailMessages[0]);
                ToolResult saved = Assert.Single(Assert.IsType<ToolResultsMessage>(request.TailMessages[1]).Results);
                Assert.Equal("feedback-call", saved.ToolCallId);
                Assert.Contains(Text(feedback, "FeedbackId"), requests[1].Payload.GetProperty("Tail").GetRawText());
                Assert.Equal(0, history.State.Business.Character(actor).OperationSequence);
            };
            var controller = new TownLlmCharacterController(client, "fixture", trace: session.Trace, feedbackSink: session.SaveFeedback);
            var kernel = TownEngine.CreateControlledKernel(history, controller.ChooseCommandAsync);
            // Each actor first receives a completed opening S, then one accepted timed action.
            for (int step = 0; step < 4; step++) {
                Assert.Equal(StepStatus.Committed, await kernel.StepAsync(history.State.Business.EndTime));
                session.ReconcileCommitted();
            }
            Assert.Equal(0, history.State.Time.Ticks);
            Assert.Equal(TownActivityKind.Wait, history.State.Business.Character("a").Activity!.Kind);
            Assert.Equal(TownActivityKind.Wait, history.State.Business.Character("b").Activity!.Kind);
        }
        Assert.Equal(4, client.Requests.Count);
        Assert.Empty(client.Requests[2].TailMessages);
        Assert.DoesNotContain("a-private-obstacle", Assert.IsType<ObservationMessage>(client.Requests[2].PromptPrefix.SharedContextMessages.Single()).Content);
        GymJournalSnapshot snapshot = files.Read();
        string a = GymTraceExporter.Render(snapshot, branch, "a");
        string b = GymTraceExporter.Render(snapshot, branch, "b");
        Assert.Contains("a-private-obstacle", a);
        Assert.DoesNotContain("b-private-obstacle", a);
        Assert.Contains("b-private-obstacle", b);
        Assert.DoesNotContain("a-private-obstacle", b);
        Assert.Equal(2, snapshot.Records.Count(r => r.Kind == "operation"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Fork_uses_exact_selected_state_and_only_inherits_evidence_before_selected_request(bool fromFeedback) {
        using var files = new Files();
        BranchId parentId;
        CommitId parentHead;
        using (var history = files.Create()) {
            parentId = history.BranchId;
            using var session = new TownGymSession(files.Store, history, new(Model: "fixture", FeedbackEnabled: true));
            session.ReconcileCommitted();
            var controller = new TownLlmCharacterController(new Client(Feedback("source-obstacle"),
                Act("create-goal {\"title\":\"parent-later-goal\",\"criteria\":\"parent-later-criterion\"}")), "fixture",
                trace: session.Trace, feedbackSink: session.SaveFeedback);
            var kernel = TownEngine.CreateControlledKernel(history, controller.ChooseCommandAsync);
            await kernel.StepAsync(history.State.Business.EndTime);
            await kernel.StepAsync(history.State.Business.EndTime);
            session.ReconcileCommitted();
            parentHead = history.HeadCommitId;
            Assert.Equal("parent-later-goal", Assert.Single(history.State.Business.Character("a").Goals.Goals).Title);
        }
        GymJournalSnapshot before = files.Read();
        GymRecord[] requests = before.Records.Where(r => r.Kind == "request").ToArray();
        GymRecord source = fromFeedback ? requests[0] : requests[1];
        GymRecord feedback = Assert.Single(before.Records, r => r.Kind == "feedback");
        var boundary = source.Payload.GetProperty("Boundary").Deserialize<TownGymBoundary>()!;
        string sourceId = fromFeedback ? Text(feedback, "FeedbackId") : Text(source, "RequestId");
        TownBranchInfo child = TownGymCommands.Fork(files.Store, sourceId, fromFeedback, "counterfactual");
        Assert.Equal(TownDurableHistory.ParseCommitId(boundary.StateCommitId), child.CompletedStateCommitId);
        var client = new Client(Act("buy grain 2"));
        using (var history = TownDurableHistory.Open(files.Store, child.Id)) {
            Assert.Equal(boundary.StateCommitId, TownDurableHistory.FormatCommitId(history.CompletedStateCommitId));
            Assert.Equal(boundary.LineageId, history.Cursor.Version.LineageId);
            Assert.Equal(boundary.TransitionCount, history.Cursor.Version.TransitionCount);
            Assert.Empty(history.State.Business.Character("a").Goals.Goals);
            using var session = new TownGymSession(files.Store, history, new(Model: "fixture", FeedbackEnabled: true));
            session.ReconcileCommitted();
            var controller = new TownLlmCharacterController(client, "fixture", trace: session.Trace, feedbackSink: session.SaveFeedback);
            var kernel = TownEngine.CreateControlledKernel(history, controller.ChooseCommandAsync);
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(history.State.Business.EndTime));
            session.ReconcileCommitted();
            Assert.Equal(2, history.State.Business.Character("a").Inventory.Count("grain"));
        }
        Assert.Empty(Assert.Single(client.Requests).TailMessages);
        Assert.DoesNotContain("source-obstacle", Assert.IsType<ObservationMessage>(client.Requests[0].PromptPrefix.SharedContextMessages.Single()).Content);
        using (var parent = TownDurableHistory.Open(files.Store, parentId)) {
            Assert.Equal(parentHead, parent.HeadCommitId);
            Assert.Equal(0, parent.State.Business.Character("a").Inventory.Count("grain"));
            Assert.Equal("parent-later-goal", Assert.Single(parent.State.Business.Character("a").Goals.Goals).Title);
        }
        GymJournalSnapshot after = files.Read();
        string childId = TownDurableHistory.FormatBranchId(child.Id);
        GymRecord fork = Assert.Single(after.Records, r => r.Kind == "branch-fork" && r.BranchId == childId);
        Assert.Equal(source.Sequence - 1, fork.Payload.GetProperty("SourceThroughSequence").GetInt64());
        GymRecord childRequest = Assert.Single(after.Records, r => r.Kind == "request" && r.BranchId == childId);
        Assert.Equal(boundary.StateCommitId, childRequest.Payload.GetProperty("Boundary").GetProperty("StateCommitId").GetString());
        string trace = GymTraceExporter.Render(after, childId, "a");
        Assert.Contains("buy grain 2", trace);
        Assert.DoesNotContain("parent-later-goal", trace);
        if (fromFeedback) { Assert.DoesNotContain("source-obstacle", trace); }
        else { Assert.Contains("source-obstacle", trace); }
    }

    [Fact]
    public async Task Pending_recovery_reconciles_one_exact_receipt_without_reasking_or_replaying_the_operation() {
        using var files = new Files();
        var operation = new TownControllerOperation("buy grain 2", "same-provider-id", "pending-operation");
        using (var history = files.Create()) {
            var opening = TownEngine.CreateControlledKernel(history, (_, _) => throw new InvalidOperationException("Opening must not ask."));
            await opening.StepAsync(history.State.Business.EndTime);
            using var session = new TownGymSession(files.Store, history, new());
            session.ReconcileCommitted();
            session.RecordOperationReceived("a", operation);
            var scratch = new InMemoryOccurrenceHistory<TownWorld, TownFact>(history.State, history.Cursor);
            var prepare = TownEngine.CreateControlledKernel(scratch, (_, _) => ValueTask.FromResult(operation));
            await prepare.StepAsync(history.State.Business.EndTime);
            history.CommitEvent(prepare.LastCompletion!.Event);
        }
        Assert.DoesNotContain(files.Read().Records, r => r.Kind == "operation");
        CommitId completed;
        CommitId pending;
        string exactResult;
        using (var history = TownDurableHistory.Open(files.Store)) {
            pending = history.HeadCommitId;
            Assert.NotNull(history.PendingEvent);
            using var session = new TownGymSession(files.Store, history, new());
            Assert.Throws<InvalidOperationException>(() => session.ReconcileCommitted());
            var recovery = TownEngine.CreateControlledKernel(history, (_, _) => throw new InvalidOperationException("Recovery must not call controller."));
            Assert.True(recovery.RecoverPending());
            Assert.False(recovery.RecoverPending());
            completed = history.CompletedStateCommitId;
            exactResult = history.State.Business.Character("a").LastOperationReceipt!.Result;
            session.ReconcileCommitted();
            session.ReconcileCommitted();
            Assert.Equal(2, history.State.Business.Character("a").Inventory.Count("grain"));
            var next = new TownControllerOperation("wait 1", "same-provider-id", "next-operation");
            var nextKernel = TownEngine.CreateControlledKernel(history, (observation, _) => {
                Assert.Equal("b", observation.ActorId);
                session.RecordOperationReceived(observation.ActorId, next);
                return ValueTask.FromResult(next);
            });
            await nextKernel.StepAsync(history.State.Business.EndTime);
            await nextKernel.StepAsync(history.State.Business.EndTime);
            session.ReconcileCommitted();
        }
        using (var history = TownDurableHistory.Open(files.Store)) {
            using var session = new TownGymSession(files.Store, history, new());
            session.ReconcileCommitted();
            Assert.Equal(2, history.State.Business.Character("a").Inventory.Count("grain"));
            Assert.Equal(1, history.State.Business.Character("a").OperationSequence);
        }
        GymRecord recorded = Assert.Single(files.Read().Records, r => r.Kind == "operation" && Text(r, "OperationRecordId") == operation.OperationRecordId);
        Assert.True(recorded.Payload.GetProperty("Recovered").GetBoolean());
        Assert.Equal(TownDurableHistory.FormatCommitId(completed), Text(recorded, "StateCommitId"));
        JsonElement result = recorded.Payload.GetProperty("Result");
        Assert.Equal("Succeeded", result.GetProperty("Status").GetString());
        Assert.True(result.GetProperty("ExactResult").GetBoolean());
        Assert.Equal(TownDurableHistory.FormatCommitId(pending), result.GetProperty("DomainEvent").GetString());
        Assert.Equal(exactResult, result.GetProperty("Result").GetString());
        Assert.Equal(operation, result.GetProperty("Receipt").GetProperty("Operation").Deserialize<TownControllerOperation>());
        Assert.Equal(2, files.Read().Records.Count(r => r.Kind == "operation"));
    }

    [Fact]
    public async Task Management_reads_do_not_change_store_and_bad_fork_source_does_not_register_branch() {
        using var files = new Files();
        string branch;
        using (var history = files.Create()) {
            branch = TownDurableHistory.FormatBranchId(history.BranchId);
            using var session = new TownGymSession(files.Store, history, new(Model: "fixture", FeedbackEnabled: true));
            session.ReconcileCommitted();
            var controller = new TownLlmCharacterController(new Client(Feedback("read-only-evidence"), Act("wait 1")), "fixture",
                trace: session.Trace, feedbackSink: session.SaveFeedback);
            var kernel = TownEngine.CreateControlledKernel(history, controller.ChooseCommandAsync);
            await kernel.StepAsync(history.State.Business.EndTime);
            await kernel.StepAsync(history.State.Business.EndTime);
            session.ReconcileCommitted();
        }
        string[] fingerprint = Fingerprint(files.Store);
        using var listed = new StringWriter();
        TownGymCommands.ListFeedbacks(files.Store, branch, "a", listed);
        GymRecord feedback = Assert.Single(files.Read().Records, r => r.Kind == "feedback");
        Assert.Contains(Text(feedback, "FeedbackId"), listed.ToString());
        using var other = new StringWriter();
        TownGymCommands.ListFeedbacks(files.Store, branch, "b", other);
        Assert.DoesNotContain(Text(feedback, "FeedbackId"), other.ToString());
        TownGymCommands.Export(files.Store, branch, "a", files.Export);
        Assert.Contains("read-only-evidence", File.ReadAllText(files.Export));
        Assert.Equal(fingerprint, Fingerprint(files.Store));
        Assert.Throws<InvalidDataException>(() => TownGymCommands.Fork(files.Store, "missing-feedback-id", true, "must-not-exist"));
        Assert.Single(TownDurableHistory.ListBranches(files.Store));
        Assert.Equal(fingerprint, Fingerprint(files.Store));
    }

    [Fact]
    public async Task Received_without_event_is_interrupted_once_and_never_replayed_on_cold_open() {
        using var files = new Files();
        var lost = new TownControllerOperation("buy grain 2", "same-provider-id", "received-without-event");
        CommitId openingState;
        KernelCursor openingCursor;
        using (var history = files.Create()) {
            var opening = TownEngine.CreateControlledKernel(history, (_, _) => throw new InvalidOperationException("Opening must not ask."));
            await opening.StepAsync(history.State.Business.EndTime);
            openingState = history.CompletedStateCommitId;
            openingCursor = history.Cursor;
            using var session = new TownGymSession(files.Store, history, new());
            session.ReconcileCommitted();
            session.RecordOperationReceived("a", lost);
        }
        Assert.DoesNotContain(files.Read().Records, r => r.Kind == "operation");
        using (var history = TownDurableHistory.Open(files.Store)) {
            Assert.Null(history.PendingEvent);
            using var session = new TownGymSession(files.Store, history, new());
            session.ReconcileCommitted();
            session.ReconcileCommitted();
            Assert.Equal(openingState, history.HeadCommitId);
            Assert.Equal(openingCursor, history.Cursor);
            Assert.Equal(0, history.State.Business.Character("a").OperationSequence);
            Assert.Equal(0, history.State.Business.Character("a").Inventory.Count("grain"));
            Assert.Null(history.State.Business.Character("a").LastOperationReceipt);
            int calls = 0;
            var next = new TownControllerOperation("buy grain 1", "same-provider-id", "new-after-interruption");
            var kernel = TownEngine.CreateControlledKernel(history, (observation, _) => {
                calls++;
                Assert.Equal("a", observation.ActorId);
                session.RecordOperationReceived(observation.ActorId, next);
                return ValueTask.FromResult(next);
            });
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(history.State.Business.EndTime));
            session.ReconcileCommitted();
            Assert.Equal(1, calls);
        }
        using (var history = TownDurableHistory.Open(files.Store)) {
            using var session = new TownGymSession(files.Store, history, new());
            session.ReconcileCommitted();
            Assert.Equal(1, history.State.Business.Character("a").OperationSequence);
            Assert.Equal(1, history.State.Business.Character("a").Inventory.Count("grain"));
            Assert.Equal("new-after-interruption", history.State.Business.Character("a").LastOperationReceipt!.Operation.OperationRecordId);
        }
        GymRecord[] results = files.Read().Records.Where(r => r.Kind == "operation").ToArray();
        Assert.Equal(2, results.Length);
        GymRecord interrupted = Assert.Single(results, r => Text(r, "OperationRecordId") == lost.OperationRecordId);
        Assert.Equal("Interrupted", interrupted.Payload.GetProperty("Result").GetProperty("Status").GetString());
        Assert.False(interrupted.Payload.GetProperty("Result").GetProperty("ExactResult").GetBoolean());
        Assert.Equal(JsonValueKind.Null, interrupted.Payload.GetProperty("Result").GetProperty("DomainEvent").ValueKind);
        Assert.Equal("Succeeded", Assert.Single(results, r => Text(r, "OperationRecordId") == "new-after-interruption")
            .Payload.GetProperty("Result").GetProperty("Status").GetString());
    }

    [Fact]
    public async Task Feedback_with_mismatched_actor_or_boundary_cannot_register_a_branch() {
        using var files = new Files();
        using (var history = files.Create()) {
            using var session = new TownGymSession(files.Store, history, new(Model: "fixture", FeedbackEnabled: true));
            session.ReconcileCommitted();
            var controller = new TownLlmCharacterController(new Client(Feedback("valid-source"), Act("wait 1")), "fixture",
                trace: session.Trace, feedbackSink: session.SaveFeedback);
            var kernel = TownEngine.CreateControlledKernel(history, controller.ChooseCommandAsync);
            await kernel.StepAsync(history.State.Business.EndTime);
            await kernel.StepAsync(history.State.Business.EndTime);
            session.ReconcileCommitted();
        }
        GymJournalSnapshot original = files.Read();
        GymRecord feedback = Assert.Single(original.Records, r => r.Kind == "feedback");
        GymRecord request = Assert.Single(original.Records, r => r.Kind == "request" && Text(r, "RequestId") == Text(feedback, "RequestId"));
        var boundary = feedback.Payload.GetProperty("Boundary").Deserialize<TownGymBoundary>()!;
        // Append well-formed, correctly chained records; semantic identity checks must reject them.
        using (var journal = GymJournal.Open(TownGymSession.JournalPath(files.Store))) {
            journal.Append("feedback", feedback.RunId, feedback.BranchId, "b", new {
                FeedbackId = "wrong-actor", RequestId = Text(request, "RequestId"), RequestSequence = request.Sequence,
                Boundary = boundary, Submission = feedback.Payload.GetProperty("Submission")
            });
            journal.Append("feedback", feedback.RunId, feedback.BranchId, feedback.ActorId, new {
                FeedbackId = "wrong-boundary", RequestId = Text(request, "RequestId"), RequestSequence = request.Sequence,
                Boundary = boundary with { OperationSequence = boundary.OperationSequence + 1 },
                Submission = feedback.Payload.GetProperty("Submission")
            });
        }
        string[] fingerprint = Fingerprint(files.Store);
        foreach (string id in new[] { "wrong-actor", "wrong-boundary" }) {
            Assert.Throws<InvalidDataException>(() => TownGymCommands.Fork(files.Store, id, true, "must-not-register"));
            Assert.Single(TownDurableHistory.ListBranches(files.Store));
            Assert.Equal(fingerprint, Fingerprint(files.Store));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Foreign_journal_is_rejected_before_repairing_its_incomplete_tail(bool explicitOtherGame) {
        using var files = new Files();
        string branch;
        using (var history = files.Create()) { branch = TownDurableHistory.FormatBranchId(history.BranchId); }
        string path = TownGymSession.JournalPath(files.Store);
        using (var journal = GymJournal.Open(path)) {
            object identity = explicitOtherGame ? new { GameId = "resource-cycle" } : new { Model = "legacy" };
            journal.Append("run-start", "foreign-run", branch, null, identity);
        }
        File.AppendAllText(path, "unfinished foreign evidence");
        string[] fingerprint = Fingerprint(files.Store);
        using (var history = TownDurableHistory.Open(files.Store)) {
            Assert.Throws<InvalidDataException>(() => new TownGymSession(files.Store, history, new()));
        }
        Assert.Throws<InvalidDataException>(() => TownGymCommands.ListFeedbacks(files.Store, branch, null, new StringWriter()));
        Assert.Throws<InvalidDataException>(() => TownGymCommands.Export(files.Store, branch, "a", files.Export));
        Assert.False(File.Exists(files.Export));
        Assert.Equal(fingerprint, Fingerprint(files.Store));
    }

    private static string Text(GymRecord record, string property) => record.Payload.GetProperty(property).GetString()!;
    private static CompletionResult Feedback(string obstacle) => Result(new("report-feedback", "feedback-call",
        JsonSerializer.Serialize(new { kind = "issue", summary = "fixture-summary", obstacle })));
    private static CompletionResult Act(string command) => Result(new("act", "repeatable-provider-id", JsonSerializer.Serialize(new { command })));
    private static CompletionResult Result(RawToolCall call) => new(new ActionMessage([new ActionBlock.ToolCall(call)]), new("fixture", "fixture", "fixture-model"));
    private static string[] Fingerprint(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(directory, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();

    private sealed class Client(params CompletionResult[] results) : ICompletionClient {
        private readonly Queue<CompletionResult> _results = new(results);
        public string Name => "fixture";
        public string ApiSpecId => "fixture";
        public List<CompletionRequest> Requests { get; } = [];
        public Action<int, CompletionRequest>? BeforeResponse { get; set; }
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
            Requests.Add(request);
            BeforeResponse?.Invoke(Requests.Count, request);
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class Files : IDisposable {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "town-gym-integration-" + Guid.NewGuid().ToString("N"));
        public string Store => Path.Combine(_root, "game");
        public string Export => Path.Combine(_root, "trace.md");
        public TownDurableHistory Create() {
            var initial = TownGameFactory.Create(48);
            return TownDurableHistory.Create(Store, initial.World, initial.Cursor);
        }
        public GymJournalSnapshot Read() => GymJournal.Read(TownGymSession.JournalPath(Store));
        public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); } }
    }
}
