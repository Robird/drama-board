using Atelia.DurableGraph;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Journal;

/// <summary>A complete ordered change whose planning and scratch validation have succeeded.
/// Facts and their reachable persistent graph must remain snapshots after publication.</summary>
[DurableType("DramaBoard.Kernel.OccurrenceEvent", 1)]
public sealed partial class OccurrenceEvent<TFact> : IDurableObject
{
    [DurableField(1)] private readonly CandidateKey _causeKey;
    [DurableField(2)] private readonly LogicalInstant _targetInstant;
    [DurableField(3)] private readonly TFact[] _facts;

    public OccurrenceEvent(CandidateKey causeKey, LogicalInstant targetInstant, IEnumerable<TFact> facts)
    {
        ArgumentNullException.ThrowIfNull(causeKey);
        ArgumentNullException.ThrowIfNull(facts);
        _causeKey = causeKey;
        _targetInstant = targetInstant;
        _facts = facts.ToArray();
        Validate();
    }

    public CandidateKey CauseKey => _causeKey;
    public LogicalInstant TargetInstant => _targetInstant;
    public IReadOnlyList<TFact> Facts => Array.AsReadOnly(_facts);

    public void Validate()
    {
        if (_causeKey is null || _causeKey.Length == 0 || _targetInstant.CausalOrdinal < 0 ||
            _facts is null || _facts.Length == 0 || _facts.Any(fact => fact is null))
        {
            throw new ArgumentException("An occurrence requires a valid cause, instant and nonempty, non-null ordered facts.");
        }
    }
}
