namespace DramaBoard.FirstBoard.Demo.Tests;

internal sealed class ManualPresentationPacer : IPresentationPacer
{
    private readonly object _sync = new();
    private readonly Queue<TaskCompletionSource> _pending = [];
    private TaskCompletionSource _requested = NewSignal();
    private int _requestCount;

    public ValueTask PaceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = NewSignal();
        TaskCompletionSource changed;
        lock (_sync)
        {
            _pending.Enqueue(completion);
            _requestCount = checked(_requestCount + 1);
            changed = _requested;
            _requested = NewSignal();
        }

        changed.TrySetResult();
        return AwaitAsync(completion, cancellationToken);
    }

    public async Task WaitForRequestCountAsync(int count)
    {
        while (true)
        {
            Task changed;
            lock (_sync)
            {
                if (_requestCount >= count)
                {
                    return;
                }

                changed = _requested.Task;
            }

            await changed.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    public void ReleaseNext()
    {
        TaskCompletionSource completion;
        lock (_sync)
        {
            completion = _pending.Count > 0
                ? _pending.Dequeue()
                : throw new InvalidOperationException("No presentation pace is pending.");
        }

        completion.TrySetResult();
    }

    private static async ValueTask AwaitAsync(
        TaskCompletionSource completion,
        CancellationToken cancellationToken) =>
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
