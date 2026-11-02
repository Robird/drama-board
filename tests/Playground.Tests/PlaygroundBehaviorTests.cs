using DramaBoard.Kernel.Simulation;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class PlaygroundBehaviorTests {
    [Fact]
    public async Task Day_growth_preserves_partial_progress_and_stops_at_capacity() {
        var history = Memory(PlaygroundWorld.Create(Hour(16), []));
        var kernel = PlaygroundEngine.CreateKernel(history);
        int initialSun = kernel.World.GetResource("sunflower").Stock;
        int initialMoon = kernel.World.GetResource("moongrass").Stock;
        await RunUntil(kernel, Hour(32));
        Assert.Equal(initialSun + 1, kernel.World.GetResource("sunflower").Stock);
        Assert.Equal(Math.Min(initialMoon + 3, kernel.World.GetResource("moongrass").Capacity), kernel.World.GetResource("moongrass").Stock);
        Assert.Contains(history.CompletedEvents.SelectMany(e => e.Facts), f => f.Kind == PlaygroundEventKind.ResourceRegenerated && f.ResourceId == "sunflower" && f.At == Hour(31));
        Assert.Contains(history.CompletedEvents.SelectMany(e => e.Facts), f => f.Kind == PlaygroundEventKind.ResourceRegenerated && f.ResourceId == "moongrass" && f.At == Hour(22));
    }

    [Fact]
    public async Task Paused_resource_retains_subscription_and_later_reactivates() {
        var kernel = PlaygroundEngine.CreateKernel(Memory(PlaygroundWorld.Create(Hour(16), [])));
        await RunUntil(kernel, Hour(18));
        Assert.Equal(2 * PlaygroundTime.Hour, kernel.World.GetResource("sunflower").AccumulatedTicks);
        Assert.Null(kernel.World.GetResource("sunflower").ActiveSince);
        Assert.DoesNotContain(kernel.World.Forecasts.Predictions, p => p.OwnerId == "resource:sunflower:regen");
        Assert.Contains(kernel.World.Forecasts.Bindings, p => p.OwnerId == "resource:sunflower:regen");
        await RunUntil(kernel, Hour(30));
        Assert.Contains(kernel.World.Forecasts.Predictions, p => p.OwnerId == "resource:sunflower:regen" && p.Due == Hour(31));
    }

    [Fact]
    public async Task Destruction_empties_stock_discards_progress_and_does_not_recalculate_unrelated_resource() {
        var metrics = new PlaygroundMetrics();
        var kernel = PlaygroundEngine.CreateKernel(Memory(PlaygroundWorld.Create(Hour(16), [new("sunflower", Hour(17))])), metrics: metrics);
        await Step(kernel);
        var sun = kernel.World.GetResource("sunflower");
        Assert.Equal(0, sun.Stock);
        Assert.Equal(0, sun.AccumulatedTicks);
        Assert.Equal(Hour(17), sun.ActiveSince);
        Assert.Contains(kernel.World.Forecasts.Predictions, p => p.OwnerId == "resource:sunflower:regen" && p.Due == Hour(20));
        Assert.Equal(0, Recalculations(metrics, "resource:moongrass:regen"));
        await RunUntil(kernel, Hour(32));
        Assert.Equal(1, kernel.World.GetResource("sunflower").Stock);
    }

    [Fact]
    public async Task Coincident_regeneration_precedes_light_change_and_destruction() {
        var history = Memory(PlaygroundWorld.Create(Hour(15), [new("sunflower", Hour(18))]));
        var kernel = PlaygroundEngine.CreateKernel(history);
        await RunUntil(kernel, Hour(18));
        var events = history.CompletedEvents.Where(e => e.TargetInstant.ModelTime == Hour(18)).ToArray();
        Assert.Equal(new[] { PlaygroundEventKind.ResourceRegenerated, PlaygroundEventKind.LightChanged, PlaygroundEventKind.ResourceDestroyed },
            events.SelectMany(e => e.Facts).Select(f => f.Kind));
        Assert.Equal(new long[] { 0, 1, 2 }, events.Select(e => e.TargetInstant.CausalOrdinal));
        Assert.Equal(0, kernel.World.GetResource("sunflower").Stock);
        Assert.Null(kernel.World.GetResource("sunflower").ActiveSince);
    }

    [Fact]
    public async Task Full_resources_do_not_regenerate_or_recalculate_on_light_changes() {
        var initial = PlaygroundWorld.Create(Hour(6), []);
        var full = initial.Resources.Select(r => new PlaygroundResource(r.Id, r.Name, r.ItemId, r.GrowsInDaylight,
            r.RegenerationTicks, r.Capacity, r.Capacity)).ToArray();
        var metrics = new PlaygroundMetrics();
        var kernel = PlaygroundEngine.CreateKernel(Memory(PlaygroundEngine.Rebuild(initial.Business.Replace(initial.Time, changedResources: full), initial.Time)), metrics: metrics);
        await RunUntil(kernel, Hour(30));
        Assert.All(kernel.World.Resources, r => Assert.Equal(r.Capacity, r.Stock));
        Assert.Equal(0, Recalculations(metrics, "resource:sunflower:regen"));
        Assert.Equal(0, Recalculations(metrics, "resource:moongrass:regen"));
    }

    [Theory]
    [InlineData(16, 96)]
    [InlineData(0, 72)]
    [InlineData(6, 72)]
    [InlineData(18, 96)]
    public async Task Incremental_and_full_recalculation_have_identical_complete_traces(long start, long end) {
        PlaygroundDestruction[] script = [new("sunflower", Hour(start + 1)), new("moongrass", Hour(start + 4)), new("sunflower", Hour(start + 17)), new("sunflower", Hour(start + 25)), new("moongrass", Hour(start + 29))];
        var ih = Memory(PlaygroundWorld.Create(Hour(start), script));
        var fh = Memory(PlaygroundWorld.Create(Hour(start), script));
        var im = new PlaygroundMetrics();
        var fm = new PlaygroundMetrics();
        var incremental = PlaygroundEngine.CreateKernel(ih, PredictionMode.Incremental, im);
        var full = PlaygroundEngine.CreateKernel(fh, PredictionMode.Full, fm);
        await RunUntil(incremental, Hour(end));
        await RunUntil(full, Hour(end));
        Assert.Equal(full.World.DescribeState(), incremental.World.DescribeState());
        Assert.Equal(full.Cursor, incremental.Cursor);
        Assert.Equal(fh.CompletedEvents.Select(Describe), ih.CompletedEvents.Select(Describe));
        Assert.True(im.Recalculations < fm.Recalculations);
        Assert.Equal(fm.PredictionChecks, im.PredictionChecks);
        Assert.Equal((long)ih.CompletedEvents.Count, im.PredictionChecks);
    }

    [Fact]
    public async Task Published_snapshots_are_not_modified_by_later_events() {
        var initial = PlaygroundWorld.Create();
        var history = Memory(initial);
        var kernel = PlaygroundEngine.CreateKernel(history);
        string initialText = initial.DescribeState();
        await Step(kernel);
        var first = kernel.World;
        string firstText = first.DescribeState();
        string firstEvent = Describe(Assert.Single(history.CompletedEvents));
        await RunUntil(kernel, Hour(96));
        Assert.Equal(initialText, initial.DescribeState());
        Assert.Equal(firstText, first.DescribeState());
        Assert.Equal(firstEvent, Describe(history.CompletedEvents[0]));
    }
}
