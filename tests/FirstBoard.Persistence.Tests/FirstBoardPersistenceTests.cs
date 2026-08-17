using System.Text.Json;
using System.Text.Json.Serialization;
using DramaBoard.FirstBoard;
using DramaBoard.Host;
using DramaBoard.Journal.Atelia;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Protocol;

namespace DramaBoard.FirstBoard.Persistence.Tests;

public sealed class FirstBoardPersistenceTests
{
    private const long LineageId = 10_001;
    private const string PayloadCodec = "firstboard-json/1";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    [Fact]
    public async Task ScriptedGame_PersistsReopensAndFoldsToRuntimeWorld()
    {
        using var directory = new TemporaryJournalDirectory();
        FirstBoardWorld initial = FirstBoardWorld.CreateInitial(worldSeed: 42);
        PlayerDecisionSessionResult<FirstBoardWorld> runtime;
        DomainEvent<BoardEventPayload>[] written;

        using (var sink = CreateSink(directory.Path))
        {
            runtime = await RunAsync(
                sink,
                initial,
                SimulationCursor.CreateInitial(LineageId, ModelTime.Zero),
                new ModelTime(BoardTiming.RandomRunBoundaryTicks));
            written = [.. sink.Events];
        }

        var replay = AteliaJournalSink<BoardEventPayload>.OpenAndReplay(
            directory.Path,
            "main",
            LineageId,
            PayloadCodec,
            SerializePayload,
            DeserializePayload);
        using (replay.Sink)
        {
            FirstBoardWorld folded = replay.Events.Aggregate(initial, new FirstBoardReducer().Apply);

            Assert.Equal(StopReason.BoundaryReached, runtime.StopReason);
            Assert.Equal(SerializeWorld(runtime.World), SerializeWorld(folded));
            AssertDomainEventsEqual(written, replay.Events);
            Assert.Equal(LineageId, replay.Sink.LineageId);
            Assert.Contains(replay.Events, domainEvent =>
                domainEvent.Payload is ChestOpenedEvent);
            Assert.Contains(replay.Events, domainEvent =>
                domainEvent.Payload is ObjectPlacedEvent);
            Assert.Contains(replay.Events, domainEvent =>
                domainEvent.Payload is CellarSealedEvent);
        }
    }

    [Fact]
    public async Task PersistedCursor_ReopenAndContinue_EqualsOneShotJournalEventForEvent()
    {
        using var directory = new TemporaryJournalDirectory();
        string oneShotPath = Path.Combine(directory.Path, "one-shot");
        string splitPath = Path.Combine(directory.Path, "split");
        string snapshotPath = Path.Combine(directory.Path, "cursor.snapshot.json");
        FirstBoardWorld initial = FirstBoardWorld.CreateInitial(worldSeed: 42);
        PlayerDecisionSessionResult<FirstBoardWorld> expected;
        DomainEvent<BoardEventPayload>[] expectedEvents;

        using (var oneShotSink = CreateSink(oneShotPath))
        {
            expected = await RunAsync(
                oneShotSink,
                initial,
                SimulationCursor.CreateInitial(LineageId, ModelTime.Zero),
                new ModelTime(BoardTiming.RandomRunBoundaryTicks));
            expectedEvents = [.. oneShotSink.Events];
        }

        using (var firstSink = CreateSink(splitPath))
        {
            PlayerDecisionSessionResult<FirstBoardWorld> first = await RunAsync(
                firstSink,
                initial,
                SimulationCursor.CreateInitial(LineageId, ModelTime.Zero),
                new ModelTime(450_000));

            Assert.Equal(StopReason.BoundaryReached, first.StopReason);
            Assert.Equal(0, first.PendingDecisionCount);
            File.WriteAllBytes(
                snapshotPath,
                CursorSnapshotEnvelopeCodec.Serialize(first.Cursor.ToSnapshot()));
        }

        PlayerDecisionSessionResult<FirstBoardWorld> actual;
        DomainEvent<BoardEventPayload>[] actualEvents;
        using (var reopened = CreateSink(splitPath))
        {
            FirstBoardWorld replayedWorld = reopened.Events.Aggregate(
                initial,
                new FirstBoardReducer().Apply);
            CursorSnapshot persistedSnapshot = CursorSnapshotEnvelopeCodec.Deserialize(
                File.ReadAllBytes(snapshotPath));
            SimulationCursor restoredCursor = SimulationCursor.FromSnapshot(persistedSnapshot);

            Assert.Equal(reopened.LineageId, restoredCursor.LineageId);
            actual = await RunAsync(
                reopened,
                replayedWorld,
                restoredCursor,
                new ModelTime(BoardTiming.RandomRunBoundaryTicks));
            actualEvents = [.. reopened.Events];
        }

        Assert.Equal(SerializeWorld(expected.World), SerializeWorld(actual.World));
        Assert.Equal(expected.Cursor.ToSnapshot(), actual.Cursor.ToSnapshot());
        Assert.Equal(expected.Version, actual.Version);
        AssertDomainEventsEqual(expectedEvents, actualEvents);
    }

    private static AteliaJournalSink<BoardEventPayload> CreateSink(string path) =>
        new(path, LineageId, PayloadCodec, SerializePayload, DeserializePayload);

    private static async Task<PlayerDecisionSessionResult<FirstBoardWorld>> RunAsync(
        IJournalSink<BoardEventPayload> journal,
        FirstBoardWorld initialWorld,
        SimulationCursor initialCursor,
        ModelTime until)
    {
        var session = new PlayerDecisionSession<FirstBoardWorld, BoardCandidate, BoardEventPayload>(
            FirstBoardScenario.CreateLoop(new FirstBoardReducer()),
            journal,
            initialWorld,
            initialCursor,
            FirstBoardScenario.SelectActor,
            CreateDrivers(),
            FirstBoardScenario.BuildRequest,
            FirstBoardScenario.TranslateDecision,
            rejectionSelector: FirstBoardScenario.SelectRejectedActor);
        return await session.RunUntilAsync(until, CancellationToken.None);
    }

    private static IReadOnlyDictionary<string, IPlayerDriver> CreateDrivers() =>
        new Dictionary<string, IPlayerDriver>(StringComparer.Ordinal)
        {
            [BoardIds.Alice] = new DecisionIdScriptDriver(),
            [BoardIds.Bob] = new DecisionIdScriptDriver(),
        };

    private static byte[] SerializePayload(BoardEventPayload payload) =>
        payload switch
        {
            DecisionRequestedEvent value => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions),
            ActionRequestedEvent value => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions),
            ActorDepartedEvent value => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions),
            ActorArrivedEvent value => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions),
            ActorWaitStartedEvent value => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions),
            ActorWaitedEvent value => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions),
            ActorSpokeEvent value => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions),
            ActorObservedEvent value => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions),
            ObjectTakenEvent value => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions),
            ObjectPlacedEvent value => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions),
            ObjectGivenEvent value => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions),
            ObjectShownEvent value => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions),
            ChestOpenedEvent value => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions),
            ObjectContentionResolvedEvent value => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions),
            ActionRejectedEvent value => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions),
            CellarSealedEvent value => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions),
            _ => throw new InvalidOperationException(
                $"Unknown FirstBoard payload type '{payload.GetType().FullName}'."),
        };

    private static BoardEventPayload DeserializePayload(EventKind kind, byte[] payload) =>
        kind.Id switch
        {
            "decision.requested" => Deserialize<DecisionRequestedEvent>(payload),
            "action.travel-requested" or
            "action.wait-requested" or
            "action.talk-requested" or
            "action.observe-requested" or
            "action.take-requested" or
            "action.put-requested" or
            "action.give-requested" or
            "action.show-requested" or
            "action.use-requested" or
            "action.unknown-requested" => Deserialize<ActionRequestedEvent>(payload),
            "actor.departed" => Deserialize<ActorDepartedEvent>(payload),
            "actor.arrived" => Deserialize<ActorArrivedEvent>(payload),
            "actor.wait-started" => Deserialize<ActorWaitStartedEvent>(payload),
            "actor.waited" => Deserialize<ActorWaitedEvent>(payload),
            "actor.spoke" => Deserialize<ActorSpokeEvent>(payload),
            "actor.observed" => Deserialize<ActorObservedEvent>(payload),
            "object.taken" => Deserialize<ObjectTakenEvent>(payload),
            "object.placed" => Deserialize<ObjectPlacedEvent>(payload),
            "object.given" => Deserialize<ObjectGivenEvent>(payload),
            "object.shown" => Deserialize<ObjectShownEvent>(payload),
            "chest.opened" => Deserialize<ChestOpenedEvent>(payload),
            "object.contention-resolved" => Deserialize<ObjectContentionResolvedEvent>(payload),
            "action.rejected" => Deserialize<ActionRejectedEvent>(payload),
            "cellar.sealed" => Deserialize<CellarSealedEvent>(payload),
            _ => throw new NotSupportedException(
                $"FirstBoard payload kind '{kind.Id}' version {kind.Version} is not supported."),
        };

    private static TPayload Deserialize<TPayload>(byte[] payload)
        where TPayload : BoardEventPayload =>
        JsonSerializer.Deserialize<TPayload>(payload, JsonOptions)
        ?? throw new JsonException($"FirstBoard payload '{typeof(TPayload).Name}' cannot be null.");

    private static byte[] SerializeWorld(FirstBoardWorld world) =>
        JsonSerializer.SerializeToUtf8Bytes(world, JsonOptions);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new ModelTimeJsonConverter());
        return options;
    }

    private static void AssertDomainEventsEqual(
        IReadOnlyList<DomainEvent<BoardEventPayload>> expected,
        IReadOnlyList<DomainEvent<BoardEventPayload>> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].Timestamp, actual[index].Timestamp);
            Assert.Equal(expected[index].Cause, actual[index].Cause);
            Assert.Equal(expected[index].Kind.Id, actual[index].Kind.Id);
            Assert.Equal(expected[index].Kind.Version, actual[index].Kind.Version);
            Assert.Equal(expected[index].Payload.GetType(), actual[index].Payload.GetType());
            Assert.Equal(SerializePayload(expected[index].Payload), SerializePayload(actual[index].Payload));
        }
    }

    private sealed class DecisionIdScriptDriver : IPlayerDriver
    {
        public ValueTask<PlayerDecision> DecideAsync(
            DecisionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Intent intent = request.DecisionId.Value switch
            {
                "decision.alice.1" => new Intent(ActionKinds.Travel, DestinationId: BoardIds.Market),
                "decision.alice.2" => new Intent(ActionKinds.Take, TargetObjectId: BoardIds.BrassKey),
                "decision.alice.3" => new Intent(
                    ActionKinds.Talk,
                    TargetActorId: BoardIds.Bob,
                    FreeText: $"fact:{BoardIds.KeyLocationKnown}"),
                "decision.alice.4" => new Intent(ActionKinds.Travel, DestinationId: BoardIds.Cellar),
                "decision.alice.5" => new Intent(ActionKinds.Use, TargetObjectId: BoardIds.LockedChest),
                "decision.alice.6" => new Intent(
                    ActionKinds.Put,
                    TargetObjectId: BoardIds.DuchessLetter),
                "decision.alice.7" => new Intent(ActionKinds.Wait, DurationMs: 5_000_000),
                "decision.bob.1" => new Intent(ActionKinds.Wait, DurationMs: BoardTiming.TravelTicks),
                "decision.bob.2" => new Intent(ActionKinds.Wait, DurationMs: BoardTiming.TravelTicks),
                "decision.bob.3" => new Intent(ActionKinds.Travel, DestinationId: BoardIds.Tavern),
                "decision.bob.4" => new Intent(ActionKinds.Wait, DurationMs: 5_000_000),
                _ => throw new InvalidOperationException(
                    $"The scripted game has no decision for '{request.DecisionId.Value}'."),
            };
            return ValueTask.FromResult(new PlayerDecision(
                request.DecisionId,
                request.BasedOnWorldVersion,
                request.LineageId,
                intent));
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
}
