using DramaBoard.Spatial.Tests.TestSupport;

namespace DramaBoard.Spatial.Tests.State;

public sealed class GraphSpatialStateTests
{
    [Fact]
    public void Create_CanonicalizesEntitiesAtKnownPlacesWithZeroMovementGeneration()
    {
        GraphDefinition definition = GraphTestWorld.Definition(
            places: [GraphTestWorld.A, GraphTestWorld.B],
            passages: []);

        GraphSpatialState state = GraphTestWorld.State(
            definition,
            ("z", GraphTestWorld.B),
            ("a", GraphTestWorld.A));

        Assert.Equal([new EntityId("a"), new EntityId("z")], state.Entities.Select(value => value.Id));
        Assert.All(state.Entities, value => Assert.Equal(0, value.MovementGeneration));
        Assert.Equal(GraphTestWorld.A, Assert.IsType<AtPlaceLocation>(state.Entities[0].Location).PlaceId);
        Assert.Empty(state.PassageEntryAccessOverrides);
        Assert.Empty(state.ScheduledPassageEntryChanges);
        Assert.Empty(state.ConsumedContacts);
    }

    [Fact]
    public void Create_RejectsDuplicateEntityAndUnknownPlace()
    {
        GraphDefinition definition = GraphTestWorld.Definition(
            places: [GraphTestWorld.A, GraphTestWorld.B],
            passages: []);

        Assert.Throws<InvalidOperationException>(() => GraphTestWorld.State(
            definition,
            ("actor", GraphTestWorld.A),
            ("actor", GraphTestWorld.B)));
        Assert.Throws<ArgumentException>(() => GraphTestWorld.State(
            definition,
            ("actor", GraphTestWorld.C)));
    }

    [Fact]
    public void LocationValues_RejectImpossibleSnapshots()
    {
        Assert.Throws<ArgumentException>(() => new AtPlaceLocation(default));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpatialEntity(
            new EntityId("actor"),
            movementGeneration: -1,
            new AtPlaceLocation(GraphTestWorld.A)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TraversingLocation(
            GraphTestWorld.Bridge,
            anchorOffset: 0,
            GraphTestWorld.Time(10),
            GraphTestWorld.B,
            speedSnapshot: 0,
            GraphTestWorld.Time(11)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TraversingLocation(
            GraphTestWorld.Bridge,
            anchorOffset: 0,
            GraphTestWorld.Time(10),
            GraphTestWorld.B,
            speedSnapshot: 1,
            GraphTestWorld.Time(10)));
    }

    [Fact]
    public void Validator_RejectsMalformedAnchoredTraversalSnapshots()
    {
        GraphDefinition definition = GraphDefinition.Create(
            [GraphTestWorld.A, GraphTestWorld.B, GraphTestWorld.C],
            [GraphTestWorld.Passage(GraphTestWorld.Bridge, GraphTestWorld.A, GraphTestWorld.B, length: 10)]);
        GraphSpatialState state = GraphTestWorld.State(definition, ("actor", GraphTestWorld.A));
        var actor = new EntityId("actor");

        AssertInvalidTraversal(new TraversingLocation(
            GraphTestWorld.Bridge,
            anchorOffset: 11,
            GraphTestWorld.Time(0),
            GraphTestWorld.A,
            speedSnapshot: 1,
            GraphTestWorld.Time(11)));
        AssertInvalidTraversal(new TraversingLocation(
            GraphTestWorld.Bridge,
            anchorOffset: 0,
            GraphTestWorld.Time(0),
            GraphTestWorld.C,
            speedSnapshot: 1,
            GraphTestWorld.Time(10)));
        AssertInvalidTraversal(new TraversingLocation(
            GraphTestWorld.Bridge,
            anchorOffset: 0,
            GraphTestWorld.Time(0),
            GraphTestWorld.B,
            speedSnapshot: 3,
            GraphTestWorld.Time(3)));

        void AssertInvalidTraversal(TraversingLocation traversal)
        {
            GraphSpatialState malformed = state.Rebuild(
                entities: [new SpatialEntity(actor, movementGeneration: 1, traversal)]);
            Assert.Throws<InvalidOperationException>(() =>
                GraphSpatialStateValidator.ValidateComplete(definition, malformed));
        }
    }

    [Fact]
    public void Restore_SnapshotsAndCanonicalizesACompleteNontrivialState()
    {
        GraphDefinition definition = GraphDefinition.Create(
            [GraphTestWorld.A, GraphTestWorld.B],
            [GraphTestWorld.Passage(
                GraphTestWorld.Bridge,
                GraphTestWorld.A,
                GraphTestWorld.B,
                length: 10)]);
        var reducer = new GraphSpatialReducer(definition);
        GraphSpatialState state = GraphTestWorld.State(
            definition,
            ("alice", GraphTestWorld.A),
            ("bob", GraphTestWorld.B));
        state = reducer.Apply(
            state,
            GraphTestWorld.Instant(0),
            new TraversalStartedFact(
                new EntityId("alice"),
                GraphTestWorld.Bridge,
                GraphTestWorld.A,
                SpeedSnapshot: 4));
        state = reducer.Apply(
            state,
            GraphTestWorld.Instant(0, 1),
            new TraversalStartedFact(
                new EntityId("bob"),
                GraphTestWorld.Bridge,
                GraphTestWorld.B,
                SpeedSnapshot: 3));
        state = reducer.Apply(
            state,
            GraphTestWorld.Instant(0, 2),
            new PassageEntryAccessChangedFact(
                GraphTestWorld.Bridge,
                new PassageEntryAccess(false, true)));
        state = reducer.Apply(
            state,
            GraphTestWorld.Instant(0, 3),
            new PassageEntryChangeScheduledFact(
                GraphTestWorld.Bridge,
                GraphTestWorld.Time(8),
                new PassageEntryPatch(enterableFromA: null, enterableFromB: false)));
        var contact = new PassageContactKey(
            GraphTestWorld.Bridge,
            new EntityId("alice"),
            movementGenerationA: 1,
            new EntityId("bob"),
            movementGenerationB: 1);
        state = reducer.Apply(
            state,
            GraphTestWorld.Instant(1),
            new PassageContactOccurredFact(contact, PassageContactKind.HeadOnMeeting));

        SpatialEntity[] entities = [.. state.Entities.Reverse()];
        PassageEntryAccessOverride[] overrides =
            [.. state.PassageEntryAccessOverrides.Reverse()];
        ScheduledPassageEntryChange[] schedules =
            [.. state.ScheduledPassageEntryChanges.Reverse()];
        PassageContactKey[] contacts = [.. state.ConsumedContacts.Reverse()];

        GraphSpatialState restored = GraphSpatialState.Restore(
            definition,
            entities,
            overrides,
            schedules,
            contacts);

        Assert.Equal(state, restored);
        Assert.Equal(
            [new EntityId("alice"), new EntityId("bob")],
            restored.Entities.Select(entity => entity.Id));
        Assert.IsType<TraversingLocation>(restored.Entities[0].Location);
        Assert.Single(restored.PassageEntryAccessOverrides);
        Assert.Single(restored.ScheduledPassageEntryChanges);
        Assert.Equal(contact, Assert.Single(restored.ConsumedContacts));

        entities[0] = new SpatialEntity(
            new EntityId("replacement"),
            movementGeneration: 0,
            new AtPlaceLocation(GraphTestWorld.A));
        overrides[0] = new PassageEntryAccessOverride(
            GraphTestWorld.Bridge,
            new PassageEntryAccess(true, false));
        schedules[0] = new ScheduledPassageEntryChange(
            GraphTestWorld.Bridge,
            GraphTestWorld.Time(9),
            new PassageEntryPatch(enterableFromA: false, enterableFromB: null));
        contacts[0] = new PassageContactKey(
            GraphTestWorld.Bridge,
            new EntityId("alice"),
            movementGenerationA: 0,
            new EntityId("bob"),
            movementGenerationB: 0);

        Assert.Equal(state, restored);
    }

    [Fact]
    public void Restore_RejectsAConsumedContactForAStaleMovementSegment()
    {
        GraphDefinition definition = GraphDefinition.Create(
            [GraphTestWorld.A, GraphTestWorld.B],
            [GraphTestWorld.Passage(
                GraphTestWorld.Bridge,
                GraphTestWorld.A,
                GraphTestWorld.B,
                length: 10)]);
        SpatialEntity[] entities =
        [
            new(
                new EntityId("alice"),
                movementGeneration: 1,
                new TraversingLocation(
                    GraphTestWorld.Bridge,
                    anchorOffset: 0,
                    GraphTestWorld.Time(0),
                    GraphTestWorld.B,
                    speedSnapshot: 1,
                    GraphTestWorld.Time(10))),
            new(
                new EntityId("bob"),
                movementGeneration: 1,
                new TraversingLocation(
                    GraphTestWorld.Bridge,
                    anchorOffset: 10,
                    GraphTestWorld.Time(0),
                    GraphTestWorld.A,
                    speedSnapshot: 1,
                    GraphTestWorld.Time(10))),
        ];
        var staleContact = new PassageContactKey(
            GraphTestWorld.Bridge,
            new EntityId("alice"),
            movementGenerationA: 0,
            new EntityId("bob"),
            movementGenerationB: 1);

        Assert.Throws<InvalidOperationException>(() => GraphSpatialState.Restore(
            definition,
            entities,
            passageEntryAccessOverrides: [],
            scheduledPassageEntryChanges: [],
            consumedContacts: [staleContact]));
    }
}
