using System.Text.Json;
using Atelia.Completion.Abstractions;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.ResourceCycle;

namespace DramaBoard.ResourceCycle.Cli.Tests;

/// <summary>Real persistent hosts with offline Completion fixtures; not online-model evidence.</summary>
public sealed class EconomyHostTests {
    [Fact]
    public async Task Model_can_buy_produce_sell_and_consume_then_resume_from_exact_frozen_material() {
        using var files = new Files();
        var client = new Client("go market-home", "buy item.food 2", "go market-workshop", "produce ration 1",
            "go market-workshop", "sell item.ration 1", "use item.ration", "handoff economic-loop-complete");
        using var output = new StringWriter();
        Assert.Equal(2, await LlmConsole.RunAsync(files.Store, files.Options(8), default, output, client, characterCount: 1));
        Assert.Equal(8, client.Requests.Count);
        string expected;
        using (var saved = CycleDurableHistory.Open(files.Store)) {
            CycleCharacter actor = saved.State.GetCharacter("a");
            Assert.Equal(11, actor.Money);
            Assert.Equal(3, actor.Backpack.Count("item.food"));
            Assert.Equal(0, actor.Backpack.Count("item.ration"));
            Assert.Null(actor.Activity);
            Assert.Equal("market", CycleActions.CurrentPlace(saved.State, "a"));
            Assert.True(actor.IsActive);
            CharacterInteractionState interaction = saved.State.Business.InteractionStates["a"];
            Assert.Equal("economic-loop-complete", interaction.DraftHandoff);
            expected = CharacterContextRenderer.RenderFull(interaction, new DecisionPlayLayout());
            Assert.Contains("sell item.ration 1", expected);
            Assert.Contains("use item.ration", expected);
            Assert.Contains("自己的资金余额", expected);
            Assert.Contains("11钱", expected);
        }
        using (var operations = CycleOperationJournal.Open(Path.Combine(files.Store, "operations.jsonl"))) {
            Assert.Equal(8, operations.Entries.Count);
            Assert.All(operations.Entries, entry => Assert.Equal(CycleOperationStatus.Succeeded, entry.Completed!.Status));
        }
        var resumed = new Client("handoff same-world-after-cold-open");
        Assert.Equal(2, await LlmConsole.RunAsync(files.Store, files.Options(1), default, output, resumed));
        Assert.Equal(expected, Material(Assert.Single(resumed.Requests)));
        using var restored = CycleDurableHistory.Open(files.Store);
        Assert.Equal(11, restored.State.GetCharacter("a").Money);
        Assert.Equal(3, restored.State.GetCharacter("a").Backpack.Count("item.food"));
        Assert.Equal("same-world-after-cold-open", restored.State.Business.InteractionStates["a"].DraftHandoff);
    }

    [Fact]
    public async Task Money_gift_wakes_human_preserves_wait_and_survives_cold_open_with_exact_personal_material() {
        using var files = new Files();
        var client = new Client("give a money 5");
        using var output = new StringWriter();
        Assert.Equal(0, await LlmConsole.RunAsync(files.Store, files.Options(1) with { HumanActor = "a" },
            default, output, client, characterCount: 2, inputReader: new StringReader("wait 4\n:exit\n")));
        Assert.Single(client.Requests);
        string expected;
        long due;
        using (var saved = CycleDurableHistory.Open(files.Store)) {
            CycleCharacter actor = saved.State.GetCharacter("a");
            Assert.Equal(17, actor.Money);
            Assert.Equal(7, saved.State.GetCharacter("b").Money);
            Assert.Equal(8 * CycleTime.Hour, saved.State.Time.Ticks);
            Assert.Equal(CycleActivityKind.Wait, actor.Activity!.Kind);
            due = actor.Activity.Due.Ticks;
            Assert.Equal(12 * CycleTime.Hour, due);
            CharacterInteractionState human = saved.State.Business.InteractionStates["a"];
            expected = CharacterContextRenderer.RenderFull(human, new DecisionPlayLayout());
            Assert.Contains("17 钱", expected);
            Assert.Contains("5 钱", expected);
            Assert.DoesNotContain("余额：7 钱", expected);
        }
        Assert.Contains("5 钱", output.ToString());
        Assert.DoesNotContain("余额：7 钱", output.ToString());
        var resumed = new Client("handoff gift-received");
        Assert.Equal(2, await LlmConsole.RunAsync(files.Store, files.Options(1), default, output, resumed));
        Assert.Equal(expected, Material(Assert.Single(resumed.Requests)));
        using var restored = CycleDurableHistory.Open(files.Store);
        Assert.Equal(due, restored.State.GetCharacter("a").Activity!.Due.Ticks);
        Assert.Equal(17, restored.State.GetCharacter("a").Money);
        Assert.Equal(7, restored.State.GetCharacter("b").Money);
    }

    [Fact]
    public async Task Model_can_cancel_an_interrupted_production_after_cold_open_and_recover_reserved_inputs() {
        using var files = new Files();
        var initial = CycleGameFactory.CreateLivingWorld(characterCount: 2, enableWeather: false);
        var controller = new Commands(new Dictionary<string, Queue<string>> {
            ["a"] = new(["go home-workshop", "produce ration 1"]),
            ["b"] = new(["go home-workshop", "say a production-checkpoint"])
        });
        var kernel = CycleEngine.CreateKernel(new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor),
            characterController: controller);
        bool Interrupted() => kernel.World.GetCharacter("a").Activity?.Kind == CycleActivityKind.Produce &&
            kernel.World.GetCharacter("a").Communication.AttentionPending &&
            kernel.World.Business.InteractionStates["a"].CurrentContext?.Status == CharacterContextStatus.Open;
        for (int step = 0; !Interrupted() && step < 100; step++) {
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
        }
        Assert.True(Interrupted());
        Assert.Equal(1, kernel.World.GetCharacter("a").Backpack.Count("item.food"));
        string expected = CharacterContextRenderer.RenderFull(kernel.World.Business.InteractionStates["a"], new DecisionPlayLayout());
        Assert.Contains("正在生产", expected);
        Assert.Contains("production-checkpoint", expected);
        using (var saved = CycleDurableHistory.Create(files.Store, kernel.World, kernel.Cursor)) { }
        var client = new Client("cancel");
        using var output = new StringWriter();
        Assert.Equal(2, await LlmConsole.RunAsync(files.Store, files.Options(1), default, output, client));
        Assert.Equal(expected, Material(Assert.Single(client.Requests)));
        using var restored = CycleDurableHistory.Open(files.Store);
        CycleCharacter actor = restored.State.GetCharacter("a");
        Assert.Null(actor.Activity);
        Assert.Equal(3, actor.Backpack.Count("item.food"));
        Assert.Equal(0, actor.Backpack.Count("item.ration"));
        Assert.Equal(kernel.World.Time, restored.State.Time);
        Assert.Equal(12, actor.Money);
        using var journal = CycleOperationJournal.Open(Path.Combine(files.Store, "operations.jsonl"));
        CycleOperationEntry cancelled = Assert.Single(journal.Entries);
        Assert.Equal("a", cancelled.Received.ActorId);
        Assert.Equal("cancel", cancelled.Received.RawInput);
        Assert.Equal(CycleOperationStatus.Succeeded, cancelled.Completed!.Status);
    }

    private static string Material(CompletionRequest request) =>
        Assert.IsType<ObservationMessage>(Assert.Single(request.PromptPrefix.SharedContextMessages)).Content!;

    private sealed class Client(params string[] commands) : ICompletionClient {
        private readonly Queue<string> _commands = new(commands);
        public string Name => "economy-fixture";
        public string ApiSpecId => "fixture";
        public List<CompletionRequest> Requests { get; } = [];
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default) {
            Requests.Add(request);
            string command = _commands.Dequeue();
            return Task.FromResult(new CompletionResult(new ActionMessage([
                new ActionBlock.ToolCall(new("act", "economy-fixture-call", JsonSerializer.Serialize(new { command })))
            ]), new("fixture", "fixture", request.ModelId)));
        }
    }
    private sealed class Commands(Dictionary<string, Queue<string>> commands) : ICycleCharacterController {
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CycleControllerOperation(Guid.NewGuid().ToString("N"),
                commands[context.InteractionState.ActorId].Dequeue(), Guid.NewGuid().ToString("N")));
    }
    private sealed class Files : IDisposable {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "drama-economy-host-" + Guid.NewGuid().ToString("N"));
        public string Store => Path.Combine(_root, "game");
        public LlmHostOptions Options(int maxCalls) => new(MaxCalls: maxCalls,
            LogPath: Path.Combine(_root, Guid.NewGuid().ToString("N") + ".jsonl"));
        public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); } }
    }
}
