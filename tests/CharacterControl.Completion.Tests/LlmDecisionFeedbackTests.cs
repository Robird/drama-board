using System.Text.Json;
using Atelia.Completion.Abstractions;
using static DramaBoard.CharacterControl.Completion.Tests.LlmDecisionTestSupport;

namespace DramaBoard.CharacterControl.Completion.Tests;

public sealed class LlmDecisionFeedbackTests {
    [Fact]
    public async Task Disabled_feedback_keeps_single_act_contract_and_rejects_feedback() {
        var client = new Client(Feedback("f"));
        var loop = new LlmDecisionLoop(client, "model",
            new() { MaxConsecutiveProtocolFailures = 1 });
        var error = await Assert.ThrowsAsync<LlmControllerPausedException>(async () =>
            await loop.ChooseCommandAsync(Input(), default));
        Assert.Equal("protocol-failure-limit", error.ReasonCode);
        var contract = client.Requests[0].PromptPrefix.OutputContract;
        Assert.Equal(CompletionToolChoiceKind.Auto, contract.ToolChoice.Kind);
        Assert.False(contract.AllowParallelToolCalls);
        var definition = Assert.Single(contract.Tools);
        Assert.Equal("act", definition.Name);
        var schema = Assert.IsType<ToolSchema.Object>(definition.InputSchema);
        Assert.False(schema.AdditionalProperties);
        Assert.True(Assert.Single(schema.Properties).IsRequired);
        Assert.DoesNotContain("feedback", client.Requests[0].PromptPrefix.SystemPrompt);
    }

    [Fact]
    public async Task Save_failure_is_a_pause_without_success_receipt_or_second_model_call() {
        var client = new Client(Feedback("f"));
        var traces = new List<LlmControllerTrace>();
        var loop = new LlmDecisionLoop(client, "model", trace: traces.Add,
            feedbackSink: (_, _) => throw new IOException("private storage details"));
        var error = await Assert.ThrowsAsync<LlmControllerPausedException>(async () =>
            await loop.ChooseCommandAsync(Input(), default));
        Assert.Equal("feedback-save-failed", error.ReasonCode);
        Assert.DoesNotContain("private", error.Message);
        Assert.Single(client.Requests);
        Assert.DoesNotContain(traces, t => t.Kind == LlmControllerTraceKind.Feedback || t.Operation is not null);
        Assert.Equal("feedback-save-failed", traces.Last().ProtocolError);
    }

    [Fact]
    public async Task Feedback_limit_and_protocol_correction_retain_only_successful_history() {
        var client = new Client(Feedback("f1"), Feedback("f2"), Act("act"));
        int saved = 0;
        var traces = new List<LlmControllerTrace>();
        var loop = new LlmDecisionLoop(client, "model",
            new() { MaxFeedbackPerDecision = 1 }, traces.Add,
            (_, _) => ValueTask.FromResult($"saved-{++saved}"));
        Assert.Equal("act", (await loop.ChooseCommandAsync(Input(), default)).ToolCallId);
        Assert.Equal(1, saved);
        Assert.Equal(3, loop.CallsMade);
        Assert.Equal("feedback-budget-exhausted", traces.Single(t => t.Kind == LlmControllerTraceKind.Response && t.CallNumber == 2).ProtocolError);
        Assert.Equal(3, client.Requests[2].TailMessages.Length);
        Assert.Equal("f1", Assert.Single(Assert.IsType<ActionMessage>(client.Requests[2].TailMessages[0]).ToolCalls).ToolCallId);
        Assert.Contains("feedback-budget-exhausted", Assert.IsType<ObservationMessage>(client.Requests[2].TailMessages[2]).Content);
    }

    [Fact]
    public async Task Saved_feedback_does_not_refund_total_calls() {
        var client = new Client(Feedback("f"));
        int saved = 0;
        var loop = new LlmDecisionLoop(client, "model", new() { MaxCalls = 1 },
            feedbackSink: (_, _) => ValueTask.FromResult($"saved-{++saved}"));
        var error = await Assert.ThrowsAsync<LlmControllerPausedException>(async () =>
            await loop.ChooseCommandAsync(Input(), default));
        Assert.Equal("call-budget-exhausted", error.ReasonCode);
        Assert.Equal(1, saved);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task Native_feedback_tail_counts_toward_context_budget_without_truncation() {
        var result = Feedback("f");
        result = result with { Message = new ActionMessage([new ActionBlock.Text(new string('x', 500)), .. result.Message.Blocks]) };
        var client = new Client(result);
        int saved = 0;
        var loop = new LlmDecisionLoop(client, "model", new() { MaxContextCharacters = 100 },
            feedbackSink: (_, _) => ValueTask.FromResult($"saved-{++saved}"));
        var error = await Assert.ThrowsAsync<LlmControllerPausedException>(async () =>
            await loop.ChooseCommandAsync(Input(), default));
        Assert.Equal("context-too-large", error.ReasonCode);
        Assert.Equal(1, saved);
        Assert.Single(client.Requests);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"text\":\"s\",\"extra\":1}")]
    [InlineData("{\"text\":\"s\",\"text\":\"s2\"}")]
    [InlineData("{\"text\":\" \"}")]
    [InlineData("{\"text\":null}")]
    [InlineData("{\"text\":1}")]
    [InlineData("{\"kind\":\"issue\",\"summary\":\"s\",\"obstacle\":\"o\"}")]
    public async Task Malformed_feedback_never_reaches_sink(string json) {
        var client = new Client(Result(new("feedback", "f", json)));
        var loop = new LlmDecisionLoop(client, "model", new() { MaxConsecutiveProtocolFailures = 1 },
            feedbackSink: (_, _) => throw new InvalidOperationException("must not save"));
        var error = await Assert.ThrowsAsync<LlmControllerPausedException>(async () =>
            await loop.ChooseCommandAsync(Input(), default));
        Assert.Equal("protocol-failure-limit", error.ReasonCode);
    }

    [Theory]
    [InlineData("原文\n  空白", true)]
    [InlineData("12345678", true)]
    [InlineData("123456789", false)]
    public async Task Feedback_text_preserves_original_content_and_enforces_decoded_length(string text, bool accepted) {
        var client = new Client(Result(new("feedback", "f", JsonSerializer.Serialize(new { text }))), Act("act"));
        var saved = new List<LlmFeedbackSubmission>();
        var traces = new List<LlmControllerTrace>();
        var loop = new LlmDecisionLoop(client, "model",
            new() { MaxFeedbackCharacters = 8, MaxConsecutiveProtocolFailures = 1 }, traces.Add,
            (submission, _) => { saved.Add(submission); return ValueTask.FromResult("saved"); });
        if (accepted) {
            Assert.Equal("act", (await loop.ChooseCommandAsync(Input(), default)).ToolCallId);
            Assert.Equal(text, Assert.Single(saved).Text);
        } else {
            var error = await Assert.ThrowsAsync<LlmControllerPausedException>(async () =>
                await loop.ChooseCommandAsync(Input(), default));
            Assert.Equal("protocol-failure-limit", error.ReasonCode);
            Assert.Empty(saved);
            Assert.Equal("feedback-too-large", traces.Last().ProtocolError);
            Assert.Single(client.Requests);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Multiple_tools_or_incomplete_response_cannot_partially_save(bool multipleTools) {
        var result = Feedback("f");
        result = multipleTools
            ? result with { Message = new ActionMessage([.. result.Message.Blocks, .. Act("act").Message.Blocks]) }
            : result with { Termination = new(CompletionTerminationKind.Incomplete) };
        var client = new Client(result);
        var loop = new LlmDecisionLoop(client, "model", new() { MaxConsecutiveProtocolFailures = 1 },
            feedbackSink: (_, _) => throw new InvalidOperationException("must not save"));
        var error = await Assert.ThrowsAsync<LlmControllerPausedException>(async () =>
            await loop.ChooseCommandAsync(Input(), default));
        Assert.Equal(multipleTools ? "protocol-failure-limit" : "completion-incomplete", error.ReasonCode);
    }

    [Fact]
    public async Task Cancellation_after_durable_save_keeps_saved_trace_but_never_continues() {
        using var source = new CancellationTokenSource();
        var traces = new List<LlmControllerTrace>();
        var client = new Client(Feedback("f"));
        var loop = new LlmDecisionLoop(client, "model", trace: traces.Add,
            feedbackSink: (_, _) => { source.Cancel(); return ValueTask.FromResult("saved"); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await loop.ChooseCommandAsync(Input(), source.Token));
        Assert.Single(client.Requests);
        Assert.Equal("saved", Assert.Single(traces, t => t.Kind == LlmControllerTraceKind.Feedback).FeedbackId);
    }
}
