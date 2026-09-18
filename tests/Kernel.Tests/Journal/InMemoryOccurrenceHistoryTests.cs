using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.Journal;

public sealed class InMemoryOccurrenceHistoryTests {
    [Fact]
    public void EventPublishesPendingWithoutAdvancingStateAndStateCompletesIt() {
        var cursor = new KernelCursor(new(3, 0), new(40), null, null);
        var history = new InMemoryOccurrenceHistory<string, int>("old", cursor);
        var occurrence = new OccurrenceEvent<int>(CandidateKey.FromUtf8("event"), new(new(40), 0), [1, 2]);
        history.CommitEvent(occurrence);
        Assert.Same(occurrence, history.PendingEvent);
        Assert.Equal("old", history.State);
        Assert.Equal(cursor, history.Cursor);
        Assert.Empty(history.CompletedEvents);
        KernelCursor next = cursor.Advance(occurrence.CauseKey, occurrence.TargetInstant);
        history.CommitState("new", next);
        Assert.Equal("new", history.State);
        Assert.Equal(next, history.Cursor);
        Assert.Null(history.PendingEvent);
        Assert.Same(occurrence, Assert.Single(history.CompletedEvents));
    }

    [Fact]
    public void RejectsDoubleEventStateWithoutEventAndMismatchedCursor() {
        var cursor = new KernelCursor(new(3, 0), ModelTime.Zero, null, null);
        var history = new InMemoryOccurrenceHistory<int, int>(1, cursor);
        var occurrence = new OccurrenceEvent<int>(CandidateKey.FromUtf8("e"), new(ModelTime.Zero, 0), [1]);
        Assert.Throws<InvalidOperationException>(() => history.CommitState(2, cursor));
        history.CommitEvent(occurrence);
        Assert.Throws<InvalidOperationException>(() => history.CommitEvent(occurrence));
        Assert.Throws<ArgumentException>(() => history.CommitState(2, cursor));
        Assert.Equal(1, history.State);
        Assert.Equal(cursor, history.Cursor);
        Assert.Same(occurrence, history.PendingEvent);
    }

    [Fact]
    public void CompletedEventCollectionIsAnOracleNotRequiredHistoryForLoadedCursor() {
        var cursor = new KernelCursor(new(3, 99), ModelTime.Zero, new(new(50), 0), CandidateKey.FromUtf8("previous"));
        var history = new InMemoryOccurrenceHistory<int, int>(123, cursor);
        Assert.Empty(history.CompletedEvents);
        Assert.Equal(99, history.Cursor.Version.TransitionCount);
        var occurrence = new OccurrenceEvent<int>(CandidateKey.FromUtf8("next"), new(new(51), 0), [1]);
        history.CommitEvent(occurrence);
        history.CommitState(124, cursor.Advance(occurrence.CauseKey, occurrence.TargetInstant));
        Assert.Equal(100, history.Cursor.Version.TransitionCount);
        Assert.Single(history.CompletedEvents);
    }

    [Fact]
    public void EventOwnsFactArrayAndRejectsEmptyOrNullFacts() {
        int[] facts = [1, 2];
        var key = CandidateKey.FromUtf8("e");
        var instant = new LogicalInstant(ModelTime.Zero, 0);
        var occurrence = new OccurrenceEvent<int>(key, instant, facts);
        facts[0] = 99;
        Assert.Equal([1, 2], occurrence.Facts);
        Assert.Throws<NotSupportedException>(() => ((IList<int>)occurrence.Facts)[0] = 9);
        Assert.Throws<ArgumentException>(() => new OccurrenceEvent<int>(key, instant, []));
        Assert.Throws<ArgumentException>(() => new OccurrenceEvent<string>(key, instant, [null!]));
    }
}
