using System.Collections.ObjectModel;
using System.Globalization;
using DramaBoard.Kernel.Time;
using DramaBoard.ResourceCycle;

/// <summary>A transient, actor-scoped semantic index. Missing values are resolved from this observation only.</summary>
internal sealed class CycleViewSnapshot {
    private readonly Dictionary<CycleStateKey, CycleVisibleValue> _values = [];
    private readonly Dictionary<CycleStateKey, string> _labels = [];
    private readonly Dictionary<CycleStateKey, CycleAttributeView> _rateExplanations = [];
    public CycleObservation Observation { get; }
    public CharacterInteractionState? InteractionState { get; }
    public CharacterContext? Context { get; }
    public CycleBackground Background { get; }
    public string? DraftHandoff { get; }
    public bool IncludeHistory { get; }
    public bool IncludeGoals { get; }
    public IReadOnlyDictionary<CycleStateKey, CycleVisibleValue> Values { get; }
    public IReadOnlyDictionary<CycleStateKey, CycleAttributeView> RateExplanations { get; }

    private CycleViewSnapshot(CycleObservation observation, CharacterInteractionState? interactionState,
        CharacterContext? context = null, string? draftHandoff = null, bool includeHistory = true, bool includeGoals = true) {
        Observation = observation;
        InteractionState = interactionState;
        Context = context ?? interactionState?.CurrentContext;
        Background = Context?.Opening.Background ?? CycleBackground.For(observation);
        DraftHandoff = context is not null ? draftHandoff : interactionState?.DraftHandoff;
        IncludeHistory = includeHistory;
        IncludeGoals = includeGoals;
        Values = new ReadOnlyDictionary<CycleStateKey, CycleVisibleValue>(_values);
        RateExplanations = new ReadOnlyDictionary<CycleStateKey, CycleAttributeView>(_rateExplanations);
        Project();
    }

    public static CycleViewSnapshot Create(CycleObservation observation, CharacterInteractionState? interactionState = null) => new(observation, interactionState);

    public static CycleViewSnapshot CreateRecorded(CycleObservation observation, CharacterContext context,
        string? draftHandoff, bool includeHistory = true, bool includeGoals = true) => new(observation, null, context, draftHandoff, includeHistory, includeGoals);

    public string? KnownLabel(CycleStateKey key) => _values.ContainsKey(key) ? Label(key) : null;

    public CycleVisibleValue Resolve(CycleStateKey key) {
        if (_values.TryGetValue(key, out var value)) { return value; }
        return key.Category switch {
            "following" when key.Field.StartsWith("follower:", StringComparison.Ordinal) => CycleVisibleValue.Flag(false),
            "inventory" when key.Field == "count" => CycleVisibleValue.Numeric(Observation.Actor.Backpack.Count(key.ObjectId), "份"),
            "command" when key.Field == "warning" && _values.ContainsKey(new("command", key.ObjectId, "rejection")) => CycleVisibleValue.KnownAbsent,
            "resource" or "passage" or "other" or "command" or "market" or "recipe" or "trade" => CycleVisibleValue.Unobserved,
            _ => CycleVisibleValue.KnownAbsent
        };
    }

    public string Label(CycleStateKey key) => _labels.GetValueOrDefault(key) ?? key.Category switch {
        "following" when key.Field == "leader" => "跟随领队",
        "following" when key.Field.StartsWith("follower:", StringComparison.Ordinal) => "直属队员 " + key.Field[9..],
        "inventory" => $"{Observation.ItemNames.GetValueOrDefault(key.ObjectId, key.ObjectId)}库存",
        "resource" => $"{key.ObjectId} · {FieldLabel(key.Field)}",
        "money" => "自己的资金余额",
        "market" => $"市场 {Observation.ItemNames.GetValueOrDefault(key.ObjectId, key.ObjectId)} · {FieldLabel(key.Field)}",
        "recipe" => $"配方 {key.ObjectId} · {FieldLabel(key.Field)}",
        "trade" => $"报价 {key.ObjectId} · {FieldLabel(key.Field)}",
        "command" => $"{key.ObjectId} · {FieldLabel(key.Field)}",
        "notice-detail" => $"历史通知 · {NoticeFieldLabel(key.Field)}",
        "passage" => $"通路 {key.ObjectId} · {FieldLabel(key.Field)}",
        "memory" or "memory-resource" => $"历史观察 {key.ObjectId} · {FieldLabel(key.Field)}",
        var category when category.StartsWith("incoming", StringComparison.Ordinal) => $"上次推进历史 · {FieldLabel(key.Field)}",
        _ => FieldLabel(key.Field)
    };

    private void Add(string category, string id, string field, CycleVisibleValue value, string? label = null) {
        var key = new CycleStateKey(category, id, field);
        _values.Add(key, value);
        if (label is not null) { _labels.Add(key, label); }
    }
    private static CycleVisibleValue Text(string? value) => value is null ? CycleVisibleValue.KnownAbsent : CycleVisibleValue.Literal(value);
    private static CycleVisibleValue At(ModelTime? value) => value is { } at ? CycleVisibleValue.Numeric(at.Ticks, "ticks") : CycleVisibleValue.KnownAbsent;
    private static CycleVisibleValue Number(decimal value, string unit = "点") => CycleVisibleValue.Numeric(checked((long)decimal.Round(value * CycleSurvival.Scale)), unit, CycleSurvival.Scale);

    private void Project() {
        var o = Observation;
        string actor = o.Actor.Id;
        Add("world", "world", "time", At(o.Time));
        Add("world", "world", "light", CycleVisibleValue.Flag(o.IsDay));
        Add("world", "world", "next-light", At(o.NextLightChange));
        Add("world", "world", "outcome", Text(o.Outcome.ToString()));
        Add("world", "world", "ending", Text(o.EndingReason));
        Add("location", actor, "place", Text(o.PlaceId));
        Add("location", actor, "name", Text(o.PlaceName), "位置");
        Add("location", actor, "description", Text(o.PlaceDescription), "眼前环境");
        Add("location", actor, "arrival", At(o.ArrivalDue));
        Add("location", actor, "journey", Text(o.Journey is { } journey ? JourneyView.Route(journey) : null), "行程");
        Add("actor", actor, "skill", CycleVisibleValue.Numeric(o.Actor.SkillLevel), "采药等级");
        if (o.IsExpedition) { Add("money", actor, "balance", CycleVisibleValue.Numeric(o.Actor.Money, "钱"), "自己的资金余额"); }
        if (o.MarketRefreshAt is { } refresh) { Add("market", "market", "refresh", At(refresh), "市场下次补充"); }
        foreach (CycleMarketQuote quote in o.MarketQuotes) {
            string name = o.ItemNames.GetValueOrDefault(quote.ItemId, quote.ItemId);
            Add("market", quote.ItemId, "buy-price", quote.BuyPrice > 0 ? CycleVisibleValue.Numeric(quote.BuyPrice, "钱") : Text("不出售"), name + "买入单价");
            Add("market", quote.ItemId, "sell-price", quote.SellPrice > 0 ? CycleVisibleValue.Numeric(quote.SellPrice, "钱") : Text("不收购"), name + "卖出单价");
            Add("market", quote.ItemId, "stock", CycleVisibleValue.Numeric(quote.Stock, "份"), name + "市场可买库存");
            Add("market", quote.ItemId, "demand", CycleVisibleValue.Numeric(quote.Demand, "份"), name + "剩余收购额度");
        }
        foreach (CycleRecipe recipe in o.Recipes) {
            Add("recipe", recipe.Id, "input", Text(recipe.InputItem), recipe.Id + "每批投入物品");
            Add("recipe", recipe.Id, "input-count", CycleVisibleValue.Numeric(recipe.InputCount, "份"), recipe.Id + "每批投入数量");
            Add("recipe", recipe.Id, "output", Text(recipe.OutputItem), recipe.Id + "每批产物");
            Add("recipe", recipe.Id, "output-count", CycleVisibleValue.Numeric(recipe.OutputCount, "份"), recipe.Id + "每批产出数量");
            Add("recipe", recipe.Id, "duration", CycleVisibleValue.Numeric(recipe.DurationTicks, "duration"), recipe.Id + "每批生产耗时");
        }
        foreach (CycleTradeOffer offer in o.Trades) {
            Add("trade", offer.Id, "proposer", Text(offer.ProposerId), "报价 " + offer.Id + "发起方");
            Add("trade", offer.Id, "recipient", Text(offer.RecipientId), "报价 " + offer.Id + "收件人");
            Add("trade", offer.Id, "give", Text(TradeView.Asset(offer.Give, o)), "报价 " + offer.Id + "发起方给予");
            Add("trade", offer.Id, "want", Text(TradeView.Asset(offer.Want, o)), "报价 " + offer.Id + "发起方索取");
            Add("trade", offer.Id, "status", Text(TradeView.Status(offer.Status)), "报价 " + offer.Id + "状态");
            Add("trade", offer.Id, "created", At(offer.CreatedAt), "报价 " + offer.Id + "发起时间");
            Add("trade", offer.Id, "expires", At(offer.ExpiresAt), "报价 " + offer.Id + "到期时间");
            Add("trade", offer.Id, "closed", At(offer.ClosedAt), "报价 " + offer.Id + "结束时间");
        }
        Add("backpack", actor, "total", CycleVisibleValue.Numeric(o.Actor.Backpack.TotalCount, "件"), "背包总量");
        Add("backpack", actor, "capacity", o.Actor.Backpack.UnitCapacity is { } cap ? CycleVisibleValue.Numeric(cap, "件") : Text("不限"), "背包容量");
        foreach (var item in o.Actor.Backpack.Items) {
            Add("inventory", item.Key, "count", CycleVisibleValue.Numeric(item.Value, "份"), $"{o.ItemNames.GetValueOrDefault(item.Key, item.Key)}库存");
        }
        if (o.Survival is { } s) {
            foreach (var attribute in CycleAttributeView.FromSurvival(s, o.Outcome == CycleExpeditionOutcome.Exploring)) {
                _rateExplanations.Add(new("body", actor, attribute.Field + "-rate"), attribute);
            }
            Add("body", actor, "food", Number(s.Food));
            Add("body", actor, "water", Number(s.Water));
            Add("body", actor, "warmth", Number(s.Warmth));
            Add("body", actor, "food-rate", CycleVisibleValue.Numeric(s.FoodPerHour, "点/小时"));
            Add("body", actor, "water-rate", CycleVisibleValue.Numeric(s.WaterPerHour, "点/小时"));
            Add("body", actor, "warmth-rate", CycleVisibleValue.Numeric(s.WarmthPerHour, "点/小时"));
            Add("body", actor, "sheltered", CycleVisibleValue.Flag(s.Sheltered));
            Add("body", actor, "warning-food", CycleVisibleValue.Flag((s.PendingWarningMask & 1) != 0));
            Add("body", actor, "warning-water", CycleVisibleValue.Flag((s.PendingWarningMask & 2) != 0));
            Add("body", actor, "warning-warmth", CycleVisibleValue.Flag((s.PendingWarningMask & 4) != 0));
            Add("body", actor, "warnings", s.PendingWarnings.Count == 0 ? CycleVisibleValue.KnownAbsent : Text(string.Join("；", s.PendingWarnings)), "待确认历史警告");
        }
        if (o.IsExpedition) {
            Add("following", actor, "leader", Text(o.FollowingLeaderId), "跟随领队");
            foreach (var follower in o.Followers) {
                Add("following", actor, "follower:" + follower.Key, CycleVisibleValue.Flag(true), $"直属队员 {follower.Value}（{follower.Key}）");
            }
        }
        var activity = o.Actor.Activity;
        Add("activity", actor, "kind", Text(activity?.Kind.ToString()));
        Add("activity", actor, "due", At(activity?.Due));
        Add("activity", actor, "completed", activity?.Kind != CycleActivityKind.Gather ? CycleVisibleValue.KnownAbsent : CycleVisibleValue.Numeric(activity.CompletedCount, "份"));
        Add("activity", actor, "target", activity?.Kind != CycleActivityKind.Gather ? CycleVisibleValue.KnownAbsent : CycleVisibleValue.Numeric(activity.TargetCount, "份"));
        Add("activity", actor, "resource", Text(activity?.ResourceId));
        Add("activity", actor, "started", At(activity?.StartedAt));
        if (activity?.Production is { } production) {
            Add("activity", actor, "recipe", Text(production.RecipeId), "生产配方");
            Add("activity", actor, "batches", CycleVisibleValue.Numeric(production.Batches, "批"), "生产批数");
            Add("activity", actor, "input", Text(production.InputItem), "生产预留物品");
            Add("activity", actor, "input-count", CycleVisibleValue.Numeric(production.InputCount, "份"), "生产预留总量");
            Add("activity", actor, "output", Text(production.OutputItem), "生产目标物品");
            Add("activity", actor, "output-count", CycleVisibleValue.Numeric(production.OutputCount, "份"), "生产目标总量");
        }
        Add("activity", actor, "selected-all", activity is null ? CycleVisibleValue.KnownAbsent : CycleVisibleValue.Flag(activity.SelectedAll));
        Add("notice", actor, "count", CycleVisibleValue.Numeric(o.Notices.Count, "条"));
        if (o.Communication is { } communication) {
            Add("communication", actor, "remaining", CycleVisibleValue.Numeric(communication.RemainingSends, "次"), "本时刻剩余通信发送额度");
            Add("communication", actor, "pending", CycleVisibleValue.Flag(communication.AttentionPending), "通信待响应");
        }
        foreach (var n in o.Notices) {
            string id = string.Join('|', n.Notice.SourceId, n.Notice.Kind, n.Notice.ResourceId, n.Notice.ActorId ?? "");
            Add("notice-detail", id, "kind", Text(n.Notice.Kind.ToString()), "历史通知类别");
            Add("notice-detail", id, "at", At(n.Notice.At), "历史通知发生时间");
            Add("notice-detail", id, "reason", Text(n.Notice.Reason), $"{n.ResourceName}通知");
            Add("notice-detail", id, "actor", Text(n.ActorLabel), "通知来源");
            Add("notice-detail", id, "still-gathering", n.StillGathering is { } gathering ? CycleVisibleValue.Flag(gathering) : CycleVisibleValue.Unobserved, "通知所指角色仍在采集");
            Add("notice-detail", id, "stock", n.CurrentStock is { } stock ? CycleVisibleValue.Numeric(stock, "份") : CycleVisibleValue.Unobserved, n.ResourceName + "当前余量");
            Add("notice-detail", id, "capacity", n.Capacity is { } capacity ? CycleVisibleValue.Numeric(capacity, "份") : CycleVisibleValue.Unobserved, n.ResourceName + "容量");
            Add("notice-detail", id, "count", CycleVisibleValue.Numeric(n.Notice.Count, "次"), "同类通知累计次数");
        }
        foreach (var r in o.Resources) {
            string prefix = r.Name + " · ";
            Add("resource", r.Id, "stock", CycleVisibleValue.Numeric(r.Stock, "份"), prefix + "余量");
            Add("resource", r.Id, "capacity", CycleVisibleValue.Numeric(r.Capacity, "份"), prefix + "容量");
            Add("resource", r.Id, "item", Text(r.ItemId), prefix + "产物");
            Add("resource", r.Id, "harvest", CycleVisibleValue.Numeric(o.HarvestDurations[r.Id], "duration"), prefix + "每份耗时");
            Add("resource", r.Id, "regeneration", CycleVisibleValue.Numeric(r.RegenerationTicks, "duration"), prefix + "再生周期");
            Add("resource", r.Id, "progress", CycleVisibleValue.Numeric(r.ProgressAt(o.Time), "duration"), prefix + "再生进度");
            Add("resource", r.Id, "active", CycleVisibleValue.Flag(r.ActiveSince is not null), prefix + "正在再生");
            Add("resource", r.Id, "day-growth", CycleVisibleValue.Flag(r.GrowsInDaylight), prefix + "白天生长");
            Add("resource", r.Id, "required-level", CycleVisibleValue.Numeric(r.RequiredLevel), prefix + "所需等级");
        }
        foreach (var p in o.Passages) {
            Add("passage", p.Id, "description", Text(p.Description), "通路 " + p.Id + " · 线索");
            Add("passage", p.Id, "duration", CycleVisibleValue.Numeric(p.DurationTicks, "duration"), "通路 " + p.Id + " · 耗时");
            Add("passage", p.Id, "destination", Text(p.KnownDestinationName), "通路 " + p.Id + " · 已知目的地");
        }
        foreach (var c in o.Commands) {
            string id = CommandId(c.Command);
            Add("command", id, "rejection", Text(c.Rejection), CommandSyntax(c.Command) + "条件");
            if (c.Warning is not null) {
                Add("command", id, "warning", Text(c.Warning), CommandSyntax(c.Command) + "额外提示");
            }
        }
        foreach (var other in o.OtherCharacters) {
            Add("other", other.Id, "name", Text(other.Name), "同地角色 " + other.Id);
            Add("other", other.Id, "gathering", Text(other.GatheringResourceId), other.Name + "采集");
            Add("other", other.Id, "following-leader", Text(other.FollowingLeaderId), other.Name + "跟随领队");
            Add("other", other.Id, "waiting", CycleVisibleValue.Flag(other.Waiting), other.Name + "等待");
            Add("other", other.Id, "producing", Text(other.ProducingRecipeId), other.Name + "正在生产");
        }
        foreach (var memory in o.Memories) {
            string memoryId = memory.PlaceId ?? throw new InvalidOperationException("Visible place memory requires a stable place identity.");
            Add("memory", memoryId, "name", Text(memory.Name), "历史地点");
            Add("memory", memoryId, "at", At(memory.At), memory.Name + "历史观察时间");
            foreach (var r in memory.Resources) {
                string id = memoryId + "/" + r.Id;
                Add("memory-resource", id, "stock", CycleVisibleValue.Numeric(r.Stock, "份"), memory.Name + "历史" + r.Name);
                Add("memory-resource", id, "capacity", CycleVisibleValue.Numeric(r.Capacity, "份"), memory.Name + "历史" + r.Name + "容量");
            }
        }
        if (o.Weather is { } w) {
            Add("weather", "world", "next", At(w.NextWindow), "下次天气窗口");
            Add("weather", "world", "interval", CycleVisibleValue.Numeric(w.IntervalTicks, "duration"), "天气间隔");
            Add("weather", "world", "possible-loss", Number(w.ExposedWarmthLoss), "暴露时寒风最多损失");
            Add("weather", "world", "last-at", At(w.LastAt), "最近天气时间");
            Add("weather", "world", "last-kind", Text(w.LastKind?.ToString()), "最近天气");
            Add("weather", "world", "last-loss", Number(w.LastActualWarmthLoss), "最近实际温暖损失");
            Add("weather", "world", "last-sheltered", CycleVisibleValue.Flag(w.LastWasSheltered), "最近寒风时有庇护");
        }
        if (Context is { } context) {
            Add("continuity", actor, "draft", Text(DraftHandoff));
            Add("continuity", actor, "sequence", CycleVisibleValue.Numeric(context.ContextSequence), "决策回合");
        }
    }

    internal static string CommandId(CycleCommand c) => string.Join('|', c.Kind, c.Hours.ToString(CultureInfo.InvariantCulture),
        c.ResourceId ?? "", c.Count.ToString(CultureInfo.InvariantCulture), c.All ? "all" : "count", c.PassageId ?? "", c.ItemId ?? "", c.TargetActorId ?? "", c.RecipeId ?? "", c.MoneyAmount.ToString(CultureInfo.InvariantCulture),
        c.TradeId ?? "", c.GiveAsset?.AssetId ?? "", c.GiveAsset?.Amount.ToString(CultureInfo.InvariantCulture) ?? "",
        c.WantAsset?.AssetId ?? "", c.WantAsset?.Amount.ToString(CultureInfo.InvariantCulture) ?? "");
    internal static string CommandSyntax(CycleCommand c) => c.Kind switch {
        CycleCommandKind.Offer => $"offer {c.TargetActorId} {c.GiveAsset!.AssetId} {c.GiveAsset.Amount} for {c.WantAsset!.AssetId} {c.WantAsset.Amount} {c.Hours}",
        CycleCommandKind.Accept => $"accept {c.TradeId}", CycleCommandKind.Reject => $"reject {c.TradeId}", CycleCommandKind.Withdraw => $"withdraw {c.TradeId}",
        CycleCommandKind.Wait => $"wait {c.Hours}", CycleCommandKind.Gather => $"gather {c.ResourceId} {(c.All ? "all" : c.Count.ToString(CultureInfo.InvariantCulture))}",
        CycleCommandKind.Buy => $"buy {c.ItemId} {c.Count}", CycleCommandKind.Sell => $"sell {c.ItemId} {c.Count}",
        CycleCommandKind.Produce => $"produce {c.RecipeId} {c.Count}", CycleCommandKind.GiveMoney => $"give {c.TargetActorId} money {c.MoneyAmount}",
        CycleCommandKind.Give => $"give {c.TargetActorId} {c.ItemId} {c.Count}",
        CycleCommandKind.Follow => $"follow {c.TargetActorId}", CycleCommandKind.DismissFollowers => "dismiss-followers",
        CycleCommandKind.Go => $"go {c.PassageId}", CycleCommandKind.Use => $"use {c.ItemId}", _ => c.Kind.ToString().ToLowerInvariant()
    };

    private static string FieldLabel(string field) => field switch {
        "proposer" => "发起方", "recipient" => "收件人", "give" => "发起方给予", "want" => "发起方索取", "status" => "状态", "created" => "发起时间", "expires" => "到期时间", "closed" => "结束时间",
        "balance" => "资金余额", "buy-price" => "买入单价", "sell-price" => "卖出单价", "demand" => "剩余收购额度", "recipe" => "生产配方", "batches" => "生产批数",
        "input" => "预留投入物品", "input-count" => "预留投入数量", "output" => "计划产物", "output-count" => "计划产物数量",
        "food" => "食物", "water" => "饮水", "warmth" => "温暖", "food-rate" => "食物变化速率", "water-rate" => "饮水变化速率", "warmth-rate" => "温暖变化速率",
        "sheltered" => "有庇护", "warning-food" => "食物历史警告待确认", "warning-water" => "饮水历史警告待确认", "warning-warmth" => "温暖历史警告待确认",
        "kind" => "活动", "due" => "活动期限", "completed" => "已采集", "target" => "采集目标", "resource" => "采集资源",
        "started" => "当前周期开始", "selected-all" => "固定 all 目标", "draft" => "下回合留言草稿", "time" => "时间", "light" => "白天", "next-light" => "下次昼夜切换",
        "outcome" => "旅程状态", "ending" => "结局原因", "place" => "位置标识", "arrival" => "预计抵达", "count" => "待确认通知", "stock" => "余量", "warning" => "额外提示", "rejection" => "条件",
        "description" => "线索", "destination" => "已知目的地", "duration" => "耗时", _ => field
    };
    private static string NoticeFieldLabel(string field) => field switch {
        "kind" => "类别", "at" => "发生时间", "reason" => "原因", "actor" => "来源",
        "still-gathering" => "相关角色采集状态", "stock" => "相关资源余量", "capacity" => "相关资源容量", "count" => "累计次数", _ => field
    };
}
