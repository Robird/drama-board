using Atelia.DurableGraph;
using DramaBoard.Goals;
using DramaBoard.Inventory;
using DramaBoard.Spatial;

namespace DramaBoard.Playground;

[DurableType("DramaBoard.Playground.CommandKind", 1)]
public enum PlaygroundCommandKind { Wait, Gather, Continue, Cancel, Go, Use, Handoff, Say, Give, Goal, Follow, Unfollow, DismissFollowers, Shout, Buy, Sell, Produce, GiveMoney, Offer, Accept, Reject, Withdraw }

/// <summary>Intent also recorded in a frozen visible command catalogue; execution still requires validation.</summary>
[DurableType("DramaBoard.Playground.Command", 2)]
public sealed partial record PlaygroundCommand(
    [field: DurableField(1)] PlaygroundCommandKind Kind,
    [field: DurableField(2)] int Hours = 0,
    [field: DurableField(3)] string? ResourceId = null,
    [field: DurableField(4)] int Count = 0,
    [field: DurableField(5)] bool All = false,
    [field: DurableField(6)] string? EntranceId = null,
    [field: DurableField(7)] string? ItemId = null,
    [field: DurableField(8)] string? HandoffText = null,
    [field: DurableField(9)] string? TargetActorId = null,
    [field: DurableField(10)] string? SpeechText = null,
    [field: DurableField(11)] GoalCommand? Goal = null,
    [field: DurableField(12)] string? RecipeId = null,
    [field: DurableField(13)] long MoneyAmount = 0,
    [field: DurableField(14)] PlaygroundTradeAsset? GiveAsset = null,
    [field: DurableField(15)] PlaygroundTradeAsset? WantAsset = null,
    [field: DurableField(16)] string? TradeId = null) : IDurableObject {
    public static PlaygroundCommand Offer(string targetActorId, PlaygroundTradeAsset give, PlaygroundTradeAsset want, int hours = 6) =>
        new(PlaygroundCommandKind.Offer, Hours: hours, TargetActorId: targetActorId, GiveAsset: give, WantAsset: want);
    public static PlaygroundCommand Accept(string tradeId) => new(PlaygroundCommandKind.Accept, TradeId: tradeId);
    public static PlaygroundCommand Reject(string tradeId) => new(PlaygroundCommandKind.Reject, TradeId: tradeId);
    public static PlaygroundCommand Withdraw(string tradeId) => new(PlaygroundCommandKind.Withdraw, TradeId: tradeId);
    public static PlaygroundCommand Buy(string itemId, int count = 1) => new(PlaygroundCommandKind.Buy, ItemId: itemId, Count: count);
    public static PlaygroundCommand Sell(string itemId, int count = 1) => new(PlaygroundCommandKind.Sell, ItemId: itemId, Count: count);
    public static PlaygroundCommand Produce(string recipeId, int batches = 1) => new(PlaygroundCommandKind.Produce, RecipeId: recipeId, Count: batches);
    public static PlaygroundCommand GiveMoney(string targetActorId, long amount) => new(PlaygroundCommandKind.GiveMoney, TargetActorId: targetActorId, MoneyAmount: amount);
    public static PlaygroundCommand ManageGoal(GoalCommand goal) => new(PlaygroundCommandKind.Goal, Goal: goal);
    public static PlaygroundCommand Shout(string text) => new(PlaygroundCommandKind.Shout, SpeechText: text);
    public static PlaygroundCommand Say(string targetActorId, string text) => new(PlaygroundCommandKind.Say, TargetActorId: targetActorId, SpeechText: text);
    public static PlaygroundCommand Give(string targetActorId, string itemId, int count) => new(PlaygroundCommandKind.Give, TargetActorId: targetActorId, ItemId: itemId, Count: count);
    public static PlaygroundCommand Wait(int hours) => new(PlaygroundCommandKind.Wait, Hours: hours);
    public static PlaygroundCommand Gather(string resourceId, int count = 1) => new(PlaygroundCommandKind.Gather, ResourceId: resourceId, Count: count);
    public static PlaygroundCommand GatherAll(string resourceId) => new(PlaygroundCommandKind.Gather, ResourceId: resourceId, All: true);
    public static PlaygroundCommand Continue() => new(PlaygroundCommandKind.Continue);
    public static PlaygroundCommand Cancel() => new(PlaygroundCommandKind.Cancel);
    public static PlaygroundCommand Go(string entranceId) => new(PlaygroundCommandKind.Go, EntranceId: entranceId);
    public static PlaygroundCommand Use(string itemId) => new(PlaygroundCommandKind.Use, ItemId: itemId);
    public static PlaygroundCommand Handoff(string? text) => new(PlaygroundCommandKind.Handoff, HandoffText: text);
    public static PlaygroundCommand Follow(string targetActorId) => new(PlaygroundCommandKind.Follow, TargetActorId: targetActorId);
    public static PlaygroundCommand Unfollow() => new(PlaygroundCommandKind.Unfollow);
    public static PlaygroundCommand DismissFollowers() => new(PlaygroundCommandKind.DismissFollowers);
}

/// <summary>Captured controller identity; IDs are opaque and must survive execution and recovery unchanged.</summary>
public sealed record PlaygroundControllerOperation(string ToolCallId, string RawInput, string OperationRecordId);

/// <summary>Captures raw submissions for the requested character; one instance may serve multiple characters.</summary>
/// <remarks>Cross-decision character memory belongs to explicit game state, not hidden controller history.
/// Each decision receives a character-scoped observation and a pure legality query.</remarks>
public interface IPlaygroundCharacterController {
    /// <summary>Runs one decision interaction without committing world changes; the engine parses and validates the returned input.</summary>
    ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken);
}

public static class PlaygroundActions {
    /// <summary>Scenario capability, independent of whether an actor has decision history.</summary>
    public static bool SupportsHandoff(PlaygroundWorld world) => world.Expedition is not null;
    public static bool SupportsGoals(PlaygroundWorld world) => world.Expedition is not null;
    public static bool SupportsFollowing(PlaygroundWorld world) => world.Expedition is not null;
    /// <summary>Active position or historical ending position. Never use this fallback for interaction eligibility.</summary>
    public static SpatialLocation? Location(PlaygroundWorld world, string actorId) => Location(world.Business, actorId);
    public static SpatialLocation? Location(PlaygroundBusinessState business, string actorId) =>
        business.Expedition?.Spatial.Entities.SingleOrDefault(e => e.Id.Value == actorId)?.Location ?? business.GetCharacter(actorId).Ending?.Location;
    public static bool IsTravelling(PlaygroundWorld world, string actorId) => IsTravelling(world.Business, actorId);
    internal static bool IsTravelling(PlaygroundBusinessState business, string actorId) =>
        business.Expedition?.Spatial.Entities.SingleOrDefault(e => e.Id.Value == actorId)?.Location is TraversingLocation;
    public static string? CurrentPlace(PlaygroundWorld world, string actorId) =>
        (world.Expedition?.Spatial.Entities.SingleOrDefault(e => e.Id.Value == actorId)?.Location as AtPlaceLocation)?.PlaceId.Value;
    public static bool NeedsDecision(PlaygroundWorld world, string actorId) => NeedsDecision(world.Business, actorId);
    internal static bool NeedsDecision(PlaygroundBusinessState business, string actorId) {
        PlaygroundCharacter actor = business.GetCharacter(actorId);
        return actor.IsActive &&
            (actor.Communication.AttentionPending || actor.Notices.Count > 0 || actor.Survival?.PendingMask > 0 ||
                !IsTravelling(business, actorId) && actor.Activity is null && actor.FollowingLeaderId is null);
    }

    public static long HarvestDuration(PlaygroundResource resource, PlaygroundCharacter actor) {
        long speed = checked(100L + 25L * actor.SkillLevel);
        // Int128 avoids rejecting a representable duration merely because the intermediate product exceeds Int64.
        Int128 numerator = (Int128)resource.HarvestTicks * 100;
        return Math.Max(1, checked((long)((numerator + speed - 1) / speed)));
    }
    /// <summary>Pure query; returns a displayable reason or null for an executable command.</summary>
    public static string? Check(PlaygroundWorld world, string actorId, PlaygroundCommand? command) => Check(world, actorId, command, null);
    internal static string? Check(PlaygroundWorld world, string actorId, PlaygroundCommand? command, long? contextSequence, bool visibleOnly = false) {
        ArgumentNullException.ThrowIfNull(world);
        PlaygroundCharacter? actor = world.Characters.SingleOrDefault(c => c.Id == actorId);
        if (actor is null) { return "当前世界没有这个角色。"; }
        if (command is null) { return "没有提供行动。"; }
        if (!actor.IsActive) { return "旅程已经结束。"; }
        if (!NeedsDecision(world, actorId)) { return "角色正在活动，当前没有需要响应的感知。"; }
        bool travelling = IsTravelling(world, actorId);
        if (travelling && command.Kind is not (PlaygroundCommandKind.Continue or PlaygroundCommandKind.Use or PlaygroundCommandKind.Handoff or PlaygroundCommandKind.Goal)) { return "正在途中；本段移动不可取消，也不能同时开始其他耗时行动。"; }
        if (command.Kind != PlaygroundCommandKind.Goal && command.Goal is not null) { return "这个行动不接受目标操作。"; }
        if (command.Kind != PlaygroundCommandKind.Handoff && command.HandoffText is not null) { return "这个行动不接受交接文字。"; }
        if (command.Kind != PlaygroundCommandKind.Go && command.EntranceId is not null) { return "这个行动不接受通路参数。"; }
        if (command.Kind is not (PlaygroundCommandKind.Use or PlaygroundCommandKind.Give or PlaygroundCommandKind.Buy or PlaygroundCommandKind.Sell) && command.ItemId is not null) { return "这个行动不接受物品参数。"; }
        if (command.Kind is not (PlaygroundCommandKind.Say or PlaygroundCommandKind.Give or PlaygroundCommandKind.GiveMoney or PlaygroundCommandKind.Follow or PlaygroundCommandKind.Offer) && command.TargetActorId is not null) { return "这个行动不接受角色参数。"; }
        if (command.Kind != PlaygroundCommandKind.Produce && command.RecipeId is not null || command.Kind != PlaygroundCommandKind.GiveMoney && command.MoneyAmount != 0) { return "这个行动不接受生产或资金参数。"; }
        if (command.Kind is not (PlaygroundCommandKind.Say or PlaygroundCommandKind.Shout) && command.SpeechText is not null) { return "这个行动不接受话语参数。"; }
        if (actor.DecisionGeneration >= long.MaxValue - 2) { return "角色请求版本已达到支持上限。"; }
        if ((contextSequence ?? world.Business.InteractionStates[actorId].CurrentContext?.ContextSequence) == long.MaxValue &&
            (command.Kind is PlaygroundCommandKind.Wait or PlaygroundCommandKind.Gather or PlaygroundCommandKind.Go or PlaygroundCommandKind.Follow or PlaygroundCommandKind.Produce ||
                command.Kind == PlaygroundCommandKind.Continue && (actor.Activity is not null || travelling || actor.FollowingLeaderId is not null))) { return "决策回合编号已达到支持上限。"; }
        if (actor.FollowingLeaderId is not null && command.Kind is PlaygroundCommandKind.Go or PlaygroundCommandKind.Gather or PlaygroundCommandKind.Wait or PlaygroundCommandKind.Produce) {
            return "正在跟随领队，请先 unfollow 再开始独立耗时行动。";
        }
        if (!PlaygroundTradeRules.IsTradeCommand(command) && (command.GiveAsset is not null || command.WantAsset is not null || command.TradeId is not null)) { return "这个行动不接受报价参数。"; }
        switch (command.Kind) {
            case PlaygroundCommandKind.Offer or PlaygroundCommandKind.Accept or PlaygroundCommandKind.Reject or PlaygroundCommandKind.Withdraw:
                return visibleOnly ? PlaygroundTradeRules.CheckVisible(world, actorId, command) : PlaygroundTradeRules.Check(world, actorId, command);
            case PlaygroundCommandKind.Buy or PlaygroundCommandKind.Sell or PlaygroundCommandKind.Produce or PlaygroundCommandKind.GiveMoney:
                return PlaygroundEconomyRules.Check(world, actorId, command);
            case PlaygroundCommandKind.Follow or PlaygroundCommandKind.Unfollow or PlaygroundCommandKind.DismissFollowers:
                if (!SupportsFollowing(world)) { return "此场景未启用跟随。"; }
                if (command.Hours != 0 || command.ResourceId is not null || command.Count != 0 || command.All) { return "跟随操作只接受声明的角色参数。"; }
                return PlaygroundFollowingRules.Check(world, actor, command);
            case PlaygroundCommandKind.Goal:
                if (!SupportsGoals(world)) { return "此场景未启用目标管理。"; }
                if (command.Goal is null || command.Hours != 0 || command.ResourceId is not null || command.Count != 0 || command.All) {
                    return "目标操作只接受声明的目标参数。";
                }
                CharacterInteractionState interaction = world.Business.InteractionStates[actorId];
                long sequence = interaction.CurrentContext?.LastOperationSequence ?? 0;
                if (sequence == long.MaxValue) { return "角色操作编号已达到支持上限。"; }
                return GoalRules.Apply(interaction.Goals, command.Goal,
                    contextSequence ?? interaction.CurrentContext?.ContextSequence ?? 1, sequence + 1).Error;
            case PlaygroundCommandKind.Shout:
                if (command.Hours != 0 || command.ResourceId is not null || command.Count != 0 || command.All) { return "大喊只接受话语文本。"; }
                return PlaygroundCommunicationRules.Check(world, actor, command);
            case PlaygroundCommandKind.Say:
                if (command.Hours != 0 || command.ResourceId is not null || command.Count != 0 || command.All || string.IsNullOrWhiteSpace(command.TargetActorId)) { return "说话需要角色编号与文本。"; }
                return PlaygroundCommunicationRules.Check(world, actor, command);
            case PlaygroundCommandKind.Give:
                if (command.Hours != 0 || command.ResourceId is not null || command.All || command.Count <= 0 || string.IsNullOrWhiteSpace(command.TargetActorId) || string.IsNullOrWhiteSpace(command.ItemId)) { return "赠与需要角色编号、物品编号与正整数数量。"; }
                return PlaygroundCommunicationRules.Check(world, actor, command);
            case PlaygroundCommandKind.Handoff:
                if (!SupportsHandoff(world)) { return "此场景未启用交接文字。"; }
                if (command.Hours != 0 || command.ResourceId is not null || command.Count != 0 || command.All) { return "handoff 只接受交接文字。"; }
                return !CharacterInteractionState.ValidHandoff(command.HandoffText) ? "交接文字最多 2000 个 Unicode 字符。" : null;
            case PlaygroundCommandKind.Continue:
                if (command.Hours != 0 || command.ResourceId is not null || command.Count != 0 || command.All) { return "continue 不接受参数。"; }
                return actor.Communication.AttentionPending || actor.Survival?.PendingMask > 0 || (actor.Activity is not null || travelling || actor.FollowingLeaderId is not null) && actor.Notices.Count > 0
                    ? null : "当前没有需要确认的交流、活动感知或身体警告。";
            case PlaygroundCommandKind.Cancel:
                if (command.Hours != 0 || command.ResourceId is not null || command.Count != 0 || command.All) { return "cancel 不接受参数。"; }
                return actor.Activity is null ? "当前没有可取消的活动。" : null;
            case PlaygroundCommandKind.Wait:
                if (actor.Activity is not null) { return "已有耗时活动，请先 cancel。"; }
                if (command.Hours is < 1 or > 24 || command.ResourceId is not null || command.Count != 0 || command.All) { return "等待需要 1 到 24 的整数小时数。"; }
                if (world.Time.Ticks > long.MaxValue - command.Hours * PlaygroundTime.Hour || actor.NextActivityId == long.MaxValue) { return "等待结束时间或活动编号超出支持范围。"; }
                return null;
            case PlaygroundCommandKind.Gather:
                if (actor.Activity is not null) { return "已有耗时活动，请先 cancel。"; }
                if (command.Hours != 0 || (command.All ? command.Count != 0 : command.Count <= 0)) { return "采集数量需要正整数或 all。"; }
                PlaygroundResource? resource = world.Resources.SingleOrDefault(r => r.Id == command.ResourceId &&
                    (world.Expedition is null || world.Definition.GetResourcePlace(r.Id) == CurrentPlace(world, actorId)));
                if (resource is null) { return "没有这个资源点。"; }
                if (actor.SkillLevel < resource.RequiredLevel) { return $"采药等级不足，需要 {resource.RequiredLevel} 级。"; }
                if (resource.Stock == 0) { return "资源点当前无货，不能新开采集。"; }
                switch (actor.Backpack.CheckAdd(resource.ItemId)) {
                    case InventoryAdmission.ItemQuantityLimit: return "背包中该物品的数量已达到上限。";
                    case InventoryAdmission.CapacityLimit: return "背包总件数已达到容量上限。";
                }
                if (world.Time.Ticks > long.MaxValue - HarvestDuration(resource, actor) || actor.NextActivityId == long.MaxValue) { return "采集结束时间或活动编号超出支持范围。"; }
                return null;
            case PlaygroundCommandKind.Go:
                if (actor.Activity is not null) { return "已有耗时活动，请先 cancel。"; }
                if (command.Hours != 0 || command.ResourceId is not null || command.Count != 0 || command.All || string.IsNullOrWhiteSpace(command.EntranceId)) { return "移动需要一个当前可见的入口编号。"; }
                string? place = CurrentPlace(world, actorId);
                if (place is null || !world.Definition.GetEntrances(place).Any(p => p.EntranceId == command.EntranceId)) { return "当前没有可用的这个入口；请使用当前入口列表中的编号。"; }
                foreach (PlaygroundCharacter member in PlaygroundFollowingRules.MovingMembers(world.Business, actorId)) {
                    if (!member.IsActive || member.Activity is not null || CurrentPlace(world, member.Id) != place || member.DecisionGeneration >= long.MaxValue - 2 ||
                        world.Definition.Runtime.Planner.TryStartTraversal(world.Expedition!.Spatial, new(member.Id), new(world.Definition.GetEntrances(place!).Single(p => p.EntranceId == command.EntranceId).Id), 1, world.Time) is not SpatialPlanAccepted) {
                        return $"同行成员 {member.Name} 当前无法出发；所有人保持原状。";
                    }
                }
                return null;
            case PlaygroundCommandKind.Use:
                if (command.Hours != 0 || command.ResourceId is not null || command.EntranceId is not null || command.Count != 0 || command.All ||
                    string.IsNullOrWhiteSpace(command.ItemId)) { return "使用补给需要一个物品编号。"; }
                if (actor.Survival is null || PlaygroundSurvivalRules.UseDescription(world.Definition, command.ItemId) is null) { return "这个物品不能用于补给。"; }
                if (actor.Backpack.Count(command.ItemId) == 0) { return "背包中没有这个物品。"; }
                PlaygroundSurvival projected = actor.Survival.Project(world.Time);
                if (projected.IsFatal) { return "身体已耗尽，无法通过此时补给挽回。"; }
                return (world.Definition.SupplyFood(command.ItemId) == 0 || projected.FoodUnits == PlaygroundSurvival.MaximumUnits) &&
                    (world.Definition.SupplyWater(command.ItemId) == 0 || projected.WaterUnits == PlaygroundSurvival.MaximumUnits)
                    ? "这项身体需求已经充足，使用补给不会产生收益。" : null;
            default: return "未知行动。";
        }
    }
}
