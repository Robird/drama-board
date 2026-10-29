using DramaBoard.Inventory;

namespace DramaBoard.ResourceCycle;

/// <summary>Compares frozen visible values, independently of durable object identity.</summary>
internal static class CycleObservationEquality {
    internal static bool Same(CycleObservation a, CycleObservation b) =>
        ReferenceEquals(a, b) ||
        a.Time == b.Time && a.IsDay == b.IsDay && a.NextLightChange == b.NextLightChange &&
        SameActor(a.Actor, b.Actor) && SameList(a.Resources, b.Resources, SameResource) &&
        SameDictionary(a.HarvestDurations, b.HarvestDurations) && SameDictionary(a.ItemNames, b.ItemNames) &&
        SameList(a.OtherCharacters, b.OtherCharacters, (x, y) => x == y) &&
        SameList(a.Notices, b.Notices, SameNoticeObservation) &&
        SameList(a.Commands, b.Commands, (x, y) => x == y) &&
        a.IsExpedition == b.IsExpedition && a.PlaceId == b.PlaceId && a.PlaceName == b.PlaceName &&
        a.PlaceDescription == b.PlaceDescription && a.ArrivalDue == b.ArrivalDue &&
        SameList(a.Passages, b.Passages, (x, y) => x == y) &&
        SameList(a.Memories, b.Memories, SameMemory) &&
        a.Outcome == b.Outcome && a.EndingReason == b.EndingReason &&
        SameSurvival(a.Survival, b.Survival) && a.Weather == b.Weather;

    private static bool SameActor(CycleActorObservation a, CycleActorObservation b) =>
        a.Id == b.Id && a.Name == b.Name && a.SkillLevel == b.SkillLevel &&
        SameInventory(a.Backpack, b.Backpack) && SameActivity(a.Activity, b.Activity) &&
        SameList(a.Notices, b.Notices, SameNotice) && a.LastResult == b.LastResult &&
        a.NextActivityId == b.NextActivityId;

    internal static bool SameInventory(ItemInventory a, ItemInventory b) =>
        a.UnitCapacity == b.UnitCapacity && a.TotalCount == b.TotalCount && SameDictionary(a.Items, b.Items);

    internal static bool SameActivity(CycleActivity? a, CycleActivity? b) =>
        ReferenceEquals(a, b) || a is not null && b is not null &&
        a.Kind == b.Kind && a.Id == b.Id && a.StartedAt == b.StartedAt &&
        a.DurationTicks == b.DurationTicks && a.Due == b.Due && a.ResourceId == b.ResourceId &&
        a.TargetCount == b.TargetCount && a.CompletedCount == b.CompletedCount &&
        a.SelectedAll == b.SelectedAll && a.CycleIndex == b.CycleIndex;

    internal static bool SameNotice(CycleNotice a, CycleNotice b) =>
        a.Kind == b.Kind && a.ResourceId == b.ResourceId && a.ActorId == b.ActorId &&
        a.SourceId == b.SourceId && a.At == b.At && a.Reason == b.Reason && a.Count == b.Count;

    private static bool SameNoticeObservation(CycleNoticeObservation a, CycleNoticeObservation b) =>
        SameNotice(a.Notice, b.Notice) && a.ResourceName == b.ResourceName && a.ActorLabel == b.ActorLabel &&
        a.StillGathering == b.StillGathering && a.CurrentStock == b.CurrentStock && a.Capacity == b.Capacity;

    internal static bool SameResource(CycleResource a, CycleResource b) =>
        a.Id == b.Id && a.Name == b.Name && a.ItemId == b.ItemId && a.GrowsInDaylight == b.GrowsInDaylight &&
        a.RegenerationTicks == b.RegenerationTicks && a.Capacity == b.Capacity && a.Stock == b.Stock &&
        a.ActiveSince == b.ActiveSince && a.AccumulatedTicks == b.AccumulatedTicks &&
        a.HarvestTicks == b.HarvestTicks && a.SkillId == b.SkillId && a.RequiredLevel == b.RequiredLevel &&
        a.Generation == b.Generation;

    private static bool SameMemory(CyclePlaceMemory a, CyclePlaceMemory b) =>
        a.Name == b.Name && a.At == b.At && a.PlaceId == b.PlaceId && SameList(a.Resources, b.Resources, SameResource);

    private static bool SameSurvival(CycleSurvivalObservation? a, CycleSurvivalObservation? b) =>
        ReferenceEquals(a, b) || a is not null && b is not null &&
        a.Food == b.Food && a.Water == b.Water && a.Warmth == b.Warmth &&
        a.FoodPerHour == b.FoodPerHour && a.WaterPerHour == b.WaterPerHour && a.WarmthPerHour == b.WarmthPerHour &&
        a.Sheltered == b.Sheltered && SameList(a.PendingWarnings, b.PendingWarnings, (x, y) => x == y) &&
        a.FatalAt == b.FatalAt && a.WarmthWarningPending == b.WarmthWarningPending &&
        a.PendingWarningMask == b.PendingWarningMask;

    internal static bool SameList<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, Func<T, T, bool> same) {
        if (a.Count != b.Count) { return false; }
        for (int i = 0; i < a.Count; i++) {
            if (!same(a[i], b[i])) { return false; }
        }
        return true;
    }

    private static bool SameDictionary<T>(IReadOnlyDictionary<string, T> a, IReadOnlyDictionary<string, T> b) {
        if (a.Count != b.Count) { return false; }
        foreach ((string key, T value) in a) {
            if (!b.TryGetValue(key, out T? other) || !EqualityComparer<T>.Default.Equals(value, other)) { return false; }
        }
        return true;
    }
}
