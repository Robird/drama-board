using System.Text.Json;
using Atelia.Completion.Abstractions;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Playground.Completion.Tests;

public sealed class LlmCharacterControllerTests {
    [Fact]
    public async Task Shared_adapter_resolves_actual_actor_and_preserves_one_budget_across_connections() {
        var first = new FakeClient(Result(Call("a-call", "wait 1")));
        var second = new FakeClient(Result(Call("b-call", "wait 1")));
        var resolved = new List<string>();
        var traces = new List<LlmControllerTrace>();
        var controller = new LlmCharacterController((actor, token) => {
            resolved.Add(actor);
            return ValueTask.FromResult(new LlmDecisionBinding(actor == "a" ? first : second,
                actor + "-model", actor + "-connection"));
        }, context => "material-" + context.InteractionState.ActorId,
            new() { MaxCalls = 2 }, traces.Add);
        var initial = PlaygroundGameFactory.CreateSurvival(enableWeather: false, characterCount: 2);
        var kernel = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor),
            characterController: controller);
        // Let real openings freeze each actor's context before the kernel invokes the adapter.
        for (int step = 0; controller.CallsMade < 2 && step < 20; step++) {
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(30 * PlaygroundTime.Hour)));
        }
        Assert.Equal(2, controller.CallsMade);
        var paused = await Assert.ThrowsAsync<LlmControllerPausedException>(async () => {
            for (int step = 0; step < 20; step++) {
                Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(30 * PlaygroundTime.Hour)));
            }
        });
        Assert.Equal("call-budget-exhausted", paused.ReasonCode);
        Assert.Equal(new[] { "a", "b" }, resolved);
        Assert.Equal("a-model", Assert.Single(first.Requests).ModelId);
        Assert.Equal("b-model", Assert.Single(second.Requests).ModelId);
        Assert.All(first.Requests.Concat(second.Requests), request => Assert.Empty(request.TailMessages));
        Assert.Equal(new[] { "a-connection", "b-connection" }, traces.Where(t => t.Kind == LlmControllerTraceKind.Request).Select(t => t.ConnectionId));
        Assert.Equal(2, controller.CallsMade);
    }

    [Fact]
    public async Task Shared_controller_selects_guidance_from_each_roles_actual_capabilities() {
        var client = new FakeClient(Result(Call("survival", "wait 1")), Result(Call("gathering", "wait 1")));
        var controller = new LlmCharacterController(client, "fixture-model", _ => "角色材料");
        await controller.ChooseCommandAsync(await Context(), default);
        var initial = PlaygroundGameFactory.CreatePlayable(characters: [new PlaygroundCharacter("a", "阿青")]);
        var kernel = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor));
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(30 * PlaygroundTime.Hour)));
        await controller.ChooseCommandAsync(PlaygroundDecisionContext.Create(kernel.World, "a"), default);

        string goals = client.Requests[0].PromptPrefix.SystemPrompt;
        string gathering = client.Requests[1].PromptPrefix.SystemPrompt;
        Assert.Contains("界面已提供目标管理", goals);
        Assert.Contains("不必再次抄写", goals);
        Assert.DoesNotContain("先用 handoff", goals);
        Assert.DoesNotContain("界面已提供目标管理", gathering);
        Assert.DoesNotContain("handoff", gathering);
        Assert.DoesNotContain("reopen", gathering);
        Assert.Contains("follow 主动集合", goals);
        Assert.Contains("dismiss-followers", goals);
        Assert.Contains("shout 大喊召集", goals);
        Assert.Contains("声音连接的一跳地点", goals);
        Assert.Contains("回执不提供听众名单或接收确认", goals);
        Assert.Contains("say / give 仍仅限同地", goals);
        Assert.Contains("山野镇", goals);
        Assert.Contains("买入价是你付款", goals);
        Assert.Contains("预留全部投入", goals);
        Assert.Contains("物品效果以当前冻结材料为准", goals);
        Assert.DoesNotContain("use item.ration", goals);
        Assert.Contains("give <actor-id> money <amount>", goals);
        Assert.DoesNotContain("follow", gathering);
        Assert.DoesNotContain("shout", gathering);
        Assert.All(client.Requests, request => Assert.Empty(request.TailMessages));
    }

    [Fact]
    public async Task Each_decision_projects_current_material_without_native_history_and_preserves_call_identity() {
        var client = new FakeClient(Result(Call("  provider:id/原值  ", "use item.water")), Result(Call("same-id", "handoff 计划")));
        var traces = new List<LlmControllerTrace>();
        string material = "完整材料一";
        var controller = new LlmCharacterController(client, "fixture-model", _ => material, trace: traces.Add);
        PlaygroundDecisionContext context = await Context();
        PlaygroundControllerOperation first = await controller.ChooseCommandAsync(context, default);
        material = "完整材料二（含已提交反馈）";
        PlaygroundControllerOperation second = await controller.ChooseCommandAsync(context, default);

        Assert.Equal("  provider:id/原值  ", first.ToolCallId);
        Assert.Equal("use item.water", first.RawInput);
        Assert.NotEqual(first.OperationRecordId, second.OperationRecordId);
        Assert.NotEqual(first.ToolCallId, first.OperationRecordId);
        Assert.Equal(2, controller.CallsMade);
        Assert.Equal(new[] { "完整材料一", material }, client.Requests.Select(r =>
            Assert.IsType<ObservationMessage>(Assert.Single(r.PromptPrefix.SharedContextMessages)).Content));
        Assert.All(client.Requests, request => {
            Assert.Empty(request.TailMessages);
            Assert.Equal("fixture-model", request.ModelId);
            Assert.Equal(CompletionToolChoiceKind.Auto, request.PromptPrefix.OutputContract.ToolChoice.Kind);
            Assert.False(request.PromptPrefix.OutputContract.AllowParallelToolCalls);
            var definition = Assert.Single(request.PromptPrefix.OutputContract.Tools);
            Assert.Equal("act", definition.Name);
            var schema = Assert.IsType<ToolSchema.Object>(definition.InputSchema);
            Assert.False(schema.AdditionalProperties);
            Assert.True(Assert.Single(schema.Properties).IsRequired);
        });
        Assert.Equal(new[] { LlmControllerTraceKind.Request, LlmControllerTraceKind.Response,
            LlmControllerTraceKind.Request, LlmControllerTraceKind.Response }, traces.Select(t => t.Kind));
        Assert.Equal(new CommandSubmission(first.ToolCallId, first.RawInput, first.OperationRecordId), traces[1].Operation);
        Assert.All(traces, trace => Assert.Equal("a", trace.CharacterId));
    }

    public static IEnumerable<object[]> InvalidResponses() {
        yield return [new ActionMessage([new ActionBlock.Text("wait 1")]), "expected-single-call"];
        yield return [new ActionMessage([new ActionBlock.ToolCall(Call("1", "wait 1")), new ActionBlock.ToolCall(Call("2", "wait 2"))]), "expected-single-call"];
        yield return [new ActionMessage([new ActionBlock.ToolCall(new("other", "1", "{}"))]), "unexpected-tool"];
        yield return [new ActionMessage([new ActionBlock.ToolCall(Call(" ", "wait 1"))]), "missing-call-id"];
        yield return [new ActionMessage([new ActionBlock.ToolCall(RawToolCall.FromText("act", "1", "wait 1"))]), "expected-json-input"];
        foreach (string input in new[] { "{}", "{\"command\":1}", "{\"command\":null}", "{\"Command\":\"wait 1\"}",
            "{\"command\":\"wait 1\",\"extra\":1}", "{\"command\":\"wait 1\",\"command\":\"wait 2\"}" }) {
            yield return [new ActionMessage([new ActionBlock.ToolCall(new("act", "1", input))]), "expected-command-string"];
        }
        yield return [new ActionMessage([new ActionBlock.ToolCall(new("act", "1", "[]"))]), "expected-object"];
        yield return [new ActionMessage([new ActionBlock.ToolCall(new("act", "1", "{\"command\":"))]), "invalid-json"];
    }

    [Theory, MemberData(nameof(InvalidResponses))]
    public async Task Invalid_completed_envelopes_are_bounded_and_never_produce_operations(ActionMessage message, string code) {
        var result = new CompletionResult(message, Descriptor);
        var client = new FakeClient(result, result, result);
        var traces = new List<LlmControllerTrace>();
        var controller = new LlmCharacterController(client, "fixture-model", _ => "material", trace: traces.Add);
        var exception = await Assert.ThrowsAsync<LlmControllerPausedException>(async () =>
            await controller.ChooseCommandAsync(await Context(), default));
        Assert.Equal("protocol-failure-limit", exception.ReasonCode);
        Assert.Equal(3, exception.CallsMade);
        Assert.All(traces.Where(t => t.Kind == LlmControllerTraceKind.Response), trace => {
            Assert.Equal(code, trace.ProtocolError);
            Assert.Null(trace.Operation);
        });
        Assert.All(client.Requests.Skip(1), request => {
            Assert.Empty(request.TailMessages);
            Assert.Equal(2, request.PromptPrefix.SharedContextMessages.Length);
            Assert.Contains(code, Assert.IsType<ObservationMessage>(request.PromptPrefix.SharedContextMessages[1]).Content);
        });
    }

    [Fact]
    public async Task Protocol_correction_can_succeed_and_failure_count_restarts_after_valid_operation() {
        var invalid = new CompletionResult(new ActionMessage([]), Descriptor);
        var client = new FakeClient(invalid, Result(Call("1", "wait 1")), invalid, Result(Call("2", "wait 1")));
        var controller = new LlmCharacterController(client, "fixture-model", _ => "material",
            new() { MaxConsecutiveProtocolFailures = 2 });
        PlaygroundDecisionContext context = await Context();
        Assert.Equal("1", (await controller.ChooseCommandAsync(context, default)).ToolCallId);
        Assert.Equal("2", (await controller.ChooseCommandAsync(context, default)).ToolCallId);
        Assert.Equal(4, controller.CallsMade);
    }

    [Theory]
    [InlineData(CompletionTerminationKind.Incomplete, "completion-incomplete")]
    [InlineData(CompletionTerminationKind.Failed, "completion-failed")]
    public async Task Non_completed_results_with_full_looking_tool_calls_pause_without_execution_or_retry(CompletionTerminationKind kind, string code) {
        var client = new FakeClient(Result(Call("id", "wait 1")) with { Termination = new(kind) });
        var controller = new LlmCharacterController(client, "model", _ => "material");
        var exception = await Assert.ThrowsAsync<LlmControllerPausedException>(async () =>
            await controller.ChooseCommandAsync(await Context(), default));
        Assert.Equal(code, exception.ReasonCode);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task Total_budget_covers_game_legal_or_illegal_commands_without_automatic_substitute() {
        var client = new FakeClient(Result(Call("1", "nonsense")), Result(Call("2", "")));
        var controller = new LlmCharacterController(client, "model", _ => "material", new() { MaxCalls = 2 });
        PlaygroundDecisionContext context = await Context();
        Assert.Equal("nonsense", (await controller.ChooseCommandAsync(context, default)).RawInput);
        Assert.Equal("", (await controller.ChooseCommandAsync(context, default)).RawInput);
        var exception = await Assert.ThrowsAsync<LlmControllerPausedException>(async () => await controller.ChooseCommandAsync(context, default));
        Assert.Equal("call-budget-exhausted", exception.ReasonCode);
        Assert.Equal(2, client.Requests.Count);
    }

    [Fact]
    public async Task Oversized_material_is_not_truncated_or_sent() {
        var client = new FakeClient();
        var controller = new LlmCharacterController(client, "model", _ => "12345", new() { MaxContextCharacters = 4 });
        var exception = await Assert.ThrowsAsync<LlmControllerPausedException>(async () =>
            await controller.ChooseCommandAsync(await Context(), default));
        Assert.Equal("context-too-large", exception.ReasonCode);
        Assert.Empty(client.Requests);
        Assert.Equal(0, controller.CallsMade);
    }

    [Theory]
    [InlineData("12345", "command-too-large")]
    [InlineData("large-json", "arguments-too-large")]
    public async Task Oversized_tool_input_is_rejected(string value, string error) {
        RawToolCall call = value == "large-json" ? new("act", "id", new string(' ', 1100)) : Call("id", value);
        var traces = new List<LlmControllerTrace>();
        var controller = new LlmCharacterController(new FakeClient(Result(call)), "model", _ => "material",
            new() { MaxCommandCharacters = 4, MaxConsecutiveProtocolFailures = 1 }, traces.Add);
        await Assert.ThrowsAsync<LlmControllerPausedException>(async () => await controller.ChooseCommandAsync(await Context(), default));
        Assert.Equal(error, traces.Last().ProtocolError);
    }

    [Fact]
    public async Task Transport_interruption_pauses_without_retry_and_keeps_structured_cause() {
        var failure = new CompletionFailureException(new(CompletionFailureKind.Transport), "private provider diagnostic");
        var client = new FakeClient { OnCall = (_, _) => throw failure };
        var controller = new LlmCharacterController(client, "model", _ => "material");
        var exception = await Assert.ThrowsAsync<LlmControllerPausedException>(async () =>
            await controller.ChooseCommandAsync(await Context(), default));
        Assert.Equal("transport-failure", exception.ReasonCode);
        Assert.DoesNotContain("private", exception.Message);
        Assert.Same(failure, exception.InnerException);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task Local_request_rejection_is_a_host_pause_with_safe_diagnostics() {
        var rejection = new CompletionRequestRejectedException(CompletionTermination.Failed("unsupported-contract"));
        var client = new FakeClient { OnCall = (_, _) => throw rejection };
        var traces = new List<LlmControllerTrace>();
        var controller = new LlmCharacterController(client, "model", _ => "material", trace: traces.Add);
        var exception = await Assert.ThrowsAsync<LlmControllerPausedException>(async () =>
            await controller.ChooseCommandAsync(await Context(), default));
        Assert.Equal("request-rejected", exception.ReasonCode);
        Assert.Same(rejection, exception.InnerException);
        Assert.Equal("request-rejected", traces.Last().ProtocolError);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task Catching_protocol_pause_does_not_refund_consecutive_failure_budget() {
        var client = new FakeClient(new CompletionResult(new ActionMessage([]), Descriptor));
        var controller = new LlmCharacterController(client, "model", _ => "material",
            new() { MaxConsecutiveProtocolFailures = 1 });
        PlaygroundDecisionContext context = await Context();
        for (int i = 0; i < 2; i++) {
            var exception = await Assert.ThrowsAsync<LlmControllerPausedException>(async () => await controller.ChooseCommandAsync(context, default));
            Assert.Equal("protocol-failure-limit", exception.ReasonCode);
        }
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task Cancellation_retains_token_and_pre_cancelled_calls_spend_no_budget() {
        using var source = new CancellationTokenSource();
        var client = new FakeClient { OnCall = (_, token) => {
            source.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException();
        } };
        var controller = new LlmCharacterController(client, "model", _ => "material");
        PlaygroundDecisionContext context = await Context();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await controller.ChooseCommandAsync(context, source.Token));
        Assert.Equal(source.Token, exception.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await controller.ChooseCommandAsync(context, source.Token));
        Assert.Single(client.Requests);
        Assert.Equal(1, controller.CallsMade);
    }

    [Fact]
    public async Task Engine_commits_game_rejection_and_next_independent_request_receives_it() {
        var client = new FakeClient(Result(Call("bad", "made up action")), Result(Call("supply", "use item.water")), Result(Call("note", "handoff 接下来探索")));
        static string Render(PlaygroundDecisionContext context) => string.Join("\n", context.Context.Items.Select(item => $"{item.RawInput}: {item.Result}"));
        var controller = new LlmCharacterController(client, "model", Render);
        var initial = PlaygroundGameFactory.CreateSurvival(enableWeather: false);
        var kernel = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor), characterController: controller);
        for (int i = 0; i < 4; i++) { Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(30 * PlaygroundTime.Hour))); }
        var context = kernel.World.Business.InteractionStates["a"].CurrentContext!;
        Assert.Equal(1, context.ContextSequence);
        Assert.Equal(3, context.Items.Count);
        Assert.True(context.Items[0].IsRejected);
        Assert.Contains("made up action", Assert.IsType<ObservationMessage>(Assert.Single(client.Requests[1].PromptPrefix.SharedContextMessages)).Content);
        Assert.Equal(90, context.LatestObservation.Survival!.Water);
        Assert.Equal("接下来探索", kernel.World.Business.InteractionStates["a"].DraftHandoff);
        Assert.Equal(initial.World.Time, kernel.World.Time);
    }

    [Fact]
    public async Task Concurrent_and_pre_cancelled_services_do_not_invoke_the_renderer() {
        var client = new PendingClient();
        int rendered = 0;
        var controller = new LlmCharacterController(client, "fixture-model", _ => {
            rendered++;
            return "frozen character material";
        });
        PlaygroundDecisionContext context = await Context();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await controller.ChooseCommandAsync(context, cancelled.Token));
        Assert.Equal(cancelled.Token, cancellation.CancellationToken);
        Assert.Equal(0, rendered);

        Task<PlaygroundControllerOperation> first = controller.ChooseCommandAsync(context, default).AsTask();
        try {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await controller.ChooseCommandAsync(context, default));
            Assert.Equal(1, rendered);
            Assert.Equal(1, client.Calls);
        } finally { client.Response.SetResult(Result(Call("one", "wait 1"))); }
        Assert.Equal("wait 1", (await first).RawInput);

        // Completion releases the adapter gate; later services may render again.
        await controller.ChooseCommandAsync(context, default);
        Assert.Equal(2, rendered);
        Assert.Equal(2, client.Calls);
    }

    private sealed class PendingClient : ICompletionClient {
        public string Name => "fixture";
        public string ApiSpecId => "fixture-api";
        public int Calls { get; private set; }
        public TaskCompletionSource<CompletionResult> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default) {
            Calls++;
            return Response.Task;
        }
    }

    private static CompletionDescriptor Descriptor => new("fixture", "fixture-api", "fixture-model");
    private static RawToolCall Call(string id, string command) => new("act", id, JsonSerializer.Serialize(new { command }));
    private static CompletionResult Result(RawToolCall call) => new(new ActionMessage([new ActionBlock.ToolCall(call)]), Descriptor);
    private static async Task<PlaygroundDecisionContext> Context() {
        var initial = PlaygroundGameFactory.CreateSurvival(enableWeather: false);
        var kernel = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor));
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(30 * PlaygroundTime.Hour)));
        return PlaygroundDecisionContext.Create(kernel.World, "a");
    }

    private sealed class FakeClient(params CompletionResult[] results) : ICompletionClient {
        private readonly Queue<CompletionResult> _results = new(results);
        public string Name => "fixture";
        public string ApiSpecId => "fixture-api";
        public List<CompletionRequest> Requests { get; } = [];
        public Func<CompletionRequest, CancellationToken, CompletionResult>? OnCall { get; init; }
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
            Requests.Add(request);
            return Task.FromResult(OnCall?.Invoke(request, cancellationToken) ?? _results.Dequeue());
        }
    }
}
