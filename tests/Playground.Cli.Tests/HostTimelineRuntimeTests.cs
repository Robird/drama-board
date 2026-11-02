using System.Text;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using DramaBoard.Playground;
using DramaBoard.Playground.Cli;

namespace DramaBoard.Playground.Cli.Tests;

public sealed class HostTimelineRuntimeTests {
    [Theory]
    [InlineData(null, true)]
    [InlineData(false, false)]
    public async Task Human_short_queries_are_durable_before_EOF_and_detail_mode_does_not_disable_operations(bool? gym, bool detailed) {
        using var files = new HostFiles();
        using var output = new StringWriter();
        using var input = new StringReader(":help\n:look\n");
        var options = new PlaygroundHostOptions(Gym: gym, Controls: HostFixture.Humans("a"), LogPath: files.Raw);

        Assert.Equal(0, await PlaygroundHost.RunAsync(files.Store, options, default, output, characterCount: 1, inputReader: input));

        PlaygroundHostJournalSnapshot snapshot = PlaygroundHostJournal.Read(PlaygroundHostJournal.JournalPath(files.Store));
        Assert.Equal(2, snapshot.Records.Count(record => record.Kind == "operation-received"));
        Assert.Equal(2, snapshot.Records.Count(record => record.Kind == "operation-completed"));
        Assert.Equal(detailed, Assert.Single(snapshot.Records, record => record.Kind == "run-start").Payload.GetProperty("DetailedRecording").GetBoolean());
        Assert.Equal(detailed, snapshot.Records.Any(record => record.Kind == "character-material"));
        Assert.DoesNotContain(snapshot.Records, record => record.Kind.StartsWith("model-", StringComparison.Ordinal));
        Assert.False(File.Exists(files.Raw));
        Assert.False(File.Exists(Path.Combine(files.Store, "operations.jsonl")));
        Assert.False(Directory.Exists(Path.Combine(files.Store, "branch-operations")));
        Assert.False(Directory.Exists(Path.Combine(files.Store, "gym")));
        if (detailed) {
            using var saved = PlaygroundDurableHistory.Open(files.Store);
            CharacterMaterialSnapshot material = snapshot.Records.Last(record => record.Kind == "character-material")
                .ReadPayload<CharacterMaterialSnapshot>();
            Assert.Equal(saved.CurrentBranch.CompletedStateCommitId, material.StateCommitId);
            Assert.Equal(saved.Cursor.Version.LineageId, material.LineageId);
            Assert.Equal(saved.Cursor.Version.TransitionCount, material.TransitionCount);
            Assert.Equal(saved.State.Time.Ticks, material.TimeTicks);
        }
        using var reopened = PlaygroundHostJournal.Open(PlaygroundHostJournal.JournalPath(files.Store));
        var audit = new PlaygroundOperationAudit(reopened, HostFixture.BranchId(files.Store), "inspection");
        Assert.All(audit.Entries, entry => Assert.Equal(PlaygroundOperationStatus.Succeeded, entry.Completed!.Status));
        Assert.Empty(audit.PendingOperationRecordIds);
    }

    [Fact]
    public async Task Human_feedback_and_main_association_share_the_run_without_creating_model_audit() {
        using var files = new HostFiles();
        using var output = new StringWriter();
        using var input = new StringReader(":feedback 共用本次运行身份\n");
        Assert.Equal(0, await PlaygroundHost.RunAsync(files.Store,
            new(Controls: HostFixture.Humans("a"), LogPath: files.Raw), default, output, characterCount: 1, inputReader: input));

        PlaygroundHostJournalSnapshot snapshot = PlaygroundHostJournal.Read(PlaygroundHostJournal.JournalPath(files.Store));
        PlaygroundHostRecord run = Assert.Single(snapshot.Records, record => record.Kind == "run-start");
        PlaygroundHostRecord association = Assert.Single(snapshot.Records, record => record.Kind == "feedback-saved");
        FeedbackRecord feedback = Assert.Single(FeedbackJournal.Read(FeedbackSession.JournalPath(files.Store)));
        Assert.Equal(run.RunId, feedback.RunId);
        Assert.Equal(run.RunId, association.RunId);
        Assert.Equal(feedback.FeedbackId, association.Payload.GetProperty("FeedbackId").GetString());
        Assert.Equal(feedback.ActorId, association.ActorId);
        Assert.Equal(feedback.ContextSequence, association.ContextSequence);
        Assert.DoesNotContain(snapshot.Records, record => record.Kind == "operation-received");
        Assert.False(File.Exists(files.Raw));
    }

    [Theory]
    [InlineData("model-request", 0, 1)]
    [InlineData("model-response", 1, 2)]
    public async Task Model_evidence_flush_failure_stops_before_receiving_or_repeating_an_action(
        string failKind, int expectedCalls, int rawRows) {
        using var files = new HostFiles();
        using var output = new StringWriter();
        var stream = new FailOnKindStream(failKind);
        var client = new ToolClient("act", new { command = "handoff must-not-commit" });

        Assert.Equal(1, await PlaygroundHost.RunAsync(files.Store,
            new(LogPath: files.Raw, MaxCalls: 3), default, output, client, characterCount: 1,
            journalFactory: _ => PlaygroundHostJournal.CreateForTesting(stream)));

        Assert.Equal(expectedCalls, client.Calls);
        LlmRawAuditSnapshot raw = LlmRawAudit.Read(files.Raw);
        Assert.Equal(rawRows, raw.Records.Count(record => record.Kind != "metadata"));
        Assert.DoesNotContain("operation-received", stream.WrittenKinds);
        Assert.DoesNotContain("run-end", stream.WrittenKinds);
        Assert.Equal(failKind, stream.WrittenKinds[^1]);
        using var saved = PlaygroundDurableHistory.Open(files.Store);
        Assert.Null(saved.PendingEvent);
        Assert.Null(saved.State.Business.InteractionStates["a"].DraftHandoff);
        Assert.Empty(saved.State.Business.InteractionStates["a"].CurrentContext!.Items);
    }

    [Fact]
    public async Task Llm_feedback_association_failure_preserves_saved_receipt_and_does_not_make_a_second_call() {
        using var files = new HostFiles();
        using var output = new StringWriter();
        var stream = new FailOnKindStream("feedback-saved");
        var client = new ToolClient("feedback", new { text = "Save independently before association fails." });

        Assert.Equal(1, await PlaygroundHost.RunAsync(files.Store,
            new(LogPath: files.Raw, MaxCalls: 3), default, output, client, characterCount: 1,
            journalFactory: _ => PlaygroundHostJournal.CreateForTesting(stream)));

        FeedbackRecord report = Assert.Single(FeedbackJournal.Read(FeedbackSession.JournalPath(files.Store)));
        Assert.Equal(1, client.Calls);
        Assert.Contains(report.FeedbackId, output.ToString());
        Assert.Contains("反馈已保存", output.ToString());
        Assert.DoesNotContain("反馈保存未确认", output.ToString());
        Assert.DoesNotContain("feedback-save-failed", output.ToString());
        Assert.DoesNotContain("operation-received", stream.WrittenKinds);
        Assert.DoesNotContain("run-end", stream.WrittenKinds);
        LlmRawAuditSnapshot raw = LlmRawAudit.Read(files.Raw);
        Assert.Equal(report.RunId, raw.RunId);
        Assert.Equal(new[] { "metadata", "model-request", "model-response" }, raw.Records.Select(record => record.Kind));
        using var saved = PlaygroundDurableHistory.Open(files.Store);
        Assert.Empty(saved.State.Business.InteractionStates["a"].CurrentContext!.Items);
    }

    [Fact]
    public async Task Client_cleanup_failure_preserves_host_journal_failure_and_does_not_write_a_false_run_end() {
        using var files = new HostFiles();
        using var output = new StringWriter();
        var stream = new FailOnKindStream("model-response");
        var factory = new ThrowingCleanupFactory();

        Assert.Equal(1, await PlaygroundHost.RunAsync(files.Store,
            new(ConnectionsPath: files.Connections(), LogPath: files.Raw, MaxCalls: 3), default, output,
            characterCount: 1, clientFactory: factory,
            journalFactory: _ => PlaygroundHostJournal.CreateForTesting(stream)));

        Assert.Equal(1, factory.Calls);
        Assert.Equal(1, factory.Disposals);
        Assert.Contains("宿主已停止（IOException）", output.ToString());
        Assert.DoesNotContain("operation-received", stream.WrittenKinds);
        Assert.DoesNotContain("run-end", stream.WrittenKinds);
        using var saved = PlaygroundDurableHistory.Open(files.Store);
        Assert.Null(saved.State.Business.InteractionStates["a"].DraftHandoff);
    }

    [Fact]
    public async Task Client_cleanup_failure_changes_clean_stop_to_failure_while_preserving_committed_action() {
        using var files = new HostFiles();
        using var output = new StringWriter();
        var factory = new ThrowingCleanupFactory();

        Assert.Equal(1, await PlaygroundHost.RunAsync(files.Store,
            new(ConnectionsPath: files.Connections(), LogPath: files.Raw, MaxCalls: 3, MaxSteps: 2), default, output,
            characterCount: 1, clientFactory: factory));

        Assert.Equal(1, factory.Calls);
        Assert.Equal(1, factory.Disposals);
        using var saved = PlaygroundDurableHistory.Open(files.Store);
        Assert.Equal("cleanup-committed", saved.State.Business.InteractionStates["a"].DraftHandoff);
        PlaygroundHostJournalSnapshot snapshot = PlaygroundHostJournal.Read(PlaygroundHostJournal.JournalPath(files.Store));
        Assert.Single(snapshot.Records, record => record.Kind == "operation-completed");
        Assert.Equal("step-budget", Assert.Single(snapshot.Records, record => record.Kind == "run-end").Payload.GetProperty("Reason").GetString());
    }

    [Fact]
    public async Task Client_cleanup_failure_does_not_replace_an_escaping_committed_diagnostic_failure() {
        using var files = new HostFiles();
        using var output = new StringWriter();
        var factory = new ThrowingCleanupFactory();
        var original = new InvalidOperationException("Original diagnostic failure.");

        IOException error = await Assert.ThrowsAsync<IOException>(() => PlaygroundHost.RunAsync(files.Store,
            new(ConnectionsPath: files.Connections(), LogPath: files.Raw, MaxCalls: 3), default, output,
            characterCount: 1, clientFactory: factory,
            diagnosticSink: _ => { if (factory.Calls > 0) { throw original; } }));

        Assert.Same(original, error.InnerException);
        Assert.Equal(1, factory.Calls);
        Assert.Equal(1, factory.Disposals);
        Assert.DoesNotContain(PlaygroundHostJournal.Read(PlaygroundHostJournal.JournalPath(files.Store)).Records,
            record => record.Kind == "run-end");
        using var saved = PlaygroundDurableHistory.Open(files.Store);
        Assert.Equal("cleanup-committed", saved.State.Business.InteractionStates["a"].DraftHandoff);
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 130)]
    public async Task Client_cleanup_failure_preserves_known_pause_or_cancellation_exit_code(bool cancel, int expectedExitCode) {
        using var files = new HostFiles();
        using var output = new StringWriter();
        using var source = new CancellationTokenSource();
        var factory = new ThrowingCleanupFactory(cancel ? source : null);

        Assert.Equal(expectedExitCode, await PlaygroundHost.RunAsync(files.Store,
            new(ConnectionsPath: files.Connections(), LogPath: files.Raw, MaxCalls: 1), source.Token, output,
            characterCount: 1, clientFactory: factory));

        Assert.Equal(1, factory.Calls);
        Assert.Equal(1, factory.Disposals);
        Assert.Contains(cancel ? "cancelled" : "call-budget-exhausted", output.ToString());
    }

    private sealed class ThrowingCleanupFactory(CancellationTokenSource? cancelOnCall = null) : ICompletionClientFactory {
        public int Calls { get; private set; }
        public int Disposals { get; private set; }
        public ICompletionClient Create(CompletionConnectionConfig connection) => new Client(this);
        private sealed class Client(ThrowingCleanupFactory factory) : ICompletionClient, IAsyncDisposable {
            public string Name => "cleanup-failure-fixture";
            public string ApiSpecId => "fixture";
            public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer,
                CancellationToken cancellationToken = default) {
                factory.Calls++;
                if (factory.CancelOnCall is { } source) { source.Cancel(); cancellationToken.ThrowIfCancellationRequested(); }
                return Task.FromResult(new CompletionResult(new ActionMessage([
                    new ActionBlock.ToolCall(new("act", "cleanup-call", "{\"command\":\"handoff cleanup-committed\"}"))
                ]), new("fixture", "fixture", request.ModelId)));
            }
            public ValueTask DisposeAsync() {
                factory.Disposals++;
                return ValueTask.FromException(new IOException("Injected provider cleanup failure."));
            }
        }
        private CancellationTokenSource? CancelOnCall => cancelOnCall;
    }

    private sealed class ToolClient(string toolName, object arguments) : ICompletionClient {
        public string Name => "fixture";
        public string ApiSpecId => "fixture";
        public int Calls { get; private set; }
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default) {
            Calls++;
            return Task.FromResult(new CompletionResult(
                new ActionMessage([new ActionBlock.ToolCall(new(toolName, "call-" + Calls, JsonSerializer.Serialize(arguments)))]),
                new("fixture", "fixture", request.ModelId)));
        }
    }

    private sealed class FailOnKindStream(string failKind) : MemoryStream {
        private bool _armed;
        public List<string> WrittenKinds { get; } = [];
        public override void Write(ReadOnlySpan<byte> buffer) {
            using JsonDocument line = JsonDocument.Parse(Encoding.UTF8.GetString(buffer));
            using JsonDocument record = JsonDocument.Parse(line.RootElement.GetProperty("Payload").GetString()!);
            string kind = record.RootElement.GetProperty("Kind").GetString()!;
            WrittenKinds.Add(kind);
            _armed = kind == failKind;
            base.Write(buffer);
        }
        public override void Flush() {
            if (_armed) { throw new IOException("Injected durable flush failure at " + failKind); }
            base.Flush();
        }
    }

    private sealed class HostFiles : IDisposable {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "drama-host-timeline-" + Guid.NewGuid().ToString("N"));
        public string Store => Path.Combine(Root, "store");
        public string Raw => Path.Combine(Root, "raw.jsonl");
        public HostFiles() => Directory.CreateDirectory(Root);
        public string Connections() {
            string path = Path.Combine(Root, "connections.json");
            File.WriteAllText(path, """
                {"version":1,"defaultConnectionId":"fixture","catalog":{"v":3,"connections":[
                  {"id":"fixture","kind":"openai-responses","completionSurfaceId":"openai-responses","modelId":"fixture-model","baseAddress":"https://fixture.invalid/"}
                ]}}
                """);
            return path;
        }
        public void Dispose() { if (Directory.Exists(Root)) { Directory.Delete(Root, recursive: true); } }
    }
}
