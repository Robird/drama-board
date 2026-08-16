using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.ToyModels;

internal enum EntityDirective
{
    Kill,
    Spawn,
    Act,
}

internal sealed record LifecycleEntity(
    long Id,
    long Generation,
    ModelTime Due,
    EntityDirective Directive,
    long? TargetId = null);

internal sealed record EntityLifecycleWorld(
    long NextEntityId,
    IReadOnlyList<LifecycleEntity> Entities,
    IReadOnlyList<long> ActedEntityIds)
{
    public static EntityLifecycleWorld Start(
        long nextEntityId,
        params LifecycleEntity[] entities) =>
        new(nextEntityId, Array.AsReadOnly(entities.OrderBy(entity => entity.Id).ToArray()), []);
}

internal sealed record EntityLifecycleForecast(
    long EntityId,
    long Generation,
    EntityDirective Directive,
    long? TargetId);

internal abstract record EntityLifecycleEvent;

internal sealed record EntityKilledEvent(
    long KillerId,
    long VictimId,
    ModelTime SpawnAt) : EntityLifecycleEvent;

internal sealed record EntitySpawnedEvent(
    long SpawnerId,
    long EntityId,
    ModelTime FirstActionAt,
    ModelTime SpawnerNextActionAt) : EntityLifecycleEvent;

internal sealed record EntityActedEvent(
    long EntityId,
    ModelTime NextActionAt) : EntityLifecycleEvent;

internal sealed record ExternalEntitySpawnRequestedEvent(
    ModelTime FirstActionAt) : EntityLifecycleEvent;

internal static class EntityLifecycleEventKinds
{
    public static readonly EventKind Killed = new("entity.killed", 1);
    public static readonly EventKind Spawned = new("entity.spawned", 1);
    public static readonly EventKind Acted = new("entity.acted", 1);
    public static readonly EventKind ExternalSpawnRequested = new("entity.external-spawn-requested", 1);
}

internal sealed class EntityLifecycleSystem :
    ISimSystem<EntityLifecycleWorld, EntityLifecycleForecast, EntityLifecycleEvent>
{
    public IReadOnlyList<EventCandidate<EntityLifecycleForecast>> ForecastNext(
        EntityLifecycleWorld world,
        ModelTime now) =>
        [
            .. world.Entities.Select(entity =>
                new EventCandidate<EntityLifecycleForecast>(
                    new EventCandidateId(entity.Generation),
                    entity.Due,
                    entity.Id,
                    new EntityLifecycleForecast(
                        entity.Id,
                        entity.Generation,
                        entity.Directive,
                        entity.TargetId))),
        ];

    public IReadOnlyList<UncommittedDomainEvent<EntityLifecycleEvent>> Resolve(
        EntityLifecycleWorld world,
        EventCandidate<EntityLifecycleForecast> candidate)
    {
        LifecycleEntity entity = world.Entities.Single(current => current.Id == candidate.SourceId);
        if (candidate.Payload.EntityId != entity.Id ||
            candidate.Payload.Generation != entity.Generation ||
            candidate.Due != entity.Due ||
            candidate.Payload.Directive != entity.Directive ||
            candidate.Payload.TargetId != entity.TargetId)
        {
            throw new InvalidOperationException("The lifecycle candidate is stale for the current entity.");
        }

        return entity.Directive switch
        {
            EntityDirective.Kill when entity.TargetId is long victimId =>
                Result(
                    EntityLifecycleEventKinds.Killed,
                    new EntityKilledEvent(
                        entity.Id,
                        victimId,
                        entity.Due + ModelDuration.FromMilliseconds(10))),
            EntityDirective.Spawn =>
                Result(
                    EntityLifecycleEventKinds.Spawned,
                    new EntitySpawnedEvent(
                        entity.Id,
                        world.NextEntityId,
                        entity.Due + ModelDuration.FromMilliseconds(5),
                        entity.Due + ModelDuration.FromMilliseconds(100))),
            EntityDirective.Act =>
                Result(
                    EntityLifecycleEventKinds.Acted,
                    new EntityActedEvent(
                        entity.Id,
                        entity.Due + ModelDuration.FromMilliseconds(100))),
            _ => throw new InvalidOperationException("The lifecycle directive is invalid."),
        };
    }

    private static IReadOnlyList<UncommittedDomainEvent<EntityLifecycleEvent>> Result(
        EventKind kind,
        EntityLifecycleEvent payload) =>
        [new UncommittedDomainEvent<EntityLifecycleEvent>(kind, payload)];
}

internal sealed class EntityLifecycleReducer :
    IEventReducer<EntityLifecycleWorld, EntityLifecycleEvent>
{
    public EntityLifecycleWorld Apply(
        EntityLifecycleWorld world,
        DomainEvent<EntityLifecycleEvent> domainEvent) =>
        (domainEvent.Kind.Id, domainEvent.Payload) switch
        {
            ({ } kindId, EntityKilledEvent killed)
                when kindId == EntityLifecycleEventKinds.Killed.Id =>
                world with
                {
                    Entities = Array.AsReadOnly(
                        world.Entities
                            .Where(entity => entity.Id != killed.VictimId)
                            .Select(entity => entity.Id == killed.KillerId
                                ? entity with
                                {
                                    Generation = checked(entity.Generation + 1),
                                    Due = killed.SpawnAt,
                                    Directive = EntityDirective.Spawn,
                                    TargetId = null,
                                }
                                : entity)
                            .ToArray()),
                },
            ({ } kindId, EntitySpawnedEvent spawned)
                when kindId == EntityLifecycleEventKinds.Spawned.Id && spawned.EntityId == world.NextEntityId =>
                world with
                {
                    NextEntityId = checked(world.NextEntityId + 1),
                    Entities = Array.AsReadOnly(
                        world.Entities
                            .Select(entity => entity.Id == spawned.SpawnerId
                                ? entity with
                                {
                                    Generation = checked(entity.Generation + 1),
                                    Due = spawned.SpawnerNextActionAt,
                                    Directive = EntityDirective.Act,
                                }
                                : entity)
                            .Append(new LifecycleEntity(
                                spawned.EntityId,
                                0,
                                spawned.FirstActionAt,
                                EntityDirective.Act))
                            .OrderBy(entity => entity.Id)
                            .ToArray()),
                },
            ({ } kindId, EntityActedEvent acted)
                when kindId == EntityLifecycleEventKinds.Acted.Id =>
                world with
                {
                    Entities = Array.AsReadOnly(
                        world.Entities.Select(entity => entity.Id == acted.EntityId
                            ? entity with
                            {
                                Generation = checked(entity.Generation + 1),
                                Due = acted.NextActionAt,
                            }
                            : entity).ToArray()),
                    ActedEntityIds = [.. world.ActedEntityIds, acted.EntityId],
                },
            ({ } kindId, ExternalEntitySpawnRequestedEvent requested)
                when kindId == EntityLifecycleEventKinds.ExternalSpawnRequested.Id =>
                world with
                {
                    NextEntityId = checked(world.NextEntityId + 1),
                    Entities = Array.AsReadOnly(
                        world.Entities
                            .Append(new LifecycleEntity(
                                world.NextEntityId,
                                0,
                                requested.FirstActionAt,
                                EntityDirective.Act))
                            .OrderBy(entity => entity.Id)
                            .ToArray()),
                },
            _ => throw new InvalidOperationException(
                $"Unknown or out-of-sequence event kind '{domainEvent.Kind.Id}'."),
        };
}