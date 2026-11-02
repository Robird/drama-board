using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Spatial;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;
using static DramaBoard.Playground.Tests.FollowingTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class FollowingRecoveryTests {
    [Theory]
    [InlineData("stationary")]
    [InlineData("departed")]
    [InlineData("arrival-middle")]
    [InlineData("dismissed")]
    public async Task Three_character_following_cold_opens_at_each_real_publication_boundary(string boundary) {
        using var store = new TemporaryStore();
        PlaygroundWorld initial = World();
        PlaygroundWorld saved;
        KernelCursor cursor;
        using (var history = PlaygroundDurableHistory.Create(store.StorePath, initial, InitialCursor(initial))) {
            var kernel = PlaygroundEngine.CreateKernel(history, characterController: TwoLegScript());
            await UntilFact(kernel, fact => Matches(fact, boundary));
            saved = kernel.World;
            cursor = kernel.Cursor;
            AssertBoundary(saved, boundary);
        }

        using (var history = PlaygroundDurableHistory.Open(store.StorePath)) {
            Assert.Null(history.PendingEvent);
            Assert.Equal(cursor, history.Cursor);
            AssertSameWorld(saved, history.State);
            var forbidden = new ForbiddenController();
            var resumed = PlaygroundEngine.CreateKernel(history, characterController: forbidden);
            Assert.False(resumed.RecoverPending());
            if (boundary is "departed" or "arrival-middle") {
                await UntilState(resumed, world => AllAt(world, "stream"));
                Assert.Equal(Hour(9), resumed.World.Time);
                foreach (string actor in new[] { "b", "c" }) {
                    Assert.Equal("a", resumed.World.GetCharacter(actor).FollowingLeaderId);
                    Assert.Equal(CharacterContextStatus.Closed, Interaction(resumed.World, actor).CurrentContext!.Status);
                    Assert.Equal(PlaygroundCommandKind.Follow, Interaction(resumed.World, actor).PendingAdvance!.Command.Kind);
                    Assert.Equal(1, Interaction(resumed.World, actor).UndeliveredExperiences.Count(e => e.Event.Kind == PlaygroundObservedEventKind.MoveArrived));
                }
            } else if (boundary == "dismissed") {
                PendingAdvance original = Interaction(resumed.World, "b").PendingAdvance!;
                await Advance(resumed);
                Assert.Equal(PlaygroundEventKind.DecisionContextOpened, Last(resumed).Kind);
                Assert.Equal("b", Last(resumed).ActorId);
                Assert.Null(Interaction(resumed.World, "b").PendingAdvance);
                Assert.Equal(MaterialSnapshot(original), MaterialSnapshot(Interaction(resumed.World, "b").CurrentContext!.Opening.PreviousAdvance));
                Assert.Null(resumed.World.GetCharacter("b").FollowingLeaderId);
            }
            Assert.Equal(0, forbidden.Calls);
            saved = resumed.World;
            cursor = resumed.Cursor;
        }

        using var reopened = PlaygroundDurableHistory.Open(store.StorePath);
        Assert.Null(reopened.PendingEvent);
        Assert.Equal(cursor, reopened.Cursor);
        AssertSameWorld(saved, reopened.State);
    }

    [Theory]
    [InlineData("stationary")]
    [InlineData("departed")]
    [InlineData("arrival-middle")]
    [InlineData("dismissed")]
    public async Task Event_committed_without_state_replays_exact_following_effects_without_controller_or_planning(string boundary) {
        using var store = new TemporaryStore();
        PlaygroundWorld initial = World();
        var baseline = PlaygroundEngine.CreateKernel(Memory(initial), characterController: TwoLegScript());
        await UntilFact(baseline, fact => Matches(fact, boundary));
        AssertBoundary(baseline.World, boundary);

        PlaygroundWorld before = initial;
        KernelCursor beforeCursor = InitialCursor(initial);
        using (var history = PlaygroundDurableHistory.Create(store.StorePath, initial, beforeCursor)) {
            var interrupted = PlaygroundEngine.CreateKernel(new StopAfterEvent(history, fact => Matches(fact, boundary)),
                characterController: TwoLegScript());
            for (int steps = 0; steps < 100 && !interrupted.IsFaulted; steps++) {
                before = history.State;
                beforeCursor = history.Cursor;
                try { await Advance(interrupted); }
                catch (InvalidOperationException error) when (error.InnerException is InjectedCrash) { }
            }
            Assert.True(interrupted.IsFaulted);
            Assert.Same(before, history.State);
            Assert.NotNull(history.PendingEvent);
            Assert.Equal(MaterialSnapshot(baseline.LastCompletion!.Event), MaterialSnapshot(history.PendingEvent));
        }

        using (var history = PlaygroundDurableHistory.Open(store.StorePath)) {
            Assert.Equal(beforeCursor, history.Cursor);
            AssertSameWorld(before, history.State);
            Assert.NotNull(history.PendingEvent);
            // A rule which throws from both Forecast and Plan proves recovery only folds the
            // committed membership and movement facts; it cannot rediscover or redraw the party.
            var recovered = new SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact>(history,
                new SimulationRules(0, 1024), [new ForbiddenPlanning()], PlaygroundEngine.Fold,
                (prior, instant, facts, folded) => PlaygroundEngine.Complete(prior, instant, facts, folded, PredictionMode.Incremental),
                PlaygroundEngine.Validate);
            Assert.True(recovered.RecoverPending());
            Assert.False(recovered.RecoverPending());
            Assert.Null(history.PendingEvent);
            Assert.Equal(baseline.Cursor, recovered.Cursor);
            AssertSameWorld(baseline.World, recovered.World);
            AssertBoundary(recovered.World, boundary);
        }

        using var final = PlaygroundDurableHistory.Open(store.StorePath);
        Assert.Null(final.PendingEvent);
        Assert.Equal(baseline.Cursor, final.Cursor);
        AssertSameWorld(baseline.World, final.State);
        if (boundary is "departed" or "arrival-middle") {
            var forbidden = new ForbiddenController();
            var resumed = PlaygroundEngine.CreateKernel(final, characterController: forbidden);
            await UntilState(resumed, world => AllAt(world, "stream"));
            Assert.Equal(0, forbidden.Calls);
            Assert.All(resumed.World.Characters, actor => Assert.Equal(1,
                Interaction(resumed.World, actor.Id).UndeliveredExperiences.Count(e => e.Event.Kind == PlaygroundObservedEventKind.MoveStarted)));
        }
    }

    private static bool Matches(PlaygroundFact fact, string boundary) => boundary switch {
        "stationary" => fact.Kind == PlaygroundEventKind.FollowStarted && fact.ActorId == "c",
        "departed" => fact.Kind == PlaygroundEventKind.MoveStarted,
        "arrival-middle" => fact.Kind == PlaygroundEventKind.MoveArrived,
        "dismissed" => fact.Kind == PlaygroundEventKind.FollowersDismissed,
        _ => throw new ArgumentOutOfRangeException(nameof(boundary))
    };

    private static void AssertBoundary(PlaygroundWorld world, string boundary) {
        if (boundary == "dismissed") {
            Assert.All(world.Characters, actor => Assert.Null(actor.FollowingLeaderId));
            Assert.True(AllAt(world, "ravine"));
        } else {
            Assert.Null(world.GetCharacter("a").FollowingLeaderId);
            Assert.Equal("a", world.GetCharacter("b").FollowingLeaderId);
            Assert.Equal("a", world.GetCharacter("c").FollowingLeaderId);
            if (boundary == "stationary") { Assert.True(AllAt(world, "camp")); }
            if (boundary == "departed") {
                Assert.All(world.Expedition!.Spatial.Entities, entity => Assert.IsType<TraversingLocation>(entity.Location));
            }
            if (boundary == "arrival-middle") {
                Assert.Equal(Hour(9), world.Time);
                Assert.Single(world.Expedition!.Spatial.Entities, entity => entity.Location is AtPlaceLocation);
                Assert.Equal(2, world.Expedition.Spatial.Entities.Count(entity => entity.Location is TraversingLocation));
            }
        }
        foreach (string actor in new[] { "b", "c" }) {
            CharacterInteractionState state = Interaction(world, actor);
            Assert.Equal(CharacterContextStatus.Closed, state.CurrentContext!.Status);
            Assert.Equal(PlaygroundCommandKind.Follow, state.PendingAdvance!.Command.Kind);
            Assert.Null(state.PendingAdvance.ActivityId);
            Assert.Null(state.PendingAdvance.MovementGeneration);
        }
    }

    private sealed class ForbiddenPlanning : IOccurrenceRule<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> {
        public IReadOnlyList<OccurrenceCandidate<PlaygroundPrediction>> Forecast(PlaygroundWorld world, SimulationRules rules) =>
            throw new InvalidOperationException("Following pending recovery cannot forecast or reselect members.");
        public ValueTask<TransitionDraft<PlaygroundFact>> PlanSelectedAsync(PlaygroundWorld world,
            OccurrenceCandidate<PlaygroundPrediction> winner, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Following pending recovery cannot invoke a controller or replan movement.");
    }
}
