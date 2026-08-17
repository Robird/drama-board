namespace DramaBoard.Player.Llm;

/// <summary>Completes one self-contained LLM conversation turn.</summary>
public interface ILlmChatBackend
{
    /// <summary>Returns the assistant text for one system/user request pair.</summary>
    Task<string> CompleteAsync(LlmChatRequest request, CancellationToken cancellationToken);
}

/// <summary>Contains the two prompt roles required by the LLM Player.</summary>
public sealed record LlmChatRequest(string System, string User);
