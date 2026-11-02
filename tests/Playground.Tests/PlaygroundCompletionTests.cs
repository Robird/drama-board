using DramaBoard.Kernel.Time;
using DramaBoard.Kernel.Simulation;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class PlaygroundCompletionTests {
    [Fact]
    public async Task Complete_combines_same_tick_business_facts_without_mutating_previous_snapshots() {
        PlaygroundWorld before = PlaygroundWorld.Create(Hour(15), []);
        string original = before.DescribeState();
        var history = Memory(before);
        var kernel = PlaygroundEngine.CreateKernel(history);
        await RunUntil(kernel, Hour(18));
        PlaygroundFact[] facts = history.CompletedEvents.SelectMany(e => e.Facts).ToArray();
        Assert.Equal(2, facts.Length);
        var instant = new LogicalInstant(Hour(18), 0);
        PlaygroundWorld afterFirst = PlaygroundEngine.Fold(before, instant, facts[0]);
        PlaygroundWorld folded = PlaygroundEngine.Fold(afterFirst, instant, facts[1]);
        string[] text = facts.Select(f => f.ToString()).ToArray();
        var metrics = new PlaygroundMetrics();
        PlaygroundWorld completed = PlaygroundEngine.Complete(before, instant, facts, folded, PredictionMode.Incremental, metrics);
        PlaygroundEngine.Validate(completed, instant.ModelTime);
        Assert.Same(folded.Business, completed.Business);
        Assert.Equal(original, before.DescribeState());
        Assert.Same(before.Forecasts, afterFirst.Forecasts);
        Assert.Same(before.Forecasts, folded.Forecasts);
        Assert.Equal(text, facts.Select(f => f.ToString()));
        AssertPredictions(completed);
        Assert.Equal(before.GetResource("sunflower").Stock + 1, completed.GetResource("sunflower").Stock);
        Assert.Contains(completed.Forecasts.Predictions, p => p.OwnerId == "resource:moongrass:regen" && p.Due == Hour(22));
        Assert.DoesNotContain(completed.Forecasts.Predictions, p => p.OwnerId == "resource:sunflower:regen");
        Assert.All(metrics.RecalculationsByOwner.Values, count => Assert.Equal(1L, count));
    }

    [Fact]
    public async Task Candidate_reads_before_horizon_do_not_recalculate_or_replace_cache() {
        PlaygroundWorld initial = PlaygroundWorld.Create(Hour(16), []);
        var history = Memory(initial);
        var metrics = new PlaygroundMetrics();
        var kernel = PlaygroundEngine.CreateKernel(history, metrics: metrics);
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

    [Theory]
    [InlineData("time")]
    [InlineData("light")]
    [InlineData("character")]
    public async Task Complete_rejects_folded_business_changes_not_recorded_in_facts(string extraChange) {
        PlaygroundWorld before = Solo(Hour(15));
        string original = before.DescribeState();
        var kernel = PlaygroundEngine.CreateKernel(Memory(before), characterController: Commands(PlaygroundCommand.Wait(1)));
        await Step(kernel);
        PlaygroundFact[] facts = kernel.LastCompletion!.Event.Facts.ToArray();
        var instant = kernel.LastCompletion.Event.TargetInstant;
        PlaygroundWorld folded = facts.Aggregate(before, (world, fact) => PlaygroundEngine.Fold(world, instant, fact));
        PlaygroundBusinessState mismatched = extraChange switch {
            "time" => folded.Business.Replace(Hour(19)),
            "light" => folded.Business.Replace(folded.Time, light: new(false, Hour(30))),
            _ => folded.Business.Replace(folded.Time, changedCharacters: [new("a", "Changed", 3)])
        };
        var supplied = new PlaygroundWorld(mismatched, folded.Forecasts);
        var error = Assert.Throws<InvalidOperationException>(() => PlaygroundEngine.Complete(before, instant, facts, supplied));
        Assert.Contains("does not match the recorded fact sequence", error.Message);
        Assert.Equal(original, before.DescribeState());
        Assert.Same(before.Forecasts, folded.Forecasts);
        Assert.Same(mismatched, supplied.Business);
        AssertPredictions(before);
    }

    [Fact]
    public void Rebuild_preserves_due_now_and_rejects_expired_business_predictions() {
        PlaygroundWorld before = PlaygroundWorld.Create(Hour(15), []);
        PlaygroundWorld rebuilt = PlaygroundEngine.Rebuild(before.Business.Replace(Hour(18)), Hour(18));
        AssertPredictions(rebuilt);
        Assert.Contains(rebuilt.Forecasts.Predictions, p => p.OwnerId == "resource:sunflower:regen" && p.Due == Hour(18));
        Assert.Contains(rebuilt.Forecasts.Predictions, p => p.OwnerId == "light" && p.Due == Hour(18));
        PlaygroundResource sun = before.GetResource("sunflower");
        PlaygroundBusinessState expired = before.Business.Replace(Hour(18), changedResources: [
            new(sun.Id, sun.Name, sun.ItemId, true, 2 * PlaygroundTime.Hour, sun.Capacity, sun.Stock, Hour(15))
        ]);
        Assert.Throws<InvalidOperationException>(() => expired.Validate());
        Assert.Throws<InvalidOperationException>(() => PlaygroundEngine.Rebuild(expired, Hour(18)));
        Assert.Throws<InvalidOperationException>(() => PlaygroundEngine.Rebuild(before.Business.Replace(Hour(19)), Hour(19)));
        Assert.Throws<InvalidOperationException>(() => PlaygroundEngine.Validate(rebuilt, Hour(19)));
    }

    [Fact]
    public void Independent_business_oracle_detects_missing_cache_entries() {
        PlaygroundWorld ready = PlaygroundWorld.CreatePlayable();
        PlaygroundWorld missing = new(ready.Business, new PlaygroundForecasts([], ready.Forecasts.Bindings));
        AssertPredictions(ready);
        Assert.NotNull(Record.Exception(() => AssertPredictions(missing)));
        AssertPredictions(PlaygroundEngine.Rebuild(missing.Business, missing.Time));
    }
}
