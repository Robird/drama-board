using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Simulation;

/// <summary>Rebuilds committed world state by folding complete Journal batches only.</summary>
public static class SimulationReplay
{
    /// <summary>Replays a complete current-format transition sequence without Forecast or Plan.</summary>
    public static ReplayResult<TWorld> Replay<TWorld, TFact>(
        TWorld genesisWorld,
        long lineageId,
        ModelTime genesisTime,
        IEnumerable<JournalBatch<TFact>> batches,
        Func<TWorld, LogicalInstant, TFact, TWorld> fold,
        Action<TWorld> validate)
    {
        if (genesisWorld is null)
        {
            throw new ArgumentNullException(nameof(genesisWorld));
        }

        ArgumentNullException.ThrowIfNull(batches);
        ArgumentNullException.ThrowIfNull(fold);
        ArgumentNullException.ThrowIfNull(validate);

        TWorld world = genesisWorld;
        LogicalInstant? lastCommittedInstant = null;
        long transitionCount = 0;
        foreach (JournalBatch<TFact> batch in batches)
        {
            if (batch is null)
            {
                throw new InvalidOperationException("Replay input cannot contain a null batch.");
            }

            ValidateInstant(batch.Instant, genesisTime, lastCommittedInstant);

            TWorld scratchWorld = world;
            foreach (TFact fact in batch.Facts)
            {
                scratchWorld = fold(scratchWorld, batch.Instant, fact);
                if (scratchWorld is null)
                {
                    throw new InvalidOperationException("The replay fact fold returned a null HostWorld.");
                }
            }

            validate(scratchWorld);
            world = scratchWorld;
            lastCommittedInstant = batch.Instant;
            transitionCount = checked(transitionCount + 1);
        }

        return new ReplayResult<TWorld>(
            world,
            new WorldVersion(lineageId, transitionCount),
            lastCommittedInstant,
            lastCommittedInstant?.ModelTime ?? genesisTime);
    }

    internal static void ValidateInstant(
        LogicalInstant instant,
        ModelTime genesisTime,
        LogicalInstant? previousInstant)
    {
        if (previousInstant is not LogicalInstant previous)
        {
            if (instant.ModelTime < genesisTime)
            {
                throw new InvalidOperationException("The first replay batch cannot precede Genesis.");
            }

            if (instant.CausalOrdinal != 0)
            {
                throw new InvalidOperationException("The first replay batch must have causal ordinal zero.");
            }

            return;
        }

        if (instant.ModelTime < previous.ModelTime)
        {
            throw new InvalidOperationException("Replay batch model time cannot decrease.");
        }

        long expectedOrdinal = instant.ModelTime > previous.ModelTime
            ? 0
            : checked(previous.CausalOrdinal + 1);
        if (instant.CausalOrdinal != expectedOrdinal)
        {
            throw new InvalidOperationException(
                $"Replay batch causal ordinal must be {expectedOrdinal} at model time {instant.ModelTime}.");
        }
    }
}

/// <summary>Contains the committed state reconstructed at a complete batch boundary.</summary>
public sealed record ReplayResult<TWorld>(
    TWorld World,
    WorldVersion Version,
    LogicalInstant? LastCommittedInstant,
    ModelTime CurrentModelTime);
