using DramaBoard.Kernel.Scheduling;

namespace DramaBoard.Kernel.Simulation;

/// <summary>Forecasts occurrences and plans the complete transition for its selected candidate.</summary>
public interface IOccurrenceRule<TWorld, TCandidateData, TFact>
{
    /// <summary>Purely forecasts every current candidate owned by this rule.</summary>
    IReadOnlyList<OccurrenceCandidate<TCandidateData>> Forecast(
        TWorld world,
        SimulationRules rules);

    /// <summary>Plans the selected candidate without committing or installing world state.</summary>
    ValueTask<TransitionDraft<TFact>> PlanSelectedAsync(
        TWorld world,
        OccurrenceCandidate<TCandidateData> winner,
        CancellationToken cancellationToken);
}
