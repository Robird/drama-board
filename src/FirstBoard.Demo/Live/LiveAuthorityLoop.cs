using System.Threading.Channels;
using DramaBoard.Host;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.FirstBoard.Demo.Live;

/// <summary>Serially advances the only authoritative Kernel and publishes complete commits.</summary>
internal static class LiveAuthorityLoop
{
    public static async Task<HostRunResult<FirstBoardWorld>> RunAsync(
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> kernel,
        IJournalSink<FirstBoardFact> journal,
        ModelTime notAfter,
        ChannelWriter<CommittedTransition> writer,
        LiveSessionCoordination coordination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(coordination);

        int committedTransitionCount = 0;
        try
        {
            while (true)
            {
                StepStatus status = await kernel
                    .StepAsync(notAfter, cancellationToken)
                    .ConfigureAwait(false);
                if (status == StepStatus.Committed)
                {
                    committedTransitionCount = checked(committedTransitionCount + 1);
                    PublishInstalledCommit(kernel, journal, writer, coordination);
                    continue;
                }

                var result = new HostRunResult<FirstBoardWorld>(
                    kernel.World,
                    kernel.Version,
                    kernel.CurrentModelTime,
                    status,
                    committedTransitionCount);
                if (!writer.TryComplete())
                {
                    throw new InvalidOperationException(
                        "The committed-transition channel was completed before Authority terminated.");
                }

                return result;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A commit returned by StepAsync has already been published above. Cancellation is
            // normal session termination, so Presentation may drain the buffered prefix cleanly.
            writer.TryComplete();
            throw;
        }
        catch (Exception error)
        {
            coordination.Fail(error);
            writer.TryComplete(error);
            throw;
        }
    }

    private static void PublishInstalledCommit(
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> kernel,
        IJournalSink<FirstBoardFact> journal,
        ChannelWriter<CommittedTransition> writer,
        LiveSessionCoordination coordination)
    {
        WorldVersion version = kernel.Version;
        if (journal.LineageId != version.LineageId ||
            (long)journal.Batches.Count != version.TransitionCount ||
            journal.Batches.Count == 0)
        {
            throw new InvalidOperationException(
                "Authority cannot publish a commit that is not aligned with Journal history.");
        }

        JournalBatch<FirstBoardFact> batch = journal.Batches[^1];
        coordination.PublishCommitted(version);
        if (!writer.TryWrite(new CommittedTransition(version, batch)))
        {
            throw new InvalidOperationException(
                "The committed-transition channel rejected an installed authoritative commit.");
        }
    }
}
