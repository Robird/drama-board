using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.ResourceCycle;

public sealed record CycleSurvivalObservation(decimal Food, decimal Water, decimal Warmth,
    int FoodPerHour, int WaterPerHour, int WarmthPerHour, bool Sheltered,
    IReadOnlyList<string> PendingWarnings, ModelTime? FatalAt);

/// <summary>Application survival policy. Prediction and projection are pure; planning records concrete replacements.</summary>
public static class CycleSurvivalRules {
    public static string? UseDescription(string itemId) => itemId switch {
        "item.food" => "消耗 1 份食物，饱食 +35（最高 100）；不耗时。",
        "item.water" => "消耗 1 份饮水，水分 +45（最高 100）；不耗时。",
        _ => null
    };
    public static CycleSurvivalObservation? Observe(CycleWorld world, string actorId) {
        CycleSurvival? anchor = world.GetCharacter(actorId).Survival;
        if (anchor is null) { return null; }
        CycleSurvival body = anchor.Project(world.Time);
        var warnings = new List<string>();
        foreach ((int mask, string name) in new[] { (1, "饱食"), (2, "水分"), (4, "温暖") }) {
            if ((body.PendingMask & mask) != 0) { warnings.Add($"{name}曾降至警戒线（本组最早提醒于 {body.PendingSince}）。"); }
        }
        return new((decimal)body.FoodUnits / CycleSurvival.Scale, (decimal)body.WaterUnits / CycleSurvival.Scale,
            (decimal)body.WarmthUnits / CycleSurvival.Scale, body.FoodPerHour, body.WaterPerHour, body.WarmthPerHour,
            IsSheltered(world.Expedition?.Spatial, actorId), warnings.AsReadOnly(), FatalTime(body, world.Time));
    }

    /// <summary>Known deterministic risk over a proposed activity, including scheduled day/night boundaries.</summary>
    /// <remarks>Call only after CycleActions.Check accepts the command; this is not an identity discovery query.</remarks>
    public static string? ActionWarning(CycleWorld world, string actorId, CycleCommand command) {
        CycleSurvival? anchor = world.GetCharacter(actorId).Survival;
        if (anchor is null) { return null; }
        long duration;
        switch (command.Kind) {
            case CycleCommandKind.Wait: duration = command.Hours * CycleTime.Hour; break;
            case CycleCommandKind.Go:
                string? place = CycleActions.CurrentPlace(world, actorId);
                if (place is null) { return null; }
                duration = CycleExpeditionMap.GetPassages(place).Single(p => p.Id == command.PassageId).DurationTicks;
                break;
            case CycleCommandKind.Gather:
                CycleResource resource = world.GetResource(command.ResourceId!);
                long count = command.All ? resource.Stock : command.Count;
                duration = (long)Int128.Min(long.MaxValue - world.Time.Ticks,
                    (Int128)CycleActions.HarvestDuration(resource, world.GetCharacter(actorId)) * count);
                break;
            default: return null;
        }
        ModelTime end = new(checked(world.Time.Ticks + duration));
        bool sheltered = command.Kind != CycleCommandKind.Go && IsSheltered(world.Expedition?.Spatial, actorId);
        CycleEnvironment? weather = world.Expedition?.Environment;
        string weatherRisk = weather?.NextDue is { } weatherAt && weatherAt <= end
            ? sheltered ? $" 期间有天气窗口（下一次 {weatherAt}）；留在庇护中可免除寒风损失。"
                : $" 期间有天气窗口（下一次 {weatherAt}）；每次可能额外损失 {CycleEnvironmentRules.ExposureLoss} 点温暖。"
            : "";
        string basis = weather is null ? "按已知昼夜与当前补给" : "不计随机寒风，按已知昼夜与当前补给";
        CycleSurvival body = anchor.Project(world.Time);
        ModelTime cursor = world.Time;
        ModelTime? firstWarning = null;
        string response = command.Kind == CycleCommandKind.Go
            ? "途中可在警告时补给，但不能中止本段移动。" : "警告会暂停时间，可补给或取消活动。";
        while (cursor < end) {
            body = body.WithRates(cursor, body.FoodPerHour, body.WaterPerHour, WarmthRate(sheltered, CycleTime.IsDay(cursor)));
            ModelTime nextLight = CycleTime.NextLightChange(cursor);
            ModelTime next = nextLight < end ? nextLight : end;
            if (FatalTime(body, cursor) is { } fatal && fatal <= next) {
                return $"危险：{basis}，身体将在 {fatal} 耗尽。{response}{weatherRisk}";
            }
            if (firstWarning is null && NextThreshold(body, cursor) is { Fatal: false } warning && warning.Due <= next) { firstWarning = warning.Due; }
            body = body.Project(next);
            cursor = next;
        }
        return firstWarning is { } at ? $"注意：{basis}，预计 {at} 触发身体警告。{response}{weatherRisk}"
            : weatherRisk.Length > 0 ? weatherRisk.TrimStart() : null;
    }

    internal static (ModelTime Due, bool Fatal)? NextThreshold(CycleSurvival survival, ModelTime at) {
        CycleSurvival value = survival.Project(at);
        (ModelTime Due, bool Fatal)? best = null;
        Consider(value.FoodUnits, value.FoodPerHour, 1);
        Consider(value.WaterUnits, value.WaterPerHour, 2);
        Consider(value.WarmthUnits, value.WarmthPerHour, 4);
        return best;

        void Consider(long units, int rate, int mask) {
            Add(Due(units, rate, 0, at), true);
            if ((value.WarnedMask & mask) == 0) { Add(Due(units, rate, CycleSurvival.WarningUnits, at), false); }
        }
        void Add(ModelTime? due, bool fatal) {
            if (due is { } candidate && (best is null || candidate < best.Value.Due || candidate == best.Value.Due && fatal)) {
                best = (candidate, fatal);
            }
        }
    }
    internal static CycleFact Boundary(CycleWorld world, CyclePrediction prediction) {
        CycleCharacter actor = world.GetCharacter(prediction.TargetId);
        bool fatal = prediction.Kind == CyclePredictionKind.SurvivalFatal;
        CycleSurvival body = fatal ? actor.Survival!.Project(prediction.Due) : actor.Survival!.Warn(prediction.Due);
        string reason = fatal ? body.WaterUnits == 0 ? "脱水耗尽" : body.FoodUnits == 0 ? "饥饿耗尽" : "失温耗尽" : "身体降至警戒线，请检查补给与环境。";
        return new(fatal ? CycleEventKind.SurvivalFailed : CycleEventKind.SurvivalWarning, prediction.Due, actor.Id,
            changedCharacters: [actor.Change(actor.Activity, result: reason, survival: body)], result: reason,
            outcome: fatal ? CycleExpeditionOutcome.Dead : null, endingReason: fatal ? $"{actor.Name}因{reason}而未能走出山林。" : null);
    }

    /// <summary>Changes anchors only while planning; recorded facts contain all effects needed for recovery.</summary>
    internal static CycleFact Enrich(CycleWorld world, CycleFact fact) {
        var changes = fact.ChangedCharacters.ToDictionary(c => c.Id, StringComparer.Ordinal);
        GraphSpatialState? spatial = world.Expedition?.Spatial;
        if (spatial is not null) {
            foreach (GraphSpatialFact movement in fact.SpatialFacts) { spatial = CycleExpeditionMap.Reducer.Apply(spatial, new(fact.At, 0), movement); }
        }
        bool light = fact.Light?.IsDay ?? world.Light.IsDay;
        foreach (CycleCharacter previous in world.Characters) {
            if (previous.Survival is null || !changes.ContainsKey(previous.Id) && fact.Kind != CycleEventKind.LightChanged) { continue; }
            CycleCharacter actor = changes.GetValueOrDefault(previous.Id, previous);
            CycleSurvival body = actor.Survival!.WithRates(fact.At, actor.Survival.FoodPerHour, actor.Survival.WaterPerHour,
                WarmthRate(IsSheltered(spatial, actor.Id), light));
            if (fact.LastServedActorId == actor.Id && fact.Kind != CycleEventKind.ItemUsed) { body = body.Acknowledge(fact.At); }
            changes[actor.Id] = changes.ContainsKey(actor.Id) ? actor.WithSurvival(body) : actor.Change(actor.Activity, survival: body);
        }
        return changes.Count == 0 ? fact : fact.WithCharacters(world.Characters.Where(c => changes.ContainsKey(c.Id)).Select(c => changes[c.Id]));
    }
    internal static void Validate(CycleBusinessState business) {
        foreach (CycleCharacter actor in business.Characters) {
            if (actor.Survival is not { } body) { continue; }
            if (business.Expedition is null) { throw new InvalidOperationException("Survival needs require an expedition."); }
            if (business.Expedition.Outcome == CycleExpeditionOutcome.Exploring &&
                NextThreshold(body, body.BaselineAt) is { } threshold && threshold.Due < business.Time) {
                throw new InvalidOperationException("An unprocessed survival threshold precedes the completed boundary.");
            }
            if (business.Expedition.Outcome == CycleExpeditionOutcome.Dead && (!body.Project(business.Time).IsFatal ||
                string.IsNullOrWhiteSpace(business.Expedition.EndingReason))) {
                throw new InvalidOperationException("Death requires recorded depletion and a reason.");
            }
        }
    }
    internal static bool IsSheltered(GraphSpatialState? spatial, string actorId) =>
        spatial?.Entities.Single(e => e.Id.Value == actorId).Location is AtPlaceLocation place && CycleExpeditionMap.GetPlace(place.PlaceId.Value).Sheltered;
    private static int WarmthRate(bool sheltered, bool day) => sheltered ? 12 : day ? 6 : -12;
    private static ModelTime? FatalTime(CycleSurvival body, ModelTime at) => new[] {
        Due(body.FoodUnits, body.FoodPerHour, 0, at), Due(body.WaterUnits, body.WaterPerHour, 0, at), Due(body.WarmthUnits, body.WarmthPerHour, 0, at)
    }.Where(due => due is not null).Min();
    private static ModelTime? Due(long value, int rate, long target, ModelTime at) {
        if (value <= target) { return at; }
        if (rate >= 0) { return null; }
        long decline = -(long)rate;
        long ticks = (value - target + decline - 1) / decline;
        return at.Ticks > long.MaxValue - ticks ? null : new(at.Ticks + ticks);
    }
}
