using DramaBoard.Inventory;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Spatial;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class DecisionOutcomeSummaryTests {
    [Theory]
    [InlineData(CycleGatherStopReason.TargetReached)]
    [InlineData(CycleGatherStopReason.ResourceEmpty)]
    [InlineData(CycleGatherStopReason.ItemLimit)]
    [InlineData(CycleGatherStopReason.CapacityLimit)]
    [InlineData(CycleGatherStopReason.TimeLimit)]
    public async Task Gather_completion_records_structured_stop_reason_and_only_actual_gain(CycleGatherStopReason reason) {
        int target = reason == CycleGatherStopReason.TargetReached ? 1 : 2;
        int stock = reason == CycleGatherStopReason.ResourceEmpty ? 0 : 5;
        ItemInventory backpack = reason switch {
            CycleGatherStopReason.ItemLimit => new(new Dictionary<string, int> { ["item.sunflower"] = int.MaxValue }),
            CycleGatherStopReason.CapacityLimit => new(new Dictionary<string, int> { ["item.wood"] = 1 }, unitCapacity: 1),
            _ => new()
        };
        var action = new CycleAcceptedAction("a", 1, "original-id", "source/1", CycleAcceptedActionKind.Gather,
            Hour(6), resourceId: "sunflower", targetCount: target, activityId: 1);
        var pending = new CyclePendingAction(action, new(backpack: backpack.Items));
        var activity = new CycleActivity(CycleActivityKind.Gather, 1, Hour(6), CycleTime.Hour, Hour(7), "sunflower", target);
        var actor = new CycleCharacter("a", "Alice", backpack: backpack, activity: activity, nextActivityId: 2,
            continuity: new(pending: pending));
        CycleResource resource = Sun(Hour(6), stock, harvestTicks: reason == CycleGatherStopReason.TimeLimit ? long.MaxValue : CycleTime.Hour);
        CycleWorld world = CycleWorld.CreatePlayable(Hour(6), [actor], [resource]);
        var kernel = CycleEngine.CreateKernel(Memory(world), characterController: new ForbiddenController());

        await Advance(kernel);
        CycleFact fact = Assert.Single(kernel.LastCompletion!.Event.Facts);
        Assert.Equal(reason, fact.GatherStopReason);
        Assert.Null(kernel.World.GetCharacter("a").Activity);
        await Advance(kernel);
        CycleActionOutcome incoming = kernel.World.GetCharacter("a").Continuity!.Incoming!;
        Assert.Equal(reason, incoming.Summary.GatherStopReason);
        Assert.Equal(reason is CycleGatherStopReason.TargetReached or CycleGatherStopReason.TimeLimit ? 1 : 0, incoming.Summary.GatheredCount);
        Assert.Equal(reason == CycleGatherStopReason.TargetReached ? CycleWakeReason.ActivityCompleted : CycleWakeReason.ActivityStopped,
            incoming.Summary.Reasons);
        Assert.Equal("original-id", incoming.Action.ToolCallId);
    }

    [Fact]
    public async Task Gather_all_freezes_accepted_target_and_summary_excludes_remote_regeneration() {
        CycleWorld world = CycleWorld.CreatePlayable(Hour(6), [new("a", "Alice", continuity: new())],
            [Sun(Hour(6), regenerationTicks: CycleTime.Hour / 4)]);
        var kernel = CycleEngine.CreateKernel(Memory(world), characterController: Commands(CycleCommand.GatherAll("sunflower")));
        await Advance(kernel);
        CycleAcceptedAction action = kernel.World.GetCharacter("a").Continuity!.Pending!.Action;
        Assert.True(action.SelectedAll);
        Assert.Equal(3, action.TargetCount);
        for (int count = 0; kernel.World.GetCharacter("a").Continuity!.Incoming is null; count++) {
            Assert.True(count < 30);
            await Advance(kernel);
        }
        CycleActionOutcome incoming = kernel.World.GetCharacter("a").Continuity!.Incoming!;
        Assert.Same(action, incoming.Action);
        Assert.Equal(3, incoming.Summary.GatheredCount);
        Assert.Equal("item.sunflower", incoming.Summary.GatherItemId);
        Assert.Equal(CycleGatherStopReason.TargetReached, incoming.Summary.GatherStopReason);
        Assert.Equal(CycleWakeReason.ActivityCompleted, incoming.Summary.Reasons);
        Assert.Equal(Hour(9), incoming.DeliveredAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Weather_summary_records_actual_loss_including_shelter_zero_and_light_change(bool sheltered) {
        var initial = CycleGameFactory.CreateSurvival(Hour(17), weatherSeed: 0);
        CycleCharacter actor = initial.World.GetCharacter("a").WithSurvival(CycleSurvival.Create(Hour(17), food: 100, water: 100,
            warmth: 60, warmthPerHour: sheltered ? 12 : 6));
        CycleExpedition expedition = initial.World.Expedition!;
        if (sheltered) {
            expedition = new(expedition.ScenarioId, GraphSpatialState.Create(CycleExpeditionMap.Graph, [new(new("a"), new("cabin"))]),
                [new("a", "cabin", Hour(17), [])], environment: expedition.Environment);
        }
        CycleWorld world = CycleEngine.Rebuild(initial.World.Business.Replace(Hour(17), changedCharacters: [actor], expedition: expedition), Hour(17));
        var kernel = CycleEngine.CreateKernel(Memory(world), characterController: Commands(CycleCommand.Wait(3)));
        for (int count = 0; kernel.World.GetCharacter("a").Continuity!.Incoming is null; count++) {
            Assert.True(count < 20);
            await Advance(kernel);
        }
        CycleOutcomeSummary summary = kernel.World.GetCharacter("a").Continuity!.Incoming!.Summary;
        Assert.Equal(1, summary.ColdWeatherCount);
        Assert.Equal(0, summary.CalmWeatherCount);
        Assert.Equal((sheltered ? 0 : 10) * CycleSurvival.Scale, summary.WeatherWarmthLossUnits);
        Assert.Equal(1, summary.LightChangeCount);
        Assert.Equal(CycleWakeReason.ActivityCompleted, summary.Reasons);
        Assert.False(summary.IsSaturated);
    }

    [Fact]
    public async Task Saturated_aggregate_is_a_lower_bound_and_unknown_destination_stays_unknown() {
        var initial = CycleGameFactory.CreateSurvival(Hour(8), weatherSeed: 0);
        CycleCharacter actor = initial.World.GetCharacter("a").WithSurvival(CycleSurvival.Create(Hour(8), food: 100, water: 100, warmth: 60));
        CycleWorld world = CycleEngine.Rebuild(initial.World.Business.Replace(Hour(8), changedCharacters: [actor]), Hour(8));
        var kernel = CycleEngine.CreateKernel(Memory(world), characterController: Commands(CycleCommand.Wait(3)));
        await Advance(kernel);
        actor = kernel.World.GetCharacter("a");
        CyclePendingAction pending = actor.Continuity!.Pending!;
        var saturated = new CyclePendingAction(pending.Action, pending.Before,
            new(coldWeatherCount: long.MaxValue, weatherWarmthLossUnits: long.MaxValue), pending.Handoff);
        actor = actor.WithContinuity(new(pending: saturated));
        world = CycleEngine.Rebuild(kernel.World.Business.Replace(kernel.World.Time, changedCharacters: [actor]), kernel.World.Time);
        var resumed = CycleEngine.CreateKernel(Memory(world), characterController: new ForbiddenController());
        await Advance(resumed);
        CycleOutcomeSummary summary = resumed.World.GetCharacter("a").Continuity!.Pending!.Summary;
        Assert.Equal(long.MaxValue, summary.ColdWeatherCount);
        Assert.Equal(long.MaxValue, summary.WeatherWarmthLossUnits);
        Assert.True(summary.IsSaturated);

        var fresh = CycleGameFactory.CreateSurvival(Hour(8), enableWeather: false);
        string route = Assert.Single(CycleExpeditionMap.GetPassages("camp"), p => p.To == "ridge").Id;
        var travel = CycleEngine.CreateKernel(Memory(fresh.World), characterController: Commands(CycleCommand.Go(route)));
        for (int count = 0; travel.World.GetCharacter("a").Continuity!.Incoming is null; count++) {
            Assert.True(count < 20);
            await Advance(travel);
        }
        CycleActionOutcome incoming = travel.World.GetCharacter("a").Continuity!.Incoming!;
        Assert.Equal(CycleOutcomeActivityState.Ongoing, incoming.ActivityState);
        Assert.Null(incoming.After.PlaceId);
        Assert.Null(incoming.After.KnownTargetPlaceId);
        Assert.Null(incoming.After.KnownTargetPlaceName);
        Assert.DoesNotContain("山脚公路", System.Text.Json.JsonSerializer.Serialize(incoming,
            new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Assert.Equal(CycleWakeReason.SurvivalWarning, incoming.Summary.Reasons);
    }

    private static async Task Advance(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel) {
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(240)));
        Assert.Equal(CycleEngine.Rebuild(kernel.World.Business, kernel.World.Time).Forecasts.ToString(), kernel.World.Forecasts.ToString());
    }
    private sealed class ForbiddenController : ICycleCharacterController {
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The natural occurrence and delivery must not ask for input.");
    }
}
