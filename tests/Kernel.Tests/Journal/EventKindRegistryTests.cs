using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Tests.ToyModels;

namespace DramaBoard.Kernel.Tests.Journal;

public sealed class EventKindRegistryTests
{
    [Fact]
    public void Register_DuplicateIdAcrossVersionsAndPayloadTypes_ThrowsInvalidOperationException()
    {
        var registry = new EventKindRegistry();
        registry.Register<string>(new EventKind("test.event", 1));

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            registry.Register<int>(new EventKind("test.event", 2)));

        Assert.Contains("test.event", exception.Message);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Register_AllToyModelEventKinds_HaveUniqueIds()
    {
        var registry = new EventKindRegistry();

        registry.Register<string>(TimerEventKinds.Fired);
        registry.Register<ArrivedEventPayload>(RerouteEventKinds.Arrived);
        registry.Register<ReroutedEventPayload>(RerouteEventKinds.Rerouted);
        registry.Register<CollisionEventPayload>(BouncingEventKinds.Collision);
        registry.Register<MiningDiscovery>(MiningEventKinds.MineralDiscovered);
        registry.Register<MiningStartedEvent>(InterruptedMiningEventKinds.MiningStarted);
        registry.Register<MiningCompletedEvent>(InterruptedMiningEventKinds.MiningCompleted);
        registry.Register<MiningInterruptedEvent>(InterruptedMiningEventKinds.MiningInterrupted);
        registry.Register<MineralDiscoveredEvent>(InterruptedMiningEventKinds.MineralDiscovered);
        registry.Register<AliceArrivedEvent>(InterruptedMiningEventKinds.AliceArrived);

        Assert.Equal(10, registry.Count);
    }
}