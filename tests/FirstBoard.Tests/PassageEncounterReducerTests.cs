using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Player;
using DramaBoard.Player.Agency.Spatial;
using DramaBoard.Spatial;

namespace DramaBoard.FirstBoard.Tests;

public sealed class PassageEncounterReducerTests
{
    [Fact]
    public void Genesis_HasNoPendingEncounter()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 301);
        FirstBoardWorld world = instance.CreateInitialWorld();

        Assert.Null(world.Game.PendingEncounter);
        new FirstBoardReducer(instance.Graph).Validate(world);
    }

    [Fact]
    public void Opened_RequiresEarlierConsumedSpatialContact_AndSuppressesBothParticipants()
    {
        EncounterContext context = CreateOpenedEncounter(withAliceGoal: false);
        FirstBoardWorld beforeContact = CreateTravelingPrefix(context.Instance, withAliceGoal: false);

        Assert.Throws<InvalidOperationException>(() => context.Reducer.Apply(
            beforeContact,
            context.ContactInstant,
            new GameBoardFact(new PassageEncounterOpenedEvent(
                context.ContactKey,
                PassageContactKind.HeadOnMeeting))));

        Assert.Equal(
            new PendingPassageEncounter(
                context.ContactKey,
                PassageContactKind.HeadOnMeeting),
            context.World.Game.PendingEncounter);
        Assert.True(context.World.IsPendingEncounterParticipant(context.World.Actor(BoardIds.Alice)));
        Assert.True(context.World.IsPendingEncounterParticipant(context.World.Actor(BoardIds.Bob)));
        Assert.False(context.World.IsReadyForDecision(context.World.Actor(BoardIds.Alice)));
        Assert.False(context.World.IsReadyForDecision(context.World.Actor(BoardIds.Bob)));
        context.Reducer.Validate(context.World);
    }

    [Fact]
    public void Continued_ClearsPendingAndAdvancesOnlyResponderDecision_WhileKeepingGoalAndMotion()
    {
        EncounterContext context = CreateOpenedEncounter(withAliceGoal: true);
        BoardActor aliceBefore = context.World.Actor(BoardIds.Alice);
        BoardActor bobBefore = context.World.Actor(BoardIds.Bob);
        SpatialEntity aliceSpatialBefore = SpatialEntity(context.World, BoardIds.Alice);

        FirstBoardWorld resolved = context.Reducer.Apply(
            context.World,
            NextInstant(context.ContactInstant),
            new GameBoardFact(new PassageEncounterResolvedEvent(
                context.ContactKey,
                BoardIds.Alice,
                PassageEncounterResolution.Continued)));

        BoardActor aliceAfter = resolved.Actor(BoardIds.Alice);
        Assert.Null(resolved.Game.PendingEncounter);
        Assert.Equal(aliceBefore.Generation + 1, aliceAfter.Generation);
        Assert.Equal(aliceBefore.DecisionSequence + 1, aliceAfter.DecisionSequence);
        Assert.Equal(aliceBefore.TravelGoalPlaceId, aliceAfter.TravelGoalPlaceId);
        Assert.Equal(bobBefore, resolved.Actor(BoardIds.Bob));
        Assert.Equal(aliceSpatialBefore, SpatialEntity(resolved, BoardIds.Alice));
        context.Reducer.Validate(resolved);
    }

    [Fact]
    public void Reversed_ClearsGoalRecordsOutcomeAndAdvancesOnlyResponderDecision()
    {
        EncounterContext context = CreateOpenedEncounter(withAliceGoal: true);
        BoardActor aliceBefore = context.World.Actor(BoardIds.Alice);
        BoardActor bobBefore = context.World.Actor(BoardIds.Bob);

        FirstBoardWorld resolved = context.Reducer.Apply(
            context.World,
            NextInstant(context.ContactInstant),
            new GameBoardFact(new PassageEncounterResolvedEvent(
                context.ContactKey,
                BoardIds.Alice,
                PassageEncounterResolution.Reversed)));

        BoardActor aliceAfter = resolved.Actor(BoardIds.Alice);
        Assert.Null(resolved.Game.PendingEncounter);
        Assert.Equal(aliceBefore.Generation + 1, aliceAfter.Generation);
        Assert.Equal(aliceBefore.DecisionSequence + 1, aliceAfter.DecisionSequence);
        Assert.Null(aliceAfter.TravelGoalPlaceId);
        Assert.Contains(
            "interrupted",
            aliceAfter.KnownFacts.Single(fact => fact.Kind == BoardIds.LastActionOutcome).Text,
            StringComparison.Ordinal);
        Assert.Equal(bobBefore, resolved.Actor(BoardIds.Bob));
        context.Reducer.Validate(resolved);
    }

    [Fact]
    public void WorldChanged_AcceptsStalePendingAfterArrival_AndOnlyClearsPending()
    {
        EncounterContext context = CreateOpenedEncounter(withAliceGoal: true);
        SpatialEntity aliceSpatial = SpatialEntity(context.World, BoardIds.Alice);
        TraversingLocation traversal = Assert.IsType<TraversingLocation>(aliceSpatial.Location);
        FirstBoardWorld stale = context.Reducer.Apply(
            context.World,
            new LogicalInstant(traversal.ArrivalDue, 0),
            new SpatialBoardFact(new TraversalArrivedFact(
                new EntityId(BoardIds.Alice),
                aliceSpatial.MovementGeneration)));

        Assert.NotNull(stale.Game.PendingEncounter);
        Assert.DoesNotContain(context.ContactKey, stale.Spatial.ConsumedContacts);
        context.Reducer.Validate(stale);
        FirstBoardGameState gameBeforeCleanup = stale.Game;

        FirstBoardWorld cleaned = context.Reducer.Apply(
            stale,
            new LogicalInstant(traversal.ArrivalDue, 1),
            new GameBoardFact(new PassageEncounterResolvedEvent(
                context.ContactKey,
                RespondingActorId: null,
                PassageEncounterResolution.WorldChanged)));

        Assert.Null(cleaned.Game.PendingEncounter);
        Assert.Equal(gameBeforeCleanup.Actors, cleaned.Game.Actors);
        Assert.Equal(gameBeforeCleanup.Objects, cleaned.Game.Objects);
        Assert.Equal(gameBeforeCleanup.NextPersistentId, cleaned.Game.NextPersistentId);
        Assert.Equal(gameBeforeCleanup.CellarSealed, cleaned.Game.CellarSealed);
        Assert.Equal(gameBeforeCleanup.ChestOpened, cleaned.Game.ChestOpened);
        context.Reducer.Validate(cleaned);
    }

    [Fact]
    public void Resolved_RejectsWrongKeyResponderAndResolutionShape()
    {
        EncounterContext context = CreateOpenedEncounter(withAliceGoal: true);
        var wrongKey = new PassageContactKey(
            context.ContactKey.PassageId,
            context.ContactKey.EntityA,
            context.ContactKey.MovementGenerationA + 1,
            context.ContactKey.EntityB,
            context.ContactKey.MovementGenerationB);

        AssertResolutionRejected(context, wrongKey, BoardIds.Alice, PassageEncounterResolution.Continued);
        AssertResolutionRejected(context, context.ContactKey, "mallory", PassageEncounterResolution.Reversed);
        AssertResolutionRejected(context, context.ContactKey, null, PassageEncounterResolution.Continued);
        AssertResolutionRejected(context, context.ContactKey, BoardIds.Alice, PassageEncounterResolution.WorldChanged);
        AssertResolutionRejected(
            context,
            context.ContactKey,
            BoardIds.Alice,
            (PassageEncounterResolution)999);
    }

    [Fact]
    public void Forecasts_UseDriverRegistryAndSuppressOnlyPendingParticipants()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 307);
        var rules = new SimulationRules(instance.WorldSeed, maxTransitionsPerModelTime: 100);
        FirstBoardWorld genesis = instance.CreateInitialWorld();
        var aliceOnly = new Dictionary<string, IPlayerDriver>(StringComparer.Ordinal)
        {
            [BoardIds.Alice] = new RecordingPlayerDriver(),
        };
        var decisionRule = new DecisionPointRule(aliceOnly, instance);

        DecisionPointCandidate onlyAlice = Assert.IsType<DecisionPointCandidate>(
            Assert.Single(decisionRule.Forecast(genesis, rules)).Data);
        Assert.Equal(genesis.Actor(BoardIds.Alice).Id, onlyAlice.ActorId);

        FirstBoardWorld withCharlie = AddUnrelatedActorWithPendingEncounter(instance, genesis);
        var allDrivers = new Dictionary<string, IPlayerDriver>(StringComparer.Ordinal)
        {
            [BoardIds.Alice] = new RecordingPlayerDriver(),
            [BoardIds.Bob] = new RecordingPlayerDriver(),
            ["charlie"] = new RecordingPlayerDriver(),
        };
        var pendingDecisionRule = new DecisionPointRule(allDrivers, instance);
        DecisionPointCandidate onlyCharlie = Assert.IsType<DecisionPointCandidate>(
            Assert.Single(pendingDecisionRule.Forecast(withCharlie, rules)).Data);
        Assert.Equal(withCharlie.Actor("charlie").Id, onlyCharlie.ActorId);

        FirstBoardWorld withGoals = withCharlie with
        {
            Game = withCharlie.Game with
            {
                Actors = Array.AsReadOnly(withCharlie.Actors.Select(actor => actor with
                {
                    TravelGoalPlaceId = actor.Key is BoardIds.Alice or "charlie"
                        ? new PlaceId(BoardIds.Market)
                        : null,
                }).ToArray()),
            },
        };
        var travelGoalRule = new TravelGoalRule(
            instance,
            FullMapPlayerSpatialKnowledgeGetter<FirstBoardWorld>.Instance);
        TravelGoalCandidate onlyCharlieGoal = Assert.IsType<TravelGoalCandidate>(
            Assert.Single(travelGoalRule.Forecast(withGoals, rules)).Data);
        Assert.Equal(withGoals.Actor("charlie").Id, onlyCharlieGoal.ActorId);
        new FirstBoardReducer(instance.Graph).Validate(withGoals);
    }

    [Fact]
    public void WorldValidator_RequiresPendingParticipantsToBeBoardActorsButNotCurrentSegments()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 308);
        var reducer = new FirstBoardReducer(instance.Graph);
        FirstBoardWorld genesis = instance.CreateInitialWorld();
        var staleKey = new PassageContactKey(
            new PassageId(BoardIds.TavernMarketRoad),
            new EntityId(BoardIds.Alice),
            movementGenerationA: 42,
            new EntityId(BoardIds.Bob),
            movementGenerationB: 91);
        FirstBoardWorld legalStale = genesis with
        {
            Game = genesis.Game with
            {
                PendingEncounter = new PendingPassageEncounter(
                    staleKey,
                    PassageContactKind.HeadOnMeeting),
            },
        };
        reducer.Validate(legalStale);

        var nonActorKey = new PassageContactKey(
            new PassageId(BoardIds.TavernMarketRoad),
            new EntityId(BoardIds.Alice),
            movementGenerationA: 0,
            new EntityId(BoardIds.BrassKey),
            movementGenerationB: 0);
        FirstBoardWorld invalid = genesis with
        {
            Game = genesis.Game with
            {
                PendingEncounter = new PendingPassageEncounter(
                    nonActorKey,
                    PassageContactKind.HeadOnMeeting),
            },
        };
        Assert.Throws<InvalidOperationException>(() => reducer.Validate(invalid));
    }

    [Fact]
    public void Snapshots_IncludeAnchoredMotionConsumedContactPendingEncounterAndNewFactNames()
    {
        EncounterContext context = CreateOpenedEncounter(withAliceGoal: false);

        string snapshot = FirstBoardScenario.WorldSnapshot(context.World);
        Assert.Contains(
            "passage:tavern-market-road:0:0:market:1:300000",
            snapshot,
            StringComparison.Ordinal);
        Assert.Contains(
            "consumedContacts=tavern-market-road:alice:1:bob:1",
            snapshot,
            StringComparison.Ordinal);
        Assert.Contains(
            "pendingEncounter=tavern-market-road:alice:1:bob:1:HeadOnMeeting",
            snapshot,
            StringComparison.Ordinal);

        Assert.Equal(
            "passage-encounter.opened",
            FirstBoardScenario.FactName(new GameBoardFact(new PassageEncounterOpenedEvent(
                context.ContactKey,
                PassageContactKind.HeadOnMeeting))));
        Assert.Equal(
            "passage-encounter.resolved",
            FirstBoardScenario.FactName(new GameBoardFact(new PassageEncounterResolvedEvent(
                context.ContactKey,
                BoardIds.Alice,
                PassageEncounterResolution.Continued))));
        Assert.Equal(
            "spatial.passage-contact-occurred",
            FirstBoardScenario.FactName(new SpatialBoardFact(new PassageContactOccurredFact(
                context.ContactKey,
                PassageContactKind.HeadOnMeeting))));
        Assert.Equal(
            "spatial.traversal-reversed",
            FirstBoardScenario.FactName(new SpatialBoardFact(new TraversalReversedFact(
                new EntityId(BoardIds.Alice),
                ExpectedMovementGeneration: 1))));
    }

    private static EncounterContext CreateOpenedEncounter(bool withAliceGoal)
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 302);
        var reducer = new FirstBoardReducer(instance.Graph);
        FirstBoardWorld traveling = CreateTravelingPrefix(instance, withAliceGoal);
        var contactRule = new SpatialContactOccurrenceRule(instance.Graph);
        OccurrenceCandidate<PassageContactOccurrenceData> contact = Assert.Single(
            contactRule.Forecast(
                traveling.Spatial,
                new SimulationRules(instance.WorldSeed, maxTransitionsPerModelTime: 100)));
        var instant = new LogicalInstant(contact.Due.ModelTime, 0);
        FirstBoardWorld consumed = reducer.Apply(
            traveling,
            instant,
            new SpatialBoardFact(new PassageContactOccurredFact(
                contact.Data.ContactKey,
                contact.Data.Kind)));
        FirstBoardWorld opened = reducer.Apply(
            consumed,
            NextInstant(instant),
            new GameBoardFact(new PassageEncounterOpenedEvent(
                contact.Data.ContactKey,
                contact.Data.Kind)));
        return new EncounterContext(
            instance,
            reducer,
            opened,
            contact.Data.ContactKey,
            instant);
    }

    private static FirstBoardWorld CreateTravelingPrefix(
        ScenarioInstance instance,
        bool withAliceGoal)
    {
        var reducer = new FirstBoardReducer(instance.Graph);
        FirstBoardWorld world = instance.CreateInitialWorld();
        var at = new LogicalInstant(ModelTime.Zero, 0);
        if (withAliceGoal)
        {
            world = reducer.Apply(
                world,
                at,
                new GameBoardFact(new ActorTravelGoalSetEvent(
                    BoardIds.Alice,
                    new PlaceId(BoardIds.Cellar))));
        }

        var planner = new SpatialPlanner(instance.Graph);
        world = StartTraversal(reducer, planner, world, BoardIds.Alice, at);
        world = StartTraversal(reducer, planner, world, BoardIds.Bob, at);
        return world;
    }

    private static FirstBoardWorld StartTraversal(
        FirstBoardReducer reducer,
        SpatialPlanner planner,
        FirstBoardWorld world,
        string actorId,
        LogicalInstant instant)
    {
        SpatialPlanAccepted plan = Assert.IsType<SpatialPlanAccepted>(planner.TryStartTraversal(
            world.Spatial,
            new EntityId(actorId),
            new PassageId(BoardIds.TavernMarketRoad),
            BoardTiming.TravelSpeed,
            instant.ModelTime));
        return plan.Facts.Aggregate(
            world,
            (current, fact) => reducer.Apply(current, instant, new SpatialBoardFact(fact)));
    }

    private static FirstBoardWorld AddUnrelatedActorWithPendingEncounter(
        ScenarioInstance instance,
        FirstBoardWorld genesis)
    {
        const string charlie = "charlie";
        var key = new PassageContactKey(
            new PassageId(BoardIds.TavernMarketRoad),
            new EntityId(BoardIds.Alice),
            movementGenerationA: 0,
            new EntityId(BoardIds.Bob),
            movementGenerationB: 0);
        BoardActor[] actors =
        [
            .. genesis.Actors,
            new BoardActor(
                Id: 99,
                Key: charlie,
                Generation: 0,
                DecisionSequence: 0,
                Activity: null,
                TravelGoalPlaceId: null,
                KnownFacts: []),
        ];
        EntityPlacement[] placements =
        [
            .. genesis.Spatial.Entities.Select(entity => new EntityPlacement(
                entity.Id,
                Assert.IsType<AtPlaceLocation>(entity.Location).PlaceId)),
            new(new EntityId(charlie), new PlaceId(BoardIds.Tavern)),
        ];
        return new FirstBoardWorld(
            genesis.Game with
            {
                NextPersistentId = 100,
                Actors = Array.AsReadOnly(actors.OrderBy(actor => actor.Id).ToArray()),
                PendingEncounter = new PendingPassageEncounter(
                    key,
                    PassageContactKind.HeadOnMeeting),
            },
            GraphSpatialState.Create(instance.Graph, placements));
    }

    private static void AssertResolutionRejected(
        EncounterContext context,
        PassageContactKey key,
        string? responder,
        PassageEncounterResolution resolution) =>
        Assert.Throws<InvalidOperationException>(() => context.Reducer.Apply(
            context.World,
            NextInstant(context.ContactInstant),
            new GameBoardFact(new PassageEncounterResolvedEvent(
                key,
                responder,
                resolution))));

    private static SpatialEntity SpatialEntity(FirstBoardWorld world, string actorId)
    {
        Assert.True(world.Spatial.TryGetEntity(new EntityId(actorId), out SpatialEntity? entity));
        return entity!;
    }

    private static LogicalInstant NextInstant(LogicalInstant instant) =>
        new(instant.ModelTime, checked(instant.CausalOrdinal + 1));

    private sealed record EncounterContext(
        ScenarioInstance Instance,
        FirstBoardReducer Reducer,
        FirstBoardWorld World,
        PassageContactKey ContactKey,
        LogicalInstant ContactInstant);
}
