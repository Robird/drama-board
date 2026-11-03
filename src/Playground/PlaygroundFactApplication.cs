using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.Playground;

/// <summary>Applies recorded business replacements without planning effects or maintaining runtime forecasts.</summary>
internal static class PlaygroundFactApplication {
    internal static PlaygroundBusinessState Apply(PlaygroundBusinessState business, LogicalInstant instant, PlaygroundFact fact) {
        ArgumentNullException.ThrowIfNull(business);
        ArgumentNullException.ThrowIfNull(fact);
        if (fact.At != instant.ModelTime || fact.At < business.Time) {
            throw new InvalidOperationException("A fact must apply at its recorded time and cannot move time backwards.");
        }
        ValidateFact(business, fact);
        PlaygroundEconomyValidation.Validate(business, fact);
        PlaygroundBusinessState result = ApplyReplacements(business, instant, fact);
        ValidateRecordedVisits(business, result, fact);
        ValidateRecordedKnowledge(business, fact);
        PlaygroundPathRules.Validate(business, result, fact);
        PlaygroundObservedEventValidation.Validate(business, result, fact);
        PlaygroundGoalInteractionRules.Validate(business, result, fact);
        PlaygroundInteractionValidation.Validate(business, result, fact);
        return result;
    }
    private static void ValidateRecordedVisits(PlaygroundBusinessState before, PlaygroundBusinessState after, PlaygroundFact fact) {
        if (before.Expedition is null || fact.Kind is PlaygroundEventKind.DecisionContextOpened or PlaygroundEventKind.OperationRejected or
            PlaygroundEventKind.HandoffEdited or PlaygroundEventKind.GoalOperation or PlaygroundEventKind.MapQueried or PlaygroundEventKind.PathQueried or PlaygroundEventKind.SpeechShouted) {
            if (fact.Visits.Count != 0) { throw new InvalidOperationException("This event cannot create geographic knowledge."); }
            return;
        }
        var expected = new List<(string ActorId, string PlaceId)>();
        foreach (PlaygroundCharacter actor in before.Characters.Where(value => value.IsActive)) {
            SpatialLocation? location = PlaygroundActions.Location(after, actor.Id);
            if (fact.Kind == PlaygroundEventKind.MoveStarted && fact.SpatialFacts.OfType<TraversalStartedFact>().Any(value => value.EntityId.Value == actor.Id)) {
                location = PlaygroundActions.Location(before, actor.Id);
            }
            if (location is AtPlaceLocation at) { expected.Add((actor.Id, at.PlaceId.Value)); }
        }
        if (!expected.SequenceEqual(fact.Visits.Select(visit => (visit.ActorId, visit.PlaceId)))) {
            throw new InvalidOperationException("Visits must record exactly the active observers' actual places.");
        }
        foreach (PlaygroundVisit visit in fact.Visits) {
            PlaygroundResource[] visible = after.Resources.Where(resource => after.Definition.GetResourcePlace(resource.Id) == visit.PlaceId).ToArray();
            if (visit.At != fact.At || visit.Resources.Count != visible.Length ||
                !visit.Resources.Zip(visible).All(pair => PlaygroundObservationEquality.SameResource(pair.First, pair.Second))) {
                throw new InvalidOperationException("Visit time and resource snapshots must match the recorded boundary.");
            }
        }
    }
    private static void ValidateRecordedKnowledge(PlaygroundBusinessState before, PlaygroundFact fact) {
        PlaygroundConfirmedPassage[] expected = [];
        if (fact.Kind == PlaygroundEventKind.MoveArrived && fact.SpatialFacts.Count == 1 &&
            fact.SpatialFacts[0] is TraversalArrivedFact arrived && fact.ActorId == arrived.EntityId.Value &&
            PlaygroundActions.Location(before, arrived.EntityId.Value) is TraversingLocation movement &&
            !before.Expedition!.KnowsPassage(arrived.EntityId.Value, movement.PassageId.Value)) {
            expected = [new(arrived.EntityId.Value, movement.PassageId.Value)];
        }
        if (!expected.SequenceEqual(fact.ConfirmedPassages)) {
            throw new InvalidOperationException("Only the traveller's recorded complete arrival can confirm a passage.");
        }
    }
    // Planning-only candidate: no material validation until the event and interaction replacements exist.
    internal static PlaygroundBusinessState ApplyReplacements(PlaygroundBusinessState business, LogicalInstant instant, PlaygroundFact fact) {
        PlaygroundExpedition? expedition = business.Expedition;
        if (expedition is not null) {
            GraphSpatialState spatial = expedition.Spatial;
            foreach (GraphSpatialFact spatialFact in fact.SpatialFacts) {
                spatial = business.Definition.Runtime.Reducer.Apply(spatial, instant, spatialFact);
            }
            expedition = expedition.Change(spatial, fact.Visits, fact.Environment, fact.ConfirmedPassages,
                fact.Kind == PlaygroundEventKind.PathQueried ? fact.ActorId : null, fact.CandidatePath,
                fact.ChangedCharacters.Where(c => !c.IsActive).Select(c => c.Id));
        }
        return business.Replace(fact.At, fact.Light, fact.ChangedResources, fact.Script,
            fact.ChangedCharacters, fact.LastServedActorId, expedition, fact.InteractionChanges, fact.Economy);
    }
    internal static bool SameFoldedBusiness(PlaygroundBusinessState expected, PlaygroundBusinessState actual) =>
        expected.Time == actual.Time && ReferenceEquals(expected.Light, actual.Light) && ReferenceEquals(expected.Script, actual.Script) &&
        ReferenceEquals(expected.Economy, actual.Economy) && ReferenceEquals(expected.Definition, actual.Definition) &&
        expected.LastServedActorId == actual.LastServedActorId && expected.Resources.Count == actual.Resources.Count &&
        expected.Resources.Zip(actual.Resources).All(pair => ReferenceEquals(pair.First, pair.Second)) &&
        expected.Characters.Count == actual.Characters.Count && expected.Characters.Zip(actual.Characters).All(pair => ReferenceEquals(pair.First, pair.Second)) &&
        expected.InteractionStates.Count == actual.InteractionStates.Count &&
        expected.InteractionStates.All(pair => actual.InteractionStates.TryGetValue(pair.Key, out var state) && ReferenceEquals(pair.Value, state)) &&
        (expected.Expedition is null ? actual.Expedition is null : actual.Expedition is not null && expected.Expedition.SameState(actual.Expedition));

    private static void ValidateFact(PlaygroundBusinessState business, PlaygroundFact fact) {
        if (!Enum.IsDefined(fact.Kind) ||
            fact.InteractionChanges.Select(state => state.ActorId).Distinct(StringComparer.Ordinal).Count() != fact.InteractionChanges.Count ||
            fact.InteractionChanges.Any(state => !business.InteractionStates.ContainsKey(state.ActorId)) ||
            fact.ChangedResources.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != fact.ChangedResources.Count ||
            fact.ChangedCharacters.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != fact.ChangedCharacters.Count ||
            fact.ChangedResources.Any(r => !business.Resources.Any(old => old.Id == r.Id)) ||
            fact.ChangedCharacters.Any(c => !business.Characters.Any(old => old.Id == c.Id)) ||
            (fact.ResourceId is not null && !business.Resources.Any(r => r.Id == fact.ResourceId)) ||
            (fact.ActorId is not null && !business.Characters.Any(c => c.Id == fact.ActorId))) {
            throw new InvalidOperationException("Fact contains unknown or duplicate identities.");
        }
        bool actorEffect = fact.ActorId is not null && fact.ChangedCharacters.Any(c => c.Id == fact.ActorId);
        bool oneResource = fact.ChangedResources.Count == 1 && fact.ChangedResources[0].Id == fact.ResourceId;
        bool successfulDecision = fact.Kind is PlaygroundEventKind.WaitStarted or PlaygroundEventKind.GatherStarted or PlaygroundEventKind.ActivityCancelled or PlaygroundEventKind.AttentionAcknowledged or PlaygroundEventKind.MoveStarted or PlaygroundEventKind.ItemUsed or PlaygroundEventKind.HandoffEdited or PlaygroundEventKind.GoalOperation or PlaygroundEventKind.MapQueried or PlaygroundEventKind.PathQueried or PlaygroundEventKind.SpeechSent or PlaygroundEventKind.ItemGiven or PlaygroundEventKind.SpeechShouted or
            PlaygroundEventKind.FollowStarted or PlaygroundEventKind.FollowEnded or PlaygroundEventKind.FollowersDismissed or PlaygroundEventKind.MarketBought or PlaygroundEventKind.MarketSold or PlaygroundEventKind.ProductionStarted or PlaygroundEventKind.MoneyGiven or PlaygroundEventKind.TradeOffered or PlaygroundEventKind.TradeAccepted or PlaygroundEventKind.TradeRejected or PlaygroundEventKind.TradeWithdrawn;
        bool rejected = fact.Kind == PlaygroundEventKind.OperationRejected;
        bool operation = successfulDecision || rejected;
        if (operation ? string.IsNullOrWhiteSpace(fact.ToolCallId) || string.IsNullOrWhiteSpace(fact.OperationRecordId) || fact.RawInput is null ||
                fact.OperationDisposition != (rejected ? PlaygroundOperationDisposition.Rejected : PlaygroundOperationDisposition.Succeeded)
            : fact.ToolCallId is not null || fact.OperationRecordId is not null || fact.RawInput is not null || fact.OperationDisposition is not null) {
            throw new InvalidOperationException("Controller facts require captured identities; natural events must not invent controller calls.");
        }
        if ((successfulDecision ? fact.LastServedActorId != fact.ActorId : fact.LastServedActorId is not null) ||
            (fact.LastServedActorId is not null && !actorEffect)) {
            throw new InvalidOperationException("Service cursor does not match a recorded decision.");
        }
        if (successfulDecision && (PlaygroundCommandParser.Parse(fact.RawInput!)?.Kind, fact.Kind) is not (
            (PlaygroundCommandKind.Wait, PlaygroundEventKind.WaitStarted) or
            (PlaygroundCommandKind.Gather, PlaygroundEventKind.GatherStarted) or
            (PlaygroundCommandKind.Cancel, PlaygroundEventKind.ActivityCancelled) or
            (PlaygroundCommandKind.Continue, PlaygroundEventKind.AttentionAcknowledged) or
            (PlaygroundCommandKind.Go, PlaygroundEventKind.MoveStarted) or
            (PlaygroundCommandKind.Use, PlaygroundEventKind.ItemUsed) or
            (PlaygroundCommandKind.Handoff, PlaygroundEventKind.HandoffEdited) or
            (PlaygroundCommandKind.Goal, PlaygroundEventKind.GoalOperation) or
            (PlaygroundCommandKind.Map, PlaygroundEventKind.MapQueried) or
            (PlaygroundCommandKind.Path, PlaygroundEventKind.PathQueried) or
            (PlaygroundCommandKind.Say, PlaygroundEventKind.SpeechSent) or
            (PlaygroundCommandKind.Shout, PlaygroundEventKind.SpeechShouted) or
            (PlaygroundCommandKind.Give, PlaygroundEventKind.ItemGiven) or
            (PlaygroundCommandKind.Buy, PlaygroundEventKind.MarketBought) or
            (PlaygroundCommandKind.Sell, PlaygroundEventKind.MarketSold) or
            (PlaygroundCommandKind.Produce, PlaygroundEventKind.ProductionStarted) or
            (PlaygroundCommandKind.GiveMoney, PlaygroundEventKind.MoneyGiven) or
            (PlaygroundCommandKind.Offer, PlaygroundEventKind.TradeOffered) or
            (PlaygroundCommandKind.Accept, PlaygroundEventKind.TradeAccepted) or
            (PlaygroundCommandKind.Reject, PlaygroundEventKind.TradeRejected) or
            (PlaygroundCommandKind.Withdraw, PlaygroundEventKind.TradeWithdrawn) or
            (PlaygroundCommandKind.Follow, PlaygroundEventKind.FollowStarted) or
            (PlaygroundCommandKind.Unfollow, PlaygroundEventKind.FollowEnded) or
            (PlaygroundCommandKind.DismissFollowers, PlaygroundEventKind.FollowersDismissed))) {
            throw new InvalidOperationException("The accepted raw input must parse to the recorded operation kind.");
        }
        bool communication = fact.Kind is PlaygroundEventKind.SpeechSent or PlaygroundEventKind.ItemGiven or PlaygroundEventKind.SpeechShouted;
        if (!communication && (fact.TargetActorId is not null && fact.Kind is not (PlaygroundEventKind.FollowStarted or PlaygroundEventKind.MoneyGiven) || fact.SpeechText is not null ||
            (fact.ItemId is not null || fact.ItemCount != 0) && fact.Kind is not (PlaygroundEventKind.MarketBought or PlaygroundEventKind.MarketSold))) {
            throw new InvalidOperationException("Only communication or follow-start facts may carry their declared target or payload.");
        }
        bool valid = fact.Kind switch {
            PlaygroundEventKind.TradeOffered or PlaygroundEventKind.TradeAccepted or PlaygroundEventKind.TradeRejected or PlaygroundEventKind.TradeWithdrawn =>
                actorEffect && fact.ChangedCharacters.Count == 2 && fact.ChangedResources.Count == 0 && fact.Light is null && fact.Script is null && fact.ResourceId is null && fact.SpatialFacts.Count == 0,
            PlaygroundEventKind.TradesExpired => fact.ActorId is null && fact.ChangedCharacters.Count > 0 && fact.ChangedResources.Count == 0 && fact.Light is null && fact.Script is null && fact.ResourceId is null && fact.SpatialFacts.Count == 0,
            PlaygroundEventKind.MarketBought or PlaygroundEventKind.MarketSold or PlaygroundEventKind.ProductionStarted or PlaygroundEventKind.ProductionCompleted =>
                actorEffect && fact.ChangedCharacters.Count == 1 && fact.ChangedResources.Count == 0 && fact.Light is null && fact.Script is null && fact.ResourceId is null && fact.SpatialFacts.Count == 0,
            PlaygroundEventKind.MoneyGiven => actorEffect && fact.ChangedCharacters.Count == 2 && fact.ChangedResources.Count == 0 && fact.Light is null && fact.Script is null && fact.ResourceId is null && fact.SpatialFacts.Count == 0,
            PlaygroundEventKind.MarketRefreshed => fact.ActorId is null && fact.ChangedCharacters.Count == 0 && fact.ChangedResources.Count == 0 && fact.Light is null && fact.Script is null && fact.ResourceId is null && fact.SpatialFacts.Count == 0,
            PlaygroundEventKind.SpeechShouted => actorEffect && fact.TargetActorId is null && fact.ItemId is null && fact.ItemCount == 0 &&
                fact.ChangedResources.Count == 0 && fact.Light is null && fact.Script is null && fact.ResourceId is null && fact.Visits.Count == 0,
            PlaygroundEventKind.SpeechSent or PlaygroundEventKind.ItemGiven => actorEffect && fact.ChangedCharacters.Count == 2 &&
                fact.ChangedResources.Count == 0 && fact.Light is null && fact.Script is null && fact.ResourceId is null,
            PlaygroundEventKind.LightChanged => fact.Light is not null && fact.Script is null && fact.ActorId is null && fact.ResourceId is null &&
                fact.ChangedCharacters.All(c => c.Survival is not null),
            PlaygroundEventKind.ResourceRegenerated => oneResource && fact.Light is null && fact.Script is null && fact.ActorId is null && fact.ChangedCharacters.Count == 0,
            PlaygroundEventKind.ResourceDestroyed => oneResource && fact.Light is null && fact.Script is not null && fact.ActorId is null,
            PlaygroundEventKind.GatherYielded => actorEffect && oneResource && fact.Light is null && fact.Script is null,
            PlaygroundEventKind.GatherStarted or PlaygroundEventKind.GatherFinished => actorEffect && fact.ResourceId is not null && fact.ChangedResources.Count == 0 && fact.Light is null && fact.Script is null,
            PlaygroundEventKind.WaitStarted or PlaygroundEventKind.WaitCompleted or PlaygroundEventKind.ActivityCancelled or PlaygroundEventKind.AttentionAcknowledged or
                PlaygroundEventKind.HandoffEdited or PlaygroundEventKind.GoalOperation or PlaygroundEventKind.MapQueried or PlaygroundEventKind.PathQueried or PlaygroundEventKind.DecisionContextOpened or PlaygroundEventKind.OperationRejected =>
                actorEffect && fact.ChangedCharacters.Count == 1 && fact.ChangedResources.Count == 0 && fact.Light is null && fact.Script is null && fact.ResourceId is null,
            PlaygroundEventKind.MoveStarted or PlaygroundEventKind.MoveArrived or PlaygroundEventKind.FollowStarted or PlaygroundEventKind.FollowEnded or PlaygroundEventKind.FollowersDismissed =>
                actorEffect && fact.ChangedResources.Count == 0 && fact.Light is null && fact.Script is null && fact.ResourceId is null,
            PlaygroundEventKind.WeatherResolved => fact.ActorId is null && fact.ChangedCharacters.Count > 0 &&
                fact.ChangedCharacters.All(c => c.Survival is not null) && fact.ChangedResources.Count == 0 &&
                fact.Light is null && fact.Script is null && fact.ResourceId is null,
            PlaygroundEventKind.SurvivalFailed => actorEffect && fact.ChangedCharacters.Single(c => c.Id == fact.ActorId).Survival is not null &&
                fact.ChangedResources.Count == 0 && fact.Light is null && fact.Script is null && fact.ResourceId is null,
            PlaygroundEventKind.SurvivalWarning or PlaygroundEventKind.ItemUsed =>
                actorEffect && fact.ChangedCharacters.Count == 1 && fact.ChangedCharacters[0].Survival is not null &&
                fact.ChangedResources.Count == 0 && fact.Light is null && fact.Script is null && fact.ResourceId is null,
            _ => false
        };
        if (!valid) { throw new InvalidOperationException("Recorded effects do not match the event shape."); }
        ValidateFollowingChanges(business, fact);
        ValidateCommunicationState(business, fact, communication);
        if (fact.Kind is PlaygroundEventKind.HandoffEdited or PlaygroundEventKind.GoalOperation or PlaygroundEventKind.MapQueried or PlaygroundEventKind.PathQueried or PlaygroundEventKind.DecisionContextOpened or PlaygroundEventKind.OperationRejected) {
            PlaygroundCharacter before = business.GetCharacter(fact.ActorId!);
            PlaygroundCharacter after = fact.ChangedCharacters[0];
            if (fact.At != business.Time || !PlaygroundActions.NeedsDecision(business, before.Id) ||
                before.Id != after.Id || before.Name != after.Name || before.SkillLevel != after.SkillLevel ||
                before.Backpack.UnitCapacity != after.Backpack.UnitCapacity || !before.Backpack.Items.OrderBy(i => i.Key, StringComparer.Ordinal).SequenceEqual(after.Backpack.Items.OrderBy(i => i.Key, StringComparer.Ordinal)) ||
                !SameActivity(before.Activity, after.Activity) || !SameSurvival(before.Survival, after.Survival) || before.Communication != after.Communication ||
                !before.Notices.Select(NoticeFields).SequenceEqual(after.Notices.Select(NoticeFields)) ||
                before.LastResult != after.LastResult || before.NextActivityId != after.NextActivityId ||
                after.DecisionGeneration != checked(before.DecisionGeneration + 1) || fact.Visits.Count != 0) {
                throw new InvalidOperationException("Interaction boundary facts cannot alter gameplay state or observations.");
            }
        }
        if (rejected && (string.IsNullOrWhiteSpace(fact.Result) || fact.ObservedEvents.Count != 0 ||
            fact.InteractionChanges.Count != 1 || fact.InteractionChanges[0].ActorId != fact.ActorId)) {
            throw new InvalidOperationException("A rejection records only its actor's visible refusal, without world events or other actors' deliveries.");
        }
        if (fact.GatherStopReason is not null &&
            (!Enum.IsDefined(fact.GatherStopReason.Value) || fact.Kind is not (PlaygroundEventKind.GatherYielded or PlaygroundEventKind.GatherFinished) ||
                fact.ChangedCharacters.Single(c => c.Id == fact.ActorId).Activity is not null)) {
            throw new InvalidOperationException("A gathering stop reason requires a completed gathering activity.");
        }
        ValidateEndingsAndSpatial(business, fact);
        if (business.Expedition is null && fact.Visits.Count != 0 ||
            fact.Visits.Select(v => (v.ActorId, v.PlaceId)).Distinct().Count() != fact.Visits.Count ||
            fact.Visits.Any(v => !business.GetCharacter(v.ActorId).IsActive)) {
            throw new InvalidOperationException("Unexpected expedition observation.");
        }
        if (fact.Kind == PlaygroundEventKind.SurvivalFailed && fact.ChangedCharacters.Single(c => c.Id == fact.ActorId).Ending?.Outcome != PlaygroundExpeditionOutcome.Dead) {
            throw new InvalidOperationException("A fatal survival fact must record the personal ending.");
        }
        if (fact.Kind == PlaygroundEventKind.WeatherResolved) { ValidateWeather(business, fact); }
        else if (fact.Environment is not null) { throw new InvalidOperationException("Only weather can replace the environment stream."); }
    }
    private static void ValidateFollowingChanges(PlaygroundBusinessState business, PlaygroundFact fact) {
        var ended = fact.ChangedCharacters.Where(c => !c.IsActive).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        foreach (PlaygroundCharacter before in business.Characters) {
            PlaygroundCharacter after = fact.ChangedCharacters.SingleOrDefault(c => c.Id == before.Id) ?? before;
            string? expected = before.FollowingLeaderId;
            if (!after.IsActive || expected is not null && ended.Contains(expected) ||
                fact.Kind == PlaygroundEventKind.FollowEnded && before.Id == fact.ActorId ||
                fact.Kind == PlaygroundEventKind.FollowersDismissed && expected == fact.ActorId) { expected = null; }
            if (fact.Kind == PlaygroundEventKind.FollowStarted && before.Id == fact.ActorId) { expected = fact.TargetActorId; }
            if (after.FollowingLeaderId != expected) {
                throw new InvalidOperationException("Following changes must match the explicit command or a participant's terminal fact.");
            }
        }
        bool relationCommand = fact.Kind is PlaygroundEventKind.FollowStarted or PlaygroundEventKind.FollowEnded or PlaygroundEventKind.FollowersDismissed;
        if (relationCommand) {
            PlaygroundCharacter actor = business.GetCharacter(fact.ActorId!);
            if (business.Expedition is null || fact.At != business.Time || !PlaygroundActions.NeedsDecision(business, actor.Id) ||
                PlaygroundActions.Location(business, actor.Id) is not AtPlaceLocation at) {
                throw new InvalidOperationException("Following commands require a decision opportunity at an actual place.");
            }
            if (fact.Kind == PlaygroundEventKind.FollowStarted) {
                PlaygroundCommand command = PlaygroundCommandParser.Parse(fact.RawInput!)!;
                PlaygroundCharacter? leader = business.Characters.SingleOrDefault(c => c.Id == fact.TargetActorId);
                if (command.TargetActorId != fact.TargetActorId || leader is null || !leader.IsActive || leader.Id == actor.Id ||
                    leader.FollowingLeaderId is not null || actor.FollowingLeaderId is not null || actor.Activity is not null ||
                    PlaygroundFollowingRules.Followers(business, actor.Id).Any() ||
                    PlaygroundActions.Location(business, leader.Id) is not AtPlaceLocation leaderPlace || leaderPlace.PlaceId != at.PlaceId) {
                    throw new InvalidOperationException("Follow must establish a new direct relation with an active colocated leader.");
                }
            } else if (fact.Kind == PlaygroundEventKind.FollowEnded && actor.FollowingLeaderId is null ||
                fact.Kind == PlaygroundEventKind.FollowersDismissed && !PlaygroundFollowingRules.Followers(business, actor.Id).Any()) {
                throw new InvalidOperationException("Only an existing following relation can be ended.");
            }
            string[] expectedIds = fact.Kind == PlaygroundEventKind.FollowersDismissed
                ? PlaygroundFollowingRules.MovingMembers(business, actor.Id).Select(c => c.Id).ToArray() : [actor.Id];
            if (!expectedIds.SequenceEqual(fact.ChangedCharacters.Select(c => c.Id)) || fact.SpatialFacts.Count != 0) {
                throw new InvalidOperationException("A following command changes exactly its declared participants, without movement.");
            }
            foreach (PlaygroundCharacter changed in fact.ChangedCharacters) {
                PlaygroundCharacter before = business.GetCharacter(changed.Id);
                bool acknowledge = fact.Kind == PlaygroundEventKind.FollowStarted;
                PlaygroundSurvival? expectedBody = before.Survival?.Project(fact.At);
                if (acknowledge) { expectedBody = expectedBody?.Acknowledge(fact.At); }
                ValidatePreservedCharacter(before, changed, expectedBody, acknowledge, changed.Id == actor.Id ? fact.Result : before.LastResult);
            }
        }
        if (fact.Kind is PlaygroundEventKind.MoveArrived or PlaygroundEventKind.SurvivalFailed) {
            string[] expectedIds = business.Characters.Where(c => c.Id == fact.ActorId ||
                c.FollowingLeaderId is { } leader && ended.Contains(leader) ||
                fact.Kind == PlaygroundEventKind.SurvivalFailed && c.IsActive && fact.TradeChanges.Any(offer => offer.Involves(c.Id))).Select(c => c.Id).ToArray();
            if (!expectedIds.SequenceEqual(fact.ChangedCharacters.Select(c => c.Id))) {
                throw new InvalidOperationException("An arrival or death changes only its subject and followers released by that subject's ending.");
            }
            foreach (PlaygroundCharacter changed in fact.ChangedCharacters.Where(c => c.Id != fact.ActorId)) {
                PlaygroundCharacter before = business.GetCharacter(changed.Id);
                PlaygroundSurvival? expectedBody = before.Survival?.WithRates(fact.At, before.Survival.FoodRate, before.Survival.WaterRate,
                    PlaygroundSurvivalRules.WarmthRate(business.Definition, PlaygroundSurvivalRules.IsSheltered(business.Definition, business.Expedition!.Spatial, before.Id), business.Light.IsDay));
                ValidatePreservedCharacter(before, changed, expectedBody, false, before.LastResult);
            }
        }
    }

    private static void ValidatePreservedCharacter(PlaygroundCharacter before, PlaygroundCharacter after, PlaygroundSurvival? expectedBody, bool acknowledge, string? result) {
        if (before.Name != after.Name || before.SkillLevel != after.SkillLevel || before.Ending != after.Ending ||
            before.NextActivityId != after.NextActivityId || after.DecisionGeneration != checked(before.DecisionGeneration + 1) ||
            !SameActivity(before.Activity, after.Activity) || !SameSurvival(expectedBody, after.Survival) ||
            before.Backpack.UnitCapacity != after.Backpack.UnitCapacity ||
            !before.Backpack.Items.OrderBy(i => i.Key, StringComparer.Ordinal).SequenceEqual(after.Backpack.Items.OrderBy(i => i.Key, StringComparer.Ordinal)) ||
            !(acknowledge ? Enumerable.Empty<PlaygroundNotice>() : before.Notices).Select(NoticeFields).SequenceEqual(after.Notices.Select(NoticeFields)) ||
            after.LastResult != result) {
            throw new InvalidOperationException("Following effects must preserve each participant's unrelated state and unacknowledged reminders.");
        }
    }

    private static void ValidateCommunicationState(PlaygroundBusinessState business, PlaygroundFact fact, bool communication) {
        // Monetary gift participant state is checked together with its exact wallet effects.
        if (fact.Kind is PlaygroundEventKind.MoneyGiven or PlaygroundEventKind.TradeOffered or PlaygroundEventKind.TradeAccepted or
            PlaygroundEventKind.TradeRejected or PlaygroundEventKind.TradeWithdrawn or PlaygroundEventKind.TradesExpired) { return; }
        if (!communication) {
            foreach (PlaygroundCharacter changed in fact.ChangedCharacters) {
                PlaygroundCommunicationState before = business.GetCharacter(changed.Id).Communication;
                bool acknowledge = !changed.IsActive || changed.Id == fact.ActorId && fact.Kind is
                    PlaygroundEventKind.AttentionAcknowledged or PlaygroundEventKind.ActivityCancelled or PlaygroundEventKind.WaitStarted or PlaygroundEventKind.GatherStarted or PlaygroundEventKind.ProductionStarted or PlaygroundEventKind.MoveStarted or PlaygroundEventKind.FollowStarted;
                bool tradeNotice = changed.IsActive && fact.Kind is PlaygroundEventKind.SurvivalFailed or PlaygroundEventKind.WeatherResolved &&
                    fact.TradeChanges.Any(offer => offer.Involves(changed.Id));
                if (changed.Communication != (acknowledge ? before.Acknowledge() : tradeNotice ? before.Receive() : before)) {
                    throw new InvalidOperationException("Only accepted communication spends budget; only explicit acknowledgement clears its attention.");
                }
            }
            return;
        }
        PlaygroundCommand command = PlaygroundCommandParser.Parse(fact.RawInput!) ?? throw new InvalidOperationException("Communication requires its original parsed operation.");
        if (fact.At != business.Time || fact.ActorId is null || !PlaygroundActions.NeedsDecision(business, fact.ActorId)) {
            throw new InvalidOperationException("Communication requires a current decision opportunity.");
        }
        PlaygroundCharacter sender = business.GetCharacter(fact.ActorId);
        bool shout = fact.Kind == PlaygroundEventKind.SpeechShouted;
        bool speech = shout || fact.Kind == PlaygroundEventKind.SpeechSent;
        PlaygroundCharacter[] participants;
        if (shout) {
            if (fact.TargetActorId is not null || command.TargetActorId is not null || fact.Result != PlaygroundCommunicationRules.ShoutResult ||
                business.InteractionStates[sender.Id].NextExperienceSequence == long.MaxValue) {
                throw new InvalidOperationException("Shouting has no directed target or recipient-dependent result.");
            }
            var expectedRecipients = PlaygroundCommunicationRules.GetShoutRecipients(business, sender.Id).Select(actor => actor.Id).ToHashSet(StringComparer.Ordinal);
            participants = business.Characters.Where(actor => actor.Id == sender.Id || expectedRecipients.Contains(actor.Id)).ToArray();
            if (!participants.Select(actor => actor.Id).SequenceEqual(fact.ChangedCharacters.Select(actor => actor.Id)) ||
                participants.Any(actor => actor.Id != sender.Id && !PlaygroundCommunicationRules.CanReceiveShout(business, actor))) {
                throw new InvalidOperationException("Shouting must record exactly every eligible listener and its sender once in character order.");
            }
        } else {
            if (!PlaygroundCommunicationRules.CanTarget(business, fact.ActorId, fact.TargetActorId) || command.TargetActorId != fact.TargetActorId ||
                !fact.ChangedCharacters.Any(c => c.Id == fact.TargetActorId)) {
                throw new InvalidOperationException("Directed communication must connect two active participants at one actual place.");
            }
            participants = [sender, business.GetCharacter(fact.TargetActorId!)];
        }
        if (participants.Any(actor => actor.DecisionGeneration >= long.MaxValue - 2) || PlaygroundCommunicationRules.Remaining(sender, fact.At) <= 0 ||
            (speech ? !PlaygroundCommunicationRules.ValidSpeech(fact.SpeechText) || fact.SpeechText != command.SpeechText || fact.ItemId is not null || fact.ItemCount != 0 :
                fact.SpeechText is not null || string.IsNullOrWhiteSpace(fact.ItemId) || fact.ItemCount <= 0 || fact.ItemId != command.ItemId || fact.ItemCount != command.Count)) {
            throw new InvalidOperationException("Invalid communication payload, participant capacity or exhausted same-time sending budget.");
        }
        foreach (PlaygroundCharacter prior in participants) {
            PlaygroundCharacter after = fact.ChangedCharacters.Single(c => c.Id == prior.Id);
            bool own = prior.Id == sender.Id;
            PlaygroundCommunicationState expected = own ? prior.Communication.Sent(fact.At) : prior.Communication.Receive();
            if (after.Communication != expected || prior.Name != after.Name || prior.SkillLevel != after.SkillLevel ||
                prior.Ending != after.Ending || prior.NextActivityId != after.NextActivityId ||
                after.DecisionGeneration != checked(prior.DecisionGeneration + 1) || !SameActivity(prior.Activity, after.Activity) ||
                !SameSurvival(prior.Survival?.Project(fact.At), after.Survival) ||
                !prior.Notices.Select(NoticeFields).SequenceEqual(after.Notices.Select(NoticeFields)) ||
                after.LastResult != (own ? fact.Result : prior.LastResult) || prior.Backpack.UnitCapacity != after.Backpack.UnitCapacity) {
                throw new InvalidOperationException("Communication must preserve participants' activities, warnings and unrelated character state.");
            }
            foreach (string item in prior.Backpack.Items.Keys.Concat(after.Backpack.Items.Keys).Append(fact.ItemId ?? "").Where(id => id.Length != 0).Distinct(StringComparer.Ordinal)) {
                long delta = !speech && item == fact.ItemId ? (own ? -(long)fact.ItemCount : fact.ItemCount) : 0;
                long count = (long)prior.Backpack.Count(item) + delta;
                if (count < 0 || count > int.MaxValue || after.Backpack.Count(item) != count) {
                    throw new InvalidOperationException("A gift must transfer exactly its recorded quantity atomically; speech cannot change inventory.");
                }
            }
            after.Backpack.Validate();
        }
    }
    private static void ValidateWeather(PlaygroundBusinessState business, PlaygroundFact fact) {
        PlaygroundEnvironment previous = business.Expedition?.Environment ?? throw new InvalidOperationException("Weather is not enabled.");
        PlaygroundEnvironment next = fact.Environment ?? throw new InvalidOperationException("Weather must record its next state.");
        string[] active = business.Characters.Where(c => c.IsActive).Select(c => c.Id).Order(StringComparer.Ordinal).ToArray();
        if (previous.NextDue != fact.At || next.Seed != previous.Seed || next.Sequence != checked(previous.Sequence + 1) || next.LastAt != fact.At ||
            !active.SequenceEqual(fact.ChangedCharacters.Select(c => c.Id).Order(StringComparer.Ordinal)) ||
            !active.SequenceEqual(next.LastEffects.Select(e => e.ActorId).Order(StringComparer.Ordinal))) {
            throw new InvalidOperationException("Weather must advance once and include every active participant exactly once.");
        }
        foreach (PlaygroundWeatherEffect effect in next.LastEffects) {
            PlaygroundCharacter prior = business.GetCharacter(effect.ActorId);
            PlaygroundCharacter changed = fact.ChangedCharacters.Single(c => c.Id == effect.ActorId);
            PlaygroundSurvival body = prior.Survival!.Project(fact.At);
            bool sheltered = PlaygroundSurvivalRules.IsSheltered(business.Definition, business.Expedition!.Spatial, prior.Id);
            long loss = next.LastCold && !sheltered ? Math.Min(body.WarmthUnits, business.Definition.Weather.ExposureLoss * PlaygroundSurvival.Scale) : 0;
            PlaygroundSurvival result = changed.Survival!;
            if (body.IsFatal || effect.Sheltered != sheltered || effect.WarmthLossUnits != loss ||
                result.FoodUnits != body.FoodUnits || result.WaterUnits != body.WaterUnits || result.WarmthUnits != body.WarmthUnits - loss ||
                (changed.Ending?.Outcome == PlaygroundExpeditionOutcome.Dead) != result.IsFatal) {
                throw new InvalidOperationException("Weather effects must match recorded exposure and body changes.");
            }
        }
    }
    private static void ValidateEndingsAndSpatial(PlaygroundBusinessState business, PlaygroundFact fact) {
        var endingIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (PlaygroundCharacter changed in fact.ChangedCharacters) {
            PlaygroundCharacter prior = business.GetCharacter(changed.Id);
            if (!prior.IsActive) { throw new InvalidOperationException("A retired character cannot be changed by later gameplay."); }
            if (changed.Ending is not { } ending) { continue; }
            if (business.Expedition is null || ending.At != fact.At || fact.Kind != PlaygroundEventKind.WeatherResolved && changed.Id != fact.ActorId ||
                ending.Outcome != PlaygroundExpeditionOutcome.Dead || fact.Kind is not (PlaygroundEventKind.SurvivalFailed or PlaygroundEventKind.WeatherResolved)) {
                throw new InvalidOperationException("A personal ending must match its cause and boundary.");
            }
            SpatialLocation beforeLocation = business.Expedition.Spatial.Entities.Single(e => e.Id.Value == changed.Id).Location;
            if (!beforeLocation.Equals(ending.Location)) { throw new InvalidOperationException("The ending must retain the exact historical endpoint."); }
            endingIds.Add(changed.Id);
        }
        int index = 0;
        if (fact.Kind == PlaygroundEventKind.MoveStarted) {
            if (business.Expedition is null || fact.ActorId is null || fact.At != business.Time || !PlaygroundActions.NeedsDecision(business, fact.ActorId) ||
                business.GetCharacter(fact.ActorId).FollowingLeaderId is not null) {
                throw new InvalidOperationException("Only an independently deciding leader can start movement.");
            }
            PlaygroundCharacter[] members = PlaygroundFollowingRules.MovingMembers(business, fact.ActorId).ToArray();
            PlaygroundCommand command = PlaygroundCommandParser.Parse(fact.RawInput!)!;
            if (fact.SpatialFacts.Count != members.Length || !members.Select(c => c.Id).SequenceEqual(fact.ChangedCharacters.Select(c => c.Id))) {
                throw new InvalidOperationException("Movement must include exactly the leader and all current followers in character order.");
            }
            for (int memberIndex = 0; memberIndex < members.Length; memberIndex++) {
                PlaygroundCharacter prior = members[memberIndex], changed = fact.ChangedCharacters[memberIndex];
                if (!prior.IsActive || prior.Activity is not null ||
                    PlaygroundActions.Location(business, prior.Id) is not AtPlaceLocation atPlace ||
                    fact.SpatialFacts[memberIndex] is not TraversalStartedFact start || start.EntityId.Value != prior.Id ||
                    business.Definition.GetEntrances(atPlace.PlaceId.Value).SingleOrDefault(p => p.EntranceId == command.EntranceId)?.Id != start.PassageId.Value || start.FromPlaceId != atPlace.PlaceId || start.SpeedSnapshot != 1) {
                    throw new InvalidOperationException("Every recorded member must start the selected passage from their actual place at the common speed.");
                }
                bool own = prior.Id == fact.ActorId;
                PlaygroundSurvival? expectedBody = prior.Survival?.WithRates(fact.At, prior.Survival.FoodRate, prior.Survival.WaterRate,
                    PlaygroundSurvivalRules.WarmthRate(business.Definition, false, business.Light.IsDay));
                if (own) { expectedBody = expectedBody?.Acknowledge(fact.At); }
                ValidatePreservedCharacter(prior, changed, expectedBody, own, own ? fact.Result : prior.LastResult);
            }
            index = members.Length;
        } else if (fact.Kind == PlaygroundEventKind.MoveArrived) {
            if (business.Expedition is null || fact.SpatialFacts.Count == 0 ||
                fact.SpatialFacts[0] is not TraversalArrivedFact arrived || arrived.EntityId.Value != fact.ActorId) {
                throw new InvalidOperationException("Movement requires a matching recorded spatial fact.");
            }
            index = 1;
        }
        string[] removed = fact.SpatialFacts.Skip(index).Select(f => f is EntityRemovedFact removal ? removal.EntityId.Value :
            throw new InvalidOperationException("Only personal endings may retire spatial entities.")).ToArray();
        if (removed.Distinct(StringComparer.Ordinal).Count() != removed.Length || !endingIds.SetEquals(removed)) {
            throw new InvalidOperationException("Every personal ending must atomically retire exactly its spatial entity.");
        }
    }

    private static bool SameActivity(PlaygroundActivity? a, PlaygroundActivity? b) => PlaygroundObservationEquality.SameActivity(a, b);
    private static bool SameSurvival(PlaygroundSurvival? a, PlaygroundSurvival? b) => ReferenceEquals(a, b) || a is not null && b is not null &&
        (a.BaselineAt, a.FoodUnits, a.WaterUnits, a.WarmthUnits, a.WarnedMask, a.PendingMask, a.PendingSince) ==
        (b.BaselineAt, b.FoodUnits, b.WaterUnits, b.WarmthUnits, b.WarnedMask, b.PendingMask, b.PendingSince) &&
        a.FoodRate.SameAs(b.FoodRate) && a.WaterRate.SameAs(b.WaterRate) && a.WarmthRate.SameAs(b.WarmthRate);
    private static (PlaygroundNoticeKind, string, string?, string, ModelTime, string, int) NoticeFields(PlaygroundNotice n) =>
        (n.Kind, n.ResourceId, n.ActorId, n.SourceId, n.At, n.Reason, n.Count);
}
