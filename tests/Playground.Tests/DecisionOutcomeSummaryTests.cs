using DramaBoard.Inventory;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Spatial;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class DecisionOutcomeSummaryTests {
    [Theory]
    [InlineData(PlaygroundGatherStopReason.TargetReached)]
    [InlineData(PlaygroundGatherStopReason.ResourceEmpty)]
    [InlineData(PlaygroundGatherStopReason.ItemLimit)]
    [InlineData(PlaygroundGatherStopReason.CapacityLimit)]
    [InlineData(PlaygroundGatherStopReason.TimeLimit)]
    public async Task Gather_completion_records_structured_stop_reason_and_only_actual_gain(PlaygroundGatherStopReason reason) {
        int target = reason == PlaygroundGatherStopReason.TargetReached ? 1 : 2;
        int stock = reason == PlaygroundGatherStopReason.ResourceEmpty ? 0 : 5;
        ItemInventory backpack = reason switch {
            PlaygroundGatherStopReason.ItemLimit => new(new Dictionary<string, int> { ["item.sunflower"] = int.MaxValue }),
            PlaygroundGatherStopReason.CapacityLimit => new(new Dictionary<string, int> { ["item.wood"] = 1 }, unitCapacity: 1),
            _ => new()
        };
        var activity = new PlaygroundActivity(PlaygroundActivityKind.Gather, 1, Hour(6), PlaygroundTime.Hour, Hour(7), "sunflower", target);
        var actor = new PlaygroundCharacter("a", "Alice", backpack: backpack, activity: activity, nextActivityId: 2);
        PlaygroundResource resource = Sun(Hour(6), stock, harvestTicks: reason == PlaygroundGatherStopReason.TimeLimit ? long.MaxValue : PlaygroundTime.Hour);
        PlaygroundWorld world = PlaygroundWorld.CreatePlayable(Hour(6), [actor], [resource]);
        var kernel = PlaygroundEngine.CreateKernel(Memory(world), characterController: new ForbiddenController());

        await Advance(kernel);
        PlaygroundFact fact = Assert.Single(kernel.LastCompletion!.Event.Facts);
        Assert.Equal(reason, fact.GatherStopReason);
        Assert.Null(kernel.World.GetCharacter("a").Activity);
        await Advance(kernel);
        CharacterContextOpening opening = Context(kernel.World).Opening;
        PlaygroundObservedEvent finished = Assert.Single(opening.Experiences, e => e.Event.Kind == PlaygroundObservedEventKind.GatherFinished).Event;
        Assert.Equal(reason.ToString(), finished.Detail);
        Assert.Equal(reason is PlaygroundGatherStopReason.TargetReached or PlaygroundGatherStopReason.TimeLimit ? 1 : 0,
            ActualGain(opening, "item.sunflower"));
        Assert.Equal(PlaygroundOutcomeActivityState.Completed, opening.ActivityState);
        Assert.All(opening.Experiences, e => Assert.Equal("a", e.Event.ObserverId));

    }

    [Fact]
    public async Task Gather_all_freezes_accepted_target_and_summary_excludes_remote_regeneration() {
        PlaygroundWorld world = PlaygroundWorld.CreatePlayable(Hour(6), [new("a", "Alice")],
            [Sun(Hour(6), regenerationTicks: PlaygroundTime.Hour / 4)]);
        var kernel = PlaygroundEngine.CreateKernel(Memory(world), characterController: Commands(PlaygroundCommand.GatherAll("sunflower")));
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
        Assert.Equal(PlaygroundGatherStopReason.TargetReached.ToString(),
            Assert.Single(opening.Experiences, e => e.Event.Kind == PlaygroundObservedEventKind.GatherFinished).Event.Detail);
        Assert.Equal(PlaygroundOutcomeActivityState.Completed, opening.ActivityState);
        Assert.Equal(Hour(9), opening.DeliveredAt);
        Assert.Equal("call_fixture_1", action.ToolCallId);

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Weather_summary_records_actual_loss_including_shelter_zero_and_light_change(bool sheltered) {
        var initial = PlaygroundGameFactory.CreateSurvival(Hour(17), weatherSeed: 0);
        PlaygroundCharacter actor = initial.World.GetCharacter("a").WithSurvival(PlaygroundSurvival.Create(Hour(17), food: 100, water: 100,
            warmth: 60, warmthRate: PlaygroundTestSupport.Rate(sheltered ? 12 : 6)));
        PlaygroundExpedition expedition = initial.World.Expedition!;
        if (sheltered) {
            expedition = new(expedition.ScenarioId, GraphSpatialState.Create(PlaygroundExpeditionMap.Graph, [new(new("a"), new("cabin"))]),
                [new("a", "cabin", Hour(17), [])], environment: expedition.Environment);
        }
        PlaygroundWorld world = PlaygroundEngine.Rebuild(initial.World.Business.Replace(Hour(17), changedCharacters: [actor], expedition: expedition), Hour(17));
        var kernel = PlaygroundEngine.CreateKernel(Memory(world), characterController: Commands(PlaygroundCommand.Wait(3)));
        for (int count = 0; kernel.World.Business.InteractionStates["a"].CurrentContext?.Opening.PreviousAdvance is null; count++) {
            Assert.True(count < 20);
            await Advance(kernel);
        }
        CharacterContextOpening opening = Context(kernel.World).Opening;
        PlaygroundObservedEvent weather = Assert.Single(opening.Experiences, e => e.Event.Kind == PlaygroundObservedEventKind.WeatherResolved).Event;
        Assert.Equal(sheltered ? "ColdWindSheltered" : "ColdWind", weather.Detail);
        Assert.Equal(sheltered ? 0 : 10, weather.Effects.Where(e => e.Key == new PlaygroundStateKey("body", "a", "warmth"))
            .Sum(e => e.Before.Number - e.After.Number));
        Assert.Single(opening.Experiences, e => e.Event.Kind == PlaygroundObservedEventKind.LightChanged);
        Assert.Equal(PlaygroundOutcomeActivityState.Completed, opening.ActivityState);

    }

    [Fact]
    public async Task Unknown_destination_stays_unknown_in_saved_opening_and_original_action_description() {
        var fresh = PlaygroundGameFactory.CreateSurvival(Hour(8), enableWeather: false);
        string route = Assert.Single(PlaygroundExpeditionMap.GetEntrances("camp"), p => p.To == "ridge").EntranceId;
        PlaygroundObservation knownAtAcceptance = PlaygroundDecisionContext.Observe(fresh.World, "a");
        var travel = PlaygroundEngine.CreateKernel(Memory(fresh.World), characterController: Commands(PlaygroundCommand.Go(route)));
        for (int count = 0; travel.World.Business.InteractionStates["a"].CurrentContext?.Opening.PreviousAdvance is null; count++) {
            Assert.True(count < 20);
            await Advance(travel);
        }
        CharacterContextOpening opening = Context(travel.World).Opening;
        Assert.Equal(PlaygroundOutcomeActivityState.Ongoing, opening.ActivityState);
        Assert.Null(opening.Observation.PlaceId);
        Assert.Equal(Assert.Single(knownAtAcceptance.Passages, p => p.Id == route).Description, opening.PreviousAdvance!.Description);
        Assert.Equal(knownAtAcceptance.PlaceName, opening.PreviousAdvance.OriginPlaceName);
        Assert.DoesNotContain(PlaygroundExpeditionMap.GetPlace("road").Name, opening.PreviousAdvance.Description);
        Assert.Contains(opening.Experiences, e => e.Event.Kind == PlaygroundObservedEventKind.SurvivalWarning);
        Assert.Equal("call_fixture_1", opening.PreviousAdvance.ToolCallId);
        Assert.Equal(Hour(8), opening.PreviousAdvance.AcceptedAt);
        Assert.Equal(Hour(9), opening.DeliveredAt);
    }

    private static CharacterContext Context(PlaygroundWorld world) => world.Business.InteractionStates["a"].CurrentContext!;
    private static decimal ActualGain(CharacterContextOpening opening, string itemId) => opening.Experiences
        .Where(e => e.Event.Kind == PlaygroundObservedEventKind.GatherYielded)
        .SelectMany(e => e.Event.Effects).Where(effect => effect.Key == new PlaygroundStateKey("inventory", itemId, "count"))
        .Sum(effect => effect.After.Number - effect.Before.Number);

    private static async Task Advance(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel) {
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(240)));
        AssertForecasts(PlaygroundEngine.Rebuild(kernel.World.Business, kernel.World.Time).Forecasts, kernel.World.Forecasts);
    }
    private sealed class ForbiddenController : IPlaygroundCharacterController {
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The natural occurrence and delivery must not ask for input.");
    }
}
