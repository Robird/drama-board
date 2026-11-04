using Atelia.Completion.Abstractions;
using static DramaBoard.CharacterControl.Completion.Tests.LlmDecisionTestSupport;

namespace DramaBoard.CharacterControl.Completion.Tests;

public sealed class LlmDecisionProtocolTests {
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
        var client = new Client(result, result, result);
        var traces = new List<LlmControllerTrace>();
        var loop = new LlmDecisionLoop(client, "fixture-model", trace: traces.Add);
        var exception = await Assert.ThrowsAsync<LlmControllerPausedException>(async () =>
            await loop.ChooseCommandAsync(Input(), default));
        Assert.Equal("protocol-failure-limit", exception.ReasonCode);
        Assert.Equal(3, exception.CallsMade);
        Assert.Equal(3, client.Requests.Count);
        var responses = traces.Where(trace => trace.Kind == LlmControllerTraceKind.Response).ToArray();
        Assert.Equal(3, responses.Length);
        Assert.All(responses, trace => {
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
        var client = new Client(invalid, Result(Call("1", "wait 1")), invalid, Result(Call("2", "wait 1")));
        var loop = new LlmDecisionLoop(client, "fixture-model",
            new() { MaxConsecutiveProtocolFailures = 2 });
        LlmDecisionInput context = Input();
        Assert.Equal("1", (await loop.ChooseCommandAsync(context, default)).ToolCallId);
        Assert.Equal("2", (await loop.ChooseCommandAsync(context, default)).ToolCallId);
        Assert.Equal(4, loop.CallsMade);
    }

    [Theory]
    [InlineData(CompletionTerminationKind.Incomplete, "completion-incomplete")]
    [InlineData(CompletionTerminationKind.Failed, "completion-failed")]
    public async Task Non_completed_results_with_full_looking_tool_calls_pause_without_execution_or_retry(CompletionTerminationKind kind, string code) {
        var client = new Client(Result(Call("id", "wait 1")) with { Termination = new(kind) });
        var loop = new LlmDecisionLoop(client, "model");
        var exception = await Assert.ThrowsAsync<LlmControllerPausedException>(async () =>
            await loop.ChooseCommandAsync(Input(), default));
        Assert.Equal(code, exception.ReasonCode);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task Oversized_material_is_not_truncated_or_sent() {
        var client = new Client();
        var loop = new LlmDecisionLoop(client, "model", new() { MaxContextCharacters = 4 });
        var exception = await Assert.ThrowsAsync<LlmControllerPausedException>(async () =>
            await loop.ChooseCommandAsync(Input(material: "12345"), default));
        Assert.Equal("context-too-large", exception.ReasonCode);
        Assert.Empty(client.Requests);
        Assert.Equal(0, loop.CallsMade);
    }

    [Theory]
    [InlineData("12345", "command-too-large")]
    [InlineData("large-json", "arguments-too-large")]
    public async Task Oversized_tool_input_is_rejected(string value, string error) {
        RawToolCall call = value == "large-json" ? new("act", "id", new string(' ', 1100)) : Call("id", value);
        var traces = new List<LlmControllerTrace>();
        var loop = new LlmDecisionLoop(new Client(Result(call)), "model",
            new() { MaxCommandCharacters = 4, MaxConsecutiveProtocolFailures = 1 }, traces.Add);
        await Assert.ThrowsAsync<LlmControllerPausedException>(async () => await loop.ChooseCommandAsync(Input(), default));
        Assert.Equal(error, traces.Last().ProtocolError);
    }

    [Fact]
    public async Task Transport_interruption_pauses_without_retry_and_keeps_structured_cause() {
        var failure = new CompletionFailureException(new(CompletionFailureKind.Transport), "private provider diagnostic");
        var client = new Client { Invoke = (_, _) => throw failure };
        var loop = new LlmDecisionLoop(client, "model");
        var exception = await Assert.ThrowsAsync<LlmControllerPausedException>(async () =>
            await loop.ChooseCommandAsync(Input(), default));
        Assert.Equal("transport-failure", exception.ReasonCode);
        Assert.DoesNotContain("private", exception.Message);
        Assert.Same(failure, exception.InnerException);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task Local_request_rejection_is_a_host_pause_with_safe_diagnostics() {
        var rejection = new CompletionRequestRejectedException(CompletionTermination.Failed("unsupported-contract"));
        var client = new Client { Invoke = (_, _) => throw rejection };
        var traces = new List<LlmControllerTrace>();
        var loop = new LlmDecisionLoop(client, "model", trace: traces.Add);
        var exception = await Assert.ThrowsAsync<LlmControllerPausedException>(async () =>
            await loop.ChooseCommandAsync(Input(), default));
        Assert.Equal("request-rejected", exception.ReasonCode);
        Assert.Same(rejection, exception.InnerException);
        Assert.Equal("request-rejected", traces.Last().ProtocolError);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task Cancellation_retains_token_and_pre_cancelled_calls_spend_no_budget() {
        using var source = new CancellationTokenSource();
        var client = new Client { Invoke = (_, token) => {
            source.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException();
        } };
        var loop = new LlmDecisionLoop(client, "model");
        LlmDecisionInput context = Input();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await loop.ChooseCommandAsync(context, source.Token));
        Assert.Equal(source.Token, exception.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await loop.ChooseCommandAsync(context, source.Token));
        Assert.Single(client.Requests);
        Assert.Equal(1, loop.CallsMade);
    }
}
