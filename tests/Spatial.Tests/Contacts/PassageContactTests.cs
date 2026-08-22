using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Spatial.Tests.TestSupport;

namespace DramaBoard.Spatial.Tests.Contacts;

public sealed class PassageContactTests
{
    private static readonly SimulationRules Rules = new(worldSeed: 123, maxTransitionsPerModelTime: 100);

    [Fact]
    public void ContactKey_CanonicalizesWholeEntityGenerationPairsAndRejectsInvalidIdentity()
    {
        var first = new PassageContactKey(
            GraphTestWorld.Bridge,
            new EntityId("z"),
            movementGenerationA: 7,
            new EntityId("a"),
            movementGenerationB: 3);
        var second = new PassageContactKey(
            GraphTestWorld.Bridge,
            new EntityId("a"),
            movementGenerationA: 3,
            new EntityId("z"),
            movementGenerationB: 7);

        Assert.Equal(second, first);
        Assert.Equal(0, first.CompareTo(second));
        Assert.Equal(new EntityId("a"), first.EntityA);
        Assert.Equal(3, first.MovementGenerationA);
        Assert.Equal(new EntityId("z"), first.EntityB);
        Assert.Equal(7, first.MovementGenerationB);
        Assert.Throws<ArgumentException>(() => new PassageContactKey(
            GraphTestWorld.Bridge,
            new EntityId("same"),
            1,
            new EntityId("same"),
            2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PassageContactKey(
            GraphTestWorld.Bridge,
            new EntityId("a"),
            -1,
            new EntityId("b"),
            1));
    }

    [Fact]
    public async Task HeadOnMeeting_UsesExactBigIntegerIntersectionAndConsumesOnlySelectedPair()
    {
        ContactContext context = CreateHeadOnContext();
        var rule = new SpatialContactOccurrenceRule(context.Definition);

        OccurrenceCandidate<PassageContactOccurrenceData> candidate =
            Assert.Single(rule.Forecast(context.State, Rules));
        Assert.Equal(GraphTestWorld.Time(2), candidate.Due.ModelTime);
        Assert.Equal(PassageContactKind.HeadOnMeeting, candidate.Data.Kind);
        Assert.Equal(new EntityId("alice"), candidate.Data.ContactKey.EntityA);
        Assert.Equal(new EntityId("bob"), candidate.Data.ContactKey.EntityB);

        TransitionDraft<GraphSpatialFact> draft = await rule.PlanSelectedAsync(
            context.State,
            candidate,
            CancellationToken.None);
        PassageContactOccurredFact fact =
            Assert.IsType<PassageContactOccurredFact>(Assert.Single(draft.Facts));
        GraphSpatialState next = context.Reducer.Apply(
            context.State,
            GraphTestWorld.Instant(2),
            fact);

        Assert.Equal(candidate.Data.ContactKey, Assert.Single(next.ConsumedContacts));
        Assert.Empty(rule.Forecast(next, Rules));
        Assert.Throws<InvalidOperationException>(() => context.Reducer.Apply(
            next,
            GraphTestWorld.Instant(2, 1),
            fact));
    }

    [Fact]
    public void Overtake_UsesTheLaterAnchorAsTheCommonMotionWindow()
    {
        GraphDefinition definition = GraphDefinition.Create(
            [GraphTestWorld.A, GraphTestWorld.B],
            [GraphTestWorld.Passage(GraphTestWorld.Bridge, GraphTestWorld.A, GraphTestWorld.B, length: 20)]);
        GraphSpatialState state = GraphTestWorld.State(
            definition,
            ("slow", GraphTestWorld.A),
            ("fast", GraphTestWorld.A));
        var planner = new SpatialPlanner(definition);
        var reducer = new GraphSpatialReducer(definition);
        state = Start(reducer, planner, state, "slow", GraphTestWorld.A, speed: 2, at: 0);
        state = Start(reducer, planner, state, "fast", GraphTestWorld.A, speed: 4, at: 2);

        OccurrenceCandidate<PassageContactOccurrenceData> candidate = Assert.Single(
            new SpatialContactOccurrenceRule(definition).Forecast(state, Rules));

        Assert.Equal(PassageContactKind.Overtake, candidate.Data.Kind);
        Assert.Equal(GraphTestWorld.Time(4), candidate.Due.ModelTime);
    }

    [Fact]
    public void Overtake_TowardEndpointA_NormalizesNegativeRelativeVelocity()
    {
        GraphDefinition definition = GraphDefinition.Create(
            [GraphTestWorld.A, GraphTestWorld.B],
            [GraphTestWorld.Passage(GraphTestWorld.Bridge, GraphTestWorld.A, GraphTestWorld.B, length: 10)]);
        GraphSpatialState state = GraphTestWorld.State(
            definition,
            ("slow", GraphTestWorld.B),
            ("fast", GraphTestWorld.B));
        var planner = new SpatialPlanner(definition);
        var reducer = new GraphSpatialReducer(definition);
        state = Start(reducer, planner, state, "slow", GraphTestWorld.B, speed: 2, at: 0);
        state = Start(reducer, planner, state, "fast", GraphTestWorld.B, speed: 4, at: 2);

        OccurrenceCandidate<PassageContactOccurrenceData> candidate = Assert.Single(
            new SpatialContactOccurrenceRule(definition).Forecast(state, Rules));

        Assert.Equal(PassageContactKind.Overtake, candidate.Data.Kind);
        Assert.Equal(GraphTestWorld.Time(4), candidate.Due.ModelTime);
    }

    [Fact]
    public void Forecast_CeilsNegativeAbsoluteRationalTimeMathematically()
    {
        GraphDefinition definition = GraphDefinition.Create(
            [GraphTestWorld.A, GraphTestWorld.B],
            [GraphTestWorld.Passage(GraphTestWorld.Bridge, GraphTestWorld.A, GraphTestWorld.B, length: 10)]);
        GraphSpatialState state = GraphTestWorld.State(
            definition,
            ("alice", GraphTestWorld.A),
            ("bob", GraphTestWorld.B));
        var planner = new SpatialPlanner(definition);
        var reducer = new GraphSpatialReducer(definition);
        state = Start(reducer, planner, state, "alice", GraphTestWorld.A, speed: 4, at: -5);
        state = Start(reducer, planner, state, "bob", GraphTestWorld.B, speed: 3, at: -5);

        OccurrenceCandidate<PassageContactOccurrenceData> candidate = Assert.Single(
            new SpatialContactOccurrenceRule(definition).Forecast(state, Rules));

        Assert.Equal(PassageContactKind.HeadOnMeeting, candidate.Data.Kind);
        Assert.Equal(GraphTestWorld.Time(-3), candidate.Due.ModelTime);
    }

    [Fact]
    public void Forecast_RejectsCoTravelTauZeroEndpointAndNoCommonPhysicalWindow()
    {
        AssertNoContact(
            length: 10,
            ("a", GraphTestWorld.A, 2, 0),
            ("b", GraphTestWorld.A, 2, 0));
        AssertNoContact(
            length: 10,
            ("ahead", GraphTestWorld.A, 2, 0),
            ("behind", GraphTestWorld.A, 2, 2));
        AssertNoContact(
            length: 10,
            ("a", GraphTestWorld.A, 2, 0),
            ("b", GraphTestWorld.A, 3, 0));
        AssertNoContact(
            length: 10,
            ("slow", GraphTestWorld.A, 1, 0),
            ("fast", GraphTestWorld.A, 2, 5));
        AssertNoContact(
            length: 10,
            ("exited", GraphTestWorld.A, 10, 0),
            ("late", GraphTestWorld.B, 1, 1));
    }

    [Fact]
    public void Forecast_HandlesProductsBeyondInt64WithoutLeakingExactFraction()
    {
        GraphDefinition definition = GraphDefinition.Create(
            [GraphTestWorld.A, GraphTestWorld.B],
            [GraphTestWorld.Passage(
                GraphTestWorld.Bridge,
                GraphTestWorld.A,
                GraphTestWorld.B,
                length: long.MaxValue)]);
        GraphSpatialState state = GraphTestWorld.State(
            definition,
            ("a", GraphTestWorld.A),
            ("b", GraphTestWorld.B));
        var planner = new SpatialPlanner(definition);
        var reducer = new GraphSpatialReducer(definition);
        state = Start(reducer, planner, state, "a", GraphTestWorld.A, long.MaxValue, 0);
        state = Start(reducer, planner, state, "b", GraphTestWorld.B, long.MaxValue - 1, 0);

        OccurrenceCandidate<PassageContactOccurrenceData> candidate = Assert.Single(
            new SpatialContactOccurrenceRule(definition).Forecast(state, Rules));

        Assert.Equal(GraphTestWorld.Time(1), candidate.Due.ModelTime);
        Assert.Equal(PassageContactKind.HeadOnMeeting, candidate.Data.Kind);
    }

    [Fact]
    public async Task SelectedPlanAndReducer_RejectTamperedKeyDueKindAndGeneration()
    {
        ContactContext context = CreateHeadOnContext();
        var rule = new SpatialContactOccurrenceRule(context.Definition);
        OccurrenceCandidate<PassageContactOccurrenceData> candidate =
            Assert.Single(rule.Forecast(context.State, Rules));
        PassageContactKey key = candidate.Data.ContactKey;
        PassageContactKind wrongKind = PassageContactKind.Overtake;
        var staleKey = new PassageContactKey(
            key.PassageId,
            key.EntityA,
            key.MovementGenerationA + 1,
            key.EntityB,
            key.MovementGenerationB);

        await AssertPlanRejected(rule, context.State, new OccurrenceCandidate<PassageContactOccurrenceData>(
            CandidateKey.FromUtf8("tampered"),
            candidate.Due,
            candidate.Data));
        await AssertPlanRejected(rule, context.State, new OccurrenceCandidate<PassageContactOccurrenceData>(
            candidate.Key,
            new CandidateDue(GraphTestWorld.Time(3)),
            candidate.Data));
        await AssertPlanRejected(rule, context.State, new OccurrenceCandidate<PassageContactOccurrenceData>(
            candidate.Key,
            candidate.Due,
            new PassageContactOccurrenceData(key, wrongKind)));
        await AssertPlanRejected(rule, context.State, new OccurrenceCandidate<PassageContactOccurrenceData>(
            candidate.Key,
            candidate.Due,
            new PassageContactOccurrenceData(staleKey, candidate.Data.Kind)));

        Assert.Throws<InvalidOperationException>(() => context.Reducer.Apply(
            context.State,
            GraphTestWorld.Instant(3),
            new PassageContactOccurredFact(key, candidate.Data.Kind)));
        Assert.Throws<InvalidOperationException>(() => context.Reducer.Apply(
            context.State,
            GraphTestWorld.Instant(2),
            new PassageContactOccurredFact(key, wrongKind)));
        Assert.Throws<InvalidOperationException>(() => context.Reducer.Apply(
            context.State,
            GraphTestWorld.Instant(2),
            new PassageContactOccurredFact(staleKey, candidate.Data.Kind)));
    }

    [Fact]
    public async Task Consumption_IsPairLocalAndRemoveClearsOnlyTheReplacedSegmentKeys()
    {
        var secondPassage = new PassageId("cd");
        GraphDefinition definition = GraphDefinition.Create(
            [GraphTestWorld.A, GraphTestWorld.B, GraphTestWorld.C, GraphTestWorld.D],
            [
                GraphTestWorld.Passage(GraphTestWorld.Bridge, GraphTestWorld.A, GraphTestWorld.B),
                GraphTestWorld.Passage(secondPassage, GraphTestWorld.C, GraphTestWorld.D),
            ]);
        GraphSpatialState state = GraphTestWorld.State(
            definition,
            ("a", GraphTestWorld.A),
            ("b", GraphTestWorld.B),
            ("c", GraphTestWorld.C),
            ("d", GraphTestWorld.D));
        var planner = new SpatialPlanner(definition);
        var reducer = new GraphSpatialReducer(definition);
        state = Start(reducer, planner, state, "a", GraphTestWorld.A, 4, 0, GraphTestWorld.Bridge);
        state = Start(reducer, planner, state, "b", GraphTestWorld.B, 3, 0, GraphTestWorld.Bridge);
        state = Start(reducer, planner, state, "c", GraphTestWorld.C, 4, 0, secondPassage);
        state = Start(reducer, planner, state, "d", GraphTestWorld.D, 3, 0, secondPassage);
        var rule = new SpatialContactOccurrenceRule(definition);

        OccurrenceCandidate<PassageContactOccurrenceData>[] candidates =
            [.. rule.Forecast(state, Rules)];
        Assert.Equal(2, candidates.Length);
        foreach (OccurrenceCandidate<PassageContactOccurrenceData> candidate in candidates)
        {
            TransitionDraft<GraphSpatialFact> draft = await rule.PlanSelectedAsync(
                state,
                candidate,
                CancellationToken.None);
            state = reducer.Apply(state, GraphTestWorld.Instant(2), Assert.Single(draft.Facts));
        }

        Assert.Equal(2, state.ConsumedContacts.Count);
        state = reducer.Apply(state, GraphTestWorld.Instant(2, 1), new EntityRemovedFact(new EntityId("a")));

        PassageContactKey remaining = Assert.Single(state.ConsumedContacts);
        Assert.Equal(secondPassage, remaining.PassageId);
        Assert.Equal(new EntityId("c"), remaining.EntityA);
        Assert.Equal(new EntityId("d"), remaining.EntityB);
    }

    [Fact]
    public async Task ArrivalAndReverse_EachClearConsumedKeysForTheOldGeneration()
    {
        ContactContext arrivalContext = CreateHeadOnContext();
        var arrivalRule = new SpatialContactOccurrenceRule(arrivalContext.Definition);
        OccurrenceCandidate<PassageContactOccurrenceData> arrivalContact =
            Assert.Single(arrivalRule.Forecast(arrivalContext.State, Rules));
        GraphSpatialState arrivalState = arrivalContext.Reducer.Apply(
            arrivalContext.State,
            GraphTestWorld.Instant(2),
            Assert.Single((await arrivalRule.PlanSelectedAsync(
                arrivalContext.State,
                arrivalContact,
                CancellationToken.None)).Facts));
        arrivalState = arrivalContext.Reducer.Apply(
            arrivalState,
            GraphTestWorld.Instant(3),
            new TraversalArrivedFact(new EntityId("alice"), ExpectedMovementGeneration: 1));
        Assert.Empty(arrivalState.ConsumedContacts);

        ContactContext reverseContext = CreateHeadOnContext();
        var reverseRule = new SpatialContactOccurrenceRule(reverseContext.Definition);
        OccurrenceCandidate<PassageContactOccurrenceData> reverseContact =
            Assert.Single(reverseRule.Forecast(reverseContext.State, Rules));
        GraphSpatialState reverseState = reverseContext.Reducer.Apply(
            reverseContext.State,
            GraphTestWorld.Instant(2),
            Assert.Single((await reverseRule.PlanSelectedAsync(
                reverseContext.State,
                reverseContact,
                CancellationToken.None)).Facts));
        reverseState = GraphTestWorld.Fold(
            reverseContext.Reducer,
            reverseState,
            GraphTestWorld.Instant(2, 1),
            new SpatialPlanner(reverseContext.Definition).TryReverseTraversal(
                reverseState,
                new EntityId("alice"),
                GraphTestWorld.Time(2)));
        Assert.Empty(reverseState.ConsumedContacts);
        Assert.Equal(2, reverseState.Entities.Single(entity => entity.Id == new EntityId("alice")).MovementGeneration);
    }

    [Fact]
    public void Forecast_IsStableAcrossDefinitionEntityAndStartEnumerationOrderAndKeepsDueNow()
    {
        (GraphDefinition firstDefinition, GraphSpatialState firstState) =
            CreatePermutationContext(reverseInputs: false);
        (GraphDefinition secondDefinition, GraphSpatialState secondState) =
            CreatePermutationContext(reverseInputs: true);
        var firstRule = new SpatialContactOccurrenceRule(firstDefinition);
        var secondRule = new SpatialContactOccurrenceRule(secondDefinition);

        IReadOnlyList<OccurrenceCandidate<PassageContactOccurrenceData>> first =
            firstRule.Forecast(firstState, Rules);
        IReadOnlyList<OccurrenceCandidate<PassageContactOccurrenceData>> second =
            secondRule.Forecast(secondState, Rules);
        Assert.Equal(first.Select(value => value.Key), second.Select(value => value.Key));
        Assert.Equal(first.Select(value => value.Due), second.Select(value => value.Due));
        Assert.Equal(first.Select(value => value.Data), second.Select(value => value.Data));

        var reducer = new GraphSpatialReducer(firstDefinition);
        GraphSpatialState sameTickPrefix = reducer.Apply(
            firstState,
            GraphTestWorld.Instant(2),
            new PassageEntryAccessChangedFact(
                GraphTestWorld.Bridge,
                new PassageEntryAccess(false, true)));
        OccurrenceCandidate<PassageContactOccurrenceData> dueNow =
            Assert.Single(
                firstRule.Forecast(sameTickPrefix, Rules),
                candidate => candidate.Data.ContactKey.PassageId == GraphTestWorld.Bridge);
        Assert.Equal(GraphTestWorld.Time(2), dueNow.Due.ModelTime);
    }

    private static async Task AssertPlanRejected(
        SpatialContactOccurrenceRule rule,
        GraphSpatialState state,
        OccurrenceCandidate<PassageContactOccurrenceData> candidate) =>
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rule.PlanSelectedAsync(state, candidate, CancellationToken.None));

    private static void AssertNoContact(
        long length,
        params (string Entity, PlaceId Place, long Speed, long At)[] segments)
    {
        GraphDefinition definition = GraphDefinition.Create(
            [GraphTestWorld.A, GraphTestWorld.B],
            [GraphTestWorld.Passage(GraphTestWorld.Bridge, GraphTestWorld.A, GraphTestWorld.B, length)]);
        GraphSpatialState state = GraphTestWorld.State(
            definition,
            segments.Select(segment => (segment.Entity, segment.Place)).ToArray());
        var planner = new SpatialPlanner(definition);
        var reducer = new GraphSpatialReducer(definition);
        foreach ((string entity, PlaceId place, long speed, long at) in segments)
        {
            state = Start(reducer, planner, state, entity, place, speed, at);
        }

        Assert.Empty(new SpatialContactOccurrenceRule(definition).Forecast(state, Rules));
    }

    private static ContactContext CreateHeadOnContext()
    {
        GraphDefinition definition = GraphDefinition.Create(
            [GraphTestWorld.A, GraphTestWorld.B],
            [GraphTestWorld.Passage(GraphTestWorld.Bridge, GraphTestWorld.A, GraphTestWorld.B, length: 10)]);
        GraphSpatialState state = GraphTestWorld.State(
            definition,
            ("alice", GraphTestWorld.A),
            ("bob", GraphTestWorld.B));
        var planner = new SpatialPlanner(definition);
        var reducer = new GraphSpatialReducer(definition);
        state = Start(reducer, planner, state, "alice", GraphTestWorld.A, speed: 4, at: 0);
        state = Start(reducer, planner, state, "bob", GraphTestWorld.B, speed: 3, at: 0);
        return new ContactContext(definition, state, reducer);
    }

    private static (GraphDefinition Definition, GraphSpatialState State) CreatePermutationContext(bool reverseInputs)
    {
        var secondPassage = new PassageId("cd");
        PlaceId[] places = [GraphTestWorld.A, GraphTestWorld.B, GraphTestWorld.C, GraphTestWorld.D];
        PassageDefinition[] passages =
        [
            GraphTestWorld.Passage(GraphTestWorld.Bridge, GraphTestWorld.A, GraphTestWorld.B, length: 10),
            GraphTestWorld.Passage(secondPassage, GraphTestWorld.C, GraphTestWorld.D, length: 10),
        ];
        EntityPlacement[] placements =
        [
            new(new EntityId("alice"), GraphTestWorld.A),
            new(new EntityId("bob"), GraphTestWorld.B),
            new(new EntityId("carol"), GraphTestWorld.C),
            new(new EntityId("dave"), GraphTestWorld.D),
        ];
        if (reverseInputs)
        {
            Array.Reverse(places);
            Array.Reverse(passages);
            Array.Reverse(placements);
        }

        GraphDefinition definition = GraphDefinition.Create(places, passages);
        GraphSpatialState state = GraphSpatialState.Create(definition, placements);
        var planner = new SpatialPlanner(definition);
        var reducer = new GraphSpatialReducer(definition);
        string[] starts = reverseInputs
            ? ["dave", "carol", "bob", "alice"]
            : ["alice", "bob", "carol", "dave"];
        foreach (string entity in starts)
        {
            bool fromA = entity is "alice" or "carol";
            PlaceId place = entity switch
            {
                "alice" => GraphTestWorld.A,
                "bob" => GraphTestWorld.B,
                "carol" => GraphTestWorld.C,
                _ => GraphTestWorld.D,
            };
            PassageId passageId = entity is "alice" or "bob"
                ? GraphTestWorld.Bridge
                : secondPassage;
            state = Start(reducer, planner, state, entity, place, fromA ? 4 : 3, 0, passageId);
        }

        return (definition, state);
    }

    private static GraphSpatialState Start(
        GraphSpatialReducer reducer,
        SpatialPlanner planner,
        GraphSpatialState state,
        string entity,
        PlaceId place,
        long speed,
        long at,
        PassageId? passageId = null) =>
        GraphTestWorld.Fold(
            reducer,
            state,
            GraphTestWorld.Instant(at),
            planner.TryStartTraversal(
                state,
                new EntityId(entity),
                passageId ?? GraphTestWorld.Bridge,
                speed,
                GraphTestWorld.Time(at)));

    private sealed record ContactContext(
        GraphDefinition Definition,
        GraphSpatialState State,
        GraphSpatialReducer Reducer);
}
