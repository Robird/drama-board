using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.ToyModels;

public sealed class TimerSystemTests
{
    [Fact]
    public async Task StepAsync_ThreeTimersCommitOneBatchPerStepInTimeOrder()
    {
        TimerWorld world = TimerWorld.Start(
            new TimerEntity(1, "A", AtSecond(10)),
            new TimerEntity(2, "B", AtSecond(20)),
            new TimerEntity(3, "C", AtSecond(15)));
        var rule = new TimerRule();
        var journal = new InMemoryJournal<TimerFact>(lineageId: 1);
        SimulationKernel<TimerWorld, string, TimerFact> kernel =
            TimerModel.CreateKernel(world, rule, journal);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(AtSecond(20)));
        Assert.Single(journal.Batches);
        Assert.Equal(["A"], kernel.World.FiredTimers);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(AtSecond(20)));
        Assert.Equal(2, journal.Batches.Count);
        Assert.Equal(["A", "C"], kernel.World.FiredTimers);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(AtSecond(20)));
        Assert.Equal(StepStatus.Exhausted, await kernel.StepAsync(AtSecond(20)));

        Assert.Equal(["A", "C", "B"], kernel.World.FiredTimers);
        Assert.Equal([10_000L, 15_000L, 20_000L],
            journal.Batches.Select(batch => batch.Instant.ModelTime.Ticks));
        Assert.Equal(3, kernel.Version.TransitionCount);
        Assert.Equal(4, rule.ForecastCallCount);
        Assert.Equal(3, rule.PlanCallCount);
    }

    [Fact]
    public async Task StepAsync_SameTickTimersUseSchedulerAndReforecastAfterEachCommit()
    {
        TimerWorld world = TimerWorld.Start(
            new TimerEntity(1, "A", AtSecond(10)),
            new TimerEntity(2, "B", AtSecond(10)));
        var rule = new TimerRule(reverseForecast: true);
        var journal = new InMemoryJournal<TimerFact>(lineageId: 1);
        SimulationKernel<TimerWorld, string, TimerFact> kernel =
            TimerModel.CreateKernel(world, rule, journal);

        await kernel.StepAsync(AtSecond(10));
        await kernel.StepAsync(AtSecond(10));

        Assert.Equal(2, rule.ForecastCallCount);
        Assert.Equal([0L, 1L], journal.Batches.Select(batch => batch.Instant.CausalOrdinal));
        Assert.Equal(kernel.World.FiredTimers, journal.Batches.Select(batch => batch.Facts[0].TimerName));
    }

    private static ModelTime AtSecond(long seconds) =>
        ModelTime.Zero + ModelDuration.FromSeconds(seconds);
}
