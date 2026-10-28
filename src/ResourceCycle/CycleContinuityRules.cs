using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.ResourceCycle;

/// <summary>Pure planning of bounded personal continuity. Fold only installs these recorded replacements.</summary>
internal static class CycleContinuityRules {
    internal static bool SameOutcome(CycleActionOutcome? a, CycleActionOutcome? b) =>
        ReferenceEquals(a, b) || a is not null && b is not null && a.DeliveredAt == b.DeliveredAt &&
        a.ActivityState == b.ActivityState && SameAction(a.Action, b.Action) && SameSnapshot(a.Before, b.Before) &&
        SameSnapshot(a.After, b.After) && SameSummary(a.Summary, b.Summary) && a.Handoff == b.Handoff;
    internal static bool SamePending(CyclePendingAction a, CycleActionOutcome b) =>
        SameAction(a.Action, b.Action) && SameSnapshot(a.Before, b.Before) &&
        SameSummary(a.Summary, b.Summary) && a.Handoff == b.Handoff;
    private static bool SameAction(CycleAcceptedAction a, CycleAcceptedAction b) =>
        (a.ActorId, a.SourceContextSequence, a.ToolCallId, a.OperationRecordId, a.Kind, a.AcceptedAt, a.Hours,
            a.ResourceId, a.TargetCount, a.SelectedAll, a.PassageId, a.ActivityId, a.MovementGeneration) ==
        (b.ActorId, b.SourceContextSequence, b.ToolCallId, b.OperationRecordId, b.Kind, b.AcceptedAt, b.Hours,
            b.ResourceId, b.TargetCount, b.SelectedAll, b.PassageId, b.ActivityId, b.MovementGeneration);
    private static bool SameSnapshot(CycleContinuitySnapshot a, CycleContinuitySnapshot b) =>
        (a.FoodUnits, a.WaterUnits, a.WarmthUnits, a.PlaceId, a.PassageId, a.KnownTargetPlaceId,
            a.ActivityId, a.CompletedCount, a.TargetCount, a.ActivityDue, a.PlaceName, a.KnownTargetPlaceName) ==
        (b.FoodUnits, b.WaterUnits, b.WarmthUnits, b.PlaceId, b.PassageId, b.KnownTargetPlaceId,
            b.ActivityId, b.CompletedCount, b.TargetCount, b.ActivityDue, b.PlaceName, b.KnownTargetPlaceName) &&
        a.Backpack.Count == b.Backpack.Count && a.Backpack.All(i => b.Backpack.TryGetValue(i.Key, out int count) && count == i.Value);
    private static bool SameSummary(CycleOutcomeSummary a, CycleOutcomeSummary b) =>
        (a.GatherItemId, a.GatheredCount, a.CalmWeatherCount, a.ColdWeatherCount, a.WeatherWarmthLossUnits,
            a.LightChangeCount, a.Reasons, a.WarningMask, a.GatherStopReason, a.IsSaturated) ==
        (b.GatherItemId, b.GatheredCount, b.CalmWeatherCount, b.ColdWeatherCount, b.WeatherWarmthLossUnits,
            b.LightChangeCount, b.Reasons, b.WarningMask, b.GatherStopReason, b.IsSaturated);

    internal static CycleFact Enrich(CycleWorld world, CycleFact fact, CycleControllerOperation? operation = null) {
        GraphSpatialState? spatial = ResultingSpatial(world, fact);
        var changes = new List<CycleCharacter>();
        foreach (CycleCharacter actor in fact.ChangedCharacters) {
            CycleCharacter previous = world.GetCharacter(actor.Id);
            if (previous.Continuity is not { } continuity) { changes.Add(actor); continue; }
            CyclePendingAction? pending = continuity.Pending;
            bool accepts = operation is not null && fact.ActorId == actor.Id &&
                (operation.Command.Kind is CycleCommandKind.Go or CycleCommandKind.Wait or CycleCommandKind.Gather ||
                operation.Command.Kind == CycleCommandKind.Continue && !NeedsDecision(actor, spatial));
            if (accepts) {
                if (pending is not null || continuity.ContextSequence == long.MaxValue) {
                    throw new InvalidOperationException("Cannot accept an action before delivery or beyond the context identity limit.");
                }
                CycleCommand command = operation!.Command;
                SpatialEntity? entity = spatial?.Entities.Single(e => e.Id.Value == actor.Id);
                var moving = entity?.Location as TraversingLocation;
                var action = new CycleAcceptedAction(actor.Id, continuity.ContextSequence, operation.ToolCallId,
                    operation.OperationRecordId, command.Kind switch {
                        CycleCommandKind.Wait => CycleAcceptedActionKind.Wait,
                        CycleCommandKind.Gather => CycleAcceptedActionKind.Gather,
                        CycleCommandKind.Go => CycleAcceptedActionKind.Go,
                        _ => CycleAcceptedActionKind.Continue
                    }, fact.At, command.Hours, actor.Activity?.ResourceId, actor.Activity?.TargetCount ?? 0,
                    actor.Activity?.SelectedAll ?? false, moving?.PassageId.Value, actor.Activity?.Id,
                    moving is null ? null : entity!.MovementGeneration);
                pending = new(action, Snapshot(world.Business, actor, fact.At, world.Expedition?.Spatial), handoff: continuity.DraftHandoff);
                changes.Add(actor.WithContinuity(new(continuity.ContextSequence, pending: pending)));
                continue;
            }
            if (pending is null) { changes.Add(actor); continue; }
            CycleOutcomeSummary summary = Accumulate(world, fact, previous, actor, pending);
            pending = new(pending.Action, pending.Before, summary, pending.Handoff);
            bool terminal = fact.Outcome is CycleExpeditionOutcome.Dead or CycleExpeditionOutcome.Escaped;
            var next = terminal
                ? new CycleDecisionContinuity(continuity.ContextSequence,
                    incoming: new(pending, fact.At, Snapshot(world.Business, actor, fact.At, spatial, pending), CycleOutcomeActivityState.Terminal))
                : new CycleDecisionContinuity(continuity.ContextSequence, pending: pending);
            changes.Add(actor.WithContinuity(next));
        }
        return fact.WithCharacters(changes);
    }

    internal static CycleFact OpenContext(CycleWorld world, string actorId) {
        CycleCharacter actor = world.GetCharacter(actorId);
        CycleDecisionContinuity continuity = actor.Continuity ?? throw new InvalidOperationException("Continuity is not enabled.");
        CyclePendingAction pending = continuity.Pending ?? throw new InvalidOperationException("No pending action to deliver.");
        if (!CycleActions.NeedsDecision(world, actorId)) { throw new InvalidOperationException("No decision boundary is ready."); }
        bool ongoing = actor.Activity is not null || CycleActions.IsTravelling(world, actorId);
        var outcome = new CycleActionOutcome(pending, world.Time, Snapshot(world.Business, actor, world.Time, world.Expedition?.Spatial, pending),
            ongoing ? CycleOutcomeActivityState.Ongoing : CycleOutcomeActivityState.Completed);
        CycleCharacter updated = actor.WithContinuity(new(checked(continuity.ContextSequence + 1), incoming: outcome), advanceGeneration: true);
        return new(CycleEventKind.DecisionContextOpened, world.Time, actorId, changedCharacters: [updated], result: "已开启新的决策回合并封存上次行动结果。");
    }

    internal static CycleFact EditHandoff(CycleWorld world, string actorId, string? text) {
        CycleCharacter actor = world.GetCharacter(actorId);
        CycleDecisionContinuity continuity = actor.Continuity ?? throw new InvalidOperationException("Continuity is not enabled.");
        if (continuity.Pending is not null || !CycleDecisionContinuity.ValidHandoff(text)) {
            throw new InvalidOperationException("The handoff cannot be edited in this state.");
        }
        var next = new CycleDecisionContinuity(continuity.ContextSequence, continuity.Incoming, text);
        return new(CycleEventKind.HandoffEdited, world.Time, actorId,
            changedCharacters: [actor.WithContinuity(next, advanceGeneration: true)], lastServedActorId: actorId,
            result: text is null ? "已清空给下一回合的交接文字。" : "已保存给下一回合的交接文字。");
    }

    private static bool NeedsDecision(CycleCharacter actor, GraphSpatialState? spatial) =>
        actor.Notices.Count > 0 || actor.Survival?.PendingMask > 0 || actor.Activity is null &&
            spatial?.Entities.Single(e => e.Id.Value == actor.Id).Location is not TraversingLocation;

    private static GraphSpatialState? ResultingSpatial(CycleWorld world, CycleFact fact) {
        GraphSpatialState? spatial = world.Expedition?.Spatial;
        if (spatial is not null) {
            foreach (GraphSpatialFact movement in fact.SpatialFacts) {
                spatial = CycleExpeditionMap.Reducer.Apply(spatial, new(fact.At, 0), movement);
            }
        }
        return spatial;
    }

    internal static void ValidateOpenedOutcome(CycleBusinessState business, CycleCharacter actor, CycleActionOutcome incoming) {
        CyclePendingAction pending = actor.Continuity?.Pending ?? throw new InvalidOperationException("No action is pending delivery.");
        bool ongoing = actor.Activity is not null || CycleActions.IsTravelling(business, actor.Id);
        if (incoming.ActivityState != (ongoing ? CycleOutcomeActivityState.Ongoing : CycleOutcomeActivityState.Completed) ||
            !SameSnapshot(incoming.After, Snapshot(business, actor, business.Time, business.Expedition?.Spatial, pending))) {
            throw new InvalidOperationException("The delivered endpoint must match the character's recorded state.");
        }
    }

    private static CycleContinuitySnapshot Snapshot(CycleBusinessState business, CycleCharacter actor, ModelTime at,
        GraphSpatialState? spatial, CyclePendingAction? completed = null) {
        CycleSurvival? body = actor.Survival?.Project(at);
        SpatialLocation? location = spatial?.Entities.Single(e => e.Id.Value == actor.Id).Location;
        string? place = (location as AtPlaceLocation)?.PlaceId.Value;
        var moving = location as TraversingLocation;
        string? knownTarget = moving is not null && business.Expedition!.Visits.Any(v =>
            v.ActorId == actor.Id && v.PlaceId == moving.TargetPlaceId.Value) ? moving.TargetPlaceId.Value : null;
        long? activityId = actor.Activity?.Id;
        int completedCount = actor.Activity?.CompletedCount ?? 0, targetCount = actor.Activity?.TargetCount ?? 0;
        ModelTime? due = actor.Activity?.Due ?? moving?.ArrivalDue;
        if (actor.Activity is null && completed?.Action.ActivityId is { } endedId) {
            activityId = endedId;
            completedCount = checked((int)(completed.Before.CompletedCount + completed.Summary.GatheredCount));
            targetCount = completed.Action.TargetCount;
            due = at;
        }
        return new(body?.FoodUnits, body?.WaterUnits, body?.WarmthUnits, actor.Backpack.Items,
            place, moving?.PassageId.Value, knownTarget, activityId, completedCount, targetCount, due,
            place is null ? null : CycleExpeditionMap.GetPlace(place).Name,
            knownTarget is null ? null : CycleExpeditionMap.GetPlace(knownTarget).Name);
    }

    private static CycleOutcomeSummary Accumulate(CycleWorld world, CycleFact fact,
        CycleCharacter previous, CycleCharacter actor, CyclePendingAction pending) {
        CycleOutcomeSummary summary = pending.Summary;
        string? item = summary.GatherItemId;
        long gathered = summary.GatheredCount, calm = summary.CalmWeatherCount, cold = summary.ColdWeatherCount,
            loss = summary.WeatherWarmthLossUnits, light = summary.LightChangeCount;
        CycleWakeReason reasons = summary.Reasons;
        int warning = summary.WarningMask;
        CycleGatherStopReason? stop = summary.GatherStopReason;
        bool saturated = summary.IsSaturated;
        bool own = fact.ActorId == actor.Id;
        if (own && fact.Kind == CycleEventKind.GatherYielded && previous.Activity?.Id == pending.Action.ActivityId) {
            item = world.GetResource(fact.ResourceId!).ItemId;
            gathered = Add(gathered, actor.Backpack.Count(item) - previous.Backpack.Count(item), ref saturated);
        }
        if (own && fact.Kind is CycleEventKind.GatherYielded or CycleEventKind.GatherFinished && actor.Activity is null &&
            previous.Activity?.Id == pending.Action.ActivityId) {
            stop = fact.GatherStopReason ?? throw new InvalidOperationException("A completed gather requires a structured stop reason.");
            reasons |= stop == CycleGatherStopReason.TargetReached ? CycleWakeReason.ActivityCompleted : CycleWakeReason.ActivityStopped;
        }
        if (own && fact.Kind == CycleEventKind.WaitCompleted) { reasons |= CycleWakeReason.ActivityCompleted; }
        if (own && fact.Kind == CycleEventKind.MoveArrived) { reasons |= CycleWakeReason.Arrival; }
        if (own && fact.Kind == CycleEventKind.SurvivalWarning) {
            reasons |= CycleWakeReason.SurvivalWarning;
            warning |= actor.Survival!.PendingMask;
        }
        if (own && fact.Kind == CycleEventKind.WeatherResolved) {
            CycleEnvironment weather = fact.Environment!;
            if (weather.LastCold) { cold = Add(cold, 1, ref saturated); }
            else { calm = Add(calm, 1, ref saturated); }
            loss = Add(loss, weather.LastWarmthLossUnits, ref saturated);
        }
        if (fact.Kind == CycleEventKind.LightChanged) { light = Add(light, 1, ref saturated); }
        foreach (CycleNotice notice in actor.Notices) {
            if (previous.Notices.Any(old => old.AggregationKey == notice.AggregationKey && old.SourceId == notice.SourceId)) { continue; }
            reasons |= notice.Kind == CycleNoticeKind.CollectorJoined ? CycleWakeReason.CollectorJoined : CycleWakeReason.ResourceDepleted;
        }
        if (fact.Outcome is CycleExpeditionOutcome.Dead or CycleExpeditionOutcome.Escaped) { reasons |= CycleWakeReason.Terminal; }
        return new(item, gathered, calm, cold, loss, light, reasons, warning, stop, saturated);
    }
    private static long Add(long value, long amount, ref bool saturated) {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        if (amount > long.MaxValue - value) { saturated = true; return long.MaxValue; }
        return value + amount;
    }
}
