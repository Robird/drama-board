using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Simulation;

/// <summary>Captures immutable simulation control state that must survive across run boundaries.</summary>
public sealed record SimulationCursor
{
    private SimulationCursor(
        long lineageId,
        ModelTime now,
        int resolveCountAtCurrentTime,
        ResolvedCandidateIdentity? lastResolvedCandidate,
        bool lastResolveProducedNoEvents)
    {
        LineageId = lineageId;
        Now = now;
        ResolveCountAtCurrentTime = resolveCountAtCurrentTime;
        LastResolvedCandidate = lastResolvedCandidate;
        LastResolveProducedNoEvents = lastResolveProducedNoEvents;
    }

    /// <summary>Gets the stable identity of the world lineage being advanced.</summary>
    public long LineageId { get; private init; }

    /// <summary>Gets the current model time reached by the simulation.</summary>
    public ModelTime Now { get; private init; }

    /// <summary>Gets the number of candidates resolved at the current model time.</summary>
    public int ResolveCountAtCurrentTime { get; private init; }

    internal ResolvedCandidateIdentity? LastResolvedCandidate { get; private init; }

    internal bool LastResolveProducedNoEvents { get; private init; }

    /// <summary>Creates a cursor at the start of a lineage with empty loop guards.</summary>
    public static SimulationCursor CreateInitial(long lineageId, ModelTime startTime) =>
        new(lineageId, startTime, 0, null, false);

    internal bool IsRepeatedNoOp(ResolvedCandidateIdentity candidate) =>
        LastResolveProducedNoEvents && LastResolvedCandidate == candidate;

    internal SimulationCursor AdvanceTo(ModelTime now) =>
        this with
        {
            Now = now,
            ResolveCountAtCurrentTime = 0,
        };

    internal SimulationCursor RecordResolve(ResolvedCandidateIdentity candidate, bool producedNoEvents) =>
        this with
        {
            ResolveCountAtCurrentTime = checked(ResolveCountAtCurrentTime + 1),
            LastResolvedCandidate = candidate,
            LastResolveProducedNoEvents = producedNoEvents,
        };

    internal SimulationCursor RecordExternalInputs() =>
        this with
        {
            LastResolvedCandidate = null,
            LastResolveProducedNoEvents = false,
        };
}

internal readonly record struct ResolvedCandidateIdentity(
    long SourceId,
    EventCandidateId CandidateId,
    ModelTime Due);
