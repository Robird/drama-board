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
        CycleExpedition? expedition = business.Expedition;
        if (expedition is not null) {
            GraphSpatialState spatial = expedition.Spatial;
            foreach (GraphSpatialFact spatialFact in fact.SpatialFacts) {
                spatial = CycleExpeditionMap.Reducer.Apply(spatial, instant, spatialFact);
            }
            expedition = expedition.Change(spatial, fact.Visits, fact.Outcome, fact.EndingReason, fact.Environment);
        }
        CycleBusinessState result = business.Replace(fact.At, fact.Light, fact.ChangedResources, fact.Script,
            fact.ChangedCharacters, fact.LastServedActorId, expedition);
        CycleObservedEventValidation.Validate(business, result, fact);
        return result;
    }
    internal static bool SameFoldedBusiness(CycleBusinessState expected, CycleBusinessState actual) =>
        expected.Time == actual.Time && ReferenceEquals(expected.Light, actual.Light) && ReferenceEquals(expected.Script, actual.Script) &&
        expected.LastServedActorId == actual.LastServedActorId && expected.Resources.Count == actual.Resources.Count &&
        expected.Resources.Zip(actual.Resources).All(pair => ReferenceEquals(pair.First, pair.Second)) &&
        expected.Characters.Count == actual.Characters.Count && expected.Characters.Zip(actual.Characters).All(pair => ReferenceEquals(pair.First, pair.Second)) &&
        (expected.Expedition is null ? actual.Expedition is null : actual.Expedition is not null && expected.Expedition.SameState(actual.Expedition));

    private static void ValidateFact(CycleBusinessState business, CycleFact fact) {
        if (!Enum.IsDefined(fact.Kind) ||
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
        bool decision = fact.Kind is CycleEventKind.WaitStarted or CycleEventKind.GatherStarted or CycleEventKind.ActivityCancelled or CycleEventKind.AttentionAcknowledged or CycleEventKind.MoveStarted or CycleEventKind.ItemUsed or CycleEventKind.HandoffEdited;
        if (decision ? string.IsNullOrWhiteSpace(fact.ToolCallId) || string.IsNullOrWhiteSpace(fact.OperationRecordId)
            : fact.ToolCallId is not null || fact.OperationRecordId is not null) {
            throw new InvalidOperationException("Controller facts require captured identities; natural events must not invent controller calls.");
        }
        if ((decision ? fact.LastServedActorId != fact.ActorId : fact.LastServedActorId is not null) ||
            (fact.LastServedActorId is not null && !actorEffect)) {
            throw new InvalidOperationException("Service cursor does not match a recorded decision.");
        }
        if (decision && actorEffect && business.GetCharacter(fact.ActorId!).Continuity is { } previousContinuity) {
            CycleDecisionContinuity nextContinuity = fact.ChangedCharacters.Single(c => c.Id == fact.ActorId).Continuity
                ?? throw new InvalidOperationException("A controller decision cannot remove continuity.");
            if (previousContinuity.Pending is not null || nextContinuity.ContextSequence != previousContinuity.ContextSequence ||
                nextContinuity.Pending is { } pending && (pending.Action.ActorId != fact.ActorId ||
                    pending.Action.ToolCallId != fact.ToolCallId || pending.Action.OperationRecordId != fact.OperationRecordId || pending.Action.AcceptedAt != fact.At)) {
                throw new InvalidOperationException("The accepted action must retain its captured controller identity and context.");
            }
        }
        bool valid = fact.Kind switch {
            CycleEventKind.LightChanged => fact.Light is not null && fact.Script is null && fact.ActorId is null && fact.ResourceId is null &&
                fact.ChangedCharacters.All(c => c.Survival is not null),
            CycleEventKind.ResourceRegenerated => oneResource && fact.Light is null && fact.Script is null && fact.ActorId is null && fact.ChangedCharacters.Count == 0,
            CycleEventKind.ResourceDestroyed => oneResource && fact.Light is null && fact.Script is not null && fact.ActorId is null,
            CycleEventKind.GatherYielded => actorEffect && oneResource && fact.Light is null && fact.Script is null,
            CycleEventKind.GatherStarted or CycleEventKind.GatherFinished => actorEffect && fact.ResourceId is not null && fact.ChangedResources.Count == 0 && fact.Light is null && fact.Script is null,
            CycleEventKind.WaitStarted or CycleEventKind.WaitCompleted or CycleEventKind.ActivityCancelled or CycleEventKind.AttentionAcknowledged or
                CycleEventKind.HandoffEdited or CycleEventKind.DecisionContextOpened =>
                actorEffect && fact.ChangedCharacters.Count == 1 && fact.ChangedResources.Count == 0 && fact.Light is null && fact.Script is null && fact.ResourceId is null,
            CycleEventKind.MoveStarted or CycleEventKind.MoveArrived =>
                actorEffect && fact.ChangedCharacters.Count == 1 && fact.ChangedResources.Count == 0 && fact.Light is null && fact.Script is null && fact.ResourceId is null,
            CycleEventKind.SurvivalWarning or CycleEventKind.SurvivalFailed or CycleEventKind.ItemUsed or CycleEventKind.WeatherResolved =>
                actorEffect && fact.ChangedCharacters.Count == 1 && fact.ChangedCharacters[0].Survival is not null &&
                fact.ChangedResources.Count == 0 && fact.Light is null && fact.Script is null && fact.ResourceId is null,
            _ => false
        };
        if (!valid) { throw new InvalidOperationException("Recorded effects do not match the event shape."); }
        if (fact.Kind is CycleEventKind.HandoffEdited or CycleEventKind.DecisionContextOpened) {
            CycleCharacter before = business.GetCharacter(fact.ActorId!);
            CycleCharacter after = fact.ChangedCharacters[0];
            if (fact.At != business.Time || !CycleActions.NeedsDecision(business, before.Id) || before.Continuity is null || after.Continuity is null ||
                before.Id != after.Id || before.Name != after.Name || before.SkillLevel != after.SkillLevel ||
                before.Backpack.UnitCapacity != after.Backpack.UnitCapacity || !before.Backpack.Items.OrderBy(i => i.Key, StringComparer.Ordinal).SequenceEqual(after.Backpack.Items.OrderBy(i => i.Key, StringComparer.Ordinal)) ||
                !SameActivity(before.Activity, after.Activity) || !SameSurvival(before.Survival, after.Survival) ||
                !before.Notices.Select(NoticeFields).SequenceEqual(after.Notices.Select(NoticeFields)) ||
                before.LastResult != after.LastResult || before.NextActivityId != after.NextActivityId ||
                after.DecisionGeneration != checked(before.DecisionGeneration + 1) || fact.Visits.Count != 0) {
                throw new InvalidOperationException("Continuity-only facts cannot alter gameplay state or observations.");
            }
            if (fact.Kind == CycleEventKind.HandoffEdited ?
                after.Continuity.ContextSequence != before.Continuity.ContextSequence ||
                    !CycleContinuityRules.SameOutcome(after.Continuity.Incoming, before.Continuity.Incoming) || before.Continuity.Pending is not null || after.Continuity.Pending is not null :
                before.Continuity.Pending is null || after.Continuity.Pending is not null || after.Continuity.Incoming is null ||
                    after.Continuity.ContextSequence != checked(before.Continuity.ContextSequence + 1) ||
                    after.Continuity.DraftHandoff != before.Continuity.DraftHandoff) {
                throw new InvalidOperationException("Invalid continuity transition.");
            }
            if (fact.Kind == CycleEventKind.DecisionContextOpened) {
                CyclePendingAction pending = before.Continuity.Pending!;
                CycleActionOutcome incoming = after.Continuity.Incoming!;
                if (!CycleContinuityRules.SamePending(pending, incoming) ||
                    incoming.DeliveredAt != fact.At || incoming.ActivityState == CycleOutcomeActivityState.Terminal) {
                    throw new InvalidOperationException("Opening a context must seal the recorded pending action exactly once.");
                }
                CycleContinuityRules.ValidateOpenedOutcome(business, before, incoming);
            }
        }
        if (fact.GatherStopReason is not null &&
            (!Enum.IsDefined(fact.GatherStopReason.Value) || fact.Kind is not (CycleEventKind.GatherYielded or CycleEventKind.GatherFinished) ||
                fact.ChangedCharacters.Single(c => c.Id == fact.ActorId).Activity is not null)) {
            throw new InvalidOperationException("A gathering stop reason requires a completed gathering activity.");
        }
        bool movement = fact.Kind is CycleEventKind.MoveStarted or CycleEventKind.MoveArrived;
        if (movement) {
            if (business.Expedition is null || fact.SpatialFacts.Count != 1 ||
                (fact.Kind == CycleEventKind.MoveStarted
                    ? fact.SpatialFacts[0] is not TraversalStartedFact started || started.EntityId.Value != fact.ActorId
                    : fact.SpatialFacts[0] is not TraversalArrivedFact arrived || arrived.EntityId.Value != fact.ActorId)) {
                throw new InvalidOperationException("Movement requires a matching recorded spatial fact.");
            }
        } else if (fact.SpatialFacts.Count != 0) { throw new InvalidOperationException("Unexpected spatial facts."); }
        if (fact.Kind is not (CycleEventKind.MoveArrived or CycleEventKind.SurvivalFailed or CycleEventKind.WeatherResolved) && (fact.Outcome is not null || fact.EndingReason is not null) ||
            business.Expedition is null && fact.Visits.Count != 0 ||
            fact.Visits.Select(v => (v.ActorId, v.PlaceId)).Distinct().Count() != fact.Visits.Count) {
            throw new InvalidOperationException("Unexpected expedition observation or ending.");
        }
        if (fact.Kind == CycleEventKind.SurvivalFailed && (fact.Outcome != CycleExpeditionOutcome.Dead || string.IsNullOrWhiteSpace(fact.EndingReason))) {
            throw new InvalidOperationException("A fatal survival fact must record the ending.");
        }
        if (fact.Kind == CycleEventKind.WeatherResolved) {
            CycleEnvironment previous = business.Expedition?.Environment ?? throw new InvalidOperationException("Weather is not enabled.");
            CycleEnvironment next = fact.Environment ?? throw new InvalidOperationException("Weather must record its next state.");
            if (previous.NextDue != fact.At || next.Seed != previous.Seed || next.Sequence != checked(previous.Sequence + 1) || next.LastAt != fact.At ||
                (fact.Outcome == CycleExpeditionOutcome.Dead) != fact.ChangedCharacters[0].Survival!.Project(fact.At).IsFatal ||
                (fact.Outcome is null ? fact.EndingReason is not null : fact.Outcome != CycleExpeditionOutcome.Dead || string.IsNullOrWhiteSpace(fact.EndingReason))) {
                throw new InvalidOperationException("Weather must advance its stream once and preserve its concrete result and ending.");
            }
        } else if (fact.Environment is not null) { throw new InvalidOperationException("Only weather can replace the environment stream."); }
    }
    private static bool SameActivity(CycleActivity? a, CycleActivity? b) => ReferenceEquals(a, b) || a is not null && b is not null &&
        (a.Kind, a.Id, a.StartedAt, a.DurationTicks, a.Due, a.ResourceId, a.TargetCount, a.CompletedCount, a.SelectedAll, a.CycleIndex) ==
        (b.Kind, b.Id, b.StartedAt, b.DurationTicks, b.Due, b.ResourceId, b.TargetCount, b.CompletedCount, b.SelectedAll, b.CycleIndex);
    private static bool SameSurvival(CycleSurvival? a, CycleSurvival? b) => ReferenceEquals(a, b) || a is not null && b is not null &&
        (a.BaselineAt, a.FoodUnits, a.WaterUnits, a.WarmthUnits, a.FoodPerHour, a.WaterPerHour, a.WarmthPerHour, a.WarnedMask, a.PendingMask, a.PendingSince) ==
        (b.BaselineAt, b.FoodUnits, b.WaterUnits, b.WarmthUnits, b.FoodPerHour, b.WaterPerHour, b.WarmthPerHour, b.WarnedMask, b.PendingMask, b.PendingSince);
    private static (CycleNoticeKind, string, string?, string, ModelTime, string, int) NoticeFields(CycleNotice n) =>
        (n.Kind, n.ResourceId, n.ActorId, n.SourceId, n.At, n.Reason, n.Count);
}
