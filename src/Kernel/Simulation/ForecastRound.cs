using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Simulation;

internal static class ForecastRound
{
    public static ForecastWinner<TWorld, TCandidateData, TFact>? SelectWinner<TWorld, TCandidateData, TFact>(
        TWorld world,
        ModelTime currentModelTime,
        SimulationRules simulationRules,
        IReadOnlyList<IOccurrenceRule<TWorld, TCandidateData, TFact>> rules)
    {
        var candidates = new List<OccurrenceCandidate<TCandidateData>>();
        var owners = new Dictionary<
            CandidateKey,
            IOccurrenceRule<TWorld, TCandidateData, TFact>>();

        foreach (IOccurrenceRule<TWorld, TCandidateData, TFact> rule in rules)
        {
            IReadOnlyList<OccurrenceCandidate<TCandidateData>> forecast =
                rule.Forecast(world, simulationRules)
                ?? throw new InvalidOperationException("An occurrence rule returned a null forecast.");

            foreach (OccurrenceCandidate<TCandidateData> candidate in forecast)
            {
                if (candidate is null)
                {
                    throw new InvalidOperationException("An occurrence rule forecast a null candidate.");
                }

                if (candidate.Due.ModelTime < currentModelTime)
                {
                    throw new InvalidOperationException(
                        $"Candidate '{candidate.Key}' is due at {candidate.Due.ModelTime}, " +
                        $"before current model time {currentModelTime}.");
                }

                if (!owners.TryAdd(candidate.Key, rule))
                {
                    throw new InvalidOperationException(
                        $"Duplicate candidate key '{candidate.Key}' was forecast in one round.");
                }

                candidates.Add(candidate);
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        OccurrenceCandidate<TCandidateData> winner =
            OccurrenceScheduler.SelectWinner(candidates, simulationRules.WorldSeed);
        return new ForecastWinner<TWorld, TCandidateData, TFact>(winner, owners[winner.Key]);
    }
}

internal sealed record ForecastWinner<TWorld, TCandidateData, TFact>(
    OccurrenceCandidate<TCandidateData> Candidate,
    IOccurrenceRule<TWorld, TCandidateData, TFact> Owner);
