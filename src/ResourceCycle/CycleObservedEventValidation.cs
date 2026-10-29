using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.ResourceCycle;

/// <summary>Checks recorded explanations against authoritative endpoints, without planning or replacing material.</summary>
internal static class CycleObservedEventValidation {
    internal static void Validate(CycleBusinessState before, CycleBusinessState after, CycleFact fact) {
        if (fact.Kind is CycleEventKind.DecisionContextOpened or CycleEventKind.OperationRejected) {
            if (fact.ObservedEvents.Count != 0) { throw new InvalidOperationException("This boundary has no observed-event stream."); }
            return;
        }
        var previousByObserver = new Dictionary<string, Dictionary<CycleStateKey, CycleVisibleValue>>(StringComparer.Ordinal);
        var targetByObserver = new Dictionary<string, Dictionary<CycleStateKey, CycleVisibleValue>>(StringComparer.Ordinal);
        foreach (CycleCharacter actor in before.Characters) {
            if (!actor.IsActive) { continue; }
            string id = actor.Id;
            CycleCharacter next = after.GetCharacter(id);
            var keys = Keys(before, actor, next, fact).Distinct().ToArray();
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
                (entry.Kind is CycleObservedEventKind.BodyChanged or CycleObservedEventKind.CommunicationBudgetRenewed ? entry.From != before.Time : entry.From != fact.At)) {
                throw new InvalidOperationException("Invalid observed event identity or interval.");
            }
            bool matchingKind = entry.Kind.ToString() == fact.Kind.ToString() || entry.Kind is CycleObservedEventKind.BodyChanged or CycleObservedEventKind.CommunicationBudgetRenewed ||
                entry.Kind == CycleObservedEventKind.GatherFinished && fact.Kind == CycleEventKind.GatherYielded && fact.GatherStopReason is not null ||
                entry.Kind == CycleObservedEventKind.ExpeditionEnded && !after.GetCharacter(entry.ObserverId).IsActive;
            if (!matchingKind || !eventKinds.Add((entry.ObserverId, entry.Kind)) || entry.Effects.Select(e => e.Key).Distinct().Count() != entry.Effects.Count) {
                throw new InvalidOperationException("An event must own its cause and each effect only once.");
            }
            int phase = entry.Kind switch {
                CycleObservedEventKind.BodyChanged or CycleObservedEventKind.CommunicationBudgetRenewed => 0,
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
                if (entry.Kind == CycleObservedEventKind.CommunicationBudgetRenewed && effect.After !=
                    CycleVisibleValue.Numeric(CycleCommunicationRules.Remaining(before.GetCharacter(entry.ObserverId), fact.At), "次")) {
                    throw new InvalidOperationException("Elapsed communication budget must use the old sender anchor at the new time.");
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
            CycleSurvival? anchor = before.GetCharacter(actorId).Survival;
            bool perceivesCause = fact.ActorId == actorId ||
                fact.TargetActorId == actorId && fact.Kind is CycleEventKind.SpeechSent or CycleEventKind.ItemGiven ||
                fact.Kind is CycleEventKind.LightChanged or CycleEventKind.WeatherResolved ||
                fact.Kind is CycleEventKind.ResourceRegenerated or CycleEventKind.ResourceDestroyed &&
                fact.ResourceId is { } resourceId && previousByObserver[actorId].ContainsKey(new("resource", resourceId, "stock"));
            bool unchangedAcknowledgement = fact.Kind is CycleEventKind.AttentionAcknowledged or CycleEventKind.HandoffEdited &&
                !fact.ObservedEvents.Any(e => e.ObserverId == actorId && e.Effects.Count > 0);
            bool expectsPrimary = perceivesCause && !unchangedAcknowledgement;
            bool budgetRenewed = before.Expedition is not null &&
                CycleCommunicationRules.Remaining(before.GetCharacter(actorId), before.Time) !=
                CycleCommunicationRules.Remaining(before.GetCharacter(actorId), fact.At);
            if (eventKinds.Contains((actorId, CycleObservedEventKind.CommunicationBudgetRenewed)) != budgetRenewed) {
                throw new InvalidOperationException("Communication budget renewal must exactly follow the elapsed clock.");
            }
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
                    (perceivesCause && !after.GetCharacter(actorId).IsActive)) {
                throw new InvalidOperationException("The observed cause or ending is missing or not visible to this observer.");
            }
            foreach (string field in new[] { "food", "water", "warmth" }) {
                var key = new CycleStateKey("body", actorId, field);
                if (anchor is not null && Vital(anchor.Project(before.Time), field) != Vital(anchor.Project(fact.At), field) && !processes.Contains((actorId, key))) {
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
        CycleObservedCommunication? communication = null;
        if (entry.Kind == CycleObservedEventKind.CommunicationBudgetRenewed) {
            if (before.Expedition is null || entry.Effects.Count != 1) {
                throw new InvalidOperationException("Budget renewal requires one visible elapsed-budget effect.");
            }
        } else if (entry.Kind is CycleObservedEventKind.SpeechSent or CycleObservedEventKind.ItemGiven) {
            communication = new(fact.ActorId!, before.GetCharacter(fact.ActorId!).Name,
                fact.TargetActorId!, before.GetCharacter(fact.TargetActorId!).Name, fact.SpeechText, fact.ItemId, fact.ItemCount);
            communication.Validate(entry.ObserverId, entry.Kind);
        } else if (entry.Kind == CycleObservedEventKind.BodyChanged) {
            if (before.GetCharacter(entry.ObserverId).Survival is null || entry.Effects.Count == 0) { throw new InvalidOperationException("An elapsed physiological interval requires a body and an actual change."); }
        } else if (entry.Kind == CycleObservedEventKind.ExpeditionEnded) {
            detail = after.GetCharacter(entry.ObserverId).Ending!.Reason;
        } else {
            id = fact.ResourceId;
            name = id is null ? null : before.GetResource(id).Name;
            if (entry.Kind == CycleObservedEventKind.ItemUsed) {
                CycleCharacter actor = before.GetCharacter(entry.ObserverId), next = after.GetCharacter(entry.ObserverId);
                id = actor.Backpack.Items.Keys.SingleOrDefault(item => actor.Backpack.Count(item) > next.Backpack.Count(item));
                name = id switch { "item.food" => "食物", "item.water" => "饮水", "item.wood" => "干木", _ => id };
            } else if (entry.Kind is CycleObservedEventKind.MoveStarted or CycleObservedEventKind.MoveArrived) {
                SpatialLocation location = CycleActions.Location(after, entry.ObserverId)!;
                id = location is AtPlaceLocation place ? place.PlaceId.Value : null;
                name = id is null ? null : CycleExpeditionMap.GetPlace(id).Name;
            } else if (entry.Kind == CycleObservedEventKind.GatherFinished) {
                detail = fact.GatherStopReason?.ToString();
            } else if (entry.Kind == CycleObservedEventKind.WeatherResolved) {
                CycleEnvironment environment = fact.Environment!;
                CycleWeatherEffect weather = environment.LastEffects.Single(effect => effect.ActorId == entry.ObserverId);
                detail = environment.LastCold ? weather.Sheltered ? "ColdWindSheltered" : "ColdWind" : "Calm";
            }
        }
        if (entry.SubjectId != id || entry.SubjectName != name || entry.Detail != detail || entry.Communication != communication) {
            throw new InvalidOperationException("Observed event parameters must match its recorded business cause.");
        }
    }
    private static bool Owns(CycleObservedEventKind kind, CycleStateKey key) => kind switch {
        CycleObservedEventKind.CommunicationBudgetRenewed => key.Category == "communication" && key.Field == "remaining",
        CycleObservedEventKind.SpeechSent => key.Category == "communication" && key.Field is "remaining" or "pending",
        CycleObservedEventKind.ItemGiven => key.Category == "inventory" || key.Category == "communication" && key.Field is "remaining" or "pending",
        CycleObservedEventKind.BodyChanged => key.Category == "body" && key.Field is "food" or "water" or "warmth",
        CycleObservedEventKind.ExpeditionEnded => key == new CycleStateKey("world", "world", "outcome") ||
            key.Category is "activity" or "notice" || key.Category == "communication" && key.Field == "pending" || key.Category == "body" &&
                (key.Field.EndsWith("-rate", StringComparison.Ordinal) || key.Field.StartsWith("warning-", StringComparison.Ordinal)),
        CycleObservedEventKind.HandoffEdited => key.Category == "continuity",
        CycleObservedEventKind.ItemUsed => key.Category == "inventory" || key.Category == "body" && key.Field is "food" or "water",
        CycleObservedEventKind.ResourceRegenerated or CycleObservedEventKind.ResourceDestroyed => key.Category is "resource" or "notice",
        CycleObservedEventKind.LightChanged => key.Category == "world" && key.Field == "light" || key.Category == "body" && key.Field.EndsWith("-rate", StringComparison.Ordinal),
        CycleObservedEventKind.WeatherResolved => key.Category == "body" && key.Field is "warmth" or "warning-warmth",
        CycleObservedEventKind.SurvivalWarning => key.Category == "body" && key.Field.StartsWith("warning-", StringComparison.Ordinal),
        CycleObservedEventKind.SurvivalFailed => false,
        CycleObservedEventKind.AttentionAcknowledged => key.Category is "notice" or "continuity" || key.Category == "communication" && key.Field == "pending" || key.Category == "body" && key.Field.StartsWith("warning-", StringComparison.Ordinal),
        CycleObservedEventKind.GatherYielded => key.Category is "inventory" or "resource" or "notice" || key.Category == "activity" && key.Field is "completed" or "due",
        CycleObservedEventKind.GatherFinished or CycleObservedEventKind.WaitCompleted => key.Category == "activity",
        CycleObservedEventKind.WaitStarted or CycleObservedEventKind.GatherStarted or CycleObservedEventKind.ActivityCancelled => key.Category is "activity" or "notice" or "continuity" || key.Category == "communication" && key.Field == "pending" || key.Category == "body" && key.Field.StartsWith("warning-", StringComparison.Ordinal),
        CycleObservedEventKind.MoveStarted or CycleObservedEventKind.MoveArrived => key.Category is "location" or "activity" or "notice" or "continuity" || key.Category == "communication" && key.Field == "pending" || key.Category == "body" && (key.Field is "sheltered" or "warmth-rate" || key.Field.StartsWith("warning-", StringComparison.Ordinal)),
        _ => false
    };

    private static IEnumerable<CycleStateKey> Keys(CycleBusinessState before, CycleCharacter actor, CycleCharacter next, CycleFact fact) {
        if (actor.Survival is not null) {
            foreach (string field in new[] { "food", "water", "warmth", "food-rate", "water-rate", "warmth-rate", "sheltered", "warning-food", "warning-water", "warning-warmth" }) { yield return new("body", actor.Id, field); }
        }
        bool own = fact.ActorId == actor.Id;
        bool received = fact.TargetActorId == actor.Id && fact.Kind is CycleEventKind.SpeechSent or CycleEventKind.ItemGiven;
        bool ending = !next.IsActive;
        bool resourceCause = fact.Kind is CycleEventKind.ResourceRegenerated or CycleEventKind.ResourceDestroyed;
        // Other characters' actions may update this observer's visible stock or notices. Their
        // observation snapshot owns those updates until a dedicated perceived cause is modeled.
        if (before.Expedition is not null) {
            yield return new("communication", actor.Id, "remaining");
            yield return new("communication", actor.Id, "pending");
        }
        if (own || received) {
            foreach (string item in actor.Backpack.Items.Keys.Concat(next.Backpack.Items.Keys).Distinct(StringComparer.Ordinal)) { yield return new("inventory", item, "count"); }
        }
        if (own) {
            // Draft consumption belongs to a captured character operation, not terminal lifecycle cleanup.
            if (fact.OperationRecordId is not null) { yield return new("continuity", actor.Id, "draft"); }
            if (before.Expedition is not null) {
                yield return new("location", actor.Id, "place");
                yield return new("location", actor.Id, "arrival");
            }
        }
        if (own || ending) {
            foreach (string field in new[] { "kind", "due", "completed", "target" }) { yield return new("activity", actor.Id, field); }
        }
        if (own || ending || resourceCause) { yield return new("notice", actor.Id, "count"); }
        yield return new("world", "world", "light");
        if (before.Expedition is not null) { yield return new("world", "world", "outcome"); }
        if (own || resourceCause) {
            SpatialLocation? location = CycleActions.Location(before, actor.Id);
            foreach (CycleResource resource in before.Resources.Where(r => before.Expedition is null ||
                location is AtPlaceLocation place && CycleExpeditionMap.GetResourcePlace(r.Id) == place.PlaceId.Value)) {
                yield return new("resource", resource.Id, "stock");
            }
        }
    }

    private static CycleVisibleValue Read(CycleBusinessState state, string observer, CycleStateKey key) {
        CycleCharacter actor = state.GetCharacter(observer);
        CycleSurvival? body = actor.Survival?.Project(state.Time);
        CycleActivity? activity = actor.Activity;
        SpatialLocation? location = CycleActions.Location(state, observer);
        if (key.Category is "body" or "activity" or "location" or "notice" or "continuity" or "communication" && key.ObjectId != observer) {
            throw new InvalidOperationException("Private effects belong to their observer.");
        }
        return (key.Category, key.Field) switch {
            ("body", "food" or "water" or "warmth") => Vital(body!, key.Field),
            ("body", "food-rate") => CycleVisibleValue.Numeric(body!.FoodPerHour, "点/小时"),
            ("body", "water-rate") => CycleVisibleValue.Numeric(body!.WaterPerHour, "点/小时"),
            ("body", "warmth-rate") => CycleVisibleValue.Numeric(body!.WarmthPerHour, "点/小时"),
            ("body", "sheltered") => CycleVisibleValue.Flag(location is AtPlaceLocation shelter && CycleExpeditionMap.GetPlace(shelter.PlaceId.Value).Sheltered),
            ("body", "warning-food") => CycleVisibleValue.Flag((body!.PendingMask & 1) != 0),
            ("body", "warning-water") => CycleVisibleValue.Flag((body!.PendingMask & 2) != 0),
            ("body", "warning-warmth") => CycleVisibleValue.Flag((body!.PendingMask & 4) != 0),
            ("inventory", "count") => CycleVisibleValue.Numeric(actor.Backpack.Count(key.ObjectId), "份"),
            ("activity", "kind") => Text(activity?.Kind.ToString()),
            ("activity", "due") => activity is null ? CycleVisibleValue.KnownAbsent : CycleVisibleValue.Numeric(activity.Due.Ticks, "ticks"),
            ("activity", "completed") => activity?.Kind != CycleActivityKind.Gather ? CycleVisibleValue.KnownAbsent : CycleVisibleValue.Numeric(activity.CompletedCount, "份"),
            ("activity", "target") => activity?.Kind != CycleActivityKind.Gather ? CycleVisibleValue.KnownAbsent : CycleVisibleValue.Numeric(activity.TargetCount, "份"),
            ("notice", "count") => CycleVisibleValue.Numeric(actor.Notices.Count, "条"),
            ("continuity", "draft") => Text(state.InteractionStates[observer].DraftHandoff),
            ("communication", "remaining") => CycleVisibleValue.Numeric(CycleCommunicationRules.Remaining(actor, state.Time), "次"),
            ("communication", "pending") => CycleVisibleValue.Flag(actor.Communication.AttentionPending),
            ("world", "light") => CycleVisibleValue.Flag(state.Light.IsDay),
            // The key is actor-scoped presentation material despite its historical category name.
            ("world", "outcome") => CycleVisibleValue.Literal((actor.Ending?.Outcome ?? CycleExpeditionOutcome.Exploring).ToString()),
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
