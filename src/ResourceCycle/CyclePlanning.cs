using DramaBoard.Inventory;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.ResourceCycle;

/// <summary>Business planning only. It emits concrete effects, including perceptions, before the E/S boundary.</summary>
internal static class CyclePlanning {
    public static CycleFact Occurrence(CycleWorld world, CyclePrediction prediction) => prediction.Kind switch {
        CyclePredictionKind.ChangeLight => ChangeLight(world, prediction.Due),
        CyclePredictionKind.RegenerateResource => Regenerate(world, prediction.TargetId, prediction.Due),
        CyclePredictionKind.DestroyResource => Destroy(world, prediction.TargetId, prediction.Due),
        CyclePredictionKind.CompleteActivity => CompleteActivity(world, prediction.TargetId, prediction.Due),
        CyclePredictionKind.SurvivalFatal or CyclePredictionKind.SurvivalWarning => CycleSurvivalRules.Boundary(world, prediction),
        CyclePredictionKind.Weather => CycleEnvironmentRules.Plan(world, prediction.Due),
        CyclePredictionKind.MarketRefresh => CycleEconomyRules.Refresh(world, prediction.Due),
        _ => throw new InvalidOperationException("This occurrence requires a Player decision.")
    };

    public static CycleFact Decision(CycleWorld world, string actorId, CycleCommand command) {
        CycleCharacter actor = world.GetCharacter(actorId);
        switch (command.Kind) {
            case CycleCommandKind.Buy or CycleCommandKind.Sell or CycleCommandKind.Produce or CycleCommandKind.GiveMoney:
                return CycleEconomyRules.Plan(world, actorId, command);
            case CycleCommandKind.Follow or CycleCommandKind.Unfollow or CycleCommandKind.DismissFollowers:
                return CycleFollowingRules.Plan(world, actor, command);
            case CycleCommandKind.Say or CycleCommandKind.Give or CycleCommandKind.Shout:
                return CycleCommunicationRules.Plan(world, actorId, command);
            case CycleCommandKind.Continue: {
                string result = "已确认感知，当前活动与截止时刻保持不变。";
                return new(CycleEventKind.AttentionAcknowledged, world.Time, actorId,
                    changedCharacters: [actor.Change(actor.Activity, notices: [], result: result, communication: actor.Communication.Acknowledge())], lastServedActorId: actorId, result: result);
            }
            case CycleCommandKind.Cancel: {
                CycleActivity current = actor.Activity!;
                string result = current.Kind == CycleActivityKind.Gather
                    ? $"已取消采集，目标 {current.TargetCount} 份，已取得 {current.CompletedCount} 份；未完成的一份不结算。"
                    : current.Kind == CycleActivityKind.Produce ? "已取消生产，托管原料全部退回。" : "已取消等待。";
                return new(CycleEventKind.ActivityCancelled, world.Time, actorId,
                    changedCharacters: [actor.Change(null, CycleEconomyRules.ReturnActivityInputs(actor), notices: [], result: result, communication: actor.Communication.Acknowledge())], lastServedActorId: actorId, result: result);
            }
            case CycleCommandKind.Wait: {
                long duration = command.Hours * CycleTime.Hour;
                var activity = new CycleActivity(CycleActivityKind.Wait, actor.NextActivityId, world.Time, duration,
                    new(checked(world.Time.Ticks + duration)));
                string result = $"开始等待 {command.Hours} 小时，预计 {activity.Due} 完成。";
                return new(CycleEventKind.WaitStarted, world.Time, actorId,
                    changedCharacters: [actor.Change(activity, notices: [], result: result, nextActivityId: checked(actor.NextActivityId + 1), communication: actor.Communication.Acknowledge())],
                    lastServedActorId: actorId, result: result);
            }
            case CycleCommandKind.Gather:
                return StartGather(world, actor, command);
            case CycleCommandKind.Go: {
                string result = $"沿通路 {command.PassageId} 出发；本段移动不可取消。";
                CycleCharacter[] members = CycleFollowingRules.MovingMembers(world.Business, actorId).ToArray();
                GraphSpatialFact[] movements = members.SelectMany(member =>
                    ((SpatialPlanAccepted)CycleExpeditionMap.Planner.TryStartTraversal(
                        world.Expedition!.Spatial, new(member.Id), new(command.PassageId!), 1, world.Time)).Facts).ToArray();
                return new(CycleEventKind.MoveStarted, world.Time, actorId,
                    changedCharacters: members.Select(member => member.Id == actorId
                        ? member.Change(null, notices: [], result: result, communication: member.Communication.Acknowledge())
                        : member.Change(null)), lastServedActorId: actorId,
                    result: result, spatialFacts: movements);
            }
            case CycleCommandKind.Use: {
                string result = CycleSurvivalRules.UseDescription(command.ItemId!)!;
                return new(CycleEventKind.ItemUsed, world.Time, actorId, changedCharacters: [actor.Change(actor.Activity,
                    actor.Backpack.Remove(command.ItemId!), result: result, survival: actor.Survival!.Supply(world.Time, command.ItemId!))],
                    lastServedActorId: actorId, result: result);
            }
            default: throw new InvalidOperationException("Unknown command after validation.");
        }
    }

    internal static CycleFact Arrive(CycleWorld world, ModelTime at, IReadOnlyList<GraphSpatialFact> facts) {
        if (facts.Count != 1 || facts[0] is not TraversalArrivedFact arrived) {
            throw new InvalidOperationException("This scenario supports only traversal arrivals from Spatial.");
        }
        CycleCharacter actor = world.GetCharacter(arrived.EntityId.Value);
        var traversal = (TraversingLocation)world.Expedition!.Spatial.Entities.Single(e => e.Id == arrived.EntityId).Location;
        CyclePlaceDefinition place = CycleExpeditionMap.GetPlace(traversal.TargetPlaceId.Value);
        string result = $"抵达{place.Name}。{place.Description}";
        CycleCharacter changed = actor.Change(null, result: result);
        return new(CycleEventKind.MoveArrived, at, actor.Id, changedCharacters: [changed],
            result: result, spatialFacts: facts);
    }

    /// <summary>Records direct observations in the planned fact; Fold never discovers new knowledge.</summary>
    internal static CycleFact Observe(CycleWorld world, CycleFact fact) {
        if (world.Expedition is null || fact.Kind == CycleEventKind.SpeechShouted) { return fact; }
        GraphSpatialState spatial = world.Expedition.Spatial;
        foreach (GraphSpatialFact spatialFact in fact.SpatialFacts) {
            spatial = CycleExpeditionMap.Reducer.Apply(spatial, new(fact.At, 0), spatialFact);
        }
        var resources = world.Resources.Select(r => fact.ChangedResources.SingleOrDefault(c => c.Id == r.Id) ?? r).ToArray();
        var visits = new List<CycleVisit>();
        foreach (CycleCharacter actor in world.Characters) {
            if (!actor.IsActive) { continue; }
            CycleCharacter after = fact.ChangedCharacters.SingleOrDefault(c => c.Id == actor.Id) ?? actor;
            SpatialLocation location = spatial.Entities.SingleOrDefault(e => e.Id.Value == actor.Id)?.Location ?? after.Ending!.Location;
            // Keep the departure snapshot too; moving does not reveal the destination.
            if (fact.Kind == CycleEventKind.MoveStarted && fact.SpatialFacts.OfType<TraversalStartedFact>().Any(started => started.EntityId.Value == actor.Id)) {
                location = world.Expedition.Spatial.Entities.Single(e => e.Id.Value == actor.Id).Location;
            }
            if (location is AtPlaceLocation at) {
                visits.Add(new(actor.Id, at.PlaceId.Value, fact.At,
                    resources.Where(r => CycleExpeditionMap.GetResourcePlace(r.Id) == at.PlaceId.Value)));
            }
        }
        return fact.WithVisits(visits);
    }

    private static CycleFact StartGather(CycleWorld world, CycleCharacter actor, CycleCommand command) {
        CycleResource resource = world.GetResource(command.ResourceId!);
        int target = command.All ? resource.Stock : command.Count;
        long duration = CycleActions.HarvestDuration(resource, actor);
        var activity = new CycleActivity(CycleActivityKind.Gather, actor.NextActivityId, world.Time, duration,
            new(checked(world.Time.Ticks + duration)), resource.Id, target, selectedAll: command.All);
        string result = $"开始采集 {resource.Name}，目标固定为 {target} 份，下一份于 {activity.Due} 结算；不预留库存。";
        var changes = new Dictionary<string, CycleCharacter>(StringComparer.Ordinal) {
            [actor.Id] = actor.Change(activity, notices: [], result: result, nextActivityId: checked(actor.NextActivityId + 1), communication: actor.Communication.Acknowledge())
        };
        var notice = new CycleNotice(CycleNoticeKind.CollectorJoined, resource.Id, actor.Id,
            $"join:{actor.Id}:{activity.Id}", world.Time, $"{actor.Name} 开始在此采集。");
        NotifyCollectors(world, changes, resource.Id, notice, exceptActorId: actor.Id);
        return new(CycleEventKind.GatherStarted, world.Time, actor.Id, resource.Id,
            changedCharacters: OrderedChanges(world, changes), lastServedActorId: actor.Id, result: result);
    }

    private static CycleFact ChangeLight(CycleWorld world, ModelTime time) {
        var light = new CycleLight(!world.Light.IsDay, CycleTime.NextLightChange(time));
        CycleResource[] resources = world.Resources.Where(r => r.Stock < r.Capacity)
            .Select(r => r.Change(r.Stock, r.ProgressAt(time), r.GrowsInDaylight == light.IsDay ? time : null)).ToArray();
        return new(CycleEventKind.LightChanged, time, light: light, changedResources: resources,
            result: light.IsDay ? "天亮了。" : "夜幕降临。");
    }
    private static CycleFact Regenerate(CycleWorld world, string resourceId, ModelTime time) {
        CycleResource previous = world.GetResource(resourceId);
        int stock = checked(previous.Stock + 1);
        CycleResource resource = previous.Change(stock, 0, stock < previous.Capacity && previous.GrowsInDaylight == world.Light.IsDay ? time : null);
        return new(CycleEventKind.ResourceRegenerated, time, resourceId: resourceId, changedResources: [resource],
            result: $"{resource.Name} 再生 1 份，余量 {stock}/{resource.Capacity}。");
    }
    private static CycleFact Destroy(CycleWorld world, string resourceId, ModelTime time) {
        CycleResource previous = world.GetResource(resourceId);
        CycleResource resource = previous.Change(0, 0, previous.GrowsInDaylight == world.Light.IsDay ? time : null);
        var changes = new Dictionary<string, CycleCharacter>(StringComparer.Ordinal);
        if (previous.Stock > 0) {
            NotifyCollectors(world, changes, resourceId, new(CycleNoticeKind.ResourceDepleted, resourceId, null,
                $"destroy:{world.Script.NextIndex}", time, "环境销毁事件清空了资源。"));
        }
        return new(CycleEventKind.ResourceDestroyed, time, resourceId: resourceId, changedResources: [resource],
            script: new(world.Script.Entries, checked(world.Script.NextIndex + 1)), changedCharacters: OrderedChanges(world, changes),
            result: $"{resource.Name} 被清空，再生进度归零。");
    }
    private static CycleFact CompleteActivity(CycleWorld world, string actorId, ModelTime time) {
        CycleCharacter actor = world.GetCharacter(actorId);
        CycleActivity activity = actor.Activity ?? throw new InvalidOperationException("No activity can complete.");
        if (activity.Kind == CycleActivityKind.Produce) { return CycleEconomyRules.Complete(world, actorId, time); }
        if (activity.Kind == CycleActivityKind.Wait) {
            string result = $"等待完成，现在是 {time}。";
            return new(CycleEventKind.WaitCompleted, time, actorId,
                changedCharacters: [actor.Change(null, result: result)], result: result);
        }
        return CompleteGather(world, actor, activity, time);
    }
    private static CycleFact CompleteGather(CycleWorld world, CycleCharacter actor, CycleActivity activity, ModelTime time) {
        CycleResource previous = world.GetResource(activity.ResourceId!);
        string? failure = previous.Stock == 0 ? "本周期结算时资源已空" : actor.Backpack.CheckAdd(previous.ItemId) switch {
            InventoryAdmission.ItemQuantityLimit => "背包该物品数量已达上限",
            InventoryAdmission.CapacityLimit => "背包总件数已达容量上限",
            _ => null
        };
        if (failure is not null) {
            string result = $"{previous.Name} 采集结束：目标 {activity.TargetCount}，取得 {activity.CompletedCount}；{failure}。该次结算后余量 {previous.Stock}/{previous.Capacity}。";
            return new(CycleEventKind.GatherFinished, time, actor.Id, previous.Id,
                changedCharacters: [actor.Change(null, result: result)], result: result,
                gatherStopReason: previous.Stock == 0 ? CycleGatherStopReason.ResourceEmpty :
                    actor.Backpack.CheckAdd(previous.ItemId) == InventoryAdmission.ItemQuantityLimit ? CycleGatherStopReason.ItemLimit : CycleGatherStopReason.CapacityLimit);
        }
        int completed = checked(activity.CompletedCount + 1);
        CycleResource resource = previous.Stock == previous.Capacity
            ? previous.Change(previous.Stock - 1, 0, previous.GrowsInDaylight == world.Light.IsDay ? time : null)
            : previous.Change(previous.Stock - 1, previous.AccumulatedTicks, previous.ActiveSince);
        ItemInventory backpack = actor.Backpack.Add(resource.ItemId);
        CycleActivity? next = null;
        string finish = "已达到目标";
        if (completed < activity.TargetCount) {
            long duration = CycleActions.HarvestDuration(resource, actor);
            if (time.Ticks <= long.MaxValue - duration) {
                next = new(CycleActivityKind.Gather, activity.Id, time, duration, new(time.Ticks + duration),
                    resource.Id, activity.TargetCount, completed, activity.SelectedAll, checked(activity.CycleIndex + 1));
                finish = "继续下一周期";
            } else { finish = "无法继续安排时间，批次结束"; }
        }
        string resultText = $"取得 {resource.Name} 1 份，目标 {activity.TargetCount}，已取得 {completed}；{finish}。该次结算后余量 {resource.Stock}/{resource.Capacity}。";
        var changes = new Dictionary<string, CycleCharacter>(StringComparer.Ordinal) {
            [actor.Id] = actor.Change(next, backpack, result: resultText)
        };
        if (resource.Stock == 0) {
            // Receivers are determined from post-transition activity state. A completed collector receives its result,
            // while a collector with another cycle also receives this perception without losing the new activity.
            var notice = new CycleNotice(CycleNoticeKind.ResourceDepleted, resource.Id, actor.Id,
                $"gather:{actor.Id}:{activity.Id}:{activity.CycleIndex}", time, $"{actor.Name} 取走了最后一份 {resource.Name}。");
            NotifyCollectors(world, changes, resource.Id, notice);
        }
        return new(CycleEventKind.GatherYielded, time, actor.Id, resource.Id, changedResources: [resource],
            changedCharacters: OrderedChanges(world, changes), result: resultText,
            gatherStopReason: next is not null ? null : completed >= activity.TargetCount ? CycleGatherStopReason.TargetReached : CycleGatherStopReason.TimeLimit);
    }
    private static void NotifyCollectors(CycleWorld world, Dictionary<string, CycleCharacter> changes,
        string resourceId, CycleNotice notice, string? exceptActorId = null) {
        foreach (CycleCharacter previous in world.Characters) {
            CycleCharacter current = changes.GetValueOrDefault(previous.Id, previous);
            if (current.Id != exceptActorId && current.Activity is { Kind: CycleActivityKind.Gather } activity && activity.ResourceId == resourceId) {
                changes[current.Id] = current.Notify(notice);
            }
        }
    }
    private static IEnumerable<CycleCharacter> OrderedChanges(CycleWorld world, Dictionary<string, CycleCharacter> changes) =>
        world.Characters.Where(c => changes.ContainsKey(c.Id)).Select(c => changes[c.Id]);
}
