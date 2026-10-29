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
        bool successfulDecision = fact.Kind is CycleEventKind.WaitStarted or CycleEventKind.GatherStarted or CycleEventKind.ActivityCancelled or CycleEventKind.AttentionAcknowledged or CycleEventKind.MoveStarted or CycleEventKind.ItemUsed or CycleEventKind.HandoffEdited;
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
            (CycleCommandKind.Handoff, CycleEventKind.HandoffEdited))) {
            throw new InvalidOperationException("The accepted raw input must parse to the recorded operation kind.");
        }
        bool valid = fact.Kind switch {
            CycleEventKind.LightChanged => fact.Light is not null && fact.Script is null && fact.ActorId is null && fact.ResourceId is null &&
                fact.ChangedCharacters.All(c => c.Survival is not null),
            CycleEventKind.ResourceRegenerated => oneResource && fact.Light is null && fact.Script is null && fact.ActorId is null && fact.ChangedCharacters.Count == 0,
            CycleEventKind.ResourceDestroyed => oneResource && fact.Light is null && fact.Script is not null && fact.ActorId is null,
            CycleEventKind.GatherYielded => actorEffect && oneResource && fact.Light is null && fact.Script is null,
            CycleEventKind.GatherStarted or CycleEventKind.GatherFinished => actorEffect && fact.ResourceId is not null && fact.ChangedResources.Count == 0 && fact.Light is null && fact.Script is null,
            CycleEventKind.WaitStarted or CycleEventKind.WaitCompleted or CycleEventKind.ActivityCancelled or CycleEventKind.AttentionAcknowledged or
                CycleEventKind.HandoffEdited or CycleEventKind.DecisionContextOpened or CycleEventKind.OperationRejected =>
                actorEffect && fact.ChangedCharacters.Count == 1 && fact.ChangedResources.Count == 0 && fact.Light is null && fact.Script is null && fact.ResourceId is null,
            CycleEventKind.MoveStarted or CycleEventKind.MoveArrived =>
                actorEffect && fact.ChangedCharacters.Count == 1 && fact.ChangedResources.Count == 0 && fact.Light is null && fact.Script is null && fact.ResourceId is null,
            CycleEventKind.WeatherResolved => fact.ActorId is null && fact.ChangedCharacters.Count > 0 &&
                fact.ChangedCharacters.All(c => c.Survival is not null) && fact.ChangedResources.Count == 0 &&
                fact.Light is null && fact.Script is null && fact.ResourceId is null,
            CycleEventKind.SurvivalWarning or CycleEventKind.SurvivalFailed or CycleEventKind.ItemUsed =>
                actorEffect && fact.ChangedCharacters.Count == 1 && fact.ChangedCharacters[0].Survival is not null &&
                fact.ChangedResources.Count == 0 && fact.Light is null && fact.Script is null && fact.ResourceId is null,
            _ => false
        };
        if (!valid) { throw new InvalidOperationException("Recorded effects do not match the event shape."); }
        if (fact.Kind is CycleEventKind.HandoffEdited or CycleEventKind.DecisionContextOpened or CycleEventKind.OperationRejected) {
            CycleCharacter before = business.GetCharacter(fact.ActorId!);
            CycleCharacter after = fact.ChangedCharacters[0];
            if (fact.At != business.Time || !CycleActions.NeedsDecision(business, before.Id) ||
                before.Id != after.Id || before.Name != after.Name || before.SkillLevel != after.SkillLevel ||
                before.Backpack.UnitCapacity != after.Backpack.UnitCapacity || !before.Backpack.Items.OrderBy(i => i.Key, StringComparer.Ordinal).SequenceEqual(after.Backpack.Items.OrderBy(i => i.Key, StringComparer.Ordinal)) ||
                !SameActivity(before.Activity, after.Activity) || !SameSurvival(before.Survival, after.Survival) ||
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
        if (fact.Kind == CycleEventKind.SurvivalFailed && fact.ChangedCharacters[0].Ending?.Outcome != CycleExpeditionOutcome.Dead) {
            throw new InvalidOperationException("A fatal survival fact must record the personal ending.");
        }
        if (fact.Kind == CycleEventKind.WeatherResolved) { ValidateWeather(business, fact); }
        else if (fact.Environment is not null) { throw new InvalidOperationException("Only weather can replace the environment stream."); }
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
            if (business.Expedition is null || ending.At != fact.At ||
                (ending.Outcome == CycleExpeditionOutcome.Escaped ? fact.Kind != CycleEventKind.MoveArrived :
                    ending.Outcome != CycleExpeditionOutcome.Dead || fact.Kind is not (CycleEventKind.SurvivalFailed or CycleEventKind.WeatherResolved))) {
                throw new InvalidOperationException("A personal ending must match its cause and boundary.");
            }
            SpatialLocation beforeLocation = business.Expedition.Spatial.Entities.Single(e => e.Id.Value == changed.Id).Location;
            SpatialLocation expected = ending.Outcome == CycleExpeditionOutcome.Escaped && beforeLocation is TraversingLocation traversal
                ? new AtPlaceLocation(traversal.TargetPlaceId) : beforeLocation;
            if (!expected.Equals(ending.Location)) { throw new InvalidOperationException("The ending must retain the exact historical endpoint."); }
            endingIds.Add(changed.Id);
        }
        int index = 0;
        if (fact.Kind is CycleEventKind.MoveStarted or CycleEventKind.MoveArrived) {
            if (business.Expedition is null || fact.SpatialFacts.Count == 0 ||
                (fact.Kind == CycleEventKind.MoveStarted
                    ? fact.SpatialFacts[0] is not TraversalStartedFact started || started.EntityId.Value != fact.ActorId
                    : fact.SpatialFacts[0] is not TraversalArrivedFact arrived || arrived.EntityId.Value != fact.ActorId)) {
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
