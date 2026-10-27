using DramaBoard.Kernel.Simulation;
using DramaBoard.ResourceCycle;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class CycleBehaviorTests {
    [Fact]
    public async Task Day_growth_pauses_and_resumes_with_exact_hand_calculated_maturity_times() {
        var history = Memory(CycleWorld.Create(Hour(16), []));
        var kernel = CycleEngine.CreateKernel(history);

        await RunUntil(kernel, Hour(32));

        var maturity = history.CompletedEvents.SelectMany(item => item.Facts)
            .Where(fact => fact.Kind == CycleEventKind.PlantMatured).ToArray();
        Assert.Collection(maturity,
            moon => {
                Assert.Equal("moongrass", moon.TargetId);
                Assert.Equal(Hour(22), moon.At);
            },
            sun => {
                Assert.Equal("sunflower", sun.TargetId);
                Assert.Equal(Hour(31), sun.At);
            });
        Assert.All(kernel.World.Plants, plant => Assert.True(plant.IsMature));
    }

    [Fact]
    public async Task Paused_plant_retains_subscription_but_loses_old_completion_prediction() {
        var history = Memory(CycleWorld.Create(Hour(16), []));
        var kernel = CycleEngine.CreateKernel(history);
        Assert.Contains(kernel.World.Forecasts.Predictions,
            prediction => prediction.OwnerId == "plant:sunflower" && prediction.Due == Hour(19));

        await RunUntil(kernel, Hour(18));

        var sun = kernel.World.GetPlant("sunflower");
        Assert.Equal(2 * 3_600_000L, sun.AccumulatedTicks);
        Assert.Null(sun.ActiveSince);
        Assert.DoesNotContain(kernel.World.Forecasts.Predictions,
            prediction => prediction.OwnerId == "plant:sunflower");
        Assert.Contains(kernel.World.Forecasts.Bindings,
            binding => binding.OwnerId == "plant:sunflower");

        await RunUntil(kernel, Hour(30));

        Assert.Contains(kernel.World.Forecasts.Predictions,
            prediction => prediction.OwnerId == "plant:sunflower" && prediction.Due == Hour(31));
        Assert.DoesNotContain(history.CompletedEvents.SelectMany(item => item.Facts),
            fact => fact.Kind == CycleEventKind.PlantMatured && fact.TargetId == "sunflower");
    }

    [Fact]
    public async Task Destroying_immature_plant_discards_accumulated_growth_and_old_prediction() {
        var history = Memory(CycleWorld.Create(Hour(16), [new("sunflower", Hour(17))]));
        var kernel = CycleEngine.CreateKernel(history);

        await RunUntil(kernel, Hour(17));

        var sun = kernel.World.GetPlant("sunflower");
        Assert.False(sun.IsMature);
        Assert.Equal(0, sun.AccumulatedTicks);
        Assert.Equal(Hour(17), sun.ActiveSince);
        Assert.Contains(kernel.World.Forecasts.Predictions,
            prediction => prediction.OwnerId == "plant:sunflower" && prediction.Due == Hour(20));
        Assert.DoesNotContain(kernel.World.Forecasts.Predictions,
            prediction => prediction.OwnerId == "plant:sunflower" && prediction.Due == Hour(19));

        await RunUntil(kernel, Hour(32));

        var matured = Assert.Single(history.CompletedEvents.SelectMany(item => item.Facts),
            fact => fact.Kind == CycleEventKind.PlantMatured && fact.TargetId == "sunflower");
        Assert.Equal(Hour(32), matured.At);
    }

    [Fact]
    public async Task Coincident_events_mature_before_light_change_before_destruction() {
        var history = Memory(CycleWorld.Create(Hour(15), [new("sunflower", Hour(18))]));
        var kernel = CycleEngine.CreateKernel(history);

        await RunUntil(kernel, Hour(18));

        var events = history.CompletedEvents.Where(item => item.TargetInstant.ModelTime == Hour(18)).ToArray();
        Assert.Equal(new[] {
            CycleEventKind.PlantMatured, CycleEventKind.LightChanged, CycleEventKind.PlantDestroyed,
        }, events.SelectMany(item => item.Facts).Select(fact => fact.Kind));
        Assert.Equal(new[] { 0L, 1L, 2L }, events.Select(item => item.TargetInstant.CausalOrdinal));
        var sun = kernel.World.GetPlant("sunflower");
        Assert.False(sun.IsMature);
        Assert.Equal(0, sun.AccumulatedTicks);
        Assert.Null(sun.ActiveSince);
        Assert.DoesNotContain(kernel.World.Forecasts.Predictions,
            prediction => prediction.OwnerId == "plant:sunflower");

        await RunUntil(kernel, Hour(33));
        Assert.True(kernel.World.GetPlant("sunflower").IsMature);
        Assert.Equal(new[] { Hour(18), Hour(33) }, history.CompletedEvents.SelectMany(item => item.Facts)
            .Where(fact => fact.Kind == CycleEventKind.PlantMatured && fact.TargetId == "sunflower")
            .Select(fact => fact.At));
    }

    [Fact]
    public async Task Destroying_one_plant_does_not_route_to_or_recalculate_unrelated_plant() {
        var metrics = new CycleMetrics();
        var history = Memory(CycleWorld.Create(Hour(16), [new("sunflower", Hour(17))]));
        var kernel = CycleEngine.CreateKernel(history, metrics: metrics);
        long unrelatedBefore = Recalculations(metrics, "plant:moongrass");
        long affectedBefore = Recalculations(metrics, "plant:sunflower");
        long routedBefore = metrics.RoutedNotifications;

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(17)));

        Assert.Equal(unrelatedBefore, Recalculations(metrics, "plant:moongrass"));
        Assert.True(Recalculations(metrics, "plant:sunflower") > affectedBefore);
        Assert.True(metrics.RoutedNotifications - routedBefore < 4,
            "A targeted destruction should not broadcast to all four predictor owners.");
    }

    [Fact]
    public async Task Mature_plants_receive_light_notification_but_fine_filter_skips_recalculation() {
        var metrics = new CycleMetrics();
        var kernel = CycleEngine.CreateKernel(Memory(CycleWorld.Create(Hour(6), [])), metrics: metrics);
        await RunUntil(kernel, Hour(22));
        Assert.All(kernel.World.Plants, plant => Assert.True(plant.IsMature));
        long sunCount = Recalculations(metrics, "plant:sunflower");
        long moonCount = Recalculations(metrics, "plant:moongrass");
        long routedBefore = metrics.RoutedNotifications;

        await RunUntil(kernel, Hour(30));

        Assert.True(metrics.RoutedNotifications > routedBefore);
        Assert.Equal(sunCount, Recalculations(metrics, "plant:sunflower"));
        Assert.Equal(moonCount, Recalculations(metrics, "plant:moongrass"));
    }

    [Theory]
    [InlineData(16, 96)]
    [InlineData(0, 72)]
    [InlineData(6, 72)]
    [InlineData(18, 96)]
    public async Task Incremental_and_full_recalculation_have_identical_complete_traces(long start, long end) {
        CycleDestruction[] script = [
            new("sunflower", Hour(start + 1)), new("moongrass", Hour(start + 4)),
            new("sunflower", Hour(start + 17)), new("sunflower", Hour(start + 25)),
            new("moongrass", Hour(start + 29)),
        ];
        var incrementalHistory = Memory(CycleWorld.Create(Hour(start), script));
        var fullHistory = Memory(CycleWorld.Create(Hour(start), script));
        var incrementalMetrics = new CycleMetrics();
        var fullMetrics = new CycleMetrics();
        var incremental = CycleEngine.CreateKernel(incrementalHistory, PredictionMode.Incremental, incrementalMetrics);
        var full = CycleEngine.CreateKernel(fullHistory, PredictionMode.Full, fullMetrics);

        await RunUntil(incremental, Hour(end));
        await RunUntil(full, Hour(end));

        Assert.Equal(full.World.DescribeState(), incremental.World.DescribeState());
        Assert.Equal(full.Cursor, incremental.Cursor);
        Assert.Equal(fullHistory.CompletedEvents.Select(Describe), incrementalHistory.CompletedEvents.Select(Describe));
        Assert.True(incrementalMetrics.Recalculations < fullMetrics.Recalculations);
        Assert.Equal(fullMetrics.PredictionChecks, incrementalMetrics.PredictionChecks);
        Assert.Equal((long)fullHistory.CompletedEvents.Count, fullMetrics.PredictionChecks);
        Assert.Equal((long)incrementalHistory.CompletedEvents.Count, incrementalMetrics.PredictionChecks);
    }

    [Fact]
    public async Task Published_snapshots_are_not_modified_by_later_events() {
        var initial = CycleWorld.Create();
        var history = Memory(initial);
        var kernel = CycleEngine.CreateKernel(history);
        string initialText = initial.DescribeState();
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(96)));
        var first = kernel.World;
        string firstText = first.DescribeState();
        string firstEvent = Describe(Assert.Single(history.CompletedEvents));

        await RunUntil(kernel, Hour(96));

        Assert.Equal(initialText, initial.DescribeState());
        Assert.Equal(firstText, first.DescribeState());
        Assert.Equal(firstEvent, Describe(history.CompletedEvents[0]));
    }
}
