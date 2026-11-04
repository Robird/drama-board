using System.Text.Json;
using Atelia.Completion.Abstractions;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Playground.Completion.Tests;

public sealed class LlmFeedbackTests {
    [Fact]
    public async Task Adapter_forwards_feedback_identity_without_changing_frozen_context_and_returns_game_operation() {
        var feedback = new CompletionResult(new ActionMessage([new ActionBlock.ToolCall(new("feedback", "feedback-call",
            JsonSerializer.Serialize(new { text = "feedback text" })))]), new("fixture", "fixture-api", "fixture-model"));
        var act = new CompletionResult(new ActionMessage([new ActionBlock.ToolCall(new("act", "act-call",
            "{\"command\":\"wait 1\"}"))]), new("fixture", "fixture-api", "fixture-model"));
        var client = new Client(feedback, act);
        var submissions = new List<LlmFeedbackSubmission>();
        var initial = PlaygroundGameFactory.CreateSurvival(enableWeather: false);
        var kernel = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor));
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(30 * PlaygroundTime.Hour)));
        var context = PlaygroundDecisionContext.Create(kernel.World, "a");
        var before = context.InteractionState;
        var controller = new LlmCharacterController(client, "fixture-model", _ => "材料",
            feedbackSink: (submission, _) => {
                submissions.Add(submission);
                return ValueTask.FromResult("receipt-1");
            });

        PlaygroundControllerOperation operation = await controller.ChooseCommandAsync(context, default);

        Assert.Equal(context.InteractionState.ActorId, Assert.Single(submissions).CharacterId);
        Assert.Equal(context.Context.ContextSequence, submissions[0].ContextSequence);
        Assert.Same(before, context.InteractionState);
        Assert.Empty(context.Context.Items);
        Assert.Equal("act-call", operation.ToolCallId);
        Assert.Equal("wait 1", operation.RawInput);
        Assert.False(string.IsNullOrWhiteSpace(operation.OperationRecordId));
    }

    private sealed class Client(params CompletionResult[] results) : ICompletionClient {
        private readonly Queue<CompletionResult> _results = new(results);
        public string Name => "fixture";
        public string ApiSpecId => "fixture-api";
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default) => Task.FromResult(_results.Dequeue());
    }
}
