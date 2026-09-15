namespace DramaBoard.Player.Llm;

/// <summary>Completes one self-contained LLM conversation turn.</summary>
public interface ILlmChatBackend
{
    /// <summary>Returns the assistant text and any provider-reported usage for one request pair.</summary>
    Task<LlmChatResponse> CompleteAsync(
        LlmChatRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Contains the two prompt roles required by the LLM Player.</summary>
public sealed record LlmChatRequest(string System, string User);

/// <summary>Contains assistant text plus optional provider timing and token measurements.</summary>
public sealed record LlmChatResponse(
    string Content,
    LlmTokenUsage? Usage = null,
    TimeSpan? QueueDuration = null,
    TimeSpan? ServiceDuration = null);

/// <summary>Normalizes token usage fields commonly returned by compatible providers.</summary>
public sealed record LlmTokenUsage(
    long? PromptTokens,
    long? CompletionTokens,
    long? TotalTokens,
    long? ReasoningTokens,
    long? CacheReadTokens,
    long? CacheMissTokens);
