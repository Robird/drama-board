using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.ToyModels;

public sealed class TimerSystemTests
{
    [Fact]
    public void Run_ThreeIndependentTimers_CommitsInTimeOrderWithThreeJumps()
    {
        TimerSystem[] systems =
        [
            new("A", sourceId: 1, new ModelTime(10)),
            new("B", sourceId: 2, new ModelTime(20)),
            new("C", sourceId: 3, new ModelTime(15)),
        ];
        var loop = new SimulationLoop<TimerWorld, string, string>(systems);
        var journal = new InMemoryJournal<string>();

        SimulationRunResult<TimerWorld> result = loop.Run(
            new TimerWorld([]),
            initialTime: new ModelTime(0),
            until: new ModelTime(20),
            journal);

        Assert.Equal(["A", "C", "B"], journal.Events.Select(domainEvent => domainEvent.Payload));
        Assert.Equal([10, 15, 20], journal.Events.Select(domainEvent => domainEvent.Timestamp.ModelTime.Ticks));
        Assert.Equal(3, result.TimeAdvanceCount);
        Assert.Equal(3, result.ResolvedCandidateCount);
        Assert.Equal(["A", "C", "B"], result.World.FiredTimers);
    }
}