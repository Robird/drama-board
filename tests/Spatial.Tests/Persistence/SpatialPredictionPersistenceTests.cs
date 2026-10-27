using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial.Tests.TestSupport;

namespace DramaBoard.Spatial.Tests.Persistence;

public sealed class SpatialPredictionPersistenceTests {
    private static readonly SimulationRules Rules = new(71, 100);

    [Fact]
    public async Task Every_completed_state_cold_reopens_and_continues_the_same_trace() {
        using var store = new TemporaryStore();
        var (simulation, initial) = Fixture();
        var memory = new InMemoryOccurrenceHistory<SpatialSimulationState, SpatialSimulationFact>(initial, Cursor());
        var baseline = simulation.CreateKernel(Rules, memory);
        using (SpatialDurableHistory.Create(store.Path, initial, Cursor())) { }

        int completed = 0;
        for (; completed < 10; completed++) {
            using var reopened = SpatialDurableHistory.Open(store.Path);
            Assert.Null(reopened.PendingEvent);
            Assert.NotSame(initial, reopened.State);
            Assert.NotSame(initial.Business, reopened.State.Business);
            Assert.NotSame(initial.Contacts, reopened.State.Contacts);
            EqualWorld(baseline.World, reopened.State);
            Assert.Equal(baseline.Cursor, reopened.Cursor);
            var resumed = simulation.CreateKernel(Rules, reopened);
            Assert.False(resumed.RecoverPending());

            StepStatus expected = await baseline.StepAsync(GraphTestWorld.Time(20));
            Assert.Equal(expected, await resumed.StepAsync(GraphTestWorld.Time(20)));
            EqualWorld(baseline.World, resumed.World);
            Assert.Equal(baseline.Cursor, resumed.Cursor);
            if (expected != StepStatus.Committed) { break; }
            EqualEvent(baseline.LastCompletion!.Event, resumed.LastCompletion!.Event);
        }

        // Contact, scheduled access, and both arrivals all cross fresh repositories.
        Assert.Equal(4, completed);
        Assert.Empty(baseline.World.Contacts.Entries);
        Assert.All(baseline.World.Business.Entities, entity => Assert.IsType<AtPlaceLocation>(entity.Location));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(0, true)]
    [InlineData(2, true)]
    public async Task Event_only_cold_recovery_uses_saved_predictions_without_complete_or_player(
        int priorSteps, bool failState) {
        using var store = new TemporaryStore();
        var (simulation, initial) = Fixture();
        var memory = new InMemoryOccurrenceHistory<SpatialSimulationState, SpatialSimulationFact>(initial, Cursor());
        var baseline = simulation.CreateKernel(Rules, memory);
        OccurrenceEvent<SpatialSimulationFact> saved;
        SpatialSimulationState before;
        KernelCursor beforeCursor;

        using (var history = SpatialDurableHistory.Create(store.Path, initial, Cursor())) {
            var running = simulation.CreateKernel(Rules, history);
            for (int index = 0; index < priorSteps; index++) {
                Assert.Equal(StepStatus.Committed, await running.StepAsync(GraphTestWorld.Time(20)));
                Assert.Equal(StepStatus.Committed, await baseline.StepAsync(GraphTestWorld.Time(20)));
            }
            before = running.World;
            beforeCursor = running.Cursor;
            var interrupted = simulation.CreateKernel(Rules, new PublicationFailure(history, failState));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => interrupted.StepAsync(GraphTestWorld.Time(20)).AsTask());
            Assert.IsType<InjectedFailure>(error.InnerException);
            Assert.True(interrupted.IsFaulted);
            Assert.Same(before, history.State);
            Assert.Equal(beforeCursor, history.Cursor);
            saved = Assert.IsType<OccurrenceEvent<SpatialSimulationFact>>(history.PendingEvent);
            Assert.Equal(StepStatus.Committed, await baseline.StepAsync(GraphTestWorld.Time(20)));
            EqualEvent(baseline.LastCompletion!.Event, saved);
        }

        using (var reopened = SpatialDurableHistory.Open(store.Path)) {
            EqualWorld(before, reopened.State);
            Assert.Equal(beforeCursor, reopened.Cursor);
            EqualEvent(saved, Assert.IsType<OccurrenceEvent<SpatialSimulationFact>>(reopened.PendingEvent));
            Assert.NotSame(saved, reopened.PendingEvent);
            Assert.NotSame(saved.Facts[0], reopened.PendingEvent!.Facts[0]);
            var forbidden = new ForbiddenPlanning();
            var recovering = new SimulationKernel<SpatialSimulationState, SpatialOccurrenceData, SpatialSimulationFact>(
                reopened, Rules, [forbidden], simulation.Fold,
                (_, _, _) => throw new InvalidOperationException("Recovery called Complete."), simulation.Validate);

            Assert.True(recovering.RecoverPending());
            EqualEvent(saved, recovering.LastCompletion!.Event);
            Assert.False(recovering.RecoverPending());
            Assert.Equal(0, forbidden.Calls);
            EqualWorld(baseline.World, recovering.World);
            Assert.Equal(baseline.Cursor, recovering.Cursor);
            Assert.Null(reopened.PendingEvent);

            var resumed = simulation.CreateKernel(Rules, reopened);
            for (int remaining = priorSteps + 1; remaining < 4; remaining++) {
                Assert.Equal(StepStatus.Committed, await baseline.StepAsync(GraphTestWorld.Time(20)));
                Assert.Equal(StepStatus.Committed, await resumed.StepAsync(GraphTestWorld.Time(20)));
                EqualEvent(baseline.LastCompletion!.Event, resumed.LastCompletion!.Event);
                EqualWorld(baseline.World, resumed.World);
            }
        }

        using var final = SpatialDurableHistory.Open(store.Path);
        Assert.Null(final.PendingEvent);
        EqualWorld(baseline.World, final.State);
        Assert.Equal(baseline.Cursor, final.Cursor);
    }

    [Fact]
    public async Task Rejected_state_commit_faults_adapter_and_leaves_durable_event_recoverable() {
        using var store = new TemporaryStore();
        var (simulation, initial) = Fixture();
        var memory = new InMemoryOccurrenceHistory<SpatialSimulationState, SpatialSimulationFact>(initial, Cursor());
        var expected = simulation.CreateKernel(Rules, memory);
        Assert.Equal(StepStatus.Committed, await expected.StepAsync(GraphTestWorld.Time(20)));

        using (var history = SpatialDurableHistory.Create(store.Path, initial, Cursor())) {
            history.CommitEvent(expected.LastCompletion!.Event);
            Assert.Throws<ArgumentException>(() => history.CommitState(expected.World, Cursor()));
            Assert.True(history.IsFaulted);
            Assert.Throws<InvalidOperationException>(() => history.State);
        }
        using var reopened = SpatialDurableHistory.Open(store.Path);
        var recovered = simulation.CreateKernel(Rules, reopened);
        Assert.True(recovered.RecoverPending());
        EqualWorld(expected.World, recovered.World);
        Assert.Equal(expected.Cursor, recovered.Cursor);
    }

    [Fact]
    public async Task Newcomer_event_cold_recovers_its_nonempty_prediction_replacement_without_rediscovery() {
        using var store = new TemporaryStore();
        var definition = GraphDefinition.Create([GraphTestWorld.A, GraphTestWorld.B],
            [GraphTestWorld.Passage(GraphTestWorld.Bridge, GraphTestWorld.A, GraphTestWorld.B, length: 20)]);
        var business = GraphTestWorld.State(definition, ("alice", GraphTestWorld.A), ("bob", GraphTestWorld.B));
        business = new GraphSpatialReducer(definition).Apply(business, GraphTestWorld.Instant(0),
            new TraversalStartedFact(new EntityId("alice"), GraphTestWorld.Bridge, GraphTestWorld.A, 1));
        var simulation = new SpatialSimulation(definition);
        SpatialSimulationState initial = simulation.CreateGenesis(business, ModelTime.Zero);
        Assert.Empty(initial.Contacts.Entries);
        var memory = new InMemoryOccurrenceHistory<SpatialSimulationState, SpatialSimulationFact>(initial, Cursor());
        var baseline = simulation.CreateKernel(Rules, memory, [new NewcomerRule()]);
        Assert.Equal(StepStatus.Committed, await baseline.StepAsync(GraphTestWorld.Time(30)));
        ContactPrediction expected = Assert.Single(baseline.World.Contacts.Entries);
        Assert.Equal(new EntityId("alice"), expected.ContactKey.EntityA);
        Assert.Equal(new EntityId("bob"), expected.ContactKey.EntityB);
        Assert.Equal(1, expected.ContactKey.MovementGenerationA);
        Assert.Equal(1, expected.ContactKey.MovementGenerationB);
        Assert.Equal(GraphTestWorld.Time(10), expected.Due);
        Assert.Equal(PassageContactKind.HeadOnMeeting, expected.Kind);

        using (var history = SpatialDurableHistory.Create(store.Path, initial, Cursor())) {
            var interrupted = simulation.CreateKernel(Rules, new PublicationFailure(history, true), [new NewcomerRule()]);
            SpatialSimulationState beforePublication = interrupted.World;
            EqualWorld(initial, beforePublication);
            await Assert.ThrowsAsync<InvalidOperationException>(() => interrupted.StepAsync(GraphTestWorld.Time(30)).AsTask());
            Assert.Same(beforePublication, interrupted.World);
            Assert.Same(beforePublication, history.State);
            EqualWorld(initial, beforePublication);
            Assert.Empty(initial.Contacts.Entries);
            var published = Assert.IsType<ContactPredictionsReplaced>(history.PendingEvent!.Facts[^1]);
            Assert.Equal(expected, Assert.Single(published.Predictions));
        }

        using (var reopened = SpatialDurableHistory.Open(store.Path)) {
            SpatialSimulationState beforeRecovery = reopened.State;
            Assert.Empty(reopened.State.Contacts.Entries);
            var published = Assert.IsType<ContactPredictionsReplaced>(reopened.PendingEvent!.Facts[^1]);
            ContactPrediction restoredOutput = Assert.Single(published.Predictions);
            Assert.NotSame(expected, restoredOutput);
            Assert.Equal(expected, restoredOutput);
            var forbidden = new ForbiddenPlanning();
            var recovering = new SimulationKernel<SpatialSimulationState, SpatialOccurrenceData, SpatialSimulationFact>(
                reopened, Rules, [forbidden], simulation.Fold,
                (_, _, _) => throw new InvalidOperationException("Recovery called Complete."), simulation.Validate);
            Assert.True(recovering.RecoverPending());
            Assert.Equal(0, forbidden.Calls);
            EqualWorld(baseline.World, recovering.World);
            Assert.Equal(expected, Assert.Single(recovering.World.Contacts.Entries));
            Assert.Equal(baseline.Cursor, recovering.Cursor);
            // The old published root must remain unchanged even after the replacement is folded.
            EqualWorld(initial, beforeRecovery);
            Assert.NotSame(beforeRecovery, recovering.World);
            Assert.Empty(initial.Contacts.Entries);
            Assert.IsType<AtPlaceLocation>(initial.Business.Entities.Single(entity => entity.Id == new EntityId("bob")).Location);

            var resumed = simulation.CreateKernel(Rules, reopened);
            for (int index = 0; index < 3; index++) {
                Assert.Equal(StepStatus.Committed, await baseline.StepAsync(GraphTestWorld.Time(30)));
                Assert.Equal(StepStatus.Committed, await resumed.StepAsync(GraphTestWorld.Time(30)));
                EqualEvent(baseline.LastCompletion!.Event, resumed.LastCompletion!.Event);
                EqualWorld(baseline.World, resumed.World);
            }
        }
        using var final = SpatialDurableHistory.Open(store.Path);
        EqualWorld(baseline.World, final.State);
        Assert.Equal(baseline.Cursor, final.Cursor);
    }

    internal static (SpatialSimulation Simulation, SpatialSimulationState World) Fixture() {
        var definition = GraphDefinition.Create([GraphTestWorld.A, GraphTestWorld.B],
            [GraphTestWorld.Passage(GraphTestWorld.Bridge, GraphTestWorld.A, GraphTestWorld.B, length: 20)]);
        var business = GraphTestWorld.State(definition, ("alice", GraphTestWorld.A), ("bob", GraphTestWorld.B));
        var reducer = new GraphSpatialReducer(definition);
        business = reducer.Apply(business, GraphTestWorld.Instant(0),
            new TraversalStartedFact(new EntityId("alice"), GraphTestWorld.Bridge, GraphTestWorld.A, 2));
        business = reducer.Apply(business, GraphTestWorld.Instant(0),
            new TraversalStartedFact(new EntityId("bob"), GraphTestWorld.Bridge, GraphTestWorld.B, 2));
        business = reducer.Apply(business, GraphTestWorld.Instant(0),
            new PassageEntryChangeScheduledFact(GraphTestWorld.Bridge, GraphTestWorld.Time(7),
                new PassageEntryPatch(false, null)));
        var simulation = new SpatialSimulation(definition);
        return (simulation, simulation.CreateGenesis(business, ModelTime.Zero));
    }

    internal static KernelCursor Cursor() => new(new WorldVersion(17, 0), ModelTime.Zero, null, null);

    internal static void EqualWorld(SpatialSimulationState expected, SpatialSimulationState actual) {
        Assert.Equal(expected.Business, actual.Business);
        Assert.Equal(expected.Contacts.Entries.Select(value => (value.ContactKey, value.Due, value.Kind)),
            actual.Contacts.Entries.Select(value => (value.ContactKey, value.Due, value.Kind)));
    }

    private static void EqualEvent(OccurrenceEvent<SpatialSimulationFact> expected,
        OccurrenceEvent<SpatialSimulationFact> actual) {
        Assert.Equal(expected.CauseKey, actual.CauseKey);
        Assert.Equal(expected.TargetInstant, actual.TargetInstant);
        Assert.Equal(expected.Facts.Count, actual.Facts.Count);
        for (int index = 0; index < expected.Facts.Count; index++) {
            if (expected.Facts[index] is SpatialBusinessFact business) {
                Assert.Equal(business.Fact, Assert.IsType<SpatialBusinessFact>(actual.Facts[index]).Fact);
            } else {
                var replacement = Assert.IsType<ContactPredictionsReplaced>(expected.Facts[index]);
                var restored = Assert.IsType<ContactPredictionsReplaced>(actual.Facts[index]);
                Assert.Equal(replacement.Passages, restored.Passages);
                Assert.Equal(replacement.Predictions.Select(value => (value.ContactKey, value.Due, value.Kind)),
                    restored.Predictions.Select(value => (value.ContactKey, value.Due, value.Kind)));
            }
        }
    }

    internal sealed class ForbiddenPlanning : IOccurrenceRule<SpatialSimulationState, SpatialOccurrenceData, SpatialSimulationFact> {
        public int Calls { get; private set; }
        public IReadOnlyList<OccurrenceCandidate<SpatialOccurrenceData>> Forecast(SpatialSimulationState world,
            SimulationRules rules) {
            Calls++;
            throw new InvalidOperationException("Recovery read candidates.");
        }
        public ValueTask<TransitionDraft<SpatialSimulationFact>> PlanSelectedAsync(SpatialSimulationState world,
            OccurrenceCandidate<SpatialOccurrenceData> winner, CancellationToken cancellationToken) {
            Calls++;
            throw new InvalidOperationException("Recovery planned an action or called its Player.");
        }
    }

    private sealed class InjectedFailure : IOException { }

    private sealed record NewcomerData : SpatialOccurrenceData;

    private sealed class NewcomerRule : IOccurrenceRule<SpatialSimulationState, SpatialOccurrenceData, SpatialSimulationFact> {
        public IReadOnlyList<OccurrenceCandidate<SpatialOccurrenceData>> Forecast(SpatialSimulationState world,
            SimulationRules rules) => world.Business.Entities.Single(entity => entity.Id == new EntityId("bob")).MovementGeneration == 0
            ? [new OccurrenceCandidate<SpatialOccurrenceData>(CandidateKey.FromUtf8("test/newcomer"),
                new CandidateDue(GraphTestWorld.Time(1)), new NewcomerData())]
            : [];

        public ValueTask<TransitionDraft<SpatialSimulationFact>> PlanSelectedAsync(SpatialSimulationState world,
            OccurrenceCandidate<SpatialOccurrenceData> winner, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new TransitionDraft<SpatialSimulationFact>([
                new SpatialBusinessFact(new TraversalStartedFact(new EntityId("bob"), GraphTestWorld.Bridge, GraphTestWorld.B, 1)),
            ]));
    }

    private sealed class PublicationFailure(SpatialDurableHistory inner, bool failState)
        : IOccurrenceHistory<SpatialSimulationState, SpatialSimulationFact> {
        public SpatialSimulationState State => inner.State;
        public KernelCursor Cursor => inner.Cursor;
        public OccurrenceEvent<SpatialSimulationFact>? PendingEvent => inner.PendingEvent;
        public void CommitEvent(OccurrenceEvent<SpatialSimulationFact> occurrence) {
            inner.CommitEvent(occurrence);
            if (!failState) { throw new InjectedFailure(); }
        }
        public void CommitState(SpatialSimulationState state, KernelCursor cursor) => throw new InjectedFailure();
    }

    internal sealed class TemporaryStore : IDisposable {
        private readonly string _parent = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DramaBoard.Spatial.Tests", Guid.NewGuid().ToString("N"));
        public TemporaryStore() { Directory.CreateDirectory(_parent); }
        public string Path => System.IO.Path.Combine(_parent, "repository");
        public void Dispose() { Directory.Delete(_parent, recursive: true); }
    }
}
