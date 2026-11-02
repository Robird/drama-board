using System.Text.Json;
using Atelia.Completion.Abstractions;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Playground;
using DramaBoard.Playground.Cli;

namespace DramaBoard.Playground.Cli.Tests;

/// <summary>Real host and disk boundaries with fake Completion responses; not real-model acceptance.</summary>
public sealed class GymIntegrationTests {
    [Fact]
    public async Task Feedback_continues_the_same_duo_service_and_audits_full_tail_without_private_leakage() {
        using var files = new Files(characterCount: 2);
        var feedback = Feedback();
        var client = new Client(feedback, Act("handoff model-private"));
        using var input = new StringReader("handoff human-private\nwait 1\n");
        using var output = new StringWriter();
        var options = files.Options(2) with { Controls = HostFixture.Humans("a") };
        Assert.Equal(2, await PlaygroundHost.RunAsync(files.Store, options, default, output, client, characterCount: 2, inputReader: input));

        var snapshot = files.Read();
        JsonElement builds = Assert.Single(snapshot.Records, record => record.Kind == "run-start").Payload.GetProperty("Builds");
        Assert.Contains(builds.EnumerateArray(), build => build.GetProperty("Name").GetString() == "DramaBoard.CharacterGym");
        Assert.Contains(builds.EnumerateArray(), build => build.GetProperty("Name").GetString() == "DramaBoard.CharacterControl.Completion");
        Assert.Contains(builds.EnumerateArray(), build => build.GetProperty("Name").GetString() == "DramaBoard.Goals");
        PlaygroundHostRecord[] requests = snapshot.Records.Where(r => r.Kind == "model-request").ToArray();
        Assert.Equal(2, requests.Length);
        Assert.All(requests, request => Assert.Equal("b", request.ActorId));
        Assert.NotEqual(Text(requests[0], "RequestId"), Text(requests[1], "RequestId"));
        Assert.Equal(requests[0].Payload.GetProperty("Boundary").GetRawText(), requests[1].Payload.GetProperty("Boundary").GetRawText());
        Assert.Equal(requests[0].Payload.GetProperty("Shared").GetRawText(), requests[1].Payload.GetProperty("Shared").GetRawText());
        FeedbackRecord savedFeedback = Assert.Single(files.Feedback());
        Assert.Equal(Text(requests[0], "RequestId"), savedFeedback.Model!.RequestId);
        Assert.Equal("feedback-call", savedFeedback.Model.ToolCallId);
        Assert.Equal("fixture-obstacle", savedFeedback.Text);
        Assert.Equal(requests[0].Payload.GetProperty("Boundary").Deserialize<PlaygroundDecisionBoundary>(), savedFeedback.Boundary);
        Assert.DoesNotContain(snapshot.Records, r => r.Kind == "feedback");
        PlaygroundHostRecord savedReceipt = Assert.Single(snapshot.Records, r => r.Kind == "feedback-saved");
        Assert.Equal(savedFeedback.FeedbackId, Text(savedReceipt, "FeedbackId"));
        Assert.True(requests[0].Sequence < savedReceipt.Sequence && savedReceipt.Sequence < requests[1].Sequence);
        var boundary = requests[0].Payload.GetProperty("Boundary").Deserialize<PlaygroundDecisionBoundary>()!;
        Assert.Equal(boundary, PlaygroundDurableHistory.InspectDecisionBoundary(files.Store, boundary.BranchId, boundary.StateCommitId, "b"));

        Assert.Same(feedback.Message, client.Requests[1].TailMessages[0]);
        ToolResult receipt = Assert.Single(Assert.IsType<ToolResultsMessage>(client.Requests[1].TailMessages[1]).Results);
        Assert.Equal("feedback-call", receipt.ToolCallId);
        JsonElement tail = requests[1].Payload.GetProperty("Tail");
        Assert.Equal(2, tail.GetArrayLength());
        JsonElement response = Assert.Single(snapshot.Records, r => r.Kind == "model-response" && Text(r, "RequestId") == Text(requests[0], "RequestId")).Payload;
        Assert.Equal(response.GetProperty("Result").GetProperty("Blocks").GetRawText(), tail[0].GetProperty("Blocks").GetRawText());
        Assert.Contains(savedFeedback.FeedbackId, tail[1].GetRawText());
        Assert.Contains("fixture-obstacle", tail[0].GetRawText());
        // The standalone run audit must carry the same native feedback history, not just a summary.
        JsonElement auditedInput = LlmRawAudit.Read(options.LogPath!).Records.Last(line => line.Kind == "model-request").Payload;
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(client.Requests[1].TailMessages.Select(GymMessageProjection.Message).ToArray()),
            auditedInput.GetProperty("Tail")));

        using var state = PlaygroundDurableHistory.Open(files.Store);
        Assert.Equal(15 * PlaygroundTime.Hour, state.State.Time.Ticks);
        Assert.Equal(45 * PlaygroundSurvival.Scale, state.State.GetCharacter("b").Survival!.WaterUnits);
        Assert.Equal(1, state.State.GetCharacter("b").Backpack.Count("item.water"));
        Assert.Equal("model-private", state.State.Business.InteractionStates["b"].DraftHandoff);
        CharacterContext modelContext = state.State.Business.InteractionStates["b"].CurrentContext!;
        CharacterContextItem gameOperation = Assert.Single(modelContext.Items, item => item.OperationSequence is not null);
        Assert.Equal("handoff model-private", gameOperation.RawInput);
        Assert.Equal("repeatable-provider-id", gameOperation.ToolCallId);
        Assert.Equal(PlaygroundObservedEventKind.HandoffEdited, Assert.Single(gameOperation.Experiences).Event.Kind);
        // Compare with the same game commands without feedback: natural perceptions are legitimate
        // Items, but the host-only tool must create neither extra game transitions nor experiences.
        using var baselineFiles = new Files(characterCount: 2);
        using var baselineInput = new StringReader("handoff human-private\nwait 1\n");
        Assert.Equal(2, await PlaygroundHost.RunAsync(baselineFiles.Store, baselineFiles.Options(1) with { Controls = HostFixture.Humans("a") }, default, output,
            new Client(Act("handoff model-private")), characterCount: 2, inputReader: baselineInput));
        using var baseline = PlaygroundDurableHistory.Open(baselineFiles.Store);
        Assert.Equal(baseline.Cursor.Version.TransitionCount, state.Cursor.Version.TransitionCount);
        foreach (string actor in new[] { "a", "b" }) {
            Assert.Equal(ExperienceMaterial(baseline.State.Business.InteractionStates[actor]), ExperienceMaterial(state.State.Business.InteractionStates[actor]));
        }
        using var hostJournal = PlaygroundHostJournal.Open(PlaygroundHostJournal.JournalPath(files.Store));
        var operations = new PlaygroundOperationAudit(hostJournal, boundary.BranchId, "test-read");
        Assert.Equal(new[] { "a", "b", "a" }, operations.Entries.Select(entry => entry.Received.ActorId));
        Assert.All(operations.Entries, entry => Assert.NotNull(entry.Completed));
        foreach (PlaygroundOperationEntry entry in operations.Entries) {
            JsonElement recorded = Assert.Single(snapshot.Records, record => record.Kind == "operation-completed" &&
                Text(record, "OperationRecordId") == entry.Received.OperationRecordId).Payload.GetProperty("Completed");
            PlaygroundOperationResult completed = entry.Completed!;
            Assert.Equal(completed.Status, recorded.Deserialize<PlaygroundOperationResult>()!.Status);
            Assert.Equal(completed.ToolCallId, recorded.GetProperty("ToolCallId").GetString());
            Assert.Equal(completed.Result, recorded.GetProperty("Result").GetString());
            Assert.Equal(completed.WorldVersion, recorded.GetProperty("WorldVersion").GetString());
            Assert.Equal(completed.DomainEvent, recorded.GetProperty("DomainEvent").GetString());
            Assert.Equal(completed.ExactResult, recorded.GetProperty("ExactResult").GetBoolean());
        }
        string exported = CharacterHistoryExporter.Render(snapshot, boundary.BranchId, "b", files.Feedback());
        Assert.Contains("model-private", exported);
        Assert.Contains("fixture-obstacle", exported);
        Assert.DoesNotContain("human-private", exported);
    }

    [Fact]
    public async Task Two_runs_and_early_fork_keep_parent_future_out_of_child_trace_and_state() {
        using var files = new Files();
        using var output = new StringWriter();
        Assert.Equal(2, await PlaygroundHost.RunAsync(files.Store, files.Options(2), default, output, new Client(Feedback(), Act("use item.water"))));
        Assert.Equal(2, await PlaygroundHost.RunAsync(files.Store, files.Options(1), default, output, new Client(Act("handoff parent-future-only"))));
        PlaygroundHostJournalSnapshot parentSnapshot = files.Read();
        PlaygroundHostRecord request = parentSnapshot.Records.First(r => r.Kind == "model-request");
        var source = request.Payload.GetProperty("Boundary").Deserialize<PlaygroundDecisionBoundary>()!;
        Assert.Equal(2, parentSnapshot.Records.Count(r => r.Kind == "run-start"));
        Assert.Equal(2, parentSnapshot.Records.Where(r => r.Kind == "run-start").Select(r => r.RunId).Distinct().Count());
        string parentHead;
        using (var parent = PlaygroundDurableHistory.Open(files.Store)) {
            parentHead = parent.CurrentBranch.HeadCommitId;
            Assert.Equal(90 * PlaygroundSurvival.Scale, parent.State.GetCharacter("a").Survival!.WaterUnits);
        }
        PlaygroundBranchForkResult fork = Fork(files, request, "alternate");
        var childClient = new Client(Act("handoff child-only"));
        Assert.Equal(2, await PlaygroundHost.RunAsync(files.Store, files.Options(1, fork.Branch.BranchId), default, output, childClient));
        using (var child = PlaygroundDurableHistory.Open(files.Store, fork.Branch.BranchId)) {
            Assert.Equal(45 * PlaygroundSurvival.Scale, child.State.GetCharacter("a").Survival!.WaterUnits);
            Assert.Equal("child-only", child.State.Business.InteractionStates["a"].DraftHandoff);
        }
        using (var parent = PlaygroundDurableHistory.Open(files.Store, source.BranchId)) {
            Assert.Equal(parentHead, parent.CurrentBranch.HeadCommitId);
            Assert.Equal("parent-future-only", parent.State.Business.InteractionStates["a"].DraftHandoff);
        }
        PlaygroundHostJournalSnapshot all = files.Read();
        string childTrace = CharacterHistoryExporter.Render(all, fork.Branch.BranchId, "a", files.Feedback());
        Assert.Contains("child-only", childTrace);
        Assert.DoesNotContain("parent-future-only", childTrace);
        Assert.DoesNotContain("fixture-obstacle", childTrace);
        Assert.Contains(Text(request, "RequestId"), childTrace);
        string parentTrace = CharacterHistoryExporter.Render(all, source.BranchId, "a", files.Feedback());
        Assert.Contains("parent-future-only", parentTrace);
        Assert.Contains("fixture-obstacle", parentTrace);
        Assert.DoesNotContain("child-only", parentTrace);
        Assert.Empty(childClient.Requests.Single().TailMessages);
        PlaygroundHostRecord childRequest = Assert.Single(all.Records, r => r.Kind == "model-request" && r.BranchId == fork.Branch.BranchId);
        Assert.Equal(source.StateCommitId, childRequest.Payload.GetProperty("Boundary").GetProperty("StateCommitId").GetString());
    }

    [Fact]
    public async Task Fork_from_feedback_followup_preserves_prior_evidence_but_restarts_from_clean_game_context() {
        using var files = new Files();
        using var output = new StringWriter();
        Assert.Equal(2, await PlaygroundHost.RunAsync(files.Store, files.Options(2), default, output,
            new Client(Feedback(), Act("create-goal {\"title\":\"parent-later-goal\",\"criteria\":\"parent-later-criterion\"}"))));
        PlaygroundHostJournalSnapshot original = files.Read();
        PlaygroundHostRecord[] parentRequests = original.Records.Where(r => r.Kind == "model-request").ToArray();
        Assert.Equal(2, parentRequests.Length);
        PlaygroundHostRecord followup = parentRequests[1];
        var source = followup.Payload.GetProperty("Boundary").Deserialize<PlaygroundDecisionBoundary>()!;
        Assert.Equal(parentRequests[0].Payload.GetProperty("Boundary").GetRawText(), followup.Payload.GetProperty("Boundary").GetRawText());
        Assert.Equal(2, followup.Payload.GetProperty("Tail").GetArrayLength());
        FeedbackRecord feedback = Assert.Single(files.Feedback());
        PlaygroundHostRecord feedbackReceipt = Assert.Single(original.Records, r => r.Kind == "feedback-saved");
        Assert.Equal(feedback.FeedbackId, Text(feedbackReceipt, "FeedbackId"));
        Assert.Equal(Text(parentRequests[0], "RequestId"), feedback.Model!.RequestId);
        Assert.True(parentRequests[0].Sequence < feedbackReceipt.Sequence && feedbackReceipt.Sequence < followup.Sequence);
        Assert.Contains(feedback.FeedbackId, followup.Payload.GetProperty("Tail")[1].GetRawText());
        Assert.DoesNotContain(original.Records, r => r.Kind == "feedback");

        PlaygroundBranchForkResult fork = Fork(files, followup, "after-feedback-before-act");
        using (var child = PlaygroundDurableHistory.Open(files.Store, fork.Branch.BranchId)) {
            Assert.Equal(source.StateCommitId, child.CurrentBranch.CompletedStateCommitId);
            CharacterInteractionState interaction = child.State.Business.InteractionStates["a"];
            Assert.Empty(interaction.Goals.Goals);
            Assert.Empty(interaction.CurrentContext!.Items);
            Assert.Empty(interaction.CurrentContext.Opening.Observation.Goals!.Goals);
            Assert.DoesNotContain("fixture-obstacle", CharacterContextRenderer.RenderFull(interaction, new DecisionPlayLayout()));
        }
        var client = new Client(Act("handoff child-after-feedback"));
        Assert.Equal(2, await PlaygroundHost.RunAsync(files.Store, files.Options(1, fork.Branch.BranchId), default, output, client));
        Assert.Empty(client.Requests.Single().TailMessages);
        Assert.DoesNotContain("fixture-obstacle", Assert.IsType<ObservationMessage>(client.Requests.Single().PromptPrefix.SharedContextMessages.Single()).Content);

        PlaygroundHostJournalSnapshot all = files.Read();
        PlaygroundHostRecord childRequest = Assert.Single(all.Records, r => r.Kind == "model-request" && r.BranchId == fork.Branch.BranchId);
        Assert.Equal(source.StateCommitId, childRequest.Payload.GetProperty("Boundary").GetProperty("StateCommitId").GetString());
        Assert.Equal(followup.Payload.GetProperty("Shared").GetRawText(), childRequest.Payload.GetProperty("Shared").GetRawText());
        Assert.Equal(0, childRequest.Payload.GetProperty("Tail").GetArrayLength());
        string trace = CharacterHistoryExporter.Render(all, fork.Branch.BranchId, "a", files.Feedback());
        Assert.Contains("fixture-obstacle", trace);
        Assert.Contains(feedback.FeedbackId, trace);
        Assert.Contains("child-after-feedback", trace);
        Assert.DoesNotContain("parent-later-goal", trace);
        using (var child = PlaygroundDurableHistory.Open(files.Store, fork.Branch.BranchId)) {
            Assert.Empty(child.State.Business.InteractionStates["a"].Goals.Goals);
            Assert.Single(child.State.Business.InteractionStates["a"].CurrentContext!.Items);
        }
        using var parent = PlaygroundDurableHistory.Open(files.Store, source.BranchId);
        Assert.Equal("parent-later-goal", Assert.Single(parent.State.Business.InteractionStates["a"].Goals.Goals).Title);
    }

    [Fact]
    public async Task Child_start_neither_recovers_parent_pending_event_nor_reconciles_parent_operation_journal() {
        using var files = new Files(createStore: false);
        var initial = PlaygroundGameFactory.CreateSurvival();
        var memory = new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor);
        var operation = new PlaygroundControllerOperation("parent-tool", "use item.water", "parent-pending");
        var kernel = PlaygroundEngine.CreateKernel(memory, characterController: new FixedOperation(operation));
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
        PlaygroundWorld opening = kernel.World;
        KernelCursor cursor = kernel.Cursor;
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
        PlaygroundDecisionBoundary boundary;
        string parentHead;
        using (var parent = PlaygroundDurableHistory.Create(files.Store, opening, cursor)) {
            boundary = parent.CaptureDecisionBoundary("a");
            parent.CommitEvent(kernel.LastCompletion!.Event);
            parentHead = parent.CurrentBranch.HeadCommitId;
        }
        string operationsPath = PlaygroundHostJournal.JournalPath(files.Store);
        using (var host = PlaygroundHostJournal.Open(operationsPath)) {
            var journal = new PlaygroundOperationAudit(host, boundary.BranchId, "parent-run");
            journal.Receive("parent-run", operation.ToolCallId, "a", 1, "act", operation.RawInput, operation.OperationRecordId);
        }
        string[] originalOperations = PlaygroundHostJournal.Read(operationsPath).Records.Where(r => r.BranchId == boundary.BranchId)
            .Select(r => JsonSerializer.Serialize(r)).ToArray();
        PlaygroundBranchForkResult fork = PlaygroundDurableHistory.Fork(files.Store, boundary.BranchId, boundary.StateCommitId, "a", "pending-isolation");
        using var output = new StringWriter();
        var client = new Client(Act("handoff child-pending-safe"));
        Assert.Equal(2, await PlaygroundHost.RunAsync(files.Store, files.Options(1, fork.Branch.BranchId), default, output, client));
        Assert.Equal(originalOperations, PlaygroundHostJournal.Read(operationsPath).Records.Where(r => r.BranchId == boundary.BranchId)
            .Select(r => JsonSerializer.Serialize(r)).ToArray());
        using (var parent = PlaygroundDurableHistory.Open(files.Store, boundary.BranchId)) {
            Assert.Equal(parentHead, parent.CurrentBranch.HeadCommitId);
            Assert.NotNull(parent.PendingEvent);
            Assert.Equal(45 * PlaygroundSurvival.Scale, parent.State.GetCharacter("a").Survival!.WaterUnits);
        }
        using (var child = PlaygroundDurableHistory.Open(files.Store, fork.Branch.BranchId)) {
            Assert.Null(child.PendingEvent);
            Assert.Equal(45 * PlaygroundSurvival.Scale, child.State.GetCharacter("a").Survival!.WaterUnits);
            using var host = PlaygroundHostJournal.Open(PlaygroundHostJournal.JournalPath(files.Store));
            var journal = new PlaygroundOperationAudit(host, child.CurrentBranch.BranchId, "test-read");
            Assert.Single(journal.Entries);
            Assert.DoesNotContain(journal.Entries, entry => entry.Received.OperationRecordId == operation.OperationRecordId);
        }
        Assert.Single(client.Requests);
        Assert.DoesNotContain(files.Read().Records.Where(r => r.Kind == "operation-completed"), r => Text(r, "OperationRecordId") == operation.OperationRecordId);
    }

    [Fact]
    public async Task Child_receipt_reusing_an_inherited_operation_id_is_not_reconciled_from_parent_world_facts() {
        using var files = new Files();
        using var output = new StringWriter();
        Assert.Equal(2, await PlaygroundHost.RunAsync(files.Store, files.Options(1), default, output,
            new Client(Act("handoff parent-completed"))));
        PlaygroundHostRecord received = Assert.Single(files.Read().Records, record => record.Kind == "operation-received");
        PlaygroundOperationReceipt receipt = received.ReadPayload<PlaygroundOperationReceipt>();
        PlaygroundDecisionBoundary source;
        using (var parent = PlaygroundDurableHistory.Open(files.Store)) { source = parent.CaptureDecisionBoundary("a"); }
        PlaygroundBranchForkResult fork = PlaygroundDurableHistory.Fork(files.Store, source.BranchId, source.StateCommitId,
            "a", "reused-id");
        using (var journal = PlaygroundHostJournal.Open(PlaygroundHostJournal.JournalPath(files.Store))) {
            var childOperations = new PlaygroundOperationAudit(journal, fork.Branch.BranchId, "child-received");
            childOperations.Receive(receipt.BatchId, receipt.ToolCallId, receipt.ActorId, receipt.ContextSequence,
                receipt.ToolName, receipt.RawInput, receipt.OperationRecordId);
        }
        using var input = new StringReader("");
        Assert.Equal(0, await PlaygroundHost.RunAsync(files.Store,
            files.Options(1, fork.Branch.BranchId) with { Controls = HostFixture.Humans("a") },
            default, output, inputReader: input));
        PlaygroundHostRecord childResult = Assert.Single(files.Read().Records,
            record => record.Kind == "operation-completed" && record.BranchId == fork.Branch.BranchId);
        PlaygroundOperationCompletion completion = childResult.ReadPayload<PlaygroundOperationCompletion>();
        Assert.Equal(receipt.OperationRecordId, completion.OperationRecordId);
        Assert.Equal(PlaygroundOperationStatus.Interrupted, completion.Completed.Status);
        Assert.Null(completion.Completed.DomainEvent);
        Assert.False(completion.Completed.ExactResult);
        Assert.True(completion.Recovered);
        using var saved = PlaygroundDurableHistory.Open(files.Store, fork.Branch.BranchId);
        Assert.Equal(source.StateCommitId, saved.CurrentBranch.CompletedStateCommitId);
    }

    [Fact]
    public async Task Feedback_only_pause_preserves_world_and_cold_open_starts_fresh_local_tool_loop() {
        using var files = new Files();
        using var output = new StringWriter();
        Assert.Equal(2, await PlaygroundHost.RunAsync(files.Store, files.Options(1), default, output, new Client(Feedback())));
        string stateCommit;
        using (var paused = PlaygroundDurableHistory.Open(files.Store)) {
            stateCommit = paused.CurrentBranch.CompletedStateCommitId!;
            Assert.Empty(paused.State.Business.InteractionStates["a"].CurrentContext!.Items);
            Assert.Null(paused.PendingEvent);
            Assert.Equal(45 * PlaygroundSurvival.Scale, paused.State.GetCharacter("a").Survival!.WaterUnits);
            Assert.Equal(15 * PlaygroundTime.Hour, paused.State.Time.Ticks);
        }
        var resumed = new Client(Act("handoff after-feedback-pause"));
        Assert.Equal(2, await PlaygroundHost.RunAsync(files.Store, files.Options(1), default, output, resumed));
        Assert.Empty(resumed.Requests.Single().TailMessages);
        Assert.DoesNotContain("fixture-obstacle", Assert.IsType<ObservationMessage>(resumed.Requests.Single().PromptPrefix.SharedContextMessages.Single()).Content);
        PlaygroundHostJournalSnapshot snapshot = files.Read();
        PlaygroundHostRecord[] requests = snapshot.Records.Where(r => r.Kind == "model-request").ToArray();
        Assert.Equal(2, requests.Length);
        Assert.All(requests, r => Assert.Equal(stateCommit, r.Payload.GetProperty("Boundary").GetProperty("StateCommitId").GetString()));
        Assert.Single(files.Feedback());
        Assert.Single(snapshot.Records, r => r.Kind == "feedback-saved");
        Assert.DoesNotContain(snapshot.Records, r => r.Kind == "feedback");
        Assert.Single(snapshot.Records, r => r.Kind == "operation-completed");
    }

    private static PlaygroundBranchForkResult Fork(Files files, PlaygroundHostRecord request, string label) {
        var boundary = request.Payload.GetProperty("Boundary").Deserialize<PlaygroundDecisionBoundary>()!;
        var fork = PlaygroundDurableHistory.Fork(files.Store, boundary.BranchId, boundary.StateCommitId, boundary.ActorId, label);
        using var journal = PlaygroundHostJournal.Open(PlaygroundHostJournal.JournalPath(files.Store));
        journal.Append("branch-fork", null, fork.Branch.BranchId, null, null, new {
            SourceJournalId = journal.JournalId,
            SourceBranchId = boundary.BranchId, SourceStateCommitId = boundary.StateCommitId,
            SourceThroughSequence = request.Sequence - 1, SourceRequestId = Text(request, "RequestId")
        });
        return fork;
    }

    private static string ExperienceMaterial(CharacterInteractionState interaction) => JsonSerializer.Serialize(new {
        interaction.NextExperienceSequence,
        Undelivered = interaction.UndeliveredExperiences.Select(experience => new {
            experience.ExperienceSequence, experience.Event.EventOrdinal, experience.Event.ObserverId,
            experience.Event.Kind, From = experience.Event.From.Ticks, At = experience.Event.At.Ticks,
            experience.Event.SubjectId, experience.Event.SubjectName, experience.Event.Detail,
            experience.Event.Communication,
            Effects = experience.Event.Effects.Select(effect => new {
                effect.Key, Before = VisibleValue(effect.Before), After = VisibleValue(effect.After)
            })
        }),
        Material = CharacterContextRenderer.RenderFull(interaction, new DecisionPlayLayout())
    });
    // Number intentionally throws for nonnumeric variants; serialize the discriminated fields.
    private static object VisibleValue(PlaygroundVisibleValue value) => new {
        value.Kind, value.Units, value.Scale, value.Unit, value.Text, value.Boolean
    };
    private static string Text(PlaygroundHostRecord record, string name) => record.Payload.GetProperty(name).GetString()!;
    private static CompletionResult Feedback() => Result(new("feedback", "feedback-call",
        JsonSerializer.Serialize(new { text = "fixture-obstacle" })));
    private static CompletionResult Act(string command) => Result(new("act", "repeatable-provider-id", JsonSerializer.Serialize(new { command })));
    private static CompletionResult Result(RawToolCall call) => new(new ActionMessage([new ActionBlock.ToolCall(call)]), new("fixture", "fixture", "fixture-model"));

    private sealed class Client(params CompletionResult[] results) : ICompletionClient {
        private readonly Queue<CompletionResult> _results = new(results);
        public string Name => "fixture";
        public string ApiSpecId => "fixture";
        public List<CompletionRequest> Requests { get; } = [];
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
            Requests.Add(request);
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class FixedOperation(PlaygroundControllerOperation operation) : IPlaygroundCharacterController {
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) => ValueTask.FromResult(operation);
    }

    private sealed class Files : IDisposable {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "drama-gym-integration-" + Guid.NewGuid().ToString("N"));
        public string Store => Path.Combine(_root, "game");
        public Files(int characterCount = 1, bool createStore = true) {
            if (createStore) {
                var initial = PlaygroundGameFactory.CreateSurvival(characterCount: characterCount);
                using var saved = PlaygroundDurableHistory.Create(Store, initial.World, initial.Cursor);
            }
        }
        public PlaygroundHostOptions Options(int maxCalls, string? branch = null) => new(MaxCalls: maxCalls,
            LogPath: Path.Combine(_root, Guid.NewGuid().ToString("N") + ".jsonl"), Gym: true, Branch: branch);
        public IReadOnlyList<FeedbackRecord> Feedback() => FeedbackJournal.Read(FeedbackSession.JournalPath(Store));
        public PlaygroundHostJournalSnapshot Read() => PlaygroundHostJournal.Read(PlaygroundHostJournal.JournalPath(Store));
        public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); } }
    }
}
