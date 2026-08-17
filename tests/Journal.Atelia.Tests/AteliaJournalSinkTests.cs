using System.Text.Json;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Journal.Atelia.Tests;

public sealed class AteliaJournalSinkTests
{
    [Fact]
    public void Envelope_RoundTripsEveryDomainEventField()
    {
        var expected = new DomainEvent<CounterEvent>(
            new LogicalTimestamp(new ModelTime(123), new Microstep(4)),
            EventCause.FromResolve(
                sourceId: 17,
                new EventCandidateId(23),
                new ModelTime(120),
                batchOrdinal: 8),
            new EventKind("counter.custom", 2),
            new CounterEvent(3, -7, "round-trip"));

        byte[] envelope = DomainEventEnvelopeCodec.Serialize(expected, SerializePayload);
        DomainEvent<CounterEvent> actual = DomainEventEnvelopeCodec.Deserialize(
            envelope,
            DeserializePayload);

        AssertDomainEventEqual(expected, actual);
        using JsonDocument document = JsonDocument.Parse(envelope);
        Assert.Equal(1, document.RootElement.GetProperty("v").GetInt32());
        Assert.Equal(
            SerializePayload(expected.Payload),
            document.RootElement.GetProperty("p").GetBytesFromBase64());
    }

    [Fact]
    public void SimulationLoop_PersistsReplaysAndFoldsToRuntimeWorld()
    {
        using var directory = new TemporaryJournalDirectory();
        CounterWorld initial = InitialWorld();
        var reducer = new CounterReducer();
        SimulationRunResult<CounterWorld, CounterEvent> runtime;
        DomainEvent<CounterEvent>[] written;

        using (var sink = CreateSink(directory.Path))
        {
            runtime = CreateLoop(reducer).Run(
                initial,
                SimulationCursor.CreateInitial(lineageId: 11, ModelTime.Zero),
                new ModelTime(40),
                sink);
            written = [.. sink.Events];
        }

        var replay = AteliaJournalSink<CounterEvent>.OpenAndReplay(
            directory.Path,
            "main",
            SerializePayload,
            DeserializePayload);
        using (replay.Sink)
        {
            CounterWorld folded = replay.Events.Aggregate(initial, reducer.Apply);

            Assert.Equal(runtime.World, folded);
            Assert.Equal(10, folded.Total);
            AssertDomainEventsEqual(written, replay.Events);
        }
    }

    [Fact]
    public void ReopenAndContinue_IsEquivalentToOneShotRun()
    {
        using var directory = new TemporaryJournalDirectory();
        CounterWorld initial = InitialWorld();
        var reducer = new CounterReducer();
        SimulationLoop<CounterWorld, CounterCandidate, CounterEvent> loop = CreateLoop(reducer);
        SimulationRunResult<CounterWorld, CounterEvent> first;

        using (var sink = CreateSink(directory.Path))
        {
            first = loop.Run(
                initial,
                SimulationCursor.CreateInitial(lineageId: 12, ModelTime.Zero),
                new ModelTime(20),
                sink);
            Assert.Equal(2, sink.Events.Count);
        }

        SimulationRunResult<CounterWorld, CounterEvent> continued;
        DomainEvent<CounterEvent>[] persisted;
        using (var reopened = CreateSink(directory.Path))
        {
            continued = loop.Run(
                first.World,
                first.Cursor,
                new ModelTime(40),
                reopened);
            persisted = [.. reopened.Events];
        }

        var memory = new InMemoryJournal<CounterEvent>();
        SimulationRunResult<CounterWorld, CounterEvent> oneShot = loop.Run(
            initial,
            SimulationCursor.CreateInitial(lineageId: 12, ModelTime.Zero),
            new ModelTime(40),
            memory);

        Assert.Equal(oneShot.World, continued.World);
        Assert.Equal(oneShot.Cursor, continued.Cursor);
        AssertDomainEventsEqual(memory.Events, persisted);
        Assert.Equal(oneShot.World, persisted.Aggregate(initial, reducer.Apply));
    }

    [Fact]
    public void ForkBranch_SharesPrefixAndAllowsDivergentSuffixes()
    {
        using var directory = new TemporaryJournalDirectory();
        var reducer = new CounterReducer();

        using (var main = CreateSink(directory.Path))
        {
            _ = CreateLoop(reducer).Run(
                InitialWorld(),
                SimulationCursor.CreateInitial(lineageId: 13, ModelTime.Zero),
                new ModelTime(20),
                main);
            main.ForkBranch("fork-1", main.Events.Count);
        }

        using (var main = CreateSink(directory.Path))
        {
            main.Append(DivergentEvent(delta: 3, route: "main"));
        }

        using (var fork = CreateSink(directory.Path, "fork-1"))
        {
            fork.Append(DivergentEvent(delta: 30, route: "fork-1"));
        }

        DomainEvent<CounterEvent>[] mainEvents;
        using (var main = CreateSink(directory.Path))
        {
            mainEvents = [.. main.Events];
        }

        DomainEvent<CounterEvent>[] forkEvents;
        using (var fork = CreateSink(directory.Path, "fork-1"))
        {
            forkEvents = [.. fork.Events];
        }

        Assert.Equal(3, mainEvents.Length);
        Assert.Equal(3, forkEvents.Length);
        AssertDomainEventsEqual(mainEvents[..2], forkEvents[..2]);
        Assert.NotEqual(mainEvents[^1].Payload, forkEvents[^1].Payload);
        Assert.Equal("main", mainEvents[^1].Payload.Route);
        Assert.Equal("fork-1", forkEvents[^1].Payload.Route);
    }

    [Fact]
    public void IdenticalEvents_ProduceIdenticalStoredLogicalPayloadBytes()
    {
        using var directory = new TemporaryJournalDirectory();
        string firstPath = Path.Combine(directory.Path, "first");
        string secondPath = Path.Combine(directory.Path, "second");
        DomainEvent<CounterEvent>[] events =
        [
            new DomainEvent<CounterEvent>(
                new LogicalTimestamp(new ModelTime(10), new Microstep(0)),
                EventCause.FromResolve(101, new EventCandidateId(1), new ModelTime(10), 0),
                CounterEventKinds.Advanced,
                new CounterEvent(1, 1, "deterministic")),
            new DomainEvent<CounterEvent>(
                new LogicalTimestamp(new ModelTime(20), new Microstep(0)),
                EventCause.FromExternalInput(1),
                new EventKind("counter.external", 3),
                new CounterEvent(2, 9, "deterministic")),
        ];

        IReadOnlyList<byte[]> firstPayloads = WriteAndReadPayloads(firstPath, events);
        IReadOnlyList<byte[]> secondPayloads = WriteAndReadPayloads(secondPath, events);

        Assert.Equal(firstPayloads.Count, secondPayloads.Count);
        for (int index = 0; index < firstPayloads.Count; index++)
        {
            Assert.Equal(firstPayloads[index], secondPayloads[index]);
        }

        AssertStorageDirectoriesEqual(
            Path.Combine(firstPath, "events"),
            Path.Combine(secondPath, "events"));
    }

    private static CounterWorld InitialWorld() => new(Total: 0, NextStep: 1, FinalStep: 4);

    private static SimulationLoop<CounterWorld, CounterCandidate, CounterEvent> CreateLoop(
        CounterReducer reducer) =>
        new([new CounterSystem()], reducer);

    private static AteliaJournalSink<CounterEvent> CreateSink(
        string path,
        string branchName = "main") =>
        new(path, SerializePayload, DeserializePayload, branchName);

    private static DomainEvent<CounterEvent> DivergentEvent(int delta, string route) =>
        new(
            new LogicalTimestamp(new ModelTime(30), new Microstep(0)),
            EventCause.FromResolve(101, new EventCandidateId(3), new ModelTime(30), 2),
            CounterEventKinds.Advanced,
            new CounterEvent(3, delta, route));

    private static IReadOnlyList<byte[]> WriteAndReadPayloads(
        string path,
        IReadOnlyList<DomainEvent<CounterEvent>> events)
    {
        using var sink = CreateSink(path);
        foreach (DomainEvent<CounterEvent> domainEvent in events)
        {
            sink.Append(domainEvent);
        }

        return sink.ReadStoredPayloads();
    }

    private static void AssertStorageDirectoriesEqual(string expectedRoot, string actualRoot)
    {
        string[] expectedFiles =
        [
            .. Directory.GetFiles(expectedRoot, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(expectedRoot, path))
                .Order(StringComparer.Ordinal),
        ];
        string[] actualFiles =
        [
            .. Directory.GetFiles(actualRoot, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(actualRoot, path))
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(expectedFiles, actualFiles);
        foreach (string relativePath in expectedFiles)
        {
            Assert.Equal(
                File.ReadAllBytes(Path.Combine(expectedRoot, relativePath)),
                File.ReadAllBytes(Path.Combine(actualRoot, relativePath)));
        }
    }

    private static byte[] SerializePayload(CounterEvent payload) =>
        JsonSerializer.SerializeToUtf8Bytes(payload);

    private static CounterEvent DeserializePayload(byte[] payload) =>
        JsonSerializer.Deserialize<CounterEvent>(payload)
        ?? throw new JsonException("Counter event payload cannot be null.");

    private static void AssertDomainEventsEqual(
        IReadOnlyList<DomainEvent<CounterEvent>> expected,
        IReadOnlyList<DomainEvent<CounterEvent>> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int index = 0; index < expected.Count; index++)
        {
            AssertDomainEventEqual(expected[index], actual[index]);
        }
    }

    private static void AssertDomainEventEqual(
        DomainEvent<CounterEvent> expected,
        DomainEvent<CounterEvent> actual)
    {
        Assert.Equal(expected.Timestamp, actual.Timestamp);
        Assert.Equal(expected.Cause, actual.Cause);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.Payload, actual.Payload);
    }
}