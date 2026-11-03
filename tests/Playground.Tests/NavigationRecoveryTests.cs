using DramaBoard.Kernel.Simulation;
using DramaBoard.Spatial;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;
using static DramaBoard.Playground.Tests.PathQueryTests;
using static DramaBoard.Playground.Tests.NavigationTests;

namespace DramaBoard.Playground.Tests;

public sealed class NavigationRecoveryTests {
    [Theory]
    [InlineData("started", false)]
    [InlineData("started", true)]
    [InlineData("half-party", false)]
    [InlineData("half-party", true)]
    [InlineData("waiting", false)]
    [InlineData("waiting", true)]
    [InlineData("next-leg", false)]
    [InlineData("next-leg", true)]
    [InlineData("cancelled-moving", false)]
    [InlineData("cancelled-moving", true)]
    public async Task Navigation_boundaries_restore_the_exact_recorded_route_and_progress_without_controller_decisions(string boundary, bool eventOnly) {
        using var store = new TemporaryStore();
        PlaygroundWorld initial = World(count: 3, needs: boundary == "cancelled-moving");
        if (boundary == "cancelled-moving") {
            PlaygroundCharacter actor = initial.GetCharacter("a");
            var body = new PlaygroundSurvival(initial.Time, 100 * PlaygroundSurvival.Scale, 29 * PlaygroundSurvival.Scale,
                100 * PlaygroundSurvival.Scale, Rate(0), PlaygroundSurvivalRules.WaterRate, Rate(0));
            initial = PlaygroundEngine.Rebuild(initial.Business.Replace(initial.Time, changedCharacters: [actor.WithSurvival(body)]), initial.Time);
        }
        NavigationScript Script() => boundary == "cancelled-moving" ? new("path ravine", "navigate-current", "cancel") : new("path ravine", "navigate-current", "continue");
        bool Matches(PlaygroundFact fact) => boundary switch {
            "started" => fact.Kind == PlaygroundEventKind.MoveStarted && fact.RawInput!.StartsWith("navigate ", StringComparison.Ordinal),
            "half-party" => fact.Kind == PlaygroundEventKind.MoveArrived,
            "waiting" => fact.Kind == PlaygroundEventKind.DecisionContextOpened && fact.At == Hour(9) && fact.ActorId == "a",
            "next-leg" => fact.Kind == PlaygroundEventKind.MoveStarted && fact.RawInput == "continue",
            _ => fact.Kind == PlaygroundEventKind.NavigationCancelled
        };
        var baseline = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Script());
        await Until(baseline, Matches);
        string expected = MaterialSnapshot(baseline.World.Business);
        using (var history = PlaygroundDurableHistory.Create(store.StorePath, initial, InitialCursor(initial))) {
            if (eventOnly) {
                var interrupted = PlaygroundEngine.CreateKernel(new FollowingTestSupport.StopAfterEvent(history, Matches), characterController: Script());
                try { await Until(interrupted, Matches); }
                catch (InvalidOperationException error) when (error.InnerException is FollowingTestSupport.InjectedCrash) { }
                Assert.True(interrupted.IsFaulted);
                Assert.NotNull(history.PendingEvent);
            } else {
                var kernel = PlaygroundEngine.CreateKernel(history, characterController: Script());
                await Until(kernel, Matches);
            }
        }
        using (var reopened = PlaygroundDurableHistory.Open(store.StorePath)) {
            var forbidden = new FollowingTestSupport.ForbiddenController();
            var recovered = PlaygroundEngine.CreateKernel(reopened, characterController: forbidden);
            Assert.Equal(eventOnly, recovered.RecoverPending());
            Assert.False(recovered.RecoverPending());
            Assert.Equal(expected, MaterialSnapshot(recovered.World.Business));
            if (boundary == "half-party") {
                Assert.Single(recovered.World.Expedition!.Spatial.Entities, e => e.Location is AtPlaceLocation);
                Assert.Equal(PlaygroundActions.Location(recovered.World, "a") is AtPlaceLocation ? 1 : 0,
                    recovered.World.Expedition.Navigation("a")!.CompletedSteps);
                foreach (SpatialEntity entity in recovered.World.Expedition.Spatial.Entities) {
                    bool previouslyKnown = initial.Expedition!.KnowsPassage(entity.Id.Value, "trail-1");
                    Assert.Equal(previouslyKnown || entity.Location is AtPlaceLocation,
                        recovered.World.Expedition.KnowsPassage(entity.Id.Value, "trail-1"));
                }
                int remainingArrivals = recovered.World.Expedition.Spatial.Entities.Count(e => e.Location is TraversingLocation);
                Assert.Equal(2, remainingArrivals);
                for (int remaining = 0; remaining < remainingArrivals; remaining++) {
                    Assert.Equal(StepStatus.Committed, await recovered.StepAsync(Hour(24)));
                    Assert.Equal(PlaygroundEventKind.MoveArrived, recovered.LastCompletion!.Event.Facts.Single().Kind);
                    Assert.Equal(0, forbidden.Calls);
                }
                Assert.All(recovered.World.Expedition.Spatial.Entities, e => Assert.IsType<AtPlaceLocation>(e.Location));
            }
            if (boundary == "cancelled-moving") {
                Assert.Null(recovered.World.Expedition!.Navigation("a"));
                Assert.IsType<TraversingLocation>(PlaygroundActions.Location(recovered.World, "a"));
                Assert.True(recovered.World.GetCharacter("a").Survival!.PendingMask > 0);
            }
            Assert.Equal(0, forbidden.Calls);
            if (boundary == "cancelled-moving") {
                var continuation = PlaygroundEngine.CreateKernel(reopened, characterController: new NavigationScript("continue"));
                await UntilAllArrived(continuation);
                Assert.Null(continuation.World.Expedition!.Navigation("a"));
                Assert.All(continuation.World.Expedition.Spatial.Entities, e => Assert.Equal("stream", Assert.IsType<AtPlaceLocation>(e.Location).PlaceId.Value));
            }
        }
        using var final = PlaygroundDurableHistory.Open(store.StorePath);
        Assert.Null(final.PendingEvent);
        if (boundary is not ("half-party" or "cancelled-moving")) { Assert.Equal(expected, MaterialSnapshot(final.State.Business)); }
    }

    [Theory]
    [InlineData("skip")]
    [InlineData("other-actor")]
    [InlineData("follower-arrival")]
    public async Task Only_the_matching_personal_arrival_can_record_one_step_of_progress(string tampering) {
        var kernel = PlaygroundEngine.CreateKernel(Memory(World(count: 3)), characterController: new NavigationScript("path ravine", "navigate-current"));
        PlaygroundWorld before = kernel.World;
        PlaygroundFact fact = null!;
        for (int step = 0; step < 30; step++) {
            before = kernel.World;
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(24)));
            fact = kernel.LastCompletion!.Event.Facts.Single();
            if (fact.Kind == PlaygroundEventKind.MoveArrived && fact.ActorId == (tampering == "follower-arrival" ? "b" : "a")) { break; }
        }
        PlaygroundNavigation active = before.Expedition!.Navigation("a")!;
        PlaygroundNavigationChange invalid = tampering == "other-actor" ? new("b", new(active.Path, 1)) : new("a", new(active.Path, active.CompletedSteps + 2));
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => PlaygroundEngine.Fold(before,
            kernel.LastCompletion!.Event.TargetInstant, Copy(fact, invalid)));
        Assert.Contains("navigation replacement", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cold_state_rejects_a_progress_cursor_that_disagrees_with_its_spatial_segment() {
        var kernel = PlaygroundEngine.CreateKernel(Memory(World()), characterController: new NavigationScript("path ravine", "navigate-current"));
        await Until(kernel, f => f.Kind == PlaygroundEventKind.MoveStarted);
        PlaygroundExpedition old = kernel.World.Expedition!;
        PlaygroundNavigation active = old.Navigation("a")!;
        var invalid = new PlaygroundExpedition(old.ScenarioId, old.Spatial, old.Visits, old.Environment, old.ConfirmedPassages,
            old.CandidatePaths, [active with { CompletedSteps = 1 }]);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => PlaygroundEngine.Rebuild(
            kernel.World.Business.Replace(kernel.World.Time, expedition: invalid), kernel.World.Time));
        Assert.Contains("current spatial segment", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PlaygroundEventKind.AttentionAcknowledged, "continue", "Navigation continue")]
    [InlineData(PlaygroundEventKind.ActivityCancelled, "cancel", "Cancel with navigation")]
    [InlineData(PlaygroundEventKind.MoveStarted, "go e-h5", "cancelling navigation")]
    [InlineData(PlaygroundEventKind.WaitStarted, "wait 1", "cancelling navigation")]
    public async Task Legacy_success_facts_cannot_bypass_navigation_command_semantics(PlaygroundEventKind kind, string raw, string message) {
        var kernel = PlaygroundEngine.CreateKernel(Memory(World()), characterController: new NavigationScript("path ravine", "navigate-current"));
        await Until(kernel, f => f.Kind == PlaygroundEventKind.MoveArrived);
        PlaygroundBusinessState before = kernel.World.Business;
        var fact = new PlaygroundFact(kind, before.Time, "a", changedCharacters: [before.GetCharacter("a").AdvanceDecisionGeneration()],
            lastServedActorId: "a", result: "forged legacy success").WithOperation(new("forged", raw, "forged"));
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => PlaygroundNavigationRules.Validate(before, before, fact));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    private static PlaygroundFact Copy(PlaygroundFact f, PlaygroundNavigationChange change) => new(
        f.Kind, f.At, f.ActorId, f.ResourceId, f.Light, f.ChangedResources, f.Script, f.ChangedCharacters,
        f.LastServedActorId, f.Result, f.SpatialFacts, f.Visits, f.Environment, f.ToolCallId, f.OperationRecordId,
        f.GatherStopReason, f.ObservedEvents, f.InteractionChanges, f.RawInput, f.OperationDisposition,
        f.TargetActorId, f.SpeechText, f.ItemId, f.ItemCount, f.GoalOperation, f.Economy, f.MoneyAmount, f.TradeChanges,
        f.ConfirmedPassages, f.CandidatePath, change);
}
