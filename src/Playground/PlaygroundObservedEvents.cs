using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.Playground;

/// <summary>Application-specific explanatory material, assembled after every business enrichment.</summary>
internal static class PlaygroundObservedEvents {
    internal static PlaygroundFact Attach(PlaygroundWorld world, PlaygroundFact fact, string? draftAfter = null) {
        if (fact.Kind is PlaygroundEventKind.DecisionContextOpened or PlaygroundEventKind.GoalOperation) { return fact; }
        var events = new List<PlaygroundObservedEvent>();
        GraphSpatialState? afterSpatial = world.Expedition?.Spatial;
        foreach (GraphSpatialFact movement in fact.SpatialFacts) {
            afterSpatial = world.Definition.Runtime.Reducer.Apply(afterSpatial!, new(fact.At, 0), movement);
        }
        PlaygroundCharacter[] afterCharacters = world.Characters.Select(character =>
            fact.ChangedCharacters.SingleOrDefault(changed => changed.Id == character.Id) ?? character).ToArray();
        foreach (PlaygroundCharacter before in world.Characters) {
            if (!before.IsActive) { continue; }
            PlaygroundCharacter after = fact.ChangedCharacters.SingleOrDefault(c => c.Id == before.Id) ?? before;
            bool ended = !after.IsActive;
            int priorRemaining = PlaygroundCommunicationRules.Remaining(before, world.Time);
            int elapsedRemaining = PlaygroundCommunicationRules.Remaining(before, fact.At);
            if (world.Expedition is not null && priorRemaining != elapsedRemaining) {
                Emit(PlaygroundObservedEventKind.CommunicationBudgetRenewed,
                    [new(new("communication", before.Id, "remaining"), Number(priorRemaining, "次"), Number(elapsedRemaining, "次"))], from: world.Time);
            }
            PlaygroundSurvival? integrated = before.Survival?.Project(fact.At);
            PlaygroundSurvival? final = after.Survival?.Project(fact.At);
            List<PlaygroundObservedEffect> process = [];
            if (before.Survival is { } body) { AddVitals(process, before.Id, body.Project(world.Time), integrated!); }
            if (process.Count > 0) { Emit(PlaygroundObservedEventKind.BodyChanged, process, from: world.Time); }

            bool subjectSelf = fact.ActorId == before.Id;
            bool ownOperation = subjectSelf && fact.OperationRecordId is not null;
            bool movedSelf = MovedSelf(fact, before.Id);
            List<PlaygroundObservedEffect> following = FollowingEffects(world.Characters, afterCharacters, before.Id);
            bool relationCommand = IsFollowingEvent(fact.Kind);
            if (following.Count > 0 && !relationCommand) {
                // A leader's terminal cause is not the surviving follower's own death or arrival.
                Emit(PlaygroundObservedEventKind.FollowEnded, following, detail: "Terminal");
            }
            bool tradeFailure = IsParticipantFailure(fact) && fact.TradeChanges.Any(offer => offer.Involves(before.Id));
            if (tradeFailure) {
                List<PlaygroundObservedEffect> tradeEffects = [];
                if (!ended) {
                    Add(tradeEffects, new("communication", before.Id, "pending"), PlaygroundVisibleValue.Flag(before.Communication.AttentionPending),
                        PlaygroundVisibleValue.Flag(after.Communication.AttentionPending));
                }
                // A distant counterparty learns only that their offer closed, not another actor's death scene.
                Emit(PlaygroundObservedEventKind.TradeParticipantFailed, tradeEffects);
            }
            bool received = ReceivesCommunication(fact, before.Id);
            bool visibleResource = fact.ResourceId is { } resourceId &&
                (world.Expedition is null || PlaygroundActions.CurrentPlace(world, before.Id) is { } place && world.Definition.GetResourcePlace(resourceId) == place);
            bool publicEvent = fact.Kind == PlaygroundEventKind.MarketRefreshed && PlaygroundActions.CurrentPlace(world, before.Id) == world.Definition.MarketPlaceId || fact.Kind is PlaygroundEventKind.LightChanged or PlaygroundEventKind.WeatherResolved || visibleResource &&
                fact.Kind is PlaygroundEventKind.ResourceRegenerated or PlaygroundEventKind.ResourceDestroyed;
            if (!subjectSelf && !received && !publicEvent && !movedSelf && !(relationCommand && following.Count > 0)) { continue; }
            var direct = new List<PlaygroundObservedEffect>();
            if (relationCommand) { direct.AddRange(following); }
            var ending = new List<PlaygroundObservedEffect>();
            List<PlaygroundObservedEffect> lifecycle = ended ? ending : direct;
            string? subjectId = fact.ResourceId;
            string? subjectName = subjectId is null ? null : world.GetResource(subjectId).Name;
            string? detail = null;
            PlaygroundObservedEventKind kind = Enum.Parse<PlaygroundObservedEventKind>(fact.Kind.ToString());
            // Direct physiology is independent of the preceding elapsed-time interval.
            if (integrated is not null && final is not null) { AddVitals(direct, before.Id, integrated, final); }
            if (subjectSelf || received || ended) {
                Add(direct, new("money", before.Id, "balance"), Number(before.Money, "钱"), Number(after.Money, "钱"));
                foreach (string item in before.Backpack.Items.Keys.Concat(after.Backpack.Items.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) {
                    Add(direct, new("inventory", item, "count"), Number(before.Backpack.Count(item), "份"), Number(after.Backpack.Count(item), "份"));
                    if (fact.Kind == PlaygroundEventKind.ItemUsed && before.Backpack.Count(item) != after.Backpack.Count(item)) {
                        subjectId = item; subjectName = PlaygroundEconomyObservation.ItemName(world.Definition, item);
                    }
                }
            }
            if (integrated is not null && final is not null) {
                Add(lifecycle, new("body", before.Id, "food-rate"), Number(integrated.FoodPerHour, "点/小时"), Number(final.FoodPerHour, "点/小时"));
                Add(lifecycle, new("body", before.Id, "water-rate"), Number(integrated.WaterPerHour, "点/小时"), Number(final.WaterPerHour, "点/小时"));
                Add(lifecycle, new("body", before.Id, "warmth-rate"), Number(integrated.WarmthPerHour, "点/小时"), Number(final.WarmthPerHour, "点/小时"));
                SpatialLocation? finalLocation = after.Ending?.Location ?? afterSpatial?.Entities.SingleOrDefault(e => e.Id.Value == before.Id)?.Location;
                Add(direct, new("body", before.Id, "sheltered"), PlaygroundVisibleValue.Flag(PlaygroundSurvivalRules.IsSheltered(world.Definition, world.Expedition!.Spatial, before.Id)),
                    PlaygroundVisibleValue.Flag(finalLocation is AtPlaceLocation sheltered && world.Definition.GetPlace(sheltered.PlaceId.Value).Sheltered));
                foreach ((int mask, string field) in new[] { (1, "warning-food"), (2, "warning-water"), (4, "warning-warmth") }) {
                    Add(lifecycle, new("body", before.Id, field), PlaygroundVisibleValue.Flag((integrated.PendingMask & mask) != 0), PlaygroundVisibleValue.Flag((final.PendingMask & mask) != 0));
                }
            }
            Add(lifecycle, new("notice", before.Id, "count"), Number(before.Notices.Count, "条"), Number(after.Notices.Count, "条"));
            if (world.Expedition is not null) {
                Add(direct, new("communication", before.Id, "remaining"), Number(elapsedRemaining, "次"),
                    Number(PlaygroundCommunicationRules.Remaining(after, fact.At), "次"));
                if (!tradeFailure || ended) {
                    Add(lifecycle, new("communication", before.Id, "pending"), PlaygroundVisibleValue.Flag(before.Communication.AttentionPending),
                        PlaygroundVisibleValue.Flag(after.Communication.AttentionPending));
                }
            }
            if (visibleResource && fact.ChangedResources.SingleOrDefault(r => r.Id == fact.ResourceId) is { } changedResource) {
                Add(direct, new("resource", changedResource.Id, "stock"), Number(world.GetResource(changedResource.Id).Stock, "份"), Number(changedResource.Stock, "份"));
            }
            if (fact.Kind == PlaygroundEventKind.LightChanged) {
                Add(direct, new("world", "world", "light"), PlaygroundVisibleValue.Flag(world.Light.IsDay), PlaygroundVisibleValue.Flag(fact.Light!.IsDay));
            }
            if (movedSelf) {
                SpatialLocation priorLocation = world.Expedition!.Spatial.Entities.Single(e => e.Id.Value == before.Id).Location;
                SpatialLocation nextLocation = after.Ending?.Location ?? afterSpatial!.Entities.Single(e => e.Id.Value == before.Id).Location;
                Add(direct, new("location", before.Id, "place"), PlaceValue(priorLocation), PlaceValue(nextLocation));
                Add(direct, new("location", before.Id, "arrival"), ArrivalValue(priorLocation), ArrivalValue(nextLocation));
                subjectId = nextLocation is AtPlaceLocation atPlace ? atPlace.PlaceId.Value : null;
                subjectName = subjectId is null ? null : world.Definition.GetPlace(subjectId).Name;
            }
            if (ownOperation) {
                Add(direct, new("continuity", before.Id, "draft"), Text(world.Business.InteractionStates[before.Id].DraftHandoff),
                    Text(draftAfter));
            }
            if (fact.Kind == PlaygroundEventKind.WeatherResolved) {
                PlaygroundWeatherEffect weather = fact.Environment!.LastEffects.Single(effect => effect.ActorId == before.Id);
                detail = fact.Environment.LastCold ? weather.Sheltered ? "ColdWindSheltered" : "ColdWind" : "Calm";
            }
            if (fact.Kind is PlaygroundEventKind.MarketBought or PlaygroundEventKind.MarketSold) {
                subjectId = fact.ItemId; subjectName = PlaygroundEconomyObservation.ItemName(world.Definition, fact.ItemId!);
            }
            if (fact.Kind == PlaygroundEventKind.GatherFinished) { detail = fact.GatherStopReason?.ToString(); }
            if (fact.Kind == PlaygroundEventKind.SpeechShouted) {
                subjectId = fact.ActorId;
                subjectName = world.GetCharacter(fact.ActorId!).Name;
                detail = fact.SpeechText;
            }
            // A successful final yield owns the transfer; its separate ending owns the lifecycle removal.
            if (subjectSelf && fact.Kind == PlaygroundEventKind.GatherYielded && after.Activity is null) {
                if (before.Activity is { } gathering) {
                    Add(direct, new("activity", before.Id, "completed"), Number(gathering.CompletedCount, "份"), Number(gathering.CompletedCount + 1, "份"));
                }
                Emit(kind, direct, subjectId, subjectName, detail);
                var gatheringEnded = new List<PlaygroundObservedEffect>();
                AddActivity(gatheringEnded, before.Id, before.Activity, null, finalYield: true);
                Emit(PlaygroundObservedEventKind.GatherFinished, gatheringEnded, subjectId, subjectName, fact.GatherStopReason?.ToString());
            } else {
                if (subjectSelf || ended) { AddActivity(lifecycle, before.Id, before.Activity, after.Activity); }
                // Empty acknowledgement/handoff operations have an operation answer, but no experienced change.
                if (direct.Count > 0 || fact.Kind is not (PlaygroundEventKind.AttentionAcknowledged or PlaygroundEventKind.HandoffEdited)) {
                    Emit(kind, direct, subjectId, subjectName, detail);
                }
            }
            if (ended) {
                // This legacy presentation key belongs to this observer's journey, never the global summary.
                ending.Add(new(new("world", "world", "outcome"), PlaygroundVisibleValue.Literal(PlaygroundExpeditionOutcome.Exploring.ToString()),
                    PlaygroundVisibleValue.Literal(after.Ending!.Outcome.ToString())));
                Emit(PlaygroundObservedEventKind.ExpeditionEnded, ending, detail: after.Ending.Reason);
            }

            void Emit(PlaygroundObservedEventKind eventKind, IEnumerable<PlaygroundObservedEffect> effects, string? id = null,
                string? name = null, string? detail = null, ModelTime? from = null) =>
                events.Add(new(events.Count, before.Id, eventKind, from ?? fact.At, fact.At, id, name, detail, effects,
                    eventKind is PlaygroundObservedEventKind.SpeechSent or PlaygroundObservedEventKind.ItemGiven or PlaygroundObservedEventKind.MoneyGiven
                        ? new(fact.ActorId!, world.GetCharacter(fact.ActorId!).Name, fact.TargetActorId!,
                            world.GetCharacter(fact.TargetActorId!).Name, fact.SpeechText, fact.ItemId, fact.ItemCount, fact.MoneyAmount)
                        : null,
                    eventKind == PlaygroundObservedEventKind.ProductionStarted ? after.Activity!.Production :
                        eventKind == PlaygroundObservedEventKind.ProductionCompleted ? before.Activity!.Production : null,
                    IsTradeEvent(eventKind) ? VisibleTradeChanges(fact, before.Id) : null));
        }
        return fact.WithObservedEvents(events);
    }

    internal static bool IsParticipantFailure(PlaygroundFact fact) =>
        fact.Kind is PlaygroundEventKind.SurvivalFailed or PlaygroundEventKind.WeatherResolved;
    internal static IReadOnlyList<PlaygroundTradeOffer> VisibleTradeChanges(PlaygroundFact fact, string actorId) =>
        Array.AsReadOnly(fact.TradeChanges.Where(offer => offer.Involves(actorId)).Select(PlaygroundEconomyObservation.VisibleTrade).ToArray());
    internal static bool IsTradeEvent(PlaygroundObservedEventKind kind) => kind is PlaygroundObservedEventKind.TradeOffered or
        PlaygroundObservedEventKind.TradeAccepted or PlaygroundObservedEventKind.TradeRejected or PlaygroundObservedEventKind.TradeWithdrawn or
        PlaygroundObservedEventKind.TradesExpired or PlaygroundObservedEventKind.TradeParticipantFailed;
    internal static bool IsTradeEvent(PlaygroundEventKind kind) => kind is PlaygroundEventKind.TradeOffered or
        PlaygroundEventKind.TradeAccepted or PlaygroundEventKind.TradeRejected or PlaygroundEventKind.TradeWithdrawn or PlaygroundEventKind.TradesExpired;

    internal static bool ReceivesCommunication(PlaygroundFact fact, string actorId) =>
        IsTradeEvent(fact.Kind) ? fact.TradeChanges.Any(offer => offer.Involves(actorId)) :
        fact.Kind == PlaygroundEventKind.SpeechShouted
            ? fact.ActorId != actorId && fact.ChangedCharacters.Any(character => character.Id == actorId)
            : fact.TargetActorId == actorId && fact.Kind is PlaygroundEventKind.SpeechSent or PlaygroundEventKind.ItemGiven or PlaygroundEventKind.MoneyGiven;

    internal static bool MovedSelf(PlaygroundFact fact, string actorId) => fact.SpatialFacts.Any(movement => movement switch {
        TraversalStartedFact started => started.EntityId.Value == actorId,
        TraversalArrivedFact arrived => arrived.EntityId.Value == actorId,
        _ => false
    });

    internal static bool IsFollowingEvent(PlaygroundEventKind kind) =>
        kind is PlaygroundEventKind.FollowStarted or PlaygroundEventKind.FollowEnded or PlaygroundEventKind.FollowersDismissed;

    private static List<PlaygroundObservedEffect> FollowingEffects(IReadOnlyList<PlaygroundCharacter> before,
        IReadOnlyList<PlaygroundCharacter> after, string observerId) {
        List<PlaygroundObservedEffect> effects = [];
        Add(effects, new("following", observerId, "leader"), Text(before.Single(c => c.Id == observerId).FollowingLeaderId),
            Text(after.Single(c => c.Id == observerId).FollowingLeaderId));
        foreach (PlaygroundCharacter character in before) {
            Add(effects, new("following", observerId, $"follower:{character.Id}"),
                PlaygroundVisibleValue.Flag(character.FollowingLeaderId == observerId),
                PlaygroundVisibleValue.Flag(after.Single(c => c.Id == character.Id).FollowingLeaderId == observerId));
        }
        return effects;
    }

    private static void AddVitals(List<PlaygroundObservedEffect> effects, string actorId, PlaygroundSurvival before, PlaygroundSurvival after) {
        Add(effects, new("body", actorId, "food"), Number(before.FoodUnits, "点", PlaygroundSurvival.Scale), Number(after.FoodUnits, "点", PlaygroundSurvival.Scale));
        Add(effects, new("body", actorId, "water"), Number(before.WaterUnits, "点", PlaygroundSurvival.Scale), Number(after.WaterUnits, "点", PlaygroundSurvival.Scale));
        Add(effects, new("body", actorId, "warmth"), Number(before.WarmthUnits, "点", PlaygroundSurvival.Scale), Number(after.WarmthUnits, "点", PlaygroundSurvival.Scale));
    }
    private static void AddActivity(List<PlaygroundObservedEffect> effects, string actorId, PlaygroundActivity? before, PlaygroundActivity? after, bool finalYield = false) {
        Add(effects, new("activity", actorId, "kind"), Text(before?.Kind.ToString()), Text(after?.Kind.ToString()));
        Add(effects, new("activity", actorId, "due"), before is null ? PlaygroundVisibleValue.KnownAbsent : Number(before.Due.Ticks, "ticks"), after is null ? PlaygroundVisibleValue.KnownAbsent : Number(after.Due.Ticks, "ticks"));
        Add(effects, new("activity", actorId, "completed"), before?.Kind == PlaygroundActivityKind.Gather ? Number(before.CompletedCount + (finalYield ? 1 : 0), "份") : PlaygroundVisibleValue.KnownAbsent,
            after?.Kind == PlaygroundActivityKind.Gather ? Number(after.CompletedCount, "份") : PlaygroundVisibleValue.KnownAbsent);
        Add(effects, new("activity", actorId, "target"), before?.Kind == PlaygroundActivityKind.Gather ? Number(before.TargetCount, "份") : PlaygroundVisibleValue.KnownAbsent,
            after?.Kind == PlaygroundActivityKind.Gather ? Number(after.TargetCount, "份") : PlaygroundVisibleValue.KnownAbsent);
    }
    private static void Add(List<PlaygroundObservedEffect> effects, PlaygroundStateKey key, PlaygroundVisibleValue before, PlaygroundVisibleValue after) {
        if (before != after) { effects.Add(new(key, before, after)); }
    }
    private static PlaygroundVisibleValue Number(long number, string unit, long scale = 1) => PlaygroundVisibleValue.Numeric(number, unit, scale);
    private static PlaygroundVisibleValue Text(string? text) => text is null ? PlaygroundVisibleValue.KnownAbsent : PlaygroundVisibleValue.Literal(text);
    private static PlaygroundVisibleValue PlaceValue(SpatialLocation location) => location is AtPlaceLocation at ? Text(at.PlaceId.Value) : PlaygroundVisibleValue.KnownAbsent;
    private static PlaygroundVisibleValue ArrivalValue(SpatialLocation location) => location is TraversingLocation moving ? Number(moving.ArrivalDue.Ticks, "ticks") : PlaygroundVisibleValue.KnownAbsent;
}
