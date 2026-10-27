using DramaBoard.Kernel.Time;
using DramaBoard.Kernel.Simulation;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class CycleCompletionTests {
    [Fact]
    public void Complete_combines_same_tick_business_facts_without_mutating_previous_snapshots() {
        CycleWorld before = CycleWorld.Create(Hour(15), []);
        string original = before.DescribeState();
        CyclePlant sun = before.GetPlant("sunflower");
        CyclePlant moon = before.GetPlant("moongrass");
        var instant = new LogicalInstant(Hour(18), 0);
        CycleFact[] facts = [
            new(CycleEventKind.PlantMatured, instant.ModelTime, sun.Id, null,
                [new(sun.Id, sun.GrowsInDaylight, sun.RequiredTicks, sun.RequiredTicks, null, true, sun.Generation)], null),
            new(CycleEventKind.LightChanged, instant.ModelTime, "world", new(false, Hour(30)),
                [new(moon.Id, moon.GrowsInDaylight, moon.RequiredTicks, 0, Hour(18), false, moon.Generation)], null)
        ];
        CycleWorld afterFirst = CycleEngine.Fold(before, instant, facts[0]);
        CycleWorld folded = CycleEngine.Fold(afterFirst, instant, facts[1]);
        string[] factText = facts.Select(f => f.ToString()).ToArray();
        var metrics = new CycleMetrics();

        CycleWorld completed = CycleEngine.Complete(before, instant, facts, folded, PredictionMode.Incremental, metrics);
        CycleEngine.Validate(completed, instant.ModelTime);

        Assert.Same(folded.Business, completed.Business);
        Assert.Equal(original, before.DescribeState());
        Assert.Same(before.Forecasts, afterFirst.Forecasts);
        Assert.Same(before.Forecasts, folded.Forecasts);
        Assert.Equal(factText, facts.Select(f => f.ToString()));
        AssertPredictions(completed);
        Assert.True(completed.GetPlant("sunflower").IsMature);
        Assert.Contains(completed.Forecasts.Predictions, p => p.OwnerId == "plant:moongrass" && p.Due == Hour(22));
        Assert.DoesNotContain(completed.Forecasts.Predictions, p => p.OwnerId == "plant:sunflower");
        Assert.All(metrics.RecalculationsByOwner.Values, count => Assert.Equal(1L, count));
    }

    [Fact]
    public async Task Candidate_reads_before_the_horizon_do_not_recalculate_or_replace_cache() {
        CycleWorld initial = CycleWorld.Create(Hour(16), []);
        var history = Memory(initial);
        var metrics = new CycleMetrics();
        var kernel = CycleEngine.CreateKernel(history, metrics: metrics);

        for (int read = 0; read < 10; read++) {
            Assert.Equal(StepStatus.BoundaryReached, await kernel.StepAsync(Hour(17)));
        }

        Assert.Same(initial, kernel.World);
        Assert.Same(initial.Forecasts, kernel.World.Forecasts);
        Assert.Empty(history.CompletedEvents);
        Assert.Equal(0, metrics.PredictionChecks);
        Assert.Equal(0, metrics.Recalculations);
        Assert.Equal(0, metrics.RoutedNotifications);
    }

    [Fact]
    public void Complete_rejects_unknown_fact_even_when_folded_business_is_unchanged() {
        CycleWorld before = CycleWorld.Create(Hour(16), []);
        var instant = new LogicalInstant(before.Time, 0);
        var unknown = new CycleFact((CycleEventKind)999, before.Time, "world", null, [], null);

        Assert.Throws<InvalidOperationException>(() => CycleEngine.Complete(before, instant, [unknown], before));
        AssertPredictions(before);
    }

    [Theory]
    [InlineData("time")]
    [InlineData("light")]
    [InlineData("character")]
    public void Complete_rejects_folded_business_changes_not_recorded_in_facts(string extraChange) {
        CycleWorld before = CycleWorld.Create(Hour(15), []);
        string original = before.DescribeState();
        CycleForecasts oldCache = before.Forecasts;
        var instant = new LogicalInstant(Hour(18), 0);
        CyclePlant sun = before.GetPlant("sunflower");
        var fact = new CycleFact(CycleEventKind.PlantMatured, instant.ModelTime, sun.Id, null,
            [new(sun.Id, sun.GrowsInDaylight, sun.RequiredTicks, sun.RequiredTicks, null, true, sun.Generation)], null);
        CycleWorld folded = CycleEngine.Fold(before, instant, fact);
        CycleBusinessState mismatched = extraChange switch {
            "time" => folded.Business.Replace(Hour(19)),
            "light" => folded.Business.Replace(folded.Time, light: new(false, Hour(30))),
            _ => folded.Business.Replace(folded.Time, character: new("unexpected-actor", new()))
        };
        var supplied = new CycleWorld(mismatched, folded.Forecasts);

        var error = Assert.Throws<InvalidOperationException>(() =>
            CycleEngine.Complete(before, instant, [fact], supplied));

        Assert.Contains("does not match the recorded fact sequence", error.Message);
        Assert.Equal(original, before.DescribeState());
        Assert.Same(oldCache, before.Forecasts);
        Assert.Same(oldCache, folded.Forecasts);
        Assert.Same(mismatched, supplied.Business);
        AssertPredictions(before);
    }

    [Fact]
    public void Rebuild_preserves_due_now_and_rejects_expired_business_predictions() {
        CycleWorld before = CycleWorld.Create(Hour(15), []);
        CycleBusinessState dueNow = before.Business.Replace(Hour(18));

        CycleWorld rebuilt = CycleEngine.Rebuild(dueNow, Hour(18));

        AssertPredictions(rebuilt);
        Assert.Contains(rebuilt.Forecasts.Predictions, p => p.OwnerId == "plant:sunflower" && p.Due == Hour(18));
        Assert.Contains(rebuilt.Forecasts.Predictions, p => p.OwnerId == "light" && p.Due == Hour(18));
        CyclePlant sun = before.GetPlant("sunflower");
        CycleBusinessState expiredGrowth = before.Business.Replace(Hour(18), changedPlants: [
            new CyclePlant(sun.Id, sun.GrowsInDaylight, 2 * CycleTime.Hour, 0, Hour(15), false, sun.Generation)
        ]);
        expiredGrowth.Validate(); // The business is valid, but its derived maturity was due at hour 17.
        Assert.Throws<InvalidOperationException>(() => CycleEngine.Rebuild(expiredGrowth, Hour(18)));
        Assert.Throws<InvalidOperationException>(() => CycleEngine.Rebuild(before.Business.Replace(Hour(19)), Hour(19)));
        Assert.Throws<InvalidOperationException>(() => CycleEngine.Validate(rebuilt, Hour(19)));
    }

    [Fact]
    public void Independent_business_oracle_detects_missing_cache_entries() {
        CycleWorld ready = CycleWorld.CreatePlayable();
        CycleWorld missing = new(ready.Business, new CycleForecasts([], ready.Forecasts.Bindings));

        AssertPredictions(ready);
        Assert.NotNull(Record.Exception(() => AssertPredictions(missing)));
        AssertPredictions(CycleEngine.Rebuild(missing.Business, missing.Time));
    }
}
