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
        using (var journal = FeedbackJournal.Open(files.Path, _ => throw new IOException("flush fixture"))) {
            Assert.Throws<IOException>(() => journal.Append(attempted));
            Assert.Throws<ObjectDisposedException>(() => journal.Append(Record("不得重试")));
        }
        // A thrown save may have left a complete line. Reopening observes it rather than claiming no write.
        Assert.Equal(attempted, Assert.Single(FeedbackJournal.Read(files.Path)));
        using (var reopened = FeedbackJournal.Open(files.Path)) { reopened.Append(Record("新回执")); }
        Assert.Equal(2, FeedbackJournal.Read(files.Path).Count);
    }

    private static FeedbackRecord Record(string text) => new(1, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
        "test-run", "a", FeedbackSource.HumanConsole, 1, text, null, null, null);

    private sealed class Files : IDisposable {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "feedback-journal-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(_root, "feedback.jsonl");
        public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, true); } }
    }
}
