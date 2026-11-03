using DramaBoard.Kernel.Simulation;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;
using static DramaBoard.Playground.Tests.PathQueryTests;

namespace DramaBoard.Playground.Tests;

public sealed class PathRecoveryTests {
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Candidate_cold_state_and_pending_event_restore_the_recorded_id_steps_and_result_without_a_controller(bool eventOnly, bool clearCandidate) {
        using var store = new TemporaryStore();
        PlaygroundWorld initial = KnownWorld();
        RawScript Script() => clearCandidate ? new("path stream", "path ridge") : new("path stream");
        var baseline = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Script());
        await UntilOperation(baseline);
        if (clearCandidate) { await UntilOperation(baseline); }
        string expected = MaterialSnapshot(baseline.World.Business);
        using (var history = PlaygroundDurableHistory.Create(store.StorePath, initial, InitialCursor(initial))) {
            if (eventOnly) {
                var interrupted = PlaygroundEngine.CreateKernel(new FollowingTestSupport.StopAfterEvent(history,
                    fact => fact.Kind == PlaygroundEventKind.PathQueried && (!clearCandidate || fact.RawInput == "path ridge")), characterController: Script());
                try {
                    await UntilOperation(interrupted);
                    if (clearCandidate) { await UntilOperation(interrupted); }
                }
                catch (InvalidOperationException error) when (error.InnerException is FollowingTestSupport.InjectedCrash) { }
                Assert.True(interrupted.IsFaulted);
                Assert.NotNull(history.PendingEvent);
            } else {
                var kernel = PlaygroundEngine.CreateKernel(history, characterController: Script());
                await UntilOperation(kernel);
                if (clearCandidate) { await UntilOperation(kernel); }
            }
        }
        using (var reopened = PlaygroundDurableHistory.Open(store.StorePath)) {
            var forbidden = new FollowingTestSupport.ForbiddenController();
            var kernel = PlaygroundEngine.CreateKernel(reopened, characterController: forbidden);
            Assert.Equal(eventOnly, kernel.RecoverPending());
            Assert.False(kernel.RecoverPending());
            Assert.Equal(expected, MaterialSnapshot(kernel.World.Business));
            if (clearCandidate) { Assert.Null(kernel.World.Expedition!.CandidatePath("a")); }
            else { Assert.Equal("path:a:1:1", kernel.World.Expedition!.CandidatePath("a")!.Id); }
            Assert.Equal(0, forbidden.Calls);
        }
        using var final = PlaygroundDurableHistory.Open(store.StorePath);
        Assert.Null(final.PendingEvent);
        Assert.Equal(expected, MaterialSnapshot(final.State.Business));
    }

    [Fact]
    public async Task Fork_inherits_the_candidate_and_child_replacement_does_not_mutate_the_parent() {
        using var store = new TemporaryStore();
        PlaygroundWorld initial = KnownWorld();
        PlaygroundDecisionBoundary source;
        string parentSnapshot;
        string inherited;
        using (var history = PlaygroundDurableHistory.Create(store.StorePath, initial, InitialCursor(initial))) {
            var kernel = PlaygroundEngine.CreateKernel(history, characterController: new RawScript("path stream"));
            await UntilOperation(kernel);
            source = history.CaptureDecisionBoundary("a");
            parentSnapshot = MaterialSnapshot(history.State.Business);
            inherited = history.State.Expedition!.CandidatePath("a")!.Id;
        }
        PlaygroundBranchForkResult fork = PlaygroundDurableHistory.Fork(store.StorePath, source.BranchId, source.StateCommitId, "a", "path candidate fork");
        using (var child = PlaygroundDurableHistory.Open(store.StorePath, PlaygroundDurableHistory.ParseBranchId(fork.Branch.BranchId))) {
            Assert.Equal(inherited, child.State.Expedition!.CandidatePath("a")!.Id);
            var kernel = PlaygroundEngine.CreateKernel(child, characterController: new RawScript("path camp"));
            await UntilOperation(kernel);
            Assert.NotEqual(inherited, kernel.World.Expedition!.CandidatePath("a")!.Id);
            Assert.Empty(kernel.World.Expedition.CandidatePath("a")!.Steps);
        }
        using var parent = PlaygroundDurableHistory.Open(store.StorePath, PlaygroundDurableHistory.ParseBranchId(source.BranchId));
        Assert.Equal(parentSnapshot, MaterialSnapshot(parent.State.Business));
        Assert.Equal(inherited, parent.State.Expedition!.CandidatePath("a")!.Id);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("cost")]
    [InlineData("unknown-edge")]
    [InlineData("query-origin")]
    public async Task Recorded_paths_validate_query_causality_and_each_known_step_without_researching(string tampering) {
        var kernel = PlaygroundEngine.CreateKernel(Memory(KnownWorld()), characterController: new RawScript("path stream"));
        PlaygroundWorld before = kernel.World;
        for (int step = 0; step < 10; step++) {
            before = kernel.World;
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(24)));
            if (kernel.LastCompletion!.Event.Facts.Single().Kind == PlaygroundEventKind.PathQueried) { break; }
        }
        PlaygroundFact fact = kernel.LastCompletion!.Event.Facts.Single();
        PlaygroundPath original = fact.CandidatePath!;
        PlaygroundPath invalid = tampering switch {
            "identity" => original with { OperationSequence = original.OperationSequence + 1, Id = "path:a:1:2" },
            "cost" => original with { TotalTicks = original.TotalTicks + 1 },
            "unknown-edge" => original with { Steps = [new("camp", "e-b9", "stream", original.TotalTicks)] },
            _ => original with { OriginPlaceId = "ridge" }
        };
        string answer = PlaygroundPathRules.Describe(before.Business, "a", PlaygroundCommand.Path("stream"), invalid);
        PlaygroundFact forged = Copy(fact, invalid, answer);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => PlaygroundEngine.Fold(before, kernel.LastCompletion.Event.TargetInstant, forged));
        string expected = tampering switch {
            "cost" => "total cost differs",
            "unknown-edge" => "continuous personally confirmed entrance",
            _ => "this query's actor, sequence and endpoints"
        };
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Equal_origin_query_cannot_be_replayed_as_a_no_route_result_or_a_cycle(bool cycle) {
        var kernel = PlaygroundEngine.CreateKernel(Memory(KnownWorld()), characterController: new RawScript("path camp"));
        PlaygroundWorld before = kernel.World;
        for (int step = 0; step < 10; step++) {
            before = kernel.World;
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(24)));
            if (kernel.LastCompletion!.Event.Facts.Single().Kind == PlaygroundEventKind.PathQueried) { break; }
        }
        PlaygroundFact original = kernel.LastCompletion!.Event.Facts.Single();
        PlaygroundPath? invalid = cycle ? original.CandidatePath! with {
            Steps = [new("camp", "e-c7", "stream", PlaygroundTime.Hour), new("stream", "e-n4", "camp", PlaygroundTime.Hour)],
            TotalTicks = 2 * PlaygroundTime.Hour
        } : null;
        string answer = PlaygroundPathRules.Describe(before.Business, "a", PlaygroundCommand.Path("camp"), invalid);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => PlaygroundEngine.Fold(before,
            kernel.LastCompletion.Event.TargetInstant, Copy(original, invalid, answer)));
        Assert.Contains("zero-step path", error.Message, StringComparison.Ordinal);
    }

    private static PlaygroundFact Copy(PlaygroundFact f, PlaygroundPath? path, string result) => new(
        f.Kind, f.At, f.ActorId, f.ResourceId, f.Light, f.ChangedResources, f.Script, f.ChangedCharacters,
        f.LastServedActorId, result, f.SpatialFacts, f.Visits, f.Environment, f.ToolCallId, f.OperationRecordId,
        f.GatherStopReason, f.ObservedEvents, f.InteractionChanges, f.RawInput, f.OperationDisposition,
        f.TargetActorId, f.SpeechText, f.ItemId, f.ItemCount, f.GoalOperation, f.Economy, f.MoneyAmount, f.TradeChanges,
        f.ConfirmedPassages, path);
}
