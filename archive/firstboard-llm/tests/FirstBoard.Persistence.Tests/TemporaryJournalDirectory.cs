namespace DramaBoard.FirstBoard.Persistence.Tests;

internal sealed class TemporaryJournalDirectory : IDisposable
{
    public TemporaryJournalDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "DramaBoard.FirstBoard.Persistence.Tests",
            Guid.NewGuid().ToString("N"));
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
