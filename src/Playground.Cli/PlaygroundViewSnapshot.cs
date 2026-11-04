using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using DramaBoard.Kernel.Time;
using DramaBoard.Playground;

/// <summary>A transient, actor-scoped semantic index. Missing values are resolved from this observation only.</summary>
internal sealed class PlaygroundViewSnapshot {
    private static readonly JsonSerializerOptions CommandIdentityJson = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };
    private readonly Dictionary<PlaygroundStateKey, PlaygroundVisibleValue> _values = [];
    private readonly Dictionary<PlaygroundStateKey, string> _labels = [];
    private readonly Dictionary<PlaygroundStateKey, PlaygroundAttributeView> _rateExplanations = [];
    private readonly Dictionary<string, PlaygroundCommand> _commands = new(StringComparer.Ordinal);
    public PlaygroundObservation Observation { get; }
    public CharacterInteractionState? InteractionState { get; }
    public CharacterContext? Context { get; }
    public PlaygroundBackground Background { get; }
    public string? DraftHandoff { get; }
    public bool IncludeHistory { get; }
    public bool IncludeGoals { get; }
    public IReadOnlyDictionary<PlaygroundStateKey, PlaygroundVisibleValue> Values { get; }
    public IReadOnlyDictionary<PlaygroundStateKey, PlaygroundAttributeView> RateExplanations { get; }
    public IReadOnlyDictionary<string, PlaygroundCommand> Commands { get; }

    private PlaygroundViewSnapshot(PlaygroundObservation observation, CharacterInteractionState? interactionState,
        CharacterContext? context = null, string? draftHandoff = null, bool includeHistory = true, bool includeGoals = true) {
        Observation = observation;
        InteractionState = interactionState;
        Context = context ?? interactionState?.CurrentContext;
        Background = Context?.Opening.Background ?? PlaygroundBackground.For(observation);
        DraftHandoff = context is not null ? draftHandoff : interactionState?.DraftHandoff;
        IncludeHistory = includeHistory;
        IncludeGoals = includeGoals;
        Values = new ReadOnlyDictionary<PlaygroundStateKey, PlaygroundVisibleValue>(_values);
        RateExplanations = new ReadOnlyDictionary<PlaygroundStateKey, PlaygroundAttributeView>(_rateExplanations);
        Commands = new ReadOnlyDictionary<string, PlaygroundCommand>(_commands);
        Project();
    }

    public static PlaygroundViewSnapshot Create(PlaygroundObservation observation, CharacterInteractionState? interactionState = null) => new(observation, interactionState);

    public static PlaygroundViewSnapshot CreateRecorded(PlaygroundObservation observation, CharacterContext context,
        string? draftHandoff, bool includeHistory = true, bool includeGoals = true) => new(observation, null, context, draftHandoff, includeHistory, includeGoals);

    public string? KnownLabel(PlaygroundStateKey key) => _values.ContainsKey(key) ? Label(key) : null;

    public PlaygroundVisibleValue Resolve(PlaygroundStateKey key) {
        if (_values.TryGetValue(key, out var value)) { return value; }
        return key.Category switch {
            "following" when key.Field.StartsWith("follower:", StringComparison.Ordinal) => PlaygroundVisibleValue.Flag(false),
            "inventory" when key.Field == "count" => PlaygroundVisibleValue.Numeric(Observation.Actor.Backpack.Count(key.ObjectId), "份"),
            "command" when key.Field == "warning" && _values.ContainsKey(new("command", key.ObjectId, "rejection")) => PlaygroundVisibleValue.KnownAbsent,
            "resource" or "passage" or "other" or "command" or "market" or "recipe" or "trade" => PlaygroundVisibleValue.Unobserved,
            _ => PlaygroundVisibleValue.KnownAbsent
        };
    }

    public string Label(PlaygroundStateKey key) => _labels.GetValueOrDefault(key) ?? key.Category switch {
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

    private void Add(string category, string id, string field, PlaygroundVisibleValue value, string? label = null) {
        var key = new PlaygroundStateKey(category, id, field);
        _values.Add(key, value);
        if (label is not null) { _labels.Add(key, label); }
    }
    private static PlaygroundVisibleValue Text(string? value) => value is null ? PlaygroundVisibleValue.KnownAbsent : PlaygroundVisibleValue.Literal(value);
    private static PlaygroundVisibleValue At(ModelTime? value) => value is { } at ? PlaygroundVisibleValue.Numeric(at.Ticks, "ticks") : PlaygroundVisibleValue.KnownAbsent;
    private static PlaygroundVisibleValue Number(decimal value, string unit = "点") => PlaygroundVisibleValue.Numeric(checked((long)decimal.Round(value * PlaygroundSurvival.Scale)), unit, PlaygroundSurvival.Scale);

    private void Project() {
        var o = Observation;
        string actor = o.Actor.Id;
        Add("world", "world", "time", At(o.Time));
        Add("world", "world", "light", PlaygroundVisibleValue.Flag(o.IsDay));
        Add("world", "world", "next-light", At(o.NextLightChange));
        Add("world", "world", "outcome", Text(o.Outcome.ToString()));
        Add("world", "world", "ending", Text(o.EndingReason));
        Add("location", actor, "place", Text(o.PlaceId));
        Add("location", actor, "name", Text(o.PlaceName), "位置");
        Add("location", actor, "description", Text(o.PlaceDescription), "眼前环境");
        Add("location", actor, "arrival", At(o.ArrivalDue));
        Add("location", actor, "journey", Text(o.Journey is { } journey ? JourneyView.Route(journey) : null), "行程");
        Add("actor", actor, "skill", PlaygroundVisibleValue.Numeric(o.Actor.SkillLevel), "采药等级");
        if (o.IsExpedition) { Add("money", actor, "balance", PlaygroundVisibleValue.Numeric(o.Actor.Money, "钱"), "自己的资金余额"); }
        if (o.MarketRefreshAt is { } refresh) { Add("market", "market", "refresh", At(refresh), "市场下次补充"); }
        foreach (PlaygroundMarketQuote quote in o.MarketQuotes) {
            string name = o.ItemNames.GetValueOrDefault(quote.ItemId, quote.ItemId);
            Add("market", quote.ItemId, "buy-price", quote.BuyPrice > 0 ? PlaygroundVisibleValue.Numeric(quote.BuyPrice, "钱") : Text("不出售"), name + "买入单价");
            Add("market", quote.ItemId, "sell-price", quote.SellPrice > 0 ? PlaygroundVisibleValue.Numeric(quote.SellPrice, "钱") : Text("不收购"), name + "卖出单价");
            Add("market", quote.ItemId, "stock", PlaygroundVisibleValue.Numeric(quote.Stock, "份"), name + "市场可买库存");
            Add("market", quote.ItemId, "demand", PlaygroundVisibleValue.Numeric(quote.Demand, "份"), name + "剩余收购额度");
        }
        foreach (PlaygroundRecipe recipe in o.Recipes) {
            Add("recipe", recipe.Id, "input", Text(recipe.InputItem), recipe.Id + "每批投入物品");
            Add("recipe", recipe.Id, "input-count", PlaygroundVisibleValue.Numeric(recipe.InputCount, "份"), recipe.Id + "每批投入数量");
            Add("recipe", recipe.Id, "output", Text(recipe.OutputItem), recipe.Id + "每批产物");
            Add("recipe", recipe.Id, "output-count", PlaygroundVisibleValue.Numeric(recipe.OutputCount, "份"), recipe.Id + "每批产出数量");
            Add("recipe", recipe.Id, "duration", PlaygroundVisibleValue.Numeric(recipe.DurationTicks, "duration"), recipe.Id + "每批生产耗时");
        }
        foreach (PlaygroundTradeOffer offer in o.Trades) {
            Add("trade", offer.Id, "proposer", Text(offer.ProposerId), "报价 " + offer.Id + "发起方");
            Add("trade", offer.Id, "recipient", Text(offer.RecipientId), "报价 " + offer.Id + "收件人");
            Add("trade", offer.Id, "give", Text(TradeView.Asset(offer.Give, o)), "报价 " + offer.Id + "发起方给予");
            Add("trade", offer.Id, "want", Text(TradeView.Asset(offer.Want, o)), "报价 " + offer.Id + "发起方索取");
            Add("trade", offer.Id, "status", Text(TradeView.Status(offer.Status)), "报价 " + offer.Id + "状态");
            Add("trade", offer.Id, "created", At(offer.CreatedAt), "报价 " + offer.Id + "发起时间");
            Add("trade", offer.Id, "expires", At(offer.ExpiresAt), "报价 " + offer.Id + "到期时间");
            Add("trade", offer.Id, "closed", At(offer.ClosedAt), "报价 " + offer.Id + "结束时间");
        }
        Add("backpack", actor, "total", PlaygroundVisibleValue.Numeric(o.Actor.Backpack.TotalCount, "件"), "背包总量");
        Add("backpack", actor, "capacity", o.Actor.Backpack.UnitCapacity is { } cap ? PlaygroundVisibleValue.Numeric(cap, "件") : Text("不限"), "背包容量");
        foreach (var item in o.Actor.Backpack.Items) {
            Add("inventory", item.Key, "count", PlaygroundVisibleValue.Numeric(item.Value, "份"), $"{o.ItemNames.GetValueOrDefault(item.Key, item.Key)}库存");
        }
        if (o.Survival is { } s) {
            foreach (var attribute in PlaygroundAttributeView.FromSurvival(s, o.Outcome == PlaygroundExpeditionOutcome.Exploring)) {
                _rateExplanations.Add(new("body", actor, attribute.Field + "-rate"), attribute);
            }
            Add("body", actor, "food", Number(s.Food));
            Add("body", actor, "water", Number(s.Water));
            Add("body", actor, "warmth", Number(s.Warmth));
            Add("body", actor, "food-rate", PlaygroundVisibleValue.Numeric(s.FoodPerHour, "点/小时"));
            Add("body", actor, "water-rate", PlaygroundVisibleValue.Numeric(s.WaterPerHour, "点/小时"));
            Add("body", actor, "warmth-rate", PlaygroundVisibleValue.Numeric(s.WarmthPerHour, "点/小时"));
            Add("body", actor, "sheltered", PlaygroundVisibleValue.Flag(s.Sheltered));
            Add("body", actor, "warning-food", PlaygroundVisibleValue.Flag((s.PendingWarningMask & 1) != 0));
            Add("body", actor, "warning-water", PlaygroundVisibleValue.Flag((s.PendingWarningMask & 2) != 0));
            Add("body", actor, "warning-warmth", PlaygroundVisibleValue.Flag((s.PendingWarningMask & 4) != 0));
            Add("body", actor, "warnings", s.PendingWarnings.Count == 0 ? PlaygroundVisibleValue.KnownAbsent : Text(string.Join("；", s.PendingWarnings)), "待确认历史警告");
        }
        if (o.IsExpedition) {
            Add("following", actor, "leader", Text(o.FollowingLeaderId), "跟随领队");
            foreach (var follower in o.Followers) {
                Add("following", actor, "follower:" + follower.Key, PlaygroundVisibleValue.Flag(true), $"直属队员 {follower.Value}（{follower.Key}）");
            }
        }
        var activity = o.Actor.Activity;
        Add("activity", actor, "kind", Text(activity?.Kind.ToString()));
        Add("activity", actor, "due", At(activity?.Due));
        Add("activity", actor, "completed", activity?.Kind != PlaygroundActivityKind.Gather ? PlaygroundVisibleValue.KnownAbsent : PlaygroundVisibleValue.Numeric(activity.CompletedCount, "份"));
        Add("activity", actor, "target", activity?.Kind != PlaygroundActivityKind.Gather ? PlaygroundVisibleValue.KnownAbsent : PlaygroundVisibleValue.Numeric(activity.TargetCount, "份"));
        Add("activity", actor, "resource", Text(activity?.ResourceId));
        Add("activity", actor, "started", At(activity?.StartedAt));
        if (activity?.Production is { } production) {
            Add("activity", actor, "recipe", Text(production.RecipeId), "生产配方");
            Add("activity", actor, "batches", PlaygroundVisibleValue.Numeric(production.Batches, "批"), "生产批数");
            Add("activity", actor, "input", Text(production.InputItem), "生产预留物品");
            Add("activity", actor, "input-count", PlaygroundVisibleValue.Numeric(production.InputCount, "份"), "生产预留总量");
            Add("activity", actor, "output", Text(production.OutputItem), "生产目标物品");
            Add("activity", actor, "output-count", PlaygroundVisibleValue.Numeric(production.OutputCount, "份"), "生产目标总量");
        }
        Add("activity", actor, "selected-all", activity is null ? PlaygroundVisibleValue.KnownAbsent : PlaygroundVisibleValue.Flag(activity.SelectedAll));
        Add("notice", actor, "count", PlaygroundVisibleValue.Numeric(o.Notices.Count, "条"));
        if (o.Communication is { } communication) {
            Add("communication", actor, "remaining", PlaygroundVisibleValue.Numeric(communication.RemainingSends, "次"), "本时刻剩余通信发送额度");
            Add("communication", actor, "pending", PlaygroundVisibleValue.Flag(communication.AttentionPending), "通信待响应");
        }
        foreach (var n in o.Notices) {
            string id = string.Join('|', n.Notice.SourceId, n.Notice.Kind, n.Notice.ResourceId, n.Notice.ActorId ?? "");
            Add("notice-detail", id, "kind", Text(n.Notice.Kind.ToString()), "历史通知类别");
            Add("notice-detail", id, "at", At(n.Notice.At), "历史通知发生时间");
            Add("notice-detail", id, "reason", Text(n.Notice.Reason), $"{n.ResourceName}通知");
            Add("notice-detail", id, "actor", Text(n.ActorLabel), "通知来源");
            Add("notice-detail", id, "still-gathering", n.StillGathering is { } gathering ? PlaygroundVisibleValue.Flag(gathering) : PlaygroundVisibleValue.Unobserved, "通知所指角色仍在采集");
            Add("notice-detail", id, "stock", n.CurrentStock is { } stock ? PlaygroundVisibleValue.Numeric(stock, "份") : PlaygroundVisibleValue.Unobserved, n.ResourceName + "当前余量");
            Add("notice-detail", id, "capacity", n.Capacity is { } capacity ? PlaygroundVisibleValue.Numeric(capacity, "份") : PlaygroundVisibleValue.Unobserved, n.ResourceName + "容量");
            Add("notice-detail", id, "count", PlaygroundVisibleValue.Numeric(n.Notice.Count, "次"), "同类通知累计次数");
        }
        foreach (var r in o.Resources) {
            string prefix = r.Name + " · ";
            Add("resource", r.Id, "stock", PlaygroundVisibleValue.Numeric(r.Stock, "份"), prefix + "余量");
            Add("resource", r.Id, "capacity", PlaygroundVisibleValue.Numeric(r.Capacity, "份"), prefix + "容量");
            Add("resource", r.Id, "item", Text(r.ItemId), prefix + "产物");
            Add("resource", r.Id, "harvest", PlaygroundVisibleValue.Numeric(o.HarvestDurations[r.Id], "duration"), prefix + "每份耗时");
            Add("resource", r.Id, "regeneration", PlaygroundVisibleValue.Numeric(r.RegenerationTicks, "duration"), prefix + "再生周期");
            Add("resource", r.Id, "progress", PlaygroundVisibleValue.Numeric(r.ProgressAt(o.Time), "duration"), prefix + "再生进度");
            Add("resource", r.Id, "active", PlaygroundVisibleValue.Flag(r.ActiveSince is not null), prefix + "正在再生");
            Add("resource", r.Id, "day-growth", PlaygroundVisibleValue.Flag(r.GrowsInDaylight), prefix + "白天生长");
            Add("resource", r.Id, "required-level", PlaygroundVisibleValue.Numeric(r.RequiredLevel), prefix + "所需等级");
        }
        if (o.IsExpedition) {
            Add("navigation", actor, "candidate", Text(NavigationView.RenderCandidate(o)), "当前候选路径");
            Add("navigation", actor, "active", Text(NavigationView.RenderActive(o)), "活动导航");
        }
        foreach (var place in o.KnownMap) {
            Add("known-place", place.Id, "name", Text(place.Name), "已知地点 " + place.Id);
            foreach (var entrance in place.Entrances) {
                string id = place.Id + "/" + entrance.Id;
                Add("known-entrance", id, "description", Text(entrance.Description), place.Name + "（" + place.Id + "）入口 " + entrance.Id + " · 线索");
                Add("known-entrance", id, "duration", PlaygroundVisibleValue.Numeric(entrance.DurationTicks, "duration"), place.Name + "（" + place.Id + "）入口 " + entrance.Id + " · 耗时");
                Add("known-entrance", id, "destination", Text(entrance.KnownDestinationId is { } destination
                    ? entrance.KnownDestinationName + "（" + destination + "）" : "去向未知"), place.Name + "（" + place.Id + "）入口 " + entrance.Id + " · 已知连接");
            }
        }
        foreach (var p in o.Passages) {
            Add("passage", p.Id, "description", Text(p.Description), "通路 " + p.Id + " · 线索");
            Add("passage", p.Id, "duration", PlaygroundVisibleValue.Numeric(p.DurationTicks, "duration"), "通路 " + p.Id + " · 耗时");
            Add("passage", p.Id, "destination", Text(p.KnownDestinationName), "通路 " + p.Id + " · 已知目的地");
        }
        foreach (var c in o.Commands) {
            string id = CommandId(c.Command);
            _commands.Add(id, c.Command);
            string syntax = PlaygroundCommandParser.Format(c.Command);
            Add("command", id, "rejection", Text(c.Rejection), syntax + "条件");
            if (c.Warning is not null) {
                Add("command", id, "warning", Text(c.Warning), syntax + "额外提示");
            }
        }
        foreach (var other in o.OtherCharacters) {
            Add("other", other.Id, "name", Text(other.Name), "同地角色 " + other.Id);
            Add("other", other.Id, "gathering", Text(other.GatheringResourceId), other.Name + "采集");
            Add("other", other.Id, "following-leader", Text(other.FollowingLeaderId), other.Name + "跟随领队");
            Add("other", other.Id, "waiting", PlaygroundVisibleValue.Flag(other.Waiting), other.Name + "等待");
            Add("other", other.Id, "producing", Text(other.ProducingRecipeId), other.Name + "正在生产");
        }
        foreach (var memory in o.Memories) {
            string memoryId = memory.PlaceId ?? throw new InvalidOperationException("Visible place memory requires a stable place identity.");
            Add("memory", memoryId, "name", Text(memory.Name), "历史地点");
            Add("memory", memoryId, "at", At(memory.At), memory.Name + "历史观察时间");
            foreach (var r in memory.Resources) {
                string id = memoryId + "/" + r.Id;
                Add("memory-resource", id, "stock", PlaygroundVisibleValue.Numeric(r.Stock, "份"), memory.Name + "历史" + r.Name);
                Add("memory-resource", id, "capacity", PlaygroundVisibleValue.Numeric(r.Capacity, "份"), memory.Name + "历史" + r.Name + "容量");
            }
        }
        if (o.Weather is { } w) {
            Add("weather", "world", "next", At(w.NextWindow), "下次天气窗口");
            Add("weather", "world", "interval", PlaygroundVisibleValue.Numeric(w.IntervalTicks, "duration"), "天气间隔");
            Add("weather", "world", "possible-loss", Number(w.ExposedWarmthLoss), "暴露时寒风最多损失");
            Add("weather", "world", "last-at", At(w.LastAt), "最近天气时间");
            Add("weather", "world", "last-kind", Text(w.LastKind?.ToString()), "最近天气");
            Add("weather", "world", "last-loss", Number(w.LastActualWarmthLoss), "最近实际温暖损失");
            Add("weather", "world", "last-sheltered", PlaygroundVisibleValue.Flag(w.LastWasSheltered), "最近寒风时有庇护");
        }
        if (Context is { } context) {
            Add("continuity", actor, "draft", Text(DraftHandoff));
            Add("continuity", actor, "sequence", PlaygroundVisibleValue.Numeric(context.ContextSequence), "决策回合");
        }
    }

    // Opaque transient identity: preserve every field, including null/default values; never parse or display it.
    internal static string CommandId(PlaygroundCommand command) => JsonSerializer.Serialize(command, CommandIdentityJson);

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
