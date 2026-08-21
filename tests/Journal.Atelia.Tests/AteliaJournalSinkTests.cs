using System.Text.Json;
using Atelia.EventJournal;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Journal.Atelia.Tests;

public sealed class AteliaJournalSinkTests
{
    private const string PayloadCodec = "counter-json/1";
    private const long DefaultLineageId = 101;

    [Fact]
    public void BatchEnvelope_RoundTripsOneHeaderAndOrderedFacts()
    {
        JournalBatch<CounterFact> expected = Batch(
            modelTime: 123,
            causalOrdinal: 4,
            cause: "counter/round-trip",
            new CounterFact(1, -7, "first"),
            new CounterFact(2, 11, "second"));

        byte[] envelope = JournalBatchEnvelopeCodec.Serialize(
            expected,
            PayloadCodec,
            SerializePayload);
        JournalBatch<CounterFact> actual = JournalBatchEnvelopeCodec.Deserialize(
            envelope,
            PayloadCodec,
            DeserializePayload);

        AssertBatchEqual(expected, actual);
        using JsonDocument document = JsonDocument.Parse(envelope);
        JsonElement root = document.RootElement;
        Assert.False(root.TryGetProperty("v", out _));
        Assert.Equal(123, root.GetProperty("instant").GetProperty("ms").GetInt64());
        Assert.Equal(4, root.GetProperty("instant").GetProperty("ordinal").GetInt64());
        Assert.Equal(expected.CauseKey.ToByteArray(), root.GetProperty("cause").GetBytesFromBase64());
        Assert.Equal(PayloadCodec, root.GetProperty("pc").GetString());
        Assert.Equal(2, root.GetProperty("facts").GetArrayLength());
        Assert.False(root.TryGetProperty("bi", out _));
        Assert.False(root.TryGetProperty("bc", out _));
    }

    [Fact]
    public void BatchEnvelope_EmptyFacts_IsRejectedBeforePayloadDeserialization()
    {
        bool deserializerCalled = false;
        byte[] envelope =
            """{"instant":{"ms":10,"ordinal":0},"cause":"AQ==","pc":"counter-json/1","facts":[]}"""u8.ToArray();

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            JournalBatchEnvelopeCodec.Deserialize<CounterFact>(
                envelope,
                PayloadCodec,
                payload =>
                {
                    deserializerCalled = true;
                    return DeserializePayload(payload);
                }));

        Assert.False(deserializerCalled);
        Assert.Contains("at least one fact", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendBatch_MultiFactTransitionAddsOneBatchAndOnePhysicalFrame()
    {
        using var directory = new TemporaryJournalDirectory();
        JournalBatch<CounterFact> batch = Batch(
            10,
            0,
            "counter/multi",
            new CounterFact(1, 1, "multi"),
            new CounterFact(2, 2, "multi"),
            new CounterFact(3, 3, "multi"));

        using (var sink = CreateSink(directory.Path))
        {
            sink.AppendBatch(batch);

            JournalBatch<CounterFact> committed = Assert.Single(sink.Batches);
            AssertBatchEqual(batch, committed);
            Assert.Equal(3, committed.Facts.Count);
        }

        using global::Atelia.EventJournal.EventJournal journal =
            global::Atelia.EventJournal.EventJournal.OpenReadOnlyExisting(directory.Path);
        RefId refId = journal.OpenBranch("main").Unwrap();
        EventAddress head = journal.GetHead(refId)
            ?? throw new InvalidDataException("The test branch has no head.");
        IReadOnlyList<EventAddress> chain = journal.ReadChronologicalChain(head, checkedRead: true).Unwrap();

        Assert.Equal(2, chain.Count); // lineage metadata + one complete transition frame
    }

    [Fact]
    public void AppendBatch_RequiresStrictlyIncreasingBatchInstants()
    {
        using var directory = new TemporaryJournalDirectory();
        using var sink = CreateSink(directory.Path);
        sink.AppendBatch(Batch(10, 0, "counter/first", new CounterFact(1, 1, "first")));

        Assert.Throws<InvalidOperationException>(() => sink.AppendBatch(
            Batch(10, 0, "counter/equal", new CounterFact(2, 2, "equal"))));
        Assert.Throws<InvalidOperationException>(() => sink.AppendBatch(
            Batch(9, 0, "counter/earlier", new CounterFact(3, 3, "earlier"))));

        Assert.Single(sink.Batches);
    }

    [Fact]
    public void OpenAndReplay_ReturnsCompleteBatchesInCommitOrder()
    {
        using var directory = new TemporaryJournalDirectory();
        JournalBatch<CounterFact>[] written =
        [
            Batch(10, 0, "counter/one", new CounterFact(1, 1, "one")),
            Batch(
                10,
                1,
                "counter/two",
                new CounterFact(2, 2, "two-a"),
                new CounterFact(3, 3, "two-b")),
            Batch(20, 0, "counter/three", new CounterFact(4, 4, "three")),
        ];

        using (var sink = CreateSink(directory.Path, lineageId: 11))
        {
            foreach (JournalBatch<CounterFact> batch in written)
            {
                sink.AppendBatch(batch);
            }
        }

        var replay = AteliaJournalSink<CounterFact>.OpenAndReplay(
            directory.Path,
            "main",
            11,
            PayloadCodec,
            SerializePayload,
            DeserializePayload);
        using (replay.Sink)
        {
            AssertBatchesEqual(written, replay.Batches);
            Assert.Equal([1, 2, 1], replay.Batches.Select(batch => batch.Facts.Count));
        }
    }

    [Fact]
    public void ForkBranch_UsesTransitionPrefixAndAllowsDivergentSuffixes()
    {
        using var directory = new TemporaryJournalDirectory();
        JournalBatch<CounterFact> shared = Batch(
            10,
            0,
            "counter/shared",
            new CounterFact(1, 1, "shared-a"),
            new CounterFact(2, 2, "shared-b"));

        using (var main = CreateSink(directory.Path, lineageId: 13))
        {
            main.AppendBatch(shared);
            main.AppendBatch(Batch(20, 0, "counter/skipped", new CounterFact(3, 3, "skipped")));
            main.ForkBranch("fork-1", prefixTransitionCount: 1, lineageId: 14);
        }

        using (var main = CreateSink(directory.Path, lineageId: 13))
        {
            main.AppendBatch(Batch(30, 0, "counter/main", new CounterFact(4, 4, "main")));
        }

        using (var fork = CreateSink(directory.Path, "fork-1", lineageId: 14))
        {
            fork.AppendBatch(Batch(30, 0, "counter/fork", new CounterFact(4, 40, "fork-1")));
        }

        JournalBatch<CounterFact>[] mainBatches;
        using (var reopenedMain = CreateSink(directory.Path, lineageId: 13))
        {
            mainBatches = [.. reopenedMain.Batches];
        }

        JournalBatch<CounterFact>[] forkBatches;
        using (var reopenedFork = CreateSink(directory.Path, "fork-1", lineageId: 14))
        {
            Assert.Equal(13, reopenedFork.ParentLineageId);
            Assert.Equal(1, reopenedFork.ForkPrefixTransitionCount);
            forkBatches = [.. reopenedFork.Batches];
        }

        Assert.Equal(3, mainBatches.Length);
        Assert.Equal(2, forkBatches.Length);
        AssertBatchEqual(mainBatches[0], forkBatches[0]);
        Assert.Equal("main", mainBatches[^1].Facts[0].Route);
        Assert.Equal("fork-1", forkBatches[^1].Facts[0].Route);
    }

    [Fact]
    public void ForkBranch_ZeroPrefixCreatesEmptyChildWithNewLineage()
    {
        using var directory = new TemporaryJournalDirectory();
        using (var main = CreateSink(directory.Path, lineageId: 21))
        {
            main.AppendBatch(Batch(10, 0, "counter/main", new CounterFact(1, 1, "main")));
            main.ForkBranch("empty-child", prefixTransitionCount: 0, lineageId: 22);
        }

        using var child = CreateSink(directory.Path, "empty-child", lineageId: 22);
        Assert.Empty(child.Batches);
        Assert.Equal(21, child.ParentLineageId);
        Assert.Equal(0, child.ForkPrefixTransitionCount);
    }

    [Fact]
    public void AppendBatch_SerializationFailureInMiddleLeavesVisibleHistoryUnchanged()
    {
        using var directory = new TemporaryJournalDirectory();
        using (var sink = new AteliaJournalSink<CounterFact>(
                   directory.Path,
                   DefaultLineageId,
                   PayloadCodec,
                   payload => payload.Route == "reject"
                       ? throw new InvalidOperationException("Injected serialization failure.")
                       : SerializePayload(payload),
                   DeserializePayload))
        {
            sink.AppendBatch(Batch(10, 0, "counter/visible", new CounterFact(1, 1, "visible")));
            JournalBatch<CounterFact> rejected = Batch(
                20,
                0,
                "counter/rejected",
                new CounterFact(2, 2, "would-be-prefix"),
                new CounterFact(3, 3, "reject"),
                new CounterFact(4, 4, "would-be-suffix"));

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => sink.AppendBatch(rejected));

            Assert.Contains("Injected serialization failure", exception.Message, StringComparison.Ordinal);
            Assert.Single(sink.Batches);
            Assert.Equal("visible", sink.Batches[0].Facts[0].Route);
        }

        using var reopened = CreateSink(directory.Path);
        Assert.Single(reopened.Batches);
        Assert.Equal("visible", reopened.Batches[0].Facts[0].Route);
    }

    [Fact]
    public void AppendBatch_ExceedsSingleFrameLimitLeavesVisibleHistoryUnchanged()
    {
        using var directory = new TemporaryJournalDirectory();
        using (var sink = new AteliaJournalSink<CounterFact>(
                   directory.Path,
                   DefaultLineageId,
                   PayloadCodec,
                   SerializePayload,
                   DeserializePayload,
                   journalOptions: new EventJournalOptions { MaxLogicalPayloadLength = 512 }))
        {
            JournalBatch<CounterFact> oversized = Batch(
                10,
                0,
                "counter/oversized",
                new CounterFact(1, 1, new string('x', 2_000)));

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => sink.AppendBatch(oversized));

            Assert.Contains("maximum", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(sink.Batches);
        }

        using global::Atelia.EventJournal.EventJournal journal =
            global::Atelia.EventJournal.EventJournal.OpenReadOnlyExisting(directory.Path);
        RefId refId = journal.OpenBranch("main").Unwrap();
        EventAddress head = journal.GetHead(refId)
            ?? throw new InvalidDataException("The test branch has no head.");
        Assert.Single(journal.ReadChronologicalChain(head, checkedRead: true).Unwrap());
    }

    [Fact]
    public void RefCasFailureLeavesActiveHistoryUnchangedAndOrphanInvisible()
    {
        using var directory = new TemporaryJournalDirectory();
        using (CreateSink(directory.Path))
        {
        }

        using (global::Atelia.EventJournal.EventJournal journal =
               global::Atelia.EventJournal.EventJournal.OpenOrCreate(directory.Path))
        {
            RefId refId = journal.OpenBranch("main").Unwrap();
            EventAddress expectedHead = journal.GetHead(refId)
                ?? throw new InvalidDataException("The test branch has no head.");
            EventAddress winnerAddress = journal.AppendEventFrame(
                expectedHead,
                JournalBatchEnvelopeCodec.Serialize(
                    Batch(10, 0, "counter/winner", new CounterFact(1, 1, "winner")),
                    PayloadCodec,
                    SerializePayload),
                AteliaJournalFrameKinds.JournalBatch,
                utcUnixTimeMilliseconds: 0).Unwrap();
            _ = journal.AdvanceRef(refId, expectedHead, winnerAddress).Unwrap();
            EventAddress orphanAddress = journal.AppendEventFrame(
                expectedHead,
                JournalBatchEnvelopeCodec.Serialize(
                    Batch(20, 0, "counter/stale", new CounterFact(2, 2, "stale")),
                    PayloadCodec,
                    SerializePayload),
                AteliaJournalFrameKinds.JournalBatch,
                utcUnixTimeMilliseconds: 0).Unwrap();

            var casFailure = journal.AdvanceRef(refId, expectedHead, orphanAddress);

            Assert.True(casFailure.IsFailure);
            Assert.Equal(winnerAddress, journal.GetHead(refId));
        }

        using var reopened = CreateSink(directory.Path);
        JournalBatch<CounterFact> committed = Assert.Single(reopened.Batches);
        Assert.Equal("winner", committed.Facts[0].Route);
    }

    [Fact]
    public void OrphanBatchFrameWithoutRefAdvanceIsInvisibleOnReopen()
    {
        using var directory = new TemporaryJournalDirectory();
        using (var sink = CreateSink(directory.Path))
        {
            sink.AppendBatch(Batch(10, 0, "counter/visible", new CounterFact(1, 1, "visible")));
        }

        AppendDirectBatchFrame(
            directory.Path,
            Batch(20, 0, "counter/orphan", new CounterFact(2, 2, "orphan")),
            advanceRef: false);

        using var reopened = CreateSink(directory.Path);
        JournalBatch<CounterFact> visible = Assert.Single(reopened.Batches);
        Assert.Equal("visible", visible.Facts[0].Route);
    }

    [Fact]
    public void VisibleMalformedBatchFailsOpenWithoutRewindingBranch()
    {
        using var directory = new TemporaryJournalDirectory();
        using (var sink = CreateSink(directory.Path))
        {
            sink.AppendBatch(Batch(10, 0, "counter/visible", new CounterFact(1, 1, "visible")));
        }

        byte[] malformed =
            """{"v":1,"instant":{"ms":20,"ordinal":0},"cause":"AQ==","pc":"counter-json/1","facts":[]}"""u8.ToArray();
        AppendDirectFrame(directory.Path, malformed, advanceRef: true);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => CreateSink(directory.Path));
        Assert.Contains("at least one fact", exception.Message, StringComparison.Ordinal);

        using global::Atelia.EventJournal.EventJournal journal =
            global::Atelia.EventJournal.EventJournal.OpenReadOnlyExisting(directory.Path);
        RefId refId = journal.OpenBranch("main").Unwrap();
        EventAddress head = journal.GetHead(refId)
            ?? throw new InvalidDataException("The test branch has no head.");
        Assert.Equal(3, journal.ReadChronologicalChain(head, checkedRead: true).Unwrap().Count);
    }

    [Fact]
    public void Reopen_WithDifferentPayloadCodecThrowsBeforeDeserializingFacts()
    {
        using var directory = new TemporaryJournalDirectory();
        using (var sink = CreateSink(directory.Path))
        {
            sink.AppendBatch(Batch(10, 0, "counter/visible", new CounterFact(1, 1, "visible")));
        }

        bool deserializerCalled = false;
        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            new AteliaJournalSink<CounterFact>(
                directory.Path,
                DefaultLineageId,
                "counter-json/2",
                SerializePayload,
                payload =>
                {
                    deserializerCalled = true;
                    return DeserializePayload(payload);
                }));

        Assert.False(deserializerCalled);
        Assert.Contains(PayloadCodec, exception.Message, StringComparison.Ordinal);
        Assert.Contains("counter-json/2", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reopen_WithDifferentLineageIdThrowsWithBothValues()
    {
        using var directory = new TemporaryJournalDirectory();
        using (CreateSink(directory.Path, lineageId: 71))
        {
        }

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreateSink(directory.Path, lineageId: 72));

        Assert.Contains("71", exception.Message, StringComparison.Ordinal);
        Assert.Contains("72", exception.Message, StringComparison.Ordinal);
    }

    private static AteliaJournalSink<CounterFact> CreateSink(
        string path,
        string branchName = "main",
        long lineageId = DefaultLineageId) =>
        new(path, lineageId, PayloadCodec, SerializePayload, DeserializePayload, branchName);

    private static JournalBatch<CounterFact> Batch(
        long modelTime,
        long causalOrdinal,
        string cause,
        params CounterFact[] facts) =>
        new(
            new LogicalInstant(new ModelTime(modelTime), causalOrdinal),
            new CandidateKey(cause),
            facts);

    private static void AppendDirectBatchFrame(
        string path,
        JournalBatch<CounterFact> batch,
        bool advanceRef) =>
        AppendDirectFrame(
            path,
            JournalBatchEnvelopeCodec.Serialize(batch, PayloadCodec, SerializePayload),
            advanceRef);

    private static void AppendDirectFrame(string path, byte[] framePayload, bool advanceRef)
    {
        using global::Atelia.EventJournal.EventJournal journal =
            global::Atelia.EventJournal.EventJournal.OpenOrCreate(path);
        RefId refId = journal.OpenBranch("main").Unwrap();
        EventAddress? head = journal.GetHead(refId);
        EventAddress address = journal.AppendEventFrame(
            head,
            framePayload,
            AteliaJournalFrameKinds.JournalBatch,
            utcUnixTimeMilliseconds: 0).Unwrap();
        if (advanceRef)
        {
            _ = journal.AdvanceRef(refId, head, address).Unwrap();
        }
    }

    private static byte[] SerializePayload(CounterFact payload) =>
        JsonSerializer.SerializeToUtf8Bytes(payload);

    private static CounterFact DeserializePayload(byte[] payload) =>
        JsonSerializer.Deserialize<CounterFact>(payload)
        ?? throw new JsonException("Counter fact payload cannot be null.");

    private static void AssertBatchesEqual(
        IReadOnlyList<JournalBatch<CounterFact>> expected,
        IReadOnlyList<JournalBatch<CounterFact>> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int index = 0; index < expected.Count; index++)
        {
            AssertBatchEqual(expected[index], actual[index]);
        }
    }

    private static void AssertBatchEqual(
        JournalBatch<CounterFact> expected,
        JournalBatch<CounterFact> actual)
    {
        Assert.Equal(expected.Instant, actual.Instant);
        Assert.Equal(expected.CauseKey, actual.CauseKey);
        Assert.Equal(expected.Facts, actual.Facts);
    }
}
