using Atelia.Completion.Abstractions;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Playground;

namespace DramaBoard.Playground.Cli.Tests;

public sealed class ContentHostTests {
    [Fact]
    public void Content_selection_is_explicit_and_is_not_an_administrative_option() {
        Assert.Null(Options.Parse([]).Content);
        Assert.Equal("gentle", Options.Parse(["--content", "gentle"]).Content);
        Assert.Equal("hillside", Options.Parse(["--llm", "--content", "hillside"]).Llm!.Content);
        Assert.Throws<ArgumentException>(() => Options.Parse(["--content", "unknown"]));
        Assert.Throws<ArgumentException>(() => Options.Parse(["--content", "gentle", "--content", "hillside"]));
        Assert.Throws<ArgumentException>(() => Options.Parse(["--store", "ignored", "--branches", "--content", "gentle"]));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task New_content_uses_selected_count_and_cold_open_uses_saved_definition(int count) {
        using var files = new Files();
        using var output = new StringWriter();
        PlaygroundWorldDefinition expected = PlaygroundWorldDefinitions.Named("gentle", characterCount: count);
        Assert.Equal(0, await PlayConsole.RunAsync(files.Store, default, inputReader: new StringReader(":quit\n"),
            outputWriter: output, characterCount: count, content: "gentle"));
        using (var created = PlaygroundDurableHistory.Open(files.Store)) {
            Assert.Equal(expected.Fingerprint, created.State.Definition.Fingerprint);
            Assert.Equal(expected.ContentId, created.State.Definition.ContentId);
            Assert.Equal(count, created.State.Definition.InitialCharacters.Count);
            Assert.Equal(expected.InitialCharacters[0].Money, created.State.GetCharacter("a").Money);
        }
        foreach (string? content in new string?[] { null, "gentle" }) {
            Assert.Equal(0, await PlayConsole.RunAsync(files.Store, default, inputReader: new StringReader(":quit\n"),
                outputWriter: output, content: content));
            using var restored = PlaygroundDurableHistory.Open(files.Store);
            Assert.Equal(expected.Fingerprint, restored.State.Definition.Fingerprint);
            Assert.Equal(count, restored.State.Characters.Count);
        }
        await Assert.ThrowsAsync<ArgumentException>(() => PlayConsole.RunAsync(files.Store, default,
            inputReader: new StringReader(""), outputWriter: output, characterCount: count == 1 ? 2 : 1, content: "gentle"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_mismatch_rejects_before_pending_recovery_or_host_writes_and_correct_resume_recovers_once(bool model) {
        using var files = new Files();
        var initial = PlaygroundGameFactory.Create(PlaygroundWorldDefinitions.Named("gentle", characterCount: 1, enableWeather: false));
        var planned = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor),
            characterController: new Handoff());
        PlaygroundWorld before = initial.World;
        KernelCursor beforeCursor = initial.Cursor;
        for (int step = 0; step < 8; step++) {
            before = planned.World;
            beforeCursor = planned.Cursor;
            Assert.Equal(StepStatus.Committed, await planned.StepAsync(new ModelTime(long.MaxValue)));
            if (planned.LastCompletion!.Event.Facts.Any(fact => fact.Kind == PlaygroundEventKind.HandoffEdited)) { break; }
        }
        Assert.Contains(planned.LastCompletion!.Event.Facts, fact => fact.Kind == PlaygroundEventKind.HandoffEdited);
        using (var pending = PlaygroundDurableHistory.Create(files.Store, before, beforeCursor)) { pending.CommitEvent(planned.LastCompletion.Event); }
        Dictionary<string, byte[]> snapshot = files.Snapshot();
        var client = new NeverCalled();
        using var output = new StringWriter();
        if (model) {
            await Assert.ThrowsAsync<ArgumentException>(() => LlmConsole.RunAsync(files.Store,
                new(Content: "hillside", LogPath: files.Log), default, output, client));
        } else {
            await Assert.ThrowsAsync<ArgumentException>(() => PlayConsole.RunAsync(files.Store, default,
                inputReader: new StringReader(""), outputWriter: output, content: "hillside"));
        }
        Assert.Equal(0, client.Calls);
        Dictionary<string, byte[]> after = files.Snapshot();
        Assert.Equal(snapshot.Keys.Order(), after.Keys.Order());
        foreach (string path in snapshot.Keys) { Assert.Equal(snapshot[path], after[path]); }
        using (var stillPending = PlaygroundDurableHistory.Open(files.Store)) {
            Assert.NotNull(stillPending.PendingEvent);
            Assert.Equal(beforeCursor, stillPending.Cursor);
        }
        var options = new LlmHostOptions(LogPath: files.Log, Until: initial.World.Time);
        Assert.Equal(0, await LlmConsole.RunAsync(files.Store, options, default, output, client));
        Assert.Equal(0, client.Calls);
        using (var recovered = PlaygroundDurableHistory.Open(files.Store)) {
            Assert.Null(recovered.PendingEvent);
            Assert.Equal(planned.Cursor, recovered.Cursor);
            Assert.Equal("content-pending-once", recovered.State.Business.InteractionStates["a"].DraftHandoff);
        }
        Assert.Equal(0, await LlmConsole.RunAsync(files.Store, options with { LogPath = files.Log + ".second", Content = "gentle" },
            default, output, client));
        using var reopened = PlaygroundDurableHistory.Open(files.Store);
        Assert.Equal(planned.Cursor, reopened.Cursor);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public void Both_layouts_render_custom_frozen_use_effects_without_default_content_fallback() {
        PlaygroundWorldDefinition definition = PlaygroundWorldDefinitions.Named("gentle", characterCount: 1).Change(
            waterSupplies: new Dictionary<string, int> { ["item.water"] = 17 }, name: "自定义补给世界");
        PlaygroundObservation observation = PlaygroundDecisionContext.Observe(PlaygroundGameFactory.Create(definition).World, "a");
        string effect = observation.ItemUseDescriptions["item.water"];
        Assert.Contains("17", effect);
        Assert.Equal("自定义补给世界", observation.WorldName);
        foreach (IPlayLayout layout in new IPlayLayout[] { new ClassicPlayLayout(), new DecisionPlayLayout() }) {
            string rendered = layout.Render(observation);
            Assert.Contains(effect.TrimEnd('。'), rendered);
            Assert.Contains(observation.WorldName, rendered);
        }
    }

    private sealed class Handoff : IPlaygroundCharacterController {
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PlaygroundControllerOperation("pending-tool", "handoff content-pending-once", "pending-record"));
    }
    private sealed class NeverCalled : ICompletionClient {
        public int Calls { get; private set; }
        public string Name => "content-no-call";
        public string ApiSpecId => "fixture";
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default) {
            Calls++;
            throw new InvalidOperationException("Identity rejection or a paused recovery must not call a model.");
        }
    }
    private sealed class Files : IDisposable {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "drama-content-host-" + Guid.NewGuid().ToString("N"));
        public string Store => Path.Combine(_root, "game");
        public string Log => Path.Combine(_root, "model.jsonl");
        public Dictionary<string, byte[]> Snapshot() => Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(_root, path), File.ReadAllBytes, StringComparer.Ordinal);
        public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); } }
    }
}
