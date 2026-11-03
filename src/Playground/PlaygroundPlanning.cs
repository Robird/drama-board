using DramaBoard.Inventory;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.Playground;

/// <summary>Business planning only. It emits concrete effects, including perceptions, before the E/S boundary.</summary>
internal static class PlaygroundPlanning {
    public static PlaygroundFact Occurrence(PlaygroundWorld world, PlaygroundPrediction prediction) => prediction.Kind switch {
        PlaygroundPredictionKind.ChangeLight => ChangeLight(world, prediction.Due),
        PlaygroundPredictionKind.RegenerateResource => Regenerate(world, prediction.TargetId, prediction.Due),
        PlaygroundPredictionKind.DestroyResource => Destroy(world, prediction.TargetId, prediction.Due),
        PlaygroundPredictionKind.CompleteActivity => CompleteActivity(world, prediction.TargetId, prediction.Due),
        PlaygroundPredictionKind.SurvivalFatal or PlaygroundPredictionKind.SurvivalWarning => PlaygroundSurvivalRules.Boundary(world, prediction),
        PlaygroundPredictionKind.Weather => PlaygroundEnvironmentRules.Plan(world, prediction.Due),
        PlaygroundPredictionKind.TradesExpire => PlaygroundTradeRules.Expire(world),
        PlaygroundPredictionKind.MarketRefresh => PlaygroundEconomyRules.Refresh(world, prediction.Due),
        _ => throw new InvalidOperationException("This occurrence requires a Player decision.")
    };

    public static PlaygroundFact Decision(PlaygroundWorld world, string actorId, PlaygroundCommand command) {
        PlaygroundCharacter actor = world.GetCharacter(actorId);
        switch (command.Kind) {
            case PlaygroundCommandKind.Offer or PlaygroundCommandKind.Accept or PlaygroundCommandKind.Reject or PlaygroundCommandKind.Withdraw:
                return PlaygroundTradeRules.Plan(world, actorId, command);
            case PlaygroundCommandKind.Buy or PlaygroundCommandKind.Sell or PlaygroundCommandKind.Produce or PlaygroundCommandKind.GiveMoney:
                return PlaygroundEconomyRules.Plan(world, actorId, command);
            case PlaygroundCommandKind.Follow or PlaygroundCommandKind.Unfollow or PlaygroundCommandKind.DismissFollowers:
                return PlaygroundFollowingRules.Plan(world, actor, command);
            case PlaygroundCommandKind.Say or PlaygroundCommandKind.Give or PlaygroundCommandKind.Shout:
                return PlaygroundCommunicationRules.Plan(world, actorId, command);
            case PlaygroundCommandKind.Continue: {
                string result = "已确认感知，当前活动与截止时刻保持不变。";
                return new(PlaygroundEventKind.AttentionAcknowledged, world.Time, actorId,
                    changedCharacters: [actor.Change(actor.Activity, notices: [], result: result, communication: actor.Communication.Acknowledge())], lastServedActorId: actorId, result: result);
            }
            case PlaygroundCommandKind.Cancel: {
                PlaygroundActivity current = actor.Activity!;
                string result = current.Kind == PlaygroundActivityKind.Gather
                    ? $"已取消采集，目标 {current.TargetCount} 份，已取得 {current.CompletedCount} 份；未完成的一份不结算。"
                    : current.Kind == PlaygroundActivityKind.Produce ? "已取消生产，托管原料全部退回。" : "已取消等待。";
                return new(PlaygroundEventKind.ActivityCancelled, world.Time, actorId,
                    changedCharacters: [actor.Change(null, PlaygroundEconomyRules.ReturnActivityInputs(actor), notices: [], result: result, communication: actor.Communication.Acknowledge())], lastServedActorId: actorId, result: result);
            }
            case PlaygroundCommandKind.Wait: {
                long duration = command.Hours * PlaygroundTime.Hour;
                var activity = new PlaygroundActivity(PlaygroundActivityKind.Wait, actor.NextActivityId, world.Time, duration,
                    new(checked(world.Time.Ticks + duration)));
                string result = $"开始等待 {command.Hours} 小时，预计 {activity.Due} 完成。";
                return new(PlaygroundEventKind.WaitStarted, world.Time, actorId,
                    changedCharacters: [actor.Change(activity, notices: [], result: result, nextActivityId: checked(actor.NextActivityId + 1), communication: actor.Communication.Acknowledge())],
                    lastServedActorId: actorId, result: result);
            }
            case PlaygroundCommandKind.Gather:
                return StartGather(world, actor, command);
            case PlaygroundCommandKind.Go: {
                PlaygroundPassageDefinition entrance = world.Definition.GetEntrances(PlaygroundActions.CurrentPlace(world, actorId)!)
                    .Single(p => p.EntranceId == command.EntranceId);
                string result = $"沿入口 {entrance.EntranceId} 出发；本段移动不可取消。";
                PlaygroundCharacter[] members = PlaygroundFollowingRules.MovingMembers(world.Business, actorId).ToArray();
                GraphSpatialFact[] movements = members.SelectMany(member =>
                    ((SpatialPlanAccepted)world.Definition.Runtime.Planner.TryStartTraversal(
                        world.Expedition!.Spatial, new(member.Id), new(entrance.Id), 1, world.Time)).Facts).ToArray();
                return new(PlaygroundEventKind.MoveStarted, world.Time, actorId,
                    changedCharacters: members.Select(member => member.Id == actorId
                        ? member.Change(null, notices: [], result: result, communication: member.Communication.Acknowledge())
                        : member.Change(null)), lastServedActorId: actorId,
                    result: result, spatialFacts: movements);
            }
            case PlaygroundCommandKind.Use: {
                string result = PlaygroundSurvivalRules.UseDescription(world.Definition, command.ItemId!)!;
                return new(PlaygroundEventKind.ItemUsed, world.Time, actorId, changedCharacters: [actor.Change(actor.Activity,
                    actor.Backpack.Remove(command.ItemId!), result: result, survival: actor.Survival!.Supply(world.Time, command.ItemId!, world.Definition))],
                    lastServedActorId: actorId, result: result);
            }
            default: throw new InvalidOperationException("Unknown command after validation.");
        }
    }

    internal static PlaygroundFact Arrive(PlaygroundWorld world, ModelTime at, IReadOnlyList<GraphSpatialFact> facts) {
        if (facts.Count != 1 || facts[0] is not TraversalArrivedFact arrived) {
            throw new InvalidOperationException("This scenario supports only traversal arrivals from Spatial.");
        }
        PlaygroundCharacter actor = world.GetCharacter(arrived.EntityId.Value);
        var traversal = (TraversingLocation)world.Expedition!.Spatial.Entities.Single(e => e.Id == arrived.EntityId).Location;
        PlaygroundPlaceDefinition place = world.Definition.GetPlace(traversal.TargetPlaceId.Value);
        string result = $"抵达{place.Name}。{place.Description}";
        PlaygroundCharacter changed = actor.Change(null, result: result);
        return new(PlaygroundEventKind.MoveArrived, at, actor.Id, changedCharacters: [changed],
            result: result, spatialFacts: facts, confirmedPassages: world.Expedition.KnowsPassage(actor.Id, traversal.PassageId.Value)
                ? [] : [new(actor.Id, traversal.PassageId.Value)]);
    }

    /// <summary>Records direct observations in the planned fact; Fold never discovers new knowledge.</summary>
    internal static PlaygroundFact Observe(PlaygroundWorld world, PlaygroundFact fact) {
        if (world.Expedition is null || fact.Kind == PlaygroundEventKind.SpeechShouted) { return fact; }
        GraphSpatialState spatial = world.Expedition.Spatial;
        foreach (GraphSpatialFact spatialFact in fact.SpatialFacts) {
            spatial = world.Definition.Runtime.Reducer.Apply(spatial, new(fact.At, 0), spatialFact);
        }
        var resources = world.Resources.Select(r => fact.ChangedResources.SingleOrDefault(c => c.Id == r.Id) ?? r).ToArray();
        var visits = new List<PlaygroundVisit>();
        foreach (PlaygroundCharacter actor in world.Characters) {
            if (!actor.IsActive) { continue; }
            PlaygroundCharacter after = fact.ChangedCharacters.SingleOrDefault(c => c.Id == actor.Id) ?? actor;
            SpatialLocation location = spatial.Entities.SingleOrDefault(e => e.Id.Value == actor.Id)?.Location ?? after.Ending!.Location;
            // Keep the departure snapshot too; moving does not reveal the destination.
            if (fact.Kind == PlaygroundEventKind.MoveStarted && fact.SpatialFacts.OfType<TraversalStartedFact>().Any(started => started.EntityId.Value == actor.Id)) {
                location = world.Expedition.Spatial.Entities.Single(e => e.Id.Value == actor.Id).Location;
            }
            if (location is AtPlaceLocation at) {
                visits.Add(new(actor.Id, at.PlaceId.Value, fact.At,
                    resources.Where(r => world.Definition.GetResourcePlace(r.Id) == at.PlaceId.Value)));
            }
        }
        return fact.WithVisits(visits);
    }

    private static PlaygroundFact StartGather(PlaygroundWorld world, PlaygroundCharacter actor, PlaygroundCommand command) {
        PlaygroundResource resource = world.GetResource(command.ResourceId!);
        int target = command.All ? resource.Stock : command.Count;
        long duration = PlaygroundActions.HarvestDuration(resource, actor);
        var activity = new PlaygroundActivity(PlaygroundActivityKind.Gather, actor.NextActivityId, world.Time, duration,
            new(checked(world.Time.Ticks + duration)), resource.Id, target, selectedAll: command.All);
        string result = $"开始采集 {resource.Name}，目标固定为 {target} 份，下一份于 {activity.Due} 结算；不预留库存。";
        var changes = new Dictionary<string, PlaygroundCharacter>(StringComparer.Ordinal) {
            [actor.Id] = actor.Change(activity, notices: [], result: result, nextActivityId: checked(actor.NextActivityId + 1), communication: actor.Communication.Acknowledge())
        };
        var notice = new PlaygroundNotice(PlaygroundNoticeKind.CollectorJoined, resource.Id, actor.Id,
            $"join:{actor.Id}:{activity.Id}", world.Time, $"{actor.Name} 开始在此采集。");
        NotifyCollectors(world, changes, resource.Id, notice, exceptActorId: actor.Id);
        return new(PlaygroundEventKind.GatherStarted, world.Time, actor.Id, resource.Id,
            changedCharacters: OrderedChanges(world, changes), lastServedActorId: actor.Id, result: result);
    }

    private static PlaygroundFact ChangeLight(PlaygroundWorld world, ModelTime time) {
        var light = new PlaygroundLight(!world.Light.IsDay, PlaygroundTime.NextLightChange(time));
        PlaygroundResource[] resources = world.Resources.Where(r => r.Stock < r.Capacity)
            .Select(r => r.Change(r.Stock, r.ProgressAt(time), r.GrowsInDaylight == light.IsDay ? time : null)).ToArray();
        return new(PlaygroundEventKind.LightChanged, time, light: light, changedResources: resources,
            result: light.IsDay ? "天亮了。" : "夜幕降临。");
    }
    private static PlaygroundFact Regenerate(PlaygroundWorld world, string resourceId, ModelTime time) {
        PlaygroundResource previous = world.GetResource(resourceId);
        int stock = checked(previous.Stock + 1);
        PlaygroundResource resource = previous.Change(stock, 0, stock < previous.Capacity && previous.GrowsInDaylight == world.Light.IsDay ? time : null);
        return new(PlaygroundEventKind.ResourceRegenerated, time, resourceId: resourceId, changedResources: [resource],
            result: $"{resource.Name} 再生 1 份，余量 {stock}/{resource.Capacity}。");
    }
    private static PlaygroundFact Destroy(PlaygroundWorld world, string resourceId, ModelTime time) {
        PlaygroundResource previous = world.GetResource(resourceId);
        PlaygroundResource resource = previous.Change(0, 0, previous.GrowsInDaylight == world.Light.IsDay ? time : null);
        var changes = new Dictionary<string, PlaygroundCharacter>(StringComparer.Ordinal);
        if (previous.Stock > 0) {
            NotifyCollectors(world, changes, resourceId, new(PlaygroundNoticeKind.ResourceDepleted, resourceId, null,
                $"destroy:{world.Script.NextIndex}", time, "环境销毁事件清空了资源。"));
        }
        return new(PlaygroundEventKind.ResourceDestroyed, time, resourceId: resourceId, changedResources: [resource],
            script: new(world.Script.Entries, checked(world.Script.NextIndex + 1)), changedCharacters: OrderedChanges(world, changes),
            result: $"{resource.Name} 被清空，再生进度归零。");
    }
    private static PlaygroundFact CompleteActivity(PlaygroundWorld world, string actorId, ModelTime time) {
        PlaygroundCharacter actor = world.GetCharacter(actorId);
        PlaygroundActivity activity = actor.Activity ?? throw new InvalidOperationException("No activity can complete.");
        if (activity.Kind == PlaygroundActivityKind.Produce) { return PlaygroundEconomyRules.Complete(world, actorId, time); }
        if (activity.Kind == PlaygroundActivityKind.Wait) {
            string result = $"等待完成，现在是 {time}。";
            return new(PlaygroundEventKind.WaitCompleted, time, actorId,
                changedCharacters: [actor.Change(null, result: result)], result: result);
        }
        return CompleteGather(world, actor, activity, time);
    }
    private static PlaygroundFact CompleteGather(PlaygroundWorld world, PlaygroundCharacter actor, PlaygroundActivity activity, ModelTime time) {
        PlaygroundResource previous = world.GetResource(activity.ResourceId!);
        string? failure = previous.Stock == 0 ? "本周期结算时资源已空" : actor.Backpack.CheckAdd(previous.ItemId) switch {
            InventoryAdmission.ItemQuantityLimit => "背包该物品数量已达上限",
            InventoryAdmission.CapacityLimit => "背包总件数已达容量上限",
            _ => null
        };
        if (failure is not null) {
            string result = $"{previous.Name} 采集结束：目标 {activity.TargetCount}，取得 {activity.CompletedCount}；{failure}。该次结算后余量 {previous.Stock}/{previous.Capacity}。";
            return new(PlaygroundEventKind.GatherFinished, time, actor.Id, previous.Id,
                changedCharacters: [actor.Change(null, result: result)], result: result,
                gatherStopReason: previous.Stock == 0 ? PlaygroundGatherStopReason.ResourceEmpty :
                    actor.Backpack.CheckAdd(previous.ItemId) == InventoryAdmission.ItemQuantityLimit ? PlaygroundGatherStopReason.ItemLimit : PlaygroundGatherStopReason.CapacityLimit);
        }
        int completed = checked(activity.CompletedCount + 1);
        PlaygroundResource resource = previous.Stock == previous.Capacity
            ? previous.Change(previous.Stock - 1, 0, previous.GrowsInDaylight == world.Light.IsDay ? time : null)
            : previous.Change(previous.Stock - 1, previous.AccumulatedTicks, previous.ActiveSince);
        ItemInventory backpack = actor.Backpack.Add(resource.ItemId);
        PlaygroundActivity? next = null;
        string finish = "已达到目标";
        if (completed < activity.TargetCount) {
            long duration = PlaygroundActions.HarvestDuration(resource, actor);
            if (time.Ticks <= long.MaxValue - duration) {
                next = new(PlaygroundActivityKind.Gather, activity.Id, time, duration, new(time.Ticks + duration),
                    resource.Id, activity.TargetCount, completed, activity.SelectedAll, checked(activity.CycleIndex + 1));
                finish = "继续下一周期";
            } else { finish = "无法继续安排时间，批次结束"; }
        }
        string resultText = $"取得 {resource.Name} 1 份，目标 {activity.TargetCount}，已取得 {completed}；{finish}。该次结算后余量 {resource.Stock}/{resource.Capacity}。";
        var changes = new Dictionary<string, PlaygroundCharacter>(StringComparer.Ordinal) {
            [actor.Id] = actor.Change(next, backpack, result: resultText)
        };
        if (resource.Stock == 0) {
            // Receivers are determined from post-transition activity state. A completed collector receives its result,
            // while a collector with another cycle also receives this perception without losing the new activity.
            var notice = new PlaygroundNotice(PlaygroundNoticeKind.ResourceDepleted, resource.Id, actor.Id,
                $"gather:{actor.Id}:{activity.Id}:{activity.CycleIndex}", time, $"{actor.Name} 取走了最后一份 {resource.Name}。");
            NotifyCollectors(world, changes, resource.Id, notice);
        }
        return new(PlaygroundEventKind.GatherYielded, time, actor.Id, resource.Id, changedResources: [resource],
            changedCharacters: OrderedChanges(world, changes), result: resultText,
            gatherStopReason: next is not null ? null : completed >= activity.TargetCount ? PlaygroundGatherStopReason.TargetReached : PlaygroundGatherStopReason.TimeLimit);
    }
    private static void NotifyCollectors(PlaygroundWorld world, Dictionary<string, PlaygroundCharacter> changes,
        string resourceId, PlaygroundNotice notice, string? exceptActorId = null) {
        foreach (PlaygroundCharacter previous in world.Characters) {
            PlaygroundCharacter current = changes.GetValueOrDefault(previous.Id, previous);
            if (current.Id != exceptActorId && current.Activity is { Kind: PlaygroundActivityKind.Gather } activity && activity.ResourceId == resourceId) {
                changes[current.Id] = current.Notify(notice);
            }
        }
    }
    private static IEnumerable<PlaygroundCharacter> OrderedChanges(PlaygroundWorld world, Dictionary<string, PlaygroundCharacter> changes) =>
        world.Characters.Where(c => changes.ContainsKey(c.Id)).Select(c => changes[c.Id]);
}
