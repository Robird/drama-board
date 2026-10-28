using DramaBoard.Kernel.Time;
using DramaBoard.ResourceCycle;

internal enum PlayDeliveryPhase { Advancing, ControllerInput }

/// <summary>One serial writer owns delivery and its transient semantic baseline.</summary>
internal sealed class PlayPresentationSession(PlayPresentation presentation, TextWriter output, Action? clearScreen = null) {
    public CycleViewSnapshot? Baseline { get; private set; }
    public PlayDeliveryPhase Phase { get; private set; } = PlayDeliveryPhase.Advancing;
    public string? StartupText { get; set; }
    public bool TerminalDelivered { get; private set; }
    private (string Actor, long? Context, ModelTime? LegacyTime)? _identity;
    private PlayDeliveryPhase? _deliveredPhase;

    public void EnterInput(CycleObservation observation) {
        Phase = PlayDeliveryPhase.ControllerInput;
        if (Baseline is null || _identity != Identity(observation) || !observation.IsExpedition) {
            ShowFull(observation);
            return;
        }
        DeliverUpdate(observation, [], fullTail: false);
    }

    public void BeginAdvancing() => Phase = PlayDeliveryPhase.Advancing;

    public void Reply(string text) {
        output.WriteLine(text);
        output.Flush();
    }

    public void Commit(CycleObservation observation, IReadOnlyList<CycleObservedEvent> events,
        IReadOnlyList<CycleObservedEventSource>? sources = null) {
        // ContextOpened is delivered at the next controller entry, never on the old screen.
        if (_identity != Identity(observation) && observation.Outcome == CycleExpeditionOutcome.Exploring) { return; }
        if (Baseline is null) { ShowFull(observation); return; }
        DeliverUpdate(observation, events, presentation.Mode == PlayOutputMode.Full ||
            observation.Outcome != CycleExpeditionOutcome.Exploring, sources);
    }

    public void ShowFull(CycleObservation observation, Action<string>? beforeWrite = null, string? prefix = null) {
        var snapshot = CycleViewSnapshot.Create(observation);
        string text = (StartupText is { } startup ? startup + "\n" : "") + prefix +
            presentation.Current.RenderFull(snapshot) + PhaseText(observation);
        beforeWrite?.Invoke(text);
        Write(text, snapshot, clear: _identity != Identity(observation));
    }

    public string Prompt(CycleObservation observation) => presentation.Mode == PlayOutputMode.Delta
        ? $"{observation.Actor.Id}> " : presentation.Current.RenderPrompt(observation);

    private void DeliverUpdate(CycleObservation observation, IReadOnlyList<CycleObservedEvent> events, bool fullTail,
        IReadOnlyList<CycleObservedEventSource>? sources = null) {
        var snapshot = CycleViewSnapshot.Create(observation);
        CycleViewUpdate update = CycleViewUpdate.Create(Baseline!, snapshot, events, Phase, fullTail, sources);
        bool phaseChanged = _deliveredPhase != Phase;
        if (!fullTail && events.Count == 0 && update.CurrentValues.Count == 0 && !phaseChanged) { return; }
        string text = presentation.Current.RenderUpdate(update);
        // A successful commit is not an input opportunity. Only EnterInput changes this phase.
        if (phaseChanged || fullTail) { text += PhaseText(observation); }
        Write(text, snapshot, clear: false);
    }

    private string PhaseText(CycleObservation observation) => observation.Outcome != CycleExpeditionOutcome.Exploring
        ? "\n本局已结束；以上为冻结状态。\n"
        : Phase == PlayDeliveryPhase.ControllerInput ? "\n现在可输入；等待输入时世界暂停。\n"
        : "\n正在处理结算，尚未进入下一次输入。\n";

    private void Write(string text, CycleViewSnapshot snapshot, bool clear) {
        try {
            if (clear) { output.Flush(); clearScreen?.Invoke(); }
            output.WriteLine(text);
            output.Flush();
            Baseline = snapshot;
            _identity = Identity(snapshot.Observation);
            _deliveredPhase = Phase;
            StartupText = null;
            TerminalDelivered = snapshot.Observation.Outcome != CycleExpeditionOutcome.Exploring;
        } catch {
            Baseline = null;
            _deliveredPhase = null;
            throw;
        }
    }

    private static (string Actor, long? Context, ModelTime? LegacyTime) Identity(CycleObservation observation) =>
        (observation.Actor.Id, observation.Continuity?.ContextSequence,
            observation.Continuity is null ? observation.Time : null);
}
