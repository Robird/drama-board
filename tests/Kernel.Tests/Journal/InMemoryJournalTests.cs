using DramaBoard.Kernel.Journal;
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
        journal.Append(new DomainEvent<string>(timestamp, TestKind, "first"));

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            journal.Append(new DomainEvent<string>(timestamp, TestKind, "duplicate")));

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

    private static DomainEvent<string> Event(long modelTime, int microstep, string payload) =>
        new(new LogicalTimestamp(new ModelTime(modelTime), new Microstep(microstep)), TestKind, payload);
}