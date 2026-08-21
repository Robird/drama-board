using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Simulation;

/// <summary>Creates a new lineage from a complete in-memory Journal transition prefix.</summary>
public static class SimulationFork
{
    /// <summary>Copies and replays a batch prefix under a new lineage identity.</summary>
    public static InMemoryForkResult<TWorld, TFact> Create<TWorld, TFact>(
        TWorld genesisWorld,
        ModelTime genesisTime,
        InMemoryJournal<TFact> sourceJournal,
        long prefixTransitionCount,
        long newLineageId,
        SimulationRules simulationRules,
        Func<TWorld, LogicalInstant, TFact, TWorld> fold,
        Action<TWorld> validate)
    {
        ArgumentNullException.ThrowIfNull(sourceJournal);
        ArgumentNullException.ThrowIfNull(simulationRules);

        InMemoryJournal<TFact> prefixJournal =
            sourceJournal.ForkPrefix(prefixTransitionCount, newLineageId);
        ReplayResult<TWorld> replay = SimulationReplay.Replay(
            genesisWorld,
            newLineageId,
            genesisTime,
            prefixJournal.Batches,
            fold,
            validate);
        return new InMemoryForkResult<TWorld, TFact>(replay, simulationRules, prefixJournal);
    }
}

/// <summary>Contains a replayed new-lineage world and its independent complete-batch Journal prefix.</summary>
public sealed record InMemoryForkResult<TWorld, TFact>(
    ReplayResult<TWorld> Replay,
    SimulationRules SimulationRules,
    InMemoryJournal<TFact> Journal);
