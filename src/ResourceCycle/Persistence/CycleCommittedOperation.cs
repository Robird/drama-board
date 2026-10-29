namespace DramaBoard.ResourceCycle;

/// <summary>Recovered semantic receipt from a completed game boundary, not verbatim controller output.</summary>
public sealed record CycleCommittedOperation(string OperationRecordId, string ToolCallId, string ActorId,
    string Result, CycleOperationDisposition Disposition, long TimeTicks, long LineageId, long TransitionCount, string DomainEvent);
