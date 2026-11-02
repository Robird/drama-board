using DramaBoard.Inventory;

namespace DramaBoard.Playground;

/// <summary>Compares frozen visible values, independently of durable object identity.</summary>
internal static class PlaygroundObservationEquality {
    internal static bool Same(PlaygroundObservation a, PlaygroundObservation b) =>
        ReferenceEquals(a, b) ||
        a.WorldName == b.WorldName && SameDictionary(a.ItemUseDescriptions, b.ItemUseDescriptions) &&
        a.Time == b.Time && a.IsDay == b.IsDay && a.NextLightChange == b.NextLightChange &&
        SameActor(a.Actor, b.Actor) && SameList(a.Resources, b.Resources, SameResource) &&
        SameList(a.MarketQuotes, b.MarketQuotes, (x, y) => x == y) &&
        SameList(a.Trades, b.Trades, (x, y) => x == y) && SameList(a.Recipes, b.Recipes, (x, y) => x == y) && a.MarketRefreshAt == b.MarketRefreshAt &&
        SameDictionary(a.HarvestDurations, b.HarvestDurations) && SameDictionary(a.ItemNames, b.ItemNames) &&
        SameList(a.OtherCharacters, b.OtherCharacters, (x, y) => x == y) &&
        SameList(a.Notices, b.Notices, SameNoticeObservation) &&
        SameList(a.Commands, b.Commands, (x, y) => x == y) &&
        a.IsExpedition == b.IsExpedition && a.PlaceId == b.PlaceId && a.PlaceName == b.PlaceName &&
        a.PlaceDescription == b.PlaceDescription && a.ArrivalDue == b.ArrivalDue && a.Journey == b.Journey &&
        SameList(a.Passages, b.Passages, (x, y) => x == y) &&
        SameList(a.Memories, b.Memories, SameMemory) &&
        a.Outcome == b.Outcome && a.EndingReason == b.EndingReason &&
        a.FollowingLeaderId == b.FollowingLeaderId && a.FollowingLeaderName == b.FollowingLeaderName && SameDictionary(a.Followers, b.Followers) &&
        SameSurvival(a.Survival, b.Survival) && a.Weather == b.Weather && a.Communication == b.Communication &&
        (a.Goals is null ? b.Goals is null : a.Goals.SameAs(b.Goals));

    private static bool SameActor(PlaygroundActorObservation a, PlaygroundActorObservation b) =>
        a.Id == b.Id && a.Name == b.Name && a.SkillLevel == b.SkillLevel &&
        SameInventory(a.Backpack, b.Backpack) && SameActivity(a.Activity, b.Activity) &&
        SameList(a.Notices, b.Notices, SameNotice) && a.LastResult == b.LastResult &&
        a.NextActivityId == b.NextActivityId && a.Money == b.Money;

    internal static bool SameInventory(ItemInventory a, ItemInventory b) =>
        a.UnitCapacity == b.UnitCapacity && a.TotalCount == b.TotalCount && SameDictionary(a.Items, b.Items);

    internal static bool SameActivity(PlaygroundActivity? a, PlaygroundActivity? b) =>
        ReferenceEquals(a, b) || a is not null && b is not null &&
        a.Kind == b.Kind && a.Id == b.Id && a.StartedAt == b.StartedAt &&
        a.DurationTicks == b.DurationTicks && a.Due == b.Due && a.ResourceId == b.ResourceId &&
        a.TargetCount == b.TargetCount && a.CompletedCount == b.CompletedCount &&
        a.SelectedAll == b.SelectedAll && a.CycleIndex == b.CycleIndex && a.Production == b.Production;

    internal static bool SameNotice(PlaygroundNotice a, PlaygroundNotice b) =>
        a.Kind == b.Kind && a.ResourceId == b.ResourceId && a.ActorId == b.ActorId &&
        a.SourceId == b.SourceId && a.At == b.At && a.Reason == b.Reason && a.Count == b.Count;

    private static bool SameNoticeObservation(PlaygroundNoticeObservation a, PlaygroundNoticeObservation b) =>
        SameNotice(a.Notice, b.Notice) && a.ResourceName == b.ResourceName && a.ActorLabel == b.ActorLabel &&
        a.StillGathering == b.StillGathering && a.CurrentStock == b.CurrentStock && a.Capacity == b.Capacity;

    internal static bool SameResource(PlaygroundResource a, PlaygroundResource b) =>
        a.Id == b.Id && a.Name == b.Name && a.ItemId == b.ItemId && a.GrowsInDaylight == b.GrowsInDaylight &&
        a.RegenerationTicks == b.RegenerationTicks && a.Capacity == b.Capacity && a.Stock == b.Stock &&
        a.ActiveSince == b.ActiveSince && a.AccumulatedTicks == b.AccumulatedTicks &&
        a.HarvestTicks == b.HarvestTicks && a.SkillId == b.SkillId && a.RequiredLevel == b.RequiredLevel &&
        a.Generation == b.Generation;

    private static bool SameMemory(PlaygroundPlaceMemory a, PlaygroundPlaceMemory b) =>
        a.Name == b.Name && a.At == b.At && a.PlaceId == b.PlaceId && SameList(a.Resources, b.Resources, SameResource);

    private static bool SameSurvival(PlaygroundSurvivalObservation? a, PlaygroundSurvivalObservation? b) =>
        ReferenceEquals(a, b) || a is not null && b is not null &&
        a.Food == b.Food && a.Water == b.Water && a.Warmth == b.Warmth &&
        a.FoodRate.SameAs(b.FoodRate) && a.WaterRate.SameAs(b.WaterRate) && a.WarmthRate.SameAs(b.WarmthRate) &&
        a.Minimum == b.Minimum && a.Maximum == b.Maximum &&
        a.Sheltered == b.Sheltered && SameList(a.PendingWarnings, b.PendingWarnings, (x, y) => x == y) &&
        a.FoodForecast == b.FoodForecast && a.WaterForecast == b.WaterForecast && a.WarmthForecast == b.WarmthForecast &&
        a.WarningThreshold == b.WarningThreshold && a.WarmthWarningPending == b.WarmthWarningPending &&
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
