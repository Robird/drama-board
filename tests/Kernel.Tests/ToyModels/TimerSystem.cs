using System.Globalization;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.ToyModels;

internal sealed record TimerEntity(long Id, string Name, ModelTime Due);

internal sealed record TimerWorld(
    IReadOnlyList<TimerEntity> Timers,
    IReadOnlyList<string> FiredTimers)
{
    public static TimerWorld Start(params TimerEntity[] timers) =>
        new(Array.AsReadOnly([.. timers]), []);
}

internal sealed record TimerFact(string TimerName);

internal sealed class TimerRule : IOccurrenceRule<TimerWorld, string, TimerFact>
{
    private readonly bool _reverseForecast;
    private readonly bool _throwIfPlanCalled;

    public TimerRule(bool reverseForecast = false, bool throwIfPlanCalled = false)
    {
        _reverseForecast = reverseForecast;
        _throwIfPlanCalled = throwIfPlanCalled;
    }

    public int ForecastCallCount { get; private set; }

    public int PlanCallCount { get; private set; }

    public IReadOnlyList<OccurrenceCandidate<string>> Forecast(
        TimerWorld world,
        SimulationRules rules)
    {
        ForecastCallCount++;
        IEnumerable<TimerEntity> timers = world.Timers
            .Where(timer => !world.FiredTimers.Contains(timer.Name));
        if (_reverseForecast)
        {
            timers = timers.Reverse();
        }

        return
        [
            .. timers.Select(timer => new OccurrenceCandidate<string>(
                CandidateKey.FromUtf8(
                    "timer:" + timer.Id.ToString(CultureInfo.InvariantCulture)),
                new CandidateDue(timer.Due),
                timer.Name)),
        ];
    }

    public ValueTask<TransitionDraft<TimerFact>> PlanSelectedAsync(
        TimerWorld world,
        OccurrenceCandidate<string> winner,
        CancellationToken cancellationToken)
    {
        PlanCallCount++;
        if (_throwIfPlanCalled)
        {
            throw new InvalidOperationException("Conformance and Replay must not call Plan.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            new TransitionDraft<TimerFact>([new TimerFact(winner.Data)]));
    }
}

internal static class TimerModel
{
    public static TimerWorld Fold(
        TimerWorld world,
        LogicalInstant instant,
        TimerFact fact) =>
        world with { FiredTimers = [.. world.FiredTimers, fact.TimerName] };

    public static void Validate(TimerWorld world)
    {
        if (world.FiredTimers.Count != world.FiredTimers.Distinct(StringComparer.Ordinal).Count())
        {
            throw new InvalidOperationException("A timer cannot fire twice.");
        }

        if (world.FiredTimers.Any(
            fired => !world.Timers.Any(timer => string.Equals(timer.Name, fired, StringComparison.Ordinal))))
        {
            throw new InvalidOperationException("An unknown timer was fired.");
        }
    }

    public static SimulationKernel<TimerWorld, string, TimerFact> CreateKernel(
        TimerWorld world,
        TimerRule rule,
        InMemoryJournal<TimerFact> journal,
        SimulationRules? simulationRules = null,
        WorldVersion? version = null,
        LogicalInstant? lastCommittedInstant = null,
        long lineageId = 1) =>
        new(
            world,
            version ?? new WorldVersion(lineageId, journal.Batches.Count),
            ModelTime.Zero,
            lastCommittedInstant,
            simulationRules ?? new SimulationRules(worldSeed: 42, maxTransitionsPerModelTime: 100),
            [rule],
            journal,
            Fold,
            Validate);
}
