using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.Journal;

public sealed class InMemoryJournalTests
{
    [Fact]
    public void JournalBatch_CopiesNonEmptyFactsAndExposesOneSharedHeader()
    {
        string[] inputFacts = ["first", "second"];
        var instant = new LogicalInstant(new ModelTime(10), causalOrdinal: 3);
        CandidateKey causeKey = CandidateKey.FromUtf8("timer:1");

        var batch = new JournalBatch<string>(instant, causeKey, inputFacts);
        inputFacts[0] = "mutated";

        Assert.Equal(instant, batch.Instant);
        Assert.Equal(causeKey, batch.CauseKey);
        Assert.Equal(["first", "second"], batch.Facts);
    }

    [Fact]
    public void JournalBatch_EmptyNullOrContainingNullFactsAreRejected()
    {
        LogicalInstant instant = Instant(10, 0);
        CandidateKey key = CandidateKey.FromUtf8("key");

        Assert.Throws<ArgumentNullException>(() =>
            new JournalBatch<string>(instant, null!, ["fact"]));
        Assert.Throws<ArgumentNullException>(() =>
            new JournalBatch<string>(instant, key, null!));
        Assert.Throws<ArgumentException>(() =>
            new JournalBatch<string>(instant, key, []));
        Assert.Throws<ArgumentException>(() =>
            new JournalBatch<string>(instant, key, ["fact", null!]));
    }

    [Fact]
    public void AppendBatch_StoresOnlyWholeBatchesAndAllowsManyOrderedFacts()
    {
        var journal = new InMemoryJournal<string>(lineageId: 1);
        var batch = new JournalBatch<string>(
            Instant(10, 0),
            CandidateKey.FromUtf8("multi"),
            ["a", "b", "c"]);

        journal.AppendBatch(batch);

        Assert.Single(journal.Batches);
        Assert.Same(batch, journal.Batches[0]);
        Assert.Equal(["a", "b", "c"], journal.Batches[0].Facts);
        Assert.Null(typeof(IJournalSink<string>).GetProperty("Events"));
        Assert.Null(typeof(IJournalSink<string>).GetMethod("Append"));
    }

    [Fact]
    public void AppendBatch_RequiresStrictlyIncreasingBatchInstants()
    {
        var journal = new InMemoryJournal<string>(lineageId: 1);
        JournalBatch<string> first = Batch(10, 1, "first");
        journal.AppendBatch(first);

        Assert.Throws<InvalidOperationException>(() =>
            journal.AppendBatch(Batch(10, 1, "equal")));
        Assert.Throws<InvalidOperationException>(() =>
            journal.AppendBatch(Batch(10, 0, "earlier")));

        Assert.Single(journal.Batches);
        Assert.Same(first, journal.Batches[0]);
    }

    [Fact]
    public void ForkPrefix_CopiesOnlyCompleteBatchesIntoIndependentJournal()
    {
        var journal = new InMemoryJournal<string>(lineageId: 1);
        journal.AppendBatch(Batch(10, 0, "a", "b"));
        journal.AppendBatch(Batch(10, 1, "c"));

        InMemoryJournal<string> prefix = journal.ForkPrefix(1, newLineageId: 2);
        prefix.AppendBatch(Batch(11, 0, "fork-only"));

        Assert.Equal(2, journal.Batches.Count);
        Assert.Equal(2, journal.Batches[0].Facts.Count);
        Assert.Equal(2, prefix.Batches.Count);
        Assert.Equal(["a", "b"], prefix.Batches[0].Facts);
        Assert.Equal(["fork-only"], prefix.Batches[1].Facts);
        Assert.Equal(2, prefix.LineageId);
        Assert.Throws<ArgumentException>(() => journal.ForkPrefix(1, newLineageId: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => journal.ForkPrefix(-1, newLineageId: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => journal.ForkPrefix(3, newLineageId: 2));
    }

    private static JournalBatch<string> Batch(
        long modelTime,
        long causalOrdinal,
        params string[] facts) =>
        new(
            Instant(modelTime, causalOrdinal),
            CandidateKey.FromUtf8($"cause:{modelTime}:{causalOrdinal}"),
            facts);

    private static LogicalInstant Instant(long modelTime, long causalOrdinal) =>
        new(new ModelTime(modelTime), causalOrdinal);
}
