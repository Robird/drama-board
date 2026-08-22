using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;

namespace DramaBoard.FirstBoard.Demo.Live;

/// <summary>Transfers one already-installed authoritative commit to Presentation.</summary>
internal sealed record CommittedTransition
{
    public CommittedTransition(
        WorldVersion version,
        JournalBatch<FirstBoardFact> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (version.TransitionCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(version),
                "A committed transition must advance beyond the genesis prefix.");
        }

        Version = version;
        Batch = batch;
    }

    public WorldVersion Version { get; }

    public JournalBatch<FirstBoardFact> Batch { get; }
}
