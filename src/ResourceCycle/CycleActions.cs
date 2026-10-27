namespace DramaBoard.ResourceCycle;

public enum CycleCommandKind { Wait, Gather, Continue, Cancel }

/// <summary>Runtime intent; only validated concrete effects enter durable facts.</summary>
public sealed record CycleCommand(CycleCommandKind Kind, int Hours = 0, string? ResourceId = null, int Count = 0, bool All = false) {
    public static CycleCommand Wait(int hours) => new(CycleCommandKind.Wait, Hours: hours);
    public static CycleCommand Gather(string resourceId, int count = 1) => new(CycleCommandKind.Gather, ResourceId: resourceId, Count: count);
    public static CycleCommand GatherAll(string resourceId) => new(CycleCommandKind.Gather, ResourceId: resourceId, All: true);
    public static CycleCommand Continue() => new(CycleCommandKind.Continue);
    public static CycleCommand Cancel() => new(CycleCommandKind.Cancel);
}

/// <summary>Provides commands for the requested character; one instance may serve multiple characters.</summary>
/// <remarks>Cross-decision character memory belongs to explicit game state, not hidden controller history.
/// The current world argument is a trusted application boundary, not a character-scoped information view.</remarks>
public interface ICycleCharacterController {
    /// <summary>Runs one decision interaction without committing world changes; the engine validates the returned intent.</summary>
    ValueTask<CycleCommand> ChooseCommandAsync(CycleWorld world, string actorId, CancellationToken cancellationToken);
}

public static class CycleActions {
    public static long HarvestDuration(CycleResource resource, CycleCharacter actor) {
        long speed = checked(100L + 25L * actor.SkillLevel);
        // Int128 avoids rejecting a representable duration merely because the intermediate product exceeds Int64.
        Int128 numerator = (Int128)resource.HarvestTicks * 100;
        return Math.Max(1, checked((long)((numerator + speed - 1) / speed)));
    }
    /// <summary>Pure query; returns a displayable reason or null for an executable command.</summary>
    public static string? Check(CycleWorld world, string actorId, CycleCommand? command) {
        ArgumentNullException.ThrowIfNull(world);
        CycleCharacter? actor = world.Characters.SingleOrDefault(c => c.Id == actorId);
        if (actor is null) { return "当前世界没有这个角色。"; }
        if (command is null) { return "没有提供行动。"; }
        if (!actor.NeedsDecision) { return "角色正在活动，当前没有需要响应的感知。"; }
        if (actor.DecisionGeneration >= long.MaxValue - 2) { return "角色请求版本已达到支持上限。"; }
        switch (command.Kind) {
            case CycleCommandKind.Continue:
                if (command.Hours != 0 || command.ResourceId is not null || command.Count != 0 || command.All) { return "continue 不接受参数。"; }
                return actor.Activity is not null && actor.Notices.Count > 0 ? null : "只有正在活动且收到待确认感知时才能 continue。";
            case CycleCommandKind.Cancel:
                if (command.Hours != 0 || command.ResourceId is not null || command.Count != 0 || command.All) { return "cancel 不接受参数。"; }
                return actor.Activity is null ? "当前没有可取消的活动。" : null;
            case CycleCommandKind.Wait:
                if (actor.Activity is not null) { return "已有耗时活动，请先 cancel。"; }
                if (command.Hours is < 1 or > 24 || command.ResourceId is not null || command.Count != 0 || command.All) { return "等待需要 1 到 24 的整数小时数。"; }
                if (world.Time.Ticks > long.MaxValue - command.Hours * CycleTime.Hour || actor.NextActivityId == long.MaxValue) { return "等待结束时间或活动编号超出支持范围。"; }
                return null;
            case CycleCommandKind.Gather:
                if (actor.Activity is not null) { return "已有耗时活动，请先 cancel。"; }
                if (command.Hours != 0 || (command.All ? command.Count != 0 : command.Count <= 0)) { return "采集数量需要正整数或 all。"; }
                CycleResource? resource = world.Resources.SingleOrDefault(r => r.Id == command.ResourceId);
                if (resource is null) { return "没有这个资源点。"; }
                if (actor.SkillLevel < resource.RequiredLevel) { return $"采药等级不足，需要 {resource.RequiredLevel} 级。"; }
                if (resource.Stock == 0) { return "资源点当前无货，不能新开采集。"; }
                if (!actor.Backpack.CanAdd(resource.ItemId)) { return "背包中该物品的数量已达到上限。"; }
                if (world.Time.Ticks > long.MaxValue - HarvestDuration(resource, actor) || actor.NextActivityId == long.MaxValue) { return "采集结束时间或活动编号超出支持范围。"; }
                return null;
            default: return "未知行动。";
        }
    }
}
