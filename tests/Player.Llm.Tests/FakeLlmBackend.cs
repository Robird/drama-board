namespace DramaBoard.Player.Llm.Tests;

internal sealed class FakeLlmBackend : ILlmChatBackend
{
    private readonly Queue<string> _responses;
    private readonly List<LlmChatRequest> _requests = [];

    public FakeLlmBackend(IEnumerable<string> responses)
    {
        ArgumentNullException.ThrowIfNull(responses);
        _responses = new Queue<string>(responses);
    }

    public IReadOnlyList<LlmChatRequest> Requests => _requests.AsReadOnly();

    public Task<string> CompleteAsync(
        LlmChatRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        _requests.Add(request);
        if (!_responses.TryDequeue(out string? response))
        {
            throw new InvalidOperationException("The fake LLM backend has no scripted response remaining.");
        }

        return Task.FromResult(response);
    }
}