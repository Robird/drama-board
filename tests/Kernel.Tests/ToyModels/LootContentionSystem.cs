using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Random;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.ToyModels;

internal sealed record LootActor(
    long Id,
    string Name,
    ModelTime PickupAt,
    bool WantsItem = true);

internal sealed record LootItem(
    long Id,
    long ContentionRound,
    long? OwnerId);

internal sealed record LootWorld(
    ulong WorldSeed,
    LootItem Item,
    IReadOnlyList<LootActor> Actors);

internal sealed record LootPickupForecast(long ItemId);

internal abstract record LootEvent;

internal sealed record LootContentionResolvedEvent(
    long ItemId,
    IReadOnlyList<long> CompetitorIds,
    long WinnerId,
    RandomSampleCoordinates Sample) : LootEvent;

internal sealed record LootTakenEvent(
    long ItemId,
    long ActorId) : LootEvent;

internal static class LootEventKinds
{
    public static readonly EventKind ContentionResolved = new("loot.contention-resolved", 1);
    public static readonly EventKind Taken = new("loot.taken", 1);
}

internal sealed class LootContentionSystem :
    ISimSystem<LootWorld, LootPickupForecast, LootEvent>
{
    public IReadOnlyList<EventCandidate<LootPickupForecast>> ForecastNext(
        LootWorld world,
        ModelTime now) =>
        world.Item.OwnerId is not null
            ? []
            :
            [
                .. world.Actors
                    .Where(actor => actor.WantsItem)
                    .Select(actor => new EventCandidate<LootPickupForecast>(
                        new EventCandidateId(world.Item.ContentionRound),
                        actor.PickupAt,
                        actor.Id,
                        new LootPickupForecast(world.Item.Id))),
            ];

    public IReadOnlyList<UncommittedDomainEvent<LootEvent>> Resolve(
        LootWorld world,
        EventCandidate<LootPickupForecast> candidate)
    {
        if (world.Item.OwnerId is not null || candidate.Payload.ItemId != world.Item.Id)
        {
            throw new InvalidOperationException("The loot candidate is stale for the current item.");
        }

        long[] competitorIds =
        [
            .. world.Actors
                .Where(actor => actor.WantsItem && actor.PickupAt == candidate.Due)
                .Select(actor => actor.Id)
                .OrderBy(actorId => actorId),
        ];
        if (!competitorIds.Contains(candidate.SourceId))
        {
            throw new InvalidOperationException("The resolving actor is not a visible competitor.");
        }

        if (competitorIds.Length == 1)
        {
            return
            [
                new UncommittedDomainEvent<LootEvent>(
                    LootEventKinds.Taken,
                    new LootTakenEvent(world.Item.Id, competitorIds[0])),
            ];
        }

        ulong generation = checked((ulong)world.Item.ContentionRound);
        var sample = new RandomSampleCoordinates(
            DeterministicRandom.DeriveStreamId(world.Item.Id),
            generation,
            0);
        int winnerIndex = DeterministicRandom.SampleInt32(
            world.WorldSeed,
            sample.StreamId,
            sample.Generation,
            minInclusive: 0,
            maxExclusive: competitorIds.Length,
            sampleIndex: sample.SampleIndex);
        var resolved = new LootContentionResolvedEvent(
            world.Item.Id,
            Array.AsReadOnly(competitorIds),
            competitorIds[winnerIndex],
            sample);

        return
        [
            new UncommittedDomainEvent<LootEvent>(
                LootEventKinds.ContentionResolved,
                resolved),
        ];
    }
}

internal sealed class LootReducer : IEventReducer<LootWorld, LootEvent>
{
    public LootWorld Apply(LootWorld world, DomainEvent<LootEvent> domainEvent) =>
        (domainEvent.Kind.Id, domainEvent.Payload) switch
        {
            ({ } kindId, LootContentionResolvedEvent resolved)
                when kindId == LootEventKinds.ContentionResolved.Id &&
                    resolved.ItemId == world.Item.Id &&
                    resolved.CompetitorIds.Contains(resolved.WinnerId) =>
                world with
                {
                    Item = world.Item with
                    {
                        OwnerId = resolved.WinnerId,
                        ContentionRound = checked(world.Item.ContentionRound + 1),
                    },
                },
            ({ } kindId, LootTakenEvent taken)
                when kindId == LootEventKinds.Taken.Id && taken.ItemId == world.Item.Id =>
                world with { Item = world.Item with { OwnerId = taken.ActorId } },
            _ => throw new InvalidOperationException(
                $"Unknown or out-of-sequence event kind '{domainEvent.Kind.Id}'."),
        };
}
