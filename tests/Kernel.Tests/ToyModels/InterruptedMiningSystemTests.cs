using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.ToyModels;

public sealed class InterruptedMiningSystemTests
{
    private static readonly ModelDuration CompletionDuration = ModelDuration.FromSeconds(2 * 60 * 60);
    private static readonly ModelDuration ArrivalDelay = ModelDuration.FromSeconds(17 * 60);
    private static readonly ModelTime ArrivalAt = ModelTime.Zero + ArrivalDelay;

    [Fact]
    public void Run_AliceArrivesAtSeventeenMinutes_InterruptsAndMaterializesProgress()
    {
        RunOutput output = Run(includeAlice: true, includeDiscoveries: false);

        Assert.Equal(
            [
                InterruptedMiningEventKinds.AliceArrivalScheduled,
                InterruptedMiningEventKinds.MiningStarted,
                InterruptedMiningEventKinds.AliceArrived,
                InterruptedMiningEventKinds.MiningInterrupted,
            ],
            output.Journal.Events.Select(domainEvent => domainEvent.Kind));
        Assert.Equal(
            [ModelTime.Zero, ModelTime.Zero, ArrivalAt, ArrivalAt],
            output.Journal.Events.Select(domainEvent => domainEvent.Timestamp.ModelTime));
        Assert.Equal(
            [0, 1, 0, 1],
            output.Journal.Events.Select(domainEvent => domainEvent.Timestamp.Microstep.Value));

        MiningInterruptedEvent interrupted = Assert.IsType<MiningInterruptedEvent>(
            output.Journal.Events[3].Payload);
        Assert.Equal(ArrivalDelay, interrupted.Elapsed);
        Assert.Equal(ArrivalDelay.Ticks / (decimal)CompletionDuration.Ticks, interrupted.ProgressFraction);
        Assert.IsType<ConversationActivity>(output.Result.World.Activity);
        Assert.DoesNotContain(
            output.Journal.Events,
            domainEvent => domainEvent.Payload is MiningCompletedEvent);
    }

    [Fact]
    public void Run_AliceInterruptionWithDifferentSystemInsertionOrders_ProducesIdenticalJournal()
    {
        RunOutput first = Run(includeAlice: true, includeDiscoveries: false);
        RunOutput repeated = Run(includeAlice: true, includeDiscoveries: false);
        RunOutput reversed = Run(includeAlice: true, includeDiscoveries: false, reverseSystems: true);

        Assert.Equal(Snapshot(first.Journal), Snapshot(repeated.Journal));
        Assert.Equal(Snapshot(first.Journal), Snapshot(reversed.Journal));
    }

    [Fact]
    public void Run_AliceDoesNotArrive_CompletesMiningAtTwoHours()
    {
        RunOutput output = Run(includeAlice: false, includeDiscoveries: false);

        Assert.Equal(
            [InterruptedMiningEventKinds.MiningStarted, InterruptedMiningEventKinds.MiningCompleted],
            output.Journal.Events.Select(domainEvent => domainEvent.Kind));
        Assert.Equal(
            [ModelTime.Zero, ModelTime.Zero + CompletionDuration],
            output.Journal.Events.Select(domainEvent => domainEvent.Timestamp.ModelTime));
        Assert.IsType<FinishedMiningActivity>(output.Result.World.Activity);
    }

    [Fact]
    public void Run_RandomDiscoveriesBeforeInterruption_AreRetainedAndReproducible()
    {
        RunOutput first = Run(includeAlice: true, includeDiscoveries: true);
        RunOutput second = Run(includeAlice: true, includeDiscoveries: true);

        Assert.Equal(Snapshot(first.Journal), Snapshot(second.Journal));

        MineralDiscoveredEvent[] discoveries =
        [
            .. first.Journal.Events
                .Select(domainEvent => domainEvent.Payload)
                .OfType<MineralDiscoveredEvent>(),
        ];
        Assert.NotEmpty(discoveries);
        Assert.All(discoveries, discovery => Assert.True(discovery.DiscoveredAt < ArrivalAt));

        int interruptionIndex = first.Journal.Events
            .Select((domainEvent, index) => (domainEvent, index))
            .Single(item => item.domainEvent.Payload is MiningInterruptedEvent)
            .index;
        Assert.DoesNotContain(
            first.Journal.Events.Skip(interruptionIndex + 1),
            domainEvent => domainEvent.Payload is MineralDiscoveredEvent);
        Assert.DoesNotContain(
            first.Journal.Events,
            domainEvent => domainEvent.Payload is MiningCompletedEvent);
    }

    private static RunOutput Run(
        bool includeAlice,
        bool includeDiscoveries,
        bool reverseSystems = false)
    {
        var mining = new InterruptedMiningSystem(
            completionDuration: CompletionDuration,
            meanDiscoveryInterval: includeDiscoveries
                ? ModelDuration.FromSeconds(2 * 60)
                : null);
        var systems = new List<
            ISimSystem<InterruptedMiningWorld, InterruptedMiningForecast, InterruptedMiningEvent>>
        {
            mining,
        };

        if (includeAlice)
        {
            systems.Add(new AliceArrivalSystem());
        }

        if (reverseSystems)
        {
            systems.Reverse();
        }

        var loop = new SimulationLoop<
            InterruptedMiningWorld,
            InterruptedMiningForecast,
            InterruptedMiningEvent>(systems, new InterruptedMiningReducer());
        var journal = new InMemoryJournal<InterruptedMiningEvent>();
        UncommittedDomainEvent<InterruptedMiningEvent>[] externalInputs = includeAlice
            ?
            [
                new UncommittedDomainEvent<InterruptedMiningEvent>(
                    InterruptedMiningEventKinds.AliceArrivalScheduled,
                    new AliceArrivalScheduledEvent(ArrivalAt)),
            ]
            : [];
        SimulationRunResult<InterruptedMiningWorld, InterruptedMiningEvent> result = loop.Run(
            InterruptedMiningWorld.Start(worldSeed: 42),
            cursor: SimulationCursor.CreateInitial(lineageId: 1, ModelTime.Zero),
            until: ModelTime.Zero + ModelDuration.FromSeconds(3 * 60 * 60),
            journal,
            externalInputs);

        return new RunOutput(result, journal);
    }

    private static (LogicalTimestamp Timestamp, EventKind Kind, InterruptedMiningEvent Payload)[] Snapshot(
        InMemoryJournal<InterruptedMiningEvent> journal) =>
        [
            .. journal.Events.Select(
                domainEvent => (domainEvent.Timestamp, domainEvent.Kind, domainEvent.Payload)),
        ];

    private sealed record RunOutput(
        SimulationRunResult<InterruptedMiningWorld, InterruptedMiningEvent> Result,
        InMemoryJournal<InterruptedMiningEvent> Journal);
}
