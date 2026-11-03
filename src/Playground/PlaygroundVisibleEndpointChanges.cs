using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;
using static DramaBoard.Playground.PlaygroundObservationEquality;

namespace DramaBoard.Playground;

/// <summary>Checks changes to visible business endpoints without regenerating perception or command menus.</summary>
/// <remarks>This is a necessary-delivery guard, not a second observation projector. Derived command wording,
/// rules and definitions are intentionally outside its scope.</remarks>
internal static class PlaygroundVisibleEndpointChanges {
    internal static bool Changed(PlaygroundBusinessState before, PlaygroundBusinessState after, string actorId) {
        if (before.Time != after.Time || before.Light.IsDay != after.Light.IsDay ||
            before.Light.NextChange != after.Light.NextChange) { return true; }
        if (!PlaygroundPath.Same(before.Expedition?.CandidatePath(actorId), after.Expedition?.CandidatePath(actorId))) { return true; }
        if (!before.InteractionStates[actorId].Goals.SameAs(after.InteractionStates[actorId].Goals)) { return true; }
        if (!PlaygroundEconomyObservation.Trades(before, actorId).SequenceEqual(PlaygroundEconomyObservation.Trades(after, actorId))) { return true; }
        PlaygroundCharacter a = before.GetCharacter(actorId), b = after.GetCharacter(actorId);
        if (a.Ending != b.Ending || a.Name != b.Name || a.SkillLevel != b.SkillLevel || a.LastResult != b.LastResult ||
            a.FollowingLeaderId != b.FollowingLeaderId || a.Money != b.Money ||
            !before.Characters.Where(c => c.FollowingLeaderId == actorId).Select(c => (c.Id, c.Name))
                .SequenceEqual(after.Characters.Where(c => c.FollowingLeaderId == actorId).Select(c => (c.Id, c.Name))) ||
            a.FollowingLeaderId is { } leader && before.GetCharacter(leader).Name != after.GetCharacter(leader).Name ||
            a.NextActivityId != b.NextActivityId || !SameInventory(a.Backpack, b.Backpack) ||
            !SameActivity(a.Activity, b.Activity) || !SameList(a.Notices, b.Notices, SameNotice) ||
            !SameBody(a.Survival, b.Survival, before.Time) || a.Communication != b.Communication) { return true; }

        PlaygroundExpedition? oldExpedition = before.Expedition, newExpedition = after.Expedition;
        if ((oldExpedition is null) != (newExpedition is null)) { return true; }
        string? oldPlace = Place(before, actorId), newPlace = Place(after, actorId);
        if (oldPlace != newPlace || Arrival(before, actorId) != Arrival(after, actorId)) { return true; }
        if (oldPlace == before.Definition.MarketPlaceId && !(before.Economy?.Market.SameState(after.Economy?.Market) ?? after.Economy is null)) { return true; }
        if (oldExpedition is not null && newExpedition is not null &&
            (!oldExpedition.ConfirmedPassages.Where(k => k.ActorId == actorId).SequenceEqual(newExpedition.ConfirmedPassages.Where(k => k.ActorId == actorId)) ||
            !SameWeather(oldExpedition.Environment, newExpedition.Environment, actorId) ||
            !SameList(HistoricalVisits(oldExpedition, actorId, oldPlace), HistoricalVisits(newExpedition, actorId, newPlace), SameVisit))) {
            return true;
        }
        if (!SameList(VisibleResources(before, oldPlace), VisibleResources(after, newPlace), SameResource)) { return true; }
        return !SameList(VisibleOthers(before, actorId, oldPlace), VisibleOthers(after, actorId, newPlace), SamePublicCharacter);
    }

    private static string? Place(PlaygroundBusinessState state, string actorId) =>
        PlaygroundActions.Location(state, actorId)
            is AtPlaceLocation at ? at.PlaceId.Value : null;

    private static ModelTime? Arrival(PlaygroundBusinessState state, string actorId) =>
        PlaygroundActions.Location(state, actorId)
            is TraversingLocation moving ? moving.ArrivalDue : null;

    private static PlaygroundResource[] VisibleResources(PlaygroundBusinessState state, string? place) =>
        state.Resources.Where(resource => state.Expedition is null ||
            place is not null && state.Definition.GetResourcePlace(resource.Id) == place).ToArray();

    private static PlaygroundCharacter[] VisibleOthers(PlaygroundBusinessState state, string actorId, string? place) =>
        state.Characters.Where(other => other.IsActive && other.Id != actorId && (state.Expedition is null ||
            place is not null && Place(state, other.Id) == place)).ToArray();

    private static bool SamePublicCharacter(PlaygroundCharacter a, PlaygroundCharacter b) =>
        a.Id == b.Id && a.Name == b.Name && a.FollowingLeaderId == b.FollowingLeaderId &&
        a.Activity?.Production?.RecipeId == b.Activity?.Production?.RecipeId &&
        (a.Activity is { Kind: PlaygroundActivityKind.Gather } ag ? ag.ResourceId : null) ==
            (b.Activity is { Kind: PlaygroundActivityKind.Gather } bg ? bg.ResourceId : null) &&
        (a.Activity?.Kind == PlaygroundActivityKind.Wait) == (b.Activity?.Kind == PlaygroundActivityKind.Wait);

    private static PlaygroundVisit[] HistoricalVisits(PlaygroundExpedition expedition, string actorId, string? place) =>
        expedition.Visits.Where(visit => visit.ActorId == actorId && visit.PlaceId != place).ToArray();

    private static bool SameVisit(PlaygroundVisit a, PlaygroundVisit b) =>
        a.PlaceId == b.PlaceId && a.At == b.At && SameList(a.Resources, b.Resources, SameResource);

    private static bool SameWeather(PlaygroundEnvironment? a, PlaygroundEnvironment? b, string actorId) =>
        ReferenceEquals(a, b) || a is not null && b is not null &&
        a.NextDue == b.NextDue && a.LastAt == b.LastAt && a.LastCold == b.LastCold &&
        a.LastEffects.SingleOrDefault(effect => effect.ActorId == actorId) == b.LastEffects.SingleOrDefault(effect => effect.ActorId == actorId);

    private static bool SameBody(PlaygroundSurvival? a, PlaygroundSurvival? b, ModelTime at) {
        if (ReferenceEquals(a, b)) { return true; }
        if (a is null || b is null) { return false; }
        // Reanchoring at the same instant can preserve all visible values. Compare the existing
        // integrated values and evaluated causes, not anchor identity, and do not forecast future command outcomes.
        PlaygroundSurvival x = a.Project(at), y = b.Project(at);
        return x.FoodUnits == y.FoodUnits && x.WaterUnits == y.WaterUnits && x.WarmthUnits == y.WarmthUnits &&
            x.FoodRate.SameAs(y.FoodRate) && x.WaterRate.SameAs(y.WaterRate) && x.WarmthRate.SameAs(y.WarmthRate) &&
            x.PendingMask == y.PendingMask && x.PendingSince == y.PendingSince;
    }
}
