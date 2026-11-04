using System.Text;
using System.Text.Json.Nodes;

namespace DramaBoard.Playground.Cli.Tests;

public sealed class FeedbackJournalTests {
    [Fact]
    public void Active_writer_supports_multiple_appends_reads_and_reopen() {
        using var files = new Files();
        FeedbackRecord first = Record("一"), second = Record("二"), third = Record("三");
        using (var writer = FeedbackJournal.Open(files.Path)) {
            writer.Append(first);
            Assert.Equal(first, Assert.Single(FeedbackJournal.Read(files.Path)));
            writer.Append(second);
            Assert.Equal(new[] { first, second }, FeedbackJournal.Read(files.Path));
        }
        using (var writer = FeedbackJournal.Open(files.Path)) { writer.Append(third); }
        Assert.Equal(new[] { first, second, third }, FeedbackJournal.Read(files.Path));
    }

    [Fact]
    public void Missing_file_is_lazy_and_read_only_queries_never_repair_tail() {
        using var files = new Files();
        Assert.Empty(FeedbackJournal.Read(files.Path));
        using (var unused = FeedbackJournal.Open(files.Path)) { }
        Assert.False(File.Exists(files.Path));
        FeedbackRecord first = Record("第一条\n反馈");
        using (var journal = FeedbackJournal.Open(files.Path)) { Assert.Equal(first.FeedbackId, journal.Append(first)); }
        File.AppendAllText(files.Path, "{\"Text\":\"截断", Encoding.UTF8);
        byte[] before = File.ReadAllBytes(files.Path);
        var snapshot = FeedbackJournal.ReadSnapshot(files.Path);
        Assert.Equal(first, Assert.Single(snapshot.Records));
        Assert.True(snapshot.IgnoredTailBytes > 0);
        Assert.Equal(before, File.ReadAllBytes(files.Path));
        FeedbackRecord second = Record("重开后");
        using (var journal = FeedbackJournal.Open(files.Path)) { journal.Append(second); }
        Assert.Equal(new[] { first, second }, FeedbackJournal.Read(files.Path));
        Assert.Equal(0, FeedbackJournal.ReadSnapshot(files.Path).IgnoredTailBytes);
        Assert.Contains("HumanConsole", File.ReadAllText(files.Path));
    }

    [Fact]
    public void Complete_corruption_and_missing_metadata_are_rejected_without_repair() {
        using var files = new Files();
        using (var journal = FeedbackJournal.Open(files.Path)) { journal.Append(Record("有效")); }
        JsonNode node = JsonNode.Parse(File.ReadAllText(files.Path))!;
        node.AsObject().Remove("ContextSequence");
        File.WriteAllText(files.Path, node.ToJsonString() + "\n");
        byte[] before = File.ReadAllBytes(files.Path);
        Assert.Throws<InvalidDataException>(() => FeedbackJournal.Read(files.Path));
        using var reopened = FeedbackJournal.Open(files.Path);
        Assert.Throws<InvalidDataException>(() => reopened.Append(Record("不应覆盖")));
        Assert.Equal(before, File.ReadAllBytes(files.Path));
        Assert.Throws<ObjectDisposedException>(() => reopened.Append(Record("已退役")));
    }

    [Fact]
    public void Flush_failure_gives_no_receipt_and_retires_writer_until_reopen() {
        using var files = new Files();
        FeedbackRecord attempted = Record("结果未知");
        using (var journal = FeedbackJournal.OpenForTesting(files.Path, path => new FaultingFileStream(path, "flush"))) {
            Assert.Throws<IOException>(() => journal.Append(attempted));
            Assert.Throws<ObjectDisposedException>(() => journal.Append(Record("不得重试")));
        }
        // A thrown save may have left a complete line. Reopening observes it rather than claiming no write.
        Assert.Equal(attempted, Assert.Single(FeedbackJournal.Read(files.Path)));
        using (var reopened = FeedbackJournal.Open(files.Path)) { reopened.Append(Record("新回执")); }
        Assert.Equal(2, FeedbackJournal.Read(files.Path).Count);
    }

    [Theory]
    [InlineData("body", false)]
    [InlineData("body", true)]
    [InlineData("newline", false)]
    [InlineData("newline", true)]
    [InlineData("flush", false)]
    [InlineData("flush", true)]
    public void Append_and_cleanup_failures_preserve_original_error_and_retire_writer(string failAt, bool nonIoCleanup) {
        using var files = new Files();
        FeedbackRecord attempted = Record("结果未知：双失败");
        Exception cleanupError = nonIoCleanup ? new InvalidOperationException("cleanup fixture") : new IOException("cleanup fixture");
        FaultingFileStream? stream = null;
        int opens = 0;
        using var journal = FeedbackJournal.OpenForTesting(files.Path, path => {
            opens++;
            return stream = new FaultingFileStream(path, failAt, cleanupError);
        });
        Assert.Equal(0, opens);
        string? receipt = null;
        IOException error = Assert.Throws<IOException>(() => receipt = journal.Append(attempted));
        Assert.Same(stream!.AppendError, error);
        Assert.Contains(nameof(FaultingFileStream), error.StackTrace);
        Assert.Null(receipt);
        Assert.Equal(1, stream.DisposeAttempts);
        byte[] afterFailure = File.ReadAllBytes(files.Path);

        Assert.Throws<ObjectDisposedException>(() => journal.Append(Record("不得重试")));
        journal.Dispose();
        journal.Dispose();
        Assert.Equal(1, opens);
        Assert.Equal(1, stream.DisposeAttempts);
        Assert.Equal(afterFailure, File.ReadAllBytes(files.Path));

        // These fixtures deliberately leave a tail or a complete line; neither outcome is a receipt.
        Assert.Equal(failAt == "flush" ? new[] { attempted } : Array.Empty<FeedbackRecord>(), FeedbackJournal.Read(files.Path));
        FeedbackRecord next = Record("重新打开后的回执");
        using (var reopened = FeedbackJournal.Open(files.Path)) { Assert.Equal(next.FeedbackId, reopened.Append(next)); }
        Assert.Equal(failAt == "flush" ? new[] { attempted, next } : new[] { next }, FeedbackJournal.Read(files.Path));
    }

    [Fact]
    public void Using_cleanup_does_not_replace_the_escaping_append_error() {
        using var files = new Files();
        FaultingFileStream? stream = null;
        IOException error = Assert.Throws<IOException>(() => {
            using var journal = FeedbackJournal.OpenForTesting(files.Path,
                path => stream = new FaultingFileStream(path, "flush", new IOException("cleanup fixture")));
            journal.Append(Record("作用域清理"));
        });
        Assert.Same(stream!.AppendError, error);
        Assert.Contains(nameof(FaultingFileStream), error.StackTrace);
        Assert.Equal(1, stream.DisposeAttempts);
    }

    [Fact]
    public void Standalone_dispose_failure_is_reported_once_and_writer_stays_retired() {
        using var files = new Files();
        var cleanupError = new IOException("standalone cleanup fixture");
        FaultingFileStream? stream = null;
        using var journal = FeedbackJournal.OpenForTesting(files.Path,
            path => stream = new FaultingFileStream(path, cleanupError: cleanupError));
        FeedbackRecord saved = Record("已经刷盘");
        Assert.Equal(saved.FeedbackId, journal.Append(saved));

        Assert.Same(cleanupError, Assert.Throws<IOException>(journal.Dispose));
        Assert.Throws<ObjectDisposedException>(() => journal.Append(Record("已经关闭")));
        journal.Dispose();
        Assert.Equal(1, stream!.DisposeAttempts);
        Assert.Equal(saved, Assert.Single(FeedbackJournal.Read(files.Path)));
    }

    private sealed class FaultingFileStream(string path, string? failAt = null, Exception? cleanupError = null)
        : FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read) {
        public IOException AppendError { get; } = new("append fixture: " + failAt);
        public int DisposeAttempts { get; private set; }

        public override void Write(ReadOnlySpan<byte> buffer) {
            if (failAt == "body") {
                base.Write(buffer[..5]);
                throw AppendError;
            }
            base.Write(buffer);
        }

        public override void WriteByte(byte value) {
            if (failAt == "newline") { throw AppendError; }
            base.WriteByte(value);
        }

        public override void Flush(bool flushToDisk) {
            base.Flush(flushToDisk);
            if (failAt == "flush") { throw AppendError; }
        }

        protected override void Dispose(bool disposing) {
            if (disposing) { DisposeAttempts++; }
            base.Dispose(disposing);
            if (disposing && cleanupError is not null) { throw cleanupError; }
        }
    }

    private static FeedbackRecord Record(string text) => new(1, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
        "test-run", "a", FeedbackSource.HumanConsole, 1, text, null, null, null);

    private sealed class Files : IDisposable {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "feedback-journal-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(_root, "feedback.jsonl");
        public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, true); } }
    }
}
