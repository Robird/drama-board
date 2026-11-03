using Atelia.Completion.Abstractions;

namespace DramaBoard.Playground.Cli.Tests;

public sealed class LookaheadRawAuditTests {
    [Fact]
    public void First_lookahead_event_lazily_creates_a_readable_raw_file() {
        using var files = new Files();
        using (var audit = new LlmRawAudit(files.Path, "lookahead-run")) {
            Assert.False(File.Exists(files.Path));
            Assert.Equal(2, audit.RecordLookahead("lookahead-physical-request", new { RequestId = "physical-one" }));
        }
        LlmRawAuditSnapshot snapshot = LlmRawAudit.Read(files.Path);
        Assert.Equal(new[] { "metadata", "lookahead-event" }, snapshot.Records.Select(record => record.Kind));
        Assert.Equal("lookahead-physical-request", snapshot.Records[1].Payload.GetProperty("Kind").GetString());
        Assert.Equal("physical-one", snapshot.Records[1].Payload.GetProperty("Data").GetProperty("RequestId").GetString());
        Assert.Equal(0, snapshot.DiscardedTailBytes);
    }

    [Fact]
    public async Task Concurrent_trace_and_lookahead_writes_preserve_the_complete_hash_chain() {
        using var files = new Files();
        using (var audit = new LlmRawAudit(files.Path, "concurrent-run")) {
            audit.Trace(Request());
            await Task.WhenAll(Enumerable.Range(0, 80).Select(index => Task.Run(() => {
                if (index % 2 == 0) { audit.RecordLookahead("lookahead-physical-result", new { Index = index }); }
                else { audit.Trace(Request() with { RequestId = "request-" + index }); }
            })));
        }
        // Read independently recomputes every sequence and chained checksum.
        LlmRawAuditSnapshot snapshot = LlmRawAudit.Read(files.Path);
        Assert.Equal(82, snapshot.Records.Count);
        Assert.Equal(Enumerable.Range(1, 82).Select(value => (long)value), snapshot.Records.Select(record => record.Sequence));
        Assert.Equal(40, snapshot.Records.Count(record => record.Kind == "lookahead-event"));
        Assert.Equal(41, snapshot.Records.Count(record => record.Kind == "model-request"));
        Assert.Equal(0, snapshot.DiscardedTailBytes);
    }

    [Fact]
    public void Ordinary_raw_records_remain_readable_without_lookahead() {
        using var files = new Files();
        using (var audit = new LlmRawAudit(files.Path, "ordinary-run")) {
            audit.Trace(Request());
            audit.Trace(Request() with { Kind = LlmControllerTraceKind.Failure, ProtocolError = "cancelled" });
        }
        Assert.Equal(new[] { "metadata", "model-request", "model-failure" },
            LlmRawAudit.Read(files.Path).Records.Select(record => record.Kind));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Lookahead_write_or_flush_failure_retires_all_writer_entry_points(bool failAtFlush) {
        var audit = LlmRawAudit.CreateForTesting(new FailingStream(failAtFlush), "failed-run");
        Assert.Throws<IOException>(() => audit.RecordLookahead("lookahead-physical-request", new { RequestId = "one" }));
        Assert.True(audit.IsFaulted);
        Assert.Throws<InvalidOperationException>(() => audit.RecordLookahead("lookahead-discard", new { }));
        Assert.Throws<InvalidOperationException>(() => audit.Trace(Request()));
        audit.Dispose();
    }

    [Fact]
    public void Invalid_kinds_do_not_create_a_file_and_disposal_rejects_writes() {
        using var files = new Files();
        var audit = new LlmRawAudit(files.Path, "validation-run");
        Assert.Throws<ArgumentException>(() => audit.RecordLookahead("model-response", new { }));
        Assert.Throws<ArgumentException>(() => audit.RecordLookahead("lookahead-", new { }));
        Assert.False(File.Exists(files.Path));
        audit.Dispose();
        Assert.Throws<ObjectDisposedException>(() => audit.RecordLookahead("lookahead-discard", new { }));
        Assert.Throws<ObjectDisposedException>(() => audit.Trace(Request()));
        using var nowhere = new LlmRawAudit(null, "nowhere");
        Assert.Null(nowhere.RecordLookahead("lookahead-discard", new { }));
    }

    private static LlmControllerTrace Request() => new(LlmControllerTraceKind.Request, 1, "a", 1,
        new CompletionRequest("fixture-model", new CompletionPromptPrefix("system",
            new CompletionOutputContract([], CompletionToolChoice.Auto, allowParallelToolCalls: false),
            [new ObservationMessage("material")]), []), RequestId: "request-one");

    private sealed class FailingStream(bool failAtFlush) : MemoryStream {
        public override void Write(ReadOnlySpan<byte> buffer) {
            if (!failAtFlush) { throw new IOException("write-original"); }
            base.Write(buffer);
        }
        public override void Flush() => throw new IOException("flush-original");
        protected override void Dispose(bool disposing) {
            base.Dispose(disposing);
            if (disposing) { throw new IOException("dispose-secondary"); }
        }
    }

    private sealed class Files : IDisposable {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lookahead-raw-test-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Root, "raw.jsonl");
        public void Dispose() { if (Directory.Exists(Root)) { Directory.Delete(Root, recursive: true); } }
    }
}
