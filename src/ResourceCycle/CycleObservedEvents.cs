using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.ResourceCycle;

/// <summary>Application-specific explanatory material, assembled after every business enrichment.</summary>
internal static class CycleObservedEvents {
    internal static CycleFact Attach(CycleWorld world, CycleFact fact, string? draftAfter = null) {
        if (fact.Kind is CycleEventKind.DecisionContextOpened or CycleEventKind.GoalOperation) { return fact; }
        var events = new List<CycleObservedEvent>();
        GraphSpatialState? afterSpatial = world.Expedition?.Spatial;
        foreach (GraphSpatialFact movement in fact.SpatialFacts) {
            afterSpatial = CycleExpeditionMap.Reducer.Apply(afterSpatial!, new(fact.At, 0), movement);
        }
        CycleCharacter[] afterCharacters = world.Characters.Select(character =>
            fact.ChangedCharacters.SingleOrDefault(changed => changed.Id == character.Id) ?? character).ToArray();
        foreach (CycleCharacter before in world.Characters) {
            if (!before.IsActive) { continue; }
            CycleCharacter after = fact.ChangedCharacters.SingleOrDefault(c => c.Id == before.Id) ?? before;
            bool ended = !after.IsActive;
            int priorRemaining = CycleCommunicationRules.Remaining(before, world.Time);
            int elapsedRemaining = CycleCommunicationRules.Remaining(before, fact.At);
            if (world.Expedition is not null && priorRemaining != elapsedRemaining) {
                Emit(CycleObservedEventKind.CommunicationBudgetRenewed,
                    [new(new("communication", before.Id, "remaining"), Number(priorRemaining, "次"), Number(elapsedRemaining, "次"))], from: world.Time);
            }
            CycleSurvival? integrated = before.Survival?.Project(fact.At);
            CycleSurvival? final = after.Survival?.Project(fact.At);
            List<CycleObservedEffect> process = [];
            if (before.Survival is { } body) { AddVitals(process, before.Id, body.Project(world.Time), integrated!); }
            if (process.Count > 0) { Emit(CycleObservedEventKind.BodyChanged, process, from: world.Time); }

            bool subjectSelf = fact.ActorId == before.Id;
            bool ownOperation = subjectSelf && fact.OperationRecordId is not null;
            bool movedSelf = MovedSelf(fact, before.Id);
            List<CycleObservedEffect> following = FollowingEffects(world.Characters, afterCharacters, before.Id);
            bool relationCommand = IsFollowingEvent(fact.Kind);
            if (following.Count > 0 && !relationCommand) {
                // A leader's terminal cause is not the surviving follower's own death or arrival.
                Emit(CycleObservedEventKind.FollowEnded, following, detail: "Terminal");
            }
            bool received = fact.TargetActorId == before.Id && fact.Kind is CycleEventKind.SpeechSent or CycleEventKind.ItemGiven;
            bool visibleResource = fact.ResourceId is { } resourceId &&
                (world.Expedition is null || CycleActions.CurrentPlace(world, before.Id) is { } place && CycleExpeditionMap.GetResourcePlace(resourceId) == place);
            bool publicEvent = fact.Kind is CycleEventKind.LightChanged or CycleEventKind.WeatherResolved || visibleResource &&
                fact.Kind is CycleEventKind.ResourceRegenerated or CycleEventKind.ResourceDestroyed;
            if (!subjectSelf && !received && !publicEvent && !movedSelf && !(relationCommand && following.Count > 0)) { continue; }
            var direct = new List<CycleObservedEffect>();
            if (relationCommand) { direct.AddRange(following); }
            var ending = new List<CycleObservedEffect>();
            List<CycleObservedEffect> lifecycle = ended ? ending : direct;
            string? subjectId = fact.ResourceId;
            string? subjectName = subjectId is null ? null : world.GetResource(subjectId).Name;
            string? detail = null;
            CycleObservedEventKind kind = Enum.Parse<CycleObservedEventKind>(fact.Kind.ToString());
            // Direct physiology is independent of the preceding elapsed-time interval.
            if (integrated is not null && final is not null) { AddVitals(direct, before.Id, integrated, final); }
            if (subjectSelf || received) {
                foreach (string item in before.Backpack.Items.Keys.Concat(after.Backpack.Items.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) {
                    Add(direct, new("inventory", item, "count"), Number(before.Backpack.Count(item), "份"), Number(after.Backpack.Count(item), "份"));
                    if (fact.Kind == CycleEventKind.ItemUsed && before.Backpack.Count(item) != after.Backpack.Count(item)) {
                        subjectId = item; subjectName = ItemName(item);
                    }
                }
            }
            if (integrated is not null && final is not null) {
                Add(lifecycle, new("body", before.Id, "food-rate"), Number(integrated.FoodPerHour, "点/小时"), Number(final.FoodPerHour, "点/小时"));
                Add(lifecycle, new("body", before.Id, "water-rate"), Number(integrated.WaterPerHour, "点/小时"), Number(final.WaterPerHour, "点/小时"));
                Add(lifecycle, new("body", before.Id, "warmth-rate"), Number(integrated.WarmthPerHour, "点/小时"), Number(final.WarmthPerHour, "点/小时"));
                SpatialLocation? finalLocation = after.Ending?.Location ?? afterSpatial?.Entities.SingleOrDefault(e => e.Id.Value == before.Id)?.Location;
                Add(direct, new("body", before.Id, "sheltered"), CycleVisibleValue.Flag(CycleSurvivalRules.IsSheltered(world.Expedition!.Spatial, before.Id)),
                    CycleVisibleValue.Flag(finalLocation is AtPlaceLocation sheltered && CycleExpeditionMap.GetPlace(sheltered.PlaceId.Value).Sheltered));
                foreach ((int mask, string field) in new[] { (1, "warning-food"), (2, "warning-water"), (4, "warning-warmth") }) {
                    Add(lifecycle, new("body", before.Id, field), CycleVisibleValue.Flag((integrated.PendingMask & mask) != 0), CycleVisibleValue.Flag((final.PendingMask & mask) != 0));
                }
            }
            Add(lifecycle, new("notice", before.Id, "count"), Number(before.Notices.Count, "条"), Number(after.Notices.Count, "条"));
            if (world.Expedition is not null) {
                Add(direct, new("communication", before.Id, "remaining"), Number(elapsedRemaining, "次"),
                    Number(CycleCommunicationRules.Remaining(after, fact.At), "次"));
                Add(lifecycle, new("communication", before.Id, "pending"), CycleVisibleValue.Flag(before.Communication.AttentionPending),
                    CycleVisibleValue.Flag(after.Communication.AttentionPending));
            }
            if (visibleResource && fact.ChangedResources.SingleOrDefault(r => r.Id == fact.ResourceId) is { } changedResource) {
                Add(direct, new("resource", changedResource.Id, "stock"), Number(world.GetResource(changedResource.Id).Stock, "份"), Number(changedResource.Stock, "份"));
            }
            if (fact.Kind == CycleEventKind.LightChanged) {
                Add(direct, new("world", "world", "light"), CycleVisibleValue.Flag(world.Light.IsDay), CycleVisibleValue.Flag(fact.Light!.IsDay));
            }
            if (movedSelf) {
                SpatialLocation priorLocation = world.Expedition!.Spatial.Entities.Single(e => e.Id.Value == before.Id).Location;
                SpatialLocation nextLocation = after.Ending?.Location ?? afterSpatial!.Entities.Single(e => e.Id.Value == before.Id).Location;
                Add(direct, new("location", before.Id, "place"), PlaceValue(priorLocation), PlaceValue(nextLocation));
                Add(direct, new("location", before.Id, "arrival"), ArrivalValue(priorLocation), ArrivalValue(nextLocation));
                subjectId = nextLocation is AtPlaceLocation atPlace ? atPlace.PlaceId.Value : null;
                subjectName = subjectId is null ? null : CycleExpeditionMap.GetPlace(subjectId).Name;
            }
            if (ownOperation) {
                Add(direct, new("continuity", before.Id, "draft"), Text(world.Business.InteractionStates[before.Id].DraftHandoff),
                    Text(draftAfter));
            }
            if (fact.Kind == CycleEventKind.WeatherResolved) {
                CycleWeatherEffect weather = fact.Environment!.LastEffects.Single(effect => effect.ActorId == before.Id);
                detail = fact.Environment.LastCold ? weather.Sheltered ? "ColdWindSheltered" : "ColdWind" : "Calm";
            }
            if (fact.Kind == CycleEventKind.GatherFinished) { detail = fact.GatherStopReason?.ToString(); }
            // A successful final yield owns the transfer; its separate ending owns the lifecycle removal.
            if (subjectSelf && fact.Kind == CycleEventKind.GatherYielded && after.Activity is null) {
                if (before.Activity is { } gathering) {
                    Add(direct, new("activity", before.Id, "completed"), Number(gathering.CompletedCount, "份"), Number(gathering.CompletedCount + 1, "份"));
                }
                Emit(kind, direct, subjectId, subjectName, detail);
                var gatheringEnded = new List<CycleObservedEffect>();
                AddActivity(gatheringEnded, before.Id, before.Activity, null, finalYield: true);
                Emit(CycleObservedEventKind.GatherFinished, gatheringEnded, subjectId, subjectName, fact.GatherStopReason?.ToString());
            } else {
                if (subjectSelf || ended) { AddActivity(lifecycle, before.Id, before.Activity, after.Activity); }
                // Empty acknowledgement/handoff operations have an operation answer, but no experienced change.
                if (direct.Count > 0 || fact.Kind is not (CycleEventKind.AttentionAcknowledged or CycleEventKind.HandoffEdited)) {
                    Emit(kind, direct, subjectId, subjectName, detail);
                }
            }
            if (ended) {
                // This legacy presentation key belongs to this observer's journey, never the global summary.
                ending.Add(new(new("world", "world", "outcome"), CycleVisibleValue.Literal(CycleExpeditionOutcome.Exploring.ToString()),
                    CycleVisibleValue.Literal(after.Ending!.Outcome.ToString())));
                Emit(CycleObservedEventKind.ExpeditionEnded, ending, detail: after.Ending.Reason);
            }

            void Emit(CycleObservedEventKind eventKind, IEnumerable<CycleObservedEffect> effects, string? id = null,
                string? name = null, string? detail = null, ModelTime? from = null) =>
                events.Add(new(events.Count, before.Id, eventKind, from ?? fact.At, fact.At, id, name, detail, effects,
                    eventKind is CycleObservedEventKind.SpeechSent or CycleObservedEventKind.ItemGiven
                        ? new(fact.ActorId!, world.GetCharacter(fact.ActorId!).Name, fact.TargetActorId!,
                            world.GetCharacter(fact.TargetActorId!).Name, fact.SpeechText, fact.ItemId, fact.ItemCount)
                        : null));
        }
        return fact.WithObservedEvents(events);
    }

    internal static bool MovedSelf(CycleFact fact, string actorId) => fact.SpatialFacts.Any(movement => movement switch {
        TraversalStartedFact started => started.EntityId.Value == actorId,
        TraversalArrivedFact arrived => arrived.EntityId.Value == actorId,
        _ => false
    });

    internal static bool IsFollowingEvent(CycleEventKind kind) =>
        kind is CycleEventKind.FollowStarted or CycleEventKind.FollowEnded or CycleEventKind.FollowersDismissed;

    private static List<CycleObservedEffect> FollowingEffects(IReadOnlyList<CycleCharacter> before,
        IReadOnlyList<CycleCharacter> after, string observerId) {
        List<CycleObservedEffect> effects = [];
        Add(effects, new("following", observerId, "leader"), Text(before.Single(c => c.Id == observerId).FollowingLeaderId),
            Text(after.Single(c => c.Id == observerId).FollowingLeaderId));
        foreach (CycleCharacter character in before) {
            Add(effects, new("following", observerId, $"follower:{character.Id}"),
                CycleVisibleValue.Flag(character.FollowingLeaderId == observerId),
                CycleVisibleValue.Flag(after.Single(c => c.Id == character.Id).FollowingLeaderId == observerId));
        }
        return effects;
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
