using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.ToyModels;

public sealed class ForkHarnessTests
{
    private static readonly ModelDuration CompletionDuration = ModelDuration.FromSeconds(2 * 60 * 60);
    private static readonly ModelDuration DiscoveryInterval = ModelDuration.FromSeconds(2 * 60);
    private static readonly ModelTime ArrivalAt = ModelTime.Zero + ModelDuration.FromSeconds(17 * 60);
    private static readonly ModelTime Until = ModelTime.Zero + ModelDuration.FromSeconds(3 * 60 * 60);

    [Fact]
    public void Fork_BeforeAliceArrives_BranchesShareRandomHistoryAndDivergeDeterministically()
    {
        InterruptedMiningWorld initialWorld = InterruptedMiningWorld.Start(worldSeed: 42);
        InMemoryJournal<InterruptedMiningEvent> original = RunOriginal(initialWorld);
        int eventCount = original.Events.TakeWhile(
            domainEvent => domainEvent.Timestamp.ModelTime < ArrivalAt).Count();

        Assert.Contains(
            original.Events.Take(eventCount),
            domainEvent => domainEvent.Payload is MineralDiscoveredEvent);

        ReplayForkResult<InterruptedMiningWorld, InterruptedMiningEvent> aliceBranch =
            Fork(initialWorld, original, eventCount, includeAlice: true);
        ReplayForkResult<InterruptedMiningWorld, InterruptedMiningEvent> repeatedAliceBranch =
            Fork(initialWorld, original, eventCount, includeAlice: true);
        ReplayForkResult<InterruptedMiningWorld, InterruptedMiningEvent> completionBranch =
            Fork(initialWorld, original, eventCount, includeAlice: false);
        ReplayForkResult<InterruptedMiningWorld, InterruptedMiningEvent> repeatedCompletionBranch =
            Fork(initialWorld, original, eventCount, includeAlice: false);

        Assert.Equal(Snapshot(original), Snapshot(aliceBranch.Journal));
        Assert.Equal(Snapshot(aliceBranch.Journal), Snapshot(repeatedAliceBranch.Journal));
        Assert.Equal(Snapshot(completionBranch.Journal), Snapshot(repeatedCompletionBranch.Journal));
        Assert.Equal(
            Snapshot(original).Take(eventCount),
            Snapshot(completionBranch.Journal).Take(eventCount));

        Assert.IsType<ConversationActivity>(aliceBranch.Result.World.Activity);
        Assert.Contains(
            aliceBranch.Journal.Events.Skip(eventCount),
            domainEvent => domainEvent.Payload is MiningInterruptedEvent);
        Assert.DoesNotContain(
            aliceBranch.Journal.Events,
            domainEvent => domainEvent.Payload is MiningCompletedEvent);

        Assert.IsType<FinishedMiningActivity>(completionBranch.Result.World.Activity);
        Assert.Contains(
            completionBranch.Journal.Events.Skip(eventCount),
            domainEvent => domainEvent.Payload is MiningCompletedEvent completed &&
                completed.CompletedAt == ModelTime.Zero + CompletionDuration);
        Assert.DoesNotContain(
            completionBranch.Journal.Events,
            domainEvent => domainEvent.Payload is AliceArrivedEvent or MiningInterruptedEvent);
    }

    private static InMemoryJournal<InterruptedMiningEvent> RunOriginal(InterruptedMiningWorld initialWorld)
    {
        var loop = new SimulationLoop<
            InterruptedMiningWorld,
            InterruptedMiningForecast,
            InterruptedMiningEvent>(CreateSystems(includeAlice: true));
        var journal = new InMemoryJournal<InterruptedMiningEvent>();
        _ = loop.Run(initialWorld, ModelTime.Zero, Until, journal);
        return journal;
    }

    private static ReplayForkResult<InterruptedMiningWorld, InterruptedMiningEvent> Fork(
        InterruptedMiningWorld initialWorld,
        InMemoryJournal<InterruptedMiningEvent> original,
        int eventCount,
        bool includeAlice) =>
        ReplayHarness.Fork<InterruptedMiningWorld, InterruptedMiningForecast, InterruptedMiningEvent>(
            initialWorld,
            ModelTime.Zero,
            original.Events,
            eventCount,
            ReplayReducerTests.ApplyInterruptedMining,
            CreateSystems(includeAlice),
            Until);

    private static IReadOnlyList<
        ISimSystem<InterruptedMiningWorld, InterruptedMiningForecast, InterruptedMiningEvent>> CreateSystems(
        bool includeAlice)
    {
        var systems = new List<
            ISimSystem<InterruptedMiningWorld, InterruptedMiningForecast, InterruptedMiningEvent>>
        {
            new InterruptedMiningSystem(
                sourceId: 20,
                completionDuration: CompletionDuration,
                activityStreamId: 73,
                meanDiscoveryInterval: DiscoveryInterval),
        };
        if (includeAlice)
        {
            systems.Add(new AliceArrivalSystem(sourceId: 10, ArrivalAt));
        }

        return systems;
    }

    private static (LogicalTimestamp Timestamp, string Kind, InterruptedMiningEvent Payload)[] Snapshot(
        InMemoryJournal<InterruptedMiningEvent> journal) =>
        [
            .. journal.Events.Select(domainEvent =>
                (domainEvent.Timestamp, domainEvent.Kind, domainEvent.Payload)),
        ];
}