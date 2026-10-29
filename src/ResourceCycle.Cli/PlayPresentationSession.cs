using DramaBoard.ResourceCycle;

internal enum PlayDeliveryPhase { Advancing, ControllerInput }

/// <summary>Transient Console delivery state. All character material comes from committed Contexts.</summary>
internal sealed class PlayPresentationSession(PlayPresentation presentation, ConsoleMetaCommands metaCommands,
    TextWriter output, Action<string>? writeBoundary = null) {
    internal const string OutOfBandStart = "┄┄ Console 带外信息 ┄┄";
    internal const string OutOfBandEnd = "┄┄ Console 带外信息结束 ┄┄";
    private sealed record Delivery(long ItemSequence, bool Ended);
    private readonly Dictionary<(string Actor, long Context), Delivery> _deliveries = [];
    private (string Actor, long Context)? _active;
    public CycleViewSnapshot? Baseline { get; private set; }
    public string? StartupText { get; set; }
    public bool TerminalDelivered { get; private set; }
    public long ConsumedItemSequence => _active is { } key && _deliveries.TryGetValue(key, out var delivery)
        ? delivery.ItemSequence : 0;

    public void EnterInput(CharacterInteractionState state) {
        if (state.CurrentContext?.Status != CharacterContextStatus.Open) {
            throw new InvalidOperationException("Only an open saved context can request Console input.");
        }
        ShowFullOrAppend(state);
    }

    public void Reply(string text) {
        output.WriteLine();
        output.WriteLine(OutOfBandStart);
        output.WriteLine(text);
        output.WriteLine(OutOfBandEnd);
        output.Flush();
    }

    public void CloseSession(string text = "Console 会话关闭；角色上下文保持已提交状态。") => Reply(text);

    public void Commit(CharacterInteractionState state) {
        if (state.CurrentContext is not { } context) { return; }
        var key = Identity(state);
        if (context.Status == CharacterContextStatus.Terminal) {
            // Terminal replaces the previous context even when the character did not release it.
            foreach (var old in _deliveries.Where(pair => pair.Key.Actor == state.ActorId &&
                pair.Key != key && !pair.Value.Ended).ToArray()) {
                WriteEnd(old.Key);
            }
            ShowFullOrAppend(state);
            return;
        }
        if (context.Status == CharacterContextStatus.Closed) {
            if (_deliveries.TryGetValue(key, out var delivery) && !delivery.Ended) { WriteEnd(key); }
            return;
        }
        // Other actors' Open updates remain in their Model until that actor is selected.
        if (_active == key) { ShowFullOrAppend(state); }
    }

    public void ShowFull(CharacterInteractionState state) => ShowFullOrAppend(state, forceFull: true);

    /// <summary>Read-only management preview. Does not move the item cursor or incremental baseline.</summary>
    public void ShowCurrent(CharacterInteractionState state, Action<string>? beforeWrite = null,
        string? prefix = null, IPlayLayout? previewLayout = null) {
        string text = prefix + CharacterContextRenderer.RenderLatestState(state, previewLayout ?? presentation.Current);
        beforeWrite?.Invoke(text);
        Reply(text);
    }

    public string Prompt(CycleObservation observation) => $"[Console 输入] {observation.Actor.Id}> ";

    private void ShowFullOrAppend(CharacterInteractionState state, bool forceFull = false) {
        CharacterContext context = state.CurrentContext ?? throw new InvalidOperationException("No saved context to render.");
        var key = Identity(state);
        bool first = !_deliveries.TryGetValue(key, out var delivered);
        bool switching = _active != key;
        if (delivered?.Ended == true) { return; }
        long last = context.Items.LastOrDefault()?.ItemSequence ?? 0;
        bool newItems = last > (delivered?.ItemSequence ?? 0);
        if (!first && !switching && !forceFull && !newItems && Baseline is not null) { return; }
        bool full = first || switching || forceFull || Baseline is null || presentation.Mode == PlayOutputMode.Full;
        string text = full
            ? CharacterContextRenderer.RenderOpening(state, presentation.Current) +
                CharacterContextRenderer.RenderItems(state, presentation.Current, full: presentation.Mode == PlayOutputMode.Full)
            : CharacterContextRenderer.RenderItems(state, presentation.Current, delivered!.ItemSequence);
        try {
            if (StartupText is { } startup) { Reply(startup); }
            if (first) {
                string hint = context.Status == CharacterContextStatus.Open ? metaCommands.RenderHint(presentation) : "";
                if (hint.Length != 0) { Reply(hint); }
                Boundary(CharacterContextRenderer.BeginMarker(state));
            } else if (switching) {
                Reply($"恢复显示角色 {state.ActorId} 的上下文 {context.ContextSequence}；以下重放已保存材料。");
            }
            output.WriteLine();
            output.WriteLine(text);
            if (context.Status == CharacterContextStatus.Terminal) { Boundary(CharacterContextRenderer.EndMarker(state)); }
            output.Flush();
            foreach (var old in _deliveries.Keys.Where(old => old.Actor == state.ActorId && old != key).ToArray()) {
                _deliveries.Remove(old);
            }
            _deliveries[key] = new(last, context.Status == CharacterContextStatus.Terminal);
            _active = key;
            Baseline = CycleViewSnapshot.Create(context.LatestObservation, state);
            StartupText = null;
            TerminalDelivered = context.Status == CharacterContextStatus.Terminal;
        } catch {
            Baseline = null;
            throw;
        }
    }

    private void WriteEnd((string Actor, long Context) key) {
        try {
            Boundary(CharacterContextRenderer.EndMarker(key.Actor, key.Context));
            output.Flush();
            _deliveries[key] = _deliveries[key] with { Ended = true };
            if (_active == key) { Baseline = null; }
        } catch {
            Baseline = null;
            throw;
        }
    }

    private void Boundary(string text) {
        output.WriteLine();
        if (writeBoundary is not null) { writeBoundary(text); }
        else { output.WriteLine(text); }
    }

    private static (string Actor, long Context) Identity(CharacterInteractionState state) =>
        (state.ActorId, state.CurrentContext!.ContextSequence);
}
