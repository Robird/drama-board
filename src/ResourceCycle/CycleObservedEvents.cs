using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.ResourceCycle;

/// <summary>Application-specific explanatory material, assembled after every business enrichment.</summary>
internal static class CycleObservedEvents {
    internal static CycleFact Attach(CycleWorld world, CycleFact fact) {
        if (world.Expedition is null || fact.Kind == CycleEventKind.DecisionContextOpened) { return fact; }
        var events = new List<CycleObservedEvent>();
        GraphSpatialState afterSpatial = world.Expedition.Spatial;
        foreach (GraphSpatialFact movement in fact.SpatialFacts) {
            afterSpatial = CycleExpeditionMap.Reducer.Apply(afterSpatial, new(fact.At, 0), movement);
        }
        foreach (CycleCharacter before in world.Characters.Where(c => c.Survival is not null)) {
            CycleCharacter after = fact.ChangedCharacters.SingleOrDefault(c => c.Id == before.Id) ?? before;
            CycleSurvival old = before.Survival!.Project(world.Time);
            CycleSurvival integrated = before.Survival.Project(fact.At);
            CycleSurvival final = after.Survival!.Project(fact.At);
            List<CycleObservedEffect> process = [];
            AddVitals(process, before.Id, old, integrated);
            if (process.Count > 0) { Emit(CycleObservedEventKind.BodyChanged, process, from: world.Time); }

            bool own = fact.ActorId == before.Id;
            bool visibleResource = fact.ResourceId is { } resourceId &&
                CycleActions.CurrentPlace(world, before.Id) is { } place && CycleExpeditionMap.GetResourcePlace(resourceId) == place;
            bool publicEvent = fact.Kind == CycleEventKind.LightChanged || visibleResource &&
                fact.Kind is CycleEventKind.ResourceRegenerated or CycleEventKind.ResourceDestroyed;
            if (!own && !publicEvent) { continue; }
            var direct = new List<CycleObservedEffect>();
            string? subjectId = fact.ResourceId;
            string? subjectName = subjectId is null ? null : world.GetResource(subjectId).Name;
            string? detail = null;
            CycleObservedEventKind kind = Enum.Parse<CycleObservedEventKind>(fact.Kind.ToString());
            // Direct physiology is independent of the preceding elapsed-time interval.
            AddVitals(direct, before.Id, integrated, final);
            if (own) {
                foreach (string item in before.Backpack.Items.Keys.Concat(after.Backpack.Items.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) {
                    Add(direct, new("inventory", item, "count"), Number(before.Backpack.Count(item), "份"), Number(after.Backpack.Count(item), "份"));
                    if (fact.Kind == CycleEventKind.ItemUsed && before.Backpack.Count(item) != after.Backpack.Count(item)) {
                        subjectId = item; subjectName = ItemName(item);
                    }
                }
            }
            Add(direct, new("body", before.Id, "food-rate"), Number(integrated.FoodPerHour, "点/小时"), Number(final.FoodPerHour, "点/小时"));
            Add(direct, new("body", before.Id, "water-rate"), Number(integrated.WaterPerHour, "点/小时"), Number(final.WaterPerHour, "点/小时"));
            Add(direct, new("body", before.Id, "warmth-rate"), Number(integrated.WarmthPerHour, "点/小时"), Number(final.WarmthPerHour, "点/小时"));
            Add(direct, new("body", before.Id, "sheltered"), CycleVisibleValue.Flag(CycleSurvivalRules.IsSheltered(world.Expedition.Spatial, before.Id)),
                CycleVisibleValue.Flag(CycleSurvivalRules.IsSheltered(afterSpatial, before.Id)));
            foreach ((int mask, string field) in new[] { (1, "warning-food"), (2, "warning-water"), (4, "warning-warmth") }) {
                Add(direct, new("body", before.Id, field), CycleVisibleValue.Flag((integrated.PendingMask & mask) != 0), CycleVisibleValue.Flag((final.PendingMask & mask) != 0));
            }
            Add(direct, new("notice", before.Id, "count"), Number(before.Notices.Count, "条"), Number(after.Notices.Count, "条"));
            if (visibleResource && fact.ChangedResources.SingleOrDefault(r => r.Id == fact.ResourceId) is { } changedResource) {
                Add(direct, new("resource", changedResource.Id, "stock"), Number(world.GetResource(changedResource.Id).Stock, "份"), Number(changedResource.Stock, "份"));
            }
            if (fact.Kind == CycleEventKind.LightChanged) {
                Add(direct, new("world", "world", "light"), CycleVisibleValue.Flag(world.Light.IsDay), CycleVisibleValue.Flag(fact.Light!.IsDay));
            }
            if (own && fact.Kind is CycleEventKind.MoveStarted or CycleEventKind.MoveArrived) {
                SpatialLocation priorLocation = world.Expedition.Spatial.Entities.Single(e => e.Id.Value == before.Id).Location;
                SpatialLocation nextLocation = afterSpatial.Entities.Single(e => e.Id.Value == before.Id).Location;
                Add(direct, new("location", before.Id, "place"), PlaceValue(priorLocation), PlaceValue(nextLocation));
                Add(direct, new("location", before.Id, "arrival"), ArrivalValue(priorLocation), ArrivalValue(nextLocation));
                subjectId = nextLocation is AtPlaceLocation atPlace ? atPlace.PlaceId.Value : null;
                subjectName = subjectId is null ? null : CycleExpeditionMap.GetPlace(subjectId).Name;
            }
            if (own) {
                Add(direct, new("continuity", before.Id, "draft"), Text(before.Continuity?.DraftHandoff), Text(after.Continuity?.DraftHandoff));
            }
            if (fact.Kind == CycleEventKind.WeatherResolved) {
                detail = fact.Environment!.LastCold ? fact.Environment.LastSheltered ? "ColdWindSheltered" : "ColdWind" : "Calm";
            }
            if (fact.Kind == CycleEventKind.GatherFinished) { detail = fact.GatherStopReason?.ToString(); }
            // A successful final yield owns the transfer; its separate ending owns the lifecycle removal.
            if (own && fact.Kind == CycleEventKind.GatherYielded && after.Activity is null) {
                if (before.Activity is { } gathering) {
                    Add(direct, new("activity", before.Id, "completed"), Number(gathering.CompletedCount, "份"), Number(gathering.CompletedCount + 1, "份"));
                }
                Emit(kind, direct, subjectId, subjectName, detail);
                var ended = new List<CycleObservedEffect>();
                AddActivity(ended, before.Id, before.Activity, null, finalYield: true);
                Emit(CycleObservedEventKind.GatherFinished, ended, subjectId, subjectName, fact.GatherStopReason?.ToString());
            } else {
                if (own) { AddActivity(direct, before.Id, before.Activity, after.Activity); }
                // Empty acknowledgement/handoff operations have an operation answer, but no experienced change.
                if (direct.Count > 0 || fact.Kind is not (CycleEventKind.AttentionAcknowledged or CycleEventKind.HandoffEdited)) {
                    Emit(kind, direct, subjectId, subjectName, detail);
                }
            }
            if (fact.Outcome is { } outcome && outcome != world.Expedition.Outcome) {
                Emit(CycleObservedEventKind.ExpeditionEnded,
                    [new(new("world", "world", "outcome"), CycleVisibleValue.Literal(world.Expedition.Outcome.ToString()), CycleVisibleValue.Literal(outcome.ToString()))],
                    detail: fact.EndingReason);
            }

            void Emit(CycleObservedEventKind eventKind, IEnumerable<CycleObservedEffect> effects, string? id = null,
                string? name = null, string? detail = null, ModelTime? from = null) =>
                events.Add(new(events.Count, before.Id, eventKind, from ?? fact.At, fact.At, id, name, detail, effects));
        }
        return fact.WithObservedEvents(events);
    }

    private static void AddVitals(List<CycleObservedEffect> effects, string actorId, CycleSurvival before, CycleSurvival after) {
        Add(effects, new("body", actorId, "food"), Number(before.FoodUnits, "点", CycleSurvival.Scale), Number(after.FoodUnits, "点", CycleSurvival.Scale));
        Add(effects, new("body", actorId, "water"), Number(before.WaterUnits, "点", CycleSurvival.Scale), Number(after.WaterUnits, "点", CycleSurvival.Scale));
        Add(effects, new("body", actorId, "warmth"), Number(before.WarmthUnits, "点", CycleSurvival.Scale), Number(after.WarmthUnits, "点", CycleSurvival.Scale));
    }
    private static void AddActivity(List<CycleObservedEffect> effects, string actorId, CycleActivity? before, CycleActivity? after, bool finalYield = false) {
        Add(effects, new("activity", actorId, "kind"), Text(before?.Kind.ToString()), Text(after?.Kind.ToString()));
        Add(effects, new("activity", actorId, "due"), before is null ? CycleVisibleValue.KnownAbsent : Number(before.Due.Ticks, "ticks"), after is null ? CycleVisibleValue.KnownAbsent : Number(after.Due.Ticks, "ticks"));
        Add(effects, new("activity", actorId, "completed"), before?.Kind == CycleActivityKind.Gather ? Number(before.CompletedCount + (finalYield ? 1 : 0), "份") : CycleVisibleValue.KnownAbsent,
            after?.Kind == CycleActivityKind.Gather ? Number(after.CompletedCount, "份") : CycleVisibleValue.KnownAbsent);
        Add(effects, new("activity", actorId, "target"), before?.Kind == CycleActivityKind.Gather ? Number(before.TargetCount, "份") : CycleVisibleValue.KnownAbsent,
            after?.Kind == CycleActivityKind.Gather ? Number(after.TargetCount, "份") : CycleVisibleValue.KnownAbsent);
    }
    private static void Add(List<CycleObservedEffect> effects, CycleStateKey key, CycleVisibleValue before, CycleVisibleValue after) {
        if (before != after) { effects.Add(new(key, before, after)); }
    }
    private static CycleVisibleValue Number(long number, string unit, long scale = 1) => CycleVisibleValue.Numeric(number, unit, scale);
    private static CycleVisibleValue Text(string? text) => text is null ? CycleVisibleValue.KnownAbsent : CycleVisibleValue.Literal(text);
    private static CycleVisibleValue PlaceValue(SpatialLocation location) => location is AtPlaceLocation at ? Text(at.PlaceId.Value) : CycleVisibleValue.KnownAbsent;
    private static CycleVisibleValue ArrivalValue(SpatialLocation location) => location is TraversingLocation moving ? Number(moving.ArrivalDue.Ticks, "ticks") : CycleVisibleValue.KnownAbsent;
    private static string ItemName(string id) => id switch { "item.food" => "食物", "item.water" => "饮水", "item.wood" => "干木", _ => id };
}
