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
        TimerWorld initialWorld = TimerWorld.Start(
            new TimerEntity(1, "A", ModelTime.Zero + ModelDuration.FromSeconds(10)),
            new TimerEntity(2, "B", ModelTime.Zero + ModelDuration.FromSeconds(20)),
            new TimerEntity(3, "C", ModelTime.Zero + ModelDuration.FromSeconds(15)));
        TimerSystem[] systems = [new()];
        var reducer = new TimerReducer();
        var loop = new SimulationLoop<TimerWorld, string, string>(systems, reducer);
        var journal = new InMemoryJournal<string>();

        SimulationRunResult<TimerWorld, string> result = loop.Run(
            initialWorld,
            SimulationCursor.CreateInitial(lineageId: 1, ModelTime.Zero),
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
                completionDuration: CompletionDuration,
                meanDiscoveryInterval: ModelDuration.FromSeconds(2 * 60)),
            new AliceArrivalSystem(),
        };
        var reducer = new InterruptedMiningReducer();
        var loop = new SimulationLoop<
            InterruptedMiningWorld,
            InterruptedMiningForecast,
            InterruptedMiningEvent>(systems, reducer);
        var journal = new InMemoryJournal<InterruptedMiningEvent>();

        ModelTime arrivalAt = ModelTime.Zero + ModelDuration.FromSeconds(17 * 60);
        SimulationRunResult<InterruptedMiningWorld, InterruptedMiningEvent> result = loop.Run(
            initialWorld,
            SimulationCursor.CreateInitial(lineageId: 1, ModelTime.Zero),
            ModelTime.Zero + ModelDuration.FromSeconds(3 * 60 * 60),
            journal,
            [
                new UncommittedDomainEvent<InterruptedMiningEvent>(
                    InterruptedMiningEventKinds.AliceArrivalScheduled,
                    new AliceArrivalScheduledEvent(arrivalAt)),
            ]);

        InterruptedMiningWorld replayed = ReplayHarness.Replay(initialWorld, journal.Events, reducer);

        Assert.Equal(result.World, replayed);
    }
}
