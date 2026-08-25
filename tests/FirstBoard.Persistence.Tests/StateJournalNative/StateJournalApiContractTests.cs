using Atelia;
using Atelia.StateJournal;

namespace DramaBoard.FirstBoard.Persistence.Tests.StateJournalNative;

public sealed class StateJournalApiContractTests
{
    [Fact]
    public void TypedByteStringDeque_CommitAndReopen_RoundTrips()
    {
        using var directory = new TemporaryJournalDirectory();
        string repositoryPath = Path.Combine(directory.Path, "typed-byte-string");
        ByteString first = ByteString.Empty;
        ByteString second = new(new byte[] { 0x00, 0x7F, 0x80, 0xFF });

        using (Repository repository = Require(
                   Repository.Create(repositoryPath),
                   "create repository"))
        {
            Revision revision = Require(
                repository.CreateBranch("main"),
                "create main branch");
            DurableDict<string, DurableDeque<ByteString>> root =
                revision.CreateDict<string, DurableDeque<ByteString>>();
            DurableDeque<ByteString> values = revision.CreateDeque<ByteString>();
            values.PushBack(first);
            values.PushBack(second);
            root.Upsert("values", values);

            _ = Require(repository.Commit(root), "commit ByteString graph");
        }

        using Repository reopened = Require(
            Repository.Open(repositoryPath),
            "reopen repository");
        Revision reopenedRevision = Require(
            reopened.CheckoutBranch("main"),
            "checkout main branch");
        DurableDict<string, DurableDeque<ByteString>> reopenedRoot =
            Assert.IsAssignableFrom<DurableDict<string, DurableDeque<ByteString>>>(
                reopenedRevision.GraphRoot);
        DurableDeque<ByteString> reopenedValues =
            reopenedRoot.GetOrThrow("values")!;

        Assert.Equal(2, reopenedValues.Count);
        Assert.Equal(GetIssue.None, reopenedValues.GetAt(0, out ByteString actualFirst));
        Assert.Equal(first, actualFirst);
        Assert.Equal(GetIssue.None, reopenedValues.GetAt(1, out ByteString actualSecond));
        Assert.Equal(second, actualSecond);
    }

    [Fact]
    public void RepositoryDispose_InvalidatesOwnedRevisionRootAndLiveView()
    {
        using var directory = new TemporaryJournalDirectory();
        string repositoryPath = Path.Combine(directory.Path, "owned-lifetime");
        using Repository repository = Require(
            Repository.Create(repositoryPath),
            "create repository");
        Revision revision = Require(
            repository.CreateBranch("main"),
            "create main branch");
        DurableDict<string, int> root = revision.CreateDict<string, int>();
        root.Upsert("answer", 42);
        _ = Require(repository.Commit(root), "commit owned graph");
        IEnumerable<string> liveKeys = root.Keys;
        IEnumerator<string> liveEnumerator = liveKeys.GetEnumerator();
        Assert.True(liveEnumerator.MoveNext());

        repository.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _ = revision.GraphRoot);
        Assert.Throws<ObjectDisposedException>(() => _ = root.Count);
        Assert.Throws<ObjectDisposedException>(() => _ = liveKeys.GetEnumerator());
        Assert.Throws<ObjectDisposedException>(() => liveEnumerator.MoveNext());
        liveEnumerator.Dispose();
    }

    private static T Require<T>(AteliaResult<T> result, string operation)
        where T : notnull
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException(
                $"Failed to {operation}: {result.Error}");
        }

        return result.Value!;
    }
}
