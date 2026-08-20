using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.Journal;

public sealed class InMemoryJournalTests
{
    private static readonly EventKind TestKind = new("test.event", 1);

    [Fact]
    public void Append_EqualLogicalTimestamp_ThrowsInvalidOperationException()
    {
        var journal = new InMemoryJournal<string>();
        var timestamp = new LogicalTimestamp(new ModelTime(10), new Microstep(2));
        EventCause cause = EventCause.FromExternalInput(batchOrdinal: 0);
        journal.Append(new DomainEvent<string>(timestamp, cause, TestKind, "first"));

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            journal.Append(new DomainEvent<string>(timestamp, cause, TestKind, "duplicate")));

        Assert.Contains("strictly increasing", exception.Message);
        Assert.Single(journal.Events);
    }

    [Fact]
    public void Append_StrictlyIncreasingLogicalTimestamps_AcceptsEvents()
    {
        var journal = new InMemoryJournal<string>();
        journal.Append(Event(modelTime: 10, microstep: 2, payload: "first"));
        journal.Append(Event(modelTime: 10, microstep: 3, payload: "second"));
        journal.Append(Event(modelTime: 11, microstep: 0, payload: "third"));

        Assert.Equal(["first", "second", "third"], journal.Events.Select(domainEvent => domainEvent.Payload));
    }

    [Fact]
    public void AppendBatch_ValidBatch_AppendsEveryEventTogether()
    {
        var journal = new InMemoryJournal<string>();
        journal.Append(Event(modelTime: 9, microstep: 0, payload: "existing"));
        EventCause cause = EventCause.FromResolve(
            sourceId: 7,
            new EventCandidateId(11),
            new ModelTime(10),
            batchOrdinal: 1);

        journal.AppendBatch(
        [
            Event(modelTime: 10, microstep: 0, payload: "first", cause),
            Event(modelTime: 10, microstep: 1, payload: "second", cause),
            Event(modelTime: 10, microstep: 2, payload: "third", cause),
        ]);

        Assert.Equal(
            ["existing", "first", "second", "third"],
            journal.Events.Select(domainEvent => domainEvent.Payload));
    }

    [Fact]
    public void AppendBatch_EmptyBatch_IsNoOp()
    {
        var journal = new InMemoryJournal<string>();
        journal.Append(Event(modelTime: 9, microstep: 0, payload: "existing"));

        journal.AppendBatch([]);

        Assert.Equal(["existing"], journal.Events.Select(domainEvent => domainEvent.Payload));
    }

    [Fact]
    public void AppendBatch_InvalidTimestampInMiddle_LeavesJournalUnchanged()
    {
        var journal = new InMemoryJournal<string>();
        journal.Append(Event(modelTime: 9, microstep: 0, payload: "existing"));
        EventCause cause = EventCause.FromExternalInput(batchOrdinal: 1);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => journal.AppendBatch(
        [
            Event(modelTime: 10, microstep: 0, payload: "would-be-prefix", cause),
            Event(modelTime: 10, microstep: 0, payload: "duplicate", cause),
            Event(modelTime: 10, microstep: 1, payload: "would-be-suffix", cause),
        ]));

        Assert.Contains("strictly increasing", exception.Message);
        Assert.Equal(["existing"], journal.Events.Select(domainEvent => domainEvent.Payload));
    }

    [Fact]
    public void AppendBatch_DifferentCauseInMiddle_LeavesJournalUnchanged()
    {
        var journal = new InMemoryJournal<string>();
        journal.Append(Event(modelTime: 9, microstep: 0, payload: "existing"));
        EventCause expectedCause = EventCause.FromExternalInput(batchOrdinal: 1);
        EventCause otherCause = EventCause.FromExternalInput(batchOrdinal: 2);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => journal.AppendBatch(
        [
            Event(modelTime: 10, microstep: 0, payload: "would-be-prefix", expectedCause),
            Event(modelTime: 10, microstep: 1, payload: "different-cause", otherCause),
            Event(modelTime: 10, microstep: 2, payload: "would-be-suffix", expectedCause),
        ]));

        Assert.Contains("same cause", exception.Message);
        Assert.Equal(["existing"], journal.Events.Select(domainEvent => domainEvent.Payload));
    }

    [Fact]
    public void AppendBatch_NullEventInMiddle_LeavesJournalUnchanged()
    {
        var journal = new InMemoryJournal<string>();
        journal.Append(Event(modelTime: 9, microstep: 0, payload: "existing"));
        EventCause cause = EventCause.FromExternalInput(batchOrdinal: 1);

        ArgumentException exception = Assert.Throws<ArgumentException>(() => journal.AppendBatch(
        [
            Event(modelTime: 10, microstep: 0, payload: "would-be-prefix", cause),
            null!,
            Event(modelTime: 10, microstep: 2, payload: "would-be-suffix", cause),
        ]));

        Assert.Contains("null events", exception.Message);
        Assert.Equal(["existing"], journal.Events.Select(domainEvent => domainEvent.Payload));
    }

    private static DomainEvent<string> Event(
        long modelTime,
        int microstep,
        string payload,
        EventCause? cause = null) =>
    new(
        new LogicalTimestamp(new ModelTime(modelTime), new Microstep(microstep)),
        cause ?? EventCause.FromExternalInput(batchOrdinal: microstep),
        TestKind,
        payload);
}
