namespace DramaBoard.ResourceCycle;

/// <summary>Application semantics version, independent of durable schema and simulation WorldVersion.</summary>
public static class CycleGameRevision {
    public const int Current = 3;

    /// <summary>Upgrades restored representations without executing or publishing pending work.</summary>
    /// <remarks>
    /// Production currently supports only the current revision. Internal adjacent-step delegates let
    /// fixtures exercise the real loading path without inventing production migration history.
    /// State steps cannot inspect pending work; Event steps may use that step's old and new State.
    /// Steps must preserve their input snapshots and have stable semantics across supported releases.
    /// </remarks>
    internal static (CycleDurableRoot State, CycleDurableEvent? PendingEvent) UpgradeLoadedBoundary(
        CycleDurableRoot state, CycleDurableEvent? pending, int targetRevision,
        Func<CycleDurableRoot, CycleDurableRoot>? upgradeState = null,
        Func<CycleDurableRoot, CycleDurableRoot, CycleDurableEvent, CycleDurableEvent>? upgradeEvent = null) {
        state.ValidateStructure();
        pending?.ValidateStructure();
        if (targetRevision <= 0 || state.GameRevision > targetRevision ||
            pending is not null && (pending.GameRevision < state.GameRevision || pending.GameRevision > targetRevision)) {
            throw new InvalidDataException("Unsupported ResourceCycle State/Event GameRevision boundary.");
        }
        if (state.GameRevision < targetRevision && upgradeState is null ||
            pending is not null && pending.GameRevision < targetRevision && upgradeEvent is null) {
            throw new InvalidDataException("No upgrade path supports this ResourceCycle GameRevision.");
        }

        // Reproduce the exact prestate used to plan a newer persisted Event before transforming it.
        while (pending is not null && state.GameRevision < pending.GameRevision) {
            state = AdvanceState(state, upgradeState!);
        }
        while (state.GameRevision < targetRevision) {
            CycleDurableRoot previous = state;
            state = AdvanceState(previous, upgradeState!);
            if (pending is not null) {
                CycleDurableEvent previousEvent = pending;
                pending = upgradeEvent!(previous, state, previousEvent)
                    ?? throw new InvalidDataException("An upgrade cannot discard pending work.");
                pending.ValidateStructure();
                if (pending.GameRevision != state.GameRevision ||
                    pending.Occurrence.CauseKey != previousEvent.Occurrence.CauseKey ||
                    pending.Occurrence.TargetInstant != previousEvent.Occurrence.TargetInstant) {
                    throw new InvalidDataException("An Event upgrade must advance one revision and preserve occurrence identity.");
                }
            }
        }
        return (state, pending);
    }

    private static CycleDurableRoot AdvanceState(
        CycleDurableRoot previous, Func<CycleDurableRoot, CycleDurableRoot> upgradeState) {
        CycleDurableRoot next = upgradeState(previous)
            ?? throw new InvalidDataException("A State upgrade returned no representation.");
        next.ValidateStructure();
        if (next.GameRevision != checked(previous.GameRevision + 1) || next.Cursor != previous.Cursor) {
            throw new InvalidDataException("A State upgrade must advance one revision and preserve the completed cursor.");
        }
        return next;
    }
}
