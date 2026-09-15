namespace DramaBoard.Player.Llm;

/// <summary>Configures a local Codex app-server process and per-turn model selection.</summary>
public sealed record CodexAppServerOptions(
    string CommandPath = "codex",
    string? Model = null,
    string? WorkingDirectory = null,
    string? ReasoningEffort = null,
    TimeSpan? RequestTimeout = null);
