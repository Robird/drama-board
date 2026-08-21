using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Simulation;

/// <summary>Checks committed causes against the current build's Forecast and scheduler semantics.</summary>
public static class SchedulerConformance
{
    /// <summary>
    /// Rebuilds from Genesis, recomputes each winner, and folds recorded facts without invoking Plan.
    /// </summary>
    public static ReplayResult<TWorld> Verify<TWorld, TCandidateData, TFact>(
        TWorld genesisWorld,
        long lineageId,
        ModelTime genesisTime,
        SimulationRules simulationRules,
        IEnumerable<IOccurrenceRule<TWorld, TCandidateData, TFact>> rules,
        IEnumerable<JournalBatch<TFact>> batches,
        Func<TWorld, LogicalInstant, TFact, TWorld> fold,
        Action<TWorld> validate)
    {
        if (genesisWorld is null)
        {
            throw new ArgumentNullException(nameof(genesisWorld));
        }

        ArgumentNullException.ThrowIfNull(simulationRules);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(batches);
        ArgumentNullException.ThrowIfNull(fold);
        ArgumentNullException.ThrowIfNull(validate);

        IOccurrenceRule<TWorld, TCandidateData, TFact>[] ruleArray = [.. rules];
        if (ruleArray.Any(rule => rule is null))
        {
            throw new ArgumentException("Occurrence rules cannot contain null entries.", nameof(rules));
        }

        TWorld world = genesisWorld;
        LogicalInstant? lastCommittedInstant = null;
        CandidateKey? previousCauseKey = null;
        long transitionCount = 0;
        foreach (JournalBatch<TFact> batch in batches)
        {
            if (batch is null)
            {
                throw new InvalidOperationException("Conformance input cannot contain a null batch.");
            }

            if (previousCauseKey == batch.CauseKey)
            {
                throw new InvalidOperationException(
                    $"Candidate '{batch.CauseKey}' was committed in adjacent transitions; " +
                    "the recorded rule made no key-visible authoritative progress.");
            }

            ModelTime currentModelTime = lastCommittedInstant?.ModelTime ?? genesisTime;
            ForecastWinner<TWorld, TCandidateData, TFact>? selection = ForecastRound.SelectWinner(
                world,
                currentModelTime,
                simulationRules,
                ruleArray);
            if (selection is null)
            {
                throw new InvalidOperationException(
                    "Committed Journal history continues after the current build Forecast is exhausted.");
            }

            LogicalInstant expectedInstant = LogicalInstantRules.Propose(
                selection.Candidate.Due,
                genesisTime,
                lastCommittedInstant,
                simulationRules.MaxTransitionsPerModelTime);
            if (batch.CauseKey != selection.Candidate.Key)
            {
                throw new InvalidOperationException(
                    $"Committed cause '{batch.CauseKey}' is not the current scheduler winner " +
                    $"'{selection.Candidate.Key}'.");
            }

            if (batch.Instant != expectedInstant)
            {
                throw new InvalidOperationException(
                    $"Committed instant {batch.Instant} does not match expected winner instant {expectedInstant}.");
            }

            TWorld scratchWorld = world;
            foreach (TFact fact in batch.Facts)
            {
                scratchWorld = fold(scratchWorld, batch.Instant, fact);
                if (scratchWorld is null)
                {
                    throw new InvalidOperationException("The conformance fact fold returned a null HostWorld.");
                }
            }

            validate(scratchWorld);
            world = scratchWorld;
            lastCommittedInstant = batch.Instant;
            previousCauseKey = batch.CauseKey;
            transitionCount = checked(transitionCount + 1);
        }

        return new ReplayResult<TWorld>(
            world,
            new WorldVersion(lineageId, transitionCount),
            lastCommittedInstant,
            lastCommittedInstant?.ModelTime ?? genesisTime);
    }
}
