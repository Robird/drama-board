using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.Journal;

public sealed class EventCauseTests
{
    [Fact]
    public void FromResolve_PreservesCandidateMetadata()
    {
        var candidateId = new EventCandidateId(17);
        var due = new ModelTime(23);

        EventCause cause = EventCause.FromResolve(11, candidateId, due, batchOrdinal: 5);

        Assert.Equal(CauseKind.ResolveBatch, cause.Kind);
        Assert.Equal(11, cause.SourceId);
        Assert.Equal(candidateId, cause.CandidateId);
        Assert.Equal(due, cause.Due);
        Assert.Equal(5, cause.BatchOrdinal);
    }

    [Fact]
    public void FromExternalInput_UsesDefaultCandidateMetadata()
    {
        EventCause cause = EventCause.FromExternalInput(batchOrdinal: 3);

        Assert.Equal(CauseKind.ExternalInput, cause.Kind);
        Assert.Equal(default, cause.SourceId);
        Assert.Equal(default, cause.CandidateId);
        Assert.Equal(default, cause.Due);
        Assert.Equal(3, cause.BatchOrdinal);
    }

    [Fact]
    public void Constructor_NegativeBatchOrdinal_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EventCause.FromResolve(1, new EventCandidateId(2), new ModelTime(3), batchOrdinal: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EventCause.FromExternalInput(batchOrdinal: -1));
    }

    [Fact]
    public void Constructor_ExternalInputWithCandidateMetadata_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            new EventCause(
                CauseKind.ExternalInput,
                sourceId: 1,
                new EventCandidateId(2),
                new ModelTime(3),
                batchOrdinal: 0));
    }
}