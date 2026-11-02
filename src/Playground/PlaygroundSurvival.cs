using Atelia.DurableGraph;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Playground;

/// <summary>One character's anchored, piecewise linear needs. A point is Scale units; rates are units per tick.</summary>
[DurableType("DramaBoard.Playground.Survival", 1)]
public sealed partial class PlaygroundSurvival : IDurableObject {
    public const long Scale = PlaygroundTime.Hour;
    public const int MinimumValue = 0;
    public const int MaximumValue = 100;
    public const long MaximumUnits = MaximumValue * Scale;
    public const long WarningUnits = 25 * Scale;
    [DurableField(1)] private ModelTime _baselineAt;
    [DurableField(2)] private long _foodUnits;
    [DurableField(3)] private long _waterUnits;
    [DurableField(4)] private long _warmthUnits;
    [DurableField(5)] private RateBreakdown _foodRate;
    [DurableField(6)] private RateBreakdown _waterRate;
    [DurableField(7)] private RateBreakdown _warmthRate;
    [DurableField(8)] private int _warnedMask;
    [DurableField(9)] private int _pendingMask;
    [DurableField(10)] private ModelTime? _pendingSince;
    public ModelTime BaselineAt => _baselineAt;
    public long FoodUnits => _foodUnits;
    public long WaterUnits => _waterUnits;
    public long WarmthUnits => _warmthUnits;
    public RateBreakdown FoodRate => _foodRate;
    public int FoodPerHour => FoodRate.Total;
    public RateBreakdown WaterRate => _waterRate;
    public int WaterPerHour => WaterRate.Total;
    public RateBreakdown WarmthRate => _warmthRate;
    public int WarmthPerHour => WarmthRate.Total;
    public int WarnedMask => _warnedMask;
    public int PendingMask => _pendingMask;
    public ModelTime? PendingSince => _pendingSince;
    public bool IsFatal => FoodUnits == 0 || WaterUnits == 0 || WarmthUnits == 0;
    public PlaygroundSurvival(ModelTime baselineAt, long foodUnits, long waterUnits, long warmthUnits,
        RateBreakdown foodRate, RateBreakdown waterRate, RateBreakdown warmthRate,
        int warnedMask = 0, int pendingMask = 0, ModelTime? pendingSince = null) {
        _baselineAt = baselineAt; _foodUnits = foodUnits; _waterUnits = waterUnits; _warmthUnits = warmthUnits;
        ArgumentNullException.ThrowIfNull(foodRate);
        ArgumentNullException.ThrowIfNull(waterRate);
        ArgumentNullException.ThrowIfNull(warmthRate);
        _foodRate = foodRate; _waterRate = waterRate; _warmthRate = warmthRate;
        _warnedMask = warnedMask; _pendingMask = pendingMask; _pendingSince = pendingSince;
        Validate();
    }
    /// <summary>Creates initial outdoor needs using the current calendar; callers may supply an explicit warmth cause.</summary>
    public static PlaygroundSurvival Create(ModelTime time, int food = 65, int water = 65, int warmth = 60, RateBreakdown? warmthRate = null) =>
        new(time, checked(food * Scale), checked(water * Scale), checked(warmth * Scale),
            PlaygroundSurvivalRules.FoodRate, PlaygroundSurvivalRules.WaterRate,
            warmthRate ?? PlaygroundSurvivalRules.WarmthRate(false, PlaygroundTime.IsDay(time)));
    /// <summary>Creates a body using the effective world's bound physiology and initial exposure.</summary>
    public static PlaygroundSurvival Create(PlaygroundWorldDefinition definition, ModelTime time, int food, int water, int warmth, bool sheltered) =>
        new(time, checked(food * Scale), checked(water * Scale), checked(warmth * Scale),
            new([new("metabolism", "基础代谢", definition.Body.FoodPerHour)]),
            new([new("metabolism", "基础代谢", definition.Body.WaterPerHour)]),
            PlaygroundSurvivalRules.WarmthRate(definition, sheltered, PlaygroundTime.IsDay(time)));
    public PlaygroundSurvival Project(ModelTime at) {
        if (at < BaselineAt) { throw new ArgumentOutOfRangeException(nameof(at)); }
        long elapsed = at.Ticks - BaselineAt.Ticks;
        long food = Integrate(FoodUnits, FoodPerHour, elapsed), water = Integrate(WaterUnits, WaterPerHour, elapsed), warmth = Integrate(WarmthUnits, WarmthPerHour, elapsed);
        int armed = WarnedMask & ((food <= WarningUnits ? 1 : 0) | (water <= WarningUnits ? 2 : 0) | (warmth <= WarningUnits ? 4 : 0));
        return new(at, food, water, warmth, FoodRate, WaterRate, WarmthRate, armed, PendingMask, PendingSince);
    }
    public PlaygroundSurvival WithRates(ModelTime at, RateBreakdown foodRate, RateBreakdown waterRate, RateBreakdown warmthRate) {
        PlaygroundSurvival value = Project(at);
        return new(at, value.FoodUnits, value.WaterUnits, value.WarmthUnits, foodRate, waterRate, warmthRate,
            value.WarnedMask, value.PendingMask, value.PendingSince);
    }
    /// <summary>Legacy fixture helper. Runtime callers supply their effective world definition.</summary>
    public PlaygroundSurvival Supply(ModelTime at, string itemId) =>
        Supply(at, LivingWorldEconomyContent.SupplyFood(itemId), LivingWorldEconomyContent.SupplyWater(itemId));
    public PlaygroundSurvival Supply(ModelTime at, string itemId, PlaygroundWorldDefinition definition) =>
        Supply(at, definition.SupplyFood(itemId), definition.SupplyWater(itemId));
    private PlaygroundSurvival Supply(ModelTime at, int foodGain, int waterGain) {
        PlaygroundSurvival value = Project(at);
        if (value.IsFatal) { throw new InvalidOperationException("A depleted vital need cannot be revived by same-boundary consumption."); }
        long food = value.FoodUnits, water = value.WaterUnits, warmth = value.WarmthUnits;
        if (foodGain == 0 && waterGain == 0) { throw new ArgumentException("Unknown survival supply."); }
        food = Math.Min(MaximumUnits, food + foodGain * Scale);
        water = Math.Min(MaximumUnits, water + waterGain * Scale);
        return new PlaygroundSurvival(at, food, water, warmth, FoodRate, WaterRate, WarmthRate,
            value.WarnedMask, value.PendingMask, value.PendingSince).Project(at);
    }
    public PlaygroundSurvival Acknowledge(ModelTime at) {
        PlaygroundSurvival value = Project(at);
        return new(at, value.FoodUnits, value.WaterUnits, value.WarmthUnits, FoodRate, WaterRate, WarmthRate, value.WarnedMask);
    }
    /// <summary>Plans an instantaneous exposure loss after integrating the preceding segment.</summary>
    internal PlaygroundSurvival LoseWarmth(ModelTime at, long units) {
        ArgumentOutOfRangeException.ThrowIfNegative(units);
        PlaygroundSurvival value = Project(at);
        return new(at, value.FoodUnits, value.WaterUnits, Math.Max(0, value.WarmthUnits - units),
            value.FoodRate, value.WaterRate, value.WarmthRate, value.WarnedMask, value.PendingMask, value.PendingSince);
    }
    /// <summary>Ends all continuous needs at a recorded personal endpoint.</summary>
    internal PlaygroundSurvival Freeze(ModelTime at) {
        PlaygroundSurvival value = Project(at);
        var stopped = new RateBreakdown([new("journey-ended", "个人旅程已结束", 0)]);
        return new(at, value.FoodUnits, value.WaterUnits, value.WarmthUnits, stopped, stopped, stopped, value.WarnedMask);
    }
    public PlaygroundSurvival Warn(ModelTime at) {
        PlaygroundSurvival value = Project(at);
        int low = (value.FoodUnits <= WarningUnits ? 1 : 0) | (value.WaterUnits <= WarningUnits ? 2 : 0) | (value.WarmthUnits <= WarningUnits ? 4 : 0);
        int newlyWarned = low & ~value.WarnedMask;
        return new(at, value.FoodUnits, value.WaterUnits, value.WarmthUnits, FoodRate, WaterRate, WarmthRate,
            value.WarnedMask | newlyWarned, value.PendingMask | newlyWarned,
            value.PendingSince ?? (newlyWarned == 0 ? null : at));
    }
    public void Validate() {
        RateBreakdown.ValidateSources(FoodRate, WaterRate, WarmthRate);
        if (BaselineAt.Ticks < 0 || FoodUnits < 0 || FoodUnits > MaximumUnits || WaterUnits < 0 || WaterUnits > MaximumUnits ||
            WarmthUnits < 0 || WarmthUnits > MaximumUnits || (WarnedMask & ~7) != 0 || (PendingMask & ~7) != 0 ||
            (PendingMask == 0) != (PendingSince is null) || PendingSince is { } since && (since.Ticks < 0 || since > BaselineAt)) {
            throw new InvalidOperationException("Invalid survival anchor or warning state.");
        }
    }
    private static long Integrate(long value, int rate, long elapsed) =>
        (long)Int128.Clamp((Int128)value + (Int128)rate * elapsed, 0, MaximumUnits);
    public override string ToString() => $"{BaselineAt.Ticks}:{FoodUnits}:{WaterUnits}:{WarmthUnits}:{FoodPerHour}:{WaterPerHour}:{WarmthPerHour}:{WarnedMask}:{PendingMask}:{PendingSince?.Ticks}";
}
