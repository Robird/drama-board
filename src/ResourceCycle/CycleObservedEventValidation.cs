using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.ResourceCycle;

/// <summary>Checks recorded explanations against authoritative endpoints, without planning or replacing material.</summary>
internal static class CycleObservedEventValidation {
    internal static void Validate(CycleBusinessState before, CycleBusinessState after, CycleFact fact) {
        if (before.Expedition is null || fact.Kind == CycleEventKind.DecisionContextOpened) {
            if (fact.ObservedEvents.Count != 0) { throw new InvalidOperationException("This boundary has no observed-event stream."); }
            return;
        }
        var previousByObserver = new Dictionary<string, Dictionary<CycleStateKey, CycleVisibleValue>>(StringComparer.Ordinal);
        var targetByObserver = new Dictionary<string, Dictionary<CycleStateKey, CycleVisibleValue>>(StringComparer.Ordinal);
        foreach (CycleCharacter actor in before.Characters.Where(c => c.Survival is not null)) {
            string id = actor.Id;
            CycleCharacter next = after.GetCharacter(id);
            var keys = Keys(before, after, actor, next).Distinct().ToArray();
            previousByObserver.Add(id, keys.ToDictionary(key => key, key => Read(before, id, key)));
            targetByObserver.Add(id, keys.ToDictionary(key => key, key => Read(after, id, key)));
        }
        var processes = new HashSet<(string, CycleStateKey)>();
        var eventKinds = new HashSet<(string, CycleObservedEventKind)>();
        var lastPhaseByObserver = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < fact.ObservedEvents.Count; i++) {
            CycleObservedEvent entry = fact.ObservedEvents[i];
            if (entry.EventOrdinal != i || !Enum.IsDefined(entry.Kind) || !previousByObserver.TryGetValue(entry.ObserverId, out var current) ||
                entry.At != fact.At || entry.From > entry.At || entry.From < before.Time ||
                (entry.Kind == CycleObservedEventKind.BodyChanged ? entry.From != before.Time : entry.From != fact.At)) {
                throw new InvalidOperationException("Invalid observed event identity or interval.");
            }
            bool matchingKind = entry.Kind.ToString() == fact.Kind.ToString() || entry.Kind == CycleObservedEventKind.BodyChanged ||
                entry.Kind == CycleObservedEventKind.GatherFinished && fact.Kind == CycleEventKind.GatherYielded && fact.GatherStopReason is not null ||
                entry.Kind == CycleObservedEventKind.ExpeditionEnded && fact.Outcome is not null;
            if (!matchingKind || !eventKinds.Add((entry.ObserverId, entry.Kind)) || entry.Effects.Select(e => e.Key).Distinct().Count() != entry.Effects.Count) {
                throw new InvalidOperationException("An event must own its cause and each effect only once.");
            }
            int phase = entry.Kind switch {
                CycleObservedEventKind.BodyChanged => 0,
                CycleObservedEventKind.GatherFinished when fact.Kind == CycleEventKind.GatherYielded => 2,
                CycleObservedEventKind.ExpeditionEnded => 3,
                _ => 1
            };
            if (lastPhaseByObserver.TryGetValue(entry.ObserverId, out int previousPhase) && phase < previousPhase) {
                throw new InvalidOperationException("Observed causes must follow elapsed changes and precede their endings.");
            }
            lastPhaseByObserver[entry.ObserverId] = phase;
            ValidateParameters(before, after, fact, entry);
            foreach (CycleObservedEffect effect in entry.Effects) {
                effect.Before.Validate(); effect.After.Validate();
                if (string.IsNullOrWhiteSpace(effect.Key.Category) || string.IsNullOrWhiteSpace(effect.Key.ObjectId) || string.IsNullOrWhiteSpace(effect.Key.Field) ||
                    !current.TryGetValue(effect.Key, out CycleVisibleValue prior) || prior != effect.Before || effect.Before == effect.After || !Owns(entry.Kind, effect.Key)) {
                    throw new InvalidOperationException("An observed effect is out of scope or disagrees with its preceding endpoint.");
                }
                if (entry.Kind == CycleObservedEventKind.BodyChanged) {
                    CycleSurvival integrated = before.GetCharacter(entry.ObserverId).Survival!.Project(fact.At);
                    CycleVisibleValue expected = Vital(integrated, effect.Key.Field);
                    if (effect.After != expected || !processes.Add((entry.ObserverId, effect.Key))) {
                        throw new InvalidOperationException("Elapsed physiology must end at the old anchor projected to the fact time.");
                    }
                }
                if (entry.Kind == CycleObservedEventKind.GatherYielded && effect.Key.Category == "activity" && effect.Key.Field == "completed") {
                    CycleActivity gathering = before.GetCharacter(entry.ObserverId).Activity!;
                    if (effect.After != CycleVisibleValue.Numeric(gathering.CompletedCount + 1, "份")) {
                        throw new InvalidOperationException("A yield must record exactly one completed gathering cycle.");
                    }
                }
                current[effect.Key] = effect.After;
            }
        }
        foreach ((string actorId, var current) in previousByObserver) {
            CycleSurvival anchor = before.GetCharacter(actorId).Survival!;
            bool perceivesCause = fact.ActorId == actorId || fact.Kind == CycleEventKind.LightChanged ||
                fact.Kind is CycleEventKind.ResourceRegenerated or CycleEventKind.ResourceDestroyed &&
                fact.ResourceId is { } resourceId && previousByObserver[actorId].ContainsKey(new("resource", resourceId, "stock"));
            bool unchangedAcknowledgement = fact.Kind is CycleEventKind.AttentionAcknowledged or CycleEventKind.HandoffEdited &&
                !fact.ObservedEvents.Any(e => e.ObserverId == actorId && e.Effects.Count > 0);
            bool expectsPrimary = perceivesCause && !unchangedAcknowledgement;
            if (fact.Kind == CycleEventKind.GatherYielded && fact.ActorId == actorId) {
                bool finished = after.GetCharacter(actorId).Activity is null;
                if (eventKinds.Contains((actorId, CycleObservedEventKind.GatherFinished)) != finished ||
                    !fact.ObservedEvents.Any(e => e.ObserverId == actorId && e.Kind == CycleObservedEventKind.GatherYielded &&
                        e.Effects.Any(effect => effect.Key == new CycleStateKey("activity", actorId, "completed")))) {
                    throw new InvalidOperationException("A yield must retain its completed cycle and a distinct ending when finished.");
                }
            }
            var primaryKind = Enum.Parse<CycleObservedEventKind>(fact.Kind.ToString());
            if (eventKinds.Contains((actorId, primaryKind)) != expectsPrimary ||
                eventKinds.Contains((actorId, CycleObservedEventKind.ExpeditionEnded)) !=
                    (perceivesCause && fact.Outcome is not null && before.Expedition.Outcome != fact.Outcome)) {
                throw new InvalidOperationException("The observed cause or ending is missing or not visible to this observer.");
            }
            foreach (string field in new[] { "food", "water", "warmth" }) {
                var key = new CycleStateKey("body", actorId, field);
                if (Vital(anchor.Project(before.Time), field) != Vital(anchor.Project(fact.At), field) && !processes.Contains((actorId, key))) {
                    throw new InvalidOperationException("Elapsed physiological effects are missing.");
                }
            }
            if (current.Any(pair => pair.Value != targetByObserver[actorId][pair.Key])) {
                throw new InvalidOperationException("Recorded observed effects do not reach the committed business endpoints.");
            }
        }
    }

    private static void ValidateParameters(CycleBusinessState before, CycleBusinessState after, CycleFact fact, CycleObservedEvent entry) {
        string? id = null, name = null, detail = null;
        if (entry.Kind == CycleObservedEventKind.BodyChanged) {
            if (entry.Effects.Count == 0) { throw new InvalidOperationException("An elapsed interval must contain an actual change."); }
        } else if (entry.Kind == CycleObservedEventKind.ExpeditionEnded) {
            detail = fact.EndingReason;
        } else {
            id = fact.ResourceId;
            name = id is null ? null : before.GetResource(id).Name;
            if (entry.Kind == CycleObservedEventKind.ItemUsed) {
                CycleCharacter actor = before.GetCharacter(entry.ObserverId), next = after.GetCharacter(entry.ObserverId);
                id = actor.Backpack.Items.Keys.SingleOrDefault(item => actor.Backpack.Count(item) > next.Backpack.Count(item));
                name = id switch { "item.food" => "食物", "item.water" => "饮水", "item.wood" => "干木", _ => id };
            } else if (entry.Kind is CycleObservedEventKind.MoveStarted or CycleObservedEventKind.MoveArrived) {
                SpatialLocation location = after.Expedition!.Spatial.Entities.Single(e => e.Id.Value == entry.ObserverId).Location;
                id = location is AtPlaceLocation place ? place.PlaceId.Value : null;
                name = id is null ? null : CycleExpeditionMap.GetPlace(id).Name;
            } else if (entry.Kind == CycleObservedEventKind.GatherFinished) {
                detail = fact.GatherStopReason?.ToString();
            } else if (entry.Kind == CycleObservedEventKind.WeatherResolved) {
                CycleEnvironment environment = fact.Environment!;
                detail = environment.LastCold ? environment.LastSheltered ? "ColdWindSheltered" : "ColdWind" : "Calm";
            }
        }
        if (entry.SubjectId != id || entry.SubjectName != name || entry.Detail != detail) {
            throw new InvalidOperationException("Observed event parameters must match its recorded business cause.");
        }
    }
    private static bool Owns(CycleObservedEventKind kind, CycleStateKey key) => kind switch {
        CycleObservedEventKind.BodyChanged => key.Category == "body" && key.Field is "food" or "water" or "warmth",
        CycleObservedEventKind.ExpeditionEnded => key == new CycleStateKey("world", "world", "outcome"),
        CycleObservedEventKind.HandoffEdited => key.Category == "continuity",
        CycleObservedEventKind.ItemUsed => key.Category == "inventory" || key.Category == "body" && key.Field is "food" or "water",
        CycleObservedEventKind.ResourceRegenerated or CycleObservedEventKind.ResourceDestroyed => key.Category is "resource" or "notice",
        CycleObservedEventKind.LightChanged => key.Category == "world" && key.Field == "light" || key.Category == "body" && key.Field.EndsWith("-rate", StringComparison.Ordinal),
        CycleObservedEventKind.WeatherResolved => key.Category == "body" && key.Field is "warmth" or "warning-warmth",
        CycleObservedEventKind.SurvivalWarning => key.Category == "body" && key.Field.StartsWith("warning-", StringComparison.Ordinal),
        CycleObservedEventKind.SurvivalFailed => false,
        CycleObservedEventKind.AttentionAcknowledged => key.Category is "notice" or "continuity" || key.Category == "body" && key.Field.StartsWith("warning-", StringComparison.Ordinal),
        CycleObservedEventKind.GatherYielded => key.Category is "inventory" or "resource" or "notice" || key.Category == "activity" && key.Field is "completed" or "due",
        CycleObservedEventKind.GatherFinished or CycleObservedEventKind.WaitCompleted => key.Category == "activity",
        CycleObservedEventKind.WaitStarted or CycleObservedEventKind.GatherStarted or CycleObservedEventKind.ActivityCancelled => key.Category is "activity" or "notice" or "continuity" || key.Category == "body" && key.Field.StartsWith("warning-", StringComparison.Ordinal),
        CycleObservedEventKind.MoveStarted or CycleObservedEventKind.MoveArrived => key.Category is "location" or "activity" or "notice" or "continuity" || key.Category == "body" && (key.Field is "sheltered" or "warmth-rate" || key.Field.StartsWith("warning-", StringComparison.Ordinal)),
        _ => false
    };

    private static IEnumerable<CycleStateKey> Keys(CycleBusinessState before, CycleBusinessState after, CycleCharacter actor, CycleCharacter next) {
        foreach (string field in new[] { "food", "water", "warmth", "food-rate", "water-rate", "warmth-rate", "sheltered", "warning-food", "warning-water", "warning-warmth" }) { yield return new("body", actor.Id, field); }
        foreach (string item in actor.Backpack.Items.Keys.Concat(next.Backpack.Items.Keys).Distinct(StringComparer.Ordinal)) { yield return new("inventory", item, "count"); }
        foreach (string field in new[] { "kind", "due", "completed", "target" }) { yield return new("activity", actor.Id, field); }
        yield return new("notice", actor.Id, "count");
        yield return new("continuity", actor.Id, "draft");
        yield return new("world", "world", "light");
        yield return new("world", "world", "outcome");
        yield return new("location", actor.Id, "place");
        yield return new("location", actor.Id, "arrival");
        SpatialLocation location = before.Expedition!.Spatial.Entities.Single(e => e.Id.Value == actor.Id).Location;
        if (location is AtPlaceLocation place) {
            foreach (CycleResource resource in before.Resources.Where(r => CycleExpeditionMap.GetResourcePlace(r.Id) == place.PlaceId.Value)) {
                yield return new("resource", resource.Id, "stock");
            }
        }
    }

    private static CycleVisibleValue Read(CycleBusinessState state, string observer, CycleStateKey key) {
        CycleCharacter actor = state.GetCharacter(observer);
        CycleSurvival body = actor.Survival!.Project(state.Time);
        CycleActivity? activity = actor.Activity;
        SpatialLocation location = state.Expedition!.Spatial.Entities.Single(e => e.Id.Value == observer).Location;
        if (key.Category is "body" or "activity" or "location" or "notice" or "continuity" && key.ObjectId != observer) {
            throw new InvalidOperationException("Private effects belong to their observer.");
        }
        return (key.Category, key.Field) switch {
            ("body", "food" or "water" or "warmth") => Vital(body, key.Field),
            ("body", "food-rate") => CycleVisibleValue.Numeric(body.FoodPerHour, "点/小时"),
            ("body", "water-rate") => CycleVisibleValue.Numeric(body.WaterPerHour, "点/小时"),
            ("body", "warmth-rate") => CycleVisibleValue.Numeric(body.WarmthPerHour, "点/小时"),
            ("body", "sheltered") => CycleVisibleValue.Flag(CycleSurvivalRules.IsSheltered(state.Expedition.Spatial, observer)),
            ("body", "warning-food") => CycleVisibleValue.Flag((body.PendingMask & 1) != 0),
            ("body", "warning-water") => CycleVisibleValue.Flag((body.PendingMask & 2) != 0),
            ("body", "warning-warmth") => CycleVisibleValue.Flag((body.PendingMask & 4) != 0),
            ("inventory", "count") => CycleVisibleValue.Numeric(actor.Backpack.Count(key.ObjectId), "份"),
            ("activity", "kind") => Text(activity?.Kind.ToString()),
            ("activity", "due") => activity is null ? CycleVisibleValue.KnownAbsent : CycleVisibleValue.Numeric(activity.Due.Ticks, "ticks"),
            ("activity", "completed") => activity?.Kind != CycleActivityKind.Gather ? CycleVisibleValue.KnownAbsent : CycleVisibleValue.Numeric(activity.CompletedCount, "份"),
            ("activity", "target") => activity?.Kind != CycleActivityKind.Gather ? CycleVisibleValue.KnownAbsent : CycleVisibleValue.Numeric(activity.TargetCount, "份"),
            ("notice", "count") => CycleVisibleValue.Numeric(actor.Notices.Count, "条"),
            ("continuity", "draft") => Text(actor.Continuity?.DraftHandoff),
            ("world", "light") => CycleVisibleValue.Flag(state.Light.IsDay),
            ("world", "outcome") => CycleVisibleValue.Literal(state.Expedition.Outcome.ToString()),
            ("location", "place") => location is AtPlaceLocation at ? Text(at.PlaceId.Value) : CycleVisibleValue.KnownAbsent,
            ("location", "arrival") => location is TraversingLocation moving ? CycleVisibleValue.Numeric(moving.ArrivalDue.Ticks, "ticks") : CycleVisibleValue.KnownAbsent,
            ("resource", "stock") => CycleVisibleValue.Numeric(state.GetResource(key.ObjectId).Stock, "份"),
            _ => throw new InvalidOperationException("Unknown explanatory state key.")
        };
    }
    private static CycleVisibleValue Vital(CycleSurvival body, string field) => CycleVisibleValue.Numeric(field switch {
        "food" => body.FoodUnits, "water" => body.WaterUnits, "warmth" => body.WarmthUnits,
        _ => throw new InvalidOperationException("Not a physiological amount.")
    }, "点", CycleSurvival.Scale);
    private static CycleVisibleValue Text(string? text) => text is null ? CycleVisibleValue.KnownAbsent : CycleVisibleValue.Literal(text);
}
