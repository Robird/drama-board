using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DramaBoard.ResourceCycle;

public sealed class OperationJournalTests : IDisposable {
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "drama-operation-journal-" + Guid.NewGuid().ToString("N"));
    private string JournalPath => Path.Combine(_directory, "operations.jsonl");

    public OperationJournalTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void LegacyEscapedReceiptCanBeCompletedAndAppendedWithReadableJsonl() {
        const string input = "旧输入 <标签> & '引用'\n🌳";
        const string result = "原回答 <标签 属性=\"值\"> & '引用'\r\n第二行\t\\path 🌳";
        // Reproduce the old writer to prove that changing the encoder preserves its checksum chain.
        var legacyJson = new JsonSerializerOptions { Encoder = JavaScriptEncoder.Default };
        var received = new CycleOperationReceipt("legacy", "batch", "call", "actor", 1, "console", input);
        string payload = JsonSerializer.Serialize(new {
            Received = received, OperationRecordId = (string?)null, Completed = (CycleOperationResult?)null
        }, legacyJson);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("1\n\n" + payload)));
        string legacyLine = JsonSerializer.Serialize(new { Sequence = 1L, PreviousHash = "", Payload = payload, Hash = hash }, legacyJson);
        File.WriteAllText(JournalPath, legacyLine + "\n", new UTF8Encoding(false));
        byte[] legacyBytes = File.ReadAllBytes(JournalPath);
        Assert.Contains("\\u65E7", legacyLine);

        using (var journal = CycleOperationJournal.Open(JournalPath)) {
            Assert.Equal(received, Assert.Single(journal.Entries).Received);
            journal.Complete("legacy", CycleOperationStatus.Succeeded, result);
            journal.Receive("batch", "call-2", "actor", 2, "console", "新输入 <标签> &\n下一行", "new");
        }

        Assert.Equal(legacyBytes, File.ReadAllBytes(JournalPath)[..legacyBytes.Length]);
        string[] lines = File.ReadAllLines(JournalPath);
        Assert.Equal(3, lines.Length);
        Assert.Contains("原回答 <标签", lines[1]);
        Assert.Contains("新输入 <标签> &", lines[2]);
        using var reopened = CycleOperationJournal.Open(JournalPath);
        var entry = Assert.Single(reopened.Entries, entry => entry.Received.OperationRecordId == "legacy");
        Assert.Equal(received, entry.Received);
        Assert.Equal(result, entry.Completed!.Result);
        Assert.Equal(["new"], reopened.PendingOperationRecordIds);
        Assert.Equal("新输入 <标签> &\n下一行", reopened.Entries.Single(entry => entry.Received.OperationRecordId == "new").Received.RawInput);
    }

    [Fact]
    public void ReceiptsPreserveProviderIdsAndSeparateBatches() {
        using var journal = CycleOperationJournal.CreateInMemory();
        var first = journal.Receive("batch-a", "provider.call:0", "actor", 1, "use", "use item.water");
        var second = journal.Receive("batch-b", "provider.call:0", "actor", 1, "use", "use item.water");
        Assert.NotEqual(first.OperationRecordId, second.OperationRecordId);
        Assert.Equal("provider.call:0", first.ToolCallId);
        var result = journal.Complete(first.OperationRecordId, CycleOperationStatus.Succeeded, "accepted", "world:2", "event:2");
        Assert.Equal(first.ToolCallId, result.ToolCallId);
        Assert.Equal([second.OperationRecordId], journal.PendingOperationRecordIds);
    }

    [Fact]
    public void SameRecordRetransmitsButBatchDuplicateAndChangedParametersAreRejected() {
        using var journal = CycleOperationJournal.CreateInMemory();
        var first = journal.Receive("batch", "call", "actor", 1, "wait", "wait 1", "record");
        Assert.Same(first, journal.Receive("batch", "call", "actor", 1, "wait", "wait 1", "record"));
        Assert.Throws<InvalidOperationException>(() => journal.Receive("batch", "call", "actor", 1, "wait", "wait 1"));
        Assert.Throws<InvalidOperationException>(() => journal.Receive("batch", "call", "actor", 1, "wait", "wait 2", "record"));
        var result = journal.Complete("record", CycleOperationStatus.Succeeded, "accepted");
        Assert.Same(result, journal.Complete("record", CycleOperationStatus.Succeeded, "accepted"));
        Assert.Throws<InvalidOperationException>(() => journal.Complete("record", CycleOperationStatus.Rejected, "different"));
        Assert.Single(journal.Entries);
    }

    [Fact]
    public void ReopenPreservesOriginalInputAndExactQueryResult() {
        CycleOperationReceipt received;
        using (var journal = CycleOperationJournal.Open(JournalPath)) {
            received = journal.Receive("batch", "call_日本語", "actor", null, "console", " :help  \t");
            journal.Complete(received.OperationRecordId, CycleOperationStatus.Succeeded, "原回答\n第二行 😀", "world:7");
        }
        using var reopened = CycleOperationJournal.Open(JournalPath);
        var entry = Assert.Single(reopened.Entries);
        Assert.Equal(received, entry.Received);
        Assert.Equal("原回答\n第二行 😀", entry.Completed!.Result);
        Assert.True(entry.Completed.ExactResult);
        Assert.Equal("world:7", entry.Completed.WorldVersion);
        Assert.Empty(reopened.PendingOperationRecordIds);
    }

    [Fact]
    public void MissingResultsReconcileOnlyFromCommittedDomainFactsAndNeverExecuteInput() {
        using (var journal = CycleOperationJournal.Open(JournalPath)) {
            journal.Receive("batch", "game-call", "actor", 1, "wait", "wait 1", "committed");
            journal.Receive("batch", "query-call", "actor", 1, "console", ":look", "query");
            journal.Receive("batch", "unpublished-call", "actor", 1, "use", "use item.water", "unpublished");
        }
        using (var reopened = CycleOperationJournal.Open(JournalPath)) {
            reopened.Reconcile([new("committed", "game-call", "actor", "等待已接受。", "world:2", "event:2", CycleOperationDisposition.Succeeded)]);
            var committed = reopened.Entries.Single(e => e.Received.OperationRecordId == "committed").Completed!;
            Assert.Equal(CycleOperationStatus.Succeeded, committed.Status);
            Assert.False(committed.ExactResult);
            Assert.Equal("event:2", committed.DomainEvent);
            Assert.Equal("game-call", committed.ToolCallId);
            foreach (var entry in reopened.Entries.Where(e => e.Received.OperationRecordId != "committed")) {
                Assert.Equal(CycleOperationStatus.Interrupted, entry.Completed!.Status);
                Assert.False(entry.Completed.ExactResult);
                Assert.Null(entry.Completed.DomainEvent);
            }
        }
        using var final = CycleOperationJournal.Open(JournalPath);
        Assert.Empty(final.PendingOperationRecordIds);
        Assert.Equal(3, final.Entries.Count);
    }

    [Fact]
    public void DomainIdentityMismatchFailsBeforeAnyRecoveryResultsAreWritten() {
        using var journal = CycleOperationJournal.CreateInMemory();
        journal.Receive("batch", "call", "actor", 1, "wait", "wait 1", "record");
        Assert.Throws<InvalidDataException>(() => journal.Reconcile([new("record", "wrong-call", "actor", "accepted", "world", "event", CycleOperationDisposition.Succeeded)]));
        Assert.Null(Assert.Single(journal.Entries).Completed);
        Assert.Throws<InvalidDataException>(() => journal.Reconcile([
            new("record", "call", "actor", "accepted", "world", "event", CycleOperationDisposition.Succeeded),
            new("record", "call", "actor", "other", "world", "event", CycleOperationDisposition.Succeeded)]));
        Assert.Null(Assert.Single(journal.Entries).Completed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedReceiptWriteOrFlushPreventsDispatchAndRetiresJournal(bool failAtFlush) {
        using var stream = new FailingStream(failAtFlush);
        using var journal = CycleOperationJournal.CreateForTesting(stream);
        bool executed = false;
        Assert.Throws<IOException>(() => {
            journal.Receive("batch", "call", "actor", 1, "wait", "wait 1");
            executed = true;
        });
        Assert.False(executed);
        Assert.True(journal.IsFaulted);
        Assert.Throws<InvalidOperationException>(() => journal.Receive("batch", "call2", "actor", 1, "wait", "wait 1"));
    }

    [Fact]
    public void IncompleteTailIsIdentifiedAndRemovedBeforeAppending() {
        using (var journal = CycleOperationJournal.Open(JournalPath)) {
            journal.Receive("batch", "call", "actor", 1, "wait", "wait 1", "record");
        }
        long completeLength = new FileInfo(JournalPath).Length;
        byte[] torn = Encoding.UTF8.GetBytes("{\"Sequence\":2,\"Payload\":\"unfinished");
        using (var tail = new FileStream(JournalPath, FileMode.Append, FileAccess.Write)) { tail.Write(torn); }
        using (var reopened = CycleOperationJournal.Open(JournalPath)) {
            Assert.Equal(torn.Length, reopened.DiscardedTailBytes);
            Assert.Equal(completeLength, new FileInfo(JournalPath).Length);
            reopened.Complete("record", CycleOperationStatus.Succeeded, "accepted");
        }
        using var final = CycleOperationJournal.Open(JournalPath);
        Assert.Equal(0, final.DiscardedTailBytes);
        Assert.Equal("accepted", Assert.Single(final.Entries).Completed!.Result);
    }

    [Fact]
    public void CompletedCorruptLineFailsClosedAndIsNotTreatedAsTornTail() {
        using (var journal = CycleOperationJournal.Open(JournalPath)) {
            journal.Receive("batch", "call", "actor", 1, "wait", "wait 1", "record");
            journal.Complete("record", CycleOperationStatus.Succeeded, "accepted");
        }
        string corrupt = File.ReadAllText(JournalPath).Replace("accepted", "tampered", StringComparison.Ordinal);
        File.WriteAllText(JournalPath, corrupt, new UTF8Encoding(false));
        Assert.Throws<InvalidDataException>(() => CycleOperationJournal.Open(JournalPath));
        Assert.Equal(corrupt, File.ReadAllText(JournalPath));
    }

    [Fact]
    public void DeletingAnInteriorRecordBreaksSequenceAndHashChain() {
        using (var journal = CycleOperationJournal.Open(JournalPath)) {
            journal.Receive("batch", "call1", "actor", 1, "wait", "wait 1", "one");
            journal.Complete("one", CycleOperationStatus.Succeeded, "accepted");
            journal.Receive("batch", "call2", "actor", 1, "wait", "wait 2", "two");
        }
        string[] lines = File.ReadAllLines(JournalPath);
        File.WriteAllText(JournalPath, lines[0] + "\n" + lines[2] + "\n", new UTF8Encoding(false));
        Assert.Throws<InvalidDataException>(() => CycleOperationJournal.Open(JournalPath));
    }

    [Fact]
    public void DurableJournalExcludesSecondWriterUntilDisposed() {
        using (var first = CycleOperationJournal.Open(JournalPath)) {
            Assert.Throws<IOException>(() => CycleOperationJournal.Open(JournalPath));
        }
        using var reopened = CycleOperationJournal.Open(JournalPath);
        Assert.Empty(reopened.Entries);
    }

    private sealed class FailingStream(bool failAtFlush) : MemoryStream {
        public override void Write(ReadOnlySpan<byte> buffer) {
            if (failAtFlush) { base.Write(buffer); }
            else { throw new IOException("Injected write failure."); }
        }
        public override void Flush() => throw new IOException("Injected durable-flush failure.");
    }
}
