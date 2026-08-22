using DramaBoard.Protocol;

namespace DramaBoard.FirstBoard.Demo.Tests;

internal sealed class FakeTerminalUi : ITerminalUi
{
    private readonly object _sync = new();
    private readonly List<PresentationCue> _cues = [];
    private readonly List<DeveloperOverlay> _overlays = [];
    private readonly List<TerminalStatus> _statuses = [];
    private readonly List<DecisionRequest> _prompts = [];
    private readonly List<string> _errors = [];
    private TaskCompletionSource _promptChanged = NewSignal();
    private TaskCompletionSource _errorChanged = NewSignal();
    private TaskCompletionSource _outputChanged = NewSignal();
    private TaskCompletionSource _readChanged = NewSignal();
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

    public IReadOnlyList<PresentationCue> Cues => Snapshot(_cues);

    public IReadOnlyList<DeveloperOverlay> DeveloperOverlays => Snapshot(_overlays);

    public IReadOnlyList<TerminalStatus> Statuses => Snapshot(_statuses);

    public ValueTask ShowCueAsync(
        PresentationCue cue,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cue);
        cancellationToken.ThrowIfCancellationRequested();
        Record(_cues, cue);
        return ValueTask.CompletedTask;
    }

    public ValueTask ShowDeveloperOverlayAsync(
        DeveloperOverlay overlay,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        cancellationToken.ThrowIfCancellationRequested();
        Record(_overlays, overlay);
        return ValueTask.CompletedTask;
    }

    public ValueTask ShowStatusAsync(
        TerminalStatus status,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(status);
        cancellationToken.ThrowIfCancellationRequested();
        Record(_statuses, status);
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
        TaskCompletionSource changed;
        lock (_sync)
        {
            if (_activeRead is not null)
            {
                throw new InvalidOperationException("The fake terminal already has an active read.");
            }

            _activeRead = pending;
            changed = _readChanged;
            _readChanged = NewSignal();
        }

        pending.Registration = cancellationToken.Register(
            () => CancelRead(pending, cancellationToken));
        changed.TrySetResult();
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

    public async Task WaitForActiveReadAsync(DecisionId decisionId)
    {
        while (true)
        {
            Task changed;
            lock (_sync)
            {
                if (_activeRead?.DecisionId == decisionId)
                {
                    return;
                }

                changed = _readChanged.Task;
            }

            await changed.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    public Task WaitForCueCountAsync(int count) =>
        WaitForOutputCountAsync(() => _cues.Count, count);

    public Task WaitForStatusCountAsync(int count) =>
        WaitForOutputCountAsync(() => _statuses.Count, count);

    private IReadOnlyList<T> Snapshot<T>(IEnumerable<T> values)
    {
        lock (_sync)
        {
            return Array.AsReadOnly(values.ToArray());
        }
    }

    private void Record<T>(ICollection<T> values, T value)
    {
        TaskCompletionSource changed;
        lock (_sync)
        {
            values.Add(value);
            changed = _outputChanged;
            _outputChanged = NewSignal();
        }

        changed.TrySetResult();
    }

    private async Task WaitForOutputCountAsync(Func<int> count, int expected)
    {
        while (true)
        {
            Task changed;
            lock (_sync)
            {
                if (count() >= expected)
                {
                    return;
                }

                changed = _outputChanged.Task;
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
