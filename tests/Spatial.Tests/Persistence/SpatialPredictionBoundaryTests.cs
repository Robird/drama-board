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
    public void Cold_state_open_rejects_wrong_generation_or_expired_contact_without_a_step(bool staleGeneration) {
        using var store = new TemporaryStore();
        var (simulation, initial) = Fixture();
        SpatialSimulationState invalid = WithInvalidContact(initial, staleGeneration, GraphTestWorld.Time(-1));
        using (SpatialDurableHistory.Create(store.Path, invalid, Cursor())) { }

        using var reopened = SpatialDurableHistory.Open(store.Path);
        Assert.Throws<InvalidOperationException>(() => simulation.CreateKernel(Rules, reopened));
        Assert.Equal(Cursor(), reopened.Cursor);
        Assert.Null(reopened.PendingEvent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Empty_replay_validates_genesis_even_when_there_is_no_first_batch(bool staleGeneration) {
        var (simulation, initial) = Fixture();
        SpatialSimulationState invalid = WithInvalidContact(initial, staleGeneration, GraphTestWorld.Time(-1));

        Assert.Throws<InvalidOperationException>(() => SimulationReplay.Replay<SpatialSimulationState, SpatialSimulationFact>(
            invalid, 17, ModelTime.Zero, [], simulation.Fold, simulation.Validate));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pending_cold_recovery_validates_the_target_time_and_generation_before_state_publication(bool staleGeneration) {
        using var store = new TemporaryStore();
        var (simulation, initial) = Fixture();
        OccurrenceEvent<SpatialSimulationFact> invalid = InvalidEvent(initial, staleGeneration);
        using (var created = SpatialDurableHistory.Create(store.Path, initial, Cursor())) {
            // Deliberately malformed external E: bypass production Complete to test the recovery boundary.
            created.CommitEvent(invalid);
        }

        using (var reopened = SpatialDurableHistory.Open(store.Path)) {
            var forbidden = new ForbiddenPlanning();
            var kernel = new SimulationKernel<SpatialSimulationState, SpatialOccurrenceData, SpatialSimulationFact>(
                reopened, Rules, [forbidden], simulation.Fold,
                (_, _, _) => throw new InvalidOperationException("Recovery called Complete."), simulation.Validate);
            var error = Assert.Throws<InvalidOperationException>(() => kernel.RecoverPending());
            Assert.DoesNotContain("Complete", error.Message);
            Assert.True(kernel.IsFaulted);
            Assert.Equal(0, forbidden.Calls);
            EqualWorld(initial, kernel.World);
            Assert.Equal(Cursor(), kernel.Cursor);
            Assert.NotNull(reopened.PendingEvent);
        }

        using var stillPending = SpatialDurableHistory.Open(store.Path);
        Assert.NotNull(stillPending.PendingEvent);
        EqualWorld(initial, stillPending.State);
        Assert.Equal(Cursor(), stillPending.Cursor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Replay_validates_each_full_batch_at_its_own_time(bool staleGeneration) {
        var (simulation, initial) = Fixture();
        OccurrenceEvent<SpatialSimulationFact> invalid = InvalidEvent(initial, staleGeneration);
        var batch = new JournalBatch<SpatialSimulationFact>(invalid.TargetInstant, invalid.CauseKey, invalid.Facts);

        Assert.Throws<InvalidOperationException>(() => SimulationReplay.Replay(
            initial, 17, ModelTime.Zero, [batch], simulation.Fold, simulation.Validate));
    }

    [Fact]
    public void Genesis_rejects_an_already_expired_computed_contact_instead_of_dropping_it() {
        var (simulation, initial) = Fixture();
        Assert.Single(initial.Contacts.Entries);
        Assert.Equal(GraphTestWorld.Time(5), initial.Contacts.Entries[0].Due);

        Assert.Throws<InvalidOperationException>(() => simulation.CreateGenesis(initial.Business, GraphTestWorld.Time(6)));
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
        Assert.Empty(simulation.CreateGenesis(business, ModelTime.Zero).Contacts.Entries);

        Assert.Throws<InvalidOperationException>(() => simulation.CreateGenesis(business, GraphTestWorld.Time(11)));
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

    private static OccurrenceEvent<SpatialSimulationFact> InvalidEvent(SpatialSimulationState initial, bool staleGeneration) {
        // The expired-Due case intentionally has no replacement fact: an unrelated business event
        // leaves the old contact at t=5, so only validation against the batch's t=6 catches it.
        SpatialSimulationFact fact = staleGeneration
            ? new ContactPredictionsReplaced([GraphTestWorld.Bridge],
                WithInvalidContact(initial, true, ModelTime.Zero).Contacts.Entries)
            : new SpatialBusinessFact(new PassageEntryAccessChangedFact(GraphTestWorld.Bridge,
                new PassageEntryAccess(false, true)));
        return new OccurrenceEvent<SpatialSimulationFact>(CandidateKey.FromUtf8("malformed-test-event"),
            GraphTestWorld.Instant(staleGeneration ? 1 : 6), [fact]);
    }
}
