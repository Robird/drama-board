using System.Text.Json;
using Atelia.Completion.Abstractions;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Playground;

namespace DramaBoard.Playground.Cli.Tests;

public sealed class LlmHostTests {
    [Theory]
    [InlineData("--llm")]
    [InlineData("--character-connections", "connections.json")]
    [InlineData("--model", "x")]
    [InlineData("--llm", "--catalog", "x")]
    [InlineData("--llm", "--connection", "x")]
    [InlineData("--llm", "--max-calls", "0")]
    [InlineData("--llm", "--catalog", "x", "--connection", "a", "--auth-file", "y")]
    [InlineData("--human", "a")]
    public void Llm_options_require_explicit_connection_configuration_and_reject_old_flags(params string[] args) =>
        Assert.Throws<ArgumentException>(() => Options.Parse(args));

    [Fact]
    public void Default_llm_uses_home_and_control_overrides_are_explicit() {
        Assert.Null(HostFixture.Resolve([]).Host.Controls);
        Assert.Null(HostFixture.Resolve(["--llm-connections", "connections.json"]).Host.CharacterControlsPath);
        var llm = HostFixture.Resolve(["--llm-connections", "connections.json", "--max-calls", "7"]).Host;
        Assert.Equal(Path.GetFullPath("connections.json"), llm.ConnectionsPath);
        Assert.Equal(7, llm.MaxCalls);
        Assert.Null(llm.CharacterControlsPath);
    }

    [Fact]
    public async Task Budget_pause_saves_immediate_action_and_reopen_projects_its_feedback_without_repeating_it() {
        using var files = new Files();
        var first = new Client("use item.water");
        using var output = new StringWriter();
        PlaygroundHostOptions firstOptions = files.Options();
        Assert.Equal(2, await PlaygroundHost.RunAsync(files.Store, firstOptions, default, output, first));
        Assert.Single(first.Requests);
        Assert.Contains("call-budget-exhausted", output.ToString());
        JsonElement input = LlmRawAudit.Read(firstOptions.LogPath!).Records
            .First(record => record.Kind == "model-request").Payload;
        Assert.Contains("你在一个持续运行的游戏中", input.GetRawText());
        {
            JsonElement schema = input.GetProperty("OutputContract").GetProperty("Tools")[0].GetProperty("InputSchema");
            Assert.Equal("object", schema.GetProperty("Type").GetString());
            Assert.False(schema.GetProperty("AdditionalProperties").GetBoolean());
            JsonElement parameter = schema.GetProperty("Properties")[0];
            Assert.Equal("command", parameter.GetProperty("Name").GetString());
            Assert.True(parameter.GetProperty("IsRequired").GetBoolean());
            Assert.Equal("String", parameter.GetProperty("Schema").GetProperty("ValueKind").GetString());
            Assert.Equal(8192, parameter.GetProperty("Schema").GetProperty("MaxLength").GetInt32());
        }
        using (var saved = PlaygroundDurableHistory.Open(files.Store)) {
            Assert.Equal(90 * PlaygroundSurvival.Scale, saved.State.GetCharacter("a").Survival!.WaterUnits);
            Assert.Equal(0, saved.State.GetCharacter("a").Backpack.Count("item.water"));
            Assert.Equal(15 * PlaygroundTime.Hour, saved.State.Time.Ticks);
            Assert.Single(saved.State.Business.InteractionStates["a"].CurrentContext!.Items);
        }
        var second = new Client("handoff 继续探索");
        Assert.Equal(2, await PlaygroundHost.RunAsync(files.Store, files.Options(), default, output, second));
        string material = Assert.IsType<ObservationMessage>(Assert.Single(second.Requests.Single().PromptPrefix.SharedContextMessages)).Content!;
        Assert.Contains("use item.water", material);
        Assert.DoesNotContain(":exit", material);
        using var restored = PlaygroundDurableHistory.Open(files.Store);
        Assert.Equal(90 * PlaygroundSurvival.Scale, restored.State.GetCharacter("a").Survival!.WaterUnits);
        Assert.Equal("继续探索", restored.State.Business.InteractionStates["a"].DraftHandoff);
        using var journalStore = PlaygroundHostJournal.Open(PlaygroundHostJournal.JournalPath(files.Store));
        var journal = new PlaygroundOperationAudit(journalStore, restored.CurrentBranch.BranchId, "test-run");
        Assert.Equal(2, journal.Entries.Count);
        Assert.All(journal.Entries, entry => Assert.Equal(PlaygroundOperationStatus.Succeeded, entry.Completed!.Status));
        Assert.All(journal.Entries, entry => Assert.Equal("provider-repeatable-id", entry.Received.ToolCallId));
        Assert.Equal(2, journal.Entries.Select(entry => entry.Received.OperationRecordId).Distinct().Count());
    }

    [Fact]
    public async Task Shout_without_listeners_persists_literal_speech_and_next_model_receives_the_same_personal_material() {
        using var files = new Files();
        const string speech = "ColdWind\nTerminal\n\"回来集合\" **现在**";
        var first = new Client("shout " + speech);
        using var output = new StringWriter();
        Assert.Equal(2, await PlaygroundHost.RunAsync(files.Store, files.Options(), default, output, first));
        string expected;
        using (var saved = PlaygroundDurableHistory.Open(files.Store)) {
            CharacterInteractionState interaction = saved.State.Business.InteractionStates["a"];
            PlaygroundObservedEvent shouted = Assert.Single(interaction.CurrentContext!.Items.SelectMany(item => item.Experiences),
                experience => experience.Event.Kind == PlaygroundObservedEventKind.SpeechShouted).Event;
            Assert.Equal(speech, shouted.Detail);
            Assert.Equal("a", shouted.SubjectId);
            Assert.Null(shouted.Communication);
            expected = CharacterContextRenderer.RenderFull(interaction, new DecisionPlayLayout());
            Assert.Contains(CharacterContextRenderer.DataLiteral(speech), expected);
            Assert.DoesNotContain("寒风袭来", expected);
            Assert.DoesNotContain("相关角色的旅程已结束", expected);
            Assert.Equal(1, saved.State.GetCharacter("a").Communication.SentCount);
        }
        var second = new Client("handoff 等待回复");
        Assert.Equal(2, await PlaygroundHost.RunAsync(files.Store, files.Options(), default, output, second));
        string material = Assert.IsType<ObservationMessage>(Assert.Single(second.Requests.Single().PromptPrefix.SharedContextMessages)).Content!;
        Assert.Equal(expected, material);
        Assert.Contains("不报告听众或接收确认", material);
    }

    [Fact]
    public async Task Complete_response_audit_failure_before_E_does_not_apply_action_and_allows_fresh_request() {
        using var files = new Files();
        var first = new Client("use item.water");
        using var broken = new FailOnceOnResponse();
        Assert.Equal(1, await PlaygroundHost.RunAsync(files.Store, files.Options(), default, broken, first));
        using (var saved = PlaygroundDurableHistory.Open(files.Store)) {
            Assert.Equal(45 * PlaygroundSurvival.Scale, saved.State.GetCharacter("a").Survival!.WaterUnits);
            Assert.Empty(saved.State.Business.InteractionStates["a"].CurrentContext!.Items);
            Assert.Null(saved.PendingEvent);
        }
        using var output = new StringWriter();
        var second = new Client("use item.water");
        Assert.Equal(2, await PlaygroundHost.RunAsync(files.Store, files.Options(), default, output, second));
        Assert.Single(second.Requests);
        using var final = PlaygroundDurableHistory.Open(files.Store);
        Assert.Equal(90 * PlaygroundSurvival.Scale, final.State.GetCharacter("a").Survival!.WaterUnits);
        Assert.Single(final.State.Business.InteractionStates["a"].CurrentContext!.Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_E_or_saved_S_is_recovered_and_receipt_repaired_before_next_model_input(bool saveState) {
        using var files = new Files(createStore: false);
        var initial = PlaygroundGameFactory.CreateSurvival();
        var operation = new PlaygroundControllerOperation("persisted-provider-id", "use item.water", "persisted-record-id");
        var memory = new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor);
        var planned = PlaygroundEngine.CreateKernel(memory, characterController: new FixedOperation(operation));
        Assert.Equal(StepStatus.Committed, await planned.StepAsync(new ModelTime(long.MaxValue)));
        PlaygroundWorld opened = planned.World;
        KernelCursor openedCursor = planned.Cursor;
        Assert.Equal(StepStatus.Committed, await planned.StepAsync(new ModelTime(long.MaxValue)));
        using (var saved = PlaygroundDurableHistory.Create(files.Store, opened, openedCursor)) {
            saved.CommitEvent(planned.LastCompletion!.Event);
            if (saveState) { saved.CommitState(planned.World, planned.Cursor); }
        }
        using (var journalStore = PlaygroundHostJournal.Open(PlaygroundHostJournal.JournalPath(files.Store))) {
            var journal = new PlaygroundOperationAudit(journalStore, HostFixture.BranchId(files.Store), "test-run");
            journal.Receive("old-run", operation.ToolCallId, "a", 1, "act", operation.RawInput, operation.OperationRecordId);
        }
        var client = new Client("handoff 已恢复");
        using var output = new StringWriter();
        Assert.Equal(2, await PlaygroundHost.RunAsync(files.Store, files.Options(), default, output, client));
        string material = Assert.IsType<ObservationMessage>(Assert.Single(client.Requests.Single().PromptPrefix.SharedContextMessages)).Content!;
        Assert.Contains("use item.water", material);
        using var final = PlaygroundDurableHistory.Open(files.Store);
        Assert.Null(final.PendingEvent);
        Assert.Equal(90 * PlaygroundSurvival.Scale, final.State.GetCharacter("a").Survival!.WaterUnits);
        Assert.Equal(2, final.State.Business.InteractionStates["a"].CurrentContext!.Items.Count);
        using var repairedStore = PlaygroundHostJournal.Open(PlaygroundHostJournal.JournalPath(files.Store));
        var repaired = new PlaygroundOperationAudit(repairedStore, final.CurrentBranch.BranchId, "test-run");
        var entry = repaired.Entries.Single(entry => entry.Received.OperationRecordId == operation.OperationRecordId);
        Assert.Equal(PlaygroundOperationStatus.Succeeded, entry.Completed!.Status);
        Assert.False(entry.Completed.ExactResult);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task Cancelled_provider_call_is_a_host_pause_without_game_operation() {
        using var files = new Files();
        using var source = new CancellationTokenSource();
        var client = new Client { OnCall = (_, token) => { source.Cancel(); token.ThrowIfCancellationRequested(); return null!; } };
        using var output = new StringWriter();
        Assert.Equal(130, await PlaygroundHost.RunAsync(files.Store, files.Options(), source.Token, output, client));
        Assert.Contains("cancelled", output.ToString());
        using var final = PlaygroundDurableHistory.Open(files.Store);
        Assert.Null(final.PendingEvent);
        Assert.Empty(final.State.Business.InteractionStates["a"].CurrentContext!.Items);
        Assert.Equal(45 * PlaygroundSurvival.Scale, final.State.GetCharacter("a").Survival!.WaterUnits);
    }

    private sealed class Client(params string[] commands) : ICompletionClient {
        private readonly Queue<string> _commands = new(commands);
        public string Name => "fixture";
        public string ApiSpecId => "fixture";
        public List<CompletionRequest> Requests { get; } = [];
        public Func<CompletionRequest, CancellationToken, CompletionResult>? OnCall { get; init; }
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
            Requests.Add(request);
            return Task.FromResult(OnCall?.Invoke(request, cancellationToken) ?? new CompletionResult(
                new ActionMessage([new ActionBlock.ToolCall(new("act", "provider-repeatable-id", JsonSerializer.Serialize(new { command = _commands.Dequeue() })))]),
                new("fixture", "fixture", request.ModelId)));
        }
    }
    private sealed class FixedOperation(PlaygroundControllerOperation operation) : IPlaygroundCharacterController {
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) => ValueTask.FromResult(operation);
    }
    private sealed class FailOnceOnResponse : StringWriter {
        private bool _failed;
        public override void WriteLine(string? value) {
            if (!_failed && value?.StartsWith("[model-output]", StringComparison.Ordinal) == true) {
                _failed = true;
                throw new IOException("Injected output failure after complete response.");
            }
            base.WriteLine(value);
        }
    }
    private sealed class Files : IDisposable {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "drama-llm-host-" + Guid.NewGuid().ToString("N"));
        public string Store => Path.Combine(_root, "game");
        // These recovery contracts intentionally use a known single-actor needs boundary.
        // Default living-world creation and multi-actor routing are tested in DuoHostTests.
        public Files(bool createStore = true) {
            if (createStore) {
                var initial = PlaygroundGameFactory.CreateSurvival();
                using var saved = PlaygroundDurableHistory.Create(Store, initial.World, initial.Cursor);
            }
        }
        public PlaygroundHostOptions Options() => new(MaxCalls: 1, LogPath: Path.Combine(_root, Guid.NewGuid().ToString("N") + ".jsonl"));
        public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); } }
    }
}
