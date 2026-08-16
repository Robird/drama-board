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
        var loop = new SimulationLoop<TimerWorld, string, string>(systems);
        var journal = new InMemoryJournal<string>();

        SimulationRunResult<TimerWorld> result = loop.Run(
            initialWorld,
            ModelTime.Zero,
            ModelTime.Zero + ModelDuration.FromSeconds(20),
            journal);

        TimerWorld replayed = ReplayHarness.Replay(
            initialWorld,
            journal.Events,
            static (world, domainEvent) => domainEvent.Kind == "TimerFired"
                ? new TimerWorld([.. world.FiredTimers, domainEvent.Payload])
                : throw new InvalidOperationException($"Unknown event kind '{domainEvent.Kind}'."));

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
        var loop = new SimulationLoop<
            InterruptedMiningWorld,
            InterruptedMiningForecast,
            InterruptedMiningEvent>(systems);
        var journal = new InMemoryJournal<InterruptedMiningEvent>();

        SimulationRunResult<InterruptedMiningWorld> result = loop.Run(
            initialWorld,
            ModelTime.Zero,
            ModelTime.Zero + ModelDuration.FromSeconds(3 * 60 * 60),
            journal);

        InterruptedMiningWorld replayed = ReplayHarness.Replay(
            initialWorld,
            journal.Events,
            ApplyInterruptedMining);

        Assert.Equal(result.World, replayed);
    }

    internal static InterruptedMiningWorld ApplyInterruptedMining(
        InterruptedMiningWorld world,
        DomainEvent<InterruptedMiningEvent> domainEvent) =>
        domainEvent.Payload switch
        {
            MiningStartedEvent started => world with
            {
                Activity = new MiningActivity(started.StartedAt),
                LastDiscoveryAt = started.StartedAt,
            },
            MiningCompletedEvent completed => world with
            {
                Activity = new FinishedMiningActivity(completed.CompletedAt),
            },
            MiningInterruptedEvent interrupted => world with
            {
                Activity = new ConversationActivity(interrupted.InterruptedAt),
            },
            MineralDiscoveredEvent discovered => world with
            {
                DiscoveryGeneration = checked(world.DiscoveryGeneration + 1),
                LastDiscoveryAt = discovered.DiscoveredAt,
            },
            AliceArrivedEvent => world with { AliceAtMine = true },
            _ => throw new InvalidOperationException($"Unknown event kind '{domainEvent.Kind}'."),
        };
}