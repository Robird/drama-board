using System.Collections.ObjectModel;
using System.Globalization;
using DramaBoard.Kernel.Time;
using DramaBoard.ResourceCycle;

/// <summary>A transient, actor-scoped semantic index. Missing values are resolved from this observation only.</summary>
internal sealed class CycleViewSnapshot {
    private readonly Dictionary<CycleStateKey, CycleVisibleValue> _values = [];
    private readonly Dictionary<CycleStateKey, string> _labels = [];
    public CycleObservation Observation { get; }
    public IReadOnlyDictionary<CycleStateKey, CycleVisibleValue> Values { get; }

    private CycleViewSnapshot(CycleObservation observation) {
        Observation = observation;
        Values = new ReadOnlyDictionary<CycleStateKey, CycleVisibleValue>(_values);
        Project();
    }

    public static CycleViewSnapshot Create(CycleObservation observation) => new(observation);

    public string? KnownLabel(CycleStateKey key) => _values.ContainsKey(key) ? Label(key) : null;

    public CycleVisibleValue Resolve(CycleStateKey key) {
        if (_values.TryGetValue(key, out var value)) { return value; }
        return key.Category switch {
            "inventory" when key.Field == "count" => CycleVisibleValue.Numeric(Observation.Actor.Backpack.Count(key.ObjectId), "份"),
            "resource" or "passage" or "other" or "command" => CycleVisibleValue.Unobserved,
            _ => CycleVisibleValue.KnownAbsent
        };
    }

    public string Label(CycleStateKey key) => _labels.GetValueOrDefault(key) ?? key.Category switch {
        "inventory" => $"{Observation.ItemNames.GetValueOrDefault(key.ObjectId, key.ObjectId)}库存",
        "resource" => $"{key.ObjectId} · {FieldLabel(key.Field)}",
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
        Add("actor", actor, "skill", CycleVisibleValue.Numeric(o.Actor.SkillLevel), "采药等级");
        Add("backpack", actor, "total", CycleVisibleValue.Numeric(o.Actor.Backpack.TotalCount, "件"), "背包总量");
        Add("backpack", actor, "capacity", o.Actor.Backpack.UnitCapacity is { } cap ? CycleVisibleValue.Numeric(cap, "件") : Text("不限"), "背包容量");
        foreach (var item in o.Actor.Backpack.Items) {
            Add("inventory", item.Key, "count", CycleVisibleValue.Numeric(item.Value, "份"), $"{o.ItemNames.GetValueOrDefault(item.Key, item.Key)}库存");
        }
        if (o.Survival is { } s) {
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
            Add("body", actor, "fatal-at", At(s.FatalAt));
            Add("body", actor, "warnings", s.PendingWarnings.Count == 0 ? CycleVisibleValue.KnownAbsent : Text(string.Join("；", s.PendingWarnings)), "待确认历史警告");
        }
        var activity = o.Actor.Activity;
        Add("activity", actor, "kind", Text(activity?.Kind.ToString()));
        Add("activity", actor, "due", At(activity?.Due));
        Add("activity", actor, "completed", activity?.Kind != CycleActivityKind.Gather ? CycleVisibleValue.KnownAbsent : CycleVisibleValue.Numeric(activity.CompletedCount, "份"));
        Add("activity", actor, "target", activity?.Kind != CycleActivityKind.Gather ? CycleVisibleValue.KnownAbsent : CycleVisibleValue.Numeric(activity.TargetCount, "份"));
        Add("activity", actor, "resource", Text(activity?.ResourceId));
        Add("activity", actor, "started", At(activity?.StartedAt));
        Add("activity", actor, "selected-all", activity is null ? CycleVisibleValue.KnownAbsent : CycleVisibleValue.Flag(activity.SelectedAll));
        Add("notice", actor, "count", CycleVisibleValue.Numeric(o.Notices.Count, "条"));
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
            Add("command", id, "warning", Text(c.Warning), CommandSyntax(c.Command) + "风险");
        }
        foreach (var other in o.OtherCharacters) {
            Add("other", other.Id, "name", Text(other.Name));
            Add("other", other.Id, "gathering", Text(other.GatheringResourceId), other.Name + "采集");
            Add("other", other.Id, "waiting", CycleVisibleValue.Flag(other.Waiting), other.Name + "等待");
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
        if (o.Continuity is { } continuity) {
            Add("continuity", actor, "draft", Text(continuity.DraftHandoff));
            Add("continuity", actor, "sequence", CycleVisibleValue.Numeric(continuity.ContextSequence), "决策回合");
            Add("continuity", actor, "pending", Text(continuity.Pending?.Action.Kind.ToString()), "已接受行动");
            if (continuity.Incoming is { } incoming) {
                Add("incoming", actor, "kind", Text(incoming.Action.Kind.ToString()), "上次行动");
                Add("incoming", actor, "accepted", At(incoming.Action.AcceptedAt), "上次行动接受时间");
                Add("incoming", actor, "delivered", At(incoming.DeliveredAt), "上次行动结果时间");
                Add("incoming", actor, "state", Text(incoming.ActivityState.ToString()), "上次行动状态");
                Add("incoming", actor, "handoff", Text(incoming.Handoff), "上回合留言");
                Add("incoming", actor, "gathered", CycleVisibleValue.Numeric(incoming.Summary.GatheredCount, "份"), "上次采集所得");
                Add("incoming", actor, "reasons", Text(incoming.Summary.Reasons.ToString()), "唤醒原因");
                Add("incoming", actor, "gather-item", Text(incoming.Summary.GatherItemId), "上次采集产物");
                Add("incoming", actor, "stop-reason", Text(incoming.Summary.GatherStopReason?.ToString()), "上次采集停止原因");
                Add("incoming", actor, "calm-count", CycleVisibleValue.Numeric(incoming.Summary.CalmWeatherCount, "次"), "上次行动期间平静天气");
                Add("incoming", actor, "cold-count", CycleVisibleValue.Numeric(incoming.Summary.ColdWeatherCount, "次"), "上次行动期间寒风");
                Add("incoming", actor, "cold-loss", CycleVisibleValue.Numeric(incoming.Summary.WeatherWarmthLossUnits, "点", CycleSurvival.Scale), "上次行动寒风总损失");
                Add("incoming", actor, "light-count", CycleVisibleValue.Numeric(incoming.Summary.LightChangeCount, "次"), "上次行动昼夜切换");
                Add("incoming", actor, "warning-mask", CycleVisibleValue.Numeric(incoming.Summary.WarningMask), "上次行动警告类别");
                Add("incoming", actor, "saturated", CycleVisibleValue.Flag(incoming.Summary.IsSaturated), "上次行动计数已达上限");
                Add("incoming", actor, "hours", CycleVisibleValue.Numeric(incoming.Action.Hours, "小时"), "上次等待时长");
                Add("incoming", actor, "resource", Text(incoming.Action.ResourceId), "上次采集资源");
                Add("incoming", actor, "target", CycleVisibleValue.Numeric(incoming.Action.TargetCount, "份"), "上次采集目标");
                Add("incoming", actor, "selected-all", CycleVisibleValue.Flag(incoming.Action.SelectedAll), "上次固定 all 目标");
                Add("incoming", actor, "passage", Text(incoming.Action.PassageId), "上次行经通路");
                ProjectEndpoint("incoming-before", "上次行动前", incoming.Before);
                ProjectEndpoint("incoming-after", "上次行动后", incoming.After);
            }
        }
    }

    private void ProjectEndpoint(string category, string label, CycleContinuitySnapshot endpoint) {
        string actor = Observation.Actor.Id;
        foreach (var (field, units) in new[] { ("food", endpoint.FoodUnits), ("water", endpoint.WaterUnits), ("warmth", endpoint.WarmthUnits) }) {
            Add(category, actor, field, units is { } value ? CycleVisibleValue.Numeric(value, "点", CycleSurvival.Scale) : CycleVisibleValue.KnownAbsent, label + FieldLabel(field));
        }
        Add(category, actor, "place", Text(endpoint.PlaceId), label + "位置标识");
        Add(category, actor, "place-name", Text(endpoint.PlaceName), label + "位置");
        Add(category, actor, "passage", Text(endpoint.PassageId), label + "行经通路");
        Add(category, actor, "target-place", Text(endpoint.KnownTargetPlaceId), label + "已知目的地标识");
        Add(category, actor, "target-name", Text(endpoint.KnownTargetPlaceName), label + "已知目的地");
        Add(category, actor, "activity-id", endpoint.ActivityId is { } id ? CycleVisibleValue.Numeric(id) : CycleVisibleValue.KnownAbsent, label + "活动编号");
        Add(category, actor, "completed", CycleVisibleValue.Numeric(endpoint.CompletedCount, "份"), label + "采集进度");
        Add(category, actor, "target", CycleVisibleValue.Numeric(endpoint.TargetCount, "份"), label + "采集目标");
        Add(category, actor, "due", At(endpoint.ActivityDue), label + "活动期限");
        foreach (var item in endpoint.Backpack) {
            Add(category + "-inventory", item.Key, "count", CycleVisibleValue.Numeric(item.Value, "份"), label + Observation.ItemNames.GetValueOrDefault(item.Key, item.Key) + "库存");
        }
    }

    internal static string CommandId(CycleCommand c) => string.Join('|', c.Kind, c.Hours.ToString(CultureInfo.InvariantCulture),
        c.ResourceId ?? "", c.Count.ToString(CultureInfo.InvariantCulture), c.All ? "all" : "count", c.PassageId ?? "", c.ItemId ?? "");
    internal static string CommandSyntax(CycleCommand c) => c.Kind switch {
        CycleCommandKind.Wait => $"wait {c.Hours}", CycleCommandKind.Gather => $"gather {c.ResourceId} {(c.All ? "all" : c.Count.ToString(CultureInfo.InvariantCulture))}",
        CycleCommandKind.Go => $"go {c.PassageId}", CycleCommandKind.Use => $"use {c.ItemId}", _ => c.Kind.ToString().ToLowerInvariant()
    };

    private static string FieldLabel(string field) => field switch {
        "food" => "食物", "water" => "饮水", "warmth" => "温暖", "food-rate" => "食物变化速率", "water-rate" => "饮水变化速率", "warmth-rate" => "温暖变化速率",
        "sheltered" => "有庇护", "warning-food" => "食物历史警告待确认", "warning-water" => "饮水历史警告待确认", "warning-warmth" => "温暖历史警告待确认",
        "fatal-at" => "预计最早耗尽", "kind" => "活动", "due" => "活动期限", "completed" => "已采集", "target" => "采集目标", "resource" => "采集资源",
        "started" => "当前周期开始", "selected-all" => "固定 all 目标", "draft" => "下回合留言草稿", "time" => "时间", "light" => "白天", "next-light" => "下次昼夜切换",
        "outcome" => "旅程状态", "ending" => "结局原因", "place" => "位置标识", "arrival" => "预计抵达", "count" => "待确认通知", "stock" => "余量", "warning" => "风险", "rejection" => "条件",
        "description" => "线索", "destination" => "已知目的地", "duration" => "耗时", _ => field
    };
    private static string NoticeFieldLabel(string field) => field switch {
        "kind" => "类别", "at" => "发生时间", "reason" => "原因", "actor" => "来源",
        "still-gathering" => "相关角色采集状态", "stock" => "相关资源余量", "capacity" => "相关资源容量", "count" => "累计次数", _ => field
    };
}
