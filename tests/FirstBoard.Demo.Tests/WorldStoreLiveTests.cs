using DramaBoard.FirstBoard.Persistence;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Player;
using DramaBoard.Protocol;

namespace DramaBoard.FirstBoard.Demo.Tests;

public sealed class WorldStoreLiveTests
{
    [Fact]
    public async Task CreateThenResumeUsesSavedScenarioAndDoesNotReplayCompletedPresentation()
    {
        using var directory = new TemporaryWorldStore();
        DemoOptions create = DemoOptions.Parse(["--world-store", directory.Path, "--seed", "901"]);
        BoardRunCapture first;
        using (FirstBoardOccurrenceHistory history = DemoWorldStore.Open(create)!)
        {
            first = await Run(history, AllNullDrivers(), new FakeTerminalUi());
            Assert.Equal(2, first.Result.Version.TransitionCount);
        }

        DemoOptions resume = DemoOptions.Parse(["--world-store", directory.Path, "--resume-world"]);
        using FirstBoardOccurrenceHistory reopened = DemoWorldStore.Open(resume)!;
        Assert.Equal(901UL, reopened.Scenario.WorldSeed);
        Assert.Equal(first.Result.Version, reopened.Cursor.Version);
        var terminal = new FakeTerminalUi();
        BoardRunCapture continued = await Run(reopened, AllNullDrivers(), terminal);
        Assert.Equal(first.Result.Version, continued.InitialCursor.Version);
        Assert.Equal(first.Result.Version, continued.Result.Version);
        Assert.Empty(continued.CompletedEvents);
        Assert.Empty(terminal.Cues);
        Assert.Equal(FirstBoardScenario.WorldSnapshot(first.Result.World),
            FirstBoardScenario.WorldSnapshot(continued.Result.World));
    }

    [Fact]
    public async Task PendingEventBeyondNewRunBoundaryIsCompletedAndPresentedWithoutAskingPlayers()
    {
        using var directory = new TemporaryWorldStore();
        DemoOptions options = DemoOptions.Parse(["--world-store", directory.Path, "--seed", "901"]);
        CandidateKey pendingCause = CandidateKey.FromUtf8("test/live/pending-wait");
        ScenarioInstance scenario = ScenarioInstance.CreateDefault(options.WorldSeed);
        FirstBoardWorld initial = scenario.CreateInitialWorld();
        initial = initial.With(game: initial.Game.With(now: new ModelTime(10)));
        using (FirstBoardOccurrenceHistory history = FirstBoardOccurrenceHistory.Create(directory.Path, "main",
            scenario, initial, FirstBoardScenario.CreateMemoryHistory(initial).Cursor,
            FirstBoardScenario.CreateRules(scenario), DemoWorldStore.DriverBinding(options)))
        {
            history.CommitEvent(new OccurrenceEvent<FirstBoardFact>(
                pendingCause, new LogicalInstant(new ModelTime(10), 0),
                [new GameBoardFact(new ActorWaitStartedEvent(BoardIds.Alice, new ModelTime(1_000)))]));
        }

        using FirstBoardOccurrenceHistory reopened = DemoWorldStore.Open(options with { ResumeWorld = true })!;
        var terminal = new FakeTerminalUi();
        BoardRunCapture capture = await Run(reopened, new Dictionary<string, IPlayerDriver>
        {
            [BoardIds.Alice] = new NoCallsDriver(),
            [BoardIds.Bob] = new NoCallsDriver(),
        }, terminal);
        Assert.Equal(0, capture.InitialCursor.Version.TransitionCount);
        Assert.Equal(new ModelTime(10), capture.InitialCursor.CurrentModelTime);
        Assert.Equal(pendingCause, Assert.Single(capture.CompletedEvents).CauseKey);
        Assert.Equal(StepStatus.BoundaryReached, capture.Result.Status);
        Assert.Null(reopened.PendingEvent);
        Assert.Contains(terminal.DeveloperOverlays, overlay =>
            overlay.Code == "developer.fact" && overlay.Text.Contains("actor.wait-started", StringComparison.Ordinal));
        Assert.Contains(terminal.DeveloperOverlays, overlay =>
            overlay.Code == "developer.batch" && overlay.Text.Contains(pendingCause.ToString(), StringComparison.Ordinal));
        Assert.Equal(1, reopened.Cursor.Version.TransitionCount);
    }

    [Fact]
    public async Task PersistedHumanAndAiStillShareTheLiveAuthorityLoop()
    {
        using var directory = new TemporaryWorldStore();
        DemoOptions options = DemoOptions.Parse(
            ["--world-store", directory.Path, "--human", "alice", "--seed", "902"]);
        using FirstBoardOccurrenceHistory history = DemoWorldStore.Open(options)!;
        var terminal = new FakeTerminalUi();
        Task<BoardRunCapture> run = LiveSession.RunAsync(history,
            new Dictionary<string, IPlayerDriver> { [BoardIds.Bob] = new NullPlayerDriver() },
            BoardIds.Alice, PresentationMode.Player, terminal,
            new FixedIntervalPresentationPacer(TimeSpan.Zero), ModelTime.Zero, CancellationToken.None);
        await terminal.WaitForPromptCountAsync(1);
        DecisionRequest prompt = Assert.Single(terminal.Prompts);
        await terminal.WaitForActiveReadAsync(prompt.DecisionId);
        Assert.True(terminal.TrySubmit(prompt.DecisionId, "wait 1000"));
        BoardRunCapture result = await run;
        Assert.Equal(result.Result.Version, history.Cursor.Version);
        Assert.Contains(result.CompletedEvents.SelectMany(value => value.Facts),
            fact => fact is GameBoardFact { Value: ActorWaitStartedEvent { ActorId: BoardIds.Alice } });
    }

    [Fact]
    public async Task ReopenedKernelHonorsSavedBudgetAndExactDriverRoster()
    {
        using var directory = new TemporaryWorldStore();
        ScenarioInstance scenario = ScenarioInstance.CreateDefault(903);
        FirstBoardWorld initial = scenario.CreateInitialWorld();
        var binding = new FirstBoardDriverBinding([BoardIds.Alice, BoardIds.Bob], "test/null-v1");
        using (FirstBoardOccurrenceHistory history = FirstBoardOccurrenceHistory.Create(directory.Path, "main",
            scenario, initial, FirstBoardScenario.CreateMemoryHistory(initial).Cursor,
            new SimulationRules(scenario.WorldSeed, 1), binding))
        {
            var kernel = FirstBoardScenario.CreateKernel(AllNullDrivers(), scenario, history);
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(ModelTime.Zero));
        }
        using FirstBoardOccurrenceHistory reopened = FirstBoardOccurrenceHistory.Open(directory.Path, "main", binding);
        Assert.Throws<ArgumentException>(() => FirstBoardScenario.CreateKernel(
            new Dictionary<string, IPlayerDriver> { [BoardIds.Alice] = new NullPlayerDriver() },
            reopened.Scenario, reopened));
        var resumed = FirstBoardScenario.CreateKernel(AllNullDrivers(), reopened.Scenario, reopened);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await resumed.StepAsync(ModelTime.Zero));
        Assert.Equal(1, reopened.Cursor.Version.TransitionCount);
        Assert.Null(reopened.PendingEvent);
    }

    [Fact]
    public void ResumeOptionsRequireAStoreAndUseTheSavedSeed()
    {
        Assert.Throws<ArgumentException>(() => DemoOptions.Parse(["--resume-world"]));
        Assert.Throws<ArgumentException>(() => DemoOptions.Parse(
            ["--world-store", "saved-world", "--resume-world", "--seed", "42"]));
        DemoOptions options = DemoOptions.Parse(["--world-store", "saved-world", "--resume-world"]);
        Assert.True(options.ResumeWorld);
        Assert.True(System.IO.Path.IsPathFullyQualified(options.WorldStore!));
    }

    private static Task<BoardRunCapture> Run(FirstBoardOccurrenceHistory history,
        IReadOnlyDictionary<string, IPlayerDriver> drivers, FakeTerminalUi terminal) =>
        LiveSession.RunAsync(history, drivers, null, PresentationMode.Developer, terminal,
            new FixedIntervalPresentationPacer(TimeSpan.Zero), ModelTime.Zero, CancellationToken.None);

    private static IReadOnlyDictionary<string, IPlayerDriver> AllNullDrivers() =>
        new Dictionary<string, IPlayerDriver>
        {
            [BoardIds.Alice] = new NullPlayerDriver(),
            [BoardIds.Bob] = new NullPlayerDriver(),
        };

    private sealed class NoCallsDriver : IPlayerDriver
    {
        public ValueTask<PlayerDecision> DecideAsync(DecisionRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Pending recovery must not ask the Player again.");
    }

    private sealed class TemporaryWorldStore : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "dramaboard-live-world", Guid.NewGuid().ToString("N"));
        public void Dispose()
        {
            if (Directory.Exists(Path)) { Directory.Delete(Path, recursive: true); }
        }
    }
}
