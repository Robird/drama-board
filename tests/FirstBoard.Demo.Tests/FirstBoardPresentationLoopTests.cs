using System.Threading.Channels;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.FirstBoard.Demo.Tests;

public sealed class FirstBoardPresentationLoopTests
{
    private const ulong Seed = 20_260_824;

    [Fact]
    public async Task PresentedFrontierWaitsForTheLastVisibleCueAndPacing()
    {
        PresentationHarness harness = CreateHarness(PresentationMode.Player, BoardIds.Alice);
        FirstBoardFact[] facts =
        [
            new GameBoardFact(new ActorWaitStartedEvent(BoardIds.Alice, new ModelTime(10))),
        ];
        CommittedTransition transition = Transition(
            transitionCount: 1,
            new LogicalInstant(ModelTime.Zero, 0),
            facts);
        var pacer = new ManualPresentationPacer();
        FirstBoardPresentationLoop loop = harness.CreateLoop(pacer);
        harness.Publish(transition);
        harness.Channel.Writer.TryComplete();

        Task run = loop.RunAsync(harness.Channel.Reader, CancellationToken.None);
        await harness.Terminal.WaitForCueCountAsync(1);
        await pacer.WaitForRequestCountAsync(1);

        Assert.Equal(0, harness.Coordination.Snapshot().Presented.TransitionCount);
        pacer.ReleaseNext();
        await run;

        Assert.Equal(transition.Version, harness.Coordination.Snapshot().Presented);
        Assert.Equal(
            ExpectedWorld(harness, transition),
            FirstBoardScenario.WorldSnapshot(loop.ReplayWorld));
    }

    [Fact]
    public async Task HiddenBatchFoldsAndAdvancesWithoutArtificialDelay()
    {
        PresentationHarness harness = CreateHarness(PresentationMode.Player, BoardIds.Alice);
        const string secret = "BOB-ONLY-SECRET";
        CommittedTransition transition = Transition(
            transitionCount: 1,
            new LogicalInstant(ModelTime.Zero, 0),
            [
                new GameBoardFact(new ActorObservedEvent(
                    BoardIds.Bob,
                    [new BoardFact("private", null, secret)])),
            ]);
        var pacer = new ManualPresentationPacer();
        FirstBoardPresentationLoop loop = harness.CreateLoop(pacer);
        harness.Publish(transition);
        harness.Channel.Writer.TryComplete();

        await loop.RunAsync(harness.Channel.Reader, CancellationToken.None);

        Assert.Empty(harness.Terminal.Cues);
        Assert.Equal(transition.Version, harness.Coordination.Snapshot().Presented);
        Assert.Equal(
            ExpectedWorld(harness, transition),
            FirstBoardScenario.WorldSnapshot(loop.ReplayWorld));
    }

    [Fact]
    public async Task SameTimeBatchesRemainSeparateAndOrderedByTransitionAndOrdinal()
    {
        PresentationHarness harness = CreateHarness(PresentationMode.Player, BoardIds.Alice);
        CommittedTransition first = Transition(
            transitionCount: 1,
            new LogicalInstant(ModelTime.Zero, 0),
            [
                new GameBoardFact(new ActorWaitStartedEvent(BoardIds.Alice, ModelTime.Zero)),
            ]);
        CommittedTransition second = Transition(
            transitionCount: 2,
            new LogicalInstant(ModelTime.Zero, 1),
            [new GameBoardFact(new ActorWaitedEvent(BoardIds.Alice))]);
        var pacer = new ManualPresentationPacer();
        FirstBoardPresentationLoop loop = harness.CreateLoop(pacer);
        harness.Publish(first);
        harness.Publish(second);
        harness.Channel.Writer.TryComplete();

        Task run = loop.RunAsync(harness.Channel.Reader, CancellationToken.None);
        await pacer.WaitForRequestCountAsync(1);
        Assert.Equal(0, harness.Coordination.Snapshot().Presented.TransitionCount);
        pacer.ReleaseNext();
        await harness.Coordination.WaitUntilPresentedAsync(first.Version, CancellationToken.None);
        await pacer.WaitForRequestCountAsync(2);
        Assert.Equal(first.Version, harness.Coordination.Snapshot().Presented);
        pacer.ReleaseNext();
        await run;

        Assert.Equal(second.Version, harness.Coordination.Snapshot().Presented);
        Assert.Equal(
            ["actor.wait-started", "actor.waited"],
            harness.Terminal.Cues.Select(cue => cue.Code));
        Assert.Equal(new LogicalInstant(ModelTime.Zero, 1), loop.LastPresentedInstant);
    }

    [Fact]
    public async Task InvalidAtomicBatchPublishesNoCueWorldOrPresentedPrefix()
    {
        PresentationHarness harness = CreateHarness(PresentationMode.Player, BoardIds.Alice);
        CommittedTransition invalid = Transition(
            transitionCount: 1,
            new LogicalInstant(ModelTime.Zero, 0),
            [new GameBoardFact(new ObjectTakenEvent(BoardIds.Alice, BoardIds.BrassKey))]);
        FirstBoardPresentationLoop loop = harness.CreateLoop(
            new FixedIntervalPresentationPacer(TimeSpan.Zero));
        string genesis = FirstBoardScenario.WorldSnapshot(harness.Genesis);
        harness.Publish(invalid);
        harness.Channel.Writer.TryComplete();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            loop.RunAsync(harness.Channel.Reader, CancellationToken.None));

        Assert.Empty(harness.Terminal.Cues);
        Assert.Equal(0, harness.Coordination.Snapshot().Presented.TransitionCount);
        Assert.Equal(genesis, FirstBoardScenario.WorldSnapshot(loop.ReplayWorld));
    }

    [Fact]
    public async Task HiddenRemoteOccurrenceStillPresentsCommittedLocalTimeFlow()
    {
        PresentationHarness harness = CreateHarness(PresentationMode.Player, BoardIds.Alice);
        CommittedTransition transition = Transition(
            transitionCount: 1,
            new LogicalInstant(new ModelTime(10), 0),
            [new GameBoardFact(new ActorObservedEvent(BoardIds.Bob, []))]);
        FirstBoardPresentationLoop loop = harness.CreateLoop(
            new FixedIntervalPresentationPacer(TimeSpan.Zero));
        harness.Publish(transition);
        harness.Channel.Writer.TryComplete();

        await loop.RunAsync(harness.Channel.Reader, CancellationToken.None);

        PresentationCue cue = Assert.Single(harness.Terminal.Cues);
        Assert.Equal("time.advance", cue.Code);
        Assert.DoesNotContain(BoardIds.Bob, cue.Text, StringComparison.Ordinal);
        Assert.Equal(transition.Version, harness.Coordination.Snapshot().Presented);
    }

    [Fact]
    public async Task DeveloperModeAddsObjectiveOverlaysWithoutChangingReplay()
    {
        PresentationHarness harness = CreateHarness(PresentationMode.Developer, BoardIds.Alice);
        const string secret = "DEVELOPER-SECRET";
        CommittedTransition transition = Transition(
            transitionCount: 1,
            new LogicalInstant(ModelTime.Zero, 0),
            [
                new GameBoardFact(new ActorObservedEvent(
                    BoardIds.Bob,
                    [new BoardFact("private", null, secret)])),
            ]);
        FirstBoardPresentationLoop loop = harness.CreateLoop(
            new FixedIntervalPresentationPacer(TimeSpan.Zero));
        harness.Publish(transition);
        harness.Channel.Writer.TryComplete();

        await loop.RunAsync(harness.Channel.Reader, CancellationToken.None);

        Assert.Empty(harness.Terminal.Cues);
        Assert.Contains(
            harness.Terminal.DeveloperOverlays,
            overlay => overlay.Text.Contains(secret, StringComparison.Ordinal));
        Assert.Contains(
            harness.Terminal.DeveloperOverlays,
            overlay => overlay.Code == "developer.frontiers");
        Assert.Equal(
            ExpectedWorld(harness, transition),
            FirstBoardScenario.WorldSnapshot(loop.ReplayWorld));
    }

    [Fact]
    public async Task EmptyCaughtUpBufferShowsOnlyEphemeralWaitingStatus()
    {
        PresentationHarness harness = CreateHarness(PresentationMode.Player, BoardIds.Alice);
        FirstBoardPresentationLoop loop = harness.CreateLoop(
            new FixedIntervalPresentationPacer(TimeSpan.Zero));

        Task run = loop.RunAsync(harness.Channel.Reader, CancellationToken.None);
        await harness.Terminal.WaitForStatusCountAsync(1);

        TerminalStatus status = Assert.Single(harness.Terminal.Statuses);
        Assert.Equal(TerminalStatusKind.Buffering, status.Kind);
        Assert.Empty(harness.Terminal.Cues);
        Assert.Equal(0, harness.Coordination.Snapshot().Presented.TransitionCount);
        harness.Channel.Writer.TryComplete();
        await run;
    }

    [Fact]
    public async Task CancellationSkipsRemainingPacingButStillOutputsAndAcknowledgesCommittedPrefix()
    {
        PresentationHarness harness = CreateHarness(PresentationMode.Player, BoardIds.Alice);
        CommittedTransition first = Transition(
            transitionCount: 1,
            new LogicalInstant(ModelTime.Zero, 0),
            [new GameBoardFact(new ActorWaitStartedEvent(BoardIds.Alice, ModelTime.Zero))]);
        CommittedTransition second = Transition(
            transitionCount: 2,
            new LogicalInstant(ModelTime.Zero, 1),
            [new GameBoardFact(new ActorWaitedEvent(BoardIds.Alice))]);
        var pacer = new ManualPresentationPacer();
        using var cancellation = new CancellationTokenSource();
        FirstBoardPresentationLoop loop = harness.CreateLoop(pacer);
        harness.Publish(first);
        harness.Publish(second);
        harness.Channel.Writer.TryComplete();

        Task run = loop.RunAsync(harness.Channel.Reader, cancellation.Token);
        await pacer.WaitForRequestCountAsync(1);
        cancellation.Cancel();
        await run;

        Assert.Equal(second.Version, harness.Coordination.Snapshot().Presented);
        Assert.Equal(
            ExpectedWorld(harness, first, second),
            FirstBoardScenario.WorldSnapshot(loop.ReplayWorld));
        Assert.Equal(
            ["actor.wait-started", "actor.waited"],
            harness.Terminal.Cues.Select(cue => cue.Code));
    }

    private static PresentationHarness CreateHarness(
        PresentationMode mode,
        string? humanActorId)
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(Seed);
        FirstBoardWorld genesis = instance.CreateInitialWorld();
        var version = new WorldVersion(FirstBoardScenario.LineageId, 0);
        return new(
            instance,
            genesis,
            Channel.CreateUnbounded<CommittedTransition>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            }),
            new LiveSessionCoordination(version),
            new FakeTerminalUi(),
            mode,
            humanActorId);
    }

    private static CommittedTransition Transition(
        long transitionCount,
        LogicalInstant instant,
        IReadOnlyList<FirstBoardFact> facts) =>
        new(
            new WorldVersion(FirstBoardScenario.LineageId, transitionCount),
            new JournalBatch<FirstBoardFact>(
                instant,
                CandidateKey.FromUtf8($"presentation/{transitionCount}"),
                facts));

    private static string ExpectedWorld(
        PresentationHarness harness,
        params CommittedTransition[] transitions)
    {
        var reducer = new FirstBoardReducer(harness.Instance.Graph);
        FirstBoardWorld world = harness.Genesis;
        foreach (CommittedTransition transition in transitions)
        {
            foreach (FirstBoardFact fact in transition.Batch.Facts)
            {
                world = reducer.Apply(world, transition.Batch.Instant, fact);
            }

            reducer.Validate(world);
        }

        return FirstBoardScenario.WorldSnapshot(world);
    }

    private sealed record PresentationHarness(
        ScenarioInstance Instance,
        FirstBoardWorld Genesis,
        Channel<CommittedTransition> Channel,
        LiveSessionCoordination Coordination,
        FakeTerminalUi Terminal,
        PresentationMode Mode,
        string? HumanActorId)
    {
        public FirstBoardPresentationLoop CreateLoop(IPresentationPacer pacer) =>
            new(
                Instance,
                Genesis,
                Coordination,
                Terminal,
                pacer,
                Mode,
                HumanActorId);

        public void Publish(CommittedTransition transition)
        {
            Coordination.PublishCommitted(transition.Version);
            Assert.True(Channel.Writer.TryWrite(transition));
        }
    }
}
