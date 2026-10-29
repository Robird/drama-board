using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;
using static DramaBoard.ResourceCycle.CycleObservationEquality;

namespace DramaBoard.ResourceCycle;

/// <summary>Checks changes to visible business endpoints without regenerating perception or command menus.</summary>
/// <remarks>This is a necessary-delivery guard, not a second observation projector. Derived command wording,
/// rules and definitions are intentionally outside its scope.</remarks>
internal static class CycleVisibleEndpointChanges {
    internal static bool Changed(CycleBusinessState before, CycleBusinessState after, string actorId) {
        if (before.Time != after.Time || before.Light.IsDay != after.Light.IsDay ||
            before.Light.NextChange != after.Light.NextChange) { return true; }
        CycleCharacter a = before.GetCharacter(actorId), b = after.GetCharacter(actorId);
        if (a.Name != b.Name || a.SkillLevel != b.SkillLevel || a.LastResult != b.LastResult ||
            a.NextActivityId != b.NextActivityId || !SameInventory(a.Backpack, b.Backpack) ||
            !SameActivity(a.Activity, b.Activity) || !SameList(a.Notices, b.Notices, SameNotice) ||
            !SameBody(a.Survival, b.Survival, before.Time)) { return true; }

        CycleExpedition? oldExpedition = before.Expedition, newExpedition = after.Expedition;
        if ((oldExpedition is null) != (newExpedition is null)) { return true; }
        string? oldPlace = Place(before, actorId), newPlace = Place(after, actorId);
        if (oldPlace != newPlace || Arrival(before, actorId) != Arrival(after, actorId)) { return true; }
        if (oldExpedition is not null && newExpedition is not null &&
            (oldExpedition.Outcome != newExpedition.Outcome || oldExpedition.EndingReason != newExpedition.EndingReason ||
            !SameWeather(oldExpedition.Environment, newExpedition.Environment) ||
            !SameList(HistoricalVisits(oldExpedition, actorId, oldPlace), HistoricalVisits(newExpedition, actorId, newPlace), SameVisit))) {
            return true;
        }
        if (!SameList(VisibleResources(before, oldPlace), VisibleResources(after, newPlace), SameResource)) { return true; }
        return !SameList(VisibleOthers(before, actorId, oldPlace), VisibleOthers(after, actorId, newPlace), SamePublicCharacter);
    }

    private static string? Place(CycleBusinessState state, string actorId) =>
        state.Expedition?.Spatial.Entities.Single(entity => entity.Id.Value == actorId).Location
            is AtPlaceLocation at ? at.PlaceId.Value : null;

    private static ModelTime? Arrival(CycleBusinessState state, string actorId) =>
        state.Expedition?.Spatial.Entities.Single(entity => entity.Id.Value == actorId).Location
            is TraversingLocation moving ? moving.ArrivalDue : null;

    private static CycleResource[] VisibleResources(CycleBusinessState state, string? place) =>
        state.Resources.Where(resource => state.Expedition is null ||
            place is not null && CycleExpeditionMap.GetResourcePlace(resource.Id) == place).ToArray();

    private static CycleCharacter[] VisibleOthers(CycleBusinessState state, string actorId, string? place) =>
        state.Characters.Where(other => other.Id != actorId && (state.Expedition is null ||
            place is not null && Place(state, other.Id) == place)).ToArray();

    private static bool SamePublicCharacter(CycleCharacter a, CycleCharacter b) =>
        a.Id == b.Id && a.Name == b.Name &&
        (a.Activity is { Kind: CycleActivityKind.Gather } ag ? ag.ResourceId : null) ==
            (b.Activity is { Kind: CycleActivityKind.Gather } bg ? bg.ResourceId : null) &&
        (a.Activity?.Kind == CycleActivityKind.Wait) == (b.Activity?.Kind == CycleActivityKind.Wait);

    private static CycleVisit[] HistoricalVisits(CycleExpedition expedition, string actorId, string? place) =>
        expedition.Visits.Where(visit => visit.ActorId == actorId && visit.PlaceId != place).ToArray();

    private static bool SameVisit(CycleVisit a, CycleVisit b) =>
        a.PlaceId == b.PlaceId && a.At == b.At && SameList(a.Resources, b.Resources, SameResource);

    private static bool SameWeather(CycleEnvironment? a, CycleEnvironment? b) =>
        ReferenceEquals(a, b) || a is not null && b is not null &&
        a.NextDue == b.NextDue && a.LastAt == b.LastAt && a.LastCold == b.LastCold &&
        a.LastWarmthLossUnits == b.LastWarmthLossUnits && a.LastSheltered == b.LastSheltered;

    private static bool SameBody(CycleSurvival? a, CycleSurvival? b, ModelTime at) {
        if (ReferenceEquals(a, b)) { return true; }
        if (a is null || b is null) { return false; }
        // Reanchoring at the same instant can preserve all visible values. Compare the existing
        // integrated values and evaluated causes, not anchor identity, and do not forecast future command outcomes.
        CycleSurvival x = a.Project(at), y = b.Project(at);
        return x.FoodUnits == y.FoodUnits && x.WaterUnits == y.WaterUnits && x.WarmthUnits == y.WarmthUnits &&
            x.FoodRate.SameAs(y.FoodRate) && x.WaterRate.SameAs(y.WaterRate) && x.WarmthRate.SameAs(y.WarmthRate) &&
            x.PendingMask == y.PendingMask && x.PendingSince == y.PendingSince;
    }
}
