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
        var memory = new InMemoryOccurrenceHistory<SpatialSimulationState, GraphSpatialFact>(initial, Cursor());
        var baseline = simulation.CreateKernel(Rules, memory);
        using (SpatialDurableHistory.Create(store.Path, initial, Cursor(), simulation)) { }

        int completed = 0;
        for (; completed < 10; completed++) {
            using var reopened = SpatialDurableHistory.Open(store.Path, simulation);
            Assert.Null(reopened.PendingEvent);
            Assert.NotSame(initial, reopened.State);
            Assert.NotSame(initial.Business, reopened.State.Business);
            Assert.NotSame(initial.Contacts, reopened.State.Contacts);
            Assert.Same(reopened.State, reopened.State);
            EqualWorld(baseline.World, reopened.State);
            Assert.Equal(baseline.Cursor, reopened.Cursor);
            var resumed = simulation.CreateKernel(Rules, reopened);
            Assert.False(resumed.RecoverPending());

            StepStatus expected = await baseline.StepAsync(GraphTestWorld.Time(20));
            Assert.Equal(expected, await resumed.StepAsync(GraphTestWorld.Time(20)));
            Assert.Same(resumed.World, reopened.State);
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
    public async Task Event_only_cold_recovery_rebuilds_predictions_without_planning_or_player(
        int priorSteps, bool failState) {
        using var store = new TemporaryStore();
        var (simulation, initial) = Fixture();
        var memory = new InMemoryOccurrenceHistory<SpatialSimulationState, GraphSpatialFact>(initial, Cursor());
        var baseline = simulation.CreateKernel(Rules, memory);
        OccurrenceEvent<GraphSpatialFact> saved;
        SpatialSimulationState before;
        KernelCursor beforeCursor;

        using (var history = SpatialDurableHistory.Create(store.Path, initial, Cursor(), simulation)) {
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
            saved = Assert.IsType<OccurrenceEvent<GraphSpatialFact>>(history.PendingEvent);
            Assert.Equal(StepStatus.Committed, await baseline.StepAsync(GraphTestWorld.Time(20)));
            EqualEvent(baseline.LastCompletion!.Event, saved);
        }

        using (var reopened = SpatialDurableHistory.Open(store.Path, simulation)) {
            EqualWorld(before, reopened.State);
            Assert.Equal(beforeCursor, reopened.Cursor);
            EqualEvent(saved, Assert.IsType<OccurrenceEvent<GraphSpatialFact>>(reopened.PendingEvent));
            Assert.NotSame(saved, reopened.PendingEvent);
            Assert.NotSame(saved.Facts[0], reopened.PendingEvent!.Facts[0]);
            var forbidden = new ForbiddenPlanning();
            int completions = 0;
            var recovering = new SimulationKernel<SpatialSimulationState, SpatialOccurrenceData, GraphSpatialFact>(
                reopened, Rules, [forbidden], simulation.Fold,
                (beforeState, instant, facts, folded) => {
                    completions++;
                    return simulation.Complete(beforeState, instant, facts, folded);
                }, simulation.Validate);

            Assert.True(recovering.RecoverPending());
            EqualEvent(saved, recovering.LastCompletion!.Event);
            Assert.False(recovering.RecoverPending());
            Assert.Equal(1, completions);
            Assert.Equal(0, forbidden.Calls);
            EqualWorld(baseline.World, recovering.World);
            Assert.Equal(baseline.Cursor, recovering.Cursor);
            Assert.Null(reopened.PendingEvent);
            Assert.Same(recovering.World, reopened.State);
            Assert.Same(reopened.State, reopened.State);

            var resumed = simulation.CreateKernel(Rules, reopened);
            for (int remaining = priorSteps + 1; remaining < 4; remaining++) {
                Assert.Equal(StepStatus.Committed, await baseline.StepAsync(GraphTestWorld.Time(20)));
                Assert.Equal(StepStatus.Committed, await resumed.StepAsync(GraphTestWorld.Time(20)));
                EqualEvent(baseline.LastCompletion!.Event, resumed.LastCompletion!.Event);
                EqualWorld(baseline.World, resumed.World);
            }
        }

        using var final = SpatialDurableHistory.Open(store.Path, simulation);
        Assert.Null(final.PendingEvent);
        EqualWorld(baseline.World, final.State);
        Assert.Equal(baseline.Cursor, final.Cursor);
    }

    [Fact]
    public async Task Rejected_state_commit_faults_adapter_and_leaves_durable_event_recoverable() {
        using var store = new TemporaryStore();
        var (simulation, initial) = Fixture();
        var memory = new InMemoryOccurrenceHistory<SpatialSimulationState, GraphSpatialFact>(initial, Cursor());
        var expected = simulation.CreateKernel(Rules, memory);
        Assert.Equal(StepStatus.Committed, await expected.StepAsync(GraphTestWorld.Time(20)));

        using (var history = SpatialDurableHistory.Create(store.Path, initial, Cursor(), simulation)) {
            history.CommitEvent(expected.LastCompletion!.Event);
            Assert.Throws<ArgumentException>(() => history.CommitState(expected.World, Cursor()));
            Assert.True(history.IsFaulted);
            Assert.Throws<InvalidOperationException>(() => history.State);
        }
        using var reopened = SpatialDurableHistory.Open(store.Path, simulation);
        var recovered = simulation.CreateKernel(Rules, reopened);
        Assert.True(recovered.RecoverPending());
        EqualWorld(expected.World, recovered.World);
        Assert.Equal(expected.Cursor, recovered.Cursor);
    }

    [Fact]
    public async Task Newcomer_event_cold_recovers_business_and_rederives_its_nonempty_predictions() {
        using var store = new TemporaryStore();
        var definition = GraphDefinition.Create([GraphTestWorld.A, GraphTestWorld.B],
            [GraphTestWorld.Passage(GraphTestWorld.Bridge, GraphTestWorld.A, GraphTestWorld.B, length: 20)]);
        var business = GraphTestWorld.State(definition, ("alice", GraphTestWorld.A), ("bob", GraphTestWorld.B));
        business = new GraphSpatialReducer(definition).Apply(business, GraphTestWorld.Instant(0),
            new TraversalStartedFact(new EntityId("alice"), GraphTestWorld.Bridge, GraphTestWorld.A, 1));
        var simulation = new SpatialSimulation(definition);
        SpatialSimulationState initial = simulation.Rebuild(business, ModelTime.Zero);
        Assert.Empty(initial.Contacts.Entries);
        var memory = new InMemoryOccurrenceHistory<SpatialSimulationState, GraphSpatialFact>(initial, Cursor());
        var baseline = simulation.CreateKernel(Rules, memory, [new NewcomerRule()]);
        Assert.Equal(StepStatus.Committed, await baseline.StepAsync(GraphTestWorld.Time(30)));
        ContactPrediction expected = Assert.Single(baseline.World.Contacts.Entries);
        Assert.Equal(new EntityId("alice"), expected.ContactKey.EntityA);
        Assert.Equal(new EntityId("bob"), expected.ContactKey.EntityB);
        Assert.Equal(1, expected.ContactKey.MovementGenerationA);
        Assert.Equal(1, expected.ContactKey.MovementGenerationB);
        Assert.Equal(GraphTestWorld.Time(10), expected.Due);
        Assert.Equal(PassageContactKind.HeadOnMeeting, expected.Kind);

        using (var history = SpatialDurableHistory.Create(store.Path, initial, Cursor(), simulation)) {
            var interrupted = simulation.CreateKernel(Rules, new PublicationFailure(history, true), [new NewcomerRule()]);
            SpatialSimulationState beforePublication = interrupted.World;
            EqualWorld(initial, beforePublication);
            await Assert.ThrowsAsync<InvalidOperationException>(() => interrupted.StepAsync(GraphTestWorld.Time(30)).AsTask());
            Assert.Same(beforePublication, interrupted.World);
            Assert.Same(beforePublication, history.State);
            EqualWorld(initial, beforePublication);
            Assert.Empty(initial.Contacts.Entries);
            var published = Assert.IsType<TraversalStartedFact>(Assert.Single(history.PendingEvent!.Facts));
            Assert.Equal(new EntityId("bob"), published.EntityId);
            EqualEvent(baseline.LastCompletion!.Event, history.PendingEvent);
        }

        using (var reopened = SpatialDurableHistory.Open(store.Path, simulation)) {
            SpatialSimulationState beforeRecovery = reopened.State;
            Assert.Empty(reopened.State.Contacts.Entries);
            Assert.IsType<TraversalStartedFact>(Assert.Single(reopened.PendingEvent!.Facts));
            var forbidden = new ForbiddenPlanning();
            int completions = 0;
            var recovering = new SimulationKernel<SpatialSimulationState, SpatialOccurrenceData, GraphSpatialFact>(
                reopened, Rules, [forbidden], simulation.Fold,
                (beforeState, instant, facts, folded) => {
                    completions++;
                    return simulation.Complete(beforeState, instant, facts, folded);
                }, simulation.Validate);
            Assert.True(recovering.RecoverPending());
            Assert.Equal(1, completions);
            Assert.Equal(0, forbidden.Calls);
            EqualWorld(baseline.World, recovering.World);
            Assert.Equal(expected, Assert.Single(recovering.World.Contacts.Entries));
            Assert.NotSame(expected, Assert.Single(recovering.World.Contacts.Entries));
            Assert.Equal(baseline.Cursor, recovering.Cursor);
            var newcomer = Assert.IsType<TraversingLocation>(recovering.World.Business.Entities
                .Single(entity => entity.Id == new EntityId("bob")).Location);
            Assert.Equal(recovering.CurrentModelTime, newcomer.AnchorTime);
            Assert.Equal(20, Assert.IsType<TraversingView>(new SpatialQueries(definition).GetLocation(
                recovering.World.Business, new EntityId("bob"), recovering.CurrentModelTime)).Offset);
            // Derivation creates a new runtime cache; previously published snapshots remain unchanged.
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
        using var final = SpatialDurableHistory.Open(store.Path, simulation);
        EqualWorld(baseline.World, final.State);
        Assert.Equal(baseline.Cursor, final.Cursor);
    }

    [Fact]
    public async Task Same_tick_cold_rebuild_keeps_unconsumed_contact_and_does_not_resurrect_consumed_one() {
        using var store = new TemporaryStore();
        var definition = GraphDefinition.Create([GraphTestWorld.A, GraphTestWorld.B],
            [GraphTestWorld.Passage(GraphTestWorld.Bridge, GraphTestWorld.A, GraphTestWorld.B, length: 10)]);
        var business = GraphTestWorld.State(definition, ("a", GraphTestWorld.A),
            ("b", GraphTestWorld.B), ("c", GraphTestWorld.B));
        var reducer = new GraphSpatialReducer(definition);
        foreach (var entry in new[] { ("a", GraphTestWorld.A, 3L), ("b", GraphTestWorld.B, 2L), ("c", GraphTestWorld.B, 1L) }) {
            business = reducer.Apply(business, GraphTestWorld.Instant(0),
                new TraversalStartedFact(new EntityId(entry.Item1), GraphTestWorld.Bridge, entry.Item2, entry.Item3));
        }
        var simulation = new SpatialSimulation(definition);
        SpatialSimulationState initial = simulation.Rebuild(business, ModelTime.Zero);
        Assert.Equal(2, initial.Contacts.Entries.Count);
        Assert.All(initial.Contacts.Entries, prediction => Assert.Equal(GraphTestWorld.Time(2), prediction.Due));
        var memory = new InMemoryOccurrenceHistory<SpatialSimulationState, GraphSpatialFact>(initial, Cursor());
        var baseline = simulation.CreateKernel(Rules, memory);
        Assert.Equal(StepStatus.Committed, await baseline.StepAsync(GraphTestWorld.Time(2)));

        using (var created = SpatialDurableHistory.Create(store.Path, initial, Cursor(), simulation)) {
            var first = simulation.CreateKernel(Rules, created);
            Assert.Equal(StepStatus.Committed, await first.StepAsync(GraphTestWorld.Time(2)));
            EqualWorld(baseline.World, first.World);
        }
        Assert.Equal(StepStatus.Committed, await baseline.StepAsync(GraphTestWorld.Time(2)));

        using (var reopened = SpatialDurableHistory.Open(store.Path, simulation)) {
            Assert.Single(reopened.State.Business.ConsumedContacts);
            Assert.Equal(GraphTestWorld.Time(2), Assert.Single(reopened.State.Contacts.Entries).Due);
            Assert.DoesNotContain(reopened.State.Contacts.Entries,
                entry => reopened.State.Business.ConsumedContacts.Contains(entry.ContactKey));
            var interrupted = simulation.CreateKernel(Rules, new PublicationFailure(reopened, true));
            await Assert.ThrowsAsync<InvalidOperationException>(() => interrupted.StepAsync(GraphTestWorld.Time(2)).AsTask());
            EqualEvent(baseline.LastCompletion!.Event, reopened.PendingEvent!);
            Assert.Equal(GraphTestWorld.Instant(2, 1), reopened.PendingEvent!.TargetInstant);
        }
        using (var reopened = SpatialDurableHistory.Open(store.Path, simulation)) {
            Assert.Single(reopened.State.Business.ConsumedContacts);
            Assert.Single(reopened.State.Contacts.Entries);
            var forbidden = new ForbiddenPlanning();
            var recovered = new SimulationKernel<SpatialSimulationState, SpatialOccurrenceData, GraphSpatialFact>(
                reopened, Rules, [forbidden], simulation.Fold, simulation.Complete, simulation.Validate);
            Assert.True(recovered.RecoverPending());
            Assert.Equal(0, forbidden.Calls);
            EqualWorld(baseline.World, recovered.World);
            Assert.Equal(baseline.Cursor, recovered.Cursor);
        }
        using var final = SpatialDurableHistory.Open(store.Path, simulation);
        Assert.Equal(2, final.State.Business.ConsumedContacts.Count);
        Assert.Empty(final.State.Contacts.Entries);
        EqualWorld(baseline.World, final.State);
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
        return (simulation, simulation.Rebuild(business, ModelTime.Zero));
    }

    internal static KernelCursor Cursor() => new(new WorldVersion(17, 0), ModelTime.Zero, null, null);

    internal static void EqualWorld(SpatialSimulationState expected, SpatialSimulationState actual) {
        Assert.Equal(expected.Business, actual.Business);
        Assert.Equal(expected.Contacts.Entries.Select(value => (value.ContactKey, value.Due, value.Kind)),
            actual.Contacts.Entries.Select(value => (value.ContactKey, value.Due, value.Kind)));
    }

    private static void EqualEvent(OccurrenceEvent<GraphSpatialFact> expected,
        OccurrenceEvent<GraphSpatialFact> actual) {
        Assert.Equal(expected.CauseKey, actual.CauseKey);
        Assert.Equal(expected.TargetInstant, actual.TargetInstant);
        Assert.Equal(expected.Facts, actual.Facts);
    }

    internal sealed class ForbiddenPlanning : IOccurrenceRule<SpatialSimulationState, SpatialOccurrenceData, GraphSpatialFact> {
        public int Calls { get; private set; }
        public IReadOnlyList<OccurrenceCandidate<SpatialOccurrenceData>> Forecast(SpatialSimulationState world,
            SimulationRules rules) {
            Calls++;
            throw new InvalidOperationException("Recovery read candidates.");
        }
        public ValueTask<TransitionDraft<GraphSpatialFact>> PlanSelectedAsync(SpatialSimulationState world,
            OccurrenceCandidate<SpatialOccurrenceData> winner, CancellationToken cancellationToken) {
            Calls++;
            throw new InvalidOperationException("Recovery planned an action or called its Player.");
        }
    }

    private sealed class InjectedFailure : IOException { }

    private sealed record NewcomerData : SpatialOccurrenceData;

    private sealed class NewcomerRule : IOccurrenceRule<SpatialSimulationState, SpatialOccurrenceData, GraphSpatialFact> {
        public IReadOnlyList<OccurrenceCandidate<SpatialOccurrenceData>> Forecast(SpatialSimulationState world,
            SimulationRules rules) => world.Business.Entities.Single(entity => entity.Id == new EntityId("bob")).MovementGeneration == 0
            ? [new OccurrenceCandidate<SpatialOccurrenceData>(CandidateKey.FromUtf8("test/newcomer"),
                new CandidateDue(GraphTestWorld.Time(1)), new NewcomerData())]
            : [];

        public ValueTask<TransitionDraft<GraphSpatialFact>> PlanSelectedAsync(SpatialSimulationState world,
            OccurrenceCandidate<SpatialOccurrenceData> winner, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new TransitionDraft<GraphSpatialFact>([
                new TraversalStartedFact(new EntityId("bob"), GraphTestWorld.Bridge, GraphTestWorld.B, 1),
            ]));
    }

    private sealed class PublicationFailure(SpatialDurableHistory inner, bool failState)
        : IOccurrenceHistory<SpatialSimulationState, GraphSpatialFact> {
        public SpatialSimulationState State => inner.State;
        public KernelCursor Cursor => inner.Cursor;
        public OccurrenceEvent<GraphSpatialFact>? PendingEvent => inner.PendingEvent;
        public void CommitEvent(OccurrenceEvent<GraphSpatialFact> occurrence) {
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
