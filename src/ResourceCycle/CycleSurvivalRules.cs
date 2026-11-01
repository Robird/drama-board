using Atelia.DurableGraph;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.ResourceCycle;

/// <summary>Frozen current needs and independent attribute forecasts: unchanged shelter, no supplies,
/// known day/night changes, and no random weather. Other attributes do not truncate an estimate.</summary>
[DurableType("DramaBoard.ResourceCycle.SurvivalObservation", 3)]
public sealed partial record CycleSurvivalObservation(
    [field: DurableField(1)] decimal Food,
    [field: DurableField(2)] decimal Water,
    [field: DurableField(3)] decimal Warmth,
    [field: DurableField(4)] RateBreakdown FoodRate,
    [field: DurableField(5)] RateBreakdown WaterRate,
    [field: DurableField(6)] RateBreakdown WarmthRate,
    [field: DurableField(7)] bool Sheltered,
    IReadOnlyList<string> PendingWarnings,
    [field: DurableField(14)] CycleAttributeForecast FoodForecast,
    [field: DurableField(15)] CycleAttributeForecast WaterForecast,
    [field: DurableField(16)] CycleAttributeForecast WarmthForecast,
    [field: DurableField(10)] bool WarmthWarningPending = false,
    [field: DurableField(11)] int PendingWarningMask = 0,
    [field: DurableField(12)] decimal Minimum = CycleSurvival.MinimumValue,
    [field: DurableField(13)] decimal Maximum = CycleSurvival.MaximumValue,
    [field: DurableField(17)] decimal WarningThreshold = (decimal)CycleSurvival.WarningUnits / CycleSurvival.Scale) : IDurableObject {
    public int FoodPerHour => FoodRate.Total;
    public int WaterPerHour => WaterRate.Total;
    public int WarmthPerHour => WarmthRate.Total;
    public ModelTime? FatalAt => new[] { FoodForecast.DepletionAt, WaterForecast.DepletionAt, WarmthForecast.DepletionAt }
        .Where(time => time is not null).Min();
    public void Validate(ModelTime? observedAt = null) {
        RateBreakdown.ValidateSources(FoodRate, WaterRate, WarmthRate);
        ArgumentNullException.ThrowIfNull(FoodForecast);
        ArgumentNullException.ThrowIfNull(WaterForecast);
        ArgumentNullException.ThrowIfNull(WarmthForecast);
        FoodForecast.Validate(observedAt);
        WaterForecast.Validate(observedAt);
        WarmthForecast.Validate(observedAt);
        if (observedAt is { } at) {
            ValidateCurrentThresholds(Food, FoodForecast, at);
            ValidateCurrentThresholds(Water, WaterForecast, at);
            ValidateCurrentThresholds(Warmth, WarmthForecast, at);
        }
        if (Minimum >= Maximum || WarningThreshold <= Minimum || WarningThreshold >= Maximum || Food < Minimum || Food > Maximum || Water < Minimum || Water > Maximum ||
            Warmth < Minimum || Warmth > Maximum) {
            throw new InvalidOperationException("Invalid observed attribute range or value.");
        }
    }
    private void ValidateCurrentThresholds(decimal value, CycleAttributeForecast forecast, ModelTime at) {
        if ((value <= WarningThreshold ? forecast.WarningAt != at : forecast.WarningAt is { } warning && warning <= at) ||
            (value <= Minimum ? forecast.DepletionAt != at : forecast.DepletionAt is { } depletion && depletion <= at)) {
            throw new InvalidOperationException("A forecast must distinguish an already reached threshold from a future crossing.");
        }
    }
    [DurableField(8)] private List<string> _pendingWarnings = new(PendingWarnings);
    public IReadOnlyList<string> PendingWarnings { get => _pendingWarnings.AsReadOnly(); init => _pendingWarnings = new(value); }
}

/// <summary>Application survival policy. Prediction and projection are pure; planning records concrete replacements.</summary>
public static class CycleSurvivalRules {
    /// <summary>Legacy default-content fixture helper. Runtime material must use its bound definition.</summary>
    public static string? UseDescription(string itemId) => itemId switch {
        "item.food" => "消耗 1 份食物，饱食 +35（最高 100）；不耗时。",
        "item.ration" => "消耗 1 份口粮，饱食 +55（最高 100）；不耗时。",
        "item.water" => "消耗 1 份饮水，水分 +45（最高 100）；不耗时。",
        _ => null
    };
    public static string? UseDescription(CycleWorldDefinition definition, string itemId) {
        int food = definition.SupplyFood(itemId), water = definition.SupplyWater(itemId);
        if (food == 0 && water == 0) { return null; }
        var effects = new List<string>();
        if (food > 0) { effects.Add($"饱食 +{food}"); }
        if (water > 0) { effects.Add($"水分 +{water}"); }
        return $"消耗 1 份{definition.Items.GetValueOrDefault(itemId, itemId)}，{string.Join("、", effects)}（最高 {CycleSurvival.MaximumValue}）；不耗时。";
    }
    public static CycleSurvivalObservation? Observe(CycleWorld world, string actorId) {
        CycleCharacter actor = world.GetCharacter(actorId);
        CycleSurvival? anchor = actor.Survival;
        if (anchor is null) { return null; }
        ModelTime observedAt = actor.Ending?.At ?? world.Time;
        CycleSurvival body = anchor.Project(observedAt);
        var warnings = new List<string>();
        foreach ((int mask, string name) in new[] { (1, "饱食"), (2, "水分"), (4, "温暖") }) {
            if ((body.PendingMask & mask) != 0) { warnings.Add($"{name}曾降至警戒线（本组最早提醒于 {body.PendingSince}）。"); }
        }
        bool sheltered = CycleActions.Location(world, actorId) is AtPlaceLocation atPlace && world.Definition.GetPlace(atPlace.PlaceId.Value).Sheltered;
        return new((decimal)body.FoodUnits / CycleSurvival.Scale, (decimal)body.WaterUnits / CycleSurvival.Scale,
            (decimal)body.WarmthUnits / CycleSurvival.Scale, body.FoodRate, body.WaterRate, body.WarmthRate,
            sheltered, warnings.AsReadOnly(),
            ForecastConstant(body.FoodUnits, body.FoodPerHour, observedAt),
            ForecastConstant(body.WaterUnits, body.WaterPerHour, observedAt),
            actor.IsActive ? ForecastWarmth(world.Definition, body.WarmthUnits, observedAt, sheltered) : ForecastConstant(body.WarmthUnits, 0, observedAt),
            (body.PendingMask & 4) != 0, body.PendingMask, CycleSurvival.MinimumValue, CycleSurvival.MaximumValue);
    }

    /// <summary>Only a visible action's change to the shared forecast assumptions; never a duplicate body forecast.</summary>
    /// <remarks>Call only after CycleActions.Check accepts the command; this is not an identity discovery query.</remarks>
    public static string? ActionWarning(CycleWorld world, string actorId, CycleCommand command) {
        CycleSurvival? anchor = world.GetCharacter(actorId).Survival;
        if (anchor is null || command.Kind != CycleCommandKind.Go || !IsSheltered(world.Definition, world.Expedition?.Spatial, actorId)) {
            return null;
        }
        string? place = CycleActions.CurrentPlace(world, actorId);
        if (place is null) { return null; }
        CyclePassageDefinition passage = world.Definition.GetPassages(place).Single(p => p.Id == command.PassageId);
        ModelTime end = new(checked(world.Time.Ticks + passage.DurationTicks));
        CycleSurvival body = anchor.Project(world.Time);
        CycleAttributeForecast exposed = ForecastWarmth(world.Definition, body.WarmthUnits, world.Time, sheltered: false);
        string result = "途中失去庇护，温暖按昼夜野外速率变化；本段移动不能取消。";
        if (world.Expedition?.Environment is not null) { result += " 寒风可造成额外温暖损失。"; }
        if (exposed.WarningAt is { } warning && warning <= end) {
            result += body.WarmthUnits <= CycleSurvival.WarningUnits ? " 温暖已处于警戒区。" : $" 温暖预计降至 {CycleSurvival.WarningUnits / CycleSurvival.Scale}：{warning}。";
        }
        if (exposed.DepletionAt is { } depletion && depletion <= end) { result += $" 温暖预计耗尽至 0：{depletion}。"; }
        return result;
    }

    internal static CycleAttributeForecast ForecastConstant(long value, int rate, ModelTime at) =>
        new(Due(value, rate, CycleSurvival.WarningUnits, at), Due(value, rate, 0, at));

    /// <summary>Independent warmth estimate, unaffected by food or water depletion and warning acknowledgement.</summary>
    internal static CycleAttributeForecast ForecastWarmth(long value, ModelTime start, bool sheltered) =>
        ForecastWarmth(value, start, sheltered, ShelteredWarmth.Total, DayWarmth.Total, NightWarmth.Total);
    internal static CycleAttributeForecast ForecastWarmth(CycleWorldDefinition definition, long value, ModelTime start, bool sheltered) =>
        ForecastWarmth(value, start, sheltered, definition.Body.ShelteredWarmthPerHour,
            definition.Body.DayWarmthPerHour, definition.Body.NightWarmthPerHour);

    private static CycleAttributeForecast ForecastWarmth(long value, ModelTime start, bool sheltered, int shelteredRate, int dayRate, int nightRate) {
        if (sheltered) { return ForecastConstant(value, shelteredRate, start); }
        ModelTime cursor = start;
        ModelTime? firstWarning = null;
        var previousBoundaryValues = new Dictionary<bool, long>();
        // Each complete day is a monotone map of warmth. If a repeated dawn/dusk has not
        // decreased, future cycles cannot introduce a lower threshold crossing. Otherwise
        // a negative daily drift is at least 12 points (integer rates and two 12-hour phases),
        // so the bounded 0..100 range reaches depletion in finitely many cycles. Capping may
        // lower the first cycle's value; a subsequent fixed cycle is detected the same way.
        while (true) {
            bool day = CycleTime.IsDay(cursor);
            long phase = cursor.Ticks % CycleTime.Day;
            if (phase == 6 * CycleTime.Hour || phase == 18 * CycleTime.Hour) {
                if (previousBoundaryValues.TryGetValue(day, out long previous) && value >= previous) {
                    return new(firstWarning, null);
                }
                previousBoundaryValues[day] = value;
            }
            int rate = day ? dayRate : nightRate;
            Int128 nextLight = (Int128)(cursor.Ticks - phase) + CycleTime.NextLightChange(new(phase)).Ticks;
            ModelTime next = new((long)Int128.Min(long.MaxValue, nextLight));
            if (firstWarning is null && Due(value, rate, CycleSurvival.WarningUnits, cursor) is { } warning && warning <= next) {
                firstWarning = warning;
            }
            if (Due(value, rate, 0, cursor) is { } depletion && depletion <= next) { return new(firstWarning, depletion); }
            if (next.Ticks == long.MaxValue) { return new(firstWarning, null); }
            value = (long)Int128.Clamp((Int128)value + (Int128)rate * (next.Ticks - cursor.Ticks), 0, CycleSurvival.MaximumUnits);
            cursor = next;
        }
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
            changedCharacters: [fatal ? actor.WithSurvival(body).End(CycleExpeditionOutcome.Dead, prediction.Due,
                $"{actor.Name}因{reason}而未能走出山林。", CycleActions.Location(world, actor.Id)!) : actor.Change(actor.Activity, result: reason, survival: body)],
            result: reason, spatialFacts: fatal ? [new EntityRemovedFact(new(actor.Id))] : []);
    }

    /// <summary>Changes anchors only while planning; recorded facts contain all effects needed for recovery.</summary>
    internal static CycleFact Enrich(CycleWorld world, CycleFact fact) {
        fact = CycleFollowingRules.CleanupEndings(world, fact);
        var changes = fact.ChangedCharacters.ToDictionary(c => c.Id, StringComparer.Ordinal);
        GraphSpatialState? spatial = world.Expedition?.Spatial;
        if (spatial is not null) {
            foreach (GraphSpatialFact movement in fact.SpatialFacts) { spatial = world.Definition.Runtime.Reducer.Apply(spatial, new(fact.At, 0), movement); }
        }
        bool light = fact.Light?.IsDay ?? world.Light.IsDay;
        foreach (CycleCharacter previous in world.Characters) {
            if (!previous.IsActive || previous.Survival is null || !changes.ContainsKey(previous.Id) && fact.Kind != CycleEventKind.LightChanged) { continue; }
            CycleCharacter actor = changes.GetValueOrDefault(previous.Id, previous);
            if (!actor.IsActive) { continue; }
            // Communication changes neither exposure nor physiology rules. Preserve the current causes,
            // including valid scenario-specific rate decompositions, while reanchoring elapsed values.
            CycleSurvival body = fact.Kind is CycleEventKind.SpeechSent or CycleEventKind.ItemGiven or CycleEventKind.SpeechShouted or CycleEventKind.MoneyGiven or CycleEventKind.MarketBought or CycleEventKind.MarketSold or
                CycleEventKind.FollowStarted or CycleEventKind.FollowEnded or CycleEventKind.FollowersDismissed or
                CycleEventKind.TradeOffered or CycleEventKind.TradeAccepted or CycleEventKind.TradeRejected or CycleEventKind.TradeWithdrawn or CycleEventKind.TradesExpired
                ? actor.Survival!.Project(fact.At)
                : actor.Survival!.WithRates(fact.At, actor.Survival.FoodRate, actor.Survival.WaterRate,
                    WarmthRate(world.Definition, IsSheltered(world.Definition, spatial, actor.Id), light));
            if (fact.LastServedActorId == actor.Id && fact.Kind is CycleEventKind.AttentionAcknowledged or CycleEventKind.ActivityCancelled or
                CycleEventKind.WaitStarted or CycleEventKind.GatherStarted or CycleEventKind.ProductionStarted or CycleEventKind.MoveStarted or CycleEventKind.FollowStarted) { body = body.Acknowledge(fact.At); }
            changes[actor.Id] = changes.ContainsKey(actor.Id) ? actor.WithSurvival(body) : actor.Change(actor.Activity, survival: body);
        }
        return changes.Count == 0 ? fact : fact.WithCharacters(world.Characters.Where(c => changes.ContainsKey(c.Id)).Select(c => changes[c.Id]));
    }
    internal static void Validate(CycleBusinessState business) {
        foreach (CycleCharacter actor in business.Characters) {
            if (actor.Survival is not { } body) { continue; }
            if (business.Expedition is null) { throw new InvalidOperationException("Survival needs require an expedition."); }
            if (actor.IsActive &&
                NextThreshold(body, body.BaselineAt) is { } threshold && threshold.Due < business.Time) {
                throw new InvalidOperationException("An unprocessed survival threshold precedes the completed boundary.");
            }
            if (actor.Ending?.Outcome == CycleExpeditionOutcome.Dead && (!body.Project(business.Time).IsFatal ||
                string.IsNullOrWhiteSpace(actor.Ending.Reason))) {
                throw new InvalidOperationException("Death requires recorded depletion and a reason.");
            }
            if (actor.Ending?.Outcome == CycleExpeditionOutcome.Escaped && body.IsFatal) {
                throw new InvalidOperationException("A depleted character cannot escape at the same boundary.");
            }
        }
    }
    internal static bool IsSheltered(GraphSpatialState? spatial, string actorId) =>
        spatial?.Entities.SingleOrDefault(e => e.Id.Value == actorId)?.Location is AtPlaceLocation place && CycleExpeditionMap.GetPlace(place.PlaceId.Value).Sheltered;
    internal static bool IsSheltered(CycleWorldDefinition definition, GraphSpatialState? spatial, string actorId) =>
        spatial?.Entities.SingleOrDefault(e => e.Id.Value == actorId)?.Location is AtPlaceLocation place && definition.GetPlace(place.PlaceId.Value).Sheltered;
    public static RateBreakdown FoodRate { get; } = new([new("metabolism", "基础代谢", -5)]);
    public static RateBreakdown WaterRate { get; } = new([new("metabolism", "基础代谢", -8)]);
    public static RateBreakdown WarmthRate(bool sheltered, bool day) => sheltered ? ShelteredWarmth : day ? DayWarmth : NightWarmth;
    public static RateBreakdown WarmthRate(CycleWorldDefinition definition, bool sheltered, bool day) => sheltered
        ? new([new("shelter-recovery", "庇护内回暖", definition.Body.ShelteredWarmthPerHour)])
        : day ? new([new("day-outdoors", "白天野外回暖", definition.Body.DayWarmthPerHour)])
        : new([new("night-outdoors", "夜间野外散热", definition.Body.NightWarmthPerHour)]);
    private static RateBreakdown ShelteredWarmth { get; } = new([new("shelter-recovery", "庇护内回暖", 12)]);
    private static RateBreakdown DayWarmth { get; } = new([new("day-outdoors", "白天野外回暖", 6)]);
    private static RateBreakdown NightWarmth { get; } = new([new("night-outdoors", "夜间野外散热", -12)]);
    private static ModelTime? Due(long value, int rate, long target, ModelTime at) {
        if (value <= target) { return at; }
        if (rate >= 0) { return null; }
        long decline = -(long)rate;
        long ticks = (value - target + decline - 1) / decline;
        return at.Ticks > long.MaxValue - ticks ? null : new(at.Ticks + ticks);
    }
}
