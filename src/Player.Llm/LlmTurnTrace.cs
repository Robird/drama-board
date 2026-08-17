using DramaBoard.Protocol;

namespace DramaBoard.Player.Llm;

/// <summary>Identifies how one memory shard concluded its maintenance attempt.</summary>
public enum MemoryMaintenanceOperation
{
    Keep,
    Replace,
    FallbackKeep,
}

/// <summary>Captures the outcome of independently maintaining one memory shard.</summary>
public sealed record MemoryShardMaintenanceTrace(
    string ShardKey,
    MemoryMaintenanceOperation Operation,
    string? Error);

/// <summary>Captures one successfully parsed cognitive turn at the private-memory commit point.</summary>
public sealed record LlmTurnTrace(
    DecisionRequest Request,
    PlayerDecision Decision,
    string Monologue,
    string? Dialogue,
    string MemoryProposal,
    string Memory,
    IReadOnlyList<MemoryShardMaintenanceTrace> MemoryMaintenance,
    int AttemptCount);
