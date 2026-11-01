using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.ResourceCycle;

/// <summary>Applies recorded business replacements without planning effects or maintaining runtime forecasts.</summary>
internal static class CycleFactApplication {
    internal static CycleBusinessState Apply(CycleBusinessState business, LogicalInstant instant, CycleFact fact) {
        ArgumentNullException.ThrowIfNull(business);
        ArgumentNullException.ThrowIfNull(fact);
        if (fact.At != instant.ModelTime || fact.At < business.Time) {
            throw new InvalidOperationException("A fact must apply at its recorded time and cannot move time backwards.");
        }
        ValidateFact(business, fact);
        CycleBusinessState result = ApplyReplacements(business, instant, fact);
        CycleObservedEventValidation.Validate(business, result, fact);
        CycleGoalInteractionRules.Validate(business, result, fact);
        CycleInteractionValidation.Validate(business, result, fact);
        return result;
    }
    // Planning-only candidate: no material validation until the event and interaction replacements exist.
    internal static CycleBusinessState ApplyReplacements(CycleBusinessState business, LogicalInstant instant, CycleFact fact) {
        CycleExpedition? expedition = business.Expedition;
        if (expedition is not null) {
            GraphSpatialState spatial = expedition.Spatial;
            foreach (GraphSpatialFact spatialFact in fact.SpatialFacts) {
                spatial = CycleExpeditionMap.Reducer.Apply(spatial, instant, spatialFact);
            }
            expedition = expedition.Change(spatial, fact.Visits, fact.Environment);
        }
        return business.Replace(fact.At, fact.Light, fact.ChangedResources, fact.Script,
            fact.ChangedCharacters, fact.LastServedActorId, expedition, fact.InteractionChanges);
    }
    internal static bool SameFoldedBusiness(CycleBusinessState expected, CycleBusinessState actual) =>
        expected.Time == actual.Time && ReferenceEquals(expected.Light, actual.Light) && ReferenceEquals(expected.Script, actual.Script) &&
        expected.LastServedActorId == actual.LastServedActorId && expected.Resources.Count == actual.Resources.Count &&
        expected.Resources.Zip(actual.Resources).All(pair => ReferenceEquals(pair.First, pair.Second)) &&
        expected.Characters.Count == actual.Characters.Count && expected.Characters.Zip(actual.Characters).All(pair => ReferenceEquals(pair.First, pair.Second)) &&
        expected.InteractionStates.Count == actual.InteractionStates.Count &&
        expected.InteractionStates.All(pair => actual.InteractionStates.TryGetValue(pair.Key, out var state) && ReferenceEquals(pair.Value, state)) &&
        (expected.Expedition is null ? actual.Expedition is null : actual.Expedition is not null && expected.Expedition.SameState(actual.Expedition));

    private static void ValidateFact(CycleBusinessState business, CycleFact fact) {
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
        bool successfulDecision = fact.Kind is CycleEventKind.WaitStarted or CycleEventKind.GatherStarted or CycleEventKind.ActivityCancelled or CycleEventKind.AttentionAcknowledged or CycleEventKind.MoveStarted or CycleEventKind.ItemUsed or CycleEventKind.HandoffEdited or CycleEventKind.GoalOperation or CycleEventKind.SpeechSent or CycleEventKind.ItemGiven or CycleEventKind.SpeechShouted or
            CycleEventKind.FollowStarted or CycleEventKind.FollowEnded or CycleEventKind.FollowersDismissed;
        bool rejected = fact.Kind == CycleEventKind.OperationRejected;
        bool operation = successfulDecision || rejected;
        if (operation ? string.IsNullOrWhiteSpace(fact.ToolCallId) || string.IsNullOrWhiteSpace(fact.OperationRecordId) || fact.RawInput is null ||
                fact.OperationDisposition != (rejected ? CycleOperationDisposition.Rejected : CycleOperationDisposition.Succeeded)
            : fact.ToolCallId is not null || fact.OperationRecordId is not null || fact.RawInput is not null || fact.OperationDisposition is not null) {
            throw new InvalidOperationException("Controller facts require captured identities; natural events must not invent controller calls.");
        }
        if ((successfulDecision ? fact.LastServedActorId != fact.ActorId : fact.LastServedActorId is not null) ||
            (fact.LastServedActorId is not null && !actorEffect)) {
            throw new InvalidOperationException("Service cursor does not match a recorded decision.");
        }
        if (successfulDecision && (CycleCommandParser.Parse(fact.RawInput!)?.Kind, fact.Kind) is not (
            (CycleCommandKind.Wait, CycleEventKind.WaitStarted) or
            (CycleCommandKind.Gather, CycleEventKind.GatherStarted) or
            (CycleCommandKind.Cancel, CycleEventKind.ActivityCancelled) or
            (CycleCommandKind.Continue, CycleEventKind.AttentionAcknowledged) or
            (CycleCommandKind.Go, CycleEventKind.MoveStarted) or
            (CycleCommandKind.Use, CycleEventKind.ItemUsed) or
            (CycleCommandKind.Handoff, CycleEventKind.HandoffEdited) or
            (CycleCommandKind.Goal, CycleEventKind.GoalOperation) or
            (CycleCommandKind.Say, CycleEventKind.SpeechSent) or
            (CycleCommandKind.Shout, CycleEventKind.SpeechShouted) or
            (CycleCommandKind.Give, CycleEventKind.ItemGiven) or
            (CycleCommandKind.Follow, CycleEventKind.FollowStarted) or
            (CycleCommandKind.Unfollow, CycleEventKind.FollowEnded) or
            (CycleCommandKind.DismissFollowers, CycleEventKind.FollowersDismissed))) {
            throw new InvalidOperationException("The accepted raw input must parse to the recorded operation kind.");
        }
        bool communication = fact.Kind is CycleEventKind.SpeechSent or CycleEventKind.ItemGiven or CycleEventKind.SpeechShouted;
        if (!communication && (fact.TargetActorId is not null && fact.Kind != CycleEventKind.FollowStarted || fact.SpeechText is not null || fact.ItemId is not null || fact.ItemCount != 0)) {
            throw new InvalidOperationException("Only communication or follow-start facts may carry their declared target or payload.");
        }
        bool valid = fact.Kind switch {
            CycleEventKind.SpeechShouted => actorEffect && fact.TargetActorId is null && fact.ItemId is null && fact.ItemCount == 0 &&
                fact.ChangedResources.Count == 0 && fact.Light is null && fact.Script is null && fact.ResourceId is null && fact.Visits.Count == 0,
            CycleEventKind.SpeechSent or CycleEventKind.ItemGiven => actorEffect && fact.ChangedCharacters.Count == 2 &&
                fact.ChangedResources.Count == 0 && fact.Light is null && fact.Script is null && fact.ResourceId is null,
            CycleEventKind.LightChanged => fact.Light is not null && fact.Script is null && fact.ActorId is null && fact.ResourceId is null &&
                fact.ChangedCharacters.All(c => c.Survival is not null),
            CycleEventKind.ResourceRegenerated => oneResource && fact.Light is null && fact.Script is null && fact.ActorId is null && fact.ChangedCharacters.Count == 0,
            CycleEventKind.ResourceDestroyed => oneResource && fact.Light is null && fact.Script is not null && fact.ActorId is null,
            CycleEventKind.GatherYielded => actorEffect && oneResource && fact.Light is null && fact.Script is null,
            CycleEventKind.GatherStarted or CycleEventKind.GatherFinished => actorEffect && fact.ResourceId is not null && fact.ChangedResources.Count == 0 && fact.Light is null && fact.Script is null,
            CycleEventKind.WaitStarted or CycleEventKind.WaitCompleted or CycleEventKind.ActivityCancelled or CycleEventKind.AttentionAcknowledged or
                CycleEventKind.HandoffEdited or CycleEventKind.GoalOperation or CycleEventKind.DecisionContextOpened or CycleEventKind.OperationRejected =>
                actorEffect && fact.ChangedCharacters.Count == 1 && fact.ChangedResources.Count == 0 && fact.Light is null && fact.Script is null && fact.ResourceId is null,
            CycleEventKind.MoveStarted or CycleEventKind.MoveArrived or CycleEventKind.FollowStarted or CycleEventKind.FollowEnded or CycleEventKind.FollowersDismissed =>
                actorEffect && fact.ChangedResources.Count == 0 && fact.Light is null && fact.Script is null && fact.ResourceId is null,
            CycleEventKind.WeatherResolved => fact.ActorId is null && fact.ChangedCharacters.Count > 0 &&
                fact.ChangedCharacters.All(c => c.Survival is not null) && fact.ChangedResources.Count == 0 &&
                fact.Light is null && fact.Script is null && fact.ResourceId is null,
            CycleEventKind.SurvivalFailed => actorEffect && fact.ChangedCharacters.Single(c => c.Id == fact.ActorId).Survival is not null &&
                fact.ChangedResources.Count == 0 && fact.Light is null && fact.Script is null && fact.ResourceId is null,
            CycleEventKind.SurvivalWarning or CycleEventKind.ItemUsed =>
                actorEffect && fact.ChangedCharacters.Count == 1 && fact.ChangedCharacters[0].Survival is not null &&
                fact.ChangedResources.Count == 0 && fact.Light is null && fact.Script is null && fact.ResourceId is null,
            _ => false
        };
        if (!valid) { throw new InvalidOperationException("Recorded effects do not match the event shape."); }
        ValidateFollowingChanges(business, fact);
        ValidateCommunicationState(business, fact, communication);
        if (fact.Kind is CycleEventKind.HandoffEdited or CycleEventKind.GoalOperation or CycleEventKind.DecisionContextOpened or CycleEventKind.OperationRejected) {
            CycleCharacter before = business.GetCharacter(fact.ActorId!);
            CycleCharacter after = fact.ChangedCharacters[0];
            if (fact.At != business.Time || !CycleActions.NeedsDecision(business, before.Id) ||
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
            (!Enum.IsDefined(fact.GatherStopReason.Value) || fact.Kind is not (CycleEventKind.GatherYielded or CycleEventKind.GatherFinished) ||
                fact.ChangedCharacters.Single(c => c.Id == fact.ActorId).Activity is not null)) {
            throw new InvalidOperationException("A gathering stop reason requires a completed gathering activity.");
        }
        ValidateEndingsAndSpatial(business, fact);
        if (business.Expedition is null && fact.Visits.Count != 0 ||
            fact.Visits.Select(v => (v.ActorId, v.PlaceId)).Distinct().Count() != fact.Visits.Count ||
            fact.Visits.Any(v => !business.GetCharacter(v.ActorId).IsActive)) {
            throw new InvalidOperationException("Unexpected expedition observation.");
        }
        if (fact.Kind == CycleEventKind.SurvivalFailed && fact.ChangedCharacters.Single(c => c.Id == fact.ActorId).Ending?.Outcome != CycleExpeditionOutcome.Dead) {
            throw new InvalidOperationException("A fatal survival fact must record the personal ending.");
        }
        if (fact.Kind == CycleEventKind.WeatherResolved) { ValidateWeather(business, fact); }
        else if (fact.Environment is not null) { throw new InvalidOperationException("Only weather can replace the environment stream."); }
    }
    private static void ValidateFollowingChanges(CycleBusinessState business, CycleFact fact) {
        var ended = fact.ChangedCharacters.Where(c => !c.IsActive).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        foreach (CycleCharacter before in business.Characters) {
            CycleCharacter after = fact.ChangedCharacters.SingleOrDefault(c => c.Id == before.Id) ?? before;
            string? expected = before.FollowingLeaderId;
            if (!after.IsActive || expected is not null && ended.Contains(expected) ||
                fact.Kind == CycleEventKind.FollowEnded && before.Id == fact.ActorId ||
                fact.Kind == CycleEventKind.FollowersDismissed && expected == fact.ActorId) { expected = null; }
            if (fact.Kind == CycleEventKind.FollowStarted && before.Id == fact.ActorId) { expected = fact.TargetActorId; }
            if (after.FollowingLeaderId != expected) {
                throw new InvalidOperationException("Following changes must match the explicit command or a participant's terminal fact.");
            }
        }
        bool relationCommand = fact.Kind is CycleEventKind.FollowStarted or CycleEventKind.FollowEnded or CycleEventKind.FollowersDismissed;
        if (relationCommand) {
            CycleCharacter actor = business.GetCharacter(fact.ActorId!);
            if (business.Expedition is null || fact.At != business.Time || !CycleActions.NeedsDecision(business, actor.Id) ||
                CycleActions.Location(business, actor.Id) is not AtPlaceLocation at) {
                throw new InvalidOperationException("Following commands require a decision opportunity at an actual place.");
            }
            if (fact.Kind == CycleEventKind.FollowStarted) {
                CycleCommand command = CycleCommandParser.Parse(fact.RawInput!)!;
                CycleCharacter? leader = business.Characters.SingleOrDefault(c => c.Id == fact.TargetActorId);
                if (command.TargetActorId != fact.TargetActorId || leader is null || !leader.IsActive || leader.Id == actor.Id ||
                    leader.FollowingLeaderId is not null || actor.FollowingLeaderId is not null || actor.Activity is not null ||
                    CycleFollowingRules.Followers(business, actor.Id).Any() ||
                    CycleActions.Location(business, leader.Id) is not AtPlaceLocation leaderPlace || leaderPlace.PlaceId != at.PlaceId) {
                    throw new InvalidOperationException("Follow must establish a new direct relation with an active colocated leader.");
                }
            } else if (fact.Kind == CycleEventKind.FollowEnded && actor.FollowingLeaderId is null ||
                fact.Kind == CycleEventKind.FollowersDismissed && !CycleFollowingRules.Followers(business, actor.Id).Any()) {
                throw new InvalidOperationException("Only an existing following relation can be ended.");
            }
            string[] expectedIds = fact.Kind == CycleEventKind.FollowersDismissed
                ? CycleFollowingRules.MovingMembers(business, actor.Id).Select(c => c.Id).ToArray() : [actor.Id];
            if (!expectedIds.SequenceEqual(fact.ChangedCharacters.Select(c => c.Id)) || fact.SpatialFacts.Count != 0) {
                throw new InvalidOperationException("A following command changes exactly its declared participants, without movement.");
            }
            foreach (CycleCharacter changed in fact.ChangedCharacters) {
                CycleCharacter before = business.GetCharacter(changed.Id);
                bool acknowledge = fact.Kind == CycleEventKind.FollowStarted;
                CycleSurvival? expectedBody = before.Survival?.Project(fact.At);
                if (acknowledge) { expectedBody = expectedBody?.Acknowledge(fact.At); }
                ValidatePreservedCharacter(before, changed, expectedBody, acknowledge, changed.Id == actor.Id ? fact.Result : before.LastResult);
            }
        }
        if (fact.Kind is CycleEventKind.MoveArrived or CycleEventKind.SurvivalFailed) {
            string[] expectedIds = business.Characters.Where(c => c.Id == fact.ActorId ||
                c.FollowingLeaderId is { } leader && ended.Contains(leader)).Select(c => c.Id).ToArray();
            if (!expectedIds.SequenceEqual(fact.ChangedCharacters.Select(c => c.Id))) {
                throw new InvalidOperationException("An arrival or death changes only its subject and followers released by that subject's ending.");
            }
            foreach (CycleCharacter changed in fact.ChangedCharacters.Where(c => c.Id != fact.ActorId)) {
                CycleCharacter before = business.GetCharacter(changed.Id);
                CycleSurvival? expectedBody = before.Survival?.WithRates(fact.At, before.Survival.FoodRate, before.Survival.WaterRate,
                    CycleSurvivalRules.WarmthRate(CycleSurvivalRules.IsSheltered(business.Expedition!.Spatial, before.Id), business.Light.IsDay));
                ValidatePreservedCharacter(before, changed, expectedBody, false, before.LastResult);
            }
        }
    }

    private static void ValidatePreservedCharacter(CycleCharacter before, CycleCharacter after, CycleSurvival? expectedBody, bool acknowledge, string? result) {
        if (before.Name != after.Name || before.SkillLevel != after.SkillLevel || before.Ending != after.Ending ||
            before.NextActivityId != after.NextActivityId || after.DecisionGeneration != checked(before.DecisionGeneration + 1) ||
            !SameActivity(before.Activity, after.Activity) || !SameSurvival(expectedBody, after.Survival) ||
            before.Backpack.UnitCapacity != after.Backpack.UnitCapacity ||
            !before.Backpack.Items.OrderBy(i => i.Key, StringComparer.Ordinal).SequenceEqual(after.Backpack.Items.OrderBy(i => i.Key, StringComparer.Ordinal)) ||
            !(acknowledge ? Enumerable.Empty<CycleNotice>() : before.Notices).Select(NoticeFields).SequenceEqual(after.Notices.Select(NoticeFields)) ||
            after.LastResult != result) {
            throw new InvalidOperationException("Following effects must preserve each participant's unrelated state and unacknowledged reminders.");
        }
    }

    private static void ValidateCommunicationState(CycleBusinessState business, CycleFact fact, bool communication) {
        if (!communication) {
            foreach (CycleCharacter changed in fact.ChangedCharacters) {
                CycleCommunicationState before = business.GetCharacter(changed.Id).Communication;
                bool acknowledge = !changed.IsActive || changed.Id == fact.ActorId && fact.Kind is
                    CycleEventKind.AttentionAcknowledged or CycleEventKind.ActivityCancelled or CycleEventKind.WaitStarted or CycleEventKind.GatherStarted or CycleEventKind.MoveStarted or CycleEventKind.FollowStarted;
                if (changed.Communication != (acknowledge ? before.Acknowledge() : before)) {
                    throw new InvalidOperationException("Only accepted communication spends budget; only explicit acknowledgement clears its attention.");
                }
            }
            return;
        }
        CycleCommand command = CycleCommandParser.Parse(fact.RawInput!) ?? throw new InvalidOperationException("Communication requires its original parsed operation.");
        if (fact.At != business.Time || fact.ActorId is null || !CycleActions.NeedsDecision(business, fact.ActorId)) {
            throw new InvalidOperationException("Communication requires a current decision opportunity.");
        }
        CycleCharacter sender = business.GetCharacter(fact.ActorId);
        bool shout = fact.Kind == CycleEventKind.SpeechShouted;
        bool speech = shout || fact.Kind == CycleEventKind.SpeechSent;
        CycleCharacter[] participants;
        if (shout) {
            if (fact.TargetActorId is not null || command.TargetActorId is not null || fact.Result != CycleCommunicationRules.ShoutResult ||
                business.InteractionStates[sender.Id].NextExperienceSequence == long.MaxValue) {
                throw new InvalidOperationException("Shouting has no directed target or recipient-dependent result.");
            }
            var expectedRecipients = CycleCommunicationRules.GetShoutRecipients(business, sender.Id).Select(actor => actor.Id).ToHashSet(StringComparer.Ordinal);
            participants = business.Characters.Where(actor => actor.Id == sender.Id || expectedRecipients.Contains(actor.Id)).ToArray();
            if (!participants.Select(actor => actor.Id).SequenceEqual(fact.ChangedCharacters.Select(actor => actor.Id)) ||
                participants.Any(actor => actor.Id != sender.Id && !CycleCommunicationRules.CanReceiveShout(business, actor))) {
                throw new InvalidOperationException("Shouting must record exactly every eligible listener and its sender once in character order.");
            }
        } else {
            if (!CycleCommunicationRules.CanTarget(business, fact.ActorId, fact.TargetActorId) || command.TargetActorId != fact.TargetActorId ||
                !fact.ChangedCharacters.Any(c => c.Id == fact.TargetActorId)) {
                throw new InvalidOperationException("Directed communication must connect two active participants at one actual place.");
            }
            participants = [sender, business.GetCharacter(fact.TargetActorId!)];
        }
        if (participants.Any(actor => actor.DecisionGeneration >= long.MaxValue - 2) || CycleCommunicationRules.Remaining(sender, fact.At) <= 0 ||
            (speech ? !CycleCommunicationRules.ValidSpeech(fact.SpeechText) || fact.SpeechText != command.SpeechText || fact.ItemId is not null || fact.ItemCount != 0 :
                fact.SpeechText is not null || string.IsNullOrWhiteSpace(fact.ItemId) || fact.ItemCount <= 0 || fact.ItemId != command.ItemId || fact.ItemCount != command.Count)) {
            throw new InvalidOperationException("Invalid communication payload, participant capacity or exhausted same-time sending budget.");
        }
        foreach (CycleCharacter prior in participants) {
            CycleCharacter after = fact.ChangedCharacters.Single(c => c.Id == prior.Id);
            bool own = prior.Id == sender.Id;
            CycleCommunicationState expected = own ? prior.Communication.Sent(fact.At) : prior.Communication.Receive();
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
    private static void ValidateWeather(CycleBusinessState business, CycleFact fact) {
        CycleEnvironment previous = business.Expedition?.Environment ?? throw new InvalidOperationException("Weather is not enabled.");
        CycleEnvironment next = fact.Environment ?? throw new InvalidOperationException("Weather must record its next state.");
        string[] active = business.Characters.Where(c => c.IsActive).Select(c => c.Id).Order(StringComparer.Ordinal).ToArray();
        if (previous.NextDue != fact.At || next.Seed != previous.Seed || next.Sequence != checked(previous.Sequence + 1) || next.LastAt != fact.At ||
            !active.SequenceEqual(fact.ChangedCharacters.Select(c => c.Id).Order(StringComparer.Ordinal)) ||
            !active.SequenceEqual(next.LastEffects.Select(e => e.ActorId).Order(StringComparer.Ordinal))) {
            throw new InvalidOperationException("Weather must advance once and include every active participant exactly once.");
        }
        foreach (CycleWeatherEffect effect in next.LastEffects) {
            CycleCharacter prior = business.GetCharacter(effect.ActorId);
            CycleCharacter changed = fact.ChangedCharacters.Single(c => c.Id == effect.ActorId);
            CycleSurvival body = prior.Survival!.Project(fact.At);
            bool sheltered = CycleSurvivalRules.IsSheltered(business.Expedition!.Spatial, prior.Id);
            long loss = next.LastCold && !sheltered ? Math.Min(body.WarmthUnits, CycleEnvironmentRules.ExposureLoss * CycleSurvival.Scale) : 0;
            CycleSurvival result = changed.Survival!;
            if (body.IsFatal || effect.Sheltered != sheltered || effect.WarmthLossUnits != loss ||
                result.FoodUnits != body.FoodUnits || result.WaterUnits != body.WaterUnits || result.WarmthUnits != body.WarmthUnits - loss ||
                (changed.Ending?.Outcome == CycleExpeditionOutcome.Dead) != result.IsFatal) {
                throw new InvalidOperationException("Weather effects must match recorded exposure and body changes.");
            }
        }
    }
    private static void ValidateEndingsAndSpatial(CycleBusinessState business, CycleFact fact) {
        var endingIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (CycleCharacter changed in fact.ChangedCharacters) {
            CycleCharacter prior = business.GetCharacter(changed.Id);
            if (!prior.IsActive) { throw new InvalidOperationException("A retired character cannot be changed by later gameplay."); }
            if (changed.Ending is not { } ending) { continue; }
            if (business.Expedition is null || ending.At != fact.At || fact.Kind != CycleEventKind.WeatherResolved && changed.Id != fact.ActorId ||
                ending.Outcome != CycleExpeditionOutcome.Dead || fact.Kind is not (CycleEventKind.SurvivalFailed or CycleEventKind.WeatherResolved)) {
                throw new InvalidOperationException("A personal ending must match its cause and boundary.");
            }
            SpatialLocation beforeLocation = business.Expedition.Spatial.Entities.Single(e => e.Id.Value == changed.Id).Location;
            if (!beforeLocation.Equals(ending.Location)) { throw new InvalidOperationException("The ending must retain the exact historical endpoint."); }
            endingIds.Add(changed.Id);
        }
        int index = 0;
        if (fact.Kind == CycleEventKind.MoveStarted) {
            if (business.Expedition is null || fact.ActorId is null || fact.At != business.Time || !CycleActions.NeedsDecision(business, fact.ActorId) ||
                business.GetCharacter(fact.ActorId).FollowingLeaderId is not null) {
                throw new InvalidOperationException("Only an independently deciding leader can start movement.");
            }
            CycleCharacter[] members = CycleFollowingRules.MovingMembers(business, fact.ActorId).ToArray();
            CycleCommand command = CycleCommandParser.Parse(fact.RawInput!)!;
            if (fact.SpatialFacts.Count != members.Length || !members.Select(c => c.Id).SequenceEqual(fact.ChangedCharacters.Select(c => c.Id))) {
                throw new InvalidOperationException("Movement must include exactly the leader and all current followers in character order.");
            }
            for (int memberIndex = 0; memberIndex < members.Length; memberIndex++) {
                CycleCharacter prior = members[memberIndex], changed = fact.ChangedCharacters[memberIndex];
                if (!prior.IsActive || prior.Activity is not null ||
                    CycleActions.Location(business, prior.Id) is not AtPlaceLocation atPlace ||
                    fact.SpatialFacts[memberIndex] is not TraversalStartedFact start || start.EntityId.Value != prior.Id ||
                    start.PassageId.Value != command.PassageId || start.FromPlaceId != atPlace.PlaceId || start.SpeedSnapshot != 1) {
                    throw new InvalidOperationException("Every recorded member must start the selected passage from their actual place at the common speed.");
                }
                bool own = prior.Id == fact.ActorId;
                CycleSurvival? expectedBody = prior.Survival?.WithRates(fact.At, prior.Survival.FoodRate, prior.Survival.WaterRate,
                    CycleSurvivalRules.WarmthRate(false, business.Light.IsDay));
                if (own) { expectedBody = expectedBody?.Acknowledge(fact.At); }
                ValidatePreservedCharacter(prior, changed, expectedBody, own, own ? fact.Result : prior.LastResult);
            }
            index = members.Length;
        } else if (fact.Kind == CycleEventKind.MoveArrived) {
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

    private static bool SameActivity(CycleActivity? a, CycleActivity? b) => ReferenceEquals(a, b) || a is not null && b is not null &&
        (a.Kind, a.Id, a.StartedAt, a.DurationTicks, a.Due, a.ResourceId, a.TargetCount, a.CompletedCount, a.SelectedAll, a.CycleIndex) ==
        (b.Kind, b.Id, b.StartedAt, b.DurationTicks, b.Due, b.ResourceId, b.TargetCount, b.CompletedCount, b.SelectedAll, b.CycleIndex);
    private static bool SameSurvival(CycleSurvival? a, CycleSurvival? b) => ReferenceEquals(a, b) || a is not null && b is not null &&
        (a.BaselineAt, a.FoodUnits, a.WaterUnits, a.WarmthUnits, a.WarnedMask, a.PendingMask, a.PendingSince) ==
        (b.BaselineAt, b.FoodUnits, b.WaterUnits, b.WarmthUnits, b.WarnedMask, b.PendingMask, b.PendingSince) &&
        a.FoodRate.SameAs(b.FoodRate) && a.WaterRate.SameAs(b.WaterRate) && a.WarmthRate.SameAs(b.WarmthRate);
    private static (CycleNoticeKind, string, string?, string, ModelTime, string, int) NoticeFields(CycleNotice n) =>
        (n.Kind, n.ResourceId, n.ActorId, n.SourceId, n.At, n.Reason, n.Count);
}
