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
        var activity = new CycleActivity(CycleActivityKind.Gather, 1, Hour(6), CycleTime.Hour, Hour(7), "sunflower", target);
        var actor = new CycleCharacter("a", "Alice", backpack: backpack, activity: activity, nextActivityId: 2);
        CycleResource resource = Sun(Hour(6), stock, harvestTicks: reason == CycleGatherStopReason.TimeLimit ? long.MaxValue : CycleTime.Hour);
        CycleWorld world = CycleWorld.CreatePlayable(Hour(6), [actor], [resource]);
        var kernel = CycleEngine.CreateKernel(Memory(world), characterController: new ForbiddenController());

        await Advance(kernel);
        CycleFact fact = Assert.Single(kernel.LastCompletion!.Event.Facts);
        Assert.Equal(reason, fact.GatherStopReason);
        Assert.Null(kernel.World.GetCharacter("a").Activity);
        await Advance(kernel);
        CharacterContextOpening opening = Context(kernel.World).Opening;
        CycleObservedEvent finished = Assert.Single(opening.Experiences, e => e.Event.Kind == CycleObservedEventKind.GatherFinished).Event;
        Assert.Equal(reason.ToString(), finished.Detail);
        Assert.Equal(reason is CycleGatherStopReason.TargetReached or CycleGatherStopReason.TimeLimit ? 1 : 0,
            ActualGain(opening, "item.sunflower"));
        Assert.Equal(CycleOutcomeActivityState.Completed, opening.ActivityState);
        Assert.All(opening.Experiences, e => Assert.Equal("a", e.Event.ObserverId));

    }

    [Fact]
    public async Task Gather_all_freezes_accepted_target_and_summary_excludes_remote_regeneration() {
        CycleWorld world = CycleWorld.CreatePlayable(Hour(6), [new("a", "Alice")],
            [Sun(Hour(6), regenerationTicks: CycleTime.Hour / 4)]);
        var kernel = CycleEngine.CreateKernel(Memory(world), characterController: Commands(CycleCommand.GatherAll("sunflower")));
        await StepWorldEvent(kernel);
        PendingAdvance action = kernel.World.Business.InteractionStates["a"].PendingAdvance!;
        Assert.True(action.Command.All);
        Assert.Equal(3, kernel.World.GetCharacter("a").Activity!.TargetCount);
        for (int count = 0; kernel.World.Business.InteractionStates["a"].CurrentContext?.Opening.PreviousAdvance is null; count++) {
            Assert.True(count < 30);
            await Advance(kernel);
        }
        CharacterContextOpening opening = Context(kernel.World).Opening;
        Assert.Same(action, opening.PreviousAdvance);
        Assert.Equal(3, ActualGain(opening, "item.sunflower"));
        Assert.Equal(CycleGatherStopReason.TargetReached.ToString(),
            Assert.Single(opening.Experiences, e => e.Event.Kind == CycleObservedEventKind.GatherFinished).Event.Detail);
        Assert.Equal(CycleOutcomeActivityState.Completed, opening.ActivityState);
        Assert.Equal(Hour(9), opening.DeliveredAt);
        Assert.Equal("call_fixture_1", action.ToolCallId);

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Weather_summary_records_actual_loss_including_shelter_zero_and_light_change(bool sheltered) {
        var initial = CycleGameFactory.CreateSurvival(Hour(17), weatherSeed: 0);
        CycleCharacter actor = initial.World.GetCharacter("a").WithSurvival(CycleSurvival.Create(Hour(17), food: 100, water: 100,
            warmth: 60, warmthRate: CycleTestSupport.Rate(sheltered ? 12 : 6)));
        CycleExpedition expedition = initial.World.Expedition!;
        if (sheltered) {
            expedition = new(expedition.ScenarioId, GraphSpatialState.Create(CycleExpeditionMap.Graph, [new(new("a"), new("cabin"))]),
                [new("a", "cabin", Hour(17), [])], environment: expedition.Environment);
        }
        CycleWorld world = CycleEngine.Rebuild(initial.World.Business.Replace(Hour(17), changedCharacters: [actor], expedition: expedition), Hour(17));
        var kernel = CycleEngine.CreateKernel(Memory(world), characterController: Commands(CycleCommand.Wait(3)));
        for (int count = 0; kernel.World.Business.InteractionStates["a"].CurrentContext?.Opening.PreviousAdvance is null; count++) {
            Assert.True(count < 20);
            await Advance(kernel);
        }
        CharacterContextOpening opening = Context(kernel.World).Opening;
        CycleObservedEvent weather = Assert.Single(opening.Experiences, e => e.Event.Kind == CycleObservedEventKind.WeatherResolved).Event;
        Assert.Equal(sheltered ? "ColdWindSheltered" : "ColdWind", weather.Detail);
        Assert.Equal(sheltered ? 0 : 10, weather.Effects.Where(e => e.Key == new CycleStateKey("body", "a", "warmth"))
            .Sum(e => e.Before.Number - e.After.Number));
        Assert.Single(opening.Experiences, e => e.Event.Kind == CycleObservedEventKind.LightChanged);
        Assert.Equal(CycleOutcomeActivityState.Completed, opening.ActivityState);

    }

    [Fact]
    public async Task Unknown_destination_stays_unknown_in_saved_opening_and_original_action_description() {
        var fresh = CycleGameFactory.CreateSurvival(Hour(8), enableWeather: false);
        string route = Assert.Single(CycleExpeditionMap.GetPassages("camp"), p => p.To == "ridge").Id;
        CycleObservation knownAtAcceptance = CycleDecisionContext.Observe(fresh.World, "a");
        var travel = CycleEngine.CreateKernel(Memory(fresh.World), characterController: Commands(CycleCommand.Go(route)));
        for (int count = 0; travel.World.Business.InteractionStates["a"].CurrentContext?.Opening.PreviousAdvance is null; count++) {
            Assert.True(count < 20);
            await Advance(travel);
        }
        CharacterContextOpening opening = Context(travel.World).Opening;
        Assert.Equal(CycleOutcomeActivityState.Ongoing, opening.ActivityState);
        Assert.Null(opening.Observation.PlaceId);
        Assert.Equal(Assert.Single(knownAtAcceptance.Passages, p => p.Id == route).Description, opening.PreviousAdvance!.Description);
        Assert.Equal(knownAtAcceptance.PlaceName, opening.PreviousAdvance.OriginPlaceName);
        Assert.DoesNotContain(CycleExpeditionMap.GetPlace("road").Name, opening.PreviousAdvance.Description);
        Assert.Contains(opening.Experiences, e => e.Event.Kind == CycleObservedEventKind.SurvivalWarning);
        Assert.Equal("call_fixture_1", opening.PreviousAdvance.ToolCallId);
        Assert.Equal(Hour(8), opening.PreviousAdvance.AcceptedAt);
        Assert.Equal(Hour(9), opening.DeliveredAt);
    }

    private static CharacterContext Context(CycleWorld world) => world.Business.InteractionStates["a"].CurrentContext!;
    private static decimal ActualGain(CharacterContextOpening opening, string itemId) => opening.Experiences
        .Where(e => e.Event.Kind == CycleObservedEventKind.GatherYielded)
        .SelectMany(e => e.Event.Effects).Where(effect => effect.Key == new CycleStateKey("inventory", itemId, "count"))
        .Sum(effect => effect.After.Number - effect.Before.Number);

    private static async Task Advance(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel) {
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(240)));
        Assert.Equal(CycleEngine.Rebuild(kernel.World.Business, kernel.World.Time).Forecasts.ToString(), kernel.World.Forecasts.ToString());
    }
    private sealed class ForbiddenController : ICycleCharacterController {
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The natural occurrence and delivery must not ask for input.");
    }
}
