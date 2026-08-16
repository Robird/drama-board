using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.ToyModels;

public sealed class EntityLifecycleSystemTests
{
    [Fact]
    public void Run_DeathRemovesFutureAndSpawnedEntityBeginsForecasting_ReplaysExactly()
    {
        EntityLifecycleWorld initialWorld = EntityLifecycleWorld.Start(
            nextEntityId: 30,
            new LifecycleEntity(10, 0, new ModelTime(10), EntityDirective.Kill, TargetId: 20),
            new LifecycleEntity(20, 0, new ModelTime(30), EntityDirective.Act));

        RunOutput first = Run(initialWorld, new ModelTime(25));
        RunOutput second = Run(initialWorld, new ModelTime(25));

        Assert.Equal(
            [
                EntityLifecycleEventKinds.Killed,
                EntityLifecycleEventKinds.Spawned,
                EntityLifecycleEventKinds.Acted,
            ],
            first.Journal.Events.Select(domainEvent => domainEvent.Kind));
        Assert.Equal(
            [new ModelTime(10), new ModelTime(20), new ModelTime(25)],
            first.Journal.Events.Select(domainEvent => domainEvent.Timestamp.ModelTime));
        EntityKilledEvent killed = Assert.IsType<EntityKilledEvent>(first.Journal.Events[0].Payload);
        EntitySpawnedEvent spawned = Assert.IsType<EntitySpawnedEvent>(first.Journal.Events[1].Payload);
        EntityActedEvent acted = Assert.IsType<EntityActedEvent>(first.Journal.Events[2].Payload);
        Assert.Equal(20, killed.VictimId);
        Assert.Equal(30, spawned.EntityId);
        Assert.Equal(30, acted.EntityId);
        Assert.DoesNotContain(
            first.Journal.Events,
            domainEvent => domainEvent.Payload is EntityActedEvent { EntityId: 20 });
        Assert.Equal([10L, 30L], first.Result.World.Entities.Select(entity => entity.Id));
        Assert.Equal([30L], first.Result.World.ActedEntityIds);
        Assert.Equal(Snapshot(first.Journal), Snapshot(second.Journal));

        EntityLifecycleWorld replayed = ReplayHarness.Replay(
            initialWorld,
            first.Journal.Events,
            new EntityLifecycleReducer());
        AssertWorldEqual(first.Result.World, replayed);
    }

    [Fact]
    public void Run_ExternalInputSpawnsEntity_WhichForecastsAndReplaysExactly()
    {
        EntityLifecycleWorld initialWorld = EntityLifecycleWorld.Start(nextEntityId: 40);
        var externalSpawn = new UncommittedDomainEvent<EntityLifecycleEvent>(
            EntityLifecycleEventKinds.ExternalSpawnRequested,
            new ExternalEntitySpawnRequestedEvent(new ModelTime(5)));

        RunOutput output = Run(initialWorld, new ModelTime(5), [externalSpawn]);

        Assert.Equal(
            [EntityLifecycleEventKinds.ExternalSpawnRequested, EntityLifecycleEventKinds.Acted],
            output.Journal.Events.Select(domainEvent => domainEvent.Kind));
        Assert.Equal(
            [ModelTime.Zero, new ModelTime(5)],
            output.Journal.Events.Select(domainEvent => domainEvent.Timestamp.ModelTime));
        Assert.Equal(40, Assert.IsType<EntityActedEvent>(output.Journal.Events[1].Payload).EntityId);
        Assert.Equal(41, output.Result.World.NextEntityId);
        Assert.Equal([40L], output.Result.World.Entities.Select(entity => entity.Id));

        EntityLifecycleWorld replayed = ReplayHarness.Replay(
            initialWorld,
            output.Journal.Events,
            new EntityLifecycleReducer());
        AssertWorldEqual(output.Result.World, replayed);
    }

    private static RunOutput Run(
        EntityLifecycleWorld initialWorld,
        ModelTime until,
        IReadOnlyList<UncommittedDomainEvent<EntityLifecycleEvent>>? externalInputs = null)
    {
        var reducer = new EntityLifecycleReducer();
        var loop = new SimulationLoop<
            EntityLifecycleWorld,
            EntityLifecycleForecast,
            EntityLifecycleEvent>([new EntityLifecycleSystem()], reducer);
        var journal = new InMemoryJournal<EntityLifecycleEvent>();
        SimulationRunResult<EntityLifecycleWorld, EntityLifecycleEvent> result = loop.Run(
            initialWorld,
            SimulationCursor.CreateInitial(lineageId: 1, ModelTime.Zero),
            until,
            journal,
            externalInputs);
        return new RunOutput(result, journal);
    }

    private static void AssertWorldEqual(EntityLifecycleWorld expected, EntityLifecycleWorld actual)
    {
        Assert.Equal(expected.NextEntityId, actual.NextEntityId);
        Assert.Equal(expected.Entities, actual.Entities);
        Assert.Equal(expected.ActedEntityIds, actual.ActedEntityIds);
    }

    private static (LogicalTimestamp Timestamp, EventKind Kind, EntityLifecycleEvent Payload)[] Snapshot(
        InMemoryJournal<EntityLifecycleEvent> journal) =>
        [
            .. journal.Events.Select(domainEvent =>
                (domainEvent.Timestamp, domainEvent.Kind, domainEvent.Payload)),
        ];

    private sealed record RunOutput(
        SimulationRunResult<EntityLifecycleWorld, EntityLifecycleEvent> Result,
        InMemoryJournal<EntityLifecycleEvent> Journal);
}