using DramaBoard.Microtown;

namespace DramaBoard.Microtown.Cli.Gym;

/// <summary>Stable text identifiers for host evidence; the game validates every field before a fork.</summary>
public sealed record TownGymBoundary(string GameId, int GameRevision, string BranchId, string StateCommitId,
    string ActorId, long DecisionSequence, long OperationSequence, long LineageId, long TransitionCount, long TimeTicks) {
    public static TownGymBoundary From(TownDecisionBoundary boundary) => new(boundary.GameId, boundary.GameRevision,
        TownDurableHistory.FormatBranchId(boundary.BranchId), TownDurableHistory.FormatCommitId(boundary.StateCommitId),
        boundary.ActorId, boundary.DecisionSequence, boundary.OperationSequence,
        boundary.LineageId, boundary.TransitionCount, boundary.TimeTicks);
}

/// <summary>Public, nonsecret host settings. Never pass connection credentials or client configuration here.</summary>
public sealed record TownGymRunOptions(string? Model = null, string? Connection = null, int MaxCalls = 128,
    int MaxOperations = 200, bool FeedbackEnabled = false, string? HumanActor = null,
    bool LlmEnabled = true, string? ExperimentNote = null, int MaxFailures = 3, int MaxContextCharacters = 200000);
