using Atelia.DurableGraph;
using DramaBoard.Kernel.Random;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.Playground;

/// <summary>One participant's recorded effect of the shared weather window.</summary>
[DurableType("DramaBoard.Playground.WeatherEffect", 1)]
public sealed partial record PlaygroundWeatherEffect(
    [field: DurableField(1)] string ActorId,
    [field: DurableField(2)] long WarmthLossUnits,
    [field: DurableField(3)] bool Sheltered) : IDurableObject;

/// <summary>One shared weather stream and the latest concrete per-character effects.</summary>
/// <remarks>Seed and sequence are the complete random address; queries never consume it.</remarks>
[DurableType("DramaBoard.Playground.Environment", 1)]
public sealed partial class PlaygroundEnvironment : IDurableObject {
    [DurableField(1)] private ulong _seed;
    [DurableField(2)] private long _sequence;
    [DurableField(3)] private ModelTime? _nextDue;
    [DurableField(4)] private ModelTime? _lastAt;
    [DurableField(5)] private bool _lastCold;
    [DurableField(8)] private List<PlaygroundWeatherEffect> _lastEffects;
    public ulong Seed => _seed;
    public long Sequence => _sequence;
    public ModelTime? NextDue => _nextDue;
    public ModelTime? LastAt => _lastAt;
    public bool LastCold => _lastCold;
    public IReadOnlyList<PlaygroundWeatherEffect> LastEffects => _lastEffects.AsReadOnly();
    public PlaygroundEnvironment(ulong seed, long sequence, ModelTime? nextDue, ModelTime? lastAt = null,
        bool lastCold = false, IEnumerable<PlaygroundWeatherEffect>? lastEffects = null) {
        _seed = seed; _sequence = sequence; _nextDue = nextDue; _lastAt = lastAt;
        _lastCold = lastCold; _lastEffects = lastEffects?.OrderBy(e => e.ActorId, StringComparer.Ordinal).ToList() ?? [];
    }
    /// <summary>Legacy fixture shape check. Runtime validation also binds weather to the effective definition.</summary>
    internal void Validate(ModelTime at, IReadOnlyList<PlaygroundCharacter> characters) =>
        Validate(at, characters, PlaygroundEnvironmentRules.IntervalTicks, PlaygroundEnvironmentRules.ExposureLoss);
    internal void Validate(ModelTime at, IReadOnlyList<PlaygroundCharacter> characters, PlaygroundWorldDefinition definition) {
        if (!definition.Weather.Enabled || Seed != definition.Weather.Seed) {
            throw new InvalidOperationException("Weather state does not match its bound content definition.");
        }
        Validate(at, characters, definition.Weather.IntervalTicks, definition.Weather.ExposureLoss);
    }
    private void Validate(ModelTime at, IReadOnlyList<PlaygroundCharacter> characters, long intervalTicks, int exposureLoss) {
        if (Sequence < 0 || NextDue < at || Sequence == long.MaxValue && NextDue is not null || (Sequence == 0) != (LastAt is null) ||
            NextDue is null && Sequence != long.MaxValue && (LastAt ?? at).Ticks <= long.MaxValue - intervalTicks ||
            LastAt is { } last && (last.Ticks < 0 || last > at || NextDue is { } next && next.Ticks - last.Ticks != intervalTicks) ||
            LastAt is null && (LastCold || LastEffects.Count != 0) || LastAt is not null && LastEffects.Count == 0 ||
            LastEffects.Select(e => e.ActorId).Distinct(StringComparer.Ordinal).Count() != LastEffects.Count ||
            LastEffects.Any(e => !characters.Any(c => c.Id == e.ActorId) || e.WarmthLossUnits < 0 ||
                e.WarmthLossUnits > exposureLoss * PlaygroundSurvival.Scale || (!LastCold || e.Sheltered) && e.WarmthLossUnits != 0)) {
            throw new InvalidOperationException("Invalid weather schedule or recorded per-character results.");
        }
    }
    public override string ToString() => $"{Seed}:{Sequence}:{NextDue?.Ticks}:{LastAt?.Ticks}:{LastCold}:[{string.Join(';', LastEffects)}]";
}

[DurableType("DramaBoard.Playground.WeatherKind", 1)]
public enum PlaygroundWeatherKind { Calm, ColdWind }
[DurableType("DramaBoard.Playground.WeatherObservation", 1)]
public sealed partial record PlaygroundWeatherObservation(
    [field: DurableField(1)] ModelTime? NextWindow,
    [field: DurableField(2)] long IntervalTicks,
    [field: DurableField(3)] decimal ExposedWarmthLoss,
    [field: DurableField(4)] bool Sheltered,
    [field: DurableField(5)] ModelTime? LastAt,
    [field: DurableField(6)] PlaygroundWeatherKind? LastKind,
    [field: DurableField(7)] decimal LastActualWarmthLoss,
    [field: DurableField(8)] bool LastWasSheltered) : IDurableObject;

/// <summary>Bound-content weather rules. Only planning samples; recovery applies recorded replacements.</summary>
public static class PlaygroundEnvironmentRules {
    // Default-content constants remain for explicit legacy fixtures; runtime uses world.Definition.Weather.
    public const long IntervalTicks = 2 * PlaygroundTime.Hour;
    public const int ExposureLoss = 10;
    public const ulong DefaultSeed = 20261028UL;
    // Stable historical value of DeriveStreamId("resource-cycle/weather/v1"); renaming preserves random coordinates.
    public const ulong StreamId = 0x3EA96D0230A07494UL;
    public static PlaygroundWeatherObservation? Observe(PlaygroundWorld world, string actorId) {
        if (world.Business.InteractionStates[actorId].CurrentContext is { Status: CharacterContextStatus.Terminal } terminal) {
            return terminal.LatestObservation.Weather;
        }
        PlaygroundEnvironment? environment = world.Expedition?.Environment;
        if (environment is null) { return null; }
        PlaygroundCharacter actor = world.GetCharacter(actorId);
        PlaygroundWeatherEffect? effect = environment.LastEffects.SingleOrDefault(e => e.ActorId == actorId);
        bool sheltered = PlaygroundActions.Location(world, actorId) is AtPlaceLocation place && world.Definition.GetPlace(place.PlaceId.Value).Sheltered;
        return new(actor.IsActive ? environment.NextDue : null, world.Definition.Weather.IntervalTicks, world.Definition.Weather.ExposureLoss, sheltered,
            effect is null ? null : environment.LastAt, effect is null ? null : environment.LastCold ? PlaygroundWeatherKind.ColdWind : PlaygroundWeatherKind.Calm,
            (decimal)(effect?.WarmthLossUnits ?? 0) / PlaygroundSurvival.Scale, effect?.Sheltered ?? false);
    }
    internal static PlaygroundFact Plan(PlaygroundWorld world, ModelTime at) {
        PlaygroundEnvironment previous = world.Expedition!.Environment!;
        PlaygroundWeatherDefinition weather = world.Definition.Weather;
        if (previous.NextDue != at) { throw new InvalidOperationException("Weather must resolve its recorded window."); }
        // The upper tail preserves the original default 1/2 rule (sample == 1).
        bool cold = DeterministicRandom.SampleInt32(previous.Seed, StreamId, checked((ulong)previous.Sequence),
            0, weather.ColdProbabilityDenominator) >= weather.ColdProbabilityDenominator - weather.ColdProbabilityNumerator;
        var changes = new List<PlaygroundCharacter>();
        var effects = new List<PlaygroundWeatherEffect>();
        var spatial = new List<GraphSpatialFact>();
        foreach (PlaygroundCharacter actor in world.Characters.Where(c => c.IsActive).OrderBy(c => c.Id, StringComparer.Ordinal)) {
            bool sheltered = PlaygroundSurvivalRules.IsSheltered(world.Definition, world.Expedition.Spatial, actor.Id);
            PlaygroundSurvival integrated = actor.Survival!.Project(at);
            if (integrated.IsFatal) { throw new InvalidOperationException("Continuous depletion must settle before weather at the same boundary."); }
            long loss = cold && !sheltered ? Math.Min(integrated.WarmthUnits, weather.ExposureLoss * PlaygroundSurvival.Scale) : 0;
            PlaygroundSurvival body = integrated.LoseWarmth(at, loss);
            effects.Add(new(actor.Id, loss, sheltered));
            string result = !cold ? "这次天气窗口平静，没有额外降温。" : sheltered
                ? "寒风掠过；你在庇护中，没有损失温暖。" : $"寒风袭来，温暖减少 {(decimal)loss / PlaygroundSurvival.Scale:0.##}。";
            if (body.IsFatal) {
                changes.Add(actor.WithSurvival(body).End(PlaygroundExpeditionOutcome.Dead, at,
                    $"{actor.Name}在寒风中失温，未能走出山林。", PlaygroundActions.Location(world, actor.Id)!));
                spatial.Add(new EntityRemovedFact(new(actor.Id)));
            } else { changes.Add(actor.Change(actor.Activity, result: result, survival: body)); }
        }
        long nextSequence = checked(previous.Sequence + 1);
        ModelTime? nextDue = nextSequence == long.MaxValue || at.Ticks > long.MaxValue - weather.IntervalTicks ? null : new(at.Ticks + weather.IntervalTicks);
        var environment = new PlaygroundEnvironment(previous.Seed, nextSequence, nextDue, at, cold, effects);
        return new(PlaygroundEventKind.WeatherResolved, at, changedCharacters: changes,
            result: cold ? "寒风天气已按各角色暴露条件结算。" : "这次天气窗口平静。", spatialFacts: spatial, environment: environment);
    }
}
