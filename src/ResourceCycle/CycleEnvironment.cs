using Atelia.DurableGraph;
using DramaBoard.Kernel.Random;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.ResourceCycle;

/// <summary>One participant's recorded effect of the shared weather window.</summary>
[DurableType("DramaBoard.ResourceCycle.WeatherEffect", 1)]
public sealed partial record CycleWeatherEffect(
    [field: DurableField(1)] string ActorId,
    [field: DurableField(2)] long WarmthLossUnits,
    [field: DurableField(3)] bool Sheltered) : IDurableObject;

/// <summary>One shared weather stream and the latest concrete per-character effects.</summary>
/// <remarks>Seed and sequence are the complete random address; queries never consume it.</remarks>
[DurableType("DramaBoard.ResourceCycle.Environment", 2)]
public sealed partial class CycleEnvironment : IDurableObject {
    [DurableField(1)] private ulong _seed;
    [DurableField(2)] private long _sequence;
    [DurableField(3)] private ModelTime? _nextDue;
    [DurableField(4)] private ModelTime? _lastAt;
    [DurableField(5)] private bool _lastCold;
    [DurableField(8)] private List<CycleWeatherEffect> _lastEffects;
    public ulong Seed => _seed;
    public long Sequence => _sequence;
    public ModelTime? NextDue => _nextDue;
    public ModelTime? LastAt => _lastAt;
    public bool LastCold => _lastCold;
    public IReadOnlyList<CycleWeatherEffect> LastEffects => _lastEffects.AsReadOnly();
    public CycleEnvironment(ulong seed, long sequence, ModelTime? nextDue, ModelTime? lastAt = null,
        bool lastCold = false, IEnumerable<CycleWeatherEffect>? lastEffects = null) {
        _seed = seed; _sequence = sequence; _nextDue = nextDue; _lastAt = lastAt;
        _lastCold = lastCold; _lastEffects = lastEffects?.OrderBy(e => e.ActorId, StringComparer.Ordinal).ToList() ?? [];
    }
    /// <summary>Legacy fixture shape check. Runtime validation also binds weather to the effective definition.</summary>
    internal void Validate(ModelTime at, IReadOnlyList<CycleCharacter> characters) =>
        Validate(at, characters, CycleEnvironmentRules.IntervalTicks, CycleEnvironmentRules.ExposureLoss);
    internal void Validate(ModelTime at, IReadOnlyList<CycleCharacter> characters, CycleWorldDefinition definition) {
        if (!definition.Weather.Enabled || Seed != definition.Weather.Seed) {
            throw new InvalidOperationException("Weather state does not match its bound content definition.");
        }
        Validate(at, characters, definition.Weather.IntervalTicks, definition.Weather.ExposureLoss);
    }
    private void Validate(ModelTime at, IReadOnlyList<CycleCharacter> characters, long intervalTicks, int exposureLoss) {
        if (Sequence < 0 || NextDue < at || Sequence == long.MaxValue && NextDue is not null || (Sequence == 0) != (LastAt is null) ||
            NextDue is null && Sequence != long.MaxValue && (LastAt ?? at).Ticks <= long.MaxValue - intervalTicks ||
            LastAt is { } last && (last.Ticks < 0 || last > at || NextDue is { } next && next.Ticks - last.Ticks != intervalTicks) ||
            LastAt is null && (LastCold || LastEffects.Count != 0) || LastAt is not null && LastEffects.Count == 0 ||
            LastEffects.Select(e => e.ActorId).Distinct(StringComparer.Ordinal).Count() != LastEffects.Count ||
            LastEffects.Any(e => !characters.Any(c => c.Id == e.ActorId) || e.WarmthLossUnits < 0 ||
                e.WarmthLossUnits > exposureLoss * CycleSurvival.Scale || (!LastCold || e.Sheltered) && e.WarmthLossUnits != 0)) {
            throw new InvalidOperationException("Invalid weather schedule or recorded per-character results.");
        }
    }
    public override string ToString() => $"{Seed}:{Sequence}:{NextDue?.Ticks}:{LastAt?.Ticks}:{LastCold}:[{string.Join(';', LastEffects)}]";
}

[DurableType("DramaBoard.ResourceCycle.WeatherKind", 1)]
public enum CycleWeatherKind { Calm, ColdWind }
[DurableType("DramaBoard.ResourceCycle.WeatherObservation", 1)]
public sealed partial record CycleWeatherObservation(
    [field: DurableField(1)] ModelTime? NextWindow,
    [field: DurableField(2)] long IntervalTicks,
    [field: DurableField(3)] decimal ExposedWarmthLoss,
    [field: DurableField(4)] bool Sheltered,
    [field: DurableField(5)] ModelTime? LastAt,
    [field: DurableField(6)] CycleWeatherKind? LastKind,
    [field: DurableField(7)] decimal LastActualWarmthLoss,
    [field: DurableField(8)] bool LastWasSheltered) : IDurableObject;

/// <summary>Bound-content weather rules. Only planning samples; recovery applies recorded replacements.</summary>
public static class CycleEnvironmentRules {
    // Default-content constants remain for explicit legacy fixtures; runtime uses world.Definition.Weather.
    public const long IntervalTicks = 2 * CycleTime.Hour;
    public const int ExposureLoss = 10;
    public const ulong DefaultSeed = 20261028UL;
    public static readonly ulong StreamId = DeterministicRandom.DeriveStreamId("resource-cycle/weather/v1");
    public static CycleWeatherObservation? Observe(CycleWorld world, string actorId) {
        if (world.Business.InteractionStates[actorId].CurrentContext is { Status: CharacterContextStatus.Terminal } terminal) {
            return terminal.LatestObservation.Weather;
        }
        CycleEnvironment? environment = world.Expedition?.Environment;
        if (environment is null) { return null; }
        CycleCharacter actor = world.GetCharacter(actorId);
        CycleWeatherEffect? effect = environment.LastEffects.SingleOrDefault(e => e.ActorId == actorId);
        bool sheltered = CycleActions.Location(world, actorId) is AtPlaceLocation place && world.Definition.GetPlace(place.PlaceId.Value).Sheltered;
        return new(actor.IsActive ? environment.NextDue : null, world.Definition.Weather.IntervalTicks, world.Definition.Weather.ExposureLoss, sheltered,
            effect is null ? null : environment.LastAt, effect is null ? null : environment.LastCold ? CycleWeatherKind.ColdWind : CycleWeatherKind.Calm,
            (decimal)(effect?.WarmthLossUnits ?? 0) / CycleSurvival.Scale, effect?.Sheltered ?? false);
    }
    internal static CycleFact Plan(CycleWorld world, ModelTime at) {
        CycleEnvironment previous = world.Expedition!.Environment!;
        CycleWeatherDefinition weather = world.Definition.Weather;
        if (previous.NextDue != at) { throw new InvalidOperationException("Weather must resolve its recorded window."); }
        // The upper tail preserves the original default 1/2 rule (sample == 1).
        bool cold = DeterministicRandom.SampleInt32(previous.Seed, StreamId, checked((ulong)previous.Sequence),
            0, weather.ColdProbabilityDenominator) >= weather.ColdProbabilityDenominator - weather.ColdProbabilityNumerator;
        var changes = new List<CycleCharacter>();
        var effects = new List<CycleWeatherEffect>();
        var spatial = new List<GraphSpatialFact>();
        foreach (CycleCharacter actor in world.Characters.Where(c => c.IsActive).OrderBy(c => c.Id, StringComparer.Ordinal)) {
            bool sheltered = CycleSurvivalRules.IsSheltered(world.Definition, world.Expedition.Spatial, actor.Id);
            CycleSurvival integrated = actor.Survival!.Project(at);
            if (integrated.IsFatal) { throw new InvalidOperationException("Continuous depletion must settle before weather at the same boundary."); }
            long loss = cold && !sheltered ? Math.Min(integrated.WarmthUnits, weather.ExposureLoss * CycleSurvival.Scale) : 0;
            CycleSurvival body = integrated.LoseWarmth(at, loss);
            effects.Add(new(actor.Id, loss, sheltered));
            string result = !cold ? "这次天气窗口平静，没有额外降温。" : sheltered
                ? "寒风掠过；你在庇护中，没有损失温暖。" : $"寒风袭来，温暖减少 {(decimal)loss / CycleSurvival.Scale:0.##}。";
            if (body.IsFatal) {
                changes.Add(actor.WithSurvival(body).End(CycleExpeditionOutcome.Dead, at,
                    $"{actor.Name}在寒风中失温，未能走出山林。", CycleActions.Location(world, actor.Id)!));
                spatial.Add(new EntityRemovedFact(new(actor.Id)));
            } else { changes.Add(actor.Change(actor.Activity, result: result, survival: body)); }
        }
        long nextSequence = checked(previous.Sequence + 1);
        ModelTime? nextDue = nextSequence == long.MaxValue || at.Ticks > long.MaxValue - weather.IntervalTicks ? null : new(at.Ticks + weather.IntervalTicks);
        var environment = new CycleEnvironment(previous.Seed, nextSequence, nextDue, at, cold, effects);
        return new(CycleEventKind.WeatherResolved, at, changedCharacters: changes,
            result: cold ? "寒风天气已按各角色暴露条件结算。" : "这次天气窗口平静。", spatialFacts: spatial, environment: environment);
    }
}
