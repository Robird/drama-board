using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Random;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.ToyModels;

public sealed class LootContentionSystemTests
{
    private const long ItemId = 99;
    private static readonly ModelTime PickupAt = new(10);

    [Fact]
    public void Run_SwappedActorIdsAndRegistrationOrder_ChangesFirstResolverButNotWinner()
    {
        ulong seed = FindSeedWhoseWinnerIs(actorId: 20);
        LootWorld aliceResolvesFirst = CreateWorld(
            seed,
            new LootActor(10, "Alice", PickupAt),
            new LootActor(20, "Bob", PickupAt));
        LootWorld bobResolvesFirst = CreateWorld(
            seed,
            new LootActor(20, "Alice", PickupAt),
            new LootActor(10, "Bob", PickupAt));
        LootWorld registrationReversed = CreateWorld(
            seed,
            new LootActor(20, "Bob", PickupAt),
            new LootActor(10, "Alice", PickupAt));
        var system = new LootContentionSystem();

        string firstResolverName = FirstResolverName(system, aliceResolvesFirst);
        string swappedFirstResolverName = FirstResolverName(system, bobResolvesFirst);
        RunOutput first = Run(aliceResolvesFirst);
        RunOutput swapped = Run(bobResolvesFirst);
        RunOutput reversed = Run(registrationReversed);
        LootContentionResolvedEvent firstResolution = Assert.IsType<LootContentionResolvedEvent>(
            Assert.Single(first.Journal.Events).Payload);
        LootContentionResolvedEvent swappedResolution = Assert.IsType<LootContentionResolvedEvent>(
            Assert.Single(swapped.Journal.Events).Payload);
        LootContentionResolvedEvent reversedResolution = Assert.IsType<LootContentionResolvedEvent>(
            Assert.Single(reversed.Journal.Events).Payload);

        Assert.Equal("Alice", firstResolverName);
        Assert.Equal("Bob", swappedFirstResolverName);
        Assert.Equal([10L, 20L], firstResolution.CompetitorIds);
        Assert.Equal(firstResolution.WinnerId, swappedResolution.WinnerId);
        Assert.Equal(firstResolution.WinnerId, reversedResolution.WinnerId);
        Assert.Equal(20, firstResolution.WinnerId);
        Assert.Equal(firstResolution.Sample, swappedResolution.Sample);
        Assert.Equal(LootEventKinds.ContentionResolved, first.Journal.Events[0].Kind);
        Assert.Equal(PickupAt, first.Journal.Events[0].Timestamp.ModelTime);
        Assert.Equal(1, first.Result.ResolvedCandidateCount);
        Assert.Equal(1, first.Result.World.Item.ContentionRound);
        Assert.Equal(firstResolution.WinnerId, first.Result.World.Item.OwnerId);
        Assert.DoesNotContain(first.Journal.Events, domainEvent => domainEvent.Payload is LootTakenEvent);

        int auditedWinnerIndex = DeterministicRandom.SampleInt32(
            first.Result.World.WorldSeed,
            firstResolution.Sample.StreamId,
            firstResolution.Sample.Generation,
            0,
            firstResolution.CompetitorIds.Count,
            firstResolution.Sample.SampleIndex);
        Assert.Equal(
            firstResolution.CompetitorIds[auditedWinnerIndex],
            firstResolution.WinnerId);
        Assert.Equal(DeterministicRandom.DeriveStreamId(ItemId), firstResolution.Sample.StreamId);
        Assert.Equal(0UL, firstResolution.Sample.Generation);
        Assert.Equal(0UL, firstResolution.Sample.SampleIndex);

        LootWorld replayed = ReplayHarness.Replay(
            aliceResolvesFirst,
            first.Journal.Events,
            new LootReducer());
        Assert.Equal(first.Result.World.Item, replayed.Item);
        Assert.Equal(first.Result.World.Actors, replayed.Actors);
    }

    [Fact]
    public void Run_DifferentWorldSeeds_CanSelectEitherCompetitor()
    {
        long[] winners =
        [
            .. Enumerable.Range(0, 64)
                .Select(seed => Assert.IsType<LootContentionResolvedEvent>(
                    Assert.Single(Run(CreateWorld(
                        checked((ulong)seed),
                        new LootActor(10, "Alice", PickupAt),
                        new LootActor(20, "Bob", PickupAt))).Journal.Events).Payload).WinnerId)
                .Distinct()
                .OrderBy(winnerId => winnerId),
        ];

        Assert.Equal([10L, 20L], winners);
    }

    [Fact]
    public void Run_SingleCompetitor_TakesItemWithoutRandomArbitration()
    {
        LootWorld initialWorld = CreateWorld(
            worldSeed: 42,
            new LootActor(10, "Alice", PickupAt));

        RunOutput output = Run(initialWorld);

        LootTakenEvent taken = Assert.IsType<LootTakenEvent>(Assert.Single(output.Journal.Events).Payload);
        Assert.Equal(LootEventKinds.Taken, output.Journal.Events[0].Kind);
        Assert.Equal(10, taken.ActorId);
        Assert.Equal(10, output.Result.World.Item.OwnerId);
        Assert.Equal(0, output.Result.World.Item.ContentionRound);
    }

    private static ulong FindSeedWhoseWinnerIs(long actorId)
    {
        ulong streamId = DeterministicRandom.DeriveStreamId(ItemId);
        return checked((ulong)Enumerable.Range(0, 64).First(seed =>
            (DeterministicRandom.SampleInt32(
                checked((ulong)seed),
                streamId,
                generation: 0,
                minInclusive: 0,
                maxExclusive: 2) == 0 ? 10 : 20) == actorId));
    }

    private static string FirstResolverName(LootContentionSystem system, LootWorld world)
    {
        EventCandidate<LootPickupForecast> first = system.ForecastNext(world, ModelTime.Zero)
            .OrderBy(candidate => candidate.Due)
            .ThenBy(candidate => candidate.SourceId)
            .ThenBy(candidate => candidate.Id)
            .First();
        return world.Actors.Single(actor => actor.Id == first.SourceId).Name;
    }

    private static LootWorld CreateWorld(ulong worldSeed, params LootActor[] actors) =>
        new(
            worldSeed,
            new LootItem(ItemId, ContentionRound: 0, OwnerId: null),
            Array.AsReadOnly(actors));

    private static RunOutput Run(LootWorld initialWorld)
    {
        var reducer = new LootReducer();
        var loop = new SimulationLoop<LootWorld, LootPickupForecast, LootEvent>(
            [new LootContentionSystem()],
            reducer);
        var journal = new InMemoryJournal<LootEvent>();
        SimulationRunResult<LootWorld, LootEvent> result = loop.Run(
            initialWorld,
            SimulationCursor.CreateInitial(lineageId: 1, ModelTime.Zero),
            PickupAt,
            journal);
        return new RunOutput(result, journal);
    }

    private sealed record RunOutput(
        SimulationRunResult<LootWorld, LootEvent> Result,
        InMemoryJournal<LootEvent> Journal);
}