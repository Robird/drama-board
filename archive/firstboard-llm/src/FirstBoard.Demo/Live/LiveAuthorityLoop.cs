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
        ModelTime notAfter,
        ChannelWriter<CommittedTransition> writer,
        LiveSessionCoordination coordination,
        CancellationToken cancellationToken,
        Action<OccurrenceEvent<FirstBoardFact>>? onCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(coordination);

        int committedTransitionCount = 0;
        try
        {
            if (kernel.RecoverPending(cancellationToken))
            {
                committedTransitionCount++;
                PublishInstalledCommit(kernel, writer, coordination, onCompleted);
            }
            while (true)
            {
                StepStatus status = notAfter < kernel.CurrentModelTime
                    ? StepStatus.BoundaryReached
                    : await kernel
                    .StepAsync(notAfter, cancellationToken)
                    .ConfigureAwait(false);
                if (status == StepStatus.Committed)
                {
                    committedTransitionCount = checked(committedTransitionCount + 1);
                    PublishInstalledCommit(kernel, writer, coordination, onCompleted);
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
        ChannelWriter<CommittedTransition> writer,
        LiveSessionCoordination coordination,
        Action<OccurrenceEvent<FirstBoardFact>>? onCompleted)
    {
        OccurrenceCompletion<FirstBoardFact> completion = kernel.LastCompletion
            ?? throw new InvalidOperationException("A completed Step must provide its completion material.");
        WorldVersion version = completion.Cursor.Version;
        if (version != kernel.Version)
        {
            throw new InvalidOperationException(
                "Authority cannot present a completion that is not the installed Kernel boundary.");
        }

        OccurrenceEvent<FirstBoardFact> occurrence = completion.Event;
        var batch = new JournalBatch<FirstBoardFact>(
            occurrence.TargetInstant, occurrence.CauseKey, occurrence.Facts);
        onCompleted?.Invoke(occurrence);
        coordination.PublishCommitted(version);
        if (!writer.TryWrite(new CommittedTransition(version, batch)))
        {
            throw new InvalidOperationException(
                "The committed-transition channel rejected an installed authoritative commit.");
        }
    }
}
