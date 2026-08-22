using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.FirstBoard.Tests;

public sealed class TravelGoalReducerTests
{
    private static readonly LogicalInstant Instant = new(ModelTime.Zero, 0);

    [Fact]
    public void Genesis_HasNoTravelGoal_AndIdleActorsRemainDecisionReady()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 201);
        FirstBoardWorld world = instance.CreateInitialWorld();

        Assert.All(world.Actors, actor => Assert.Null(actor.TravelGoalPlaceId));
        Assert.All(world.Actors, actor => Assert.True(world.IsReadyForDecision(actor)));
        new FirstBoardReducer(instance.Graph).Validate(world);
    }

    [Fact]
    public void GoalSet_CompletesOneDecision_AndResolvedOnlyAdvancesGeneration()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 202);
        var reducer = new FirstBoardReducer(instance.Graph);
        FirstBoardWorld initial = instance.CreateInitialWorld();
        BoardActor before = initial.Actor(BoardIds.Alice);

        FirstBoardWorld withGoal = reducer.Apply(
            initial,
            Instant,
            new GameBoardFact(new ActorTravelGoalSetEvent(
                BoardIds.Alice,
                new PlaceId(BoardIds.Cellar))));
        BoardActor active = withGoal.Actor(BoardIds.Alice);

        Assert.Equal(new PlaceId(BoardIds.Cellar), active.TravelGoalPlaceId);
        Assert.Equal(before.Generation + 1, active.Generation);
        Assert.Equal(before.DecisionSequence + 1, active.DecisionSequence);
        Assert.False(withGoal.IsReadyForDecision(active));
        reducer.Validate(withGoal);

        FirstBoardWorld completedPrefix = MoveAtPlace(
            instance,
            withGoal,
            BoardIds.Alice,
            BoardIds.Cellar);
        FirstBoardWorld completed = reducer.Apply(
            completedPrefix,
            Instant,
            new GameBoardFact(new ActorTravelGoalResolvedEvent(
                BoardIds.Alice,
                new PlaceId(BoardIds.Cellar),
                TravelGoalResolution.Completed)));
        BoardActor after = completed.Actor(BoardIds.Alice);

        Assert.Null(after.TravelGoalPlaceId);
        Assert.Equal(active.Generation + 1, after.Generation);
        Assert.Equal(active.DecisionSequence, after.DecisionSequence);
        Assert.True(completed.IsReadyForDecision(after));
        reducer.Validate(completed);
    }

    [Fact]
    public void BlockedResolution_ClearsMatchingGoalOnlyWhileShortOfDestination()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 203);
        var reducer = new FirstBoardReducer(instance.Graph);
        FirstBoardWorld withGoal = reducer.Apply(
            instance.CreateInitialWorld(),
            Instant,
            new GameBoardFact(new ActorTravelGoalSetEvent(
                BoardIds.Alice,
                new PlaceId(BoardIds.Cellar))));
        BoardActor active = withGoal.Actor(BoardIds.Alice);

        FirstBoardWorld blocked = reducer.Apply(
            withGoal,
            Instant,
            new GameBoardFact(new ActorTravelGoalResolvedEvent(
                BoardIds.Alice,
                new PlaceId(BoardIds.Cellar),
                TravelGoalResolution.Blocked)));

        Assert.Null(blocked.Actor(BoardIds.Alice).TravelGoalPlaceId);
        Assert.Equal(active.Generation + 1, blocked.Actor(BoardIds.Alice).Generation);
        Assert.Equal(active.DecisionSequence, blocked.Actor(BoardIds.Alice).DecisionSequence);
        Assert.Contains(
            "blocked from here",
            blocked.Actor(BoardIds.Alice).KnownFacts.Single(fact =>
                fact.Kind == BoardIds.LastActionOutcome).Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Reducer_RejectsInvalidGoalTransitions()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 204);
        var reducer = new FirstBoardReducer(instance.Graph);
        FirstBoardWorld initial = instance.CreateInitialWorld();

        Assert.Throws<InvalidOperationException>(() => reducer.Apply(
            initial,
            Instant,
            new GameBoardFact(new ActorTravelGoalSetEvent(
                BoardIds.Alice,
                new PlaceId(BoardIds.Tavern)))));
        Assert.Throws<InvalidOperationException>(() => reducer.Apply(
            initial,
            Instant,
            new GameBoardFact(new ActorTravelGoalSetEvent(
                BoardIds.Alice,
                new PlaceId("unknown")))));

        FirstBoardWorld withGoal = reducer.Apply(
            initial,
            Instant,
            new GameBoardFact(new ActorTravelGoalSetEvent(
                BoardIds.Alice,
                new PlaceId(BoardIds.Cellar))));
        Assert.Throws<InvalidOperationException>(() => reducer.Apply(
            withGoal,
            Instant,
            new GameBoardFact(new ActorTravelGoalResolvedEvent(
                BoardIds.Alice,
                new PlaceId(BoardIds.Market),
                TravelGoalResolution.Blocked))));
        Assert.Throws<InvalidOperationException>(() => reducer.Apply(
            withGoal,
            Instant,
            new GameBoardFact(new ActorTravelGoalResolvedEvent(
                BoardIds.Alice,
                new PlaceId(BoardIds.Cellar),
                TravelGoalResolution.Completed))));

        FirstBoardWorld atDestination = MoveAtPlace(
            instance,
            withGoal,
            BoardIds.Alice,
            BoardIds.Cellar);
        Assert.Throws<InvalidOperationException>(() => reducer.Apply(
            atDestination,
            Instant,
            new GameBoardFact(new ActorTravelGoalResolvedEvent(
                BoardIds.Alice,
                new PlaceId(BoardIds.Cellar),
                TravelGoalResolution.Blocked))));
    }

    [Fact]
    public void WorldValidator_RejectsWaitGoalOverlapAndUnknownDestination()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 205);
        var reducer = new FirstBoardReducer(instance.Graph);
        FirstBoardWorld initial = instance.CreateInitialWorld();

        FirstBoardWorld overlap = ReplaceActor(
            initial,
            initial.Actor(BoardIds.Alice) with
            {
                Activity = new BoardWaitActivity(new ModelTime(10)),
                TravelGoalPlaceId = new PlaceId(BoardIds.Cellar),
            });
        Assert.Throws<InvalidOperationException>(() => reducer.Validate(overlap));

        FirstBoardWorld unknown = ReplaceActor(
            initial,
            initial.Actor(BoardIds.Alice) with
            {
                TravelGoalPlaceId = new PlaceId("unknown"),
            });
        Assert.Throws<InvalidOperationException>(() => reducer.Validate(unknown));
    }

    private static FirstBoardWorld ReplaceActor(FirstBoardWorld world, BoardActor replacement) =>
        world with
        {
            Game = world.Game with
            {
                Actors = Array.AsReadOnly(world.Actors
                    .Select(actor => actor.Id == replacement.Id ? replacement : actor)
                    .ToArray()),
            },
        };

    private static FirstBoardWorld MoveAtPlace(
        ScenarioInstance instance,
        FirstBoardWorld world,
        string entityId,
        string placeId)
    {
        EntityPlacement[] placements =
        [
            .. world.Spatial.Entities.Select(entity => new EntityPlacement(
                entity.Id,
                entity.Id == new EntityId(entityId)
                    ? new PlaceId(placeId)
                    : Assert.IsType<AtPlaceLocation>(entity.Location).PlaceId)),
        ];
        return world with
        {
            Spatial = GraphSpatialState.Create(instance.Graph, placements),
        };
    }
}
