using System.Runtime.ExceptionServices;
using DramaBoard.Kernel.Simulation;

namespace DramaBoard.FirstBoard.Demo.Live;

internal readonly record struct LiveFrontierSnapshot(
    WorldVersion Committed,
    WorldVersion Presented)
{
    public long BacklogCount => checked(Committed.TransitionCount - Presented.TransitionCount);
}

/// <summary>Owns the synchronized committed and presented frontiers for one live lineage.</summary>
internal sealed class LiveSessionCoordination
{
    private readonly object _sync = new();
    private WorldVersion _committed;
    private WorldVersion _presented;
    private TaskCompletionSource _presentationChanged = NewSignal();
    private Exception? _failure;

    public LiveSessionCoordination(WorldVersion genesisVersion)
    {
        if (genesisVersion.TransitionCount != 0)
        {
            throw new ArgumentException(
                "The first live-session slice must start at an empty lineage.",
                nameof(genesisVersion));
        }

        _committed = genesisVersion;
        _presented = genesisVersion;
    }

    public WorldVersion CaptureCommitted()
    {
        lock (_sync)
        {
            ThrowIfFailed();
            return _committed;
        }
    }

    public LiveFrontierSnapshot Snapshot()
    {
        lock (_sync)
        {
            return new(_committed, _presented);
        }
    }

    public void PublishCommitted(WorldVersion next)
    {
        lock (_sync)
        {
            ThrowIfFailed();
            RequireSameLineage(next, _committed, nameof(next));
            if (next.TransitionCount != checked(_committed.TransitionCount + 1))
            {
                throw new InvalidOperationException(
                    "Committed frontier must advance by exactly one complete batch.");
            }

            _committed = next;
        }
    }

    public void AcknowledgePresented(WorldVersion next)
    {
        TaskCompletionSource signal;
        lock (_sync)
        {
            ThrowIfFailed();
            RequireSameLineage(next, _presented, nameof(next));
            if (next.TransitionCount != checked(_presented.TransitionCount + 1))
            {
                throw new InvalidOperationException(
                    "Presented frontier must advance by exactly one complete batch.");
            }

            if (next.TransitionCount > _committed.TransitionCount)
            {
                throw new InvalidOperationException(
                    "Presented frontier cannot advance beyond the committed frontier.");
            }

            _presented = next;
            signal = _presentationChanged;
            _presentationChanged = NewSignal();
        }

        signal.TrySetResult();
    }

    public async ValueTask WaitUntilPresentedAsync(
        WorldVersion target,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            Exception? failure;
            lock (_sync)
            {
                RequireSameLineage(target, _presented, nameof(target));
                if (target.TransitionCount > _committed.TransitionCount)
                {
                    throw new InvalidOperationException(
                        "Cannot await a presentation prefix that is not committed.");
                }

                if (_presented.TransitionCount >= target.TransitionCount)
                {
                    return;
                }

                failure = _failure;
                changed = _presentationChanged.Task;
            }

            if (failure is not null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }

            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Fail(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        TaskCompletionSource signal;
        lock (_sync)
        {
            if (_failure is not null)
            {
                return;
            }

            _failure = error;
            signal = _presentationChanged;
            _presentationChanged = NewSignal();
        }

        signal.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void RequireSameLineage(
        WorldVersion candidate,
        WorldVersion current,
        string parameterName)
    {
        if (candidate.LineageId != current.LineageId)
        {
            throw new ArgumentException(
                "Live-session frontiers must remain in one lineage.",
                parameterName);
        }
    }

    private void ThrowIfFailed()
    {
        if (_failure is not null)
        {
            ExceptionDispatchInfo.Capture(_failure).Throw();
        }
    }
}
