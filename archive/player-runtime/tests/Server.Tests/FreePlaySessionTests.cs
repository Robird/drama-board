using DramaBoard.Player;
using DramaBoard.Protocol;
using DramaBoard.Server.FreePlay;
using DramaBoard.Server.Web;

namespace DramaBoard.Server.Tests;

public sealed class FreePlaySessionTests {
    [Fact]
    public async Task TwoMovesExecuteAutomaticallyWithoutWakingAtTheMiddlePlace() {
        var programmer = new RecordingProgrammer(new ScriptedDynamicProgrammer(
        [
            Respond(new MoveInstruction(new PassageHandle("ac")), new MoveInstruction(new PassageHandle("cd"))),
            Respond(),
        ]));
        await using var run = new RunningSession(programmer);

        PlayerView staying = await run.WaitStatusAsync("staying");

        DevView dev = run.Session.GetDevView();
        Assert.Equal("staying", staying.Status);
        Assert.Equal(3000, staying.ModelTimeMs);
        Assert.Equal(7, dev.TransitionCount);
        Assert.Equal(new[] { "A", "C", "D" }, staying.Trajectory.Select(entry => entry.PlaceId));
        Assert.Equal(new long[] { 0, 2000, 3000 }, staying.Trajectory.Select(entry => entry.ModelTimeMs));
        Assert.Equal(new string?[] { null, "ac", "cd" }, staying.Trajectory.Select(entry => entry.PassageId));
        Assert.Empty(staying.PendingProgram);
        Assert.Null(staying.Request);
        // The programmer is asked at A and at D only; the stop at C executes the buffered tail.
        Assert.Equal(2, programmer.Requests.Count);
        Assert.Equal(new[] { "A", "D" }, programmer.Requests.Select(request => request.PlaceId));
        Assert.All(programmer.Requests, request => Assert.Equal(ProgrammingReason.MissingInstruction, request.Reason));
    }

    [Fact]
    public async Task ThinkLoopConsumesTimeAndCarriesNotesIntoTheNextRequest() {
        var programmer = new RecordingProgrammer(new ScriptedDynamicProgrammer(
        [
            Respond(new MoveInstruction(new PassageHandle("ac")), new ThinkInstruction("note")),
            Respond(),
        ]));
        await using var run = new RunningSession(programmer);

        PlayerView staying = await run.WaitStatusAsync("staying");

        Assert.Equal(3000, staying.ModelTimeMs);
        Assert.Contains("[2000] note", staying.Workspace);
        ProgrammingRequest last = programmer.Requests[1];
        Assert.Equal(ProgrammingReason.ThinkCompleted, last.Reason);
        Assert.Contains("[2000] note", last.Workspace.Text);
    }

    [Fact]
    public async Task BlockedMoveWaitsForRevisionAndStayKeepsTheTailUnexecuted() {
        var programmer = new HoldingProgrammer(new ScriptedDynamicProgrammer(
        [
            Respond(new MoveInstruction(new PassageHandle("ac")), new MoveInstruction(new PassageHandle("bd"))),
            Respond(new StayInstruction(), new MoveInstruction(new PassageHandle("bd"))),
        ]));
        await using var run = new RunningSession(programmer);

        PlayerView first = await run.WaitWaitingAsync("A");
        Assert.Equal("MissingInstruction", first.Request!.Reason);
        programmer.Proceed();

        PlayerView blocked = await run.WaitWaitingAsync("C");
        Assert.Equal("MoveBlocked", blocked.Request!.Reason);
        Assert.False(string.IsNullOrWhiteSpace(blocked.Request.ReasonDetail));
        Assert.Equal("bd", blocked.PendingProgram.Single().Passage);
        Assert.Equal("bd", blocked.Request.RemainingProgram.Single().Passage);
        programmer.Proceed();

        PlayerView staying = await run.WaitStatusAsync("staying");
        Assert.Equal("C", staying.Location);
        Assert.Equal(2000, staying.ModelTimeMs);
        Assert.Equal(new[] { "A", "C" }, staying.Trajectory.Select(entry => entry.PlaceId));
        // The stay keeps the arrangement visible but the buffered move never executes.
        Assert.Equal("move", staying.PendingProgram.Single().Kind);
        Assert.Equal("bd", staying.PendingProgram.Single().Passage);
        Assert.Null(staying.Request);
    }

    [Fact]
    public async Task EmptyProgramViaWebSubmitEndsStayingWithoutAdvancingTime() {
        await using var run = new RunningSession();
        PlayerView waiting = await run.WaitWaitingAsync("A");

        SubmitResult result = run.Session.Submit(Input(waiting.Request!.ActivationId, waiting.Workspace));

        Assert.Equal(202, result.StatusCode);
        PlayerView staying = await run.WaitStatusAsync("staying");
        Assert.Equal(0, staying.ModelTimeMs);
        Assert.Equal(2, run.Session.GetDevView().TransitionCount);
        Assert.Null(staying.Request);
        Assert.Equal("staying", run.Session.GetPlayerView().Status);
    }

    [Fact]
    public async Task InvalidSubmissionsDoNotConsumeThePendingRequest() {
        await using var run = new RunningSession();
        PlayerView waiting = await run.WaitWaitingAsync("A");
        string activationId = waiting.Request!.ActivationId;

        Assert.Equal(409, run.Session.Submit(new ProgrammingInput("wrong-activation", [Move("ab")], "")).StatusCode);
        Assert.Equal(400, run.Session.Submit(Input(activationId, "", Move("zz"))).StatusCode);
        Assert.Equal(400, run.Session.Submit(Input(activationId, "", Think(new string('y', 4097)))).StatusCode);
        Assert.Equal(400, run.Session.Submit(Input(activationId, new string('x', 65_537))).StatusCode);

        for (int index = 0; index < 10; index++) {
            PlayerView view = run.Session.GetPlayerView();
            Assert.Equal("waiting", view.Status);
            Assert.Equal(activationId, view.Request!.ActivationId);
            Assert.Equal(0, view.ModelTimeMs);
            Assert.Equal(0, run.Session.GetDevView().TransitionCount);
        }

        Assert.Equal(202, run.Session.Submit(Input(activationId, "", Move("ab"))).StatusCode);
        PlayerView atB = await run.WaitWaitingAsync("B");
        Assert.Equal(1000, atB.ModelTimeMs);
        Assert.Equal(3, run.Session.GetDevView().TransitionCount);
    }

    [Fact]
    public async Task ConcurrentSubmissionsHaveExactlyOneWinner() {
        await using var run = new RunningSession();
        PlayerView waiting = await run.WaitWaitingAsync("A");
        string activationId = waiting.Request!.ActivationId;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<SubmitResult>[] submissions = Enumerable.Range(0, 12).Select(async _ => {
            await start.Task;
            return run.Session.Submit(Input(activationId, "", Move("ab")));
        }).ToArray();
        start.SetResult();
        SubmitResult[] results = await Task.WhenAll(submissions).WaitAsync(run.Timeout.Token);
        Assert.Single(results, result => result.StatusCode == 202);
        Assert.Equal(11, results.Count(result => result.StatusCode == 409));
        await run.WaitWaitingAsync("B");
        Assert.Equal(3, run.Session.GetDevView().TransitionCount);
    }

    [Fact]
    public async Task StayingSessionRejectsSubmissionsButServesReads() {
        await using var run = new RunningSession();
        PlayerView waiting = await run.WaitWaitingAsync("A");
        string activationId = waiting.Request!.ActivationId;
        Assert.Equal(202, run.Session.Submit(Input(activationId, "")).StatusCode);
        await run.WaitStatusAsync("staying");

        Assert.Equal(409, run.Session.Submit(Input(activationId, "", Move("ab"))).StatusCode);

        PlayerView after = run.Session.GetPlayerView();
        Assert.Equal("staying", after.Status);
        Assert.Equal(0, after.ModelTimeMs);
        Assert.Null(after.Request);
    }

    [Fact]
    public async Task WorkspaceClaimsCannotMoveTheActor() {
        await using var run = new RunningSession();
        PlayerView waiting = await run.WaitWaitingAsync("A");
        string activationId = waiting.Request!.ActivationId;

        Assert.Equal(202, run.Session.Submit(Input(activationId, "我已位于 D", Move("ab"))).StatusCode);

        PlayerView atB = await run.WaitWaitingAsync("B");
        Assert.Equal("B", atB.Location);
        Assert.Contains("我已位于 D", atB.Workspace);
        Assert.Equal(new[] { "A", "B" }, atB.Trajectory.Select(entry => entry.PlaceId));
    }

    [Fact]
    public async Task PriorRunActivationIsRejectedAndShutdownEndsPendingWaiter() {
        await using var oldRun = new RunningSession();
        PlayerView old = await oldRun.WaitWaitingAsync("A");
        await using var newRun = new RunningSession();
        PlayerView current = await newRun.WaitWaitingAsync("A");
        Assert.NotEqual(old.RunId, current.RunId);

        Assert.Equal(409, newRun.Session.Submit(Input(old.Request!.ActivationId, "", Move("ab"))).StatusCode);
        Assert.Equal(current.Request!.ActivationId, newRun.Session.GetPlayerView().Request!.ActivationId);

        await newRun.StopAsync();
        Assert.Equal("stopped", newRun.Session.GetPlayerView().Status);
        Assert.Equal(503, newRun.Session.Submit(Input(current.Request.ActivationId, "", Move("ab"))).StatusCode);
        Assert.Equal(0, newRun.Session.GetDevView().TransitionCount);
        Assert.Equal("waiting", oldRun.Session.GetPlayerView().Status);
    }

    [Fact]
    public async Task ProgrammerFailurePublishesFaultAndPreservesLastCommittedWorld() {
        await using var run = new RunningSession(new ScriptedDynamicProgrammer(
            [Respond(new MoveInstruction(new PassageHandle("ac")))]));
        PlayerView view = await run.Session.WaitForViewAsync(view => view.Status == "faulted", run.Timeout.Token);
        await run.Completion.WaitAsync(run.Timeout.Token);

        Assert.Equal("C", view.Location);
        Assert.Equal(2000, view.ModelTimeMs);
        Assert.Null(view.Request);
        Assert.Equal(3, run.Session.GetDevView().TransitionCount);
        Assert.Equal(new[] { "A", "C" }, view.Trajectory.Select(entry => entry.PlaceId));
        Assert.Contains("no program remaining", run.Session.GetDevView().Fault);
        Assert.Equal(503, run.Session.Submit(Input("anything", "", Move("ac"))).StatusCode);
    }

    [Fact]
    public async Task InjectedInvalidResponseFailsTheRunWithZeroCommits() {
        await using var run = new RunningSession(new ScriptedDynamicProgrammer(
            [request => new ProgrammingResponse(
                request.ActivationId, [new MoveInstruction(new PassageHandle("zz"))], request.Workspace)]));
        PlayerView view = await run.Session.WaitForViewAsync(view => view.Status == "faulted", run.Timeout.Token);
        await run.Completion.WaitAsync(run.Timeout.Token);

        Assert.Equal("A", view.Location);
        Assert.Equal(0, view.ModelTimeMs);
        Assert.Equal(0, run.Session.GetDevView().TransitionCount);
        Assert.Empty(run.Session.GetDevView().Records);
        Assert.Null(view.Request);
    }

    private static Func<ProgrammingRequest, ProgrammingResponse> Respond(params Instruction[] program) =>
        request => new ProgrammingResponse(request.ActivationId, program, request.Workspace);

    private static ProgrammingInput Input(string activationId, string workspace, params RawInstruction[] program) =>
        new(activationId, program, workspace);

    private static RawInstruction Move(string passage) => new("move", passage, null);
    private static RawInstruction Think(string note) => new("think", null, note);

    /// <summary>Counting decorator that records every frozen request passed to the inner programmer.</summary>
    private sealed class RecordingProgrammer : IDynamicProgrammer {
        private readonly IDynamicProgrammer _inner;

        public RecordingProgrammer(IDynamicProgrammer inner) {
            ArgumentNullException.ThrowIfNull(inner);
            _inner = inner;
        }

        public List<ProgrammingRequest> Requests { get; } = [];

        public async ValueTask<ProgrammingResponse> ProgramAsync(
            ProgrammingRequest request, CancellationToken cancellationToken) {
            Requests.Add(request);
            return await _inner.ProgramAsync(request, cancellationToken);
        }
    }

    /// <summary>Parks each programmer request until the test releases it, keeping transient waiting views observable.</summary>
    private sealed class HoldingProgrammer : IDynamicProgrammer {
        private readonly IDynamicProgrammer _inner;
        private long _permits;
        private TaskCompletionSource _released = NewSignal();

        public HoldingProgrammer(IDynamicProgrammer inner) {
            ArgumentNullException.ThrowIfNull(inner);
            _inner = inner;
        }

        public async ValueTask<ProgrammingResponse> ProgramAsync(
            ProgrammingRequest request, CancellationToken cancellationToken) {
            while (Interlocked.Decrement(ref _permits) < 0) {
                Interlocked.Increment(ref _permits);
                await _released.Task.WaitAsync(cancellationToken);
            }
            return await _inner.ProgramAsync(request, cancellationToken);
        }

        /// <summary>Releases the parked request, or pre-opens the gate for the next one.</summary>
        public void Proceed() {
            Interlocked.Increment(ref _permits);
            TaskCompletionSource released = Interlocked.Exchange(ref _released, NewSignal());
            released.TrySetResult();
        }

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class RunningSession : IAsyncDisposable {
        private readonly CancellationTokenSource application = new();
        public CancellationTokenSource Timeout { get; } = new(TimeSpan.FromSeconds(20));
        public FreePlaySession Session { get; }
        public Task Completion { get; }

        public RunningSession(IDynamicProgrammer? programmer = null) {
            Session = new FreePlaySession(programmer);
            Completion = Session.RunAsync(application.Token);
        }

        public Task<PlayerView> WaitWaitingAsync(string place) =>
            Session.WaitForViewAsync(view => view.Status == "waiting" && view.Location == place, Timeout.Token);

        public Task<PlayerView> WaitStatusAsync(string status) =>
            Session.WaitForViewAsync(view => view.Status == status, Timeout.Token);

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
