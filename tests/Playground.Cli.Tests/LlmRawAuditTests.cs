using System.Text;
using System.Text.Json;
using Atelia.Completion.Abstractions;

namespace DramaBoard.Playground.Cli.Tests;

public sealed class LlmRawAuditTests {
    [Fact]
    public void Constructing_or_receiving_feedback_does_not_create_a_model_file() {
        using var files = new Files();
        using (var audit = new LlmRawAudit(files.Path, "run-one")) {
            Assert.Null(audit.Trace(Request() with { Kind = LlmControllerTraceKind.Feedback, FeedbackId = "saved-report" }));
        }
        Assert.False(Directory.Exists(files.Root));
        using var nowhere = new LlmRawAudit(null, "no-file-location");
        Assert.Null(nowhere.Trace(Request()));
    }

    [Fact]
    public void Raw_roundtrip_preserves_native_payload_tools_and_same_request_multiple_stages() {
        using var files = new Files();
        var origin = new CompletionDescriptor("fixture-provider", "fixture-api", "fixture-model");
        byte[] native = Encoding.UTF8.GetBytes("{\"native\":\"opaque-signature\"}");
        var result = new CompletionResult(new ActionMessage([
            new ActionBlock.OpaqueReasoningBlock("fixture-codec", native, origin, "可读摘要"),
            new ActionBlock.Text("visible-answer"),
            new ActionBlock.ToolCall(new("act", "call-one", "{\"command\":\"wait\"}"))
        ]), origin);
        LlmControllerTrace request = Request();
        using (var audit = new LlmRawAudit(files.Path, "run-one")) {
            Assert.Equal(2, audit.Trace(request));
            Assert.Equal(3, audit.Trace(request with { Kind = LlmControllerTraceKind.Response, Result = result }));
            Assert.Equal(4, audit.Trace(request with { Kind = LlmControllerTraceKind.Failure, ProtocolError = "feedback-save-failed" }));
        }
        LlmRawAuditSnapshot snapshot = LlmRawAudit.Read(files.Path);
        Assert.Equal("run-one", snapshot.RunId);
        Assert.Equal(0, snapshot.DiscardedTailBytes);
        Assert.Equal(new[] { "metadata", "model-request", "model-response", "model-failure" }, snapshot.Records.Select(record => record.Kind));
        JsonElement savedRequest = snapshot.Records[1].Payload;
        Assert.Equal("system-private", savedRequest.GetProperty("SystemPrompt").GetString());
        Assert.Equal("shared-observation", savedRequest.GetProperty("Shared")[0].GetProperty("Content").GetString());
        JsonElement receipt = savedRequest.GetProperty("Tail")[1].GetProperty("Results")[0];
        Assert.Equal("feedback", receipt.GetProperty("ToolName").GetString());
        Assert.Equal("feedback-call", receipt.GetProperty("ToolCallId").GetString());
        Assert.Equal("saved-id", receipt.GetProperty("Blocks")[0].GetProperty("Content").GetString());
        JsonElement blocks = snapshot.Records[2].Payload.GetProperty("Blocks");
        Assert.Equal(native, blocks[0].GetProperty("OpaquePayload").GetBytesFromBase64());
        Assert.Equal("可读摘要", blocks[0].GetProperty("PlainText").GetString());
        Assert.Equal("visible-answer", blocks[1].GetProperty("Content").GetString());
        Assert.Equal("act", blocks[2].GetProperty("Call").GetProperty("ToolName").GetString());
        Assert.Equal(snapshot.Records[2].Payload.GetProperty("RequestId").GetString(),
            snapshot.Records[3].Payload.GetProperty("RequestId").GetString());
        Assert.Equal(JsonValueKind.Null, snapshot.Records[3].Payload.GetProperty("Blocks").ValueKind);
    }

    [Fact]
    public void Dedicated_read_ignores_unfinished_tail_without_modifying_and_rejects_completed_corruption() {
        using var files = new Files();
        using (var audit = new LlmRawAudit(files.Path, "run-one")) { audit.Trace(Request()); }
        File.AppendAllText(files.Path, "broken-tail");
        byte[] original = File.ReadAllBytes(files.Path);
        Assert.Equal(Encoding.UTF8.GetByteCount("broken-tail"), LlmRawAudit.Read(files.Path).DiscardedTailBytes);
        Assert.Equal(original, File.ReadAllBytes(files.Path));
        File.AppendAllText(files.Path, "\n");
        Assert.Throws<InvalidDataException>(() => LlmRawAudit.Read(files.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Write_and_flush_failures_retire_the_writer_and_dispose_does_not_replace_failure(bool failAtFlush) {
        var stream = new FailingStream(failAtFlush);
        var audit = LlmRawAudit.CreateForTesting(stream, "failure-run");
        Assert.Throws<IOException>(() => audit.Trace(Request()));
        Assert.True(audit.IsFaulted);
        Assert.Throws<InvalidOperationException>(() => audit.Trace(Request()));
        audit.Dispose();
    }

    [Fact]
    public void Explicit_path_never_overwrites_an_existing_file() {
        using var files = new Files();
        Directory.CreateDirectory(files.Root);
        File.WriteAllText(files.Path, "existing");
        using var audit = new LlmRawAudit(files.Path, "new-run");
        Assert.Throws<IOException>(() => audit.Trace(Request()));
        Assert.True(audit.IsFaulted);
        Assert.Equal("existing", File.ReadAllText(files.Path));
    }

    [Fact]
    public void Mixed_human_presenter_does_not_render_model_private_material() {
        using var output = new StringWriter();
        var presenter = new LlmTracePresenter(output, showModelDetails: false);
        presenter.Trace(Request());
        presenter.Trace(Request() with { Kind = LlmControllerTraceKind.Feedback, FeedbackId = "private-report" });
        presenter.Write("committed", new { ActorId = "b", Result = "private-action" }, "private-action");
        Assert.Equal("", output.ToString());
    }

    private static LlmControllerTrace Request() {
        var request = new CompletionRequest("fixture-model", new CompletionPromptPrefix("system-private",
            new CompletionOutputContract([], CompletionToolChoice.Auto, allowParallelToolCalls: false),
            [new ObservationMessage("shared-observation")]),
            [new ActionMessage([new ActionBlock.Text("carried-answer")]),
                new ToolResultsMessage("receipt", [ToolResult.FromText("feedback", "feedback-call", ToolExecutionStatus.Success, "saved-id")])]);
        return new(LlmControllerTraceKind.Request, 1, "a", 3, request, RequestId: "request-one", ConnectionId: "fixture", ApiSpecId: "fixture-api");
    }

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
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "playground-raw-test-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Root, "raw.jsonl");
        public void Dispose() { if (Directory.Exists(Root)) { Directory.Delete(Root, recursive: true); } }
    }
}
