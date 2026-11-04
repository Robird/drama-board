using System.Globalization;
using System.Reflection;
using System.Text;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class PredictionIdentityTests {
    public static TheoryData<PlaygroundPredictionKind, string> KeyCases => new() {
        { PlaygroundPredictionKind.SurvivalFatal, "SurvivalFatal" },
        { PlaygroundPredictionKind.RegenerateResource, "RegenerateResource" },
        { PlaygroundPredictionKind.ChangeLight, "ChangeLight" },
        { PlaygroundPredictionKind.DestroyResource, "DestroyResource" },
        { PlaygroundPredictionKind.SpatialOccurrence, "SpatialOccurrence" },
        { PlaygroundPredictionKind.Weather, "Weather" },
        { PlaygroundPredictionKind.CompleteActivity, "CompleteActivity" },
        { PlaygroundPredictionKind.MarketRefresh, "MarketRefresh" },
        { PlaygroundPredictionKind.TradesExpire, "TradesExpire" },
        { PlaygroundPredictionKind.SurvivalWarning, "SurvivalWarning" },
        { PlaygroundPredictionKind.RequestDecision, "RequestDecision" }
    };

    [Theory]
    [MemberData(nameof(KeyCases))]
    public void Canonical_key_retains_the_existing_six_field_utf8_encoding(PlaygroundPredictionKind kind, string name) {
        var prediction = new PlaygroundPrediction("actor:居民|a:activity", kind, "居民|a", new(123), 456, 7);
        byte[] expected = Encoding.UTF8.GetBytes($"actor:居民|a:activity:{name}:居民|a:123:456:7");

        Assert.Equal(expected, prediction.CreateCandidateKey().ToByteArray());
        Assert.NotEqual(Encoding.UTF8.GetString(expected), prediction.ToString());
        Assert.Contains("居民|a", prediction.ToString());
    }

    [Fact]
    public void Every_kind_has_a_frozen_key_name() => Assert.Equal(Enum.GetValues<PlaygroundPredictionKind>(),
        KeyCases.Select(row => (PlaygroundPredictionKind)row[0]));

    [Fact]
    public void Scheduling_identity_compares_each_field_and_ignores_spatial_delegation_payload() {
        var original = new PlaygroundPrediction("actor:a:activity", PlaygroundPredictionKind.CompleteActivity, "a", new(123), 456, 7);
        PlaygroundPrediction[] changes = [
            new("actor:b:activity", original.Kind, original.TargetId, original.Due, original.Generation, original.Sequence),
            new("actor:A:activity", original.Kind, original.TargetId, original.Due, original.Generation, original.Sequence),
            new(original.OwnerId, PlaygroundPredictionKind.RequestDecision, original.TargetId, original.Due, original.Generation, original.Sequence),
            new(original.OwnerId, original.Kind, "b", original.Due, original.Generation, original.Sequence),
            new(original.OwnerId, original.Kind, "A", original.Due, original.Generation, original.Sequence),
            new(original.OwnerId, original.Kind, original.TargetId, new(124), original.Generation, original.Sequence),
            new(original.OwnerId, original.Kind, original.TargetId, original.Due, 457, original.Sequence),
            new(original.OwnerId, original.Kind, original.TargetId, original.Due, original.Generation, 8)
        ];
        Assert.False(original.HasSameIdentity(null));
        foreach (PlaygroundPrediction change in changes) {
            Assert.False(original.HasSameIdentity(change));
            Assert.False(change.HasSameIdentity(original));
            Assert.NotEqual(original.CreateCandidateKey(), change.CreateCandidateKey());
        }
        var same = new PlaygroundPrediction(original.OwnerId, original.Kind, original.TargetId, original.Due, original.Generation, original.Sequence) {
            SpatialCandidate = new(new("spatial-delegate"), new(original.Due), new DelegateData())
        };
        Assert.NotSame(original, same);
        Assert.True(original.HasSameIdentity(same));
        Assert.True(same.HasSameIdentity(original));
        Assert.Equal(original.CreateCandidateKey(), same.CreateCandidateKey());
    }

    [Fact]
    public void Keys_are_invariant_even_when_diagnostic_numbers_use_a_different_negative_sign() {
        CultureInfo previous = CultureInfo.CurrentCulture;
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NegativeSign = "minus";
        var valid = new PlaygroundPrediction("light", PlaygroundPredictionKind.ChangeLight, "world", new(123), 0);
        // Directly constructed negative values are not valid Playground forecasts; their new encoding is invariant too.
        var negative = new PlaygroundPrediction("sample", PlaygroundPredictionKind.ChangeLight, "world", new(-123), -456, -7);
        try {
            CultureInfo.CurrentCulture = culture;
            Assert.Equal(Encoding.UTF8.GetBytes("light:ChangeLight:world:123:0:0"), valid.CreateCandidateKey().ToByteArray());
            Assert.Equal(Encoding.UTF8.GetBytes("sample:ChangeLight:world:-123:-456:-7"), negative.CreateCandidateKey().ToByteArray());
            Assert.Contains("generation=minus456", negative.ToString());
        } finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public async Task Rule_uses_canonical_keys_for_forecast_and_rejects_diagnostic_or_stale_identities_before_dispatch() {
        PlaygroundWorld world = Solo(Hour(6));
        var controller = Commands(PlaygroundCommand.Wait(1));
        var kernel = PlaygroundEngine.CreateKernel(Memory(world), characterController: controller);
        IOccurrenceRule<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> rule = Rule(kernel);
        var candidate = Assert.Single(rule.Forecast(world, new(0, 1024)));
        Assert.Equal(Encoding.UTF8.GetBytes("actor:a:attention:RequestDecision:a:21600000:0:0"), candidate.Key.ToByteArray());

        // The first request opens the Context without asking the controller.
        await Step(kernel);
        world = kernel.World;
        candidate = Assert.Single(rule.Forecast(world, new(0, 1024)));
        var diagnostic = new OccurrenceCandidate<PlaygroundPrediction>(new(candidate.Data.ToString()), candidate.Due, candidate.Data);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rule.PlanSelectedAsync(world, diagnostic, default).AsTask());
        var stale = new PlaygroundPrediction(candidate.Data.OwnerId, candidate.Data.Kind, candidate.Data.TargetId,
            candidate.Data.Due, candidate.Data.Generation + 1, candidate.Data.Sequence);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rule.PlanSelectedAsync(world,
            new(stale.CreateCandidateKey(), candidate.Due, stale), default).AsTask());
        Assert.Equal(0, controller.Calls);

        _ = await rule.PlanSelectedAsync(world, candidate, default);
        Assert.Equal(1, controller.Calls);
    }

    [Fact]
    public async Task Same_due_regenerations_keep_the_existing_seeded_winner_and_cause_bytes() {
        var world = PlaygroundWorld.CreatePlayable(Hour(6), [], [
            new("alpha", "Alpha", "item.alpha", true, 3 * PlaygroundTime.Hour, 5, 0, Hour(6)),
            new("beta", "Beta", "item.beta", true, 3 * PlaygroundTime.Hour, 5, 0, Hour(6))
        ]);
        var kernel = PlaygroundEngine.CreateKernel(Memory(world));

        // Frozen legacy key bytes rank beta before alpha for worldSeed=0, even though alpha sorts first.
        foreach (string expectedId in new[] { "beta", "alpha" }) {
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(9)));
            Assert.Equal(expectedId, Assert.Single(kernel.LastCompletion!.Event.Facts).ResourceId);
            Assert.Equal(Encoding.UTF8.GetBytes($"resource:{expectedId}:regen:RegenerateResource:{expectedId}:32400000:0:0"),
                kernel.LastCompletion.Event.CauseKey.ToByteArray());
        }
    }

    [Fact]
    public async Task Composed_spatial_candidate_keeps_the_wrapped_core_key_and_plans_an_actual_arrival() {
        var initial = PlaygroundGameFactory.CreateSurvival(Hour(8), enableNeeds: false, enableWeather: false, characterCount: 1);
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial.World), characterController: Commands(PlaygroundCommand.Go("e-c7")));
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(24))); // Open Context.
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(24))); // Depart.
        Assert.Equal(PlaygroundEventKind.MoveStarted, Assert.Single(kernel.LastCompletion!.Event.Facts).Kind);
        var rules = new SimulationRules(0, 1024);
        var core = Assert.Single(kernel.World.Definition.Runtime.OccurrenceRule.Forecast(kernel.World.Spatial!, rules));
        var candidate = Assert.Single(Rule(kernel).Forecast(kernel.World, rules));
        Assert.Equal(PlaygroundPredictionKind.SpatialOccurrence, candidate.Data.Kind);
        string expected = FormattableString.Invariant(
            $"spatial:{Convert.ToHexString(core.Key.ToByteArray())}:SpatialOccurrence:a:{core.Due.ModelTime.Ticks}:0:0");
        Assert.Equal(Encoding.UTF8.GetBytes(expected), candidate.Key.ToByteArray());

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(24)));
        Assert.Equal(candidate.Key, kernel.LastCompletion!.Event.CauseKey);
        Assert.Equal(PlaygroundEventKind.MoveArrived, Assert.Single(kernel.LastCompletion.Event.Facts).Kind);
    }

    private static IOccurrenceRule<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> Rule(
        SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel) => Assert.Single(
            Assert.IsAssignableFrom<IReadOnlyList<IOccurrenceRule<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact>>>(
                kernel.GetType().GetField("_rules", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)));

    private sealed record DelegateData : SpatialOccurrenceData;
}
