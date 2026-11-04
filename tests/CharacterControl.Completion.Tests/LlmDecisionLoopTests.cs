using System.Text.Json;
using Atelia.Completion.Abstractions;
using static DramaBoard.CharacterControl.Completion.Tests.LlmDecisionTestSupport;

namespace DramaBoard.CharacterControl.Completion.Tests;

public sealed class LlmDecisionLoopTests {
    [Fact]
    public async Task Different_game_inputs_share_the_loop_without_sharing_policy_material_or_native_history() {
        var feedback = Result(new("feedback", "feedback-call",
            "{\"text\":\"missing price; cannot compare\"}"));
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
        var receipt = Assert.Single(Assert.IsType<ToolResultsMessage>(
            client.Requests[1].TailMessages[1]).Results);
        Assert.Equal("feedback-call", receipt.ToolCallId);
        Assert.Equal("feedback", receipt.ToolName);
        Assert.Equal(ToolExecutionStatus.Success, receipt.Status);
        string feedbackReceipt = receipt.GetFlattenedText();
        Assert.Contains("saved-feedback", feedbackReceipt);
        Assert.Contains("反馈已保存", feedbackReceipt);
        using (JsonDocument receiptDocument = JsonDocument.Parse(feedbackReceipt)) {
            Assert.True(receiptDocument.RootElement.GetProperty("saved").GetBoolean());
        }
        Assert.Empty(client.Requests[2].TailMessages);
        Assert.Contains("forest survival policy", client.Requests[2].PromptPrefix.SystemPrompt);
        Assert.DoesNotContain("town production policy", client.Requests[2].PromptPrefix.SystemPrompt);
        Assert.Equal("private forest notebook", Assert.IsType<ObservationMessage>(
            Assert.Single(client.Requests[2].PromptPrefix.SharedContextMessages)).Content);
        Assert.Equal("resident-a", Assert.Single(submissions).CharacterId);
        var submission = Assert.Single(submissions);
        Assert.Equal(4, submission.ContextSequence);
        Assert.Equal(1, submission.CallNumber);
        Assert.Equal("feedback-call", submission.ToolCallId);
        Assert.Equal("missing price; cannot compare", submission.Text);
        Assert.Equal(traces[0].RequestId, submission.RequestId);
        Assert.Equal("saved-feedback", Assert.Single(traces, trace => trace.Kind == LlmControllerTraceKind.Feedback).FeedbackId);
        Assert.Equal(2, client.Requests[0].PromptPrefix.OutputContract.Tools.Length);
        Assert.Equal(3, traces.Where(trace => trace.Kind == LlmControllerTraceKind.Request)
            .Select(trace => trace.RequestId).Distinct().Count());
        var requests = traces.Where(trace => trace.Kind == LlmControllerTraceKind.Request).ToArray();
        Assert.All(traces, trace => Assert.Equal(requests.Single(request => request.CallNumber == trace.CallNumber).RequestId, trace.RequestId));
        Assert.Equal(forest, traces[^1].Operation);
        Assert.Equal("survivor-b", traces[^1].CharacterId);
        Assert.Equal(3, loop.CallsMade);
    }

    [Fact]
    public async Task Shared_call_budget_spans_characters_without_creating_a_substitute_operation() {
        var client = new Client(Act("one", "nonsense"), Act("two", ""));
        var loop = new LlmDecisionLoop(client, "model", new() { MaxCalls = 2 });
        Assert.Equal("nonsense", (await loop.ChooseCommandAsync(new("one", 1, "material one", "policy one"), default)).RawInput);
        Assert.Equal("", (await loop.ChooseCommandAsync(new("two", 1, "material two", "policy two"), default)).RawInput);
        var error = await Assert.ThrowsAsync<LlmControllerPausedException>(async () =>
            await loop.ChooseCommandAsync(new("three", 1, "material three", "policy three"), default));
        Assert.Equal("call-budget-exhausted", error.ReasonCode);
        Assert.Equal(2, client.Requests.Count);
    }

    [Fact]
    public async Task Role_bindings_are_lazy_fixed_per_decision_and_share_call_budget_without_replaying_tail() {
        var feedback = Result(new("feedback", "feedback",
            "{\"text\":\"feedback text\"}"));
        var first = new Client(feedback, Act("a", "work"));
        var second = new Client(Act("b", "rest"));
        var resolved = new List<string>();
        var traces = new List<LlmControllerTrace>();
        var loop = new LlmDecisionLoop((actor, token) => {
            resolved.Add(actor);
            return ValueTask.FromResult(new LlmDecisionBinding(actor == "a" ? first : second,
                actor + "-model", actor + "-connection"));
        }, new() { MaxCalls = 3 }, traces.Add,
            (_, _) => ValueTask.FromResult("receipt"));

        await loop.ChooseCommandAsync(new("a", 1, "private-a", "policy"), default);
        await loop.ChooseCommandAsync(new("b", 1, "private-b", "policy"), default);
        var paused = await Assert.ThrowsAsync<LlmControllerPausedException>(async () =>
            await loop.ChooseCommandAsync(new("c", 1, "private-c", "policy"), default));

        Assert.Equal("call-budget-exhausted", paused.ReasonCode);
        Assert.Equal(new[] { "a", "b" }, resolved);
        Assert.Equal(2, first.Requests.Count);
        Assert.All(first.Requests, request => Assert.Equal("a-model", request.ModelId));
        Assert.NotEmpty(first.Requests[1].TailMessages);
        Assert.Empty(Assert.Single(second.Requests).TailMessages);
        Assert.Equal("b-model", second.Requests[0].ModelId);
        Assert.All(traces.Where(t => t.CharacterId == "a"), t => Assert.Equal("a-connection", t.ConnectionId));
        Assert.All(traces.Where(t => t.CharacterId == "b"), t => Assert.Equal("b-connection", t.ConnectionId));
        Assert.All(traces, t => Assert.Equal("fixture-api", t.ApiSpecId));
        Assert.Equal(3, loop.CallsMade);
    }

    [Fact]
    public async Task Protocol_failure_budget_is_shared_and_prevents_resolving_another_role() {
        var invalid = new CompletionResult(new ActionMessage([]), new("fixture", "fixture-api", "model"));
        var client = new Client(invalid);
        int resolutions = 0;
        var loop = new LlmDecisionLoop((actor, token) => {
            resolutions++;
            return ValueTask.FromResult(new LlmDecisionBinding(client, actor, actor));
        }, new() { MaxConsecutiveProtocolFailures = 1 });
        foreach (string actor in new[] { "a", "a", "b" }) {
            var paused = await Assert.ThrowsAsync<LlmControllerPausedException>(async () =>
                await loop.ChooseCommandAsync(new(actor, 1, "material", "policy"), default));
            Assert.Equal("protocol-failure-limit", paused.ReasonCode);
        }
        Assert.Equal(1, resolutions);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task Concurrent_decisions_are_rejected_before_invoking_a_second_model() {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<CompletionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client {
            Invoke = (_, _) => { entered.SetResult(); return release.Task; }
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
}
