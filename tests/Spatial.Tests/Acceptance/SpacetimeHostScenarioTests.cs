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
        InMemoryOccurrenceHistory<GraphSpatialState, GraphSpatialFact> first = await RunScenario(false);
        InMemoryOccurrenceHistory<GraphSpatialState, GraphSpatialFact> permuted = await RunScenario(true);

        Assert.Equal(
            first.CompletedEvents.Select(value => value.CauseKey),
            permuted.CompletedEvents.Select(value => value.CauseKey));
        Assert.Equal(
            first.CompletedEvents.Select(value => value.Facts.Single()),
            permuted.CompletedEvents.Select(value => value.Facts.Single()));
    }

    private static async Task<InMemoryOccurrenceHistory<GraphSpatialState, GraphSpatialFact>> RunScenario(
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

        var history = new InMemoryOccurrenceHistory<GraphSpatialState, GraphSpatialFact>(genesis,
            new KernelCursor(new WorldVersion(1, 0), ModelTime.Zero, null, null));
        IOccurrenceRule<GraphSpatialState, object, GraphSpatialFact>[] rules = [
            new RuleAdapter<SpatialOccurrenceData>(new SpatialOccurrenceRule(definition)),
            new RuleAdapter<PassageContactOccurrenceData>(new SpatialContactOccurrenceRule(definition)),
            new ReverseAtThreeRule(planner),
        ];
        if (reverseInputs) {
            Array.Reverse(rules);
        }
        var kernel = new SimulationKernel<GraphSpatialState, object, GraphSpatialFact>(history,
            new SimulationRules(worldSeed: 71, maxTransitionsPerModelTime: 100), rules,
            reducer.Apply, state => GraphSpatialStateValidator.ValidateComplete(definition, state));

        // Exact meeting is at 11 / (3 + 2) = 2.2; contact belongs to tick 2.
        HostRunResult<GraphSpatialState> meeting = await SimulationHost.RunUntilAsync(kernel, GraphTestWorld.Time(2));
        Assert.Equal(StepStatus.BoundaryReached, meeting.Status);
        Assert.Equal(1, meeting.CommittedTransitionCount);
        Assert.Equal(GraphTestWorld.Time(2), meeting.CurrentModelTime);
        PassageContactOccurredFact contact = Assert.IsType<PassageContactOccurredFact>(
            Assert.Single(Assert.Single(history.CompletedEvents).Facts));
        Assert.Equal(PassageContactKind.HeadOnMeeting, contact.Kind);
        Assert.Equal(contact.ContactKey, Assert.Single(meeting.World.ConsumedContacts));
        HostRunResult<GraphSpatialState> repeatedBoundary = await SimulationHost.RunUntilAsync(kernel, GraphTestWorld.Time(2));
        Assert.Equal(0, repeatedBoundary.CommittedTransitionCount);
        Assert.Single(history.CompletedEvents); // Current-segment contact is consumed exactly once.

        HostRunResult<GraphSpatialState> reversed = await SimulationHost.RunUntilAsync(kernel, GraphTestWorld.Time(3));
        Assert.Equal(StepStatus.BoundaryReached, reversed.Status);
        Assert.Equal(1, reversed.CommittedTransitionCount);
        TraversingLocation segment = Traversal(reversed.World, Alice);
        Assert.Equal(9, segment.AnchorOffset); // Strictly inside (0, 11).
        Assert.Equal(GraphTestWorld.Time(3), segment.AnchorTime);
        Assert.Equal(GraphTestWorld.A, segment.TargetPlaceId);
        Assert.Equal(GraphTestWorld.Time(6), segment.ArrivalDue);
        Assert.Equal(2, Entity(reversed.World, Alice).MovementGeneration);
        Assert.Empty(reversed.World.ConsumedContacts); // Old-generation consumption is retired.
        Assert.Equal(new TraversalReversedFact(Alice, 1), Assert.Single(history.CompletedEvents[1].Facts));

        HostRunResult<GraphSpatialState> oldArrival = await SimulationHost.RunUntilAsync(kernel, GraphTestWorld.Time(4));
        Assert.Equal(0, oldArrival.CommittedTransitionCount);
        Assert.Equal(GraphTestWorld.Time(3), oldArrival.CurrentModelTime); // Invalidated arrival cannot advance time.
        HostRunResult<GraphSpatialState> arrived = await SimulationHost.RunUntilAsync(kernel, GraphTestWorld.Time(10));
        Assert.Equal(StepStatus.Exhausted, arrived.Status);
        Assert.Equal(2, arrived.CommittedTransitionCount);
        Assert.Equal(GraphTestWorld.Time(6), arrived.CurrentModelTime);
        Assert.Equal(new WorldVersion(1, 4), arrived.Version);
        Assert.All(arrived.World.Entities, entity =>
            Assert.Equal(GraphTestWorld.A, Assert.IsType<AtPlaceLocation>(entity.Location).PlaceId));
        Assert.Empty(arrived.World.ConsumedContacts);
        Assert.Equal(
            [GraphTestWorld.Instant(2), GraphTestWorld.Instant(3), GraphTestWorld.Instant(6), GraphTestWorld.Instant(6, 1)],
            history.CompletedEvents.Select(value => value.TargetInstant));
        TraversalArrivedFact[] arrivals = history.CompletedEvents.Skip(2)
            .Select(value => Assert.IsType<TraversalArrivedFact>(Assert.Single(value.Facts))).ToArray();
        Assert.Contains(new TraversalArrivedFact(Alice, 2), arrivals);
        Assert.Contains(new TraversalArrivedFact(Bob, 1), arrivals);

        ReplayResult<GraphSpatialState> replay = SimulationReplay.Replay(genesis, 1, ModelTime.Zero,
            history.CompletedEvents.Select(value => new JournalBatch<GraphSpatialFact>(
                value.TargetInstant, value.CauseKey, value.Facts)).ToArray(),
            reducer.Apply, state => GraphSpatialStateValidator.ValidateComplete(definition, state));
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
    private sealed class RuleAdapter<TData>(IOccurrenceRule<GraphSpatialState, TData, GraphSpatialFact> inner)
        : IOccurrenceRule<GraphSpatialState, object, GraphSpatialFact> where TData : notnull {
        public IReadOnlyList<OccurrenceCandidate<object>> Forecast(GraphSpatialState world, SimulationRules rules) =>
            inner.Forecast(world, rules).Select(candidate =>
                new OccurrenceCandidate<object>(candidate.Key, candidate.Due, candidate.Data)).ToArray();

        public ValueTask<TransitionDraft<GraphSpatialFact>> PlanSelectedAsync(GraphSpatialState world,
            OccurrenceCandidate<object> winner, CancellationToken cancellationToken) =>
            inner.PlanSelectedAsync(world, new OccurrenceCandidate<TData>(winner.Key, winner.Due,
                (TData)winner.Data), cancellationToken);
    }

    // A fixed scenario action, consumed by movement generation; no Player or long-term plan.
    private sealed class ReverseAtThreeRule(SpatialPlanner planner)
        : IOccurrenceRule<GraphSpatialState, object, GraphSpatialFact> {
        public IReadOnlyList<OccurrenceCandidate<object>> Forecast(GraphSpatialState world, SimulationRules rules) =>
            Entity(world, Alice).MovementGeneration == 1
                ? [new(new CandidateKey("scenario/reverse/alice/1"), new CandidateDue(GraphTestWorld.Time(3)), Alice)]
                : [];

        public ValueTask<TransitionDraft<GraphSpatialFact>> PlanSelectedAsync(GraphSpatialState world,
            OccurrenceCandidate<object> winner, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new TransitionDraft<GraphSpatialFact>(GraphTestWorld.Accepted(
                planner.TryReverseTraversal(world, (EntityId)winner.Data, winner.Due.ModelTime)).Facts));
        }
    }
}
