using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Playground;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class MultiplayerRecoveryTests {
    [Theory]
    [InlineData("join")]
    [InlineData("depletion")]
    [InlineData("confirmation")]
    public async Task Multiplayer_event_only_recovery_restores_atomic_effects_without_planning(string boundary) {
        PlaygroundWorld initial = CompetitionWorld();
        var expected = PlaygroundEngine.CreateKernel(Memory(initial), characterController: CompetitionSource());
        int stepCount = 0;
        PlaygroundWorld before;
        do {
            before = expected.World;
            await Step(expected);
            Assert.True(++stepCount < 30, "The requested multiplayer boundary was never reached.");
        } while (!AtBoundary(boundary, before, expected.World));

        string expectedState = expected.World.DescribeState();
        string expectedEvent = Describe(expected.LastCompletion!.Event);
        KernelCursor expectedCursor = expected.Cursor;
        using var directory = new TemporaryStore();
        using (var history = PlaygroundDurableHistory.Create(directory.StorePath, initial, InitialCursor(initial))) {
            var interrupted = PlaygroundEngine.CreateKernel(new FailAfterEventNumber(history, stepCount),
                characterController: CompetitionSource());
            for (int step = 1; step < stepCount; step++) { await Step(interrupted); }
            string oldState = interrupted.World.DescribeState();
            KernelCursor oldCursor = interrupted.Cursor;

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => interrupted.StepAsync(Hour(240)).AsTask());

            Assert.IsType<InjectedCrash>(error.InnerException);
            Assert.True(interrupted.IsFaulted);
            Assert.Equal(oldState, history.State.DescribeState());
            Assert.Equal(oldCursor, history.Cursor);
            Assert.Equal(before.DescribeState(), oldState);
            Assert.Equal(expectedEvent, Describe(Assert.IsType<OccurrenceEvent<PlaygroundFact>>(history.PendingEvent)));
        }

        using (var reopened = PlaygroundDurableHistory.Open(directory.StorePath)) {
            Assert.Equal(before.DescribeState(), reopened.State.DescribeState());
            var metrics = new PlaygroundMetrics();
            int completions = 0;
            var resumed = new SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact>(reopened,
                new SimulationRules(0, 1024), [new ForbiddenPlanning()], PlaygroundEngine.Fold,
                (old, instant, facts, folded) => {
                    completions++;
                    return PlaygroundEngine.Complete(old, instant, facts, folded, PredictionMode.Incremental, metrics);
                }, PlaygroundEngine.Validate);

            Assert.True(resumed.RecoverPending());
            Assert.Equal(1, completions);
            Assert.Equal(0, metrics.PredictionChecks);
            Assert.Equal(expectedState, resumed.World.DescribeState());
            Assert.Equal(expectedCursor, resumed.Cursor);
            Assert.Equal(expectedEvent, Describe(resumed.LastCompletion!.Event));
            AssertPredictions(resumed.World);
            Assert.False(resumed.RecoverPending());
            Assert.Equal(1, completions);

            if (boundary == "depletion") {
                Assert.Equal(0, resumed.World.GetResource("sunflower").Stock);
                Assert.Equal(1, resumed.World.GetCharacter("b").Backpack.Count("item.sunflower"));
                PlaygroundNotice notice = Assert.Single(resumed.World.GetCharacter("a").Notices);
                Assert.Equal(PlaygroundNoticeKind.ResourceDepleted, notice.Kind);
                Assert.Equal("b", notice.ActorId);
                Assert.Equal(1, notice.Count);
                Assert.Equal(Hour(17), resumed.World.GetCharacter("a").Activity!.Due);
            }
            if (boundary == "confirmation") {
                Assert.Empty(resumed.World.GetCharacter("a").Notices);
                Assert.Equal(before.GetCharacter("a").Activity!.ToString(), resumed.World.GetCharacter("a").Activity!.ToString());
                Assert.DoesNotContain(resumed.World.Forecasts.Predictions,
                    prediction => prediction.OwnerId == "actor:a:attention");
            }
        }

        using var final = PlaygroundDurableHistory.Open(directory.StorePath);
        Assert.Null(final.PendingEvent);
        Assert.Equal(expectedState, final.State.DescribeState());
        Assert.Equal(expectedCursor, final.Cursor);
        AssertPredictions(final.State);
    }

    [Fact]
    public async Task Cancelling_input_before_continue_keeps_pending_notice_and_activity_after_cold_open() {
        using var directory = new TemporaryStore();
        PlaygroundWorld initial = CompetitionWorld();
        string savedState;
        KernelCursor savedCursor;
        using (var history = PlaygroundDurableHistory.Create(directory.StorePath, initial, InitialCursor(initial))) {
            var preparation = PlaygroundEngine.CreateKernel(history, characterController: CompetitionSource());
            await Until(preparation, world => world.GetResource("sunflower").Stock == 0);
            // b has just finished. Persist its next wait, so the next request is a's depletion response.
            while (preparation.World.GetCharacter("b").Activity is null) { await Step(preparation); }
            Assert.Single(preparation.World.GetCharacter("a").Notices);
            if (preparation.World.Business.InteractionStates["a"].CurrentContext?.Status != CharacterContextStatus.Open) {
                await Step(preparation);
                Assert.Equal(PlaygroundEventKind.DecisionContextOpened, Assert.Single(preparation.LastCompletion!.Event.Facts).Kind);
            }
            savedState = preparation.World.DescribeState();
            savedCursor = preparation.Cursor;
            var source = new GatedContinueCharacterController();
            var kernel = PlaygroundEngine.CreateKernel(history, characterController: source);
            using var cancellation = new CancellationTokenSource();
            Task<StepStatus> pending = kernel.StepAsync(Hour(240), cancellation.Token).AsTask();
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("a", source.Actor);
            Assert.Equal(savedState, history.State.DescribeState());
            Assert.Null(history.PendingEvent);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Null(history.PendingEvent);
        }

        using var reopened = PlaygroundDurableHistory.Open(directory.StorePath);
        Assert.Equal(savedState, reopened.State.DescribeState());
        Assert.Equal(savedCursor, reopened.Cursor);
        var response = Commands(PlaygroundCommand.Continue());
        var resumed = PlaygroundEngine.CreateKernel(reopened, characterController: response);
        Assert.False(resumed.RecoverPending());
        string activity = reopened.State.GetCharacter("a").Activity!.ToString();
        await Step(resumed);
        Assert.Equal(1, response.Calls);
        Assert.Empty(resumed.World.GetCharacter("a").Notices);
        Assert.Equal(activity, resumed.World.GetCharacter("a").Activity!.ToString());
        Assert.Equal(savedCursor.CurrentModelTime, resumed.World.Time);
    }

    private static PlaygroundWorld CompetitionWorld() => PlaygroundWorld.CreatePlayable(Hour(16),
        [new("a", "Alice", 0), new("b", "Bob", 2)], [Sun(Hour(16), stock: 1), Moon(Hour(16))]);

    private static StubCharacterController CompetitionSource() => new((world, id) => {
        var actor = world.Actor;
        if (actor.Activity is not null) { return PlaygroundCommand.Continue(); }
        return actor.NextActivityId == 1
            ? PlaygroundCommand.Gather("sunflower", id == "a" ? 2 : 1)
            : PlaygroundCommand.Wait(24);
    });

    private static bool AtBoundary(string boundary, PlaygroundWorld before, PlaygroundWorld after) => boundary switch {
        "join" => before.GetCharacter("b").Activity is null && after.GetCharacter("b").Activity is not null,
        "depletion" => before.GetResource("sunflower").Stock > 0 && after.GetResource("sunflower").Stock == 0,
        "confirmation" => before.GetResource("sunflower").Stock == 0 &&
            before.GetCharacter("a").Notices.Count > 0 && after.GetCharacter("a").Notices.Count == 0,
        _ => throw new ArgumentOutOfRangeException(nameof(boundary)),
    };

    private sealed class GatedContinueCharacterController : IPlaygroundCharacterController {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? Actor { get; private set; }
        public async ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) {
            Actor = context.Observation.Actor.Id;
            Entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The input should have been cancelled before returning Continue.");
        }
    }

    private sealed class ForbiddenPlanning : IOccurrenceRule<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> {
        public IReadOnlyList<OccurrenceCandidate<PlaygroundPrediction>> Forecast(PlaygroundWorld world, SimulationRules rules) =>
            throw new InvalidOperationException("Recovery must not discover or arbitrate candidates.");
        public ValueTask<TransitionDraft<PlaygroundFact>> PlanSelectedAsync(PlaygroundWorld world,
            OccurrenceCandidate<PlaygroundPrediction> winner, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Recovery must not plan or call the Player.");
    }

    private sealed class InjectedCrash : IOException { }
    private sealed class FailAfterEventNumber(IOccurrenceHistory<PlaygroundWorld, PlaygroundFact> inner, int stop)
        : IOccurrenceHistory<PlaygroundWorld, PlaygroundFact> {
        private int _events;
        public PlaygroundWorld State => inner.State;
        public KernelCursor Cursor => inner.Cursor;
        public OccurrenceEvent<PlaygroundFact>? PendingEvent => inner.PendingEvent;
        public void CommitEvent(OccurrenceEvent<PlaygroundFact> occurrence) {
            inner.CommitEvent(occurrence);
            if (++_events == stop) { throw new InjectedCrash(); }
        }
        public void CommitState(PlaygroundWorld state, KernelCursor cursor) => inner.CommitState(state, cursor);
    }
}
