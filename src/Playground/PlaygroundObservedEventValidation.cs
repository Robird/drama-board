using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.Playground;

/// <summary>Checks recorded explanations against authoritative endpoints, without planning or replacing material.</summary>
internal static class PlaygroundObservedEventValidation {
    internal static void Validate(PlaygroundBusinessState before, PlaygroundBusinessState after, PlaygroundFact fact) {
        if (fact.Kind is PlaygroundEventKind.DecisionContextOpened or PlaygroundEventKind.OperationRejected or PlaygroundEventKind.GoalOperation or PlaygroundEventKind.MapQueried or PlaygroundEventKind.PathQueried or PlaygroundEventKind.NavigationCancelled or PlaygroundEventKind.NavigationCompleted) {
            if (fact.ObservedEvents.Count != 0) { throw new InvalidOperationException("This boundary has no observed-event stream."); }
            return;
        }
        var previousByObserver = new Dictionary<string, Dictionary<PlaygroundStateKey, PlaygroundVisibleValue>>(StringComparer.Ordinal);
        var targetByObserver = new Dictionary<string, Dictionary<PlaygroundStateKey, PlaygroundVisibleValue>>(StringComparer.Ordinal);
        foreach (PlaygroundCharacter actor in before.Characters) {
            if (!actor.IsActive) { continue; }
            string id = actor.Id;
            PlaygroundCharacter next = after.GetCharacter(id);
            var keys = Keys(before, actor, next, fact).Distinct().ToArray();
            previousByObserver.Add(id, keys.ToDictionary(key => key, key => Read(before, id, key)));
            targetByObserver.Add(id, keys.ToDictionary(key => key, key => Read(after, id, key)));
        }
        var processes = new HashSet<(string, PlaygroundStateKey)>();
        var eventKinds = new HashSet<(string, PlaygroundObservedEventKind)>();
        var lastPhaseByObserver = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < fact.ObservedEvents.Count; i++) {
            PlaygroundObservedEvent entry = fact.ObservedEvents[i];
            if (entry.EventOrdinal != i || !Enum.IsDefined(entry.Kind) || !previousByObserver.TryGetValue(entry.ObserverId, out var current) ||
                entry.At != fact.At || entry.From > entry.At || entry.From < before.Time ||
                (entry.Kind is PlaygroundObservedEventKind.BodyChanged or PlaygroundObservedEventKind.CommunicationBudgetRenewed ? entry.From != before.Time : entry.From != fact.At)) {
                throw new InvalidOperationException("Invalid observed event identity or interval.");
            }
            bool matchingKind = entry.Kind.ToString() == fact.Kind.ToString() || entry.Kind is PlaygroundObservedEventKind.BodyChanged or PlaygroundObservedEventKind.CommunicationBudgetRenewed ||
                entry.Kind == PlaygroundObservedEventKind.TradeParticipantFailed && PlaygroundObservedEvents.IsParticipantFailure(fact) &&
                    fact.TradeChanges.Any(offer => offer.Involves(entry.ObserverId)) ||
                entry.Kind == PlaygroundObservedEventKind.GatherFinished && fact.Kind == PlaygroundEventKind.GatherYielded && fact.GatherStopReason is not null ||
                entry.Kind == PlaygroundObservedEventKind.FollowEnded && !PlaygroundObservedEvents.IsFollowingEvent(fact.Kind) &&
                    FollowingChanged(before, after, entry.ObserverId) ||
                entry.Kind == PlaygroundObservedEventKind.ExpeditionEnded && !after.GetCharacter(entry.ObserverId).IsActive;
            if (!matchingKind || !eventKinds.Add((entry.ObserverId, entry.Kind)) || entry.Effects.Select(e => e.Key).Distinct().Count() != entry.Effects.Count) {
                throw new InvalidOperationException("An event must own its cause and each effect only once.");
            }
            int phase = entry.Kind switch {
                PlaygroundObservedEventKind.BodyChanged or PlaygroundObservedEventKind.CommunicationBudgetRenewed => 0,
                PlaygroundObservedEventKind.GatherFinished when fact.Kind == PlaygroundEventKind.GatherYielded => 2,
                PlaygroundObservedEventKind.ExpeditionEnded => 3,
                _ => 1
            };
            if (lastPhaseByObserver.TryGetValue(entry.ObserverId, out int previousPhase) && phase < previousPhase) {
                throw new InvalidOperationException("Observed causes must follow elapsed changes and precede their endings.");
            }
            lastPhaseByObserver[entry.ObserverId] = phase;
            ValidateParameters(before, after, fact, entry);
            foreach (PlaygroundObservedEffect effect in entry.Effects) {
                effect.Before.Validate(); effect.After.Validate();
                if (string.IsNullOrWhiteSpace(effect.Key.Category) || string.IsNullOrWhiteSpace(effect.Key.ObjectId) || string.IsNullOrWhiteSpace(effect.Key.Field) ||
                    !current.TryGetValue(effect.Key, out PlaygroundVisibleValue prior) || prior != effect.Before || effect.Before == effect.After || !Owns(entry.Kind, effect.Key)) {
                    throw new InvalidOperationException("An observed effect is out of scope or disagrees with its preceding endpoint.");
                }
                if (entry.Kind == PlaygroundObservedEventKind.BodyChanged) {
                    PlaygroundSurvival integrated = before.GetCharacter(entry.ObserverId).Survival!.Project(fact.At);
                    PlaygroundVisibleValue expected = Vital(integrated, effect.Key.Field);
                    if (effect.After != expected || !processes.Add((entry.ObserverId, effect.Key))) {
                        throw new InvalidOperationException("Elapsed physiology must end at the old anchor projected to the fact time.");
                    }
                }
                if (entry.Kind == PlaygroundObservedEventKind.CommunicationBudgetRenewed && effect.After !=
                    PlaygroundVisibleValue.Numeric(PlaygroundCommunicationRules.Remaining(before.GetCharacter(entry.ObserverId), fact.At), "次")) {
                    throw new InvalidOperationException("Elapsed communication budget must use the old sender anchor at the new time.");
                }
                if (entry.Kind == PlaygroundObservedEventKind.GatherYielded && effect.Key.Category == "activity" && effect.Key.Field == "completed") {
                    PlaygroundActivity gathering = before.GetCharacter(entry.ObserverId).Activity!;
                    if (effect.After != PlaygroundVisibleValue.Numeric(gathering.CompletedCount + 1, "份")) {
                        throw new InvalidOperationException("A yield must record exactly one completed gathering cycle.");
                    }
                }
                current[effect.Key] = effect.After;
            }
        }
        foreach ((string actorId, var current) in previousByObserver) {
            PlaygroundSurvival? anchor = before.GetCharacter(actorId).Survival;
            bool perceivesCause = fact.ActorId == actorId ||
                PlaygroundObservedEvents.MovedSelf(fact, actorId) ||
                PlaygroundObservedEvents.IsFollowingEvent(fact.Kind) && FollowingChanged(before, after, actorId) ||
                PlaygroundObservedEvents.ReceivesCommunication(fact, actorId) ||
                fact.Kind == PlaygroundEventKind.MarketRefreshed && PlaygroundActions.Location(before, actorId) is AtPlaceLocation market && market.PlaceId.Value == before.Definition.MarketPlaceId ||
                fact.Kind is PlaygroundEventKind.LightChanged or PlaygroundEventKind.WeatherResolved ||
                fact.Kind is PlaygroundEventKind.ResourceRegenerated or PlaygroundEventKind.ResourceDestroyed &&
                fact.ResourceId is { } resourceId && previousByObserver[actorId].ContainsKey(new("resource", resourceId, "stock"));
            bool unchangedAcknowledgement = fact.Kind is PlaygroundEventKind.AttentionAcknowledged or PlaygroundEventKind.HandoffEdited &&
                !fact.ObservedEvents.Any(e => e.ObserverId == actorId && e.Effects.Count > 0);
            bool expectsPrimary = perceivesCause && !unchangedAcknowledgement;
            bool budgetRenewed = before.Expedition is not null &&
                PlaygroundCommunicationRules.Remaining(before.GetCharacter(actorId), before.Time) !=
                PlaygroundCommunicationRules.Remaining(before.GetCharacter(actorId), fact.At);
            if (eventKinds.Contains((actorId, PlaygroundObservedEventKind.CommunicationBudgetRenewed)) != budgetRenewed) {
                throw new InvalidOperationException("Communication budget renewal must exactly follow the elapsed clock.");
            }
            if (fact.Kind == PlaygroundEventKind.GatherYielded && fact.ActorId == actorId) {
                bool finished = after.GetCharacter(actorId).Activity is null;
                if (eventKinds.Contains((actorId, PlaygroundObservedEventKind.GatherFinished)) != finished ||
                    !fact.ObservedEvents.Any(e => e.ObserverId == actorId && e.Kind == PlaygroundObservedEventKind.GatherYielded &&
                        e.Effects.Any(effect => effect.Key == new PlaygroundStateKey("activity", actorId, "completed")))) {
                    throw new InvalidOperationException("A yield must retain its completed cycle and a distinct ending when finished.");
                }
            }
            if (eventKinds.Contains((actorId, PlaygroundObservedEventKind.TradeParticipantFailed)) !=
                (PlaygroundObservedEvents.IsParticipantFailure(fact) && fact.TradeChanges.Any(offer => offer.Involves(actorId)))) {
                throw new InvalidOperationException("Participant failure must close offers only in each participant's own material.");
            }
            var primaryKind = Enum.Parse<PlaygroundObservedEventKind>(fact.Kind.ToString());
            if (eventKinds.Contains((actorId, primaryKind)) != expectsPrimary ||
                eventKinds.Contains((actorId, PlaygroundObservedEventKind.ExpeditionEnded)) !=
                    (perceivesCause && !after.GetCharacter(actorId).IsActive)) {
                throw new InvalidOperationException("The observed cause or ending is missing or not visible to this observer.");
            }
            if (!PlaygroundObservedEvents.IsFollowingEvent(fact.Kind) &&
                eventKinds.Contains((actorId, PlaygroundObservedEventKind.FollowEnded)) != FollowingChanged(before, after, actorId)) {
                throw new InvalidOperationException("Terminal relationship cleanup must be recorded separately for every affected member.");
            }
            foreach (string field in new[] { "food", "water", "warmth" }) {
                var key = new PlaygroundStateKey("body", actorId, field);
                if (anchor is not null && Vital(anchor.Project(before.Time), field) != Vital(anchor.Project(fact.At), field) && !processes.Contains((actorId, key))) {
                    throw new InvalidOperationException("Elapsed physiological effects are missing.");
                }
            }
            if (current.Any(pair => pair.Value != targetByObserver[actorId][pair.Key])) {
                throw new InvalidOperationException("Recorded observed effects do not reach the committed business endpoints.");
            }
        }
    }

    private static void ValidateParameters(PlaygroundBusinessState before, PlaygroundBusinessState after, PlaygroundFact fact, PlaygroundObservedEvent entry) {
        string? id = null, name = null, detail = null;
        PlaygroundObservedCommunication? communication = null;
        PlaygroundProductionOrder? production = null;
        entry.ValidateTradeChanges();
        IReadOnlyList<PlaygroundTradeOffer> tradeChanges = PlaygroundObservedEvents.IsTradeEvent(entry.Kind)
            ? PlaygroundObservedEvents.VisibleTradeChanges(fact, entry.ObserverId) : [];
        if (entry.Kind == PlaygroundObservedEventKind.CommunicationBudgetRenewed) {
            if (before.Expedition is null || entry.Effects.Count != 1) {
                throw new InvalidOperationException("Budget renewal requires one visible elapsed-budget effect.");
            }
        } else if (entry.Kind is PlaygroundObservedEventKind.FollowStarted or PlaygroundObservedEventKind.FollowEnded or PlaygroundObservedEventKind.FollowersDismissed) {
            if (!PlaygroundObservedEvents.IsFollowingEvent(fact.Kind)) { detail = "Terminal"; }
        } else if (entry.Kind == PlaygroundObservedEventKind.SpeechShouted) {
            id = fact.ActorId;
            name = before.GetCharacter(fact.ActorId!).Name;
            detail = fact.SpeechText;
        } else if (entry.Kind is PlaygroundObservedEventKind.SpeechSent or PlaygroundObservedEventKind.ItemGiven or PlaygroundObservedEventKind.MoneyGiven) {
            communication = new(fact.ActorId!, before.GetCharacter(fact.ActorId!).Name,
                fact.TargetActorId!, before.GetCharacter(fact.TargetActorId!).Name, fact.SpeechText, fact.ItemId, fact.ItemCount, fact.MoneyAmount);
            communication.Validate(entry.ObserverId, entry.Kind);
        } else if (entry.Kind is PlaygroundObservedEventKind.ProductionStarted or PlaygroundObservedEventKind.ProductionCompleted) {
            production = (entry.Kind == PlaygroundObservedEventKind.ProductionStarted
                ? after.GetCharacter(entry.ObserverId) : before.GetCharacter(entry.ObserverId)).Activity!.Production;
        } else if (entry.Kind is PlaygroundObservedEventKind.MarketBought or PlaygroundObservedEventKind.MarketSold) {
            id = fact.ItemId; name = PlaygroundEconomyObservation.ItemName(before.Definition, id!);
        } else if (PlaygroundObservedEvents.IsTradeEvent(entry.Kind)) {
            // The typed participant-scoped payload owns all terms; no location or free-text cause is inferred.
        } else if (entry.Kind == PlaygroundObservedEventKind.BodyChanged) {
            if (before.GetCharacter(entry.ObserverId).Survival is null || entry.Effects.Count == 0) { throw new InvalidOperationException("An elapsed physiological interval requires a body and an actual change."); }
        } else if (entry.Kind == PlaygroundObservedEventKind.ExpeditionEnded) {
            detail = after.GetCharacter(entry.ObserverId).Ending!.Reason;
        } else {
            id = fact.ResourceId;
            name = id is null ? null : before.GetResource(id).Name;
            if (entry.Kind == PlaygroundObservedEventKind.ItemUsed) {
                PlaygroundCharacter actor = before.GetCharacter(entry.ObserverId), next = after.GetCharacter(entry.ObserverId);
                id = actor.Backpack.Items.Keys.SingleOrDefault(item => actor.Backpack.Count(item) > next.Backpack.Count(item));
                name = id is null ? null : PlaygroundEconomyObservation.ItemName(before.Definition, id);
            } else if (entry.Kind is PlaygroundObservedEventKind.MoveStarted or PlaygroundObservedEventKind.MoveArrived) {
                SpatialLocation location = PlaygroundActions.Location(after, entry.ObserverId)!;
                id = location is AtPlaceLocation place ? place.PlaceId.Value : null;
                name = id is null ? null : before.Definition.GetPlace(id).Name;
            } else if (entry.Kind == PlaygroundObservedEventKind.GatherFinished) {
                detail = fact.GatherStopReason?.ToString();
            } else if (entry.Kind == PlaygroundObservedEventKind.WeatherResolved) {
                PlaygroundEnvironment environment = fact.Environment!;
                PlaygroundWeatherEffect weather = environment.LastEffects.Single(effect => effect.ActorId == entry.ObserverId);
                detail = environment.LastCold ? weather.Sheltered ? "ColdWindSheltered" : "ColdWind" : "Calm";
            }
        }
        if (entry.SubjectId != id || entry.SubjectName != name || entry.Detail != detail || entry.Communication != communication || entry.Production != production || !entry.TradeChanges.SequenceEqual(tradeChanges)) {
            throw new InvalidOperationException("Observed event parameters must match its recorded business cause.");
        }
    }
    private static bool Owns(PlaygroundObservedEventKind kind, PlaygroundStateKey key) => kind switch {
        PlaygroundObservedEventKind.FollowStarted => key.Category is "following" or "notice" or "continuity" ||
            key.Category == "communication" && key.Field == "pending" || key.Category == "body" && key.Field.StartsWith("warning-", StringComparison.Ordinal),
        PlaygroundObservedEventKind.FollowEnded or PlaygroundObservedEventKind.FollowersDismissed => key.Category == "following",
        PlaygroundObservedEventKind.CommunicationBudgetRenewed => key.Category == "communication" && key.Field == "remaining",
        PlaygroundObservedEventKind.SpeechSent or PlaygroundObservedEventKind.SpeechShouted => key.Category == "communication" && key.Field is "remaining" or "pending",
        PlaygroundObservedEventKind.TradeOffered => key.Category == "communication" && key.Field is "remaining" or "pending",
        PlaygroundObservedEventKind.TradeAccepted => key.Category is "inventory" or "money" || key.Category == "communication" && key.Field == "pending",
        PlaygroundObservedEventKind.TradeRejected or PlaygroundObservedEventKind.TradeWithdrawn or PlaygroundObservedEventKind.TradesExpired or
            PlaygroundObservedEventKind.TradeParticipantFailed => key.Category == "communication" && key.Field == "pending",
        PlaygroundObservedEventKind.MarketBought or PlaygroundObservedEventKind.MarketSold => key.Category is "inventory" or "money",
        PlaygroundObservedEventKind.MoneyGiven => key.Category == "money" || key.Category == "communication" && key.Field is "remaining" or "pending",
        PlaygroundObservedEventKind.ProductionStarted => key.Category is "activity" or "inventory" or "notice" or "continuity" ||
            key.Category == "communication" && key.Field == "pending" || key.Category == "body" && key.Field.StartsWith("warning-", StringComparison.Ordinal),
        PlaygroundObservedEventKind.ProductionCompleted => key.Category is "inventory" or "activity",
        PlaygroundObservedEventKind.MarketRefreshed => false,
        PlaygroundObservedEventKind.ItemGiven => key.Category == "inventory" || key.Category == "communication" && key.Field is "remaining" or "pending",
        PlaygroundObservedEventKind.BodyChanged => key.Category == "body" && key.Field is "food" or "water" or "warmth",
        PlaygroundObservedEventKind.ExpeditionEnded => key == new PlaygroundStateKey("world", "world", "outcome") ||
            key.Category is "activity" or "notice" || key.Category == "communication" && key.Field == "pending" || key.Category == "body" &&
                (key.Field.EndsWith("-rate", StringComparison.Ordinal) || key.Field.StartsWith("warning-", StringComparison.Ordinal)),
        PlaygroundObservedEventKind.HandoffEdited => key.Category == "continuity",
        PlaygroundObservedEventKind.ItemUsed => key.Category == "inventory" || key.Category == "body" && key.Field is "food" or "water",
        PlaygroundObservedEventKind.ResourceRegenerated or PlaygroundObservedEventKind.ResourceDestroyed => key.Category is "resource" or "notice",
        PlaygroundObservedEventKind.LightChanged => key.Category == "world" && key.Field == "light" || key.Category == "body" && key.Field.EndsWith("-rate", StringComparison.Ordinal),
        PlaygroundObservedEventKind.WeatherResolved => key.Category == "inventory" || key.Category == "body" && key.Field is "warmth" or "warning-warmth",
        PlaygroundObservedEventKind.SurvivalWarning => key.Category == "body" && key.Field.StartsWith("warning-", StringComparison.Ordinal),
        PlaygroundObservedEventKind.SurvivalFailed => key.Category == "inventory",
        PlaygroundObservedEventKind.AttentionAcknowledged => key.Category is "notice" or "continuity" || key.Category == "communication" && key.Field == "pending" || key.Category == "body" && key.Field.StartsWith("warning-", StringComparison.Ordinal),
        PlaygroundObservedEventKind.GatherYielded => key.Category is "inventory" or "resource" or "notice" || key.Category == "activity" && key.Field is "completed" or "due",
        PlaygroundObservedEventKind.GatherFinished or PlaygroundObservedEventKind.WaitCompleted => key.Category == "activity",
        PlaygroundObservedEventKind.WaitStarted or PlaygroundObservedEventKind.GatherStarted or PlaygroundObservedEventKind.ActivityCancelled => key.Category is "activity" or "inventory" or "notice" or "continuity" || key.Category == "communication" && key.Field == "pending" || key.Category == "body" && key.Field.StartsWith("warning-", StringComparison.Ordinal),
        PlaygroundObservedEventKind.MoveStarted or PlaygroundObservedEventKind.MoveArrived => key.Category is "location" or "activity" or "notice" or "continuity" || key.Category == "communication" && key.Field == "pending" || key.Category == "body" && (key.Field is "sheltered" or "warmth-rate" || key.Field.StartsWith("warning-", StringComparison.Ordinal)),
        _ => false
    };

    private static IEnumerable<PlaygroundStateKey> Keys(PlaygroundBusinessState before, PlaygroundCharacter actor, PlaygroundCharacter next, PlaygroundFact fact) {
        if (actor.Survival is not null) {
            foreach (string field in new[] { "food", "water", "warmth", "food-rate", "water-rate", "warmth-rate", "sheltered", "warning-food", "warning-water", "warning-warmth" }) { yield return new("body", actor.Id, field); }
        }
        bool subjectSelf = fact.ActorId == actor.Id;
        bool ownOperation = subjectSelf && fact.OperationRecordId is not null;
        bool movedSelf = PlaygroundObservedEvents.MovedSelf(fact, actor.Id);
        bool received = PlaygroundObservedEvents.ReceivesCommunication(fact, actor.Id);
        bool ending = !next.IsActive;
        bool resourceCause = fact.Kind is PlaygroundEventKind.ResourceRegenerated or PlaygroundEventKind.ResourceDestroyed;
        // Other characters' actions may update this observer's visible stock or notices. Their
        // observation snapshot owns those updates until a dedicated perceived cause is modeled.
        if (before.Expedition is not null) {
            yield return new("communication", actor.Id, "remaining");
            yield return new("communication", actor.Id, "pending");
            yield return new("following", actor.Id, "leader");
            foreach (PlaygroundCharacter character in before.Characters) {
                yield return new("following", actor.Id, $"follower:{character.Id}");
            }
        }
        if (subjectSelf || received || ending) {
            yield return new("money", actor.Id, "balance");
            foreach (string item in actor.Backpack.Items.Keys.Concat(next.Backpack.Items.Keys).Distinct(StringComparer.Ordinal)) { yield return new("inventory", item, "count"); }
        }
        // Draft consumption belongs to a captured character operation, never an indirect group movement.
        if (ownOperation) { yield return new("continuity", actor.Id, "draft"); }
        if (subjectSelf || movedSelf) {
            if (before.Expedition is not null) {
                yield return new("location", actor.Id, "place");
                yield return new("location", actor.Id, "arrival");
            }
        }
        if (subjectSelf || ending) {
            foreach (string field in new[] { "kind", "due", "completed", "target" }) { yield return new("activity", actor.Id, field); }
        }
        if (subjectSelf || ending || resourceCause) { yield return new("notice", actor.Id, "count"); }
        yield return new("world", "world", "light");
        if (before.Expedition is not null) { yield return new("world", "world", "outcome"); }
        if (subjectSelf || resourceCause) {
            SpatialLocation? location = PlaygroundActions.Location(before, actor.Id);
            foreach (PlaygroundResource resource in before.Resources.Where(r => before.Expedition is null ||
                location is AtPlaceLocation place && before.Definition.GetResourcePlace(r.Id) == place.PlaceId.Value)) {
                yield return new("resource", resource.Id, "stock");
            }
        }
    }

    private static PlaygroundVisibleValue Read(PlaygroundBusinessState state, string observer, PlaygroundStateKey key) {
        PlaygroundCharacter actor = state.GetCharacter(observer);
        PlaygroundSurvival? body = actor.Survival?.Project(state.Time);
        PlaygroundActivity? activity = actor.Activity;
        SpatialLocation? location = PlaygroundActions.Location(state, observer);
        if (key.Category is "body" or "activity" or "location" or "notice" or "continuity" or "communication" or "following" or "money" && key.ObjectId != observer) {
            throw new InvalidOperationException("Private effects belong to their observer.");
        }
        return (key.Category, key.Field) switch {
            ("body", "food" or "water" or "warmth") => Vital(body!, key.Field),
            ("body", "food-rate") => PlaygroundVisibleValue.Numeric(body!.FoodPerHour, "点/小时"),
            ("body", "water-rate") => PlaygroundVisibleValue.Numeric(body!.WaterPerHour, "点/小时"),
            ("body", "warmth-rate") => PlaygroundVisibleValue.Numeric(body!.WarmthPerHour, "点/小时"),
            ("body", "sheltered") => PlaygroundVisibleValue.Flag(location is AtPlaceLocation shelter && state.Definition.GetPlace(shelter.PlaceId.Value).Sheltered),
            ("body", "warning-food") => PlaygroundVisibleValue.Flag((body!.PendingMask & 1) != 0),
            ("body", "warning-water") => PlaygroundVisibleValue.Flag((body!.PendingMask & 2) != 0),
            ("body", "warning-warmth") => PlaygroundVisibleValue.Flag((body!.PendingMask & 4) != 0),
            ("money", "balance") => PlaygroundVisibleValue.Numeric(actor.Money, "钱"),
            ("inventory", "count") => PlaygroundVisibleValue.Numeric(actor.Backpack.Count(key.ObjectId), "份"),
            ("activity", "kind") => Text(activity?.Kind.ToString()),
            ("activity", "due") => activity is null ? PlaygroundVisibleValue.KnownAbsent : PlaygroundVisibleValue.Numeric(activity.Due.Ticks, "ticks"),
            ("activity", "completed") => activity?.Kind != PlaygroundActivityKind.Gather ? PlaygroundVisibleValue.KnownAbsent : PlaygroundVisibleValue.Numeric(activity.CompletedCount, "份"),
            ("activity", "target") => activity?.Kind != PlaygroundActivityKind.Gather ? PlaygroundVisibleValue.KnownAbsent : PlaygroundVisibleValue.Numeric(activity.TargetCount, "份"),
            ("notice", "count") => PlaygroundVisibleValue.Numeric(actor.Notices.Count, "条"),
            ("continuity", "draft") => Text(state.InteractionStates[observer].DraftHandoff),
            ("communication", "remaining") => PlaygroundVisibleValue.Numeric(PlaygroundCommunicationRules.Remaining(actor, state.Time), "次"),
            ("communication", "pending") => PlaygroundVisibleValue.Flag(actor.Communication.AttentionPending),
            ("following", "leader") => Text(actor.FollowingLeaderId),
            ("following", var field) when field.StartsWith("follower:", StringComparison.Ordinal) =>
                PlaygroundVisibleValue.Flag(state.GetCharacter(field[9..]).FollowingLeaderId == observer),
            ("world", "light") => PlaygroundVisibleValue.Flag(state.Light.IsDay),
            // The key is actor-scoped presentation material despite its historical category name.
            ("world", "outcome") => PlaygroundVisibleValue.Literal((actor.Ending?.Outcome ?? PlaygroundExpeditionOutcome.Exploring).ToString()),
            ("location", "place") => location is AtPlaceLocation at ? Text(at.PlaceId.Value) : PlaygroundVisibleValue.KnownAbsent,
            ("location", "arrival") => location is TraversingLocation moving ? PlaygroundVisibleValue.Numeric(moving.ArrivalDue.Ticks, "ticks") : PlaygroundVisibleValue.KnownAbsent,
            ("resource", "stock") => PlaygroundVisibleValue.Numeric(state.GetResource(key.ObjectId).Stock, "份"),
            _ => throw new InvalidOperationException("Unknown explanatory state key.")
        };
    }
    private static PlaygroundVisibleValue Vital(PlaygroundSurvival body, string field) => PlaygroundVisibleValue.Numeric(field switch {
        "food" => body.FoodUnits, "water" => body.WaterUnits, "warmth" => body.WarmthUnits,
        _ => throw new InvalidOperationException("Not a physiological amount.")
    }, "点", PlaygroundSurvival.Scale);
    private static PlaygroundVisibleValue Text(string? text) => text is null ? PlaygroundVisibleValue.KnownAbsent : PlaygroundVisibleValue.Literal(text);

    private static bool FollowingChanged(PlaygroundBusinessState before, PlaygroundBusinessState after, string observerId) =>
        before.GetCharacter(observerId).FollowingLeaderId != after.GetCharacter(observerId).FollowingLeaderId ||
        before.Characters.Any(character => (character.FollowingLeaderId == observerId) !=
            (after.GetCharacter(character.Id).FollowingLeaderId == observerId));
}
