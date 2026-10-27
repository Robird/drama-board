using DramaBoard.Kernel.Time;

namespace DramaBoard.ResourceCycle;

/// <summary>Business planning only. It emits concrete effects, including perceptions, before the E/S boundary.</summary>
internal static class CyclePlanning {
    public static CycleFact Occurrence(CycleWorld world, CyclePrediction prediction) => prediction.Kind switch {
        CyclePredictionKind.ChangeLight => ChangeLight(world, prediction.Due),
        CyclePredictionKind.RegenerateResource => Regenerate(world, prediction.TargetId, prediction.Due),
        CyclePredictionKind.DestroyResource => Destroy(world, prediction.TargetId, prediction.Due),
        CyclePredictionKind.CompleteActivity => CompleteActivity(world, prediction.TargetId, prediction.Due),
        _ => throw new InvalidOperationException("This occurrence requires a Player decision.")
    };

    public static CycleFact Decision(CycleWorld world, string actorId, CycleCommand command) {
        CycleCharacter actor = world.GetCharacter(actorId);
        switch (command.Kind) {
            case CycleCommandKind.Continue: {
                string result = "已确认感知，当前活动与截止时刻保持不变。";
                return new(CycleEventKind.AttentionAcknowledged, world.Time, actorId,
                    changedCharacters: [actor.Change(actor.Activity, notices: [], result: result)], lastServedActorId: actorId, result: result);
            }
            case CycleCommandKind.Cancel: {
                CycleActivity current = actor.Activity!;
                string result = current.Kind == CycleActivityKind.Gather
                    ? $"已取消采集，目标 {current.TargetCount} 份，已取得 {current.CompletedCount} 份；未完成的一份不结算。"
                    : "已取消等待。";
                return new(CycleEventKind.ActivityCancelled, world.Time, actorId,
                    changedCharacters: [actor.Change(null, notices: [], result: result)], lastServedActorId: actorId, result: result);
            }
            case CycleCommandKind.Wait: {
                long duration = command.Hours * CycleTime.Hour;
                var activity = new CycleActivity(CycleActivityKind.Wait, actor.NextActivityId, world.Time, duration,
                    new(checked(world.Time.Ticks + duration)));
                string result = $"开始等待 {command.Hours} 小时，预计 {activity.Due} 完成。";
                return new(CycleEventKind.WaitStarted, world.Time, actorId,
                    changedCharacters: [actor.Change(activity, notices: [], result: result, nextActivityId: checked(actor.NextActivityId + 1))],
                    lastServedActorId: actorId, result: result);
            }
            case CycleCommandKind.Gather:
                return StartGather(world, actor, command);
            default: throw new InvalidOperationException("Unknown command after validation.");
        }
    }

    private static CycleFact StartGather(CycleWorld world, CycleCharacter actor, CycleCommand command) {
        CycleResource resource = world.GetResource(command.ResourceId!);
        int target = command.All ? resource.Stock : command.Count;
        long duration = CycleActions.HarvestDuration(resource, actor);
        var activity = new CycleActivity(CycleActivityKind.Gather, actor.NextActivityId, world.Time, duration,
            new(checked(world.Time.Ticks + duration)), resource.Id, target, selectedAll: command.All);
        string result = $"开始采集 {resource.Name}，目标固定为 {target} 份，下一份于 {activity.Due} 结算；不预留库存。";
        var changes = new Dictionary<string, CycleCharacter>(StringComparer.Ordinal) {
            [actor.Id] = actor.Change(activity, notices: [], result: result, nextActivityId: checked(actor.NextActivityId + 1))
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
        if (activity.Kind == CycleActivityKind.Wait) {
            string result = $"等待完成，现在是 {time}。";
            return new(CycleEventKind.WaitCompleted, time, actorId,
                changedCharacters: [actor.Change(null, result: result)], result: result);
        }
        return CompleteGather(world, actor, activity, time);
    }
    private static CycleFact CompleteGather(CycleWorld world, CycleCharacter actor, CycleActivity activity, ModelTime time) {
        CycleResource previous = world.GetResource(activity.ResourceId!);
        string? failure = previous.Stock == 0 ? "本周期结算时资源已空" :
            !actor.Backpack.CanAdd(previous.ItemId) ? "背包该物品数量已达上限" : null;
        if (failure is not null) {
            string result = $"{previous.Name} 采集结束：目标 {activity.TargetCount}，取得 {activity.CompletedCount}；{failure}。该次结算后余量 {previous.Stock}/{previous.Capacity}。";
            return new(CycleEventKind.GatherFinished, time, actor.Id, previous.Id,
                changedCharacters: [actor.Change(null, result: result)], result: result);
        }
        int completed = checked(activity.CompletedCount + 1);
        CycleResource resource = previous.Stock == previous.Capacity
            ? previous.Change(previous.Stock - 1, 0, previous.GrowsInDaylight == world.Light.IsDay ? time : null)
            : previous.Change(previous.Stock - 1, previous.AccumulatedTicks, previous.ActiveSince);
        CycleBackpack backpack = actor.Backpack.Add(resource.ItemId);
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
            changedCharacters: OrderedChanges(world, changes), result: resultText);
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
