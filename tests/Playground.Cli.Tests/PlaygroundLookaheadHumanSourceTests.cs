using System.Collections.Concurrent;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using DramaBoard.CharacterControl.Completion;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.Playground.Cli.Tests;

public sealed class PlaygroundLookaheadHumanSourceTests {
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);
    private static readonly IReadOnlyDictionary<string, CharacterControlBinding> Bindings = new Dictionary<string, CharacterControlBinding> {
        ["a"] = new CharacterControlBinding.HumanConsole(), ["b"] = new CharacterControlBinding.Llm("cold")
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task First_Human_wait_adopts_cold_B_only_when_enabled(bool enabled) {
        var history = await Source();
        // Warm JIT only, never the runtime binding resolver.
        await PlaygroundLookaheadProjection.ProjectAsync(history.State, history.Cursor, "a", Bindings, default, preparationLimit: Limit);
        var client = new Client();
        using var reader = new Reader();
        using var output = new StringWriter();
        using var stop = new CancellationTokenSource(Limit);
        var world = history.State;
        var cursor = history.Cursor;
        int count = history.CompletedEvents.Count;
        var run = PlaygroundHost.RunAsync(history, Bindings, new PlaygroundHostOptions(MaxCalls: 1, MaxSteps: 1000, Lookahead: enabled), stop.Token, output, client, reader);
        await reader.Started.Task.WaitAsync(stop.Token);
        if (enabled) {
            await client.Started.Task.WaitAsync(stop.Token);
            Assert.Same(world, history.State);
            Assert.Equal(cursor, history.Cursor);
            Assert.Equal(count, history.CompletedEvents.Count);
            Assert.Null(history.PendingEvent);
            Assert.False(run.IsCompleted);
        } else { Assert.Empty(client.Requests); }
        reader.Release.TrySetResult("wait 1");
        Assert.Equal(0, await run.WaitAsync(stop.Token));
        Assert.Single(client.Requests);
        Assert.Equal(new[] { "a", "b" }, history.CompletedEvents.SelectMany(e => e.Facts).Where(f => f.OperationRecordId is not null).Select(f => f.ActorId));
    }

    [Fact]
    public async Task Human_EOF_immediately_does_not_resolve_cold_binding() {
        var history = await Source();
        var client = new Client();
        using var input = new StringReader("");
        using var output = new StringWriter();
        Assert.Equal(0, await PlaygroundHost.RunAsync(history, Bindings,
            new PlaygroundHostOptions(MaxCalls: 1, MaxSteps: 1000, Lookahead: true), default, output, client, input));
        Assert.Empty(client.Requests);
        Assert.DoesNotContain(history.CompletedEvents.SelectMany(e => e.Facts), f => f.OperationRecordId is not null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Human_finishing_or_cancelling_retires_slow_cold_resolution_before_publication(bool cancel) {
        var history = await Source();
        await PlaygroundLookaheadProjection.ProjectAsync(history.State, history.Cursor, "a", Bindings, default, preparationLimit: Limit);
        var resolving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client();
        using var stop = new CancellationTokenSource(Limit);
        await using var controller = new PlaygroundLookaheadController(() => (history.State, history.Cursor), Bindings,
            async (_, _) => { resolving.TrySetResult(); await release.Task; return new(client, "fixture", "cold"); },
            new LlmControllerOptions { MaxCalls = 1 }, _ => { });
        var human = new Human();
        var run = controller.ChooseHumanCommandAsync(PlaygroundDecisionContext.Create(history.State, "a"), human, stop.Token).AsTask();
        await resolving.Task.WaitAsync(Limit);
        Assert.True(human.Started);
        if (cancel) { stop.Cancel(); human.Result.TrySetCanceled(stop.Token); }
        else { human.Result.TrySetResult(new("human", "wait 1", "human")); }
        // Ignore cancellation in the resolver deliberately. The late result cannot admit a request.
        release.TrySetResult();
        if (cancel) { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); }
        else { Assert.Equal("wait 1", (await run.WaitAsync(Limit)).RawInput); }
        Assert.Empty(client.Requests);
        Assert.Equal(0, controller.CallsMade);
    }

    [Fact]
    public async Task Human_shout_changes_full_B_input_and_discards_completed_candidate() {
        var history = await Source("camp", "stream");
        await PlaygroundLookaheadProjection.ProjectAsync(history.State, history.Cursor, "a", Bindings, default, preparationLimit: Limit);
        var events = new ConcurrentQueue<string>();
        var client = new Client();
        var release = new TaskCompletionSource<PlaygroundControllerOperation>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var controller = new PlaygroundLookaheadController(() => (history.State, history.Cursor), Bindings,
            (_, _) => ValueTask.FromResult(new LlmDecisionBinding(client, "fixture", "cold")), new LlmControllerOptions { MaxCalls = 2 }, _ => { },
            evidence: (kind, _) => events.Enqueue(kind));
        var human = new Human(release);
        var pending = controller.ChooseHumanCommandAsync(PlaygroundDecisionContext.Create(history.State, "a"), human, default).AsTask();
        await client.Started.Task.WaitAsync(Limit);
        release.TrySetResult(new("human", "shout changed-human-input", "human"));
        var operation = await pending.WaitAsync(Limit);
        var kernel = PlaygroundEngine.CreateKernel(history, characterController: new Script(operation, controller));
        for (int i = 0; i < 30 && !events.Contains("lookahead-candidate-discarded"); i++) {
            await kernel.StepAsync(new ModelTime(long.MaxValue));
        }
        Assert.Contains("lookahead-candidate-discarded", events);
        Assert.DoesNotContain("lookahead-candidate-adopted", events);
        Assert.Equal(2, client.Requests.Count);
    }

    [Fact]
    public async Task Completed_feedback_candidate_during_Human_wait_has_no_logical_side_effects() {
        var history = await Source();
        await PlaygroundLookaheadProjection.ProjectAsync(history.State, history.Cursor, "a", Bindings, default, preparationLimit: Limit);
        var client = new Client("feedback");
        int feedback = 0, traces = 0;
        var physicalDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var controller = new PlaygroundLookaheadController(() => (history.State, history.Cursor), Bindings,
            (_, _) => ValueTask.FromResult(new LlmDecisionBinding(client, "fixture", "cold")), new LlmControllerOptions { MaxCalls = 1 }, _ => traces++,
            feedbackSink: (_, _) => { feedback++; return ValueTask.FromResult("receipt"); },
            evidence: (kind, _) => { if (kind == "lookahead-candidate-created") { physicalDone.TrySetResult(); } });
        var human = new Human();
        var world = history.State;
        int events = history.CompletedEvents.Count;
        var run = controller.ChooseHumanCommandAsync(PlaygroundDecisionContext.Create(world, "a"), human, default).AsTask();
        await physicalDone.Task.WaitAsync(Limit);
        Assert.False(run.IsCompleted);
        Assert.Equal(0, feedback);
        Assert.Equal(0, traces);
        Assert.Same(world, history.State);
        Assert.Equal(events, history.CompletedEvents.Count);
        Assert.Null(history.PendingEvent);
        human.Result.TrySetResult(new("human", "wait 1", "human"));
        await run.WaitAsync(Limit);
        Assert.Equal(0, feedback);
        Assert.Equal(0, traces);
    }

    [Fact]
    public async Task Throwing_cancellation_callback_still_joins_preparation_and_preserves_Human_failure() {
        var history = await Source();
        await PlaygroundLookaheadProjection.ProjectAsync(history.State, history.Cursor, "a", Bindings, default, preparationLimit: Limit);
        var resolving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var controller = new PlaygroundLookaheadController(() => (history.State, history.Cursor), Bindings,
            async (_, token) => {
                using var registration = token.Register(() => { cancelled.TrySetResult(); throw new InvalidOperationException("callback"); });
                resolving.TrySetResult();
                await release.Task;
                return new(client, "fixture", "cold");
            }, new LlmControllerOptions { MaxCalls = 1 }, _ => { });
        var human = new Human();
        var run = controller.ChooseHumanCommandAsync(PlaygroundDecisionContext.Create(history.State, "a"), human, default).AsTask();
        await resolving.Task.WaitAsync(Limit);
        var primary = new IOException("human input failed");
        human.Result.TrySetException(primary);
        // Wait until cancellation has actually run its throwing callback, rather than racing release.
        await cancelled.Task.WaitAsync(Limit);
        Assert.False(run.IsCompleted);
        release.TrySetResult();
        try { await run.WaitAsync(Limit); Assert.Fail("Expected Human failure."); }
        catch (AggregateException error) { Assert.Contains(primary, error.Flatten().InnerExceptions); }
        Assert.Empty(client.Requests);
    }

    private sealed class Script(PlaygroundControllerOperation first, IPlaygroundCharacterController llm) : IPlaygroundCharacterController {
        private bool _first = true;
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken token) {
            if (context.InteractionState.ActorId == "b") { return llm.ChooseCommandAsync(context, token); }
            if (_first) { _first = false; return ValueTask.FromResult(first); }
            return ValueTask.FromResult(new PlaygroundControllerOperation(Guid.NewGuid().ToString(), "wait 1", Guid.NewGuid().ToString()));
        }
    }
    private sealed class Human(TaskCompletionSource<PlaygroundControllerOperation>? result = null) : IPlaygroundCharacterController {
        public bool Started { get; private set; }
        public TaskCompletionSource<PlaygroundControllerOperation> Result { get; } = result ?? new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken token) {
            Started = true;
            return new(Result.Task);
        }
    }
    private sealed class Reader : TextReader {
        private int _reads;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string?> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<string?> ReadLineAsync(CancellationToken token) {
            Started.TrySetResult();
            return Interlocked.Increment(ref _reads) == 1 ? new(Release.Task.WaitAsync(token)) : ValueTask.FromResult<string?>(null);
        }
    }
    private sealed class Client(string tool = "act") : ICompletionClient {
        public string Name => "human-source";
        public string ApiSpecId => "fixture";
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<CompletionRequest> Requests { get; } = new();
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
            Requests.Enqueue(request);
            var result = new CompletionResult(new ActionMessage([new ActionBlock.ToolCall(new(tool, "human-source-call", tool == "feedback" ? JsonSerializer.Serialize(new { text = "must-not-deliver" }) : JsonSerializer.Serialize(new { command = "wait 1" })))]),
                new("fixture", "fixture", request.ModelId));
            Started.TrySetResult();
            return Task.FromResult(result);
        }
    }
    private static async Task<InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>> Source(string aPlace = "camp", string bPlace = "cabin") {
        var time = new ModelTime(10 * PlaygroundTime.Hour);
        var initial = PlaygroundGameFactory.CreateSurvival(time, enableNeeds: false, enableWeather: false, characterCount: 2);
        var actors = new[] { new PlaygroundCharacter("a", "A"), new PlaygroundCharacter("b", "B") };
        var places = new[] { aPlace, bPlace };
        var spatial = GraphSpatialState.Create(PlaygroundExpeditionMap.Graph,
            actors.Select((actor, index) => new EntityPlacement(new(actor.Id), new(places[index]))));
        var expedition = new PlaygroundExpedition(PlaygroundExpeditionMap.ScenarioId, spatial,
            actors.Select((actor, index) => new PlaygroundVisit(actor.Id, places[index], time,
                initial.World.Resources.Where(resource => PlaygroundExpeditionMap.GetResourcePlace(resource.Id) == places[index]))));
        var business = new PlaygroundBusinessState(time, initial.World.Light, initial.World.Resources, initial.World.Script, actors, expedition: expedition);
        var history = new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(PlaygroundEngine.Rebuild(business, time), initial.Cursor);
        var kernel = PlaygroundEngine.CreateKernel(history);
        for (int i = 0; i < 10; i++) {
            if (PlaygroundEngine.NextDecisionActor(history.State, history.Cursor) == "a"
                && history.State.Business.InteractionStates["a"].CurrentContext?.Status == CharacterContextStatus.Open) { return history; }
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(time));
        }
        throw new InvalidOperationException("A opening not reached.");
    }

}
