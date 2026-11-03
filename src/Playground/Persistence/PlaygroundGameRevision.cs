namespace DramaBoard.Playground;

/// <summary>Application semantics version, independent of durable schema and simulation WorldVersion.</summary>
public static class PlaygroundGameRevision {
    public const int Current = 23;

    /// <summary>Upgrades restored representations without executing or publishing pending work.</summary>
    /// <remarks>
    /// Production currently supports only the current revision. Internal adjacent-step delegates let
    /// fixtures exercise the real loading path without inventing production migration history.
    /// State steps cannot inspect pending work; Event steps may use that step's old and new State.
    /// Steps must preserve their input snapshots and have stable semantics across supported releases.
    /// </remarks>
    internal static (PlaygroundDurableRoot State, PlaygroundDurableEvent? PendingEvent) UpgradeLoadedBoundary(
        PlaygroundDurableRoot state, PlaygroundDurableEvent? pending, int targetRevision,
        Func<PlaygroundDurableRoot, PlaygroundDurableRoot>? upgradeState = null,
        Func<PlaygroundDurableRoot, PlaygroundDurableRoot, PlaygroundDurableEvent, PlaygroundDurableEvent>? upgradeEvent = null) {
        state.ValidateStructure();
        pending?.ValidateStructure();
        if (targetRevision <= 0 || state.GameRevision > targetRevision ||
            pending is not null && (pending.GameRevision < state.GameRevision || pending.GameRevision > targetRevision)) {
            throw new InvalidDataException("Unsupported Playground State/Event GameRevision boundary.");
        }
        if (state.GameRevision < targetRevision && upgradeState is null ||
            pending is not null && pending.GameRevision < targetRevision && upgradeEvent is null) {
            throw new InvalidDataException("No upgrade path supports this Playground GameRevision.");
        }
        state.ValidateContent();

        // Reproduce the exact prestate used to plan a newer persisted Event before transforming it.
        while (pending is not null && state.GameRevision < pending.GameRevision) {
            state = AdvanceState(state, upgradeState!);
        }
        pending?.ValidateContent(state.Business.Definition);
        while (state.GameRevision < targetRevision) {
            PlaygroundDurableRoot previous = state;
            state = AdvanceState(previous, upgradeState!);
            if (pending is not null) {
                PlaygroundDurableEvent previousEvent = pending;
                pending = upgradeEvent!(previous, state, previousEvent)
                    ?? throw new InvalidDataException("An upgrade cannot discard pending work.");
                pending.ValidateStructure();
                if (pending.GameRevision != state.GameRevision ||
                    pending.Occurrence.CauseKey != previousEvent.Occurrence.CauseKey ||
                    pending.Occurrence.TargetInstant != previousEvent.Occurrence.TargetInstant) {
                    throw new InvalidDataException("An Event upgrade must advance one revision and preserve occurrence identity.");
                }
                pending.ValidateContent(state.Business.Definition);
            }
        }
        return (state, pending);
    }

    private static PlaygroundDurableRoot AdvanceState(
        PlaygroundDurableRoot previous, Func<PlaygroundDurableRoot, PlaygroundDurableRoot> upgradeState) {
        PlaygroundDurableRoot next = upgradeState(previous)
            ?? throw new InvalidDataException("A State upgrade returned no representation.");
        next.ValidateStructure();
        next.ValidateContent();
        if (next.GameRevision != checked(previous.GameRevision + 1) || next.Cursor != previous.Cursor) {
            throw new InvalidDataException("A State upgrade must advance one revision and preserve the completed cursor.");
        }
        return next;
    }
}
