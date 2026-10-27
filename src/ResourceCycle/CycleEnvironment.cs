using Atelia.DurableGraph;
using DramaBoard.Kernel.Random;
using DramaBoard.Kernel.Time;

namespace DramaBoard.ResourceCycle;

/// <summary>One weather stream and the last experienced result for this single-character scenario.</summary>
/// <remarks>Seed and sequence are the complete random address; queries never consume it.</remarks>
[DurableType("DramaBoard.ResourceCycle.Environment", 1)]
public sealed partial class CycleEnvironment : IDurableObject {
    [DurableField(1)] private ulong _seed;
    [DurableField(2)] private long _sequence;
    [DurableField(3)] private ModelTime? _nextDue;
    [DurableField(4)] private ModelTime? _lastAt;
    [DurableField(5)] private bool _lastCold;
    [DurableField(6)] private long _lastWarmthLossUnits;
    [DurableField(7)] private bool _lastSheltered;
    public ulong Seed => _seed;
    public long Sequence => _sequence;
    public ModelTime? NextDue => _nextDue;
    public ModelTime? LastAt => _lastAt;
    public bool LastCold => _lastCold;
    public long LastWarmthLossUnits => _lastWarmthLossUnits;
    public bool LastSheltered => _lastSheltered;
    public CycleEnvironment(ulong seed, long sequence, ModelTime? nextDue, ModelTime? lastAt = null,
        bool lastCold = false, long lastWarmthLossUnits = 0, bool lastSheltered = false) {
        _seed = seed; _sequence = sequence; _nextDue = nextDue; _lastAt = lastAt;
        _lastCold = lastCold; _lastWarmthLossUnits = lastWarmthLossUnits; _lastSheltered = lastSheltered;
    }
    internal void Validate(ModelTime at) {
        if (Sequence < 0 || NextDue < at || Sequence == long.MaxValue && NextDue is not null || (Sequence == 0) != (LastAt is null) ||
            NextDue is null && Sequence != long.MaxValue && (LastAt ?? at).Ticks <= long.MaxValue - CycleEnvironmentRules.IntervalTicks ||
            LastAt is { } last && (last.Ticks < 0 || last > at || NextDue is { } next && next.Ticks - last.Ticks != CycleEnvironmentRules.IntervalTicks) ||
            LastWarmthLossUnits < 0 || LastWarmthLossUnits > CycleEnvironmentRules.ExposureLoss * CycleSurvival.Scale ||
            (!LastCold || LastSheltered) && LastWarmthLossUnits != 0 ||
            LastAt is null && (LastCold || LastSheltered || LastWarmthLossUnits != 0)) {
            throw new InvalidOperationException("Invalid weather schedule or recorded result.");
        }
    }
    public override string ToString() => $"{Seed}:{Sequence}:{NextDue?.Ticks}:{LastAt?.Ticks}:{LastCold}:{LastWarmthLossUnits}:{LastSheltered}";
}

public enum CycleWeatherKind { Calm, ColdWind }
public sealed record CycleWeatherObservation(ModelTime? NextWindow, long IntervalTicks, decimal ExposedWarmthLoss,
    bool Sheltered, ModelTime? LastAt, CycleWeatherKind? LastKind, decimal LastActualWarmthLoss, bool LastWasSheltered);

/// <summary>A fixed application weather rule. Only planning samples; recovery applies recorded replacements.</summary>
public static class CycleEnvironmentRules {
    public const long IntervalTicks = 2 * CycleTime.Hour;
    public const int ExposureLoss = 10;
    public const ulong DefaultSeed = 20261028UL;
    public static readonly ulong StreamId = DeterministicRandom.DeriveStreamId("resource-cycle/weather/v1");
    public static CycleWeatherObservation? Observe(CycleWorld world, string actorId) {
        CycleEnvironment? environment = world.Expedition?.Environment;
        if (environment is null) { return null; }
        // This prototype's environment result belongs to its sole participant, not an arbitrary remote character.
        _ = world.GetCharacter(actorId);
        return new(environment.NextDue, IntervalTicks, ExposureLoss, CycleSurvivalRules.IsSheltered(world.Expedition!.Spatial, actorId),
            environment.LastAt, environment.LastAt is null ? null : environment.LastCold ? CycleWeatherKind.ColdWind : CycleWeatherKind.Calm,
            (decimal)environment.LastWarmthLossUnits / CycleSurvival.Scale, environment.LastSheltered);
    }
    internal static CycleFact Plan(CycleWorld world, ModelTime at) {
        CycleEnvironment previous = world.Expedition!.Environment!;
        if (previous.NextDue != at) { throw new InvalidOperationException("Weather must resolve its recorded window."); }
        CycleCharacter actor = world.Characters.Single();
        bool sheltered = CycleSurvivalRules.IsSheltered(world.Expedition.Spatial, actor.Id);
        CycleSurvival integrated = actor.Survival!.Project(at);
        if (integrated.IsFatal) { throw new InvalidOperationException("Continuous depletion must settle before weather at the same boundary."); }
        bool cold = DeterministicRandom.SampleInt32(previous.Seed, StreamId, checked((ulong)previous.Sequence), 0, 2) == 1;
        long loss = cold && !sheltered ? Math.Min(integrated.WarmthUnits, ExposureLoss * CycleSurvival.Scale) : 0;
        CycleSurvival body = integrated.LoseWarmth(at, loss);
        long nextSequence = checked(previous.Sequence + 1);
        ModelTime? nextDue = nextSequence == long.MaxValue || at.Ticks > long.MaxValue - IntervalTicks ? null : new(at.Ticks + IntervalTicks);
        var environment = new CycleEnvironment(previous.Seed, nextSequence, nextDue,
            at, cold, loss, sheltered);
        bool fatal = body.IsFatal;
        string result = !cold ? "这次天气窗口平静，没有额外降温。" : sheltered
            ? "寒风掠过；你在庇护中，没有损失温暖。" : $"寒风袭来，温暖减少 {(decimal)loss / CycleSurvival.Scale:0.##}。";
        string? ending = fatal ? $"{actor.Name}在寒风中失温，未能走出山林。" : null;
        return new(CycleEventKind.WeatherResolved, at, actor.Id,
            changedCharacters: [actor.Change(actor.Activity, result: result, survival: body)], result: result,
            outcome: fatal ? CycleExpeditionOutcome.Dead : null, endingReason: ending, environment: environment);
    }
}
