using System.Text.Json;
using System.Text.RegularExpressions;
using Atelia.Completion.Abstractions;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Playground;

namespace DramaBoard.Playground.Cli.Tests;

/// <summary>Real disk and host boundaries with offline Completion responses, not online-model acceptance.</summary>
public sealed class TradeHostTests {
    [Fact]
    public async Task Human_offer_reaches_model_as_frozen_terms_and_settlement_survives_cold_open() {
        using var files = new Files();
        string? offeredId = null;
        var model = new Client(request => {
            string material = Material(request);
            offeredId = TradeId(material);
            Assert.Contains("支付 2 钱", material);
            Assert.Contains("item.food", material);
            Assert.Contains("尚未成交", material);
            return "accept " + offeredId;
        });
        using var output = new StringWriter();
        Assert.Equal(0, await PlaygroundHost.RunAsync(files.Store, files.Options() with { Controls = HostFixture.Humans("a") }, default, output, model,
            characterCount: 2, inputReader: new StringReader("offer b item.food 1 for money 2\n:exit\n")));
        Assert.Single(model.Requests);
        Assert.NotNull(offeredId);
        string expected;
        using (var saved = PlaygroundDurableHistory.Open(files.Store)) {
            Assert.Equal(14, saved.State.GetCharacter("a").Money);
            Assert.Equal(2, saved.State.GetCharacter("a").Backpack.Count("item.food"));
            Assert.Equal(10, saved.State.GetCharacter("b").Money);
            Assert.Equal(4, saved.State.GetCharacter("b").Backpack.Count("item.food"));
            Assert.Equal(8 * PlaygroundTime.Hour, saved.State.Time.Ticks);
            PlaygroundTradeOffer retained = Assert.Single(saved.State.Business.Economy!.Trades.Offers);
            Assert.Equal(offeredId, retained.Id);
            Assert.Equal(PlaygroundTradeStatus.Accepted, retained.Status);
            expected = CharacterContextRenderer.RenderFull(saved.State.Business.InteractionStates["a"], new DecisionPlayLayout());
            Assert.Contains(offeredId!, expected);
            Assert.Contains("已成交", expected);
        }
        Assert.Contains("已成交", output.ToString());
        using (var journalStore = PlaygroundHostJournal.Open(PlaygroundHostJournal.JournalPath(files.Store))) {
            var journal = new PlaygroundOperationAudit(journalStore, HostFixture.BranchId(files.Store), "test-run");
            var accepted = Assert.Single(journal.Entries, entry => entry.Received.RawInput == "accept " + offeredId);
            Assert.Equal("b", accepted.Received.ActorId);
            Assert.Equal(PlaygroundOperationStatus.Succeeded, accepted.Completed!.Status);
            Assert.Contains(offeredId!, accepted.Completed!.Result);
        }
        var resumed = new Client(_ => "handoff trade-settled-once");
        Assert.Equal(2, await PlaygroundHost.RunAsync(files.Store, files.Options(), default, output, resumed));
        Assert.Equal(expected, Material(Assert.Single(resumed.Requests)));
        using var restored = PlaygroundDurableHistory.Open(files.Store);
        Assert.Equal(14, restored.State.GetCharacter("a").Money);
        Assert.Equal(10, restored.State.GetCharacter("b").Money);
        Assert.Equal(2, restored.State.GetCharacter("a").Backpack.Count("item.food"));
        Assert.Equal(4, restored.State.GetCharacter("b").Backpack.Count("item.food"));
    }

    [Fact]
    public async Task Cold_open_model_acceptance_preserves_production_deadline_and_reserved_inputs() {
        using var files = new Files();
        var initial = PlaygroundGameFactory.CreateLivingWorld(characterCount: 2, enableWeather: false);
        var controller = new Commands(new Dictionary<string, Queue<string>> {
            ["a"] = new(["go home-workshop", "produce ration 1"]),
            ["b"] = new(["go home-workshop", "offer a money 2 for item.water 1"])
        });
        var kernel = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor),
            characterController: controller);
        bool Interrupted() => kernel.World.GetCharacter("a").Activity?.Kind == PlaygroundActivityKind.Produce &&
            kernel.World.Business.Economy!.Trades.Offers.Count > 0 &&
            kernel.World.Business.InteractionStates["a"].CurrentContext?.Status == CharacterContextStatus.Open;
        for (int step = 0; !Interrupted() && step < 100; step++) {
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
        }
        Assert.True(Interrupted());
        PlaygroundActivity activity = Assert.IsType<PlaygroundActivity>(kernel.World.GetCharacter("a").Activity);
        string expected = CharacterContextRenderer.RenderFull(kernel.World.Business.InteractionStates["a"], new DecisionPlayLayout());
        Assert.Contains("正在生产", expected);
        Assert.Contains("尚未成交", expected);
        using (var saved = PlaygroundDurableHistory.Create(files.Store, kernel.World, kernel.Cursor)) { }
        var model = new Client(request => "accept " + TradeId(Material(request)));
        using var output = new StringWriter();
        Assert.Equal(2, await PlaygroundHost.RunAsync(files.Store, files.Options(), default, output, model));
        Assert.Equal(expected, Material(Assert.Single(model.Requests)));
        using var restored = PlaygroundDurableHistory.Open(files.Store);
        PlaygroundCharacter actor = restored.State.GetCharacter("a");
        Assert.Equal(PlaygroundActivityKind.Produce, actor.Activity!.Kind);
        Assert.Equal(activity.Id, actor.Activity.Id);
        Assert.Equal(activity.StartedAt, actor.Activity.StartedAt);
        Assert.Equal(activity.Due, actor.Activity.Due);
        Assert.Equal(activity.Production, actor.Activity.Production);
        Assert.Equal(1, actor.Backpack.Count("item.food"));
        Assert.Equal(0, actor.Backpack.Count("item.ration"));
        Assert.Equal(2, actor.Backpack.Count("item.water"));
        Assert.Equal(14, actor.Money);
        Assert.Equal(kernel.World.Time, restored.State.Time);
        Assert.Equal(PlaygroundTradeStatus.Accepted, Assert.Single(restored.State.Business.Economy!.Trades.Offers).Status);
        using var operationsStore = PlaygroundHostJournal.Open(PlaygroundHostJournal.JournalPath(files.Store));
        var operations = new PlaygroundOperationAudit(operationsStore, restored.CurrentBranch.BranchId, "test-run");
        PlaygroundOperationEntry operation = Assert.Single(operations.Entries);
        Assert.Equal("a", operation.Received.ActorId);
        Assert.Equal(PlaygroundOperationStatus.Succeeded, operation.Completed!.Status);
    }

    private static string Material(CompletionRequest request) =>
        Assert.IsType<ObservationMessage>(Assert.Single(request.PromptPrefix.SharedContextMessages)).Content!;
    // The fake model chooses an ID exclusively from material delivered through the public controller boundary.
    private static string TradeId(string material) {
        Match match = Regex.Match(material, @"trade:[a-z]+:\d+:\d+", RegexOptions.CultureInvariant);
        Assert.True(match.Success, "The delivered personal material must identify the offer to accept.");
        return match.Value;
    }
    private sealed class Client(Func<CompletionRequest, string> choose) : ICompletionClient {
        public string Name => "trade-fixture";
        public string ApiSpecId => "fixture";
        public List<CompletionRequest> Requests { get; } = [];
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default) {
            Requests.Add(request);
            string command = choose(request);
            return Task.FromResult(new CompletionResult(new ActionMessage([
                new ActionBlock.ToolCall(new("act", "trade-fixture-call", JsonSerializer.Serialize(new { command })))
            ]), new("fixture", "fixture", request.ModelId)));
        }
    }
    private sealed class Commands(Dictionary<string, Queue<string>> commands) : IPlaygroundCharacterController {
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PlaygroundControllerOperation(Guid.NewGuid().ToString("N"),
                commands[context.InteractionState.ActorId].Dequeue(), Guid.NewGuid().ToString("N")));
    }
    private sealed class Files : IDisposable {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "drama-trade-host-" + Guid.NewGuid().ToString("N"));
        public string Store => Path.Combine(_root, "game");
        public PlaygroundHostOptions Options() => new(MaxCalls: 1, LogPath: Path.Combine(_root, Guid.NewGuid().ToString("N") + ".jsonl"));
        public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); } }
    }
}
