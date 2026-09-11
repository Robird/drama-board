using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Player;
using DramaBoard.Protocol;
using DramaBoard.Spatial;

namespace DramaBoard.FirstBoard.Tests;

/// <summary>
/// Canonical A0 material for the first durable FirstBoard save/reopen witness.
/// The travelling state is deliberately constructed as this witness's S0: its
/// cursor has no completed occurrences, even though the reducer created it.
/// </summary>
internal static class EncounterPersistenceOracle
{
    internal const ulong WorldSeed = 901;
    internal static readonly ModelTime ContactDue = new(150_000);

    internal static EncounterS0 CreateTravelingS0()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(WorldSeed);
        var reducer = new FirstBoardReducer(instance.Graph);
        FirstBoardWorld world = instance.CreateInitialWorld();
        var genesis = new LogicalInstant(ModelTime.Zero, 0);

        world = reducer.Apply(world, genesis, new GameBoardFact(
            new ActorTravelGoalSetEvent(BoardIds.Alice, new PlaceId(BoardIds.Cellar))));
        world = ApplyTraversal(instance, reducer, world, BoardIds.Alice, genesis);
        world = ApplyTraversal(instance, reducer, world, BoardIds.Bob, genesis);
        reducer.Validate(world);

        InMemoryOccurrenceHistory<FirstBoardWorld, FirstBoardFact> history =
            FirstBoardScenario.CreateMemoryHistory(world);
        return new(instance, world, history);
    }

    internal static async Task<EncounterRun> OpenEncounterAsync(
        PassageEncounterResolution response)
    {
        EncounterS0 s0 = CreateTravelingS0();
        AssertS0(s0);
        var drivers = new Dictionary<string, IPlayerDriver>(StringComparer.Ordinal)
        {
            [BoardIds.Alice] = new FixedEncounterResponseDriver(response),
        };
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> kernel =
            FirstBoardScenario.CreateKernel(drivers, s0.Instance, s0.History);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(ContactDue));
        return new(s0, kernel, s0.History);
    }

    internal static async Task CompleteResponseAsync(EncounterRun run)
    {
        Assert.Equal(StepStatus.Committed, await run.Kernel.StepAsync(ContactDue));
    }

    internal static void AssertS0(EncounterS0 s0)
    {
        Assert.Empty(s0.History.CompletedEvents);
        Assert.Equal(new WorldVersion(FirstBoardScenario.LineageId, 0), s0.History.Cursor.Version);
        Assert.Equal(ModelTime.Zero, s0.History.Cursor.GenesisTime);
        Assert.Null(s0.History.Cursor.LastInstant);
        Assert.Null(s0.History.Cursor.LastCauseKey);
        Assert.Equal(ModelTime.Zero, s0.World.Now);
        Assert.Equal(new PlaceId(BoardIds.Cellar), s0.World.Actor(BoardIds.Alice).TravelGoalPlaceId);
        foreach (string actorId in new[] { BoardIds.Alice, BoardIds.Bob })
        {
            SpatialEntity entity = Assert.Single(s0.World.Spatial.Entities,
                candidate => candidate.Id == new EntityId(actorId));
            Assert.IsType<TraversingLocation>(entity.Location);
        }
    }

    internal static void AssertCommittedBoundaryEqual(EncounterRun expected, EncounterRun actual)
    {
        Assert.Null(expected.History.PendingEvent);
        Assert.Null(actual.History.PendingEvent);
        AssertWorldEqual(expected.History.State, actual.History.State);
        Assert.Equal(expected.History.Cursor, actual.History.Cursor);
        Assert.Equal(expected.History.CompletedEvents.Count, actual.History.CompletedEvents.Count);

        for (int index = 0; index < expected.History.CompletedEvents.Count; index++)
        {
            OccurrenceEvent<FirstBoardFact> expectedEvent = expected.History.CompletedEvents[index];
            OccurrenceEvent<FirstBoardFact> actualEvent = actual.History.CompletedEvents[index];
            Assert.Equal(expectedEvent.TargetInstant, actualEvent.TargetInstant);
            Assert.Equal(expectedEvent.CauseKey, actualEvent.CauseKey);
            Assert.Equal(expectedEvent.Facts, actualEvent.Facts);
        }
    }

    /// <summary>Compares every persisted game and dynamic spatial field, including fact text.</summary>
    internal static void AssertWorldEqual(FirstBoardWorld expected, FirstBoardWorld actual)
    {
        Assert.Equal(expected.Game.WorldSeed, actual.Game.WorldSeed);
        Assert.Equal(expected.Game.NextPersistentId, actual.Game.NextPersistentId);
        Assert.Equal(expected.Game.Now, actual.Game.Now);
        Assert.Equal(expected.Game.CellarSealed, actual.Game.CellarSealed);
        Assert.Equal(expected.Game.ChestOpened, actual.Game.ChestOpened);
        Assert.Equal(expected.Game.PendingEncounter, actual.Game.PendingEncounter);

        Assert.Equal(expected.Game.Actors.Count, actual.Game.Actors.Count);
        for (int index = 0; index < expected.Game.Actors.Count; index++)
        {
            BoardActor left = expected.Game.Actors[index];
            BoardActor right = actual.Game.Actors[index];
            Assert.Equal(left.Id, right.Id);
            Assert.Equal(left.Key, right.Key);
            Assert.Equal(left.Generation, right.Generation);
            Assert.Equal(left.DecisionSequence, right.DecisionSequence);
            Assert.Equal(left.Activity, right.Activity);
            Assert.Equal(left.TravelGoalPlaceId, right.TravelGoalPlaceId);
            Assert.Equal(left.KnownFacts.Count, right.KnownFacts.Count);
            for (int factIndex = 0; factIndex < left.KnownFacts.Count; factIndex++)
            {
                BoardFact expectedFact = left.KnownFacts[factIndex];
                BoardFact actualFact = right.KnownFacts[factIndex];
                Assert.Equal(expectedFact.Kind, actualFact.Kind);
                Assert.Equal(expectedFact.RelatedId, actualFact.RelatedId);
                Assert.Equal(expectedFact.Text, actualFact.Text);
            }
        }

        Assert.Equal(expected.Game.Objects, actual.Game.Objects);
        Assert.Equal(expected.Spatial.Entities, actual.Spatial.Entities);
        Assert.Equal(expected.Spatial.PassageEntryAccessOverrides, actual.Spatial.PassageEntryAccessOverrides);
        Assert.Equal(expected.Spatial.ScheduledPassageEntryChanges, actual.Spatial.ScheduledPassageEntryChanges);
        Assert.Equal(expected.Spatial.ConsumedContacts, actual.Spatial.ConsumedContacts);
    }

    private static FirstBoardWorld ApplyTraversal(
        ScenarioInstance instance,
        FirstBoardReducer reducer,
        FirstBoardWorld world,
        string actorId,
        LogicalInstant instant)
    {
        SpatialPlanAccepted plan = Assert.IsType<SpatialPlanAccepted>(
            new SpatialPlanner(instance.Graph).TryStartTraversal(
                world.Spatial,
                new EntityId(actorId),
                new PassageId(BoardIds.TavernMarketRoad),
                BoardTiming.TravelSpeed,
                instant.ModelTime));
        return plan.Facts.Aggregate(
            world,
            (current, fact) => reducer.Apply(current, instant, new SpatialBoardFact(fact)));
    }

    internal sealed record EncounterS0(
        ScenarioInstance Instance,
        FirstBoardWorld World,
        InMemoryOccurrenceHistory<FirstBoardWorld, FirstBoardFact> History);

    internal sealed record EncounterRun(
        EncounterS0 S0,
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> Kernel,
        InMemoryOccurrenceHistory<FirstBoardWorld, FirstBoardFact> History);

    internal sealed class FixedEncounterResponseDriver(PassageEncounterResolution response) : IPlayerDriver
    {
        public ValueTask<PlayerDecision> DecideAsync(
            DecisionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(BoardIds.Alice, request.ActorId);
            Assert.Equal(ContactDue.Ticks, request.ModelTimeMs);
            Assert.Equal(
                [ActionKinds.ContinueTravel, ActionKinds.ReverseTravel],
                request.AvailableActions.Select(action => action.ActionKind));
            Assert.Equal(
                BoardIds.Bob,
                request.Observation.KnownFacts.Single(fact =>
                    fact.FactKind.Id == BoardIds.PassageContactCounterpart).RelatedId);
            ActionKind action = response == PassageEncounterResolution.Continued
                ? ActionKinds.ContinueTravel
                : ActionKinds.ReverseTravel;
            return ValueTask.FromResult(new PlayerDecision(request.DecisionId, new Intent(action)));
        }
    }
}
