namespace DramaBoard.FirstBoard.Persistence;

/// <summary>Names the externally supplied strategy contract and the actors it controls.
/// PolicyId must change when assignments or strategy semantics change. Driver instances,
/// memory and external-call state are not saved by this binding.</summary>
public sealed class FirstBoardDriverBinding
{
    private readonly string[] _actorIds;

    public FirstBoardDriverBinding(IEnumerable<string> actorIds, string policyId)
    {
        ArgumentNullException.ThrowIfNull(actorIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyId);
        _actorIds = actorIds.ToArray();
        if (_actorIds.Any(string.IsNullOrWhiteSpace) ||
            _actorIds.Distinct(StringComparer.Ordinal).Count() != _actorIds.Length)
        {
            throw new ArgumentException("Driver actor IDs must be nonempty and unique.", nameof(actorIds));
        }
        Array.Sort(_actorIds, StringComparer.Ordinal);
        PolicyId = policyId;
    }

    public IReadOnlyList<string> ActorIds => Array.AsReadOnly(_actorIds);
    public string PolicyId { get; }

    internal bool Matches(FirstBoardDriverBinding other) =>
        string.Equals(PolicyId, other.PolicyId, StringComparison.Ordinal) &&
        _actorIds.SequenceEqual(other._actorIds, StringComparer.Ordinal);
}
