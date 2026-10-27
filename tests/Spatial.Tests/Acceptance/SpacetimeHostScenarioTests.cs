using DramaBoard.Host;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial.Tests.TestSupport;

namespace DramaBoard.Spatial.Tests.Acceptance;

public sealed class SpacetimeHostScenarioTests {
    private static readonly EntityId Alice = new("alice");
    private static readonly EntityId Bob = new("bob");

    [Fact]
    public async Task Host_MeetsReversesAndArrivesDeterministicallyWithoutPlayer() {
        InMemoryOccurrenceHistory<SpatialSimulationState, SpatialSimulationFact> first = await RunScenario(false);
        InMemoryOccurrenceHistory<SpatialSimulationState, SpatialSimulationFact> permuted = await RunScenario(true);

        Assert.Equal(
            first.CompletedEvents.Select(value => value.CauseKey),
            permuted.CompletedEvents.Select(value => value.CauseKey));
        Assert.Equal(
            first.CompletedEvents.Select(value => value.Facts.OfType<SpatialBusinessFact>().Single().Fact),
            permuted.CompletedEvents.Select(value => value.Facts.OfType<SpatialBusinessFact>().Single().Fact));
    }

    private static async Task<InMemoryOccurrenceHistory<SpatialSimulationState, SpatialSimulationFact>> RunScenario(
        bool reverseInputs) {
        GraphDefinition definition = GraphDefinition.Create(
            [GraphTestWorld.A, GraphTestWorld.B],
            [GraphTestWorld.Passage(GraphTestWorld.Bridge, GraphTestWorld.A, GraphTestWorld.B, length: 11)]);
        EntityPlacement[] placements = [new(Alice, GraphTestWorld.A), new(Bob, GraphTestWorld.B)];
        if (reverseInputs) {
            Array.Reverse(placements);
        }
        GraphSpatialState genesis = GraphSpatialState.Create(definition, placements);
        var planner = new SpatialPlanner(definition);
        var reducer = new GraphSpatialReducer(definition);
        genesis = GraphTestWorld.Fold(reducer, genesis, GraphTestWorld.Instant(0),
            planner.TryStartTraversal(genesis, Alice, GraphTestWorld.Bridge, 3, ModelTime.Zero));
        genesis = GraphTestWorld.Fold(reducer, genesis, GraphTestWorld.Instant(0, 1),
            planner.TryStartTraversal(genesis, Bob, GraphTestWorld.Bridge, 2, ModelTime.Zero));
        Assert.Equal(GraphTestWorld.Time(4), Traversal(genesis, Alice).ArrivalDue); // ceil(11 / 3)
        Assert.Equal(GraphTestWorld.Time(6), Traversal(genesis, Bob).ArrivalDue); // ceil(11 / 2)
        GraphTestWorld.Rejected(planner.TryReverseTraversal(genesis, Alice, ModelTime.Zero),
            "reverse-outside-active-interval");
        GraphTestWorld.Rejected(planner.TryReverseTraversal(genesis, Alice, GraphTestWorld.Time(4)),
            "reverse-outside-active-interval");

        var simulation = new SpatialSimulation(definition);
        var root = simulation.CreateGenesis(genesis, ModelTime.Zero);
        var history = new InMemoryOccurrenceHistory<SpatialSimulationState, SpatialSimulationFact>(root,
            new KernelCursor(new WorldVersion(1, 0), ModelTime.Zero, null, null));
        IOccurrenceRule<SpatialSimulationState, object, SpatialSimulationFact>[] rules = [
            new RuleAdapter<SpatialOccurrenceData>(new SpatialOccurrenceRule(definition)),
            new RuleAdapter<SpatialOccurrenceData>(new SpatialContactOccurrenceRule(definition)),
            new ReverseAtThreeRule(planner),
        ];
        if (reverseInputs) {
            Array.Reverse(rules);
        }
        int completions = 0;
        var kernel = new SimulationKernel<SpatialSimulationState, object, SpatialSimulationFact>(history,
            new SimulationRules(worldSeed: 71, maxTransitionsPerModelTime: 100), rules,
            simulation.Fold, (before, instant, draft) => {
                completions++;
                return simulation.Complete(before, instant, draft);
            }, simulation.Validate);

        // Exact meeting is at 11 / (3 + 2) = 2.2; contact belongs to tick 2.
        HostRunResult<SpatialSimulationState> meeting = await SimulationHost.RunUntilAsync(kernel, GraphTestWorld.Time(2));
        Assert.Equal(StepStatus.BoundaryReached, meeting.Status);
        Assert.Equal(1, meeting.CommittedTransitionCount);
        Assert.Equal(GraphTestWorld.Time(2), meeting.CurrentModelTime);
        PassageContactOccurredFact contact = Assert.IsType<PassageContactOccurredFact>(
            Assert.Single(Assert.Single(history.CompletedEvents).Facts.OfType<SpatialBusinessFact>()).Fact);
        ContactOracle.AssertMatches(definition, meeting.World);
        Assert.Equal(PassageContactKind.HeadOnMeeting, contact.Kind);
        Assert.Equal(contact.ContactKey, Assert.Single(meeting.World.Business.ConsumedContacts));
        HostRunResult<SpatialSimulationState> repeatedBoundary = await SimulationHost.RunUntilAsync(kernel, GraphTestWorld.Time(2));
        Assert.Equal(0, repeatedBoundary.CommittedTransitionCount);
        Assert.Single(history.CompletedEvents); // Current-segment contact is consumed exactly once.

        HostRunResult<SpatialSimulationState> reversed = await SimulationHost.RunUntilAsync(kernel, GraphTestWorld.Time(3));
        Assert.Equal(StepStatus.BoundaryReached, reversed.Status);
        Assert.Equal(1, reversed.CommittedTransitionCount);
        ContactOracle.AssertMatches(definition, reversed.World);
        TraversingLocation segment = Traversal(reversed.World.Business, Alice);
        Assert.Equal(9, segment.AnchorOffset); // Strictly inside (0, 11).
        Assert.Equal(GraphTestWorld.Time(3), segment.AnchorTime);
        Assert.Equal(GraphTestWorld.A, segment.TargetPlaceId);
        Assert.Equal(GraphTestWorld.Time(6), segment.ArrivalDue);
        Assert.Equal(2, Entity(reversed.World.Business, Alice).MovementGeneration);
        Assert.Empty(reversed.World.Business.ConsumedContacts); // Old-generation consumption is retired.
        Assert.Equal(new TraversalReversedFact(Alice, 1), Assert.Single(history.CompletedEvents[1].Facts.OfType<SpatialBusinessFact>()).Fact);

        HostRunResult<SpatialSimulationState> oldArrival = await SimulationHost.RunUntilAsync(kernel, GraphTestWorld.Time(4));
        Assert.Equal(0, oldArrival.CommittedTransitionCount);
        Assert.Equal(GraphTestWorld.Time(3), oldArrival.CurrentModelTime); // Invalidated arrival cannot advance time.
        HostRunResult<SpatialSimulationState> arrived = await SimulationHost.RunUntilAsync(kernel, GraphTestWorld.Time(10));
        Assert.Equal(StepStatus.Exhausted, arrived.Status);
        Assert.Equal(2, arrived.CommittedTransitionCount);
        Assert.Equal(GraphTestWorld.Time(6), arrived.CurrentModelTime);
        Assert.Equal(new WorldVersion(1, 4), arrived.Version);
        Assert.All(arrived.World.Business.Entities, entity =>
            Assert.Equal(GraphTestWorld.A, Assert.IsType<AtPlaceLocation>(entity.Location).PlaceId));
        Assert.Empty(arrived.World.Business.ConsumedContacts);
        Assert.Equal(
            [GraphTestWorld.Instant(2), GraphTestWorld.Instant(3), GraphTestWorld.Instant(6), GraphTestWorld.Instant(6, 1)],
            history.CompletedEvents.Select(value => value.TargetInstant));
        TraversalArrivedFact[] arrivals = history.CompletedEvents.Skip(2)
            .Select(value => Assert.IsType<TraversalArrivedFact>(Assert.Single(value.Facts.OfType<SpatialBusinessFact>()).Fact)).ToArray();
        Assert.Contains(new TraversalArrivedFact(Alice, 2), arrivals);
        Assert.Contains(new TraversalArrivedFact(Bob, 1), arrivals);

        ReplayResult<SpatialSimulationState> replay = SimulationReplay.Replay(root, 1, ModelTime.Zero,
            history.CompletedEvents.Select(value => new JournalBatch<SpatialSimulationFact>(
                value.TargetInstant, value.CauseKey, value.Facts)).ToArray(),
            simulation.Fold, simulation.Validate);
        Assert.Equal(4, completions); // All rule sources completed once; replay completed zero times.
        ContactOracle.AssertMatches(definition, arrived.World);
        Assert.Equal(arrived.World, replay.World);
        Assert.Equal(arrived.Version, replay.Version);
        Assert.Equal(kernel.LastCommittedInstant, replay.LastCommittedInstant);
        return history;
    }

    private static SpatialEntity Entity(GraphSpatialState state, EntityId id) =>
        Assert.Single(state.Entities, entity => entity.Id == id);

    private static TraversingLocation Traversal(GraphSpatialState state, EntityId id) =>
        Assert.IsType<TraversingLocation>(Entity(state, id).Location);

    // Test composition only: preserve each production rule's identity, due time and planning.
    private sealed class RuleAdapter<TData>(IOccurrenceRule<SpatialSimulationState, TData, SpatialSimulationFact> inner)
        : IOccurrenceRule<SpatialSimulationState, object, SpatialSimulationFact> where TData : notnull {
        public IReadOnlyList<OccurrenceCandidate<object>> Forecast(SpatialSimulationState world, SimulationRules rules) =>
            inner.Forecast(world, rules).Select(candidate =>
                new OccurrenceCandidate<object>(candidate.Key, candidate.Due, candidate.Data)).ToArray();

        public ValueTask<TransitionDraft<SpatialSimulationFact>> PlanSelectedAsync(SpatialSimulationState world,
            OccurrenceCandidate<object> winner, CancellationToken cancellationToken) =>
            inner.PlanSelectedAsync(world, new OccurrenceCandidate<TData>(winner.Key, winner.Due,
                (TData)winner.Data), cancellationToken);
    }

    // A fixed scenario action, consumed by movement generation; no Player or long-term plan.
    private sealed class ReverseAtThreeRule(SpatialPlanner planner)
        : IOccurrenceRule<SpatialSimulationState, object, SpatialSimulationFact> {
        public IReadOnlyList<OccurrenceCandidate<object>> Forecast(SpatialSimulationState world, SimulationRules rules) =>
            Entity(world.Business, Alice).MovementGeneration == 1
                ? [new(new CandidateKey("scenario/reverse/alice/1"), new CandidateDue(GraphTestWorld.Time(3)), Alice)]
                : [];

        public ValueTask<TransitionDraft<SpatialSimulationFact>> PlanSelectedAsync(SpatialSimulationState world,
            OccurrenceCandidate<object> winner, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new TransitionDraft<SpatialSimulationFact>(GraphTestWorld.Accepted(
                planner.TryReverseTraversal(world.Business, (EntityId)winner.Data, winner.Due.ModelTime)).Facts.Select(f => (SpatialSimulationFact)new SpatialBusinessFact(f)).ToArray()));
        }
    }
}
