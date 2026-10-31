using Atelia.DurableGraph;

namespace DramaBoard.Goals;

/// <summary>Small authoritative feedback; long submitted text remains in the operation's raw input.</summary>
[DurableType("DramaBoard.Goals.GoalReceipt", 1)]
public sealed partial class GoalReceipt : IDurableObject {
    [DurableField(1)] private GoalOperationKind _kind;
    [DurableField(2)] private string? _goalId;
    [DurableField(3)] private string? _linkId;
    [DurableField(4)] private bool _changed;
    [DurableField(5)] private bool _success;
    [DurableField(6)] private string _message;
    [DurableField(7)] private List<string> _changedFields;
    [DurableField(8)] private List<string> _focusBefore;
    [DurableField(9)] private List<string> _focusAfter;
    public GoalOperationKind Kind => _kind;
    public string? GoalId => _goalId;
    public string? LinkId => _linkId;
    public bool Changed => _changed;
    public bool Success => _success;
    public string Message => _message;
    public IReadOnlyList<string> ChangedFields => _changedFields.AsReadOnly();
    public IReadOnlyList<string> FocusBefore => _focusBefore.AsReadOnly();
    public IReadOnlyList<string> FocusAfter => _focusAfter.AsReadOnly();

    public GoalReceipt(GoalOperationKind kind, bool success, bool changed, string message,
        string? goalId = null, string? linkId = null, IEnumerable<string>? changedFields = null,
        IEnumerable<string>? focusBefore = null, IEnumerable<string>? focusAfter = null) {
        _kind = kind; _success = success; _changed = changed; _message = message; _goalId = goalId; _linkId = linkId;
        _changedFields = changedFields?.ToList() ?? []; _focusBefore = focusBefore?.ToList() ?? []; _focusAfter = focusAfter?.ToList() ?? [];
    }

    public void Validate() {
        if (!Enum.IsDefined(Kind) || !GoalLimits.ValidText(Message, 4096, true) ||
            GoalId is not null && GoalLimits.Identity(GoalId, 'G') == 0 || LinkId is not null && GoalLimits.Identity(LinkId, 'L') == 0 ||
            _changedFields is null || _focusBefore is null || _focusAfter is null ||
            ChangedFields.Count > 16 || ChangedFields.Any(field => !GoalLimits.ValidText(field, 32, true)) ||
            FocusBefore.Count > GoalLimits.MaximumGoals || FocusAfter.Count > GoalLimits.MaximumGoals ||
            FocusBefore.Concat(FocusAfter).Any(id => id is null || GoalLimits.Identity(id, 'G') == 0) ||
            !Success && Changed || !Changed && (ChangedFields.Count != 0 || !FocusBefore.SequenceEqual(FocusAfter)) ||
            Kind == GoalOperationKind.Show && Changed) {
            throw new InvalidOperationException("Invalid goal operation receipt.");
        }
    }

    public bool SameAs(GoalReceipt? other) => other is not null && Kind == other.Kind && GoalId == other.GoalId && LinkId == other.LinkId &&
        Changed == other.Changed && Success == other.Success && Message == other.Message && ChangedFields.SequenceEqual(other.ChangedFields) &&
        FocusBefore.SequenceEqual(other.FocusBefore) && FocusAfter.SequenceEqual(other.FocusAfter);
}

public sealed record GoalOperationResult(GoalSet After, GoalReceipt Receipt, string? Error) {
    public bool Success => Error is null;
}
