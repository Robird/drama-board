using Atelia.DurableGraph;
using DramaBoard.Kernel.Time;

namespace DramaBoard.ResourceCycle;

/// <summary>One character's anchored, piecewise linear needs. A point is Scale units; rates are units per tick.</summary>
[DurableType("DramaBoard.ResourceCycle.Survival", 1)]
public sealed partial class CycleSurvival : IDurableObject {
    public const long Scale = CycleTime.Hour;
    public const long MaximumUnits = 100 * Scale;
    public const long WarningUnits = 25 * Scale;
    [DurableField(1)] private ModelTime _baselineAt;
    [DurableField(2)] private long _foodUnits;
    [DurableField(3)] private long _waterUnits;
    [DurableField(4)] private long _warmthUnits;
    [DurableField(5)] private int _foodPerHour;
    [DurableField(6)] private int _waterPerHour;
    [DurableField(7)] private int _warmthPerHour;
    [DurableField(8)] private int _warnedMask;
    [DurableField(9)] private int _pendingMask;
    [DurableField(10)] private ModelTime? _pendingSince;
    public ModelTime BaselineAt => _baselineAt;
    public long FoodUnits => _foodUnits;
    public long WaterUnits => _waterUnits;
    public long WarmthUnits => _warmthUnits;
    public int FoodPerHour => _foodPerHour;
    public int WaterPerHour => _waterPerHour;
    public int WarmthPerHour => _warmthPerHour;
    public int WarnedMask => _warnedMask;
    public int PendingMask => _pendingMask;
    public ModelTime? PendingSince => _pendingSince;
    public bool IsFatal => FoodUnits == 0 || WaterUnits == 0 || WarmthUnits == 0;
    public CycleSurvival(ModelTime baselineAt, long foodUnits, long waterUnits, long warmthUnits,
        int foodPerHour = -5, int waterPerHour = -8, int warmthPerHour = 6,
        int warnedMask = 0, int pendingMask = 0, ModelTime? pendingSince = null) {
        _baselineAt = baselineAt; _foodUnits = foodUnits; _waterUnits = waterUnits; _warmthUnits = warmthUnits;
        _foodPerHour = foodPerHour; _waterPerHour = waterPerHour; _warmthPerHour = warmthPerHour;
        _warnedMask = warnedMask; _pendingMask = pendingMask; _pendingSince = pendingSince;
        Validate();
    }
    public static CycleSurvival Create(ModelTime time, int food = 65, int water = 65, int warmth = 60, int warmthPerHour = 6) =>
        new(time, checked(food * Scale), checked(water * Scale), checked(warmth * Scale), warmthPerHour: warmthPerHour);
    public CycleSurvival Project(ModelTime at) {
        if (at < BaselineAt) { throw new ArgumentOutOfRangeException(nameof(at)); }
        long elapsed = at.Ticks - BaselineAt.Ticks;
        long food = Integrate(FoodUnits, FoodPerHour, elapsed), water = Integrate(WaterUnits, WaterPerHour, elapsed), warmth = Integrate(WarmthUnits, WarmthPerHour, elapsed);
        int armed = WarnedMask & ((food <= WarningUnits ? 1 : 0) | (water <= WarningUnits ? 2 : 0) | (warmth <= WarningUnits ? 4 : 0));
        return new(at, food, water, warmth, FoodPerHour, WaterPerHour, WarmthPerHour, armed, PendingMask, PendingSince);
    }
    public CycleSurvival WithRates(ModelTime at, int foodPerHour, int waterPerHour, int warmthPerHour) {
        CycleSurvival value = Project(at);
        return new(at, value.FoodUnits, value.WaterUnits, value.WarmthUnits, foodPerHour, waterPerHour, warmthPerHour,
            value.WarnedMask, value.PendingMask, value.PendingSince);
    }
    public CycleSurvival Supply(ModelTime at, string itemId) {
        CycleSurvival value = Project(at);
        if (value.IsFatal) { throw new InvalidOperationException("A depleted vital need cannot be revived by same-boundary consumption."); }
        long food = value.FoodUnits, water = value.WaterUnits, warmth = value.WarmthUnits;
        switch (itemId) {
            case "item.food": food = Math.Min(MaximumUnits, food + 35 * Scale); break;
            case "item.water": water = Math.Min(MaximumUnits, water + 45 * Scale); break;
            default: throw new ArgumentException("Unknown survival supply.", nameof(itemId));
        }
        return new CycleSurvival(at, food, water, warmth, FoodPerHour, WaterPerHour, WarmthPerHour,
            value.WarnedMask, value.PendingMask, value.PendingSince).Project(at);
    }
    public CycleSurvival Acknowledge(ModelTime at) {
        CycleSurvival value = Project(at);
        return new(at, value.FoodUnits, value.WaterUnits, value.WarmthUnits, FoodPerHour, WaterPerHour, WarmthPerHour, value.WarnedMask);
    }
    public CycleSurvival Warn(ModelTime at) {
        CycleSurvival value = Project(at);
        int low = (value.FoodUnits <= WarningUnits ? 1 : 0) | (value.WaterUnits <= WarningUnits ? 2 : 0) | (value.WarmthUnits <= WarningUnits ? 4 : 0);
        int newlyWarned = low & ~value.WarnedMask;
        return new(at, value.FoodUnits, value.WaterUnits, value.WarmthUnits, FoodPerHour, WaterPerHour, WarmthPerHour,
            value.WarnedMask | newlyWarned, value.PendingMask | newlyWarned,
            value.PendingSince ?? (newlyWarned == 0 ? null : at));
    }
    public void Validate() {
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
