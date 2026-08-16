using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.ToyModels;

public sealed class ReplayReducerTests
{
    private static readonly ModelDuration CompletionDuration = ModelDuration.FromSeconds(2 * 60 * 60);

    [Fact]
    public void Replay_TimerJournal_ReconstructsRunFinalWorld()
    {
        TimerWorld initialWorld = new([]);
        TimerSystem[] systems =
        [
            new("A", sourceId: 1, ModelTime.Zero + ModelDuration.FromSeconds(10)),
            new("B", sourceId: 2, ModelTime.Zero + ModelDuration.FromSeconds(20)),
            new("C", sourceId: 3, ModelTime.Zero + ModelDuration.FromSeconds(15)),
        ];
        var reducer = new TimerReducer();
        var loop = new SimulationLoop<TimerWorld, string, string>(systems, reducer);
        var journal = new InMemoryJournal<string>();

        SimulationRunResult<TimerWorld> result = loop.Run(
            initialWorld,
            ModelTime.Zero,
            ModelTime.Zero + ModelDuration.FromSeconds(20),
            journal);

        TimerWorld replayed = ReplayHarness.Replay(initialWorld, journal.Events, reducer);

        Assert.Equal(result.World.FiredTimers, replayed.FiredTimers);
    }

    [Fact]
    public void Replay_InterruptedMiningJournal_ReconstructsRunFinalWorld()
    {
        InterruptedMiningWorld initialWorld = InterruptedMiningWorld.Start(worldSeed: 42);
        var systems = new ISimSystem<
            InterruptedMiningWorld,
            InterruptedMiningForecast,
            InterruptedMiningEvent>[]
        {
            new InterruptedMiningSystem(
                sourceId: 20,
                completionDuration: CompletionDuration,
                activityStreamId: 73,
                meanDiscoveryInterval: ModelDuration.FromSeconds(2 * 60)),
            new AliceArrivalSystem(
                sourceId: 10,
                arrivalAt: ModelTime.Zero + ModelDuration.FromSeconds(17 * 60)),
        };
        var reducer = new InterruptedMiningReducer();
        var loop = new SimulationLoop<
            InterruptedMiningWorld,
            InterruptedMiningForecast,
            InterruptedMiningEvent>(systems, reducer);
        var journal = new InMemoryJournal<InterruptedMiningEvent>();

        SimulationRunResult<InterruptedMiningWorld> result = loop.Run(
            initialWorld,
            ModelTime.Zero,
            ModelTime.Zero + ModelDuration.FromSeconds(3 * 60 * 60),
            journal);

        InterruptedMiningWorld replayed = ReplayHarness.Replay(initialWorld, journal.Events, reducer);

        Assert.Equal(result.World, replayed);
    }
}
