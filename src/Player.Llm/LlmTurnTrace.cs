using DramaBoard.Protocol;

namespace DramaBoard.Player.Llm;

/// <summary>Captures one successfully parsed cognitive turn at the private-memory commit point.</summary>
public sealed record LlmTurnTrace(
    DecisionRequest Request,
    PlayerDecision Decision,
    string Monologue,
    string? Dialogue,
    string Memory,
    int AttemptCount);
