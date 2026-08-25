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
    public async Task NonzeroBaselineDropsPrefixCueAndPresentsOnlyExactSuffix()
    {
        const long sourceLineage = 81_001;
        const long resumedLineage = 81_002;
        ScenarioInstance instance = ScenarioInstance.CreateDefault(Seed);
        FirstBoardWorld genesis = instance.CreateInitialWorld();
        CommittedTransition prefix = Transition(
            transitionCount: 1,
            new LogicalInstant(ModelTime.Zero, 0),
            [new GameBoardFact(new ActorWaitStartedEvent(BoardIds.Alice, ModelTime.Zero))],
            sourceLineage);
        FirstBoardWorld baselineWorld = ApplyTransitions(instance, genesis, prefix);
        PresentationHarness harness = CreateHarness(
            PresentationMode.Player,
            BoardIds.Alice,
            instance,
            baselineWorld,
            new WorldVersion(resumedLineage, 1),
            prefix.Batch.Instant);
        FirstBoardPresentationLoop loop = harness.CreateLoop(
            new FixedIntervalPresentationPacer(TimeSpan.Zero));
        CommittedTransition suffix = Transition(
            transitionCount: 2,
            new LogicalInstant(ModelTime.Zero, 1),
            [new GameBoardFact(new ActorWaitedEvent(BoardIds.Alice))],
            resumedLineage);

        Assert.Equal(
            new LiveFrontierSnapshot(
                new WorldVersion(resumedLineage, 1),
                new WorldVersion(resumedLineage, 1)),
            harness.Coordination.Snapshot());
        Assert.Empty(harness.Terminal.Cues);

        harness.Publish(suffix);
        harness.Channel.Writer.TryComplete();
        await loop.RunAsync(harness.Channel.Reader, CancellationToken.None);

        Assert.Equal(
            ["actor.waited"],
            harness.Terminal.Cues.Select(cue => cue.Code));
        Assert.DoesNotContain(
            harness.Terminal.Cues,
            cue => cue.Code == "actor.wait-started");
        Assert.Equal(
            FirstBoardScenario.WorldSnapshot(
                ApplyTransitions(instance, baselineWorld, suffix)),
            FirstBoardScenario.WorldSnapshot(loop.ReplayWorld));
        Assert.Equal(suffix.Batch.Instant, loop.LastPresentedInstant);
        Assert.Equal(
            new LiveFrontierSnapshot(suffix.Version, suffix.Version),
            harness.Coordination.Snapshot());
    }

    [Fact]
    public async Task CommitCueCrashGapBaselineSynthesizesNoPrefixCue()
    {
        const long sourceLineage = 82_001;
        const long resumedLineage = 82_002;
        ScenarioInstance instance = ScenarioInstance.CreateDefault(Seed);
        FirstBoardWorld genesis = instance.CreateInitialWorld();
        CommittedTransition committedBeforeCrash = Transition(
            transitionCount: 1,
            new LogicalInstant(ModelTime.Zero, 0),
            [new GameBoardFact(new ActorWaitStartedEvent(BoardIds.Alice, ModelTime.Zero))],
            sourceLineage);
        FirstBoardWorld baselineWorld = ApplyTransitions(
            instance,
            genesis,
            committedBeforeCrash);
        var baselineVersion = new WorldVersion(resumedLineage, 1);
        PresentationHarness harness = CreateHarness(
            PresentationMode.Player,
            BoardIds.Alice,
            instance,
            baselineWorld,
            baselineVersion,
            committedBeforeCrash.Batch.Instant);
        FirstBoardPresentationLoop loop = harness.CreateLoop(
            new FixedIntervalPresentationPacer(TimeSpan.Zero));
        harness.Channel.Writer.TryComplete();

        await loop.RunAsync(harness.Channel.Reader, CancellationToken.None);

        Assert.Empty(harness.Terminal.Cues);
        Assert.Equal(
            FirstBoardScenario.WorldSnapshot(baselineWorld),
            FirstBoardScenario.WorldSnapshot(loop.ReplayWorld));
        Assert.Equal(committedBeforeCrash.Batch.Instant, loop.LastPresentedInstant);
        Assert.Equal(
            new LiveFrontierSnapshot(baselineVersion, baselineVersion),
            harness.Coordination.Snapshot());
    }

    [Fact]
    public void InvalidPresentationBaselinesAreRejected()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(Seed);
        FirstBoardWorld genesis = instance.CreateInitialWorld();
        var terminal = new FakeTerminalUi();
        var pacer = new FixedIntervalPresentationPacer(TimeSpan.Zero);

        Assert.Throws<ArgumentException>(() => new FirstBoardPresentationLoop(
            instance,
            genesis,
            new LogicalInstant(ModelTime.Zero, 0),
            new LiveSessionCoordination(new WorldVersion(83_001, 0)),
            terminal,
            pacer,
            PresentationMode.Player,
            BoardIds.Alice));

        CommittedTransition prefix = Transition(
            transitionCount: 1,
            new LogicalInstant(ModelTime.Zero, 0),
            [new GameBoardFact(new ActorWaitStartedEvent(BoardIds.Alice, ModelTime.Zero))],
            lineageId: 83_001);
        FirstBoardWorld baselineWorld = ApplyTransitions(instance, genesis, prefix);
        var nonzero = new LiveSessionCoordination(new WorldVersion(83_002, 1));
        Assert.Throws<ArgumentException>(() => new FirstBoardPresentationLoop(
            instance,
            baselineWorld,
            nonzero,
            terminal,
            pacer,
            PresentationMode.Player,
            BoardIds.Alice));
        Assert.Throws<ArgumentException>(() => new FirstBoardPresentationLoop(
            instance,
            baselineWorld,
            new LogicalInstant(new ModelTime(1), 0),
            new LiveSessionCoordination(new WorldVersion(83_002, 1)),
            terminal,
            pacer,
            PresentationMode.Player,
            BoardIds.Alice));

        var unequal = new LiveSessionCoordination(new WorldVersion(83_002, 1));
        unequal.PublishCommitted(new WorldVersion(83_002, 2));
        Assert.Throws<ArgumentException>(() => new FirstBoardPresentationLoop(
            instance,
            baselineWorld,
            prefix.Batch.Instant,
            unequal,
            terminal,
            pacer,
            PresentationMode.Player,
            BoardIds.Alice));
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
        string baseline = FirstBoardScenario.WorldSnapshot(harness.BaselineWorld);
        harness.Publish(invalid);
        harness.Channel.Writer.TryComplete();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            loop.RunAsync(harness.Channel.Reader, CancellationToken.None));

        Assert.Empty(harness.Terminal.Cues);
        Assert.Equal(0, harness.Coordination.Snapshot().Presented.TransitionCount);
        Assert.Equal(baseline, FirstBoardScenario.WorldSnapshot(loop.ReplayWorld));
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
        FirstBoardWorld baselineWorld = instance.CreateInitialWorld();
        var baselineVersion = new WorldVersion(FirstBoardScenario.LineageId, 0);
        return CreateHarness(
            mode,
            humanActorId,
            instance,
            baselineWorld,
            baselineVersion,
            baselineLastInstant: null);
    }

    private static PresentationHarness CreateHarness(
        PresentationMode mode,
        string? humanActorId,
        ScenarioInstance instance,
        FirstBoardWorld baselineWorld,
        WorldVersion baselineVersion,
        LogicalInstant? baselineLastInstant)
    {
        return new(
            instance,
            baselineWorld,
            baselineLastInstant,
            Channel.CreateUnbounded<CommittedTransition>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            }),
            new LiveSessionCoordination(baselineVersion),
            new FakeTerminalUi(),
            mode,
            humanActorId);
    }

    private static CommittedTransition Transition(
        long transitionCount,
        LogicalInstant instant,
        IReadOnlyList<FirstBoardFact> facts,
        long lineageId = FirstBoardScenario.LineageId) =>
        new(
            new WorldVersion(lineageId, transitionCount),
            new JournalBatch<FirstBoardFact>(
                instant,
                CandidateKey.FromUtf8($"presentation/{transitionCount}"),
                facts));

    private static string ExpectedWorld(
        PresentationHarness harness,
        params CommittedTransition[] transitions)
        => FirstBoardScenario.WorldSnapshot(
            ApplyTransitions(harness.Instance, harness.BaselineWorld, transitions));

    private static FirstBoardWorld ApplyTransitions(
        ScenarioInstance instance,
        FirstBoardWorld baselineWorld,
        params CommittedTransition[] transitions)
    {
        var reducer = new FirstBoardReducer(instance.Graph);
        FirstBoardWorld world = baselineWorld;
        foreach (CommittedTransition transition in transitions)
        {
            foreach (FirstBoardFact fact in transition.Batch.Facts)
            {
                world = reducer.Apply(world, transition.Batch.Instant, fact);
            }

            reducer.Validate(world);
        }

        return world;
    }

    private sealed record PresentationHarness(
        ScenarioInstance Instance,
        FirstBoardWorld BaselineWorld,
        LogicalInstant? BaselineLastInstant,
        Channel<CommittedTransition> Channel,
        LiveSessionCoordination Coordination,
        FakeTerminalUi Terminal,
        PresentationMode Mode,
        string? HumanActorId)
    {
        public FirstBoardPresentationLoop CreateLoop(IPresentationPacer pacer) =>
            new(
                Instance,
                BaselineWorld,
                BaselineLastInstant,
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
