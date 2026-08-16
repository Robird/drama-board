using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.Simulation;

public sealed class SimulationLoopTests
{
    [Fact]
    public void Run_CandidateBeforeCurrentTime_ThrowsInvalidOperationException()
    {
        var system = new OneShotSystem(sourceId: 1, candidateId: 1, due: 9, payload: "past", stateFlag: 1);
        var loop = new SimulationLoop<int, string, string>([system]);
        var journal = new InMemoryJournal<string>();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            loop.Run(0, new ModelTime(10), new ModelTime(20), journal));

        Assert.Contains("before current model time", exception.Message);
        Assert.Empty(journal.Events);
    }

    [Fact]
    public void Run_EmptyWorld_ReturnsImmediately()
    {
        var loop = new SimulationLoop<int, string, string>([]);
        var journal = new InMemoryJournal<string>();

        SimulationRunResult<int> result = loop.Run(
            initialWorld: 7,
            initialTime: new ModelTime(5),
            until: new ModelTime(100),
            journal);

        Assert.Equal(7, result.World);
        Assert.Equal(new ModelTime(5), result.CurrentTime);
        Assert.Equal(0, result.TimeAdvanceCount);
        Assert.Equal(0, result.ResolvedCandidateCount);
        Assert.Empty(journal.Events);
    }

    [Fact]
    public void Run_CandidateLaterThanUntil_DoesNotResolve()
    {
        var system = new OneShotSystem(sourceId: 1, candidateId: 1, due: 11, payload: "late", stateFlag: 1);
        var loop = new SimulationLoop<int, string, string>([system]);
        var journal = new InMemoryJournal<string>();

        SimulationRunResult<int> result = loop.Run(
            initialWorld: 0,
            initialTime: new ModelTime(0),
            until: new ModelTime(10),
            journal);

        Assert.Equal(0, result.World);
        Assert.Equal(new ModelTime(0), result.CurrentTime);
        Assert.Equal(0, result.ResolvedCandidateCount);
        Assert.Empty(journal.Events);
    }

    [Fact]
    public void Run_SameTimeCandidates_CommitsMonotonicDeterministicMicrosteps()
    {
        OneShotSystem[] systems =
        [
            new(sourceId: 2, candidateId: 1, due: 10, payload: "B", stateFlag: 2),
            new(sourceId: 1, candidateId: 2, due: 10, payload: "A", stateFlag: 1),
        ];
        var loop = new SimulationLoop<int, string, string>(systems);
        var journal = new InMemoryJournal<string>();

        SimulationRunResult<int> result = loop.Run(
            initialWorld: 0,
            initialTime: new ModelTime(0),
            until: new ModelTime(10),
            journal);

        Assert.Equal(["A", "B"], journal.Events.Select(domainEvent => domainEvent.Payload));
        Assert.Equal([0, 1], journal.Events.Select(domainEvent => domainEvent.Timestamp.Microstep.Value));
        Assert.True(journal.Events[0].Timestamp <= journal.Events[1].Timestamp);
        Assert.Equal(1, result.TimeAdvanceCount);
        Assert.Equal(2, result.ResolvedCandidateCount);
    }

    [Fact]
    public void Run_DifferentSourcesReuseCandidateId_ResolvesEachCandidateWithItsOwner()
    {
        OneShotSystem[] systems =
        [
            new(sourceId: 2, candidateId: 1, due: 10, payload: "B", stateFlag: 2),
            new(sourceId: 1, candidateId: 1, due: 10, payload: "A", stateFlag: 1),
        ];
        var loop = new SimulationLoop<int, string, string>(systems);
        var journal = new InMemoryJournal<string>();

        SimulationRunResult<int> result = loop.Run(
            initialWorld: 0,
            initialTime: new ModelTime(0),
            until: new ModelTime(10),
            journal);

        Assert.Equal(["A", "B"], journal.Events.Select(domainEvent => domainEvent.Payload));
        Assert.Equal(3, result.World);
        Assert.Equal(2, result.ResolvedCandidateCount);
    }

    private sealed class OneShotSystem : ISimSystem<int, string, string>
    {
        private readonly long _sourceId;
        private readonly EventCandidateId _candidateId;
        private readonly ModelTime _due;
        private readonly string _payload;
        private readonly int _stateFlag;

        public OneShotSystem(long sourceId, long candidateId, long due, string payload, int stateFlag)
        {
            _sourceId = sourceId;
            _candidateId = new EventCandidateId(candidateId);
            _due = new ModelTime(due);
            _payload = payload;
            _stateFlag = stateFlag;
        }

        public IReadOnlyList<EventCandidate<string>> ForecastNext(int world, ModelTime now) =>
            (world & _stateFlag) == 0
                ? [new EventCandidate<string>(_candidateId, _due, _sourceId, 0, _payload)]
                : [];

        public ResolveResult<int, string> Resolve(int world, EventCandidate<string> candidate) =>
            new(
                world | _stateFlag,
                [new UncommittedDomainEvent<string>("OneShotResolved", candidate.Payload)]);
    }
}
