using Atelia.DurableGraph;
using DramaBoard.Inventory;
using DramaBoard.Spatial;

namespace DramaBoard.ResourceCycle;

[DurableType("DramaBoard.ResourceCycle.CommandKind", 1)]
public enum CycleCommandKind { Wait, Gather, Continue, Cancel, Go, Use, Handoff }

/// <summary>Intent also recorded in a frozen visible command catalogue; execution still requires validation.</summary>
[DurableType("DramaBoard.ResourceCycle.Command", 1)]
public sealed partial record CycleCommand(
    [field: DurableField(1)] CycleCommandKind Kind,
    [field: DurableField(2)] int Hours = 0,
    [field: DurableField(3)] string? ResourceId = null,
    [field: DurableField(4)] int Count = 0,
    [field: DurableField(5)] bool All = false,
    [field: DurableField(6)] string? PassageId = null,
    [field: DurableField(7)] string? ItemId = null,
    [field: DurableField(8)] string? HandoffText = null) : IDurableObject {
    public static CycleCommand Wait(int hours) => new(CycleCommandKind.Wait, Hours: hours);
    public static CycleCommand Gather(string resourceId, int count = 1) => new(CycleCommandKind.Gather, ResourceId: resourceId, Count: count);
    public static CycleCommand GatherAll(string resourceId) => new(CycleCommandKind.Gather, ResourceId: resourceId, All: true);
    public static CycleCommand Continue() => new(CycleCommandKind.Continue);
    public static CycleCommand Cancel() => new(CycleCommandKind.Cancel);
    public static CycleCommand Go(string passageId) => new(CycleCommandKind.Go, PassageId: passageId);
    public static CycleCommand Use(string itemId) => new(CycleCommandKind.Use, ItemId: itemId);
    public static CycleCommand Handoff(string? text) => new(CycleCommandKind.Handoff, HandoffText: text);
}

/// <summary>Captured controller identity; IDs are opaque and must survive execution and recovery unchanged.</summary>
public sealed record CycleControllerOperation(string ToolCallId, string RawInput, string OperationRecordId);

/// <summary>Captures raw submissions for the requested character; one instance may serve multiple characters.</summary>
/// <remarks>Cross-decision character memory belongs to explicit game state, not hidden controller history.
/// Each decision receives a character-scoped observation and a pure legality query.</remarks>
public interface ICycleCharacterController {
    /// <summary>Runs one decision interaction without committing world changes; the engine parses and validates the returned input.</summary>
    ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken);
}

public static class CycleActions {
    /// <summary>Scenario capability, independent of whether an actor has decision history.</summary>
    public static bool SupportsHandoff(CycleWorld world) => world.Expedition is not null;
    public static bool IsTravelling(CycleWorld world, string actorId) => IsTravelling(world.Business, actorId);
    internal static bool IsTravelling(CycleBusinessState business, string actorId) =>
        business.Expedition?.Spatial.Entities.SingleOrDefault(e => e.Id.Value == actorId)?.Location is TraversingLocation;
    public static string? CurrentPlace(CycleWorld world, string actorId) =>
        (world.Expedition?.Spatial.Entities.SingleOrDefault(e => e.Id.Value == actorId)?.Location as AtPlaceLocation)?.PlaceId.Value;
    public static bool NeedsDecision(CycleWorld world, string actorId) => NeedsDecision(world.Business, actorId);
    internal static bool NeedsDecision(CycleBusinessState business, string actorId) {
        CycleCharacter actor = business.GetCharacter(actorId);
        return (business.Expedition is null || business.Expedition.Outcome == CycleExpeditionOutcome.Exploring) &&
            (actor.Notices.Count > 0 || actor.Survival?.PendingMask > 0 || !IsTravelling(business, actorId) && actor.NeedsDecision);
    }

    public static long HarvestDuration(CycleResource resource, CycleCharacter actor) {
        long speed = checked(100L + 25L * actor.SkillLevel);
        // Int128 avoids rejecting a representable duration merely because the intermediate product exceeds Int64.
        Int128 numerator = (Int128)resource.HarvestTicks * 100;
        return Math.Max(1, checked((long)((numerator + speed - 1) / speed)));
    }
    /// <summary>Pure query; returns a displayable reason or null for an executable command.</summary>
    public static string? Check(CycleWorld world, string actorId, CycleCommand? command) => Check(world, actorId, command, null);
    internal static string? Check(CycleWorld world, string actorId, CycleCommand? command, long? contextSequence) {
        ArgumentNullException.ThrowIfNull(world);
        CycleCharacter? actor = world.Characters.SingleOrDefault(c => c.Id == actorId);
        if (actor is null) { return "当前世界没有这个角色。"; }
        if (command is null) { return "没有提供行动。"; }
        if (world.Expedition is { Outcome: not CycleExpeditionOutcome.Exploring }) { return "旅程已经结束。"; }
        if (!NeedsDecision(world, actorId)) { return "角色正在活动，当前没有需要响应的感知。"; }
        bool travelling = IsTravelling(world, actorId);
        if (travelling && command.Kind is not (CycleCommandKind.Continue or CycleCommandKind.Use or CycleCommandKind.Handoff)) { return "正在途中；本段移动不可取消，也不能同时开始其他耗时行动。"; }
        if (command.Kind != CycleCommandKind.Handoff && command.HandoffText is not null) { return "这个行动不接受交接文字。"; }
        if (command.Kind != CycleCommandKind.Go && command.PassageId is not null) { return "这个行动不接受通路参数。"; }
        if (command.Kind != CycleCommandKind.Use && command.ItemId is not null) { return "这个行动不接受物品参数。"; }
        if (actor.DecisionGeneration >= long.MaxValue - 2) { return "角色请求版本已达到支持上限。"; }
        if ((contextSequence ?? world.Business.InteractionStates[actorId].CurrentContext?.ContextSequence) == long.MaxValue &&
            (command.Kind is CycleCommandKind.Wait or CycleCommandKind.Gather or CycleCommandKind.Go ||
                command.Kind == CycleCommandKind.Continue && (actor.Activity is not null || travelling))) { return "决策回合编号已达到支持上限。"; }
        switch (command.Kind) {
            case CycleCommandKind.Handoff:
                if (!SupportsHandoff(world)) { return "此场景未启用交接文字。"; }
                if (command.Hours != 0 || command.ResourceId is not null || command.Count != 0 || command.All) { return "handoff 只接受交接文字。"; }
                return !CharacterInteractionState.ValidHandoff(command.HandoffText) ? "交接文字最多 2000 个 Unicode 字符。" : null;
            case CycleCommandKind.Continue:
                if (command.Hours != 0 || command.ResourceId is not null || command.Count != 0 || command.All) { return "continue 不接受参数。"; }
                return actor.Survival?.PendingMask > 0 || (actor.Activity is not null || travelling) && actor.Notices.Count > 0
                    ? null : "当前没有需要确认的活动感知或身体警告。";
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
                CycleResource? resource = world.Resources.SingleOrDefault(r => r.Id == command.ResourceId &&
                    (world.Expedition is null || CycleExpeditionMap.GetResourcePlace(r.Id) == CurrentPlace(world, actorId)));
                if (resource is null) { return "没有这个资源点。"; }
                if (actor.SkillLevel < resource.RequiredLevel) { return $"采药等级不足，需要 {resource.RequiredLevel} 级。"; }
                if (resource.Stock == 0) { return "资源点当前无货，不能新开采集。"; }
                switch (actor.Backpack.CheckAdd(resource.ItemId)) {
                    case InventoryAdmission.ItemQuantityLimit: return "背包中该物品的数量已达到上限。";
                    case InventoryAdmission.CapacityLimit: return "背包总件数已达到容量上限。";
                }
                if (world.Time.Ticks > long.MaxValue - HarvestDuration(resource, actor) || actor.NextActivityId == long.MaxValue) { return "采集结束时间或活动编号超出支持范围。"; }
                return null;
            case CycleCommandKind.Go:
                if (actor.Activity is not null) { return "已有耗时活动，请先 cancel。"; }
                if (command.Hours != 0 || command.ResourceId is not null || command.Count != 0 || command.All || string.IsNullOrWhiteSpace(command.PassageId)) { return "移动需要一个当前可见的通路编号。"; }
                string? place = CurrentPlace(world, actorId);
                if (place is null || !CycleExpeditionMap.GetPassages(place).Any(p => p.Id == command.PassageId)) { return "当前地点没有这条通路。"; }
                return CycleExpeditionMap.Planner.TryStartTraversal(world.Expedition!.Spatial, new(actorId), new(command.PassageId), 1, world.Time)
                    is SpatialPlanAccepted ? null : "当前不能进入这条通路。";
            case CycleCommandKind.Use:
                if (command.Hours != 0 || command.ResourceId is not null || command.PassageId is not null || command.Count != 0 || command.All ||
                    string.IsNullOrWhiteSpace(command.ItemId)) { return "使用补给需要一个物品编号。"; }
                if (actor.Survival is null || CycleSurvivalRules.UseDescription(command.ItemId) is null) { return "这个物品不能用于补给。"; }
                if (actor.Backpack.Count(command.ItemId) == 0) { return "背包中没有这个物品。"; }
                CycleSurvival projected = actor.Survival.Project(world.Time);
                if (projected.IsFatal) { return "身体已耗尽，无法通过此时补给挽回。"; }
                return (command.ItemId == "item.food" ? projected.FoodUnits : projected.WaterUnits) == CycleSurvival.MaximumUnits
                    ? "这项身体需求已经充足，使用补给不会产生收益。" : null;
            default: return "未知行动。";
        }
    }
}
