using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Simulation;

internal static class LogicalInstantRules
{
    public static LogicalInstant Propose(
        CandidateDue due,
        ModelTime genesisTime,
        LogicalInstant? lastCommittedInstant,
        int maxTransitionsPerModelTime)
    {
        ModelTime currentModelTime = lastCommittedInstant?.ModelTime ?? genesisTime;
        if (due.ModelTime < currentModelTime)
        {
            throw new InvalidOperationException(
                $"A transition cannot be committed at {due.ModelTime} before {currentModelTime}.");
        }

        long causalOrdinal = lastCommittedInstant is not LogicalInstant previous ||
            due.ModelTime > previous.ModelTime
                ? 0
                : checked(previous.CausalOrdinal + 1);
        if (causalOrdinal >= maxTransitionsPerModelTime)
        {
            throw new InvalidOperationException(
                $"Transition budget of {maxTransitionsPerModelTime} exhausted at model time {due.ModelTime}.");
        }

        return new LogicalInstant(due.ModelTime, causalOrdinal);
    }
}
