using System.Text.Json;
using Atelia.Completion.Abstractions;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Playground;

namespace DramaBoard.Playground.Cli.Tests;

public sealed class DuoHostTests {
    [Fact]
    public void Decision_layout_shows_same_place_public_status_without_other_inventory_or_skill() {
        var initial = PlaygroundGameFactory.CreateSurvival(enableNeeds: false, characterCount: 2);
        var other = new PlaygroundCharacter("b", "阿白", skillLevel: 77,
            backpack: new(new Dictionary<string, int> { ["private-item"] = 91 }),
            activity: new(PlaygroundActivityKind.Wait, 1, initial.World.Time, PlaygroundTime.Hour,
                new(initial.World.Time.Ticks + PlaygroundTime.Hour)), nextActivityId: 2);
        PlaygroundBusinessState business = initial.World.Business.Replace(initial.World.Time, changedCharacters: [other]);
        PlaygroundWorld world = PlaygroundEngine.Rebuild(business, business.Time);
        PlaygroundObservation observation = PlaygroundDecisionContext.Observe(world, "a");
        string text = new DecisionPlayLayout().Render(observation);
        Assert.Contains("## 其他角色", text);
        Assert.Contains("阿白（`b`）：正在等待", text);
        Assert.DoesNotContain("private-item", text);
        Assert.DoesNotContain("77", text);
        Assert.DoesNotContain("91", text);
    }

    [Fact]
    public async Task Decision_layout_omits_character_who_left_the_place() {
        var initial = PlaygroundGameFactory.CreateSurvival(enableNeeds: false, characterCount: 2);
        var kernel = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor),
            characterController: new Commands(actor => actor == "a" ? "wait 1" : "go trail-2"));
        for (int index = 0; PlaygroundActions.CurrentPlace(kernel.World, "b") is not null && index < 8; index++) { await Step(kernel); }
        PlaygroundObservation observation = PlaygroundDecisionContext.Observe(kernel.World, "a");
        Assert.Empty(observation.OtherCharacters);
        string text = new DecisionPlayLayout().Render(observation);
        Assert.DoesNotContain("## 其他角色", text);
        Assert.DoesNotContain("阿白", text);
    }

    [Fact]
    public void Living_world_has_one_entry_with_explicit_optional_human_binding() {
        Assert.Null(Options.Parse([]).Llm);
        Assert.Null(Options.Parse([]).CharacterCount);
        Assert.Equal(2, Options.Parse(["--characters", "2"]).CharacterCount);
        Assert.Equal(3, Options.Parse(["--characters", "3"]).CharacterCount);
        Assert.Equal(1, Options.Parse(["--characters", "1"]).CharacterCount);
        Assert.Null(Options.Parse(["--llm"]).Llm!.HumanActor);
        Assert.Equal("b", Options.Parse(["--llm", "--human", "b"]).Llm!.HumanActor);
        foreach (string oldMode in new[] { "--play", "--survival", "--duo", "--trace", "--full-recompute" }) {
            Assert.Throws<ArgumentException>(() => Options.Parse([oldMode]));
        }
        Assert.Throws<ArgumentException>(() => Options.Parse(["--characters", "4"]));
        Assert.Throws<ArgumentException>(() => Options.Parse(["--human", "a"]));
    }

    [Fact]
    public async Task Three_character_host_routes_b_and_c_to_one_shared_model_budget_and_records_actual_actors() {
        using var files = new Files();
        var client = new Client("handoff model-private");
        using var output = new StringWriter();
        using var input = new StringReader("wait 10\n");
        LlmHostOptions options = files.Options() with { MaxCalls = 2, Gym = true, HumanActor = "a" };
        Assert.Equal(2, await LlmConsole.RunAsync(files.Store, options, default, output, client,
            characterCount: 3, inputReader: input));
        Assert.Equal(2, client.Requests.Count);
        using var saved = PlaygroundDurableHistory.Open(files.Store);
        Assert.Equal(3, saved.State.Characters.Count);
        Assert.All(new[] { "b", "c" }, actor => Assert.Equal("model-private", saved.State.Business.InteractionStates[actor].DraftHandoff));
        using var journal = PlaygroundOperationJournal.Open(Path.Combine(files.Store, "operations.jsonl"));
        Assert.Equal(new[] { "a", "b", "c" }, journal.Entries.Select(entry => entry.Received.ActorId).Order().ToArray());
        JsonElement host = File.ReadLines(options.LogPath!).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Single(line => line.GetProperty("Kind").GetString() == "host").GetProperty("Payload");
        Assert.Equal(new[] { "Human", "LLM", "LLM" }, host.GetProperty("Actors").EnumerateArray()
            .Select(actor => actor.GetProperty("Controller").GetString()).ToArray());
        using var gym = GymJournal.Open(GymSession.JournalPath(files.Store));
        JsonElement started = Assert.Single(gym.Records, record => record.Kind == "run-start").Payload;
        Assert.Equal(3, started.GetProperty("CharacterCount").GetInt32());
        Assert.Equal(new[] { "a", "b", "c" }, started.GetProperty("Actors").EnumerateArray()
            .Select(actor => actor.GetProperty("ActorId").GetString()).ToArray());
    }

    [Fact]
    public async Task Three_character_save_uses_saved_count_without_an_explicit_option() {
        using var files = new Files();
        var initial = PlaygroundGameFactory.CreateSurvival(enableNeeds: false, characterCount: 3);
        using (var save = PlaygroundDurableHistory.Create(files.Store, initial.World, initial.Cursor)) { }
        using var output = new StringWriter();
        Assert.Equal(0, await PlayConsole.RunAsync(files.Store, default,
            inputReader: new StringReader(""), outputWriter: output));
        Assert.Contains("山野镇", output.ToString());
        using (var restored = PlaygroundDurableHistory.Open(files.Store)) {
            Assert.Equal(3, restored.State.Characters.Count);
            Assert.Equal(PlaygroundExpeditionMap.ScenarioId, restored.State.Expedition!.ScenarioId);
        }
        using var mixedOutput = new StringWriter();
        Assert.Equal(0, await LlmConsole.RunAsync(files.Store, files.Options() with {
            HumanActor = "a", AuthFile = Path.Combine(files.Root, "missing-auth.json")
        }, default, mixedOutput, inputReader: new StringReader("")));
        Assert.Contains("a = Human，b = LLM，c = LLM", mixedOutput.ToString());
        Assert.DoesNotContain("credential/", mixedOutput.ToString());
    }

    [Fact]
    public async Task Mixed_host_routes_only_b_to_model_and_preserves_private_contexts_and_operation_ownership() {
        using var files = new Files();
        var client = new Client("handoff model-private");
        using var output = new StringWriter();
        using var input = new StringReader("handoff human-private\nwait 1\n");
        Assert.Equal(2, await LlmConsole.RunAsync(files.Store, files.Options() with { HumanActor = "a" }, default, output, client, characterCount: 2, inputReader: input));
        string material = Assert.IsType<ObservationMessage>(Assert.Single(client.Requests.Single().PromptPrefix.SharedContextMessages)).Content!;
        Assert.DoesNotContain("human-private", material);
        Assert.DoesNotContain("Console 输入", material);
        Assert.DoesNotContain("model-private", output.ToString());
        using var saved = PlaygroundDurableHistory.Open(files.Store);
        Assert.Equal("human-private", saved.State.Business.InteractionStates["a"].PendingAdvance!.Handoff);
        Assert.Equal("model-private", saved.State.Business.InteractionStates["b"].DraftHandoff);
        Assert.Equal(8 * PlaygroundTime.Hour, saved.State.Time.Ticks);
        using var journal = PlaygroundOperationJournal.Open(Path.Combine(files.Store, "operations.jsonl"));
        Assert.Equal(2, journal.Entries.Count(entry => entry.Received.ActorId == "a"));
        Assert.Single(journal.Entries, entry => entry.Received.ActorId == "b");
        Assert.All(journal.Entries, entry => Assert.Equal(PlaygroundOperationStatus.Succeeded, entry.Completed!.Status));
        Assert.DoesNotContain("宿主全局总结", output.ToString());
    }

    [Fact]
    public async Task Human_eof_before_first_model_turn_never_initializes_credentials() {
        using var files = new Files();
        using var input = new StringReader("");
        using var output = new StringWriter();
        Assert.Equal(0, await LlmConsole.RunAsync(files.Store, files.Options() with { HumanActor = "a", AuthFile = Path.Combine(files.Root, "missing-auth.json") },
            default, output, characterCount: 2, inputReader: input));
        Assert.Contains("human-session-ended", output.ToString());
        Assert.DoesNotContain("credential/", output.ToString());
    }

    [Fact]
    public async Task Wrong_single_mode_rejects_duo_pending_before_recovery_or_any_disk_write() {
        using var files = new Files();
        var initial = PlaygroundGameFactory.CreateSurvival(characterCount: 2);
        var memory = new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor);
        var kernel = PlaygroundEngine.CreateKernel(memory, characterController: new Commands(_ => "use item.water"));
        await Step(kernel);
        PlaygroundWorld opened = kernel.World;
        KernelCursor cursor = kernel.Cursor;
        await Step(kernel);
        using (var save = PlaygroundDurableHistory.Create(files.Store, opened, cursor)) { save.CommitEvent(kernel.LastCompletion!.Event); }
        Dictionary<string, byte[]> before = Directory.GetFiles(files.Store, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        await Assert.ThrowsAsync<ArgumentException>(() => PlayConsole.RunAsync(files.Store, default, inputReader: new StringReader(""), outputWriter: new StringWriter(), characterCount: 1));
        await Assert.ThrowsAsync<ArgumentException>(() => LlmConsole.RunAsync(files.Store, files.Options(), default, new StringWriter(), new Client("wait 1"), characterCount: 1));
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(files.Store, "*", SearchOption.AllDirectories).Order());
        foreach (var pair in before) { Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key)); }
        using var saved = PlaygroundDurableHistory.Open(files.Store);
        Assert.NotNull(saved.PendingEvent);
        Assert.Equal(1, saved.State.GetCharacter("a").Backpack.Count("item.water"));
    }

    [Fact]
    public async Task Shared_human_console_reaches_town_and_can_continue_after_cold_open() {
        using var files = new Files();
        var initial = PlaygroundGameFactory.CreateSurvival(enableNeeds: false, characterCount: 1);
        using (var save = PlaygroundDurableHistory.Create(files.Store, initial.World, initial.Cursor)) { }
        using var output = new StringWriter();
        using var input = new StringReader("go trail-1\ngo trail-3\ngo trail-4\n");
        Assert.Equal(0, await PlayConsole.RunAsync(files.Store, default, inputReader: input, outputWriter: output));
        ModelTime arrival;
        using (var final = PlaygroundDurableHistory.Open(files.Store)) {
            Assert.Equal("road", PlaygroundActions.CurrentPlace(final.State, "a"));
            Assert.True(final.State.GetCharacter("a").IsActive);
            Assert.Null(final.State.GetCharacter("a").Ending);
            arrival = final.State.Time;
        }
        Assert.DoesNotContain("宿主全局总结", output.ToString());
        using var reopenedOutput = new StringWriter();
        Assert.Equal(0, await PlayConsole.RunAsync(files.Store, default, inputReader: new StringReader("wait 1\n"), outputWriter: reopenedOutput));
        Assert.Contains("[Console 输入] a>", reopenedOutput.ToString());
        using var reopened = PlaygroundDurableHistory.Open(files.Store);
        Assert.True(reopened.State.Time > arrival);
        Assert.True(reopened.State.GetCharacter("a").IsActive);
        Assert.DoesNotContain("宿主全局总结", reopenedOutput.ToString());
    }

    [Fact]
    public async Task Dead_llm_character_is_not_called_and_human_continues_after_cold_open() {
        using var files = new Files();
        var initial = PlaygroundGameFactory.CreateSurvival(enableNeeds: false, characterCount: 2);
        var dying = new PlaygroundCharacter("b", "阿白", survival: PlaygroundSurvival.Create(initial.World.Time, food: 1));
        var business = initial.World.Business.Replace(initial.World.Time, changedCharacters: [dying]);
        var world = PlaygroundEngine.Rebuild(business, business.Time);
        var kernel = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(world, initial.Cursor),
            characterController: new Commands(_ => "wait 10"));
        for (int index = 0; kernel.World.GetCharacter("b").IsActive && index < 100; index++) { await Step(kernel); }
        Assert.Equal(PlaygroundExpeditionOutcome.Dead, kernel.World.GetCharacter("b").Ending?.Outcome);
        Assert.True(kernel.World.GetCharacter("a").IsActive);
        using (var save = PlaygroundDurableHistory.Create(files.Store, kernel.World, kernel.Cursor)) { }
        using var output = new StringWriter();
        Assert.Equal(0, await LlmConsole.RunAsync(files.Store, files.Options() with { HumanActor = "a", AuthFile = Path.Combine(files.Root, "missing-auth.json") },
            default, output, characterCount: 2, inputReader: new StringReader("")));
        Assert.Contains("[Console 输入] a>", output.ToString());
        Assert.DoesNotContain("宿主全局总结", output.ToString());
        Assert.DoesNotContain("credential/", output.ToString());
    }

    [Fact]
    public async Task All_dead_residents_produce_one_host_summary_without_prompting_after_cold_open() {
        using var files = new Files();
        var initial = PlaygroundGameFactory.CreateSurvival(enableNeeds: false, characterCount: 2);
        var dying = initial.World.Characters.Select(actor => new PlaygroundCharacter(actor.Id, actor.Name,
            survival: PlaygroundSurvival.Create(initial.World.Time, food: 1))).ToArray();
        var business = initial.World.Business.Replace(initial.World.Time, changedCharacters: dying);
        var world = PlaygroundEngine.Rebuild(business, business.Time);
        var kernel = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(world, initial.Cursor),
            characterController: new Commands(_ => "wait 10"));
        for (int index = 0; kernel.World.Characters.Any(actor => actor.IsActive) && index < 100; index++) { await Step(kernel); }
        Assert.All(kernel.World.Characters, actor => Assert.Equal(PlaygroundExpeditionOutcome.Dead, actor.Ending?.Outcome));
        using (var save = PlaygroundDurableHistory.Create(files.Store, kernel.World, kernel.Cursor)) { }
        for (int run = 0; run < 2; run++) {
            using var output = new StringWriter();
            Assert.Equal(0, await PlayConsole.RunAsync(files.Store, default,
                inputReader: new StringReader(""), outputWriter: output));
            Assert.Equal(1, output.ToString().Split("宿主全局总结").Length - 1);
            Assert.DoesNotContain("[Console 输入]", output.ToString());
        }
    }

    private static async Task Step(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel) =>
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));

    private sealed class Commands(Func<string, string> choose) : IPlaygroundCharacterController {
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PlaygroundControllerOperation("call-" + Guid.NewGuid().ToString("N"), choose(context.InteractionState.ActorId), Guid.NewGuid().ToString("N")));
    }
    private sealed class Client(string command) : ICompletionClient {
        public string Name => "duo-fixture";
        public string ApiSpecId => "fixture";
        public List<CompletionRequest> Requests { get; } = [];
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
            Requests.Add(request);
            return Task.FromResult(new CompletionResult(new ActionMessage([
                new ActionBlock.ToolCall(new("act", "duo-model-call", JsonSerializer.Serialize(new { command })))
            ]), new("fixture", "fixture", request.ModelId)));
        }
    }
    private sealed class Files : IDisposable {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "drama-duo-host-" + Guid.NewGuid().ToString("N"));
        public string Store => Path.Combine(Root, "game");
        public LlmHostOptions Options() => new(MaxCalls: 1, LogPath: Path.Combine(Root, Guid.NewGuid().ToString("N") + ".jsonl"));
        public void Dispose() { if (Directory.Exists(Root)) { Directory.Delete(Root, recursive: true); } }
    }
}
