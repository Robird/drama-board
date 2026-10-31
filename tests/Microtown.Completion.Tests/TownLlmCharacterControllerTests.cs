using System.Text.Json;
using Atelia.Completion.Abstractions;
using DramaBoard.CharacterControl.Completion;
using DramaBoard.Goals;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;

namespace DramaBoard.Microtown.Completion.Tests;

public sealed class TownLlmCharacterControllerTests {
    [Fact]
    public async Task Three_residents_share_protocol_loop_but_not_private_material_or_feedback_tail() {
        CompletionResult feedback = Result(new("report-feedback", "feedback-a",
            "{\"kind\":\"feature-request\",\"summary\":\"Need a price history\",\"obstacle\":\"Cannot compare market cycles\"}"));
        var client = new Client(feedback, Act("  provider:a  ", "buy grain 2"), Act("provider:b", "wait 1"), Act("provider:c", "eat"));
        var traces = new List<LlmControllerTrace>();
        var feedbacks = new List<LlmFeedbackSubmission>();
        var controller = new TownLlmCharacterController(client, "fixture-model", trace: traces.Add,
            feedbackSink: (submission, _) => {
                Assert.Single(client.Requests);
                feedbacks.Add(submission);
                return ValueTask.FromResult("saved-report");
            });

        TownObservation a = Observation("a"), b = Observation("b"), c = Observation("c");
        string frozen = TownTextRenderer.Render(a);
        TownControllerOperation first = await controller.ChooseCommandAsync(a, default);
        TownControllerOperation second = await controller.ChooseCommandAsync(b, default);
        TownControllerOperation third = await controller.ChooseCommandAsync(c, default);

        Assert.Equal("buy grain 2", first.RawInput);
        Assert.Equal("  provider:a  ", first.ToolCallId);
        Assert.Equal("provider:b", second.ToolCallId);
        Assert.Equal("eat", third.RawInput);
        Assert.Equal(3, new[] { first.OperationRecordId, second.OperationRecordId, third.OperationRecordId }.Distinct().Count());
        Assert.Equal(new[] { "a", "a", "b", "c" }, traces.Where(trace => trace.Kind == LlmControllerTraceKind.Request).Select(trace => trace.CharacterId));
        Assert.Equal(4, traces.Where(trace => trace.Kind == LlmControllerTraceKind.Request).Select(trace => trace.RequestId).Distinct().Count());
        Assert.Equal("a", Assert.Single(feedbacks).CharacterId);
        Assert.Equal(a.DecisionSequence, Assert.Single(feedbacks).ContextSequence);
        Assert.Equal(traces[0].RequestId, feedbacks[0].RequestId);
        Assert.Same(feedback.Message, client.Requests[1].TailMessages[0]);
        ToolResult receipt = Assert.Single(Assert.IsType<ToolResultsMessage>(client.Requests[1].TailMessages[1]).Results);
        Assert.Equal("feedback-a", receipt.ToolCallId);
        Assert.Contains("saved-report", receipt.GetFlattenedText());
        Assert.All(client.Requests.Skip(2), request => Assert.Empty(request.TailMessages));
        foreach ((int index, string actor) in new[] { (0, "a"), (1, "a"), (2, "b"), (3, "c") }) {
            string material = Material(client.Requests[index]);
            Assert.Contains("private-plan-" + actor, material);
            foreach (string other in new[] { "a", "b", "c" }.Where(other => other != actor)) {
                Assert.DoesNotContain("private-plan-" + other, material);
            }
            Assert.Contains("Only the current observation", client.Requests[index].PromptPrefix.SystemPrompt);
        }
        Assert.Equal(new CommandSubmission(first.ToolCallId, first.RawInput, first.OperationRecordId),
            traces.Single(trace => trace.Operation?.ToolCallId == first.ToolCallId).Operation);
        Assert.Equal(frozen, TownTextRenderer.Render(a));
        Assert.Equal(4, controller.CallsMade);
        Assert.True(controller.FeedbackEnabled);
    }

    [Fact]
    public async Task Shared_call_budget_stops_the_next_resident_without_substituting_an_action() {
        var client = new Client(Act("a", "wait 1"), Act("b", "wait 1"));
        var controller = new TownLlmCharacterController(client, "model", new() { MaxCalls = 2 });
        await controller.ChooseCommandAsync(Observation("a"), default);
        await controller.ChooseCommandAsync(Observation("b"), default);
        var pause = await Assert.ThrowsAsync<LlmControllerPausedException>(async () =>
            await controller.ChooseCommandAsync(Observation("c"), default));
        Assert.Equal("call-budget-exhausted", pause.ReasonCode);
        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(2, controller.CallsMade);
        Assert.False(controller.FeedbackEnabled);
        Assert.All(client.Requests, request => Assert.Single(request.PromptPrefix.OutputContract.Tools));
    }

    [Fact]
    public async Task Feedback_save_failure_does_not_produce_an_operation_or_success_tail() {
        var client = new Client(Result(new("report-feedback", "report",
            "{\"kind\":\"issue\",\"summary\":\"blocked\",\"obstacle\":\"need help\"}")));
        var traces = new List<LlmControllerTrace>();
        var controller = new TownLlmCharacterController(client, "model", trace: traces.Add,
            feedbackSink: (_, _) => throw new IOException("fixture save failed"));
        var pause = await Assert.ThrowsAsync<LlmControllerPausedException>(async () =>
            await controller.ChooseCommandAsync(Observation("a"), default));
        Assert.Equal("feedback-save-failed", pause.ReasonCode);
        Assert.Single(client.Requests);
        Assert.DoesNotContain(traces, trace => trace.Operation is not null || trace.Kind == LlmControllerTraceKind.Feedback);
    }

    [Fact]
    public async Task Adapter_serializes_rendering_and_preserves_cancellation_before_rendering() {
        var pending = new TaskCompletionSource<CompletionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client { Invoke = (_, token) => pending.Task.WaitAsync(token) };
        int rendered = 0;
        var controller = new TownLlmCharacterController(client, "model", renderObservation: _ => {
            rendered++;
            return "material";
        });
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await controller.ChooseCommandAsync(Observation("a"), cancelled.Token));
        Assert.Equal(cancelled.Token, error.CancellationToken);
        Assert.Equal(0, rendered);
        using var inFlight = new CancellationTokenSource();
        Task<TownControllerOperation> active = controller.ChooseCommandAsync(Observation("a"), inFlight.Token).AsTask();
        try {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await controller.ChooseCommandAsync(Observation("b"), default));
            Assert.Equal(1, rendered);
            Assert.Single(client.Requests);
        } finally { inFlight.Cancel(); }
        var interrupted = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await active);
        Assert.Equal(inFlight.Token, interrupted.CancellationToken);
        pending.SetResult(Act("next", "wait 1"));
        Assert.Equal("next", (await controller.ChooseCommandAsync(Observation("b"), default)).ToolCallId);
        Assert.Equal(2, rendered);
    }

    [Fact]
    public async Task Preview_is_rejected_before_rendering_or_model_invocation() {
        var client = new Client();
        var controller = new TownLlmCharacterController(client, "model", renderObservation: _ => throw new InvalidOperationException("must not render"));
        TownObservation preview = TownObservation.Capture(TownGameFactory.Create().World, "a");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await controller.ChooseCommandAsync(preview, default));
        Assert.Contains("open decision", error.Message);
        Assert.Empty(client.Requests);
        Assert.Equal(0, controller.CallsMade);
    }

    [Fact]
    public async Task Actual_kernel_publishes_raw_operation_identity_and_delivers_rejection_to_the_next_request() {
        var client = new Client(Act("bad", "invented action"), Act("good", "wait 1"));
        var traces = new List<LlmControllerTrace>();
        var controller = new TownLlmCharacterController(client, "model", trace: traces.Add);
        var created = TownGameFactory.Create(2);
        var history = new InMemoryOccurrenceHistory<TownWorld, TownFact>(created.World, created.Cursor);
        var kernel = TownEngine.CreateControlledKernel(history, controller.ChooseCommandAsync);
        for (int step = 0; step < 3; step++) { Assert.Equal(StepStatus.Committed, await kernel.StepAsync(created.World.Business.EndTime)); }
        Assert.Equal(2, client.Requests.Count);
        Assert.Contains("invented action", Material(client.Requests[1]));
        Assert.Contains("rejected", Material(client.Requests[1]));
        TownOperationReceipt accepted = history.State.Business.Character("a").LastOperationReceipt!;
        Assert.True(accepted.Accepted);
        Assert.Equal("good", accepted.Operation.ToolCallId);
        Assert.Equal("wait 1", accepted.Operation.RawInput);
        Assert.Equal(traces.Last(trace => trace.Operation is not null).Operation!.OperationRecordId, accepted.Operation.OperationRecordId);
        Assert.Equal(0, history.State.Time.Ticks);
    }

    private static TownObservation Observation(string actorId) {
        TownBusinessState before = TownGameFactory.Create().World.Business;
        TownCharacter[] actors = before.Characters.Select(actor => actor with {
            DecisionSequence = 1,
            Goals = GoalRules.Apply(new(), new(GoalOperationKind.Create,
                JsonSerializer.Serialize(new { title = "private-plan-" + actor.Id, criteria = "personal criterion" })), 1, 1).After
        }).ToArray();
        var state = new TownBusinessState(before.Time, before.EndTime, actors, before.Market, before.Economy,
            before.Spatial, openDecisionActorId: actorId);
        return TownObservation.Capture(TownEngine.Rebuild(state), actorId);
    }

    private static string Material(CompletionRequest request) =>
        Assert.IsType<ObservationMessage>(Assert.Single(request.PromptPrefix.SharedContextMessages)).Content!;
    private static CompletionResult Act(string id, string command) => Result(new("act", id, JsonSerializer.Serialize(new { command })));
    private static CompletionResult Result(RawToolCall call) =>
        new(new ActionMessage([new ActionBlock.ToolCall(call)]), new("fixture", "fixture-api", "fixture-model"));

    private sealed class Client(params CompletionResult[] results) : ICompletionClient {
        private readonly Queue<CompletionResult> _results = new(results);
        public string Name => "fixture";
        public string ApiSpecId => "fixture-api";
        public List<CompletionRequest> Requests { get; } = [];
        public Func<CompletionRequest, CancellationToken, Task<CompletionResult>>? Invoke { get; init; }
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default) {
            Requests.Add(request);
            return Invoke?.Invoke(request, cancellationToken) ?? Task.FromResult(_results.Dequeue());
        }
    }
}
