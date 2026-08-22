using DramaBoard.Protocol;

namespace DramaBoard.FirstBoard.Demo.Tests;

internal sealed class FakeTerminalUi : ITerminalUi
{
    private readonly object _sync = new();
    private readonly List<DecisionRequest> _prompts = [];
    private readonly List<string> _errors = [];
    private TaskCompletionSource _promptChanged = NewSignal();
    private TaskCompletionSource _errorChanged = NewSignal();
    private PendingRead? _activeRead;

    public IReadOnlyList<DecisionRequest> Prompts
    {
        get
        {
            lock (_sync)
            {
                return Array.AsReadOnly(_prompts.ToArray());
            }
        }
    }

    public IReadOnlyList<string> Errors
    {
        get
        {
            lock (_sync)
            {
                return Array.AsReadOnly(_errors.ToArray());
            }
        }
    }

    public ValueTask ShowCueAsync(
        PresentationCue cue,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cue);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask ShowDeveloperOverlayAsync(
        DeveloperOverlay overlay,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask ShowStatusAsync(
        TerminalStatus status,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(status);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask ShowPromptAsync(
        DecisionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource changed;
        lock (_sync)
        {
            _prompts.Add(request);
            changed = _promptChanged;
            _promptChanged = NewSignal();
        }

        changed.TrySetResult();
        return ValueTask.CompletedTask;
    }

    public ValueTask ShowInputErrorAsync(
        string message,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource changed;
        lock (_sync)
        {
            _errors.Add(message);
            changed = _errorChanged;
            _errorChanged = NewSignal();
        }

        changed.TrySetResult();
        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> ReadCommandAsync(
        DecisionId decisionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pending = new PendingRead(decisionId);
        lock (_sync)
        {
            if (_activeRead is not null)
            {
                throw new InvalidOperationException("The fake terminal already has an active read.");
            }

            _activeRead = pending;
        }

        pending.Registration = cancellationToken.Register(
            () => CancelRead(pending, cancellationToken));
        return AwaitReadAsync(pending);
    }

    public bool TrySubmit(DecisionId decisionId, string? command)
    {
        PendingRead? pending;
        lock (_sync)
        {
            pending = _activeRead;
            if (pending is null || pending.DecisionId != decisionId)
            {
                return false;
            }

            _activeRead = null;
        }

        return pending.Completion.TrySetResult(command);
    }

    public async Task WaitForPromptCountAsync(int count)
    {
        while (true)
        {
            Task changed;
            lock (_sync)
            {
                if (_prompts.Count >= count)
                {
                    return;
                }

                changed = _promptChanged.Task;
            }

            await changed.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    public async Task WaitForErrorCountAsync(int count)
    {
        while (true)
        {
            Task changed;
            lock (_sync)
            {
                if (_errors.Count >= count)
                {
                    return;
                }

                changed = _errorChanged.Task;
            }

            await changed.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private async ValueTask<string?> AwaitReadAsync(PendingRead pending)
    {
        try
        {
            return await pending.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            pending.Registration.Dispose();
        }
    }

    private void CancelRead(PendingRead pending, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_activeRead, pending))
            {
                _activeRead = null;
            }
        }

        pending.Completion.TrySetCanceled(cancellationToken);
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class PendingRead(DecisionId decisionId)
    {
        public DecisionId DecisionId { get; } = decisionId;

        public TaskCompletionSource<string?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenRegistration Registration { get; set; }
    }
}
