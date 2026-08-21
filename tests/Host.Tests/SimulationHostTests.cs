using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Host.Tests;

public sealed class SimulationHostTests
{
    [Fact]
    public async Task RunUntilAsync_RepeatsPublicStepsUntilExhausted()
    {
        SimulationKernel<CounterWorld, int, int> kernel = CreateKernel(limit: 2);

        HostRunResult<CounterWorld> result = await SimulationHost.RunUntilAsync(
            kernel,
            new ModelTime(10));

        Assert.Equal(StepStatus.Exhausted, result.Status);
        Assert.Equal(2, result.World.Count);
        Assert.Equal(2, result.Version.TransitionCount);
        Assert.Equal(2, result.CommittedTransitionCount);
    }

    [Fact]
    public async Task RunUntilAsync_DoesNotHideBoundaryAsTimeAdvance()
    {
        SimulationKernel<CounterWorld, int, int> kernel = CreateKernel(limit: 1, firstDue: 10);

        HostRunResult<CounterWorld> result = await SimulationHost.RunUntilAsync(
            kernel,
            new ModelTime(9));

        Assert.Equal(StepStatus.BoundaryReached, result.Status);
        Assert.Equal(ModelTime.Zero, result.CurrentModelTime);
        Assert.Equal(0, result.CommittedTransitionCount);
    }

    private static SimulationKernel<CounterWorld, int, int> CreateKernel(
        int limit,
        long firstDue = 0)
    {
        var journal = new InMemoryJournal<int>(lineageId: 1);
        return new SimulationKernel<CounterWorld, int, int>(
            new CounterWorld(0),
            new WorldVersion(1, 0),
            ModelTime.Zero,
            lastCommittedInstant: null,
            new SimulationRules(42, 100),
            [new CounterRule(limit, firstDue)],
            journal,
            (world, _, fact) => new CounterWorld(checked(world.Count + fact)),
            _ => { });
    }

    private sealed record CounterWorld(int Count);

    private sealed class CounterRule : IOccurrenceRule<CounterWorld, int, int>
    {
        private readonly int _limit;
        private readonly long _firstDue;

        public CounterRule(int limit, long firstDue)
        {
            _limit = limit;
            _firstDue = firstDue;
        }

        public IReadOnlyList<OccurrenceCandidate<int>> Forecast(
            CounterWorld world,
            SimulationRules rules) =>
            world.Count >= _limit
                ? []
                :
                [
                    new OccurrenceCandidate<int>(
                        CandidateKey.FromUtf8($"counter/{world.Count}"),
                        new CandidateDue(new ModelTime(_firstDue + world.Count)),
                        world.Count),
                ];

        public ValueTask<TransitionDraft<int>> PlanSelectedAsync(
            CounterWorld world,
            OccurrenceCandidate<int> winner,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new TransitionDraft<int>([1]));
        }
    }
}
