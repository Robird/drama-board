using DramaBoard.Kernel.Time;
using DramaBoard.Playground;
using DramaBoard.Playground.Cli;

internal sealed record PlaygroundHostOptions(int MaxCalls = 128, int MaxFailures = 3, int MaxContextCharacters = 200_000,
    string? LogPath = null, bool? Gym = null, string? Branch = null,
    ModelTime? Until = null, int MaxSteps = 10_000, string? Content = null,
    string? ConnectionsPath = null, string? CharacterControlsPath = null, string? HomePath = null,
    CharacterControlConfiguration? Controls = null, bool Lookahead = false,
    Func<bool>? StopRequested = null,
    Action<PlaygroundDecisionContext, TimeSpan>? ServiceCompleted = null,
    Action<TimeSpan>? RawAuditCompleted = null,
    Action<TimeSpan>? HostJournalCompleted = null,
    Action<string, object>? LookaheadEvidence = null);
