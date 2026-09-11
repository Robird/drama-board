using System.Threading.Channels;
using DramaBoard.Host;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Time;

namespace DramaBoard.FirstBoard.Demo.Tests;

public sealed class LiveAuthorityLoopTests
{
    [Fact]
    public async Task UnreadChannelDoesNotThrottleCompleteCommittedBatches()
    {
        TestAuthority authority = CreateAuthority();

        HostRunResult<FirstBoardWorld> result = await LiveAuthorityLoop.RunAsync(
            authority.Kernel,

            new ModelTime(1),
            authority.Channel.Writer,
            authority.Coordination,
            CancellationToken.None);

        Assert.Equal(StepStatus.Exhausted, result.Status);
        Assert.Equal(2, result.CommittedTransitionCount);
        Assert.Equal(2, authority.Coordination.Snapshot().Committed.TransitionCount);
        Assert.Equal(0, authority.Coordination.Snapshot().Presented.TransitionCount);
        CommittedTransition[] transitions = await ReadAllAsync(authority.Channel.Reader);
        Assert.Equal([1L, 2L], transitions.Select(value => value.Version.TransitionCount));
        Assert.All(transitions, transition => Assert.NotEmpty(transition.Batch.Facts));
    }

    [Fact]
    public async Task CancellationRaisedDuringAppendStillPublishesInstalledCommit()
    {
        using var cancellation = new CancellationTokenSource();
        TestAuthority authority = CreateAuthority(afterAppend: cancellation.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LiveAuthorityLoop.RunAsync(
                authority.Kernel,

                new ModelTime(1),
                authority.Channel.Writer,
                authority.Coordination,
                cancellation.Token));

        CommittedTransition transition = Assert.Single(
            await ReadAllAsync(authority.Channel.Reader));
        Assert.Equal(authority.Kernel.Version, transition.Version);
        Assert.Equal(1, transition.Version.TransitionCount);
        Assert.Equal(1, authority.Coordination.Snapshot().Committed.TransitionCount);
    }

    [Fact]
    public async Task PlanFailurePublishesNoTransitionAndFaultsChannel()
    {
        var expected = new ArithmeticException("plan failed");
        TestAuthority authority = CreateAuthority(planFailure: expected);

        ArithmeticException actual = await Assert.ThrowsAsync<ArithmeticException>(() =>
            LiveAuthorityLoop.RunAsync(
                authority.Kernel,

                new ModelTime(1),
                authority.Channel.Writer,
                authority.Coordination,
                CancellationToken.None));

        Assert.Same(expected, actual);
        Assert.False(authority.Channel.Reader.TryRead(out _));
        Assert.Equal(0, authority.Coordination.Snapshot().Committed.TransitionCount);
        await Assert.ThrowsAsync<ArithmeticException>(async () =>
            await authority.Channel.Reader.Completion);
    }

    [Fact]
    public async Task UncertainPublicationIsNeverExposedAsCommittedTransition()
    {
        var expected = new ArithmeticException("append outcome uncertain");
        TestAuthority authority = CreateAuthority(throwAfterAppend: expected);

        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LiveAuthorityLoop.RunAsync(
                authority.Kernel,

                new ModelTime(1),
                authority.Channel.Writer,
                authority.Coordination,
                CancellationToken.None));

        Assert.Same(expected, actual.InnerException);
        Assert.Single(authority.History.CompletedEvents);
        Assert.Equal(0, authority.Kernel.Version.TransitionCount);
        Assert.Equal(0, authority.Coordination.Snapshot().Committed.TransitionCount);
        Assert.False(authority.Channel.Reader.TryRead(out _));
    }

    [Fact]
    public async Task BoundaryAndExhaustionCompleteChannelNormally()
    {
        TestAuthority boundary = CreateAuthority();
        HostRunResult<FirstBoardWorld> boundaryResult = await LiveAuthorityLoop.RunAsync(
            boundary.Kernel,

            new ModelTime(0),
            boundary.Channel.Writer,
            boundary.Coordination,
            CancellationToken.None);
        Assert.Equal(StepStatus.BoundaryReached, boundaryResult.Status);
        Assert.Single(await ReadAllAsync(boundary.Channel.Reader));

        TestAuthority exhausted = CreateAuthority(noCandidates: true);
        HostRunResult<FirstBoardWorld> exhaustedResult = await LiveAuthorityLoop.RunAsync(
            exhausted.Kernel,

            new ModelTime(1),
            exhausted.Channel.Writer,
            exhausted.Coordination,
            CancellationToken.None);
        Assert.Equal(StepStatus.Exhausted, exhaustedResult.Status);
        Assert.Empty(await ReadAllAsync(exhausted.Channel.Reader));
    }

    private static TestAuthority CreateAuthority(
        Action? afterAppend = null,
        Exception? throwAfterAppend = null,
        Exception? planFailure = null,
        bool noCandidates = false)
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 71);
        FirstBoardWorld world = instance.CreateInitialWorld();
        var journal = new HookedHistory(
            world,
            afterAppend,
            throwAfterAppend);
        var reducer = new FirstBoardReducer(instance.Graph);
        var version = journal.Cursor.Version;
        var kernel = new SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact>(
            journal,
            new SimulationRules(world.WorldSeed, maxTransitionsPerModelTime: 100),
            noCandidates ? [] : [new TwoStepWaitRule(planFailure)],
            reducer.Apply,
            reducer.Validate);
        Channel<CommittedTransition> channel = Channel.CreateUnbounded<CommittedTransition>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });
        return new(
            kernel,
            journal,
            channel,
            new LiveSessionCoordination(version));
    }

    private static async Task<CommittedTransition[]> ReadAllAsync(
        ChannelReader<CommittedTransition> reader)
    {
        var values = new List<CommittedTransition>();
        await foreach (CommittedTransition value in reader.ReadAllAsync())
        {
            values.Add(value);
        }

        return [.. values];
    }

    private sealed record WaitTestCandidate(bool Completing) : BoardCandidate;

    private sealed class TwoStepWaitRule(Exception? planFailure) :
        IOccurrenceRule<FirstBoardWorld, BoardCandidate, FirstBoardFact>
    {
        public IReadOnlyList<OccurrenceCandidate<BoardCandidate>> Forecast(
            FirstBoardWorld world,
            SimulationRules rules)
        {
            BoardActor alice = world.Actor(BoardIds.Alice);
            if (alice.DecisionSequence == 0 && alice.Activity is null)
            {
                return
                [
                    Candidate(
                        "test.wait.start",
                        world.Now,
                        new WaitTestCandidate(Completing: false)),
                ];
            }

            if (alice.DecisionSequence == 1 && alice.Activity is BoardWaitActivity activity)
            {
                return
                [
                    Candidate(
                        "test.wait.complete",
                        activity.Due,
                        new WaitTestCandidate(Completing: true)),
                ];
            }

            return [];
        }

        public ValueTask<TransitionDraft<FirstBoardFact>> PlanSelectedAsync(
            FirstBoardWorld world,
            OccurrenceCandidate<BoardCandidate> winner,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (planFailure is not null)
            {
                return ValueTask.FromException<TransitionDraft<FirstBoardFact>>(planFailure);
            }

            var candidate = Assert.IsType<WaitTestCandidate>(winner.Data);
            FirstBoardFact fact = candidate.Completing
                ? new GameBoardFact(new ActorWaitedEvent(BoardIds.Alice))
                : new GameBoardFact(new ActorWaitStartedEvent(BoardIds.Alice, new ModelTime(1)));
            return ValueTask.FromResult(new TransitionDraft<FirstBoardFact>([fact]));
        }

        private static OccurrenceCandidate<BoardCandidate> Candidate(
            string key,
            ModelTime due,
            BoardCandidate data) =>
            new(new CandidateKey(key), new CandidateDue(due), data);
    }

    private sealed class HookedHistory : IOccurrenceHistory<FirstBoardWorld, FirstBoardFact>
    {
        private readonly InMemoryOccurrenceHistory<FirstBoardWorld, FirstBoardFact> _inner;
        private readonly Action? _afterAppend;
        private readonly Exception? _throwAfterAppend;

        public HookedHistory(FirstBoardWorld state, Action? afterAppend, Exception? throwAfterAppend)
        {
            _inner = FirstBoardScenario.CreateMemoryHistory(state);
            _afterAppend = afterAppend;
            _throwAfterAppend = throwAfterAppend;
        }

        public FirstBoardWorld State => _inner.State;
        public KernelCursor Cursor => _inner.Cursor;
        public OccurrenceEvent<FirstBoardFact>? PendingEvent => _inner.PendingEvent;
        public IReadOnlyList<OccurrenceEvent<FirstBoardFact>> CompletedEvents => _inner.CompletedEvents;
        public void CommitEvent(OccurrenceEvent<FirstBoardFact> occurrence) => _inner.CommitEvent(occurrence);
        public void CommitState(FirstBoardWorld nextState, KernelCursor nextCursor)
        {
            _inner.CommitState(nextState, nextCursor);
            _afterAppend?.Invoke();
            if (_throwAfterAppend is not null)
            {
                throw _throwAfterAppend;
            }
        }
    }

    private sealed record TestAuthority(
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> Kernel,
        HookedHistory History,
        Channel<CommittedTransition> Channel,
        LiveSessionCoordination Coordination);
}
