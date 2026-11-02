namespace DramaBoard.Playground;

/// <summary>Semantic equality for recorded context materials across independent E and S object graphs.</summary>
internal static class CharacterContextEquality {
    internal static bool Same(CharacterContextOpening? a, CharacterContextOpening? b) =>
        ReferenceEquals(a, b) || a is not null && b is not null &&
        SameBackground(a.Background, b.Background) && PlaygroundObservationEquality.Same(a.Observation, b.Observation) &&
        Same(a.PreviousAdvance, b.PreviousAdvance) && a.DeliveredAt == b.DeliveredAt &&
        a.ActivityState == b.ActivityState && SameList(a.Experiences, b.Experiences, Same);

    internal static bool Same(CharacterContextItem? a, CharacterContextItem? b) =>
        ReferenceEquals(a, b) || a is not null && b is not null &&
        a.ItemSequence == b.ItemSequence && a.OperationSequence == b.OperationSequence &&
        a.RawInput == b.RawInput && a.ToolCallId == b.ToolCallId && a.OperationRecordId == b.OperationRecordId &&
        a.Result == b.Result && SameList(a.Experiences, b.Experiences, Same) &&
        SameObservation(a.Observation, b.Observation) && a.IsRejected == b.IsRejected &&
        (a.GoalOperation is null ? b.GoalOperation is null : a.GoalOperation.SameAs(b.GoalOperation));

    internal static bool Same(CharacterExperience? a, CharacterExperience? b) =>
        ReferenceEquals(a, b) || a is not null && b is not null &&
        a.ExperienceSequence == b.ExperienceSequence && SameEvent(a.Event, b.Event);

    internal static bool Same(PendingAdvance? a, PendingAdvance? b) =>
        ReferenceEquals(a, b) || a is not null && b is not null &&
        a.ActorId == b.ActorId && a.SourceContextSequence == b.SourceContextSequence &&
        a.OperationSequence == b.OperationSequence && a.Command == b.Command && a.RawInput == b.RawInput &&
        a.ToolCallId == b.ToolCallId && a.OperationRecordId == b.OperationRecordId && a.AcceptedAt == b.AcceptedAt &&
        a.Description == b.Description && a.OriginPlaceName == b.OriginPlaceName &&
        a.ActivityId == b.ActivityId && a.MovementGeneration == b.MovementGeneration && a.Handoff == b.Handoff;

    private static bool SameBackground(PlaygroundBackground a, PlaygroundBackground b) =>
        a.Title == b.Title && a.Introduction == b.Introduction &&
        SameList(a.Rules, b.Rules, (x, y) => x == y);

    private static bool SameObservation(PlaygroundObservation? a, PlaygroundObservation? b) =>
        ReferenceEquals(a, b) || a is not null && b is not null && PlaygroundObservationEquality.Same(a, b);

    private static bool SameEvent(PlaygroundObservedEvent a, PlaygroundObservedEvent b) =>
        a.EventOrdinal == b.EventOrdinal && a.ObserverId == b.ObserverId && a.Kind == b.Kind &&
        a.From == b.From && a.At == b.At && a.SubjectId == b.SubjectId && a.SubjectName == b.SubjectName &&
        a.Detail == b.Detail && a.Communication == b.Communication && a.Production == b.Production && a.TradeChanges.SequenceEqual(b.TradeChanges) && SameList(a.Effects, b.Effects,
            (x, y) => x.Key == y.Key && x.Before == y.Before && x.After == y.After);

    private static bool SameList<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, Func<T, T, bool> same) {
        if (a.Count != b.Count) { return false; }
        for (int i = 0; i < a.Count; i++) {
            if (!same(a[i], b[i])) { return false; }
        }
        return true;
    }
}
