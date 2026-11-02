using System.Text.Json;
using Atelia.Completion.Abstractions;
using DramaBoard.Playground.Cli;
using DramaBoard.Playground;

namespace DramaBoard.Playground.Cli.Tests;

public sealed class FeedbackHostTests {
    [Fact]
    public async Task Durable_human_feedback_survives_cold_resume_without_game_operations_or_extra_transitions() {
        using var files = new HostFiles();
        using var output = new StringWriter();
        long transitions;
        string stateCommit;
        using (var input = new StringReader(":feedback 第一次 自由反馈\n")) {
            Assert.Equal(0, await PlaygroundHost.RunAsync(files.Store, files.Options(human: true), default,
                output, inputReader: input));
        }
        FeedbackRecord first = Assert.Single(files.Reports());
        Assert.Equal(FeedbackSource.HumanConsole, first.Source);
        Assert.Equal("第一次 自由反馈", first.Text);
        Assert.Null(first.Model);
        Assert.NotNull(first.Boundary);
        using (var saved = PlaygroundDurableHistory.Open(files.Store)) {
            transitions = saved.Cursor.Version.TransitionCount;
            stateCommit = saved.CurrentBranch.CompletedStateCommitId!;
            Assert.Empty(saved.State.Business.InteractionStates["a"].CurrentContext!.Items);
            Assert.Null(saved.PendingEvent);
        }
        using (var input = new StringReader(":feedback 第二次 冷开反馈\n")) {
            Assert.Equal(0, await PlaygroundHost.RunAsync(files.Store, files.Options(human: true), default,
                output, inputReader: input));
        }
        FeedbackRecord[] reports = files.Reports().ToArray();
        Assert.Equal(2, reports.Length);
        Assert.Equal(first.Boundary, reports[1].Boundary);
        Assert.NotEqual(first.RunId, reports[1].RunId);
        Assert.NotEqual(first.FeedbackId, reports[1].FeedbackId);
        Assert.Contains(first.FeedbackId, output.ToString());
        Assert.Contains(reports[1].FeedbackId, output.ToString());
        using (var saved = PlaygroundDurableHistory.Open(files.Store)) {
            Assert.Equal(transitions, saved.Cursor.Version.TransitionCount);
            Assert.Equal(stateCommit, saved.CurrentBranch.CompletedStateCommitId);
            Assert.Empty(saved.State.Business.InteractionStates["a"].CurrentContext!.Items);
        }
        using var operationsStore = PlaygroundHostJournal.Open(PlaygroundHostJournal.JournalPath(files.Store));
        var operations = new PlaygroundOperationAudit(operationsStore, HostFixture.BranchId(files.Store), "test-run");
        Assert.Empty(operations.Entries);
        Assert.False(Directory.Exists(Path.Combine(files.Store, "gym")));
    }

    [Fact]
    public async Task Default_llm_feedback_saves_without_gym_and_receipt_reaches_next_request_before_act() {
        using var files = new HostFiles();
        using var output = new StringWriter();
        CompletionResult feedback = Tool("feedback", "feedback-call", new { text = "模型的独立反馈" });
        var client = new FeedbackClient(feedback, Tool("act", "act-call", new { command = "handoff after-feedback" }));
        Assert.Equal(2, await PlaygroundHost.RunAsync(files.Store, files.Options(human: false) with { MaxCalls = 2, Gym = false }, default,
            output, client));
        FeedbackRecord saved = Assert.Single(files.Reports());
        Assert.Equal(FeedbackSource.Llm, saved.Source);
        Assert.Equal("模型的独立反馈", saved.Text);
        Assert.Equal("feedback-call", saved.Model!.ToolCallId);
        Assert.NotNull(saved.Boundary);
        Assert.Equal(2, client.Requests.Count);
        Assert.Same(feedback.Message, client.Requests[1].TailMessages[0]);
        ToolResult receipt = Assert.Single(Assert.IsType<ToolResultsMessage>(client.Requests[1].TailMessages[1]).Results);
        Assert.Equal("feedback-call", receipt.ToolCallId);
        Assert.Contains(saved.FeedbackId, receipt.GetFlattenedText());
        using var history = PlaygroundDurableHistory.Open(files.Store);
        Assert.Equal("after-feedback", history.State.Business.InteractionStates["a"].DraftHandoff);
        using var operationsStore = PlaygroundHostJournal.Open(PlaygroundHostJournal.JournalPath(files.Store));
        var operations = new PlaygroundOperationAudit(operationsStore, history.CurrentBranch.BranchId, "test-run");
        Assert.Equal("act", Assert.Single(operations.Entries).Received.ToolName);
        Assert.False(Directory.Exists(Path.Combine(files.Store, "gym")));
        Assert.DoesNotContain(operationsStore.Records, record => record.Kind == "feedback-saved");
    }

    [Fact]
    public async Task Ephemeral_human_feedback_uses_home_without_claiming_a_durable_boundary_or_store() {
        using var files = new HostFiles();
        using var output = new StringWriter();
        using var input = new StringReader(":feedback 临时世界中的反馈\n");
        Assert.Equal(0, await PlaygroundHost.RunAsync((string?)null, files.Options(human: true), default,
            output, characterCount: 1, inputReader: input));
        FeedbackRecord report = Assert.Single(FeedbackJournal.Read(FeedbackSession.JournalPath(files.Home)));
        Assert.Equal("临时世界中的反馈", report.Text);
        Assert.Null(report.Boundary);
        Assert.Null(report.StorePath);
        Assert.False(File.Exists(FeedbackSession.JournalPath(files.Store)));
        Assert.False(Directory.Exists(Path.Combine(files.Home, "gym")));
        Assert.False(File.Exists(PlaygroundHostJournal.JournalPath(files.Home)));
    }

    [Fact]
    public async Task Host_start_without_feedback_creates_no_feedback_file() {
        using var files = new HostFiles();
        using var output = new StringWriter();
        using var input = new StringReader("");
        Assert.Equal(0, await PlaygroundHost.RunAsync(files.Store, files.Options(human: true), default,
            output, inputReader: input));
        Assert.False(File.Exists(FeedbackSession.JournalPath(files.Store)));
        Assert.False(File.Exists(FeedbackSession.JournalPath(files.Home)));
        Assert.False(Directory.Exists(Path.Combine(files.Store, "gym")));
    }

    [Fact]
    public async Task Feedback_stays_in_current_input_window_and_does_not_enter_operation_journal() {
        using var journalStore = PlaygroundHostJournal.CreateInMemory();
        var journal = new PlaygroundOperationAudit(journalStore, "test-branch", "test-run");
        using var input = new StringReader(":feedback 这里 有 一些 反馈\nwait 1\n");
        using var output = new StringWriter();
        var world = PlaygroundGameFactory.CreateSurvival().World;
        var context = ViewContextFixture.Create(world, "a");
        int calls = 0;
        var controller = new PlayConsole.ConsoleCharacterController(PlayPresentation.ForMode(true), ConsoleMetaCommands.HumanDefault,
            input, output, operations: journal, feedbackSink: (actual, text, token) => {
                Assert.Same(context, actual);
                Assert.Equal("这里 有 一些 反馈", text);
                Assert.Empty(journal.Entries);
                calls++;
                return ValueTask.FromResult("feedback-receipt");
            });
        var operation = await controller.ChooseCommandAsync(context, default);
        Assert.Equal(1, calls);
        Assert.Equal("wait 1", operation.RawInput);
        Assert.Equal("wait 1", Assert.Single(journal.Entries).Received.RawInput);
        Assert.Contains("反馈已保存：feedback-receipt", output.ToString());
        Assert.Same(context.Context, context.InteractionState.CurrentContext);
    }

    [Fact]
    public async Task Feedback_save_succeeds_even_when_operation_journal_is_unusable() {
        using var journalStore = PlaygroundHostJournal.CreateInMemory();
        var journal = new PlaygroundOperationAudit(journalStore, "test-branch", "test-run");
        journalStore.Dispose();
        using var input = new StringReader(":feedback 仍应保存\n");
        using var output = new StringWriter();
        int saved = 0;
        var controller = new PlayConsole.ConsoleCharacterController(PlayPresentation.ForMode(true), ConsoleMetaCommands.HumanDefault,
            input, output, operations: journal, feedbackSink: (_, _, _) => {
                saved++;
                return ValueTask.FromResult("saved-independently");
            });
        await Assert.ThrowsAsync<PlayConsole.PlayEndedException>(() => controller.ChooseCommandAsync(
            ViewContextFixture.Create(PlaygroundGameFactory.CreateSurvival().World, "a"), default).AsTask());
        Assert.Equal(1, saved);
        Assert.Contains("反馈已保存：saved-independently", output.ToString());
    }

    [Fact]
    public async Task Saved_feedback_receipt_remains_successful_when_host_association_fails() {
        using var journalStore = PlaygroundHostJournal.CreateInMemory();
        var journal = new PlaygroundOperationAudit(journalStore, "test-branch", "test-run");
        using var input = new StringReader(":feedback 关联失败也保留正文\nwait 1\n");
        using var output = new StringWriter();
        int saved = 0;
        var controller = new PlayConsole.ConsoleCharacterController(PlayPresentation.ForMode(true), ConsoleMetaCommands.HumanDefault,
            input, output, operations: journal, feedbackSink: (_, _, _) => {
                saved++;
                return ValueTask.FromResult("saved-feedback-id");
            }, feedbackSaved: (_, feedbackId) => {
                Assert.Contains("反馈已保存：saved-feedback-id", output.ToString());
                Assert.Equal("saved-feedback-id", feedbackId);
                throw new IOException("Injected host association failure after feedback was saved.");
            });

        await Assert.ThrowsAsync<IOException>(() => controller.ChooseCommandAsync(
            ViewContextFixture.Create(PlaygroundGameFactory.CreateSurvival().World, "a"), default).AsTask());

        Assert.Equal(1, saved);
        Assert.Empty(journal.Entries);
        Assert.Contains("反馈已保存：saved-feedback-id", output.ToString());
        Assert.DoesNotContain("反馈保存未确认", output.ToString());
        Assert.Equal("wait 1", await input.ReadLineAsync());
    }

    [Theory]
    [InlineData(":feedback")]
    [InlineData(":feedback   ")]
    [InlineData(":feedback too-long", true)]
    public async Task Invalid_feedback_does_not_invoke_sink_or_record_an_operation(string command, bool oversize = false) {
        if (oversize) { command = ":feedback " + new string('x', 4001); }
        using var journalStore = PlaygroundHostJournal.CreateInMemory();
        var journal = new PlaygroundOperationAudit(journalStore, "test-branch", "test-run");
        using var input = new StringReader(command + "\n");
        using var output = new StringWriter();
        var controller = new PlayConsole.ConsoleCharacterController(PlayPresentation.ForMode(true), ConsoleMetaCommands.HumanDefault,
            input, output, operations: journal, feedbackSink: (_, _, _) => throw new InvalidOperationException("Must not save"));
        await Assert.ThrowsAsync<PlayConsole.PlayEndedException>(() => controller.ChooseCommandAsync(
            ViewContextFixture.Create(PlaygroundGameFactory.CreateSurvival().World, "a"), default).AsTask());
        Assert.Empty(journal.Entries);
        Assert.Contains("反馈正文不能为空", output.ToString());
        Assert.DoesNotContain("反馈已保存：", output.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unavailable_or_failed_save_never_reports_success(bool fail) {
        using var journalStore = PlaygroundHostJournal.CreateInMemory();
        var journal = new PlaygroundOperationAudit(journalStore, "test-branch", "test-run");
        using var input = new StringReader(":feedback 保存我的反馈\n");
        using var output = new StringWriter();
        var controller = new PlayConsole.ConsoleCharacterController(PlayPresentation.ForMode(true), ConsoleMetaCommands.HumanDefault,
            input, output, operations: journal, feedbackSink: fail ? (_, _, _) => throw new IOException("disk failed") : null);
        await Assert.ThrowsAsync<PlayConsole.PlayEndedException>(() => controller.ChooseCommandAsync(
            ViewContextFixture.Create(PlaygroundGameFactory.CreateSurvival().World, "a"), default).AsTask());
        Assert.Empty(journal.Entries);
        Assert.DoesNotContain("反馈已保存：", output.ToString());
        Assert.Contains(fail ? "反馈保存未确认" : "反馈未保存", output.ToString());
    }

    [Fact]
    public async Task Disabled_directory_rejects_feedback_without_saving_or_operation_receipt() {
        using var journalStore = PlaygroundHostJournal.CreateInMemory();
        var journal = new PlaygroundOperationAudit(journalStore, "test-branch", "test-run");
        using var input = new StringReader(":feedback 不应保存\n");
        using var output = new StringWriter();
        var controller = new PlayConsole.ConsoleCharacterController(PlayPresentation.ForMode(true), ConsoleMetaCommands.None,
            input, output, operations: journal, feedbackSink: (_, _, _) => throw new InvalidOperationException("Must not save"));
        await Assert.ThrowsAsync<PlayConsole.PlayEndedException>(() => controller.ChooseCommandAsync(
            ViewContextFixture.Create(PlaygroundGameFactory.CreateSurvival().World, "a"), default).AsTask());
        Assert.Empty(journal.Entries);
        Assert.Contains("无法识别命令", output.ToString());
    }
    private static CompletionResult Tool(string name, string id, object arguments) => new(
        new ActionMessage([new ActionBlock.ToolCall(new RawToolCall(name, id, JsonSerializer.Serialize(arguments)))]),
        new("fixture", "fixture", "fixture-model"));

    private sealed class FeedbackClient(params CompletionResult[] results) : ICompletionClient {
        private readonly Queue<CompletionResult> _results = new(results);
        public string Name => "fixture";
        public string ApiSpecId => "fixture";
        public List<CompletionRequest> Requests { get; } = [];
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default) {
            Requests.Add(request);
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class HostFiles : IDisposable {
        public string Home { get; } = Path.Combine(Path.GetTempPath(), "drama-feedback-host-" + Guid.NewGuid().ToString("N"));
        public string Store => Path.Combine(Home, "game");
        public HostFiles() {
            var initial = PlaygroundGameFactory.CreateSurvival();
            using var saved = PlaygroundDurableHistory.Create(Store, initial.World, initial.Cursor);
        }
        public PlaygroundHostOptions Options(bool human) => new(HomePath: Home,
            LogPath: Path.Combine(Home, "audit-" + Guid.NewGuid().ToString("N") + ".jsonl"),
            Controls: human ? HostFixture.Humans("a") : null);
        public IReadOnlyList<FeedbackRecord> Reports() => FeedbackJournal.Read(FeedbackSession.JournalPath(Store));
        public void Dispose() { if (Directory.Exists(Home)) { Directory.Delete(Home, recursive: true); } }
    }

}
