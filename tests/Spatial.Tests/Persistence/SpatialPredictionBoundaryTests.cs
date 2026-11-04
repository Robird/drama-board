using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial.Tests.TestSupport;
using static DramaBoard.Spatial.Tests.Persistence.SpatialPredictionPersistenceTests;

namespace DramaBoard.Spatial.Tests.Persistence;

public sealed class SpatialPredictionBoundaryTests {
    private static readonly SimulationRules Rules = new(71, 100);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cold_open_discards_old_runtime_cache_and_rebuilds_from_valid_business(bool staleGeneration) {
        using var store = new TemporaryStore();
        var (simulation, initial) = Fixture();
        SpatialSimulationState invalid = WithInvalidContact(initial, staleGeneration, GraphTestWorld.Time(-1));
        // This deliberately represents an obsolete cache. Only its valid Business can reach disk.
        using (var created = SpatialDurableHistory.Create(store.Path, invalid, Cursor(), simulation)) {
            EqualWorld(initial, created.State);
        }

        using var reopened = SpatialDurableHistory.Open(store.Path, simulation);
        EqualWorld(initial, reopened.State);
        Assert.NotSame(initial.Contacts, reopened.State.Contacts);
        Assert.NotSame(invalid.Contacts, reopened.State.Contacts);
        simulation.CreateKernel(Rules, reopened);
        Assert.Equal(Cursor(), reopened.Cursor);
        Assert.Null(reopened.PendingEvent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Empty_replay_validates_genesis_even_when_there_is_no_first_batch(bool staleGeneration) {
        var (simulation, initial) = Fixture();
        SpatialSimulationState invalid = WithInvalidContact(initial, staleGeneration, GraphTestWorld.Time(-1));

        Assert.Throws<InvalidOperationException>(() => SimulationReplay.Replay<SpatialSimulationState, GraphSpatialFact>(
            invalid, 17, ModelTime.Zero, [], simulation.Fold, simulation.Complete, simulation.Validate));
        Assert.Throws<InvalidOperationException>(() => SimulationFork.Create(
            invalid, ModelTime.Zero, new InMemoryJournal<GraphSpatialFact>(17), 0, 18, Rules,
            simulation.Fold, simulation.Complete, simulation.Validate));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pending_cold_recovery_validates_the_target_time_and_generation_before_state_publication(bool staleGeneration) {
        using var store = new TemporaryStore();
        var (simulation, initial) = Fixture();
        OccurrenceEvent<GraphSpatialFact> invalid = InvalidEvent(initial, staleGeneration);
        using (var created = SpatialDurableHistory.Create(store.Path, initial, Cursor(), simulation)) {
            // Deliberately malformed business E: recovery must reject the facts or their final boundary.
            created.CommitEvent(invalid);
        }

        using (var reopened = SpatialDurableHistory.Open(store.Path, simulation)) {
            var forbidden = new ForbiddenPlanning();
            var kernel = new SimulationKernel<SpatialSimulationState, SpatialOccurrenceData, GraphSpatialFact>(
                reopened, Rules, [forbidden], simulation.Fold,
                simulation.Complete, simulation.Validate);
            Assert.Throws<InvalidOperationException>(() => kernel.RecoverPending());
            Assert.True(kernel.IsFaulted);
            Assert.Equal(0, forbidden.Calls);
            EqualWorld(initial, kernel.World);
            Assert.Equal(Cursor(), kernel.Cursor);
            Assert.NotNull(reopened.PendingEvent);
        }

        using var stillPending = SpatialDurableHistory.Open(store.Path, simulation);
        Assert.NotNull(stillPending.PendingEvent);
        EqualWorld(initial, stillPending.State);
        Assert.Equal(Cursor(), stillPending.Cursor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Replay_validates_each_full_batch_at_its_own_time(bool staleGeneration) {
        var (simulation, initial) = Fixture();
        OccurrenceEvent<GraphSpatialFact> invalid = InvalidEvent(initial, staleGeneration);
        var batch = new JournalBatch<GraphSpatialFact>(invalid.TargetInstant, invalid.CauseKey, invalid.Facts);

        Assert.Throws<InvalidOperationException>(() => SimulationReplay.Replay(
            initial, 17, ModelTime.Zero, [batch], simulation.Fold, simulation.Complete, simulation.Validate));
    }

    [Fact]
    public void Genesis_rejects_an_already_expired_computed_contact_instead_of_dropping_it() {
        var (simulation, initial) = Fixture();
        Assert.Single(initial.Contacts.Entries);
        Assert.Equal(GraphTestWorld.Time(5), initial.Contacts.Entries[0].Due);

        Assert.Throws<InvalidOperationException>(() => simulation.Rebuild(initial.Business, GraphTestWorld.Time(6)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Genesis_checks_projected_arrival_and_schedule_due_even_without_contacts(bool schedule) {
        var definition = GraphDefinition.Create([GraphTestWorld.A, GraphTestWorld.B],
            [GraphTestWorld.Passage(GraphTestWorld.Bridge, GraphTestWorld.A, GraphTestWorld.B, length: 20)]);
        var business = GraphTestWorld.State(definition, ("alice", GraphTestWorld.A));
        var reducer = new GraphSpatialReducer(definition);
        business = reducer.Apply(business, GraphTestWorld.Instant(0), schedule
            ? new PassageEntryChangeScheduledFact(GraphTestWorld.Bridge, GraphTestWorld.Time(10), new PassageEntryPatch(false, null))
            : new TraversalStartedFact(new EntityId("alice"), GraphTestWorld.Bridge, GraphTestWorld.A, 2));
        var simulation = new SpatialSimulation(definition);
        Assert.Empty(simulation.Rebuild(business, ModelTime.Zero).Contacts.Entries);

        Assert.Throws<InvalidOperationException>(() => simulation.Rebuild(business, GraphTestWorld.Time(11)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void Completed_boundary_rejects_a_future_traversal_anchor_at_all_entry_points(long ticks) {
        var (definition, business) = AnchoredTraversal();
        var simulation = new SpatialSimulation(definition);
        var world = new SpatialSimulationState(business, new SpatialContactPredictions([]));
        ModelTime boundary = GraphTestWorld.Time(ticks);
        var cursor = new KernelCursor(new WorldVersion(17, 0), boundary, null, null);
        var history = new InMemoryOccurrenceHistory<SpatialSimulationState, GraphSpatialFact>(world, cursor);

        Assert.Throws<InvalidOperationException>(() => simulation.Validate(world, boundary));
        Assert.Throws<InvalidOperationException>(() => simulation.Rebuild(business, boundary));
        Assert.Throws<InvalidOperationException>(() => simulation.CreateKernel(Rules, history));
        Assert.Throws<InvalidOperationException>(() => SimulationReplay.Replay<SpatialSimulationState, GraphSpatialFact>(
            world, 17, boundary, [], simulation.Fold, simulation.Complete, simulation.Validate));
    }

    [Theory]
    [InlineData(10, 0)]
    [InlineData(15, 5)]
    [InlineData(20, 10)]
    public void Completed_boundary_keeps_the_entire_active_traversal_interval_queryable(long ticks, long offset) {
        var (definition, business) = AnchoredTraversal();
        var simulation = new SpatialSimulation(definition);
        ModelTime boundary = GraphTestWorld.Time(ticks);

        SpatialSimulationState world = simulation.Rebuild(business, boundary);
        var location = Assert.IsType<TraversingView>(new SpatialQueries(definition).GetLocation(
            world.Business, new EntityId("traveller"), boundary));

        Assert.Equal(offset, location.Offset);
        Assert.Equal(GraphTestWorld.Time(20), location.ArrivalDue);
        Assert.Empty(world.Contacts.Entries);
    }

    [Fact]
    public void Boundary_checks_every_active_traversal_and_keeps_both_interval_endpoints() {
        var (definition, later) = AnchoredTraversal();
        GraphSpatialState business = GraphSpatialState.Restore(definition, [
            new SpatialEntity(new EntityId("earlier"), 1, new TraversingLocation(
                GraphTestWorld.Bridge, 0, ModelTime.Zero, GraphTestWorld.B, 1, GraphTestWorld.Time(10))),
            .. later.Entities,
        ], [], [], []);
        var simulation = new SpatialSimulation(definition);

        Assert.Throws<InvalidOperationException>(() => simulation.Rebuild(business, ModelTime.Zero));

        ModelTime sharedBoundary = GraphTestWorld.Time(10);
        SpatialSimulationState world = simulation.Rebuild(business, sharedBoundary);
        var queries = new SpatialQueries(definition);
        Assert.Empty(world.Contacts.Entries);
        Assert.Equal(10, Assert.IsType<TraversingView>(queries.GetLocation(
            world.Business, new EntityId("earlier"), sharedBoundary)).Offset);
        Assert.Equal(0, Assert.IsType<TraversingView>(queries.GetLocation(
            world.Business, new EntityId("traveller"), sharedBoundary)).Offset);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(15, true)]
    [InlineData(20, true)]
    [InlineData(21, false)]
    public void Cold_open_requires_the_saved_cursor_inside_the_active_traversal_interval(long ticks, bool valid) {
        using var store = new TemporaryStore();
        var (definition, business) = AnchoredTraversal();
        ModelTime boundary = GraphTestWorld.Time(ticks);
        var cursor = new KernelCursor(new WorldVersion(17, 0), boundary, null, null);
        var models = new StateModelRegistry();
        SpatialTestDurableModels.Register(models);
        using (Repository repository = Repository.CreateNew(store.Path, models)) {
            // Save the actual business/cursor pair without running the adapter's Rebuild first.
            repository.CreateRootBranchFromState("traversal-boundary", new SpatialDurableRoot(business, cursor));
        }

        var simulation = new SpatialSimulation(definition);
        if (!valid) {
            Assert.Throws<InvalidOperationException>(() => {
                using var unexpected = SpatialDurableHistory.Open(store.Path, simulation);
            });
            return;
        }

        using var reopened = SpatialDurableHistory.Open(store.Path, simulation);
        Assert.Equal(cursor, reopened.Cursor);
        Assert.NotSame(business, reopened.State.Business);
        Assert.Equal(ticks - 10, Assert.IsType<TraversingView>(new SpatialQueries(definition).GetLocation(
            reopened.State.Business, new EntityId("traveller"), boundary)).Offset);
    }

    private static (GraphDefinition Definition, GraphSpatialState Business) AnchoredTraversal() {
        var definition = GraphDefinition.Create([GraphTestWorld.A, GraphTestWorld.B],
            [GraphTestWorld.Passage(GraphTestWorld.Bridge, GraphTestWorld.A, GraphTestWorld.B, length: 10)]);
        var business = GraphSpatialState.Restore(definition, [
            new SpatialEntity(new EntityId("traveller"), 1, new TraversingLocation(
                GraphTestWorld.Bridge, 0, GraphTestWorld.Time(10), GraphTestWorld.B, 1, GraphTestWorld.Time(20))),
        ], [], [], []);
        return (definition, business);
    }

    private static SpatialSimulationState WithInvalidContact(SpatialSimulationState initial,
        bool staleGeneration, ModelTime invalidDue) {
        ContactPrediction original = Assert.Single(initial.Contacts.Entries);
        PassageContactKey key = original.ContactKey;
        var replacement = staleGeneration
            ? new ContactPrediction(new PassageContactKey(key.PassageId, key.EntityA, key.MovementGenerationA + 1,
                key.EntityB, key.MovementGenerationB), original.Due, original.Kind)
            : new ContactPrediction(key, invalidDue, original.Kind);
        return new SpatialSimulationState(initial.Business, new SpatialContactPredictions([replacement]));
    }

    private static OccurrenceEvent<GraphSpatialFact> InvalidEvent(SpatialSimulationState initial, bool staleGeneration) {
        // The expired-Due case intentionally has no movement fact: an unrelated business event
        // leaves the old contact at t=5, so only validation against the batch's t=6 catches it.
        ContactPrediction original = Assert.Single(initial.Contacts.Entries);
        PassageContactKey key = original.ContactKey;
        GraphSpatialFact fact = staleGeneration
            ? new PassageContactOccurredFact(new PassageContactKey(key.PassageId, key.EntityA,
                key.MovementGenerationA + 1, key.EntityB, key.MovementGenerationB), original.Kind)
            : new PassageEntryAccessChangedFact(GraphTestWorld.Bridge, new PassageEntryAccess(false, true));
        return new OccurrenceEvent<GraphSpatialFact>(CandidateKey.FromUtf8("malformed-test-event"),
            GraphTestWorld.Instant(staleGeneration ? 5 : 6), [fact]);
    }
}
