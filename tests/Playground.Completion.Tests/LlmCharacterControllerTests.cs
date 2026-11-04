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
        Assert.Contains("map 和 path 是普通角色命令", goals);
        Assert.Contains("途中规划需显式 from", goals);
        Assert.Contains("navigate <路径ID>", goals);
        Assert.Contains("活动路线独立保存，新 path 不会改动它", goals);
        Assert.Contains("cancel 只清剩余导航", goals);
        Assert.Contains("途中 continue 只确认提醒", goals);
        Assert.Contains("已知地图中无路清空候选", goals);
        Assert.Contains("不证明本人已经到达", goals);

        Assert.Contains("go 只接受当前可见的本端入口 ID", goals);
        Assert.Contains("去过两个地点不证明两地相连", goals);
        Assert.Contains("他人话语和自己的猜测不会增加地图知识", goals);
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
        });
        Assert.Equal(new[] { LlmControllerTraceKind.Request, LlmControllerTraceKind.Response,
            LlmControllerTraceKind.Request, LlmControllerTraceKind.Response }, traces.Select(t => t.Kind));
        Assert.Equal(new CommandSubmission(first.ToolCallId, first.RawInput, first.OperationRecordId), traces[1].Operation);
        Assert.All(traces, trace => Assert.Equal("a", trace.CharacterId));
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
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
            Requests.Add(request);
            return Task.FromResult(_results.Dequeue());
        }
    }
}
