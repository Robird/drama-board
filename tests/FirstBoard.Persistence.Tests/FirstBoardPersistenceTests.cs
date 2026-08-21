using System.Text.Json;
using System.Text.Json.Serialization;
using DramaBoard.FirstBoard;
using DramaBoard.Host;
using DramaBoard.Journal.Atelia;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Protocol;
using DramaBoard.Spatial;

namespace DramaBoard.FirstBoard.Persistence.Tests;

public sealed class FirstBoardPersistenceTests
{
    private const long LineageId = FirstBoardScenario.LineageId;
    private const string PayloadCodec = "firstboard-host-fact-json/3";
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
        FirstBoardFact[] facts =
        [
            new GameBoardFact(new ActorTravelStartedEvent("actor", "exit:road", "goal")),
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
            new GameBoardFact(new CellarSealedEvent()),
            new SpatialBoardFact(new EntityPlacedFact(new EntityId("entity"), new PlaceId("place"))),
            new SpatialBoardFact(new EntityRemovedFact(new EntityId("entity"))),
            new SpatialBoardFact(new TraversalStartedFact(
                new EntityId("entity"),
                new PassageId("passage"),
                new PlaceId("from"),
                SpeedSnapshot: 2)),
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

    private static ScenarioInstance DeadlineScenario(ulong worldSeed) =>
        new(
            ScenarioDefinition.Default with { CellarDeadlineMs = CellarDeadlineMs },
            worldSeed);

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
