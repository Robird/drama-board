namespace DramaBoard.Server.FreePlay.Runtime;

/// <summary>Enumerates the process occurrence kinds the FreePlay process rule forecasts.</summary>
internal enum FreePlayProcessCandidateKind {
    /// <summary>The runtime requests one complete program from the dynamic programmer.</summary>
    Respond,

    /// <summary>The runtime starts the head instruction of a ready program.</summary>
    Start,

    /// <summary>The running think instruction has reached its completion time.</summary>
    ThinkComplete,
}

/// <summary>Represents the rule-private data of one FreePlay process occurrence candidate.</summary>
internal abstract record FreePlayProcessCandidateData;

/// <summary>
/// Identifies one FreePlay process occurrence by its kind only; the due time and candidate key are
/// re-derived from the committed world at every forecast, so no world snapshot is carried here.
/// </summary>
/// <param name="Kind">The process occurrence kind this candidate plans.</param>
internal sealed record FreePlayProcessOccurrenceData(FreePlayProcessCandidateKind Kind) : FreePlayProcessCandidateData;
