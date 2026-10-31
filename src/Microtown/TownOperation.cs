using Atelia.DurableGraph;
using DramaBoard.Goals;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Microtown;

/// <summary>Captured controller output before game parsing. IDs correlate evidence; they do not authorize replaying an operation.</summary>
[DurableType("DramaBoard.Microtown.ControllerOperation", 1)]
public sealed partial record TownControllerOperation(
    [field: DurableField(1)] string RawInput,
    [field: DurableField(2)] string ToolCallId,
    [field: DurableField(3)] string OperationRecordId) : IDurableObject {
    public void Validate() {
        ArgumentNullException.ThrowIfNull(RawInput);
        ArgumentException.ThrowIfNullOrWhiteSpace(ToolCallId);
        ArgumentException.ThrowIfNullOrWhiteSpace(OperationRecordId);
    }
}

/// <summary>A saved result at the decision that executed it. Rejections retain their raw input and correlation identities too.</summary>
[DurableType("DramaBoard.Microtown.OperationReceipt", 1)]
public sealed partial record TownOperationReceipt(
    [field: DurableField(1)] string ActorId,
    [field: DurableField(2)] TownControllerOperation Operation,
    [field: DurableField(3)] bool Accepted,
    [field: DurableField(4)] string Result,
    [field: DurableField(5)] ModelTime At,
    [field: DurableField(6)] long DecisionSequence,
    [field: DurableField(7)] long OperationSequence,
    [field: DurableField(8)] GoalReceipt? GoalReceipt = null) : IDurableObject {
    public void Validate() {
        ArgumentException.ThrowIfNullOrWhiteSpace(ActorId);
        ArgumentNullException.ThrowIfNull(Operation);
        Operation.Validate();
        if (string.IsNullOrWhiteSpace(Result) || At < ModelTime.Zero || DecisionSequence <= 0 || OperationSequence <= 0 ||
            GoalReceipt is not null && GoalReceipt.Success != Accepted) {
            throw new InvalidOperationException("Invalid town operation receipt.");
        }
        GoalReceipt?.Validate();
    }
}

/// <summary>One resident's latest activity outcome, independent of their latest command receipt. This is not a complete memory history.</summary>
[DurableType("DramaBoard.Microtown.Experience", 2)]
public sealed partial record TownExperience(
    [field: DurableField(1)] TownEventKind Kind,
    [field: DurableField(2)] ModelTime At,
    [field: DurableField(3)] string Text) : IDurableObject {
    public void Validate() {
        if (Kind is not (TownEventKind.ProductionCompleted or TownEventKind.WaitCompleted or TownEventKind.Arrived or TownEventKind.Failed) ||
            At < ModelTime.Zero || string.IsNullOrWhiteSpace(Text)) {
            throw new InvalidOperationException("Invalid resident experience.");
        }
    }
}
