using System.Text.Json;
using System.Text.Json.Serialization;
using DramaBoard.FirstBoard;
using DramaBoard.Host;
using DramaBoard.Journal.Atelia;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Player.Agency.Spatial;
using DramaBoard.Protocol;
using DramaBoard.Spatial;

namespace DramaBoard.FirstBoard.Persistence.Tests;

public sealed class FirstBoardPersistenceTests
{
    private const long LineageId = FirstBoardScenario.LineageId;
    private const string PayloadCodec = "firstboard-host-fact-json/5";
    private const long CellarDeadlineMs = 123;
    private const long RunBoundaryMs = 300_001;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    [Fact]
    public async Task Run_PersistsReopensAndFoldsCompleteHostBatches()
    {
        using var directory = new TemporaryJournalDirectory();
        ScenarioInstance instance = DeadlineScenario(worldSeed: 42);
        FirstBoardWorld initial = instance.CreateInitialWorld();
        HostRunResult<FirstBoardWorld> runtime;
        JournalBatch<FirstBoardFact>[] written;

        using (var sink = CreateSink(directory.Path))
        {
            runtime = await RunAsync(sink, instance, initial, new ModelTime(RunBoundaryMs));
            written = [.. sink.Batches];
        }

        var replay = AteliaJournalSink<FirstBoardFact>.OpenAndReplay(
            directory.Path,
            "main",
            LineageId,
            PayloadCodec,
            SerializePayload,
            DeserializePayload);
        using (replay.Sink)
        {
            FirstBoardWorld folded = Fold(instance, initial, replay.Batches);

            Assert.Equal(StepStatus.BoundaryReached, runtime.Status);
            Assert.Equal(
                FirstBoardScenario.WorldSnapshot(runtime.World),
                FirstBoardScenario.WorldSnapshot(folded));
            AssertBatchesEqual(written, replay.Batches);
            Assert.Equal(LineageId, replay.Sink.LineageId);

            JournalBatch<FirstBoardFact> deadlineBatch = Assert.Single(
                replay.Batches,
                batch => batch.Facts.Any(fact =>
                    fact is GameBoardFact { Value: CellarSealedEvent }));
            Assert.Collection(
                deadlineBatch.Facts,
                fact => Assert.IsType<CellarSealedEvent>(
                    Assert.IsType<GameBoardFact>(fact).Value),
                fact =>
                {
                    PassageEntryAccessChangedFact changed = Assert.IsType<PassageEntryAccessChangedFact>(
                        Assert.IsType<SpatialBoardFact>(fact).Value);
                    Assert.Equal(new PassageId(BoardIds.CellarGatePassage), changed.PassageId);
                    Assert.False(changed.ResultAccess.EnterableFromA);
                });
            Assert.Contains(
                replay.Batches.SelectMany(batch => batch.Facts),
                fact => fact is SpatialBoardFact { Value: TraversalStartedFact });
            Assert.Contains(
                replay.Batches.SelectMany(batch => batch.Facts),
                fact => fact is SpatialBoardFact { Value: TraversalArrivedFact });
        }
    }

    [Fact]
    public async Task ReopenAndContinue_EqualsOneShotBatchForBatch()
    {
        using var directory = new TemporaryJournalDirectory();
        string oneShotPath = Path.Combine(directory.Path, "one-shot");
        string splitPath = Path.Combine(directory.Path, "split");
        ScenarioInstance instance = DeadlineScenario(worldSeed: 43);
        FirstBoardWorld initial = instance.CreateInitialWorld();
        HostRunResult<FirstBoardWorld> expected;
        JournalBatch<FirstBoardFact>[] expectedBatches;

        using (var sink = CreateSink(oneShotPath))
        {
            expected = await RunAsync(
                sink,
                instance,
                initial,
                new ModelTime(RunBoundaryMs));
            expectedBatches = [.. sink.Batches];
        }

        using (var firstSink = CreateSink(splitPath))
        {
            HostRunResult<FirstBoardWorld> first = await RunAsync(
                firstSink,
                instance,
                initial,
                new ModelTime(150_000));
            Assert.Equal(StepStatus.BoundaryReached, first.Status);
            Assert.Contains(
                firstSink.Batches.SelectMany(batch => batch.Facts),
                fact => fact is SpatialBoardFact { Value: TraversalStartedFact });
            Assert.DoesNotContain(
                firstSink.Batches.SelectMany(batch => batch.Facts),
                fact => fact is SpatialBoardFact { Value: TraversalArrivedFact });
        }

        HostRunResult<FirstBoardWorld> actual;
        JournalBatch<FirstBoardFact>[] actualBatches;
        using (var reopened = CreateSink(splitPath))
        {
            FirstBoardWorld replayedWorld = Fold(instance, initial, reopened.Batches);
            LogicalInstant? last = reopened.Batches.Count == 0
                ? null
                : reopened.Batches[^1].Instant;
            SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> kernel =
                FirstBoardScenario.CreateKernel(
                    Drivers(),
                    instance,
                    reopened,
                    replayedWorld,
                    new WorldVersion(LineageId, reopened.Batches.Count),
                    last);
            actual = await SimulationHost.RunUntilAsync(
                kernel,
                new ModelTime(RunBoundaryMs));
            actualBatches = [.. reopened.Batches];
        }

        Assert.Equal(StepStatus.BoundaryReached, actual.Status);
        Assert.Equal(
            FirstBoardScenario.WorldSnapshot(expected.World),
            FirstBoardScenario.WorldSnapshot(actual.World));
        Assert.Equal(expected.Version, actual.Version);
        AssertBatchesEqual(expectedBatches, actualBatches);
    }

    [Fact]
    public void PayloadCodec_RoundTripsEveryCurrentHostFactShape()
    {
        var headOnContact = new PassageContactKey(
            new PassageId("passage"),
            new EntityId("actor"),
            movementGenerationA: 3,
            new EntityId("target"),
            movementGenerationB: 5);
        var overtakeContact = new PassageContactKey(
            new PassageId("passage"),
            new EntityId("target"),
            movementGenerationA: 8,
            new EntityId("actor"),
            movementGenerationB: 7);
        FirstBoardFact[] facts =
        [
            new GameBoardFact(new ActorTravelStartedEvent("actor", "exit:road", "goal")),
            new GameBoardFact(new ActorTravelGoalSetEvent("actor", new PlaceId("goal"))),
            new GameBoardFact(new ActorTravelGoalResolvedEvent(
                "actor",
                new PlaceId("goal"),
                TravelGoalResolution.Completed)),
            new GameBoardFact(new ActorTravelGoalResolvedEvent(
                "actor",
                new PlaceId("goal"),
                TravelGoalResolution.Blocked)),
            new GameBoardFact(new PassageEncounterOpenedEvent(
                headOnContact,
                PassageContactKind.HeadOnMeeting)),
            new GameBoardFact(new PassageEncounterOpenedEvent(
                overtakeContact,
                PassageContactKind.Overtake)),
            new GameBoardFact(new PassageEncounterResolvedEvent(
                headOnContact,
                "actor",
                PassageEncounterResolution.Continued)),
            new GameBoardFact(new PassageEncounterResolvedEvent(
                headOnContact,
                "actor",
                PassageEncounterResolution.Reversed)),
            new GameBoardFact(new PassageEncounterResolvedEvent(
                headOnContact,
                RespondingActorId: null,
                PassageEncounterResolution.WorldChanged)),
            new GameBoardFact(new TicketConsumedEvent("actor", "ticket")),
            new GameBoardFact(new ActorWaitStartedEvent("actor", new ModelTime(11))),
            new GameBoardFact(new ActorWaitedEvent("actor")),
            new GameBoardFact(new ActorSpokeEvent("actor", "target", "hello", "known.fact")),
            new GameBoardFact(new ActorObservedEvent(
                "actor",
                [new BoardFact("known.fact", "related", "text")],
                "object")),
            new GameBoardFact(new ObjectTakenEvent("actor", "object")),
            new GameBoardFact(new ObjectPlacedEvent("actor", "object", "place")),
            new GameBoardFact(new ObjectGivenEvent("actor", "target", "object")),
            new GameBoardFact(new ObjectShownEvent("actor", "target", "object")),
            new GameBoardFact(new ChestOpenedEvent("actor", "chest", "key")),
            new GameBoardFact(new ActionRejectedEvent(
                "actor",
                new Intent(ActionKinds.Wait, DurationMs: 1),
                "reason")),
            new GameBoardFact(new ActionRejectedEvent(
                "actor",
                new Intent(ActionKinds.ContinueTravel, FreeText: "keep going"),
                "reason")),
            new GameBoardFact(new ActionRejectedEvent(
                "actor",
                new Intent(ActionKinds.ReverseTravel),
                "reason")),
            new GameBoardFact(new CellarSealedEvent()),
            new SpatialBoardFact(new EntityPlacedFact(new EntityId("entity"), new PlaceId("place"))),
            new SpatialBoardFact(new EntityRemovedFact(new EntityId("entity"))),
            new SpatialBoardFact(new TraversalStartedFact(
                new EntityId("entity"),
                new PassageId("passage"),
                new PlaceId("from"),
                SpeedSnapshot: 2)),
            new SpatialBoardFact(new TraversalReversedFact(
                new EntityId("entity"),
                ExpectedMovementGeneration: 3)),
            new SpatialBoardFact(new PassageContactOccurredFact(
                headOnContact,
                PassageContactKind.HeadOnMeeting)),
            new SpatialBoardFact(new PassageContactOccurredFact(
                overtakeContact,
                PassageContactKind.Overtake)),
            new SpatialBoardFact(new TraversalArrivedFact(new EntityId("entity"), 3)),
            new SpatialBoardFact(new PassageEntryAccessChangedFact(
                new PassageId("passage"),
                new PassageEntryAccess(false, true))),
            new SpatialBoardFact(new PassageEntryChangeScheduledFact(
                new PassageId("passage"),
                new ModelTime(12),
                new PassageEntryPatch(false, null))),
            new SpatialBoardFact(new ScheduledPassageEntryChangeAppliedFact(
                new PassageId("passage"),
                new ModelTime(12))),
        ];

        foreach (FirstBoardFact expected in facts)
        {
            FirstBoardFact actual = DeserializePayload(SerializePayload(expected));

            Assert.Equal(expected.GetType(), actual.GetType());
            Assert.Equal(FirstBoardScenario.FactName(expected), FirstBoardScenario.FactName(actual));
            Assert.Equal(SerializePayload(expected), SerializePayload(actual));
        }
    }

    [Fact]
    public async Task ReopenActiveTravelGoalPrefix_ReplaysPurelyThenContinuesController()
    {
        using var directory = new TemporaryJournalDirectory();
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 44);
        FirstBoardWorld initial = WithWaitingActor(
            instance.CreateInitialWorld(),
            BoardIds.Bob,
            new ModelTime(1_000_000));
        var goalBatch = new JournalBatch<FirstBoardFact>(
            new LogicalInstant(ModelTime.Zero, 0),
            CandidateKey.FromUtf8("test/persisted-travel-goal-prefix"),
            [
                new GameBoardFact(new ActorTravelGoalSetEvent(
                    BoardIds.Alice,
                    new PlaceId(BoardIds.Cellar))),
            ]);

        using (var sink = CreateSink(directory.Path))
        {
            sink.AppendBatch(goalBatch);
        }

        var getter = new CountingFullMapGetter();
        using var reopened = CreateSink(directory.Path);
        FirstBoardWorld replayed = Fold(instance, initial, reopened.Batches);

        Assert.Equal(new PlaceId(BoardIds.Cellar), replayed.Actor(BoardIds.Alice).TravelGoalPlaceId);
        Assert.True(replayed.IsAtPlace(BoardIds.Alice, new PlaceId(BoardIds.Tavern)));
        Assert.Equal(0, getter.CallCount);

        var drivers = new Dictionary<string, IPlayerDriver>(StringComparer.Ordinal)
        {
            [BoardIds.Alice] = new ThrowingPlayerDriver(),
            [BoardIds.Bob] = new ThrowingPlayerDriver(),
        };
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> kernel =
            FirstBoardScenario.CreateKernel(
                drivers,
                instance,
                reopened,
                replayed,
                new WorldVersion(LineageId, reopened.Batches.Count),
                reopened.Batches[^1].Instant,
                getter);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(ModelTime.Zero));

        Assert.Equal(1, getter.CallCount);
        Assert.Equal(new PlaceId(BoardIds.Cellar), kernel.World.Actor(BoardIds.Alice).TravelGoalPlaceId);
        Assert.IsType<TraversingLocation>(
            kernel.World.Spatial.Entities.Single(entity =>
                entity.Id == new EntityId(BoardIds.Alice)).Location);
        Assert.Equal(2, reopened.Batches.Count);
    }

    [Theory]
    [InlineData(PersistedEncounterPrefix.Pending)]
    [InlineData(PersistedEncounterPrefix.Continued)]
    [InlineData(PersistedEncounterPrefix.Reversed)]
    [InlineData(PersistedEncounterPrefix.ArrivalBeforeWorldChanged)]
    public void EncounterPrefix_PersistsReopensReplaysAndForksWithoutPlayer(
        PersistedEncounterPrefix prefix)
    {
        using var directory = new TemporaryJournalDirectory();
        ScenarioInstance instance = EncounterScenario(prefix);
        FirstBoardWorld initial = instance.CreateInitialWorld();
        JournalBatch<FirstBoardFact>[] prefixBatches = EncounterPrefixBatches(prefix);
        using (var sink = CreateSink(directory.Path))
        {
            foreach (JournalBatch<FirstBoardFact> batch in prefixBatches)
            {
                sink.AppendBatch(batch);
            }
        }

        using var reopened = CreateSink(directory.Path);
        FirstBoardWorld replayed = Fold(instance, initial, reopened.Batches);
        AssertBatchesEqual(prefixBatches, reopened.Batches);
        AssertEncounterPrefix(replayed, prefix);

        var source = new InMemoryJournal<FirstBoardFact>(LineageId);
        foreach (JournalBatch<FirstBoardFact> batch in reopened.Batches)
        {
            source.AppendBatch(batch);
        }

        var reducer = new FirstBoardReducer(instance.Graph);
        long forkLineageId = LineageId + 100 + (int)prefix;
        InMemoryForkResult<FirstBoardWorld, FirstBoardFact> fork = SimulationFork.Create(
            initial,
            ModelTime.Zero,
            source,
            prefixTransitionCount: source.Batches.Count,
            forkLineageId,
            new SimulationRules(instance.WorldSeed, maxTransitionsPerModelTime: 100),
            reducer.Apply,
            reducer.Validate);

        Assert.Equal(
            FirstBoardScenario.WorldSnapshot(replayed),
            FirstBoardScenario.WorldSnapshot(fork.Replay.World));
        Assert.Equal(new WorldVersion(forkLineageId, source.Batches.Count), fork.Replay.Version);
        Assert.Equal(reopened.Batches[^1].Instant, fork.Replay.LastCommittedInstant);
        AssertEncounterPrefix(fork.Replay.World, prefix);
    }

    [Fact]
    public async Task PendingEncounterFork_ContinuationCallsOnePlayerAndResolves()
    {
        PersistedEncounterFork persisted = PersistReopenAndForkEncounterPrefix(
            PersistedEncounterPrefix.Pending,
            forkLineageId: LineageId + 201);
        var driver = new CountingIntentPlayerDriver(
            new Intent(ActionKinds.ContinueTravel));
        var drivers = new Dictionary<string, IPlayerDriver>(StringComparer.Ordinal)
        {
            [BoardIds.Alice] = driver,
        };
        int prefixCount = persisted.Fork.Journal.Batches.Count;
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> kernel =
            FirstBoardScenario.CreateKernel(
                drivers,
                persisted.Instance,
                persisted.Fork.Journal,
                persisted.Fork.Replay.World,
                persisted.Fork.Replay.Version,
                persisted.Fork.Replay.LastCommittedInstant);

        Assert.Equal(
            StepStatus.Committed,
            await kernel.StepAsync(persisted.Fork.Replay.CurrentModelTime));

        Assert.Equal(1, driver.CallCount);
        Assert.Null(kernel.World.Game.PendingEncounter);
        Assert.Equal(prefixCount + 1, persisted.Fork.Journal.Batches.Count);
        PassageEncounterResolvedEvent resolved = Assert.IsType<PassageEncounterResolvedEvent>(
            Assert.IsType<GameBoardFact>(
                Assert.Single(persisted.Fork.Journal.Batches[^1].Facts)).Value);
        Assert.Equal(BoardIds.Alice, resolved.RespondingActorId);
        Assert.Equal(PassageEncounterResolution.Continued, resolved.Resolution);
    }

    [Fact]
    public async Task ArrivalBeforeCleanupFork_ContinuationSkipsPlayerAndCommitsWorldChanged()
    {
        PersistedEncounterFork persisted = PersistReopenAndForkEncounterPrefix(
            PersistedEncounterPrefix.ArrivalBeforeWorldChanged,
            forkLineageId: LineageId + 202);
        var driver = new CountingIntentPlayerDriver(
            new Intent(ActionKinds.ContinueTravel));
        var drivers = new Dictionary<string, IPlayerDriver>(StringComparer.Ordinal)
        {
            [BoardIds.Alice] = driver,
        };
        int prefixCount = persisted.Fork.Journal.Batches.Count;
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> kernel =
            FirstBoardScenario.CreateKernel(
                drivers,
                persisted.Instance,
                persisted.Fork.Journal,
                persisted.Fork.Replay.World,
                persisted.Fork.Replay.Version,
                persisted.Fork.Replay.LastCommittedInstant);

        for (int step = 0; step < 3 && kernel.World.Game.PendingEncounter is not null; step++)
        {
            Assert.Equal(
                StepStatus.Committed,
                await kernel.StepAsync(persisted.Fork.Replay.CurrentModelTime));
        }

        Assert.Equal(0, driver.CallCount);
        Assert.Null(kernel.World.Game.PendingEncounter);
        PassageEncounterResolvedEvent cleanup = Assert.Single(
            persisted.Fork.Journal.Batches
                .Skip(prefixCount)
                .SelectMany(batch => batch.Facts)
                .OfType<GameBoardFact>()
                .Select(fact => fact.Value)
                .OfType<PassageEncounterResolvedEvent>());
        Assert.Null(cleanup.RespondingActorId);
        Assert.Equal(PassageEncounterResolution.WorldChanged, cleanup.Resolution);
    }

    private static ScenarioInstance DeadlineScenario(ulong worldSeed) =>
        new(
            ScenarioDefinition.Default with { CellarDeadlineMs = CellarDeadlineMs },
            worldSeed);

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

    private static PersistedEncounterFork PersistReopenAndForkEncounterPrefix(
        PersistedEncounterPrefix prefix,
        long forkLineageId)
    {
        using var directory = new TemporaryJournalDirectory();
        ScenarioInstance instance = EncounterScenario(prefix);
        FirstBoardWorld initial = instance.CreateInitialWorld();
        JournalBatch<FirstBoardFact>[] batches = EncounterPrefixBatches(prefix);
        using (var sink = CreateSink(directory.Path))
        {
            foreach (JournalBatch<FirstBoardFact> batch in batches)
            {
                sink.AppendBatch(batch);
            }
        }

        using var reopened = CreateSink(directory.Path);
        FirstBoardWorld replayed = Fold(instance, initial, reopened.Batches);
        AssertBatchesEqual(batches, reopened.Batches);
        AssertEncounterPrefix(replayed, prefix);
        var source = new InMemoryJournal<FirstBoardFact>(LineageId);
        foreach (JournalBatch<FirstBoardFact> batch in reopened.Batches)
        {
            source.AppendBatch(batch);
        }

        var reducer = new FirstBoardReducer(instance.Graph);
        InMemoryForkResult<FirstBoardWorld, FirstBoardFact> fork = SimulationFork.Create(
            initial,
            ModelTime.Zero,
            source,
            prefixTransitionCount: source.Batches.Count,
            forkLineageId,
            new SimulationRules(instance.WorldSeed, maxTransitionsPerModelTime: 100),
            reducer.Apply,
            reducer.Validate);
        Assert.Equal(
            FirstBoardScenario.WorldSnapshot(replayed),
            FirstBoardScenario.WorldSnapshot(fork.Replay.World));
        return new PersistedEncounterFork(instance, fork);
    }

    private static JournalBatch<FirstBoardFact>[] EncounterPrefixBatches(
        PersistedEncounterPrefix prefix)
    {
        ModelTime contactDue = prefix == PersistedEncounterPrefix.ArrivalBeforeWorldChanged
            ? new ModelTime(1)
            : new ModelTime(150_000);
        var contactKey = new PassageContactKey(
            new PassageId(BoardIds.TavernMarketRoad),
            new EntityId(BoardIds.Alice),
            movementGenerationA: 1,
            new EntityId(BoardIds.Bob),
            movementGenerationB: 1);
        var start = new JournalBatch<FirstBoardFact>(
            new LogicalInstant(ModelTime.Zero, 0),
            CandidateKey.FromUtf8($"test/persisted-encounter/{prefix}/start"),
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
        var opened = new JournalBatch<FirstBoardFact>(
            new LogicalInstant(contactDue, 0),
            CandidateKey.FromUtf8($"test/persisted-encounter/{prefix}/opened"),
            [
                new SpatialBoardFact(new PassageContactOccurredFact(
                    contactKey,
                    PassageContactKind.HeadOnMeeting)),
                new GameBoardFact(new PassageEncounterOpenedEvent(
                    contactKey,
                    PassageContactKind.HeadOnMeeting)),
            ]);
        if (prefix == PersistedEncounterPrefix.Pending)
        {
            return [start, opened];
        }

        JournalBatch<FirstBoardFact> outcome = prefix switch
        {
            PersistedEncounterPrefix.Continued => new(
                new LogicalInstant(contactDue, 1),
                CandidateKey.FromUtf8("test/persisted-encounter/continued"),
                [new GameBoardFact(new PassageEncounterResolvedEvent(
                    contactKey,
                    BoardIds.Alice,
                    PassageEncounterResolution.Continued))]),
            PersistedEncounterPrefix.Reversed => new(
                new LogicalInstant(contactDue, 1),
                CandidateKey.FromUtf8("test/persisted-encounter/reversed"),
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
                new LogicalInstant(contactDue, 1),
                CandidateKey.FromUtf8("test/persisted-encounter/arrival-before-cleanup"),
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

    private static FirstBoardWorld WithWaitingActor(
        FirstBoardWorld world,
        string actorId,
        ModelTime due) =>
        world with
        {
            Game = world.Game with
            {
                Actors = Array.AsReadOnly(world.Actors
                    .Select(actor => actor.Key == actorId
                        ? actor with { Activity = new BoardWaitActivity(due) }
                        : actor)
                    .ToArray()),
            },
        };

    private static AteliaJournalSink<FirstBoardFact> CreateSink(string path) =>
        new(path, LineageId, PayloadCodec, SerializePayload, DeserializePayload);

    private static async Task<HostRunResult<FirstBoardWorld>> RunAsync(
        IJournalSink<FirstBoardFact> journal,
        ScenarioInstance instance,
        FirstBoardWorld initialWorld,
        ModelTime until)
    {
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> kernel =
            FirstBoardScenario.CreateKernel(Drivers(), instance, journal, initialWorld);
        return await SimulationHost.RunUntilAsync(kernel, until, CancellationToken.None);
    }

    private static IReadOnlyDictionary<string, IPlayerDriver> Drivers() =>
        new Dictionary<string, IPlayerDriver>(StringComparer.Ordinal)
        {
            [BoardIds.Alice] = new TavernRoadThenWaitDriver(),
            [BoardIds.Bob] = new NullPlayerDriver(),
        };

    private static FirstBoardWorld Fold(
        ScenarioInstance instance,
        FirstBoardWorld initial,
        IReadOnlyList<JournalBatch<FirstBoardFact>> batches)
    {
        var reducer = new FirstBoardReducer(instance.Graph);
        FirstBoardWorld world = initial;
        foreach (JournalBatch<FirstBoardFact> batch in batches)
        {
            foreach (FirstBoardFact fact in batch.Facts)
            {
                world = reducer.Apply(world, batch.Instant, fact);
            }
        }

        reducer.Validate(world);
        return world;
    }

    private static byte[] SerializePayload(FirstBoardFact fact)
    {
        object payload = fact switch
        {
            GameBoardFact game => game.Value,
            SpatialBoardFact spatial => spatial.Value,
            _ => throw new NotSupportedException(
                $"FirstBoard Host fact '{fact.GetType().Name}' is not supported."),
        };
        return JsonSerializer.SerializeToUtf8Bytes(
            new FactEnvelope(FirstBoardScenario.FactName(fact), payload),
            JsonOptions);
    }

    private static FirstBoardFact DeserializePayload(byte[] payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement root = document.RootElement;
        string kind = root.GetProperty("Kind").GetString()
            ?? throw new JsonException("FirstBoard Host fact kind cannot be null.");
        JsonElement fact = root.GetProperty("Payload");
        return kind switch
        {
            "actor.travel-started" => Game<ActorTravelStartedEvent>(fact),
            "actor.travel-goal-set" => Game<ActorTravelGoalSetEvent>(fact),
            "actor.travel-goal-resolved" => Game<ActorTravelGoalResolvedEvent>(fact),
            "passage-encounter.opened" => Game<PassageEncounterOpenedEvent>(fact),
            "passage-encounter.resolved" => Game<PassageEncounterResolvedEvent>(fact),
            "ticket.consumed" => Game<TicketConsumedEvent>(fact),
            "actor.wait-started" => Game<ActorWaitStartedEvent>(fact),
            "actor.waited" => Game<ActorWaitedEvent>(fact),
            "actor.spoke" => Game<ActorSpokeEvent>(fact),
            "actor.observed" => Game<ActorObservedEvent>(fact),
            "object.taken" => Game<ObjectTakenEvent>(fact),
            "object.placed" => Game<ObjectPlacedEvent>(fact),
            "object.given" => Game<ObjectGivenEvent>(fact),
            "object.shown" => Game<ObjectShownEvent>(fact),
            "chest.opened" => Game<ChestOpenedEvent>(fact),
            "action.rejected" => Game<ActionRejectedEvent>(fact),
            "cellar.sealed" => Game<CellarSealedEvent>(fact),
            "spatial.entity-placed" => Spatial<EntityPlacedFact>(fact),
            "spatial.entity-removed" => Spatial<EntityRemovedFact>(fact),
            "spatial.traversal-started" => Spatial<TraversalStartedFact>(fact),
            "spatial.traversal-reversed" => Spatial<TraversalReversedFact>(fact),
            "spatial.passage-contact-occurred" => Spatial<PassageContactOccurredFact>(fact),
            "spatial.traversal-arrived" => Spatial<TraversalArrivedFact>(fact),
            "spatial.passage-entry-access-changed" => Spatial<PassageEntryAccessChangedFact>(fact),
            "spatial.passage-entry-change-scheduled" => Spatial<PassageEntryChangeScheduledFact>(fact),
            "spatial.scheduled-passage-entry-change-applied" =>
                Spatial<ScheduledPassageEntryChangeAppliedFact>(fact),
            _ => throw new NotSupportedException(
                $"FirstBoard Host fact kind '{kind}' is not supported."),
        };
    }

    private static GameBoardFact Game<TPayload>(JsonElement payload)
        where TPayload : BoardEventPayload =>
        new(Deserialize<TPayload>(payload));

    private static SpatialBoardFact Spatial<TPayload>(JsonElement payload)
        where TPayload : GraphSpatialFact =>
        new(Deserialize<TPayload>(payload));

    private static TPayload Deserialize<TPayload>(JsonElement payload) =>
        payload.Deserialize<TPayload>(JsonOptions)
        ?? throw new JsonException(
            $"FirstBoard Host fact '{typeof(TPayload).Name}' cannot be null.");

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new ModelTimeJsonConverter());
        options.Converters.Add(new PlaceIdJsonConverter());
        options.Converters.Add(new PassageIdJsonConverter());
        options.Converters.Add(new EntityIdJsonConverter());
        options.Converters.Add(new PassageEntryPatchJsonConverter());
        return options;
    }

    private static void AssertBatchesEqual(
        IReadOnlyList<JournalBatch<FirstBoardFact>> expected,
        IReadOnlyList<JournalBatch<FirstBoardFact>> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int batchIndex = 0; batchIndex < expected.Count; batchIndex++)
        {
            Assert.Equal(expected[batchIndex].Instant, actual[batchIndex].Instant);
            Assert.Equal(expected[batchIndex].CauseKey, actual[batchIndex].CauseKey);
            Assert.Equal(expected[batchIndex].Facts.Count, actual[batchIndex].Facts.Count);
            for (int factIndex = 0; factIndex < expected[batchIndex].Facts.Count; factIndex++)
            {
                Assert.Equal(
                    SerializePayload(expected[batchIndex].Facts[factIndex]),
                    SerializePayload(actual[batchIndex].Facts[factIndex]));
            }
        }
    }

    private sealed record FactEnvelope(string Kind, object Payload);

    private sealed record PersistedEncounterFork(
        ScenarioInstance Instance,
        InMemoryForkResult<FirstBoardWorld, FirstBoardFact> Fork);

    public enum PersistedEncounterPrefix
    {
        Pending = 0,
        Continued = 1,
        Reversed = 2,
        ArrivalBeforeWorldChanged = 3,
    }

    private sealed class CountingFullMapGetter :
        IPlayerSpatialKnowledgeGetter<FirstBoardWorld>
    {
        public int CallCount { get; private set; }

        public PlayerSpatialKnowledgeSnapshot GetKnownGraph(
            FirstBoardWorld committedWorld,
            string subjectId,
            GraphDefinition objectiveGraph)
        {
            _ = committedWorld;
            _ = subjectId;
            CallCount++;
            return PlayerSpatialKnowledgeSnapshot.FullMap(objectiveGraph);
        }
    }

    private sealed class ThrowingPlayerDriver : IPlayerDriver
    {
        public ValueTask<PlayerDecision> DecideAsync(
            DecisionRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Replay continuation must not call a Player.");
    }

    private sealed class CountingIntentPlayerDriver(Intent intent) : IPlayerDriver
    {
        public int CallCount { get; private set; }

        public ValueTask<PlayerDecision> DecideAsync(
            DecisionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return ValueTask.FromResult(new PlayerDecision(request.DecisionId, intent));
        }
    }

    private sealed class TavernRoadThenWaitDriver : IPlayerDriver
    {
        public ValueTask<PlayerDecision> DecideAsync(
            DecisionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Intent intent = request.Observation.LocationId == BoardIds.Tavern
                ? new Intent(
                    ActionKinds.Travel,
                    ExitId: $"exit:{BoardIds.TavernMarketRoad}")
                : new Intent(ActionKinds.Wait);
            return ValueTask.FromResult(new PlayerDecision(request.DecisionId, intent));
        }
    }

    private sealed class ModelTimeJsonConverter : JsonConverter<ModelTime>
    {
        public override ModelTime Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            new(reader.GetInt64());

        public override void Write(
            Utf8JsonWriter writer,
            ModelTime value,
            JsonSerializerOptions options) =>
            writer.WriteNumberValue(value.Ticks);
    }

    private sealed class PlaceIdJsonConverter : JsonConverter<PlaceId>
    {
        public override PlaceId Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            new(ReadRequiredString(ref reader, "PlaceId"));

        public override void Write(
            Utf8JsonWriter writer,
            PlaceId value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }

    private sealed class PassageIdJsonConverter : JsonConverter<PassageId>
    {
        public override PassageId Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            new(ReadRequiredString(ref reader, "PassageId"));

        public override void Write(
            Utf8JsonWriter writer,
            PassageId value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }

    private sealed class EntityIdJsonConverter : JsonConverter<EntityId>
    {
        public override EntityId Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            new(ReadRequiredString(ref reader, "EntityId"));

        public override void Write(
            Utf8JsonWriter writer,
            EntityId value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }

    private sealed class PassageEntryPatchJsonConverter : JsonConverter<PassageEntryPatch>
    {
        public override PassageEntryPatch Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            using JsonDocument document = JsonDocument.ParseValue(ref reader);
            JsonElement root = document.RootElement;
            return new PassageEntryPatch(
                ReadNullableBoolean(root, nameof(PassageEntryPatch.EnterableFromA)),
                ReadNullableBoolean(root, nameof(PassageEntryPatch.EnterableFromB)));
        }

        public override void Write(
            Utf8JsonWriter writer,
            PassageEntryPatch value,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            WriteNullableBoolean(
                writer,
                nameof(PassageEntryPatch.EnterableFromA),
                value.EnterableFromA);
            WriteNullableBoolean(
                writer,
                nameof(PassageEntryPatch.EnterableFromB),
                value.EnterableFromB);
            writer.WriteEndObject();
        }

        private static bool? ReadNullableBoolean(JsonElement root, string propertyName)
        {
            JsonElement property = root.GetProperty(propertyName);
            return property.ValueKind == JsonValueKind.Null
                ? null
                : property.GetBoolean();
        }

        private static void WriteNullableBoolean(
            Utf8JsonWriter writer,
            string propertyName,
            bool? value)
        {
            if (value is bool specified)
            {
                writer.WriteBoolean(propertyName, specified);
            }
            else
            {
                writer.WriteNull(propertyName);
            }
        }
    }

    private static string ReadRequiredString(ref Utf8JsonReader reader, string description) =>
        reader.GetString()
        ?? throw new JsonException($"FirstBoard Host fact {description} must be a string.");
}
