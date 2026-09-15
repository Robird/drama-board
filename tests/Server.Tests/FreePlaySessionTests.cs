using DramaBoard.Player;
using DramaBoard.Protocol;
using DramaBoard.Server.FreePlay;

namespace DramaBoard.Server.Tests;

public sealed class FreePlaySessionTests {
    [Fact]
    public async Task TwoMovesCommitDepartureAndArrivalAndRebuildOwnTrajectory() {
        await using var run = new RunningSession();
        PlayerView initial = await run.WaitAtAsync("A");
        DevView initialDev = run.Session.GetDevView();
        Assert.Equal(0, initial.ModelTimeMs);
        Assert.Equal(0, initialDev.TransitionCount);
        Assert.Single(initial.Trajectory);
        Assert.Equal(4, initial.KnownMap.Places.Count);
        Assert.Equal(4, initial.KnownMap.Passages.Count);
        SubmitTo(run.Session, initial, "B");
        PlayerView atB = await run.WaitAtAsync("B");
        Assert.Equal(1000, atB.ModelTimeMs);
        Assert.Equal(2, run.Session.GetDevView().TransitionCount);
        SubmitTo(run.Session, atB, "D");
        PlayerView atD = await run.WaitAtAsync("D");
        DevView dev = run.Session.GetDevView();
        Assert.Equal(4000, atD.ModelTimeMs);
        Assert.Equal(4, dev.TransitionCount);
        Assert.Equal(new long[] { 0, 1000, 1000, 4000 }, dev.Records.Select(record => record.ModelTimeMs));
        Assert.Equal(new[] { "A", "B", "D" }, atD.Trajectory.Select(entry => entry.PlaceId));
        Assert.Equal(new long[] { 0, 1000, 4000 }, atD.Trajectory.Select(entry => entry.ModelTimeMs));
        Assert.Equal(new string?[] { null, "ab", "bd" }, atD.Trajectory.Select(entry => entry.PassageId));
        Assert.All(dev.Records, record => Assert.False(string.IsNullOrWhiteSpace(record.CauseKey)));
        Assert.Contains(dev.Records[0].FactKinds, kind => kind.Contains("Start", StringComparison.Ordinal));
        Assert.Contains(dev.Records[1].FactKinds, kind => kind.Contains("Arriv", StringComparison.Ordinal));
        Assert.Contains(dev.Records[2].FactKinds, kind => kind.Contains("Start", StringComparison.Ordinal));
        Assert.Contains(dev.Records[3].FactKinds, kind => kind.Contains("Arriv", StringComparison.Ordinal));
        // Earlier published arrays must remain snapshots while history grows.
        Assert.Empty(initialDev.Records);
        Assert.Single(initial.Trajectory);
        Assert.Equal(2, atB.Trajectory.Count);
        Assert.NotEqual(initial.Decision!.DecisionId, atB.Decision!.DecisionId);
        Assert.NotEqual(atB.Decision.DecisionId, atD.Decision!.DecisionId);
    }

    [Fact]
    public async Task WaitingReadsAndInvalidAnswersDoNotConsumeRequestOrAdvanceTime() {
        await using var run = new RunningSession();
        PlayerView initial = await run.WaitAtAsync("A");
        string decisionId = initial.Decision!.DecisionId;
        Assert.Equal(400, run.Session.Submit(decisionId, "action.wait", initial.Decision.Exits[0].ExitId).StatusCode);
        Assert.Equal(400, run.Session.Submit(decisionId, "action.travel", "unknown").StatusCode);
        for (int index = 0; index < 10; index++) {
            PlayerView view = run.Session.GetPlayerView();
            Assert.Equal(decisionId, view.Decision!.DecisionId);
            Assert.Equal(0, view.ModelTimeMs);
            Assert.Equal(0, run.Session.GetDevView().TransitionCount);
        }
        SubmitTo(run.Session, initial, "B");
        await run.WaitAtAsync("B");
        Assert.Equal(409, run.Session.Submit(decisionId, "action.travel", initial.Decision.Exits[0].ExitId).StatusCode);
    }

    [Fact]
    public async Task ConcurrentDuplicateAnswersHaveExactlyOneWinner() {
        await using var run = new RunningSession();
        PlayerView initial = await run.WaitAtAsync("A");
        DecisionView decision = initial.Decision!;
        string exit = decision.Exits.Single(candidate => candidate.DestinationId == "B").ExitId;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<SubmitResult>[] submissions = Enumerable.Range(0, 12).Select(async _ => {
            await start.Task;
            return run.Session.Submit(decision.DecisionId, "action.travel", exit);
        }).ToArray();
        start.SetResult();
        SubmitResult[] results = await Task.WhenAll(submissions).WaitAsync(run.Timeout.Token);
        Assert.Single(results, result => result.StatusCode == 202);
        Assert.Equal(11, results.Count(result => result.StatusCode == 409));
        await run.WaitAtAsync("B");
        Assert.Equal(2, run.Session.GetDevView().TransitionCount);
    }

    [Fact]
    public async Task PriorRunDecisionIsRejectedAndShutdownEndsPendingWaiter() {
        await using var oldRun = new RunningSession();
        PlayerView old = await oldRun.WaitAtAsync("A");
        await using var newRun = new RunningSession();
        PlayerView current = await newRun.WaitAtAsync("A");
        Assert.NotEqual(old.RunId, current.RunId);
        Assert.Equal(409, newRun.Session.Submit(old.Decision!.DecisionId, "action.travel", current.Decision!.Exits[0].ExitId).StatusCode);
        Assert.Equal(current.Decision.DecisionId, newRun.Session.GetPlayerView().Decision!.DecisionId);
        await newRun.StopAsync();
        Assert.Equal("stopped", newRun.Session.GetPlayerView().Status);
        Assert.Equal(503, newRun.Session.Submit(current.Decision.DecisionId, "action.travel", current.Decision.Exits[0].ExitId).StatusCode);
        Assert.Equal(0, newRun.Session.GetDevView().TransitionCount);
    }

    [Fact]
    public async Task DriverFailurePublishesFaultAndPreservesLastCommittedWorld() {
        await using var run = new RunningSession(new ThrowingDriver());
        PlayerView view = await run.Session.WaitForViewAsync(view => view.Status == "faulted", run.Timeout.Token);
        await run.Completion.WaitAsync(run.Timeout.Token);
        Assert.Equal("B", view.Location);
        Assert.Equal(1000, view.ModelTimeMs);
        Assert.Null(view.Decision);
        Assert.Equal(2, run.Session.GetDevView().TransitionCount);
        Assert.Equal(new[] { "A", "B" }, view.Trajectory.Select(entry => entry.PlaceId));
        Assert.Contains("test driver failed", run.Session.GetDevView().Fault);
        Assert.Equal(503, run.Session.Submit("anything", "action.travel", "ab").StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwningRuleRejectsInvalidInjectedAnswerBeforeAnyCommit(bool extraField) {
        await using var run = new RunningSession(new InvalidDriver(extraField));
        PlayerView view = await run.Session.WaitForViewAsync(view => view.Status == "faulted", run.Timeout.Token);
        Assert.Equal("A", view.Location);
        Assert.Equal(0, view.ModelTimeMs);
        Assert.Equal(0, run.Session.GetDevView().TransitionCount);
        Assert.Empty(run.Session.GetDevView().Records);
    }

    private static void SubmitTo(FreePlaySession session, PlayerView view, string destination) {
        DecisionView decision = view.Decision!;
        DecisionExit exit = decision.Exits.Single(exit => exit.DestinationId == destination);
        Assert.Equal(202, session.Submit(decision.DecisionId, decision.ActionKind, exit.ExitId).StatusCode);
    }

    private sealed class ThrowingDriver : IPlayerDriver {
        private bool hasMoved;
        public ValueTask<PlayerDecision> DecideAsync(DecisionRequest request, CancellationToken cancellationToken) {
            if (hasMoved) { return ValueTask.FromException<PlayerDecision>(new InvalidOperationException("test driver failed")); }
            hasMoved = true;
            string exit = request.Observation.Exits.Single(exit => exit.DestinationId == "B").ExitId;
            return ValueTask.FromResult(new PlayerDecision(request.DecisionId, new Intent(ActionKinds.Travel, ExitId: exit)));
        }
    }

    private sealed class InvalidDriver(bool extraField) : IPlayerDriver {
        public ValueTask<PlayerDecision> DecideAsync(DecisionRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PlayerDecision(request.DecisionId, new Intent(ActionKinds.Travel,
                ExitId: extraField ? request.Observation.Exits[0].ExitId : "unknown", FreeText: extraField ? "unsupported" : null)));
    }

    private sealed class RunningSession : IAsyncDisposable {
        private readonly CancellationTokenSource application = new();
        public CancellationTokenSource Timeout { get; } = new(TimeSpan.FromSeconds(20));
        public FreePlaySession Session { get; }
        public Task Completion { get; }

        public RunningSession(IPlayerDriver? driver = null) {
            Session = new FreePlaySession(driver);
            Completion = Session.RunAsync(application.Token);
        }

        public Task<PlayerView> WaitAtAsync(string place) =>
            Session.WaitForViewAsync(view => view.Status == "waiting" && view.Location == place, Timeout.Token);

        public async Task StopAsync() {
            await application.CancelAsync();
            await Completion.WaitAsync(Timeout.Token);
        }

        public async ValueTask DisposeAsync() {
            await StopAsync();
            application.Dispose();
            Timeout.Dispose();
        }
    }
}
