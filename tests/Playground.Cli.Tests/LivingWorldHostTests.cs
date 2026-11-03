using System.Text.Json;
using Atelia.Completion.Abstractions;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Playground;

namespace DramaBoard.Playground.Cli.Tests;

/// <summary>Real host, persistence and controller routing; Completion responses are offline fixtures.</summary>
public sealed class LivingWorldHostTests {
    [Fact]
    public async Task Default_console_creates_three_residents_in_one_continuing_world() {
        using var files = new Files();
        using var output = new StringWriter();
        Assert.Equal(0, await HostFixture.RunHumanAsync(files.Store, default,
            inputReader: new StringReader(""), outputWriter: output));
        using var saved = PlaygroundDurableHistory.Open(files.Store);
        Assert.Equal(new[] { "a", "b", "c" }, saved.State.Characters.Select(actor => actor.Id));
        Assert.Equal(8 * PlaygroundTime.Hour, saved.State.Time.Ticks);
        Assert.Equal(PlaygroundExpeditionMap.ScenarioId, saved.State.Expedition!.ScenarioId);
        Assert.All(saved.State.Characters, actor => Assert.True(actor.IsActive));
        Assert.Contains("山野镇", output.ToString());
        Assert.DoesNotContain("宿主全局总结", output.ToString());
    }

    [Fact]
    public async Task Default_llm_routes_all_three_residents_and_gym_records_actual_bindings() {
        using var files = new Files();
        var client = new Client("wait 1");
        PlaygroundHostOptions options = files.Options() with { MaxCalls = 3, Gym = true };
        using var output = new StringWriter();
        Assert.Equal(2, await PlaygroundHost.RunAsync(files.Store, options, default, output, client));
        Assert.Equal(3, client.Requests.Count);
        using var operationsStore = PlaygroundHostJournal.Open(PlaygroundHostJournal.JournalPath(files.Store));
        var operations = new PlaygroundOperationAudit(operationsStore, HostFixture.BranchId(files.Store), "test-run");
        Assert.Equal(new[] { "a", "b", "c" }, operations.Entries.Select(entry => entry.Received.ActorId));
        Assert.All(operations.Entries, entry => Assert.Equal(PlaygroundOperationStatus.Succeeded, entry.Completed!.Status));
        var gym = PlaygroundHostJournal.Read(PlaygroundHostJournal.JournalPath(files.Store));
        JsonElement started = Assert.Single(gym.Records, record => record.Kind == "run-start").Payload;
        Assert.Equal(PlaygroundExpeditionMap.ScenarioId, started.GetProperty("Scenario").GetString());
        Assert.Equal(new[] { "LLM", "LLM", "LLM" }, started.GetProperty("Actors").EnumerateArray()
            .Select(actor => actor.GetProperty("Controller").GetString()));
        Assert.Equal(new[] { "a", "b", "c" }, gym.Records.Where(record => record.Kind == "model-request").Select(record => record.ActorId));
        Assert.DoesNotContain("[Console 输入]", output.ToString());
    }

    [Fact]
    public async Task Human_b_receives_its_own_console_and_other_residents_use_the_model() {
        using var files = new Files();
        var client = new Client("wait 1");
        using var output = new StringWriter();
        Assert.Equal(2, await PlaygroundHost.RunAsync(files.Store, files.Options() with { MaxCalls = 2, Controls = HostFixture.Humans("b"), Gym = true },
            default, output, client, inputReader: new StringReader("handoff human-b-private\nwait 1\n")));
        Assert.Equal(2, client.Requests.Count);
        Assert.All(client.Requests, request => Assert.DoesNotContain("human-b-private",
            Assert.IsType<ObservationMessage>(Assert.Single(request.PromptPrefix.SharedContextMessages)).Content));
        using var operationsStore = PlaygroundHostJournal.Open(PlaygroundHostJournal.JournalPath(files.Store));
        var operations = new PlaygroundOperationAudit(operationsStore, HostFixture.BranchId(files.Store), "test-run");
        Assert.All(operations.Entries, entry => Assert.Equal(PlaygroundOperationStatus.Succeeded, entry.Completed!.Status));
        Assert.Contains(operations.Entries, entry => entry.Received.ActorId == "b" && entry.Received.RawInput == "handoff human-b-private");
        var gym = PlaygroundHostJournal.Read(PlaygroundHostJournal.JournalPath(files.Store));
        Assert.Equal(new[] { "a", "c" }, gym.Records.Where(record => record.Kind == "model-request").Select(record => record.ActorId));
        JsonElement started = Assert.Single(gym.Records, record => record.Kind == "run-start").Payload;
        Assert.Equal(new[] { "LLM", "Human", "LLM" }, started.GetProperty("Actors").EnumerateArray()
            .Select(actor => actor.GetProperty("Controller").GetString()));
        Assert.Contains("[Console 输入] b>", output.ToString());
        Assert.DoesNotContain("[Console 输入] a>", output.ToString());
        Assert.DoesNotContain("[Console 输入] c>", output.ToString());
    }

    [Fact]
    public async Task Step_budget_pauses_before_model_call_and_cold_open_can_rebind_to_human() {
        using var files = new Files();
        var client = new Client("handoff resumed-model");
        using var output = new StringWriter();
        Assert.Equal(0, await PlaygroundHost.RunAsync(files.Store, files.Options() with { MaxSteps = 1 }, default, output, client));
        Assert.Empty(client.Requests);
        using (var paused = PlaygroundDurableHistory.Open(files.Store)) {
            Assert.All(paused.State.Characters, actor => Assert.True(actor.IsActive));
            Assert.NotNull(paused.State.Business.InteractionStates["a"].CurrentContext);
            Assert.Null(paused.PendingEvent);
        }
        Assert.Contains("预算已到", output.ToString());
        using var resumedOutput = new StringWriter();
        Assert.Equal(0, await PlaygroundHost.RunAsync(files.Store, files.Options() with { Controls = HostFixture.Humans("a") },
            default, resumedOutput, client, inputReader: new StringReader("")));
        Assert.Empty(client.Requests);
        Assert.Contains("[Console 输入] a>", resumedOutput.ToString());
        Assert.Equal(2, await PlaygroundHost.RunAsync(files.Store, files.Options(), default, output, client));
        Assert.Single(client.Requests);
        using var resumed = PlaygroundDurableHistory.Open(files.Store);
        Assert.Equal("resumed-model", resumed.State.Business.InteractionStates["a"].DraftHandoff);
    }

    [Fact]
    public async Task Time_budget_is_a_host_pause_without_terminal_and_can_resume_later() {
        using var files = new Files();
        var client = new Client("wait 1");
        using var output = new StringWriter();
        Assert.Equal(0, await PlaygroundHost.RunAsync(files.Store, files.Options() with { Until = new ModelTime(8 * PlaygroundTime.Hour) },
            default, output, client));
        Assert.Empty(client.Requests);
        using (var paused = PlaygroundDurableHistory.Open(files.Store)) {
            Assert.All(paused.State.Characters, actor => Assert.True(actor.IsActive));
            Assert.Equal(8 * PlaygroundTime.Hour, paused.State.Time.Ticks);
        }
        Assert.Equal(2, await PlaygroundHost.RunAsync(files.Store, files.Options() with { Until = new ModelTime(9 * PlaygroundTime.Hour) },
            default, output, client));
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task Pending_gather_beyond_host_time_budget_recovers_once_before_pausing_without_model_calls() {
        using var files = new Files();
        var initial = PlaygroundGameFactory.CreateLivingWorld(characterCount: 1, enableWeather: false);
        var commands = new Queue<string>(["go e-m2", "gather well 1"]);
        var planned = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor),
            characterController: new ScriptedController(commands));
        PlaygroundWorld before = initial.World;
        KernelCursor beforeCursor = initial.Cursor;
        for (int step = 0; step < 32; step++) {
            before = planned.World;
            beforeCursor = planned.Cursor;
            Assert.Equal(StepStatus.Committed, await planned.StepAsync(new ModelTime(long.MaxValue)));
            if (planned.LastCompletion!.Event.Facts.Any(fact => fact.Kind == PlaygroundEventKind.GatherYielded)) { break; }
        }
        Assert.Empty(commands);
        Assert.Contains(planned.LastCompletion!.Event.Facts, fact => fact.Kind == PlaygroundEventKind.GatherYielded);
        Assert.True(before.Time < planned.World.Time);
        ModelTime limit = new(before.Time.Ticks + (planned.World.Time.Ticks - before.Time.Ticks) / 2);
        Assert.True(before.Time < limit && limit < planned.World.Time);
        Assert.Equal(before.GetCharacter("a").Backpack.Count("item.water") + 1,
            planned.World.GetCharacter("a").Backpack.Count("item.water"));
        using (var pending = PlaygroundDurableHistory.Create(files.Store, before, beforeCursor)) {
            pending.CommitEvent(planned.LastCompletion.Event);
        }
        using (var pending = PlaygroundDurableHistory.Open(files.Store)) {
            Assert.NotNull(pending.PendingEvent);
            Assert.Equal(beforeCursor, pending.Cursor);
            Assert.Equal(before.GetCharacter("a").Backpack.Count("item.water"), pending.State.GetCharacter("a").Backpack.Count("item.water"));
        }

        var client = new Client("wait 1");
        using var output = new StringWriter();
        PlaygroundHostOptions options = files.Options() with { Until = limit, Gym = true };
        Assert.Equal(0, await PlaygroundHost.RunAsync(files.Store, options, default, output, client));
        Assert.Empty(client.Requests);
        string recoveredCommit;
        using (var recovered = PlaygroundDurableHistory.Open(files.Store)) {
            Assert.Null(recovered.PendingEvent);
            Assert.Equal(planned.Cursor, recovered.Cursor);
            Assert.True(recovered.State.Time > limit);
            Assert.Equal(planned.World.GetCharacter("a").Backpack.Count("item.water"), recovered.State.GetCharacter("a").Backpack.Count("item.water"));
            Assert.Equal(planned.World.GetResource("well").Stock, recovered.State.GetResource("well").Stock);
            Assert.Null(recovered.State.GetCharacter("a").Activity);
            Assert.Equal(PlaygroundDiagnostics.Render(planned.World), PlaygroundDiagnostics.Render(recovered.State));
            recoveredCommit = PlaygroundDurableHistory.FormatCommitId(recovered.CompletedStateCommitId);
        }
        {
            var gym = PlaygroundHostJournal.Read(PlaygroundHostJournal.JournalPath(files.Store));
            Assert.Contains("已完成上次中断时保存的行动", output.ToString());
            Assert.Equal(recoveredCommit, Assert.Single(gym.Records, record => record.Kind == "run-end").Payload.GetProperty("StateCommitId").GetString());
            Assert.DoesNotContain(gym.Records, record => record.Kind == "model-request");
            Assert.Equal("time-budget", Assert.Single(gym.Records, record => record.Kind == "run-end").Payload.GetProperty("Reason").GetString());
        }
        // A second cold open at the recovered boundary must not republish or repeat the yield.
        Assert.Equal(0, await PlaygroundHost.RunAsync(files.Store, files.Options() with { Until = planned.World.Time }, default, output, client));
        Assert.Empty(client.Requests);
        using var reopened = PlaygroundDurableHistory.Open(files.Store);
        Assert.Null(reopened.PendingEvent);
        Assert.Equal(recoveredCommit, PlaygroundDurableHistory.FormatCommitId(reopened.CompletedStateCommitId));
        Assert.Equal(planned.Cursor, reopened.Cursor);
        Assert.Equal(planned.World.GetCharacter("a").Backpack.Count("item.water"), reopened.State.GetCharacter("a").Backpack.Count("item.water"));
        Assert.Equal(planned.World.GetResource("well").Stock, reopened.State.GetResource("well").Stock);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Mixed_console_hides_model_goals_context_and_receipts_but_delivers_legitimate_public_speech(int count) {
        using var files = new Files();
        const string goal = "PRIVATE_MODEL_GOAL_9831";
        const string criterion = "PRIVATE_MODEL_CRITERION_4872";
        const string handoff = "PRIVATE_MODEL_HANDOFF_7629";
        const string publicSpeech = "PUBLIC_SHOUT_MEET_AT_MARKET_5163";
        var client = new Client(
            "create-goal " + JsonSerializer.Serialize(new { title = goal, criteria = criterion }),
            "handoff " + handoff,
            "shout " + publicSpeech);
        PlaygroundHostOptions options = files.Options() with { MaxCalls = 3, Controls = count == 3 ? HostFixture.Humans("a", "c") : HostFixture.Humans("a"), Gym = true };
        using var output = new StringWriter();
        // After the shout, c must acknowledge attention while preserving its existing wait,
        // allowing the round-robin scheduler to deliver a's next frozen context before exit.
        Assert.Equal(0, await PlaygroundHost.RunAsync(files.Store, options, default, output, client, characterCount: count,
            inputReader: new StringReader(count == 3 ? "wait 1\nwait 1\ncontinue\n:exit\n" : "wait 1\n:exit\n")));
        Assert.Equal(3, client.Requests.Count);
        string rendered = output.ToString();
        foreach (string secret in new[] { goal, criterion, handoff }) {
            Assert.DoesNotContain(secret, rendered);
            Assert.Contains(secret, File.ReadAllText(options.LogPath!));
        }
        Assert.DoesNotContain("[model-input]", rendered);
        Assert.DoesNotContain("[model-output]", rendered);
        Assert.Contains(publicSpeech, rendered);
        using var saved = PlaygroundDurableHistory.Open(files.Store);
        foreach (string actorId in count == 3 ? new[] { "a", "c" } : new[] { "a" }) {
            Assert.Contains($"[Console 输入] {actorId}>", rendered);
            CharacterInteractionState human = saved.State.Business.InteractionStates[actorId];
            string humanMaterial = CharacterContextRenderer.RenderFull(human, new DecisionPlayLayout());
            Assert.Contains(publicSpeech, humanMaterial);
            Assert.DoesNotContain(goal, humanMaterial);
            Assert.DoesNotContain(criterion, humanMaterial);
            Assert.DoesNotContain(handoff, humanMaterial);
        }
        CharacterInteractionState model = saved.State.Business.InteractionStates["b"];
        Assert.Equal(goal, Assert.Single(model.Goals.Goals).Title);
        Assert.Equal(handoff, model.DraftHandoff);
        using var operationsStore = PlaygroundHostJournal.Open(PlaygroundHostJournal.JournalPath(files.Store));
        var operations = new PlaygroundOperationAudit(operationsStore, saved.CurrentBranch.BranchId, "test-run");
        Assert.All(operations.Entries, entry => Assert.Equal(PlaygroundOperationStatus.Succeeded, entry.Completed!.Status));
        Assert.Contains(operations.Entries, entry => entry.Received.ActorId == "b" &&
            entry.Received.RawInput.Contains(handoff, StringComparison.Ordinal) && entry.Completed!.Status == PlaygroundOperationStatus.Succeeded);
        var gym = PlaygroundHostJournal.Read(PlaygroundHostJournal.JournalPath(files.Store));
        Assert.Contains(gym.Records, record => record.Kind == "model-request" && record.ActorId == "b" &&
            record.Payload.GetRawText().Contains(goal, StringComparison.Ordinal));
    }

    private sealed class ScriptedController(Queue<string> commands) : IPlaygroundCharacterController {
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PlaygroundControllerOperation("fixture-" + Guid.NewGuid().ToString("N"), commands.Dequeue(), Guid.NewGuid().ToString("N")));
    }

    private sealed class Client(params string[] commands) : ICompletionClient {
        private readonly Queue<string> _commands = new(commands);
        public string Name => "living-world-fixture";
        public string ApiSpecId => "fixture";
        public List<CompletionRequest> Requests { get; } = [];
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default) {
            Requests.Add(request);
            string command = _commands.Count > 1 ? _commands.Dequeue() : _commands.Peek();
            return Task.FromResult(new CompletionResult(new ActionMessage([
                new ActionBlock.ToolCall(new("act", "fixture-call", JsonSerializer.Serialize(new { command })))
            ]), new("fixture", "fixture", request.ModelId)));
        }
    }

    private sealed class Files : IDisposable {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "drama-living-host-" + Guid.NewGuid().ToString("N"));
        public string Store => Path.Combine(_root, "game");
        public PlaygroundHostOptions Options() => new(MaxCalls: 1, LogPath: Path.Combine(_root, Guid.NewGuid().ToString("N") + ".jsonl"));
        public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); } }
    }
}
