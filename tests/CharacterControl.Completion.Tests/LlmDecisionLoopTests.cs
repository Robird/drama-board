using System.Text.Json;
using Atelia.Completion.Abstractions;

namespace DramaBoard.CharacterControl.Completion.Tests;

public sealed class LlmDecisionLoopTests {
    [Fact]
    public async Task Different_game_inputs_share_the_loop_without_sharing_policy_material_or_native_history() {
        var feedback = Result(new("report-feedback", "feedback-call",
            "{\"kind\":\"issue\",\"summary\":\"missing price\",\"obstacle\":\"cannot compare\"}"));
        var client = new Client(feedback, Act("town-call", "buy grain 2"), Act("forest-call", "wait 1"));
        var traces = new List<LlmControllerTrace>();
        var submissions = new List<LlmFeedbackSubmission>();
        var loop = new LlmDecisionLoop(client, "fixture-model", trace: traces.Add,
            feedbackSink: (submission, _) => {
                Assert.Single(client.Requests);
                submissions.Add(submission);
                return ValueTask.FromResult("saved-feedback");
            });

        CommandSubmission town = await loop.ChooseCommandAsync(new("resident-a", 4,
            "private town notebook", "town production policy"), default);
        CommandSubmission forest = await loop.ChooseCommandAsync(new("survivor-b", 2,
            "private forest notebook", "forest survival policy"), default);

        Assert.Equal("buy grain 2", town.RawInput);
        Assert.Equal("town-call", town.ToolCallId);
        Assert.NotEqual(town.OperationRecordId, forest.OperationRecordId);
        Assert.Same(feedback.Message, client.Requests[1].TailMessages[0]);
        Assert.Contains("saved-feedback", Assert.Single(Assert.IsType<ToolResultsMessage>(
            client.Requests[1].TailMessages[1]).Results).GetFlattenedText());
        Assert.Empty(client.Requests[2].TailMessages);
        Assert.Contains("forest survival policy", client.Requests[2].PromptPrefix.SystemPrompt);
        Assert.DoesNotContain("town production policy", client.Requests[2].PromptPrefix.SystemPrompt);
        Assert.Equal("private forest notebook", Assert.IsType<ObservationMessage>(
            Assert.Single(client.Requests[2].PromptPrefix.SharedContextMessages)).Content);
        Assert.Equal("resident-a", Assert.Single(submissions).CharacterId);
        Assert.Equal(4, Assert.Single(submissions).ContextSequence);
        Assert.Equal(3, traces.Where(trace => trace.Kind == LlmControllerTraceKind.Request)
            .Select(trace => trace.RequestId).Distinct().Count());
        Assert.Equal(forest, traces[^1].Operation);
        Assert.Equal("survivor-b", traces[^1].CharacterId);
        Assert.Equal(3, loop.CallsMade);
    }

    [Fact]
    public async Task Shared_call_budget_spans_characters_without_creating_a_substitute_operation() {
        var client = new Client(Act("one", "work"), Act("two", "rest"));
        var loop = new LlmDecisionLoop(client, "model", new() { MaxCalls = 2 });
        await loop.ChooseCommandAsync(new("one", 1, "material one", "policy one"), default);
        await loop.ChooseCommandAsync(new("two", 1, "material two", "policy two"), default);
        var error = await Assert.ThrowsAsync<LlmControllerPausedException>(async () =>
            await loop.ChooseCommandAsync(new("three", 1, "material three", "policy three"), default));
        Assert.Equal("call-budget-exhausted", error.ReasonCode);
        Assert.Equal(2, client.Requests.Count);
    }

    [Fact]
    public async Task Concurrent_decisions_are_rejected_before_invoking_a_second_model() {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<CompletionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client {
            Invoke = _ => { entered.SetResult(); return release.Task; }
        };
        var loop = new LlmDecisionLoop(client, "model");
        Task<CommandSubmission> active = loop.ChooseCommandAsync(new("a", 1, "a", "a"), default).AsTask();
        await entered.Task;
        try {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await loop.ChooseCommandAsync(new("b", 1, "b", "b"), default));
            Assert.Single(client.Requests);
        } finally { release.SetResult(Act("one", "wait")); }
        Assert.Equal("wait", (await active).RawInput);
    }

    private static CompletionResult Act(string id, string command) => Result(new("act", id, JsonSerializer.Serialize(new { command })));
    private static CompletionResult Result(RawToolCall call) =>
        new(new ActionMessage([new ActionBlock.ToolCall(call)]), new("fixture", "fixture-api", "fixture-model"));

    private sealed class Client(params CompletionResult[] results) : ICompletionClient {
        private readonly Queue<CompletionResult> _results = new(results);
        public string Name => "fixture";
        public string ApiSpecId => "fixture-api";
        public List<CompletionRequest> Requests { get; } = [];
        public Func<CompletionRequest, Task<CompletionResult>>? Invoke { get; init; }
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default) {
            Requests.Add(request);
            return Invoke?.Invoke(request) ?? Task.FromResult(_results.Dequeue());
        }
    }
}
