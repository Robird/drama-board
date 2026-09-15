using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Player;
using DramaBoard.Protocol;
using DramaBoard.Spatial;

namespace DramaBoard.FirstBoard.Tests;

/// <summary>Retains the old fork tests' gameplay boundary in the memory E/S contract.
/// These are independent in-memory lineages from complete States, not a persistent fork API.</summary>
public sealed class OccurrenceBranchSemanticsTests
{
    [Theory]
    [InlineData(PersistedEncounterPrefix.Pending)]
    [InlineData(PersistedEncounterPrefix.Continued)]
    [InlineData(PersistedEncounterPrefix.Reversed)]
    [InlineData(PersistedEncounterPrefix.ArrivalBeforeWorldChanged)]
    public void CompleteBoundary_CanStartIndependentMemoryLineageWithoutPlayer(PersistedEncounterPrefix prefix)
    {
        var (instance, source) = CreatePrefix(prefix);
        var fork = Branch(source, FirstBoardScenario.LineageId + 100 + (int)prefix);
        EncounterPersistenceOracle.AssertWorldEqual(source.State, fork.State);
        Assert.Equal(source.Cursor.Version.TransitionCount, fork.Cursor.Version.TransitionCount);
        Assert.NotEqual(source.Cursor.Version.LineageId, fork.Cursor.Version.LineageId);
        Assert.Equal(source.Cursor.LastInstant, fork.Cursor.LastInstant);
        Assert.Equal(source.Cursor.LastCauseKey, fork.Cursor.LastCauseKey);
        AssertEncounterPrefix(fork.State, prefix);
        new FirstBoardReducer(instance.Graph).Validate(fork.State);
    }

    [Fact]
    public async Task PendingEncounterBranch_ContinuationCallsOnePlayerAndResolvesOnlyBranch()
    {
        var (instance, source) = CreatePrefix(PersistedEncounterPrefix.Pending);
        var branch = Branch(source, FirstBoardScenario.LineageId + 201);
        var driver = new CountingIntentPlayerDriver(new Intent(ActionKinds.ContinueTravel));
        var kernel = FirstBoardScenario.CreateKernel(new Dictionary<string, IPlayerDriver>
            { [BoardIds.Alice] = driver }, instance, branch);
        long prefixCount = branch.Cursor.Version.TransitionCount;
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(branch.Cursor.CurrentModelTime));
        Assert.Equal(1, driver.CallCount);
        Assert.Null(kernel.World.Game.PendingEncounter);
        Assert.Equal(prefixCount + 1, branch.Cursor.Version.TransitionCount);
        Assert.NotNull(source.State.Game.PendingEncounter);
        Assert.Equal(prefixCount, source.Cursor.Version.TransitionCount);
        var resolved = Assert.IsType<PassageEncounterResolvedEvent>(
            Assert.IsType<GameBoardFact>(Assert.Single(Assert.Single(branch.CompletedEvents).Facts)).Value);
        Assert.Equal(BoardIds.Alice, resolved.RespondingActorId);
        Assert.Equal(PassageEncounterResolution.Continued, resolved.Resolution);
    }

    [Fact]
    public async Task ArrivalBeforeCleanupBranch_SkipsPlayerAndCommitsWorldChanged()
    {
        var (instance, source) = CreatePrefix(PersistedEncounterPrefix.ArrivalBeforeWorldChanged);
        var branch = Branch(source, FirstBoardScenario.LineageId + 202);
        var driver = new CountingIntentPlayerDriver(new Intent(ActionKinds.ContinueTravel));
        var kernel = FirstBoardScenario.CreateKernel(new Dictionary<string, IPlayerDriver>
            { [BoardIds.Alice] = driver }, instance, branch);
        for (int index = 0; index < 3 && kernel.World.Game.PendingEncounter is not null; index++)
        {
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(source.Cursor.CurrentModelTime));
        }
        Assert.Equal(0, driver.CallCount);
        Assert.Null(kernel.World.Game.PendingEncounter);
        Assert.NotNull(source.State.Game.PendingEncounter);
        var cleanup = Assert.Single(branch.CompletedEvents.SelectMany(item => item.Facts)
            .OfType<GameBoardFact>().Select(item => item.Value).OfType<PassageEncounterResolvedEvent>());
        Assert.Null(cleanup.RespondingActorId);
        Assert.Equal(PassageEncounterResolution.WorldChanged, cleanup.Resolution);
    }

    private static InMemoryOccurrenceHistory<FirstBoardWorld, FirstBoardFact> Branch(
        InMemoryOccurrenceHistory<FirstBoardWorld, FirstBoardFact> source, long lineageId) =>
        new(source.State, new KernelCursor(new WorldVersion(lineageId, source.Cursor.Version.TransitionCount),
            source.Cursor.GenesisTime, source.Cursor.LastInstant, source.Cursor.LastCauseKey));

    private static (ScenarioInstance, InMemoryOccurrenceHistory<FirstBoardWorld, FirstBoardFact>) CreatePrefix(
        PersistedEncounterPrefix prefix)
    {
        ScenarioInstance instance = EncounterScenario(prefix);
        var history = FirstBoardScenario.CreateMemoryHistory(instance.CreateInitialWorld());
        var reducer = new FirstBoardReducer(instance.Graph);
        foreach (var occurrence in EncounterPrefixBatches(instance, prefix))
        {
            var next = occurrence.Facts.Aggregate(history.State,
                (world, fact) => reducer.Apply(world, occurrence.TargetInstant, fact));
            reducer.Validate(next);
            history.CommitEvent(occurrence);
            history.CommitState(next, history.Cursor.Advance(occurrence.CauseKey, occurrence.TargetInstant));
        }
        return (instance, history);
    }

    public enum PersistedEncounterPrefix { Pending, Continued, Reversed, ArrivalBeforeWorldChanged }

    private sealed class CountingIntentPlayerDriver(Intent intent) : IPlayerDriver
    {
        public int CallCount { get; private set; }
        public ValueTask<PlayerDecision> DecideAsync(DecisionRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return ValueTask.FromResult(new PlayerDecision(request.DecisionId, intent));
        }
    }

    private static ScenarioInstance EncounterScenario(PersistedEncounterPrefix prefix)
    {
        ScenarioDefinition definition = ScenarioDefinition.Default;
        if (prefix == PersistedEncounterPrefix.ArrivalBeforeWorldChanged)
        {
            definition = definition with
            {
                Passages = Array.AsReadOnly(definition.Passages.Select(passage =>
                    passage.Id == BoardIds.TavernMarketRoad
                        ? passage with { Length = 1 }
                        : passage).ToArray()),
            };
        }

        return new ScenarioInstance(definition, worldSeed: 45);
    }

    private static OccurrenceEvent<FirstBoardFact>[] EncounterPrefixBatches(
        ScenarioInstance instance,
        PersistedEncounterPrefix prefix)
    {
        var start = new OccurrenceEvent<FirstBoardFact>(
            CandidateKey.FromUtf8($"test/persisted-encounter/{prefix}/start"),
            new LogicalInstant(ModelTime.Zero, 0),
            [
                new GameBoardFact(new ActorTravelGoalSetEvent(
                    BoardIds.Alice,
                    new PlaceId(BoardIds.Cellar))),
                new SpatialBoardFact(new TraversalStartedFact(
                    new EntityId(BoardIds.Alice),
                    new PassageId(BoardIds.TavernMarketRoad),
                    new PlaceId(BoardIds.Tavern),
                    BoardTiming.TravelSpeed)),
                new SpatialBoardFact(new TraversalStartedFact(
                    new EntityId(BoardIds.Bob),
                    new PassageId(BoardIds.TavernMarketRoad),
                    new PlaceId(BoardIds.Market),
                    BoardTiming.TravelSpeed)),
            ]);
        var reducer = new FirstBoardReducer(instance.Graph);
        FirstBoardWorld traveling = start.Facts.Aggregate(instance.CreateInitialWorld(),
            (world, fact) => reducer.Apply(world, start.TargetInstant, fact));
        OccurrenceCandidate<PassageContactOccurrenceData> contact = Assert.Single(
            new SpatialContactOccurrenceRule(instance.Graph).Forecast(
                traveling.Spatial,
                new SimulationRules(instance.WorldSeed, maxTransitionsPerModelTime: 100)));
        PassageContactKey contactKey = contact.Data.ContactKey;
        ModelTime contactDue = contact.Due.ModelTime;
        var contactInstant = new LogicalInstant(contactDue,
            contactDue == start.TargetInstant.ModelTime ? start.TargetInstant.CausalOrdinal + 1 : 0);
        var opened = new OccurrenceEvent<FirstBoardFact>(
            CandidateKey.FromUtf8($"test/persisted-encounter/{prefix}/opened"),
            contactInstant,
            [
                new SpatialBoardFact(new PassageContactOccurredFact(
                    contactKey,
                    contact.Data.Kind)),
                new GameBoardFact(new PassageEncounterOpenedEvent(
                    contactKey,
                    contact.Data.Kind)),
            ]);
        if (prefix == PersistedEncounterPrefix.Pending)
        {
            return [start, opened];
        }

        var responseInstant = new LogicalInstant(contactDue, contactInstant.CausalOrdinal + 1);
        TraversingLocation aliceTraversal = Assert.IsType<TraversingLocation>(
            traveling.Spatial.Entities.Single(entity => entity.Id == new EntityId(BoardIds.Alice)).Location);
        // This deliberately constructs an already-stale pending State to retain cleanup coverage.
        // Arrival is later than the floor-rounded contact, not a same-tick scheduler race.
        var arrivalInstant = new LogicalInstant(aliceTraversal.ArrivalDue,
            aliceTraversal.ArrivalDue == contactDue ? contactInstant.CausalOrdinal + 1 : 0);
        OccurrenceEvent<FirstBoardFact> outcome = prefix switch
        {
            PersistedEncounterPrefix.Continued => new(
                CandidateKey.FromUtf8("test/persisted-encounter/continued"),
                responseInstant,
                [new GameBoardFact(new PassageEncounterResolvedEvent(
                    contactKey,
                    BoardIds.Alice,
                    PassageEncounterResolution.Continued))]),
            PersistedEncounterPrefix.Reversed => new(
                CandidateKey.FromUtf8("test/persisted-encounter/reversed"),
                responseInstant,
                [
                    new GameBoardFact(new PassageEncounterResolvedEvent(
                        contactKey,
                        BoardIds.Alice,
                        PassageEncounterResolution.Reversed)),
                    new SpatialBoardFact(new TraversalReversedFact(
                        new EntityId(BoardIds.Alice),
                        ExpectedMovementGeneration: 1)),
                ]),
            PersistedEncounterPrefix.ArrivalBeforeWorldChanged => new(
                CandidateKey.FromUtf8("test/persisted-encounter/arrival-before-cleanup"),
                arrivalInstant,
                [new SpatialBoardFact(new TraversalArrivedFact(
                    new EntityId(BoardIds.Alice),
                    ExpectedMovementGeneration: 1))]),
            _ => throw new InvalidOperationException($"Unknown encounter prefix '{prefix}'."),
        };
        return [start, opened, outcome];
    }

    private static void AssertEncounterPrefix(
        FirstBoardWorld world,
        PersistedEncounterPrefix prefix)
    {
        BoardActor alice = world.Actor(BoardIds.Alice);
        SpatialEntity aliceSpatial = world.Spatial.Entities.Single(entity =>
            entity.Id == new EntityId(BoardIds.Alice));
        switch (prefix)
        {
            case PersistedEncounterPrefix.Pending:
                Assert.NotNull(world.Game.PendingEncounter);
                Assert.Single(world.Spatial.ConsumedContacts);
                Assert.IsType<TraversingLocation>(aliceSpatial.Location);
                Assert.Equal(1, alice.DecisionSequence);
                break;
            case PersistedEncounterPrefix.Continued:
                Assert.Null(world.Game.PendingEncounter);
                Assert.Single(world.Spatial.ConsumedContacts);
                Assert.IsType<TraversingLocation>(aliceSpatial.Location);
                Assert.Equal(2, alice.DecisionSequence);
                Assert.Equal(new PlaceId(BoardIds.Cellar), alice.TravelGoalPlaceId);
                break;
            case PersistedEncounterPrefix.Reversed:
                Assert.Null(world.Game.PendingEncounter);
                Assert.Empty(world.Spatial.ConsumedContacts);
                TraversingLocation reversed = Assert.IsType<TraversingLocation>(aliceSpatial.Location);
                Assert.Equal(new PlaceId(BoardIds.Tavern), reversed.TargetPlaceId);
                Assert.Equal(2, aliceSpatial.MovementGeneration);
                Assert.Equal(2, alice.DecisionSequence);
                Assert.Null(alice.TravelGoalPlaceId);
                break;
            case PersistedEncounterPrefix.ArrivalBeforeWorldChanged:
                Assert.NotNull(world.Game.PendingEncounter);
                Assert.Empty(world.Spatial.ConsumedContacts);
                Assert.Equal(
                    new PlaceId(BoardIds.Market),
                    Assert.IsType<AtPlaceLocation>(aliceSpatial.Location).PlaceId);
                Assert.Equal(1, alice.DecisionSequence);
                Assert.Equal(new PlaceId(BoardIds.Cellar), alice.TravelGoalPlaceId);
                break;
            default:
                throw new InvalidOperationException($"Unknown encounter prefix '{prefix}'.");
        }
    }

}
