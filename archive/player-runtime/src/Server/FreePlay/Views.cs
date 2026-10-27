namespace DramaBoard.Server.FreePlay;

public sealed record MapPlace(string Id, string Label, int X, int Y);
public sealed record MapPassage(string Id, string From, string To);
public sealed record KnownMap(IReadOnlyList<MapPlace> Places, IReadOnlyList<MapPassage> Passages);
public sealed record TrajectoryEntry(string PlaceId, long ModelTimeMs, string? PassageId);
public sealed record InstructionView(string Kind, string? Passage, string? Note);
public sealed record KnownPassageView(string Handle, string EndpointA, string EndpointB,
    long ExpectedDurationMs, bool EnterableFromA, bool EnterableFromB);
public sealed record ProgrammingRequestView(string ActivationId, string Reason, string? ReasonDetail,
    string LocationText, IReadOnlyList<KnownPassageView> KnownPassages, string Workspace,
    IReadOnlyList<InstructionView> RemainingProgram);
public sealed record PlayerView(string RunId, long ViewRevision, long ModelTimeMs, string Status,
    string Location, KnownMap KnownMap, IReadOnlyList<TrajectoryEntry> Trajectory,
    string Workspace, IReadOnlyList<InstructionView> PendingProgram, ProgrammingRequestView? Request);
public sealed record InstantView(long ModelTimeMs, long CausalOrdinal);
public sealed record OccurrenceView(long ModelTimeMs, string CauseKey, IReadOnlyList<string> FactKinds);
public sealed record DevView(string RunId, long ViewRevision, long ModelTimeMs, string Status,
    string Location, long TransitionCount, InstantView? LastCommittedInstant,
    IReadOnlyList<OccurrenceView> Records, string ProcessMode, long ControlRevision,
    string? PendingActivationId, string? LastRejection, string? Fault);
public sealed record SubmitResult(int StatusCode, string Code, string Message);
