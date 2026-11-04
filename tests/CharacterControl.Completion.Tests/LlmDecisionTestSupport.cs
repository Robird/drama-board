using System.Text.Json;
using Atelia.Completion.Abstractions;

namespace DramaBoard.CharacterControl.Completion.Tests;

internal static class LlmDecisionTestSupport {
    internal static LlmDecisionInput Input(string material = "material") => new("a", 1, material, "policy");
    internal static CompletionDescriptor Descriptor => new("fixture", "fixture-api", "fixture-model");
    internal static RawToolCall Call(string id, string command) => new("act", id, JsonSerializer.Serialize(new { command }));
    internal static CompletionResult Result(RawToolCall call) => new(new ActionMessage([new ActionBlock.ToolCall(call)]), Descriptor);
    internal static CompletionResult Act(string id, string command = "wait 1") => Result(Call(id, command));
    internal static CompletionResult Feedback(string id) => Result(new("feedback", id,
        JsonSerializer.Serialize(new { text = "feedback text" })));

    internal sealed class Client(params CompletionResult[] results) : ICompletionClient {
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
