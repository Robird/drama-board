using System.Text.Json;
using Atelia.Completion.Abstractions;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.ResourceCycle;

namespace DramaBoard.ResourceCycle.Cli.Tests;

public sealed class DuoHostTests {
    [Fact]
    public void Decision_layout_shows_same_place_public_status_without_other_inventory_or_skill() {
        var initial = CycleGameFactory.CreateSurvival(enableNeeds: false, characterCount: 2);
        var other = new CycleCharacter("b", "阿白", skillLevel: 77,
            backpack: new(new Dictionary<string, int> { ["private-item"] = 91 }),
            activity: new(CycleActivityKind.Wait, 1, initial.World.Time, CycleTime.Hour,
                new(initial.World.Time.Ticks + CycleTime.Hour)), nextActivityId: 2);
        CycleBusinessState business = initial.World.Business.Replace(initial.World.Time, changedCharacters: [other]);
        CycleWorld world = CycleEngine.Rebuild(business, business.Time);
        CycleObservation observation = CycleDecisionContext.Observe(world, "a");
        string text = new DecisionPlayLayout().Render(observation);
        Assert.Contains("## 其他角色", text);
        Assert.Contains("阿白（`b`）：正在等待", text);
        Assert.DoesNotContain("private-item", text);
        Assert.DoesNotContain("77", text);
        Assert.DoesNotContain("91", text);
    }

    [Fact]
    public async Task Decision_layout_omits_character_who_left_the_place() {
        var initial = CycleGameFactory.CreateSurvival(enableNeeds: false, characterCount: 2);
        var kernel = CycleEngine.CreateKernel(new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor),
            characterController: new Commands(actor => actor == "a" ? "wait 1" : "go trail-2"));
        for (int index = 0; CycleActions.CurrentPlace(kernel.World, "b") is not null && index < 8; index++) { await Step(kernel); }
        CycleObservation observation = CycleDecisionContext.Observe(kernel.World, "a");
        Assert.Empty(observation.OtherCharacters);
        string text = new DecisionPlayLayout().Render(observation);
        Assert.DoesNotContain("## 其他角色", text);
        Assert.DoesNotContain("阿白", text);
    }

    [Fact]
    public void Duo_has_explicit_human_and_mixed_modes() {
        Assert.True(Options.Parse(["--duo"]).Duo);
        Assert.Null(Options.Parse(["--duo"]).Llm);
        Assert.NotNull(Options.Parse(["--duo", "--llm"]).Llm);
        Assert.Throws<ArgumentException>(() => Options.Parse(["--duo", "--play"]));
        Assert.Throws<ArgumentException>(() => Options.Parse(["--duo", "--survival"]));
        Assert.Throws<ArgumentException>(() => Options.Parse(["--duo", "--trace"]));
    }

    [Fact]
    public async Task Mixed_host_routes_only_b_to_model_and_preserves_private_contexts_and_operation_ownership() {
        using var files = new Files();
        var client = new Client("handoff model-private");
        using var output = new StringWriter();
        using var input = new StringReader("handoff human-private\nwait 1\n");
        Assert.Equal(2, await LlmConsole.RunAsync(files.Store, files.Options(), default, output, client, duo: true, inputReader: input));
        string material = Assert.IsType<ObservationMessage>(Assert.Single(client.Requests.Single().PromptPrefix.SharedContextMessages)).Content!;
        Assert.DoesNotContain("human-private", material);
        Assert.DoesNotContain("Console 输入", material);
        using var saved = CycleDurableHistory.Open(files.Store);
        Assert.Equal("human-private", saved.State.Business.InteractionStates["a"].PendingAdvance!.Handoff);
        Assert.Equal("model-private", saved.State.Business.InteractionStates["b"].DraftHandoff);
        Assert.Equal(15 * CycleTime.Hour, saved.State.Time.Ticks);
        using var journal = CycleOperationJournal.Open(Path.Combine(files.Store, "operations.jsonl"));
        Assert.Equal(2, journal.Entries.Count(entry => entry.Received.ActorId == "a"));
        Assert.Single(journal.Entries, entry => entry.Received.ActorId == "b");
        Assert.All(journal.Entries, entry => Assert.Equal(CycleOperationStatus.Succeeded, entry.Completed!.Status));
        Assert.DoesNotContain("宿主全局总结", output.ToString());
    }

    [Fact]
    public async Task Human_eof_before_first_model_turn_never_initializes_credentials() {
        using var files = new Files();
        using var input = new StringReader("");
        using var output = new StringWriter();
        Assert.Equal(0, await LlmConsole.RunAsync(files.Store, files.Options() with { AuthFile = Path.Combine(files.Root, "missing-auth.json") },
            default, output, duo: true, inputReader: input));
        Assert.Contains("human-session-ended", output.ToString());
        Assert.DoesNotContain("credential/", output.ToString());
    }

    [Fact]
    public async Task Wrong_single_mode_rejects_duo_pending_before_recovery_or_any_disk_write() {
        using var files = new Files();
        var initial = CycleGameFactory.CreateSurvival(characterCount: 2);
        var memory = new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor);
        var kernel = CycleEngine.CreateKernel(memory, characterController: new Commands(_ => "use item.water"));
        await Step(kernel);
        CycleWorld opened = kernel.World;
        KernelCursor cursor = kernel.Cursor;
        await Step(kernel);
        using (var save = CycleDurableHistory.Create(files.Store, opened, cursor)) { save.CommitEvent(kernel.LastCompletion!.Event); }
        Dictionary<string, byte[]> before = Directory.GetFiles(files.Store, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        await Assert.ThrowsAsync<ArgumentException>(() => PlayConsole.RunAsync(files.Store, default, survival: true, inputReader: new StringReader(""), outputWriter: new StringWriter()));
        await Assert.ThrowsAsync<ArgumentException>(() => LlmConsole.RunAsync(files.Store, files.Options(), default, new StringWriter(), new Client("wait 1")));
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(files.Store, "*", SearchOption.AllDirectories).Order());
        foreach (var pair in before) { Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key)); }
        using var saved = CycleDurableHistory.Open(files.Store);
        Assert.NotNull(saved.PendingEvent);
        Assert.Equal(1, saved.State.GetCharacter("a").Backpack.Count("item.water"));
    }

    [Fact]
    public async Task Shared_human_console_continues_after_a_escapes_and_prints_one_global_summary_only_at_all_ended() {
        using var files = new Files();
        var initial = CycleGameFactory.CreateSurvival(enableNeeds: false, characterCount: 2);
        using (var save = CycleDurableHistory.Create(files.Store, initial.World, initial.Cursor)) { }
        using var output = new StringWriter();
        using var input = new StringReader("go trail-1\nwait 10\ngo trail-3\ngo trail-4\ngo trail-1\ngo trail-3\ngo trail-4\n");
        Assert.Equal(0, await PlayConsole.RunAsync(files.Store, default, survival: true, inputReader: input, outputWriter: output, duo: true));
        using (var final = CycleDurableHistory.Open(files.Store)) {
            Assert.All(final.State.Characters, actor => Assert.Equal(CycleExpeditionOutcome.Escaped, actor.Ending?.Outcome));
            Assert.True(final.State.GetCharacter("a").Ending!.At < final.State.GetCharacter("b").Ending!.At);
        }
        string text = output.ToString();
        Assert.Equal(1, text.Split("宿主全局总结").Length - 1);
        Assert.Contains("全部获救", text);
        using var reopenedOutput = new StringWriter();
        Assert.Equal(0, await PlayConsole.RunAsync(files.Store, default, survival: true, inputReader: new StringReader(""), outputWriter: reopenedOutput, duo: true));
        Assert.DoesNotContain("[Console 输入]", reopenedOutput.ToString());
    }

    [Fact]
    public async Task Ended_llm_character_is_not_bound_and_human_continues_after_cold_open() {
        using var files = new Files();
        var initial = CycleGameFactory.CreateSurvival(enableNeeds: false, characterCount: 2);
        var route = new Queue<string>(["go trail-1", "go trail-3", "go trail-4"]);
        var kernel = CycleEngine.CreateKernel(new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor),
            characterController: new Commands(actor => actor == "a" ? "wait 10" : route.Dequeue()));
        for (int index = 0; kernel.World.GetCharacter("b").IsActive && index < 100; index++) { await Step(kernel); }
        Assert.False(kernel.World.GetCharacter("b").IsActive);
        using (var save = CycleDurableHistory.Create(files.Store, kernel.World, kernel.Cursor)) { }
        using var input = new StringReader("");
        using var output = new StringWriter();
        Assert.Equal(0, await LlmConsole.RunAsync(files.Store, files.Options() with { AuthFile = Path.Combine(files.Root, "missing-auth.json") },
            default, output, duo: true, inputReader: input));
        Assert.Contains("[Console 输入] a>", output.ToString());
        Assert.DoesNotContain("宿主全局总结", output.ToString());
        Assert.DoesNotContain("credential/", output.ToString());
    }

    private static async Task Step(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel) =>
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));

    private sealed class Commands(Func<string, string> choose) : ICycleCharacterController {
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CycleControllerOperation("call-" + Guid.NewGuid().ToString("N"), choose(context.InteractionState.ActorId), Guid.NewGuid().ToString("N")));
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
