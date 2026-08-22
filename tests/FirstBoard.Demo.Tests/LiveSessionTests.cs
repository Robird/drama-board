using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Player;
using DramaBoard.Protocol;

namespace DramaBoard.FirstBoard.Demo.Tests;

public sealed class LiveSessionTests
{
    [Fact]
    public async Task AllAiScriptedRunReachesBoundaryWithTheWholePrefixPresented()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 901);
        IReadOnlyDictionary<string, IPlayerDriver> drivers = Drivers(
            (BoardIds.Alice, new NullPlayerDriver()),
            (BoardIds.Bob, new NullPlayerDriver()));
        var terminal = new FakeTerminalUi();

        BoardRunCapture capture = await LiveSession.RunAsync(
            instance,
            drivers,
            humanActorId: null,
            PresentationMode.Developer,
            terminal,
            new FixedIntervalPresentationPacer(TimeSpan.Zero),
            ModelTime.Zero,
            CancellationToken.None);

        Assert.Equal(StepStatus.BoundaryReached, capture.Result.Status);
        Assert.Equal(2, capture.Result.Version.TransitionCount);
        Assert.Equal(
            capture.Result.Version.TransitionCount,
            capture.Journal.Batches.Count);
        Assert.Equal(
            FirstBoardScenario.WorldSnapshot(capture.Result.World),
            ReplaySnapshot(instance, capture));
        Assert.Equal(
            TerminalStatusKind.Completed,
            terminal.Statuses[^1].Kind);
        Assert.Equal(6, terminal.DeveloperOverlays.Count);
    }

    [Fact]
    public async Task HumanOrdinaryCommandCommitsOnlyAfterPromptAndIsPresentedFromItsBatch()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 902);
        var terminal = new FakeTerminalUi();
        Task<BoardRunCapture> run = LiveSession.RunAsync(
            instance,
            Drivers((BoardIds.Bob, new NullPlayerDriver())),
            BoardIds.Alice,
            PresentationMode.Player,
            terminal,
            new FixedIntervalPresentationPacer(TimeSpan.Zero),
            ModelTime.Zero,
            CancellationToken.None);

        await terminal.WaitForPromptCountAsync(1);
        DecisionRequest prompt = Assert.Single(terminal.Prompts);
        Assert.Equal(BoardIds.Alice, prompt.ActorId);
        Assert.False(run.IsCompleted);
        await terminal.WaitForActiveReadAsync(prompt.DecisionId);
        Assert.True(terminal.TrySubmit(prompt.DecisionId, "wait 1000"));

        BoardRunCapture capture = await run;
        Assert.Contains(
            capture.Journal.Batches.SelectMany(batch => batch.Facts),
            fact => fact is GameBoardFact
            {
                Value: ActorWaitStartedEvent
                {
                    ActorId: BoardIds.Alice,
                    CompleteAt.Ticks: 1_000,
                },
            });
        Assert.Contains(terminal.Cues, cue => cue.Code == "actor.wait-started");
        Assert.Equal(
            capture.Result.Version.TransitionCount,
            capture.Journal.Batches.Count);
        Assert.Equal(TerminalStatusKind.Completed, terminal.Statuses[^1].Kind);
    }

    [Theory]
    [InlineData("continue", PassageEncounterResolution.Continued)]
    [InlineData("reverse", PassageEncounterResolution.Reversed)]
    public async Task HumanCanTravelIntoARealPassageEncounterAndResolveIt(
        string command,
        PassageEncounterResolution expectedResolution)
    {
        // Seed 0 selects Alice for both the first place decision and the later
        // head-on encounter response in the default scenario.
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 0);
        var terminal = new FakeTerminalUi();
        Task<BoardRunCapture> run = LiveSession.RunAsync(
            instance,
            Drivers((BoardIds.Bob, new RoadTravelerPlayerDriver())),
            BoardIds.Alice,
            PresentationMode.Player,
            terminal,
            new FixedIntervalPresentationPacer(TimeSpan.Zero),
            new ModelTime(150_000),
            CancellationToken.None);

        await terminal.WaitForPromptCountAsync(1);
        DecisionRequest travelPrompt = terminal.Prompts[0];
        Assert.Equal(BoardIds.Tavern, travelPrompt.Observation.LocationId);
        await terminal.WaitForActiveReadAsync(travelPrompt.DecisionId);
        Assert.True(terminal.TrySubmit(
            travelPrompt.DecisionId,
            $"travel exit:{BoardIds.TavernMarketRoad}"));

        await terminal.WaitForPromptCountAsync(2);
        DecisionRequest encounterPrompt = terminal.Prompts[1];
        Assert.Equal(BoardIds.TavernMarketRoad, encounterPrompt.Observation.LocationId);
        Assert.Equal([BoardIds.Bob], encounterPrompt.Observation.VisibleActorIds);
        Assert.Contains(
            encounterPrompt.AvailableActions,
            action => action.ActionKind == ActionKinds.ContinueTravel);
        Assert.Contains(
            encounterPrompt.AvailableActions,
            action => action.ActionKind == ActionKinds.ReverseTravel);
        await terminal.WaitForActiveReadAsync(encounterPrompt.DecisionId);
        Assert.True(terminal.TrySubmit(encounterPrompt.DecisionId, command));

        BoardRunCapture capture = await run;
        PassageEncounterResolvedEvent resolved = capture.Journal.Batches
            .SelectMany(batch => batch.Facts)
            .OfType<GameBoardFact>()
            .Select(fact => fact.Value)
            .OfType<PassageEncounterResolvedEvent>()
            .Single();
        Assert.Equal(expectedResolution, resolved.Resolution);
        Assert.Equal(BoardIds.Alice, resolved.RespondingActorId);
        Assert.Contains(
            terminal.Cues,
            cue => cue.Code == "passage-encounter.opened");
        Assert.Contains(
            terminal.Cues,
            cue => cue.Code == "passage-encounter.resolved");
        Assert.Equal(TerminalStatusKind.Completed, terminal.Statuses[^1].Kind);
    }

    [Fact]
    public async Task DelayedAiLetsPresentationDrainBacklogThenShowsOnlyBufferingUntilOneResponse()
    {
        ulong seed = FindFirstDecisionActorSeed(BoardIds.Alice);
        ScenarioInstance instance = ScenarioInstance.CreateDefault(seed);
        var delayedBob = new ControllablePlayerDriver();
        var terminal = new FakeTerminalUi();
        var pacer = new ManualPresentationPacer();
        Task<BoardRunCapture> run = LiveSession.RunAsync(
            instance,
            Drivers(
                (BoardIds.Alice, new NullPlayerDriver()),
                (BoardIds.Bob, delayedBob)),
            humanActorId: null,
            PresentationMode.Developer,
            terminal,
            pacer,
            ModelTime.Zero,
            CancellationToken.None);

        DecisionRequest delayedRequest = await delayedBob.WaitForRequestAsync();
        Assert.Equal(BoardIds.Bob, delayedRequest.ActorId);
        Assert.Equal(1, delayedBob.CallCount);

        await pacer.WaitForRequestCountAsync(1);
        pacer.ReleaseNext();
        await pacer.WaitForRequestCountAsync(2);
        pacer.ReleaseNext();
        await pacer.WaitForRequestCountAsync(3);
        int statusCountBeforeFinalBacklogPace = terminal.Statuses.Count;
        pacer.ReleaseNext();
        await terminal.WaitForStatusCountAsync(statusCountBeforeFinalBacklogPace + 1);

        Assert.Equal(TerminalStatusKind.Buffering, terminal.Statuses[^1].Kind);
        Assert.False(run.IsCompleted);
        Assert.Equal(1, delayedBob.CallCount);
        Assert.Equal(3, terminal.DeveloperOverlays.Count);

        delayedBob.Complete(new Intent(ActionKinds.Wait));
        await pacer.WaitForRequestCountAsync(4);
        pacer.ReleaseNext();
        await pacer.WaitForRequestCountAsync(5);
        pacer.ReleaseNext();
        await pacer.WaitForRequestCountAsync(6);
        pacer.ReleaseNext();

        BoardRunCapture capture = await run;
        Assert.Equal(1, delayedBob.CallCount);
        Assert.Equal(2, capture.Result.Version.TransitionCount);
        Assert.Equal(6, terminal.DeveloperOverlays.Count);
        Assert.Equal(TerminalStatusKind.Completed, terminal.Statuses[^1].Kind);
    }

    [Fact]
    public async Task FaultAfterCommittedPrefixPresentsThatPrefixBeforePropagatingOriginalFault()
    {
        ulong seed = FindFirstDecisionActorSeed(BoardIds.Alice);
        ScenarioInstance instance = ScenarioInstance.CreateDefault(seed);
        var expected = new ArithmeticException("delayed AI failed");
        var terminal = new FakeTerminalUi();

        ArithmeticException actual = await Assert.ThrowsAsync<ArithmeticException>(() =>
            LiveSession.RunAsync(
                instance,
                Drivers(
                    (BoardIds.Alice, new NullPlayerDriver()),
                    (BoardIds.Bob, new ThrowingPlayerDriver(expected))),
                humanActorId: null,
                PresentationMode.Developer,
                terminal,
                new FixedIntervalPresentationPacer(TimeSpan.Zero),
                ModelTime.Zero,
                CancellationToken.None));

        Assert.Same(expected, actual);
        Assert.Contains(
            terminal.DeveloperOverlays,
            overlay =>
                overlay.Code == "developer.fact" &&
                overlay.Text.Contains("name=actor.wait-started", StringComparison.Ordinal) &&
                overlay.Text.Contains(BoardIds.Alice, StringComparison.Ordinal));
        Assert.Equal(TerminalStatusKind.Faulted, terminal.Statuses[^1].Kind);
        Assert.Contains(nameof(ArithmeticException), terminal.Statuses[^1].Text);
    }

    [Fact]
    public async Task ExternalCancelDuringHumanReadClearsInputAfterCommittedPrefixWasPresented()
    {
        ulong seed = FindFirstDecisionActorSeed(BoardIds.Bob);
        ScenarioInstance instance = ScenarioInstance.CreateDefault(seed);
        var terminal = new FakeTerminalUi();
        using var cancellation = new CancellationTokenSource();
        Task<BoardRunCapture> run = LiveSession.RunAsync(
            instance,
            Drivers((BoardIds.Bob, new NullPlayerDriver())),
            BoardIds.Alice,
            PresentationMode.Developer,
            terminal,
            new FixedIntervalPresentationPacer(TimeSpan.Zero),
            ModelTime.Zero,
            cancellation.Token);

        await terminal.WaitForPromptCountAsync(1);
        DecisionRequest prompt = Assert.Single(terminal.Prompts);
        Assert.Contains(
            terminal.DeveloperOverlays,
            overlay =>
                overlay.Code == "developer.fact" &&
                overlay.Text.Contains("name=actor.wait-started", StringComparison.Ordinal) &&
                overlay.Text.Contains(BoardIds.Bob, StringComparison.Ordinal));

        cancellation.Cancel();
        LiveSessionCanceledException canceled =
            await Assert.ThrowsAsync<LiveSessionCanceledException>(async () => await run);

        Assert.False(terminal.TrySubmit(prompt.DecisionId, "wait"));
        Assert.Equal(TerminalStatusKind.Canceled, terminal.Statuses[^1].Kind);
        Assert.Single(terminal.Prompts);
        Assert.Equal(canceled.Capture.Version.TransitionCount, canceled.Capture.Journal.Batches.Count);
        Assert.Equal(
            FirstBoardScenario.WorldSnapshot(canceled.Capture.World),
            ReplayCanceledSnapshot(instance, canceled.Capture));
    }

    [Fact]
    public async Task HumanEndOfInputReturnsCanceledCaptureWithoutExternalCancellation()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 0);
        var terminal = new FakeTerminalUi();
        Task<BoardRunCapture> run = LiveSession.RunAsync(
            instance,
            Drivers((BoardIds.Bob, new NullPlayerDriver())),
            BoardIds.Alice,
            PresentationMode.Player,
            terminal,
            new FixedIntervalPresentationPacer(TimeSpan.Zero),
            ModelTime.Zero,
            CancellationToken.None);

        await terminal.WaitForPromptCountAsync(1);
        DecisionRequest prompt = Assert.Single(terminal.Prompts);
        await terminal.WaitForActiveReadAsync(prompt.DecisionId);
        Assert.True(terminal.TrySubmit(prompt.DecisionId, command: null));

        LiveSessionCanceledException canceled =
            await Assert.ThrowsAsync<LiveSessionCanceledException>(async () => await run);
        Assert.Empty(canceled.Capture.Journal.Batches);
        Assert.Equal(TerminalStatusKind.Canceled, terminal.Statuses[^1].Kind);
    }

    private static ulong FindFirstDecisionActorSeed(string expectedActorId)
    {
        IReadOnlyDictionary<string, IPlayerDriver> drivers = Drivers(
            (BoardIds.Alice, new NullPlayerDriver()),
            (BoardIds.Bob, new NullPlayerDriver()));
        for (ulong seed = 0; seed < 10_000; seed++)
        {
            ScenarioInstance instance = ScenarioInstance.CreateDefault(seed);
            FirstBoardWorld world = instance.CreateInitialWorld();
            var rules = new SimulationRules(seed, maxTransitionsPerModelTime: 10_000);
            IReadOnlyList<OccurrenceCandidate<BoardCandidate>> candidates =
                new DecisionPointRule(drivers, instance).Forecast(world, rules);
            DecisionPointCandidate winner = Assert.IsType<DecisionPointCandidate>(
                OccurrenceScheduler.SelectWinner(candidates, seed).Data);
            if (world.Actor(winner.ActorId).Key == expectedActorId)
            {
                return seed;
            }
        }

        throw new InvalidOperationException(
            $"No scheduler seed selected '{expectedActorId}' first.");
    }

    private static IReadOnlyDictionary<string, IPlayerDriver> Drivers(
        params (string ActorId, IPlayerDriver Driver)[] values) =>
        values.ToDictionary(value => value.ActorId, value => value.Driver, StringComparer.Ordinal);

    private static string ReplaySnapshot(ScenarioInstance instance, BoardRunCapture capture)
    {
        var reducer = new FirstBoardReducer(instance.Graph);
        ReplayResult<FirstBoardWorld> replay = SimulationReplay.Replay(
            capture.InitialWorld,
            capture.Journal.LineageId,
            capture.InitialWorld.Now,
            capture.Journal.Batches,
            reducer.Apply,
            reducer.Validate);
        return FirstBoardScenario.WorldSnapshot(replay.World);
    }

    private static string ReplayCanceledSnapshot(
        ScenarioInstance instance,
        LiveSessionCanceledCapture capture)
    {
        var reducer = new FirstBoardReducer(instance.Graph);
        ReplayResult<FirstBoardWorld> replay = SimulationReplay.Replay(
            capture.InitialWorld,
            capture.Journal.LineageId,
            capture.InitialWorld.Now,
            capture.Journal.Batches,
            reducer.Apply,
            reducer.Validate);
        return FirstBoardScenario.WorldSnapshot(replay.World);
    }

    private sealed class ControllablePlayerDriver : IPlayerDriver
    {
        private readonly TaskCompletionSource<DecisionRequest> _request =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<Intent> _intent =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public async ValueTask<PlayerDecision> DecideAsync(
            DecisionRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            _request.TrySetResult(request);
            Intent intent = await _intent.Task
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return new PlayerDecision(request.DecisionId, intent);
        }

        public Task<DecisionRequest> WaitForRequestAsync() =>
            _request.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void Complete(Intent intent) => _intent.TrySetResult(intent);
    }

    private sealed class ThrowingPlayerDriver(Exception error) : IPlayerDriver
    {
        public ValueTask<PlayerDecision> DecideAsync(
            DecisionRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromException<PlayerDecision>(error);
        }
    }

    private sealed class RoadTravelerPlayerDriver : IPlayerDriver
    {
        public ValueTask<PlayerDecision> DecideAsync(
            DecisionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Intent intent = request.AvailableActions.Any(
                action => action.ActionKind == ActionKinds.Travel)
                ? new Intent(
                    ActionKinds.Travel,
                    ExitId: $"exit:{BoardIds.TavernMarketRoad}")
                : request.AvailableActions.Any(
                    action => action.ActionKind == ActionKinds.ContinueTravel)
                    ? new Intent(ActionKinds.ContinueTravel)
                    : new Intent(ActionKinds.Wait);
            return ValueTask.FromResult(new PlayerDecision(request.DecisionId, intent));
        }
    }
}
