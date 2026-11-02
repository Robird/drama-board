namespace DramaBoard.Playground;

/// <summary>Recovered semantic receipt from a completed game boundary, not verbatim controller output.</summary>
public sealed record PlaygroundCommittedOperation(string OperationRecordId, string ToolCallId, string ActorId,
    string Result, PlaygroundOperationDisposition Disposition, long TimeTicks, long LineageId, long TransitionCount, string DomainEvent);
