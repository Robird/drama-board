using System.Text.Json;
using System.Text.Json.Serialization;
using DramaBoard.FirstBoard;
using DramaBoard.Host;
using DramaBoard.Journal.Atelia;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.FirstBoard.Persistence.Tests;

public sealed class FirstBoardPersistenceTests
{
    private const long LineageId = FirstBoardScenario.LineageId;
    private const string PayloadCodec = "firstboard-fact-json/2";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    [Fact]
    public async Task Run_PersistsReopensAndFoldsCompleteBatches()
    {
        using var directory = new TemporaryJournalDirectory();
        ScenarioInstance instance = DeadlineScenario(worldSeed: 42);
        FirstBoardWorld initial = instance.CreateInitialWorld();
        HostRunResult<FirstBoardWorld> runtime;
        JournalBatch<BoardEventPayload>[] written;

        using (var sink = CreateSink(directory.Path))
        {
            runtime = await RunAsync(sink, instance, initial, new ModelTime(200));
            written = [.. sink.Batches];
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
            FirstBoardWorld folded = Fold(initial, replay.Batches);

            Assert.Equal(StepStatus.BoundaryReached, runtime.Status);
            Assert.Equal(SerializeWorld(runtime.World), SerializeWorld(folded));
            AssertBatchesEqual(written, replay.Batches);
            Assert.Equal(LineageId, replay.Sink.LineageId);
            Assert.Contains(replay.Batches.SelectMany(batch => batch.Facts),
                fact => fact is CellarSealedEvent);
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
        JournalBatch<BoardEventPayload>[] expectedBatches;

        using (var sink = CreateSink(oneShotPath))
        {
            expected = await RunAsync(sink, instance, initial, new ModelTime(200));
            expectedBatches = [.. sink.Batches];
        }

        using (var firstSink = CreateSink(splitPath))
        {
            HostRunResult<FirstBoardWorld> first = await RunAsync(
                firstSink,
                instance,
                initial,
                new ModelTime(100));
            Assert.Equal(StepStatus.BoundaryReached, first.Status);
        }

        HostRunResult<FirstBoardWorld> actual;
        JournalBatch<BoardEventPayload>[] actualBatches;
        using (var reopened = CreateSink(splitPath))
        {
            FirstBoardWorld replayedWorld = Fold(initial, reopened.Batches);
            LogicalInstant? last = reopened.Batches.Count == 0
                ? null
                : reopened.Batches[^1].Instant;
            SimulationKernel<FirstBoardWorld, BoardCandidate, BoardEventPayload> kernel =
                FirstBoardScenario.CreateKernel(
                    Drivers(),
                    instance,
                    reopened,
                    replayedWorld,
                    new WorldVersion(LineageId, reopened.Batches.Count),
                    last);
            actual = await SimulationHost.RunUntilAsync(kernel, new ModelTime(200));
            actualBatches = [.. reopened.Batches];
        }

        Assert.Equal(SerializeWorld(expected.World), SerializeWorld(actual.World));
        Assert.Equal(expected.Version, actual.Version);
        AssertBatchesEqual(expectedBatches, actualBatches);
    }

    private static ScenarioInstance DeadlineScenario(ulong worldSeed) =>
        new(
            ScenarioDefinition.Default with { CellarDeadlineMs = 123 },
            worldSeed);

    private static AteliaJournalSink<BoardEventPayload> CreateSink(string path) =>
        new(path, LineageId, PayloadCodec, SerializePayload, DeserializePayload);

    private static async Task<HostRunResult<FirstBoardWorld>> RunAsync(
        IJournalSink<BoardEventPayload> journal,
        ScenarioInstance instance,
        FirstBoardWorld initialWorld,
        ModelTime until)
    {
        SimulationKernel<FirstBoardWorld, BoardCandidate, BoardEventPayload> kernel =
            FirstBoardScenario.CreateKernel(Drivers(), instance, journal, initialWorld);
        return await SimulationHost.RunUntilAsync(kernel, until, CancellationToken.None);
    }

    private static IReadOnlyDictionary<string, IPlayerDriver> Drivers() =>
        new Dictionary<string, IPlayerDriver>(StringComparer.Ordinal)
        {
            [BoardIds.Alice] = new NullPlayerDriver(),
            [BoardIds.Bob] = new NullPlayerDriver(),
        };

    private static FirstBoardWorld Fold(
        FirstBoardWorld initial,
        IReadOnlyList<JournalBatch<BoardEventPayload>> batches)
    {
        var reducer = new FirstBoardReducer();
        FirstBoardWorld world = initial;
        foreach (JournalBatch<BoardEventPayload> batch in batches)
        {
            foreach (BoardEventPayload fact in batch.Facts)
            {
                world = reducer.Apply(world, batch.Instant, fact);
            }
        }

        return world;
    }

    private static byte[] SerializePayload(BoardEventPayload payload) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new FactEnvelope(FirstBoardScenario.FactName(payload), payload),
            JsonOptions);

    private static BoardEventPayload DeserializePayload(byte[] payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement root = document.RootElement;
        string kind = root.GetProperty("Kind").GetString()
            ?? throw new JsonException("FirstBoard fact kind cannot be null.");
        JsonElement fact = root.GetProperty("Payload");
        return kind switch
        {
            "actor.departed" => Deserialize<ActorDepartedEvent>(fact),
            "actor.arrived" => Deserialize<ActorArrivedEvent>(fact),
            "actor.wait-started" => Deserialize<ActorWaitStartedEvent>(fact),
            "actor.waited" => Deserialize<ActorWaitedEvent>(fact),
            "actor.spoke" => Deserialize<ActorSpokeEvent>(fact),
            "actor.observed" => Deserialize<ActorObservedEvent>(fact),
            "object.taken" => Deserialize<ObjectTakenEvent>(fact),
            "object.placed" => Deserialize<ObjectPlacedEvent>(fact),
            "object.given" => Deserialize<ObjectGivenEvent>(fact),
            "object.shown" => Deserialize<ObjectShownEvent>(fact),
            "chest.opened" => Deserialize<ChestOpenedEvent>(fact),
            "action.rejected" => Deserialize<ActionRejectedEvent>(fact),
            "cellar.sealed" => Deserialize<CellarSealedEvent>(fact),
            _ => throw new NotSupportedException($"FirstBoard fact kind '{kind}' is not supported."),
        };
    }

    private static TPayload Deserialize<TPayload>(JsonElement payload)
        where TPayload : BoardEventPayload =>
        payload.Deserialize<TPayload>(JsonOptions)
        ?? throw new JsonException($"FirstBoard fact '{typeof(TPayload).Name}' cannot be null.");

    private static byte[] SerializeWorld(FirstBoardWorld world) =>
        JsonSerializer.SerializeToUtf8Bytes(world, JsonOptions);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new ModelTimeJsonConverter());
        return options;
    }

    private static void AssertBatchesEqual(
        IReadOnlyList<JournalBatch<BoardEventPayload>> expected,
        IReadOnlyList<JournalBatch<BoardEventPayload>> actual)
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
