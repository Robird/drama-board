using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DramaBoard.Playground;

public sealed class OperationJournalTests : IDisposable {
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "drama-host-journal-" + Guid.NewGuid().ToString("N"));
    private string JournalPath => PlaygroundHostJournal.JournalPath(_directory);
    public void Dispose() { if (Directory.Exists(_directory)) { Directory.Delete(_directory, recursive: true); } }

    [Fact]
    public void MetadataAndAllHostFactsShareOneSequenceAndJournalIdentity() {
        string identity;
        using (var journal = PlaygroundHostJournal.Open(JournalPath)) {
            identity = journal.JournalId;
            Assert.True(Guid.TryParseExact(identity, "N", out _));
            var audit = new PlaygroundOperationAudit(journal, "branch", "run");
            journal.Append("run-start", "run", "branch", null, null, new { Detailed = false });
            audit.Receive("batch", "call", "actor", 1, "console", ":look", "operation");
            audit.Complete("operation", PlaygroundOperationStatus.Succeeded, "世界回应");
            journal.Append("run-end", "run", "branch", null, null, new { Reason = "done" });
            Assert.Equal(new[] { "metadata", "run-start", "operation-received", "operation-completed", "run-end" }, journal.Records.Select(record => record.Kind));
            Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, journal.Records.Select(record => record.Sequence));
        }
        var snapshot = PlaygroundHostJournal.Read(JournalPath);
        Assert.Equal(identity, snapshot.JournalId);
        Assert.Equal(0, snapshot.DiscardedTailBytes);
        Assert.Equal("世界回应", snapshot.Records[3].ReadPayload<PlaygroundOperationCompletion>().Completed.Result);
        using var reopened = PlaygroundHostJournal.Open(JournalPath);
        Assert.Equal(identity, reopened.JournalId);
    }

    [Fact]
    public void PriorJsonEscapingIsHashedVerbatimAndNewAppendsRemainReadable() {
        const string input = "旧输入 <标签> & '引用'\n🌳";
        const string result = "原回答 <标签 属性=\"值\"> & '引用'\r\n第二行\t\\path 🌳";
        using (var initial = PlaygroundHostJournal.Open(JournalPath)) { }
        string metadata = File.ReadAllText(JournalPath);
        string previousHash = JsonDocument.Parse(metadata).RootElement.GetProperty("Hash").GetString()!;
        var legacyJson = new JsonSerializerOptions { Encoder = JavaScriptEncoder.Default };
        var received = new PlaygroundOperationReceipt("record", "batch", "call", "actor", 1, "console", input);
        var record = new PlaygroundHostRecord(1, 2, "operation-received", "run", "branch", "actor", 1,
            JsonSerializer.SerializeToElement(received, legacyJson));
        string payload = JsonSerializer.Serialize(record, legacyJson);
        string hash = Hash(2, previousHash, payload);
        string escapedLine = JsonSerializer.Serialize(new { Sequence = 2L, PreviousHash = previousHash, Payload = payload, Hash = hash }, legacyJson);
        File.AppendAllText(JournalPath, escapedLine + "\n", new UTF8Encoding(false));
        byte[] original = File.ReadAllBytes(JournalPath);
        Assert.Contains("\\u65E7", escapedLine);
        using (var journal = PlaygroundHostJournal.Open(JournalPath)) {
            var audit = new PlaygroundOperationAudit(journal, "branch", "next-run");
            Assert.Equal(received, Assert.Single(audit.Entries).Received);
            audit.Complete("record", PlaygroundOperationStatus.Succeeded, result);
            audit.Receive("batch", "call-2", "actor", 2, "console", "新输入 <标签> &\n下一行", "new");
        }
        Assert.Equal(original, File.ReadAllBytes(JournalPath)[..original.Length]);
        Assert.Contains("原回答 <标签", File.ReadAllLines(JournalPath)[2]);
        using var reopened = PlaygroundHostJournal.Open(JournalPath);
        var finalAudit = new PlaygroundOperationAudit(reopened, "branch", "last-run");
        Assert.Equal(result, finalAudit.Entries[0].Completed!.Result);
        Assert.Equal(new[] { "new" }, finalAudit.PendingOperationRecordIds);
        Assert.Equal("新输入 <标签> &\n下一行", finalAudit.Entries[1].Received.RawInput);
    }

    [Fact]
    public void ReceiptsPreserveProviderIdsAndSeparateBatches() {
        using var journal = PlaygroundHostJournal.CreateInMemory();
        var audit = new PlaygroundOperationAudit(journal, "branch", "run");
        var first = audit.Receive("batch-a", "provider.call:0", "actor", 1, "use", "use item.water");
        var second = audit.Receive("batch-b", "provider.call:0", "actor", 1, "use", "use item.water");
        Assert.NotEqual(first.OperationRecordId, second.OperationRecordId);
        Assert.Equal("provider.call:0", first.ToolCallId);
        var result = audit.Complete(first.OperationRecordId, PlaygroundOperationStatus.Succeeded, "accepted", "world:2", "event:2");
        Assert.Equal(first.ToolCallId, result.ToolCallId);
        Assert.Equal(new[] { second.OperationRecordId }, audit.PendingOperationRecordIds);
    }

    [Fact]
    public void SameRecordRetransmitsWithoutAppendingAndSavedResultCannotChange() {
        using var journal = PlaygroundHostJournal.CreateInMemory();
        var audit = new PlaygroundOperationAudit(journal, "branch", "run");
        var first = audit.Receive("batch", "call", "actor", 1, "wait", "wait 1", "record");
        int count = journal.Records.Count;
        Assert.Same(first, audit.Receive("batch", "call", "actor", 1, "wait", "wait 1", "record"));
        Assert.Equal(count, journal.Records.Count);
        Assert.Throws<InvalidOperationException>(() => audit.Receive("batch", "call", "actor", 1, "wait", "wait 1"));
        Assert.Throws<InvalidOperationException>(() => audit.Receive("batch", "call", "actor", 1, "wait", "wait 2", "record"));
        var result = audit.Complete("record", PlaygroundOperationStatus.Succeeded, "accepted");
        count = journal.Records.Count;
        Assert.Same(result, audit.Complete("record", PlaygroundOperationStatus.Succeeded, "accepted"));
        Assert.Equal(count, journal.Records.Count);
        Assert.Throws<InvalidOperationException>(() => audit.Complete("record", PlaygroundOperationStatus.Rejected, "different"));
        Assert.Single(audit.Entries);
    }

    [Fact]
    public void ReopenPreservesOriginalInputAndExactQueryResult() {
        PlaygroundOperationReceipt received;
        using (var journal = PlaygroundHostJournal.Open(JournalPath)) {
            var audit = new PlaygroundOperationAudit(journal, "branch", "run");
            received = audit.Receive("batch", "call_日本語", "actor", null, "console", " :help  \t");
            audit.Complete(received.OperationRecordId, PlaygroundOperationStatus.Succeeded, "原回答\n第二行 😀", "world:7");
        }
        using var reopened = PlaygroundHostJournal.Open(JournalPath);
        var recovered = new PlaygroundOperationAudit(reopened, "branch", "next-run");
        var entry = Assert.Single(recovered.Entries);
        Assert.Equal(received, entry.Received);
        Assert.Equal("原回答\n第二行 😀", entry.Completed!.Result);
        Assert.True(entry.Completed.ExactResult);
        Assert.Equal("world:7", entry.Completed.WorldVersion);
        Assert.Empty(recovered.PendingOperationRecordIds);
    }

    [Fact]
    public void MissingResultsReconcileOnlyFromCommittedFactsWithoutExecutingInput() {
        using (var journal = PlaygroundHostJournal.Open(JournalPath)) {
            var audit = new PlaygroundOperationAudit(journal, "branch", "run");
            audit.Receive("batch", "game-call", "actor", 1, "wait", "wait 1", "committed");
            audit.Receive("batch", "query-call", "actor", 1, "console", ":look", "query");
            audit.Receive("batch", "unpublished-call", "actor", 1, "use", "use item.water", "unpublished");
        }
        using (var reopened = PlaygroundHostJournal.Open(JournalPath)) {
            var audit = new PlaygroundOperationAudit(reopened, "branch", "recovery-run");
            audit.Reconcile([new("committed", "game-call", "actor", "等待已接受。", "world:2", "event:2", PlaygroundOperationDisposition.Succeeded)]);
            var committed = audit.Entries.Single(entry => entry.Received.OperationRecordId == "committed").Completed!;
            Assert.Equal(PlaygroundOperationStatus.Succeeded, committed.Status);
            Assert.False(committed.ExactResult);
            Assert.Equal("event:2", committed.DomainEvent);
            Assert.Equal("game-call", committed.ToolCallId);
            foreach (var entry in audit.Entries.Where(entry => entry.Received.OperationRecordId != "committed")) {
                Assert.Equal(PlaygroundOperationStatus.Interrupted, entry.Completed!.Status);
                Assert.False(entry.Completed.ExactResult);
                Assert.Null(entry.Completed.DomainEvent);
            }
            var completions = reopened.Records.Where(record => record.Kind == "operation-completed").ToArray();
            Assert.Equal(3, completions.Length);
            Assert.All(completions, record => {
                Assert.Equal("recovery-run", record.RunId);
                Assert.True(record.ReadPayload<PlaygroundOperationCompletion>().Recovered);
            });
        }
        using var final = PlaygroundHostJournal.Open(JournalPath);
        Assert.Empty(new PlaygroundOperationAudit(final, "branch", "next-run").PendingOperationRecordIds);
    }

    [Fact]
    public void BranchIndexesAllowSameIdsButNeverRecoverParentPending() {
        using var journal = PlaygroundHostJournal.CreateInMemory();
        var parent = new PlaygroundOperationAudit(journal, "parent", "parent-run");
        parent.Receive("batch", "call", "actor", 1, "wait", "wait 1", "shared-id");
        parent.Receive("batch", "parent-only", "actor", 1, "wait", "wait 1", "parent-pending");
        var child = new PlaygroundOperationAudit(journal, "child", "child-run");
        Assert.Empty(child.Entries);
        child.Receive("batch", "call", "actor", 1, "wait", "wait 2", "shared-id");
        child.Reconcile([]);
        Assert.Equal(PlaygroundOperationStatus.Interrupted, Assert.Single(child.Entries).Completed!.Status);
        Assert.All(parent.Entries, entry => Assert.Null(entry.Completed));
        Assert.Equal(new[] { "parent-pending", "shared-id" }, parent.PendingOperationRecordIds.Order(StringComparer.Ordinal));
        Assert.Single(journal.Records, record => record.Kind == "operation-completed");
        var secondParentAudit = new PlaygroundOperationAudit(journal, "parent", "later-run");
        Assert.Same(parent.Entries, secondParentAudit.Entries);
        Assert.Null(secondParentAudit.Entries[0].Completed);
    }

    [Fact]
    public void DomainIdentityMismatchFailsBeforeAnyRecoveryResultIsAppended() {
        using var journal = PlaygroundHostJournal.CreateInMemory();
        var audit = new PlaygroundOperationAudit(journal, "branch", "run");
        audit.Receive("batch", "call", "actor", 1, "wait", "wait 1", "record");
        int count = journal.Records.Count;
        Assert.Throws<InvalidDataException>(() => audit.Reconcile([new("record", "wrong-call", "actor", "accepted", "world", "event", PlaygroundOperationDisposition.Succeeded)]));
        Assert.Null(Assert.Single(audit.Entries).Completed);
        Assert.Throws<InvalidDataException>(() => audit.Reconcile([
            new("record", "call", "actor", "accepted", "world", "event", PlaygroundOperationDisposition.Succeeded),
            new("record", "call", "actor", "other", "world", "event", PlaygroundOperationDisposition.Succeeded)]));
        Assert.Equal(count, journal.Records.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedReceiptWriteOrFlushPreventsDispatchAndRetiresSharedWriter(bool failAtFlush) {
        using var stream = new FailingStream();
        using var journal = PlaygroundHostJournal.CreateForTesting(stream);
        var audit = new PlaygroundOperationAudit(journal, "branch", "run");
        var visibleRecords = journal.Records;
        var visibleEntries = audit.Entries;
        stream.Arm(failAtFlush);
        bool executed = false;
        Assert.Throws<IOException>(() => {
            audit.Receive("batch", "call", "actor", 1, "wait", "wait 1");
            executed = true;
        });
        Assert.False(executed);
        Assert.True(journal.IsFaulted);
        Assert.Single(visibleRecords); // Only the successfully flushed metadata was installed.
        Assert.Empty(visibleEntries);
        Assert.Throws<InvalidOperationException>(() => audit.Receive("batch", "call2", "actor", 1, "wait", "wait 1"));
        Assert.Throws<InvalidOperationException>(() => journal.Append("run-end", "run", "branch", null, null, new { Reason = "false success" }));
    }

    [Fact]
    public void FailedCompletionFlushDoesNotInstallResultOrMaskItsFailureDuringDispose() {
        var stream = new FailingStream();
        var journal = PlaygroundHostJournal.CreateForTesting(stream);
        var audit = new PlaygroundOperationAudit(journal, "branch", "run");
        audit.Receive("batch", "call", "actor", 1, "wait", "wait 1", "record");
        var entries = audit.Entries;
        stream.Arm(failAtFlush: true, failAtDispose: true);
        Assert.Throws<IOException>(() => audit.Complete("record", PlaygroundOperationStatus.Succeeded, "accepted"));
        Assert.Null(Assert.Single(entries).Completed);
        journal.Dispose();
        Assert.True(stream.Disposed);
    }

    [Fact]
    public void ReadOnlySnapshotReportsTornTailAndWriterAloneRepairsItBeforeAppend() {
        using (var journal = PlaygroundHostJournal.Open(JournalPath)) {
            new PlaygroundOperationAudit(journal, "branch", "run").Receive("batch", "call", "actor", 1, "wait", "wait 1", "record");
        }
        long completeLength = new FileInfo(JournalPath).Length;
        byte[] torn = Encoding.UTF8.GetBytes("{\"Sequence\":3,\"Payload\":\"unfinished");
        using (var tail = new FileStream(JournalPath, FileMode.Append, FileAccess.Write)) { tail.Write(torn); }
        byte[] before = File.ReadAllBytes(JournalPath);
        var snapshot = PlaygroundHostJournal.Read(JournalPath);
        Assert.Equal(torn.Length, snapshot.DiscardedTailBytes);
        Assert.Equal(2, snapshot.Records.Count);
        Assert.Equal(before, File.ReadAllBytes(JournalPath));
        using (var reopened = PlaygroundHostJournal.Open(JournalPath)) {
            Assert.Equal(torn.Length, reopened.DiscardedTailBytes);
            Assert.Equal(completeLength, new FileInfo(JournalPath).Length);
            new PlaygroundOperationAudit(reopened, "branch", "later-run").Complete("record", PlaygroundOperationStatus.Succeeded, "accepted");
        }
        Assert.Equal(0, PlaygroundHostJournal.Read(JournalPath).DiscardedTailBytes);
    }

    [Fact]
    public void CompletedCorruptLineFailsClosedForReadAndWriteWithoutRepair() {
        using (var journal = PlaygroundHostJournal.Open(JournalPath)) {
            var audit = new PlaygroundOperationAudit(journal, "branch", "run");
            audit.Receive("batch", "call", "actor", 1, "wait", "wait 1", "record");
            audit.Complete("record", PlaygroundOperationStatus.Succeeded, "accepted");
        }
        string corrupt = File.ReadAllText(JournalPath).Replace("accepted", "tampered", StringComparison.Ordinal);
        File.WriteAllText(JournalPath, corrupt, new UTF8Encoding(false));
        Assert.Throws<InvalidDataException>(() => PlaygroundHostJournal.Read(JournalPath));
        Assert.Throws<InvalidDataException>(() => PlaygroundHostJournal.Open(JournalPath));
        Assert.Equal(corrupt, File.ReadAllText(JournalPath));
    }

    [Fact]
    public void DeletingAnInteriorRecordBreaksTheCommonSequenceAndChain() {
        using (var journal = PlaygroundHostJournal.Open(JournalPath)) {
            var audit = new PlaygroundOperationAudit(journal, "branch", "run");
            audit.Receive("batch", "call1", "actor", 1, "wait", "wait 1", "one");
            audit.Complete("one", PlaygroundOperationStatus.Succeeded, "accepted");
            audit.Receive("batch", "call2", "actor", 1, "wait", "wait 2", "two");
        }
        string[] lines = File.ReadAllLines(JournalPath);
        File.WriteAllText(JournalPath, lines[0] + "\n" + lines[2] + "\n" + lines[3] + "\n", new UTF8Encoding(false));
        Assert.Throws<InvalidDataException>(() => PlaygroundHostJournal.Open(JournalPath));
    }

    [Fact]
    public void InvalidOperationsAndUnknownKindsFailBeforeWritingOrFaultingJournal() {
        using var journal = PlaygroundHostJournal.CreateInMemory();
        int count = journal.Records.Count;
        Assert.Throws<InvalidDataException>(() => journal.Append("future-kind", "run", "branch", null, null, new { Value = 1 }));
        Assert.Throws<InvalidDataException>(() => journal.Append("operation-completed", "run", "branch", "actor", 1,
            new PlaygroundOperationCompletion("missing", new("call", PlaygroundOperationStatus.Succeeded, "fake", null, null, true))));
        Assert.Equal(count, journal.Records.Count);
        Assert.False(journal.IsFaulted);
        new PlaygroundOperationAudit(journal, "branch", "run").Receive("batch", "call", "actor", 1, "wait", "wait 1", "valid");
        Assert.Equal(count + 1, journal.Records.Count);
    }

    [Fact]
    public void DurableWriterExcludesSecondWriterWhileReadOnlySnapshotIsAllowed() {
        using (var first = PlaygroundHostJournal.Open(JournalPath)) {
            Assert.Throws<IOException>(() => PlaygroundHostJournal.Open(JournalPath));
            Assert.Equal(first.JournalId, PlaygroundHostJournal.Read(JournalPath).JournalId);
        }
        using var reopened = PlaygroundHostJournal.Open(JournalPath);
        Assert.Empty(new PlaygroundOperationAudit(reopened, "branch", "run").Entries);
    }

    private static string Hash(long sequence, string previousHash, string payload) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(sequence.ToString(CultureInfo.InvariantCulture) + "\n" + previousHash + "\n" + payload)));

    private sealed class FailingStream : MemoryStream {
        private bool _armed;
        private bool _failAtFlush;
        private bool _failAtDispose;
        public bool Disposed { get; private set; }
        public void Arm(bool failAtFlush, bool failAtDispose = false) {
            _armed = true;
            _failAtFlush = failAtFlush;
            _failAtDispose = failAtDispose;
        }
        public override void Write(ReadOnlySpan<byte> buffer) {
            if (_armed && !_failAtFlush) { throw new IOException("Injected write failure."); }
            base.Write(buffer);
        }
        public override void Flush() {
            if (_armed && _failAtFlush) { throw new IOException("Injected durable-flush failure."); }
            base.Flush();
        }
        protected override void Dispose(bool disposing) {
            base.Dispose(disposing);
            Disposed = true;
            if (_failAtDispose) { throw new IOException("Injected cleanup failure."); }
        }
    }
}
