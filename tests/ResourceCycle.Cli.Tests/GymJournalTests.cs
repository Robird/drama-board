using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

public sealed class GymJournalTests {
    [Fact]
    public void Runs_and_branches_share_stable_identity_and_global_sequence_across_reopen() {
        using var files = new Files();
        string identity;
        using (var journal = GymJournal.Open(files.Path)) {
            identity = journal.StoreId;
            Assert.True(Guid.TryParseExact(identity, "N", out _));
            Assert.Equal("metadata", Assert.Single(journal.Records).Kind);
            var first = journal.Append("request", "run-1", "parent", "a", new { Text = "原材料\n🌳", Call = 1 });
            Assert.Equal(2, first.Sequence);
            Assert.Equal(1, first.FormatVersion);
            Assert.Equal("原材料\n🌳", first.Payload.GetProperty("Text").GetString());
        }
        using (var journal = GymJournal.Open(files.Path)) {
            Assert.Equal(identity, journal.StoreId);
            var second = journal.Append("request", "run-2", "child", "b", new { SourceThroughSequence = 1 });
            Assert.Equal(3, second.Sequence);
        }
        byte[] before = File.ReadAllBytes(files.Path);
        var snapshot = GymJournal.Read(files.Path);
        Assert.Equal(identity, snapshot.StoreId);
        Assert.Equal(new long[] { 1, 2, 3 }, snapshot.Records.Select(record => record.Sequence));
        Assert.Equal("parent", snapshot.Records[1].BranchId);
        Assert.Equal("child", snapshot.Records[2].BranchId);
        Assert.Equal(0, snapshot.DiscardedTailBytes);
        Assert.Equal(before, File.ReadAllBytes(files.Path));
    }

    [Fact]
    public void Empty_read_does_not_create_metadata_but_writer_initializes_it() {
        using var files = new Files();
        File.WriteAllBytes(files.Path, []);
        var empty = GymJournal.Read(files.Path);
        Assert.Null(empty.StoreId);
        Assert.Empty(empty.Records);
        Assert.Equal(0, new FileInfo(files.Path).Length);
        using var writer = GymJournal.Open(files.Path);
        Assert.Single(writer.Records);
        Assert.NotNull(GymJournal.Read(files.Path).StoreId);
    }

    [Fact]
    public void Read_reports_incomplete_tail_without_repair_and_writer_repairs_only_that_tail() {
        using var files = new Files();
        using (var journal = GymJournal.Open(files.Path)) {
            journal.Append("response", "run", "branch", "a", new { Result = "ok" });
        }
        long completeLength = new FileInfo(files.Path).Length;
        File.AppendAllText(files.Path, "{\"unfinished\":", new UTF8Encoding(false));
        byte[] withTail = File.ReadAllBytes(files.Path);
        var read = GymJournal.Read(files.Path);
        Assert.Equal(2, read.Records.Count);
        Assert.Equal(withTail.Length - completeLength, read.DiscardedTailBytes);
        Assert.Equal(withTail, File.ReadAllBytes(files.Path));
        using (var writer = GymJournal.Open(files.Path)) {
            Assert.Equal(read.DiscardedTailBytes, writer.DiscardedTailBytes);
            Assert.Equal(completeLength, new FileInfo(files.Path).Length);
            Assert.Equal(3, writer.Append("stop", "run", "branch", null, new { Reason = "done" }).Sequence);
        }
        Assert.Equal(0, GymJournal.Read(files.Path).DiscardedTailBytes);
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("sequence")]
    [InlineData("previous-hash")]
    [InlineData("future-version")]
    [InlineData("duplicate-metadata")]
    public void Completed_corruption_or_unsupported_format_fails_without_rewriting(string mode) {
        using var files = new Files();
        using (var journal = GymJournal.Open(files.Path)) {
            journal.Append("request", "run", "branch", "a", new { Input = "hello" });
        }
        string[] lines = File.ReadAllLines(files.Path);
        using var document = JsonDocument.Parse(lines[1]);
        var root = document.RootElement;
        long sequence = mode == "sequence" ? 99 : root.GetProperty("Sequence").GetInt64();
        string previous = mode == "previous-hash" ? "wrong" : root.GetProperty("PreviousHash").GetString()!;
        string payload = root.GetProperty("Payload").GetString()!;
        if (mode == "future-version") { payload = payload.Replace("\"FormatVersion\":1", "\"FormatVersion\":2", StringComparison.Ordinal); }
        if (mode == "duplicate-metadata") { payload = payload.Replace("\"Kind\":\"request\"", "\"Kind\":\"metadata\"", StringComparison.Ordinal); }
        string hash = mode == "checksum" ? "wrong" : Hash(sequence, previous, payload);
        lines[1] = JsonSerializer.Serialize(new { Sequence = sequence, PreviousHash = previous, Payload = payload, Hash = hash });
        File.WriteAllText(files.Path, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
        byte[] before = File.ReadAllBytes(files.Path);
        Assert.Throws<InvalidDataException>(() => GymJournal.Read(files.Path));
        Assert.Throws<InvalidDataException>(() => GymJournal.Open(files.Path));
        Assert.Equal(before, File.ReadAllBytes(files.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Write_or_flush_failure_retires_writer_without_installing_record(bool failFlush) {
        using var stream = new FailingStream();
        using var journal = GymJournal.CreateForTesting(stream);
        IReadOnlyList<GymRecord> installed = journal.Records;
        stream.FailFlush = failFlush;
        stream.FailWrite = !failFlush;
        Assert.Throws<IOException>(() => journal.Append("request", "run", "branch", "a", new { Text = "unconfirmed" }));
        Assert.True(journal.IsFaulted);
        Assert.Single(installed);
        Assert.Throws<InvalidOperationException>(() => journal.Append("request", "run", "branch", "a", new { }));
        Assert.Throws<InvalidOperationException>(() => journal.Records);
    }

    [Fact]
    public void Invalid_append_is_not_a_write_failure_and_does_not_advance_sequence() {
        using var files = new Files();
        using var journal = GymJournal.Open(files.Path);
        long length = new FileInfo(files.Path).Length;
        Assert.Throws<ArgumentException>(() => journal.Append("metadata", null, null, null, new { }));
        Assert.Throws<ArgumentException>(() => journal.Append("request", " ", null, null, new { }));
        Assert.False(journal.IsFaulted);
        Assert.Equal(length, new FileInfo(files.Path).Length);
        Assert.Equal(2, journal.Append("host-stop", "run", null, null, new { Reason = "cancelled" }).Sequence);
    }

    private static string Hash(long sequence, string previous, string payload) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(sequence.ToString(CultureInfo.InvariantCulture) + "\n" + previous + "\n" + payload)));

    private sealed class FailingStream : MemoryStream {
        public bool FailWrite { get; set; }
        public bool FailFlush { get; set; }
        public override void Write(ReadOnlySpan<byte> buffer) {
            if (FailWrite) {
                base.Write(buffer[..(buffer.Length / 2)]);
                throw new IOException("Injected partial write.");
            }
            base.Write(buffer);
        }
        public override void Flush() {
            if (FailFlush) { throw new IOException("Injected flush failure."); }
            base.Flush();
        }
    }

    private sealed class Files : IDisposable {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "drama-gym-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(_directory, "events.jsonl");
        public Files() { Directory.CreateDirectory(_directory); }
        public void Dispose() { Directory.Delete(_directory, recursive: true); }
    }
}
