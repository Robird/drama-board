using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Playground;

namespace DramaBoard.Playground.Cli.Tests;

public sealed class SurvivalNeedsViewTests {
    [Theory]
    [InlineData(30, 80, 80, "食物")]
    [InlineData(80, 33, 80, "饮水")]
    [InlineData(80, 80, 37, "温暖")]
    [InlineData(30, 33, 37, "食物、饮水、温暖")]
    public async Task Return_reason_names_all_new_warning_attributes_without_inference_from_current_values(
        int food, int water, int warmth, string attributes) {
        var initial = PreparedNeeds(PlaygroundSurvival.Create(new ModelTime(19 * PlaygroundTime.Hour), food, water, warmth));
        var kernel = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor),
            characterController: new Commands(PlaygroundCommand.Go("e-b9")));
        await Until(kernel, world => world.Business.InteractionStates["a"].CurrentContext?.ContextSequence == 2);
        var state = kernel.World.Business.InteractionStates["a"];
        var opening = state.CurrentContext!.Opening;
        foreach (IPlayLayout layout in new IPlayLayout[] { new ClassicPlayLayout(), new DecisionPlayLayout() }) {
            string text = layout.Render(opening.Observation, state);
            Assert.Contains($"身体警告（{attributes}触及警戒线）", text);
        }
        // An old pending warning is not evidence that this advance returned for a new warning.
        var withoutWarningEvents = new CharacterContextOpening(opening.Background, opening.Observation,
            opening.PreviousAdvance, opening.DeliveredAt, opening.ActivityState,
            opening.Experiences.Where(e => e.Event.Kind != PlaygroundObservedEventKind.SurvivalWarning));
        string history = ContextHistoryPlayView.RenderHistory(opening.Observation,
            new CharacterContext(2, CharacterContextStatus.Open, withoutWarningEvents));
        Assert.DoesNotContain("身体警告（", history);
        Assert.DoesNotContain("返回原因：身体警告", history);
    }

    [Theory]
    [InlineData("classic")]
    [InlineData("decision")]
    public async Task Known_reverse_route_is_rendered_from_frozen_material(string layoutId) {
        var initial = PlaygroundGameFactory.CreateSurvival(enableNeeds: false, enableWeather: false);
        var kernel = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor),
            characterController: new Commands(PlaygroundCommand.Go("e-c7"), PlaygroundCommand.Go("e-n4")));
        for (int i = 0; i < 30; i++) {
            await Step(kernel);
            var observation = PlaygroundDecisionContext.Observe(kernel.World, "a");
            if (observation.Journey?.KnownDestinationName != "林间营地") { continue; }
            IPlayLayout layout = layoutId == "classic" ? new ClassicPlayLayout() : new DecisionPlayLayout();
            string text = layout.Render(observation);
            Assert.Contains("从溪流湾出发 → 林间营地", text);
            Assert.Contains("沿林间缓坡向上", text);
            Assert.DoesNotContain("未知目的地", text);
            return;
        }
        Assert.Fail("Return journey was not reached.");
    }

    [Fact]
    public void Default_survival_shows_attribute_forecasts_without_repeating_them_on_actions() {
        PlaygroundWorld world = PlaygroundGameFactory.CreateSurvival().World;
        PlaygroundDecisionContext context = ViewContextFixture.Create(world, "a");
        string before = world.DescribeState();
        string text = PlayView.Render(context.Observation, context.InteractionState);
        Assert.NotNull(context.Observation.Survival);
        Assert.Contains("## 身体状态", text);
        Assert.Contains("| 食物 |", text);
        Assert.Contains("| 饮水 |", text);
        Assert.Contains("| 温暖 |", text);
        Assert.Contains("当前趋势", text);
        Assert.Contains("当前暴露在野外", text);
        Assert.Contains("预计降至警戒线 25", text);
        Assert.Contains("预计耗尽至 0", text);
        Assert.Contains("不补给、保持当前庇护状态，计入已知昼夜变化", text);
        Assert.Contains("各项独立推算，不代表能存活至所有预测时刻", text);
        Assert.Contains("也不预知抵达后的环境", text);
        Assert.DoesNotContain("维持当前趋势", text);
        Assert.DoesNotContain("干木", text);
        Assert.Contains("use item.water", text);
        Assert.Contains(context.Observation.ItemUseDescriptions["item.water"].TrimEnd('。'), text);
        Assert.DoesNotContain("风险提示（不禁止）", text);
        Assert.DoesNotContain("预计最早致命时刻", text);
        Assert.Null(context.Check(PlaygroundCommand.Wait(24)));
        Assert.Null(context.Warning(PlaygroundCommand.Wait(24)));
        Assert.Null(context.Warning(PlaygroundCommand.Go("e-x5")));
        Assert.Null(context.Warning(PlaygroundCommand.Gather("water")));
        Assert.NotNull(context.Check(PlaygroundCommandParser.Parse("use missing")!));
        Assert.NotNull(context.Check(PlaygroundCommandParser.Parse("use item.wood")!));
        Assert.Equal(before, world.DescribeState());
    }

    [Fact]
    public async Task Travel_warning_and_replenished_state_remain_distinct_until_continue() {
        var initial = PreparedNeeds();
        var kernel = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor),
            characterController: new Commands(PlaygroundCommand.Wait(4), PlaygroundCommand.Go("e-b9"),
                PlaygroundCommand.Use("item.water"), PlaygroundCommand.Continue()));
        await Until(kernel, world => PlaygroundActions.IsTravelling(world, "a") &&
            PlaygroundDecisionContext.Observe(world, "a").Survival!.PendingWarnings.Count > 0);
        PlaygroundDecisionContext warned = PlaygroundDecisionContext.Create(kernel.World, "a");
        ModelTime warningAt = warned.Observation.Time;
        ModelTime? arrival = warned.Observation.ArrivalDue;
        string warningView = PlayView.Render(warned.Observation, warned.InteractionState);
        Assert.Contains("生存警告（历史记录，待确认）", warningView);
        Assert.Contains("use item.water", warningView);
        Assert.DoesNotContain("- `cancel`", warningView);
        Assert.Contains("移动途中不可 cancel", warningView);
        Assert.Null(warned.Check(PlaygroundCommand.Use("item.water")));
        Assert.Null(warned.Check(PlaygroundCommand.Continue()));
        foreach (IPlayLayout layout in new IPlayLayout[] { new ClassicPlayLayout(), new DecisionPlayLayout() }) {
            string rendered = layout.Render(warned.Observation, warned.InteractionState);
            Assert.Contains("返回原因：身体警告（饮水触及警戒线）", rendered);
            Assert.Contains("从林间营地出发", rendered);
            Assert.Contains("另一端未知（这条入口去向未确认）", rendered);
            Assert.Contains("e-b9", rendered);
            Assert.Contains("沿窄径攀上风口", rendered);
            Assert.DoesNotContain("风口山脊", rendered);
        }

        await Step(kernel); // drink while the journey stays paused
        PlaygroundDecisionContext replenished = PlaygroundDecisionContext.Create(kernel.World, "a");
        Assert.Equal(warningAt, replenished.Observation.Time);
        Assert.Equal(arrival, replenished.Observation.ArrivalDue);
        Assert.True(replenished.Observation.Survival!.Water > warned.Observation.Survival!.Water);
        Assert.Equal(warned.Observation.Survival.PendingWarnings, replenished.Observation.Survival.PendingWarnings);
        string after = PlayView.Render(replenished.Observation, replenished.InteractionState);
        Assert.Contains("即使已经补给也会保留", after);
        Assert.DoesNotContain("上次结果（记录当时状态）", after);
        Assert.NotNull(replenished.Check(PlaygroundCommand.Use("item.water")));
        Assert.Null(replenished.Check(PlaygroundCommand.Continue()));
        foreach (IPlayLayout layout in new IPlayLayout[] { new ClassicPlayLayout(), new DecisionPlayLayout() }) {
            Assert.Contains("返回原因：身体警告（饮水触及警戒线）", layout.Render(replenished.Observation, replenished.InteractionState));
        }

        await Step(kernel); // acknowledgement retains the original arrival deadline
        PlaygroundObservation continued = PlaygroundDecisionContext.Observe(kernel.World, "a");
        Assert.Empty(continued.Survival!.PendingWarnings);
        Assert.Equal(arrival, continued.ArrivalDue);
        Assert.DoesNotContain("生存警告（历史记录，待确认）", PlayView.Render(continued));
    }

    [Fact]
    public async Task Cold_warning_during_travel_does_not_offer_food_or_water_as_warming() {
        ModelTime start = new(19 * PlaygroundTime.Hour);
        var initial = PreparedNeeds(new PlaygroundSurvival(start, 65 * PlaygroundSurvival.Scale,
            65 * PlaygroundSurvival.Scale, 37 * PlaygroundSurvival.Scale,
            PlaygroundSurvivalRules.FoodRate, PlaygroundSurvivalRules.WaterRate,
            warmthRate: new RateBreakdown([new("test-cold", "测试散热", -12)])));
        var kernel = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor),
            characterController: new Commands(PlaygroundCommand.Go("e-b9")));
        await Until(kernel, world => PlaygroundDecisionContext.Observe(world, "a").Survival!.WarmthWarningPending);
        PlaygroundObservation observation = PlaygroundDecisionContext.Create(kernel.World, "a").Observation;
        Assert.NotNull(observation.ArrivalDue);
        string text = PlayView.Render(observation);
        Assert.Contains(observation.ItemUseDescriptions["item.water"].TrimEnd('。'), text);
        Assert.Contains("当前温暖趋势、阈值预测与眼前补给效果", text);
        Assert.DoesNotContain("现有物品不能恢复温暖", text);
        Assert.Contains("抵达前不能安排庇护停留", text);
        Assert.DoesNotContain("可以先 use 补给", text);
        Assert.DoesNotContain("- `cancel`", text);
        Assert.DoesNotContain("废弃林屋", text);
    }

    [Fact]
    public async Task Warning_during_wait_does_not_claim_the_character_is_idle() {
        var initial = PreparedNeeds();
        var kernel = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor),
            characterController: new Commands(PlaygroundCommand.Wait(6)));
        await Until(kernel, world => PlaygroundDecisionContext.Observe(world, "a").Survival!.PendingWarnings.Count > 0);
        PlaygroundObservation observation = PlaygroundDecisionContext.Create(kernel.World, "a").Observation;
        Assert.NotNull(observation.Actor.Activity);
        string text = PlayView.Render(observation);
        Assert.Contains("原活动进行中", text);
        Assert.Contains("正在等待，完成时刻", text);
        Assert.DoesNotContain("当前空闲", text);
    }

    [Fact]
    public async Task Shelter_observation_explains_warming_without_promising_free_food_or_water() {
        var initial = PreparedNeeds();
        var kernel = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor),
            characterController: new Commands(PlaygroundCommand.Go("e-b9"), PlaygroundCommand.Go("e-u8")));
        await Until(kernel, world => PlaygroundActions.CurrentPlace(world, "a") == "cabin");
        PlaygroundObservation observation = PlaygroundDecisionContext.Create(kernel.World, "a").Observation;
        Assert.True(observation.Survival!.Sheltered);
        Assert.True(observation.Survival.WarmthPerHour > 0);
        Assert.True(observation.Survival.FoodPerHour < 0);
        Assert.True(observation.Survival.WaterPerHour < 0);
        string text = PlayView.Render(observation);
        Assert.Contains("当前有庇护保护", text);
        Assert.Contains("当前温暖正在恢复", text);
        Assert.Contains("当前趋势", text);
        Assert.False(observation.ItemUseDescriptions.ContainsKey("item.wood"));
        Assert.Contains("use item.food", text);
        Assert.Contains(observation.ItemUseDescriptions["item.food"].TrimEnd('。'), text);
        Assert.DoesNotContain("- `use item.wood`", text);
    }

    [Fact]
    public async Task Carried_wood_has_no_unsupported_use_command_after_leaving_its_resource_point() {
        var initial = PreparedNeeds();
        var kernel = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor),
            characterController: new Commands(PlaygroundCommand.Go("e-b9"), PlaygroundCommand.Go("e-u8"),
                PlaygroundCommand.Use("item.water"), PlaygroundCommand.Gather("wood"), PlaygroundCommand.Go("e-t4")));
        await Until(kernel, world => PlaygroundActions.CurrentPlace(world, "a") == "ridge" && world.GetCharacter("a").Backpack.Count("item.wood") > 0);
        PlaygroundObservation observation = PlaygroundDecisionContext.Create(kernel.World, "a").Observation;
        Assert.DoesNotContain(observation.Resources, resource => resource.ItemId == "item.wood");
        string text = PlayView.Render(observation);
        Assert.Contains("干木（`item.wood`）×1", text);
        Assert.False(observation.ItemUseDescriptions.ContainsKey("item.wood"));
        Assert.DoesNotContain("- `use item.wood`", text);
    }

    [Fact]
    public async Task Fatal_needs_show_a_reason_and_no_more_legal_action_menu() {
        var initial = PlaygroundGameFactory.CreateSurvival();
        var kernel = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor),
            characterController: new IgnoreWarnings());
        await Until(kernel, world => world.GetCharacter("a").Ending?.Outcome == PlaygroundExpeditionOutcome.Dead);
        PlaygroundDecisionContext context = PlaygroundDecisionContext.Create(kernel.World, "a");
        PlaygroundObservation observation = context.Observation;
        string text = PlayView.Render(observation, context.InteractionState);
        Assert.Contains("## 结局", text);
        Assert.False(string.IsNullOrWhiteSpace(observation.EndingReason));
        Assert.Contains(observation.EndingReason!, text);
        Assert.DoesNotContain("## 可用命令", text);
        Assert.DoesNotContain("可以先 use", text);
        Assert.Null(observation.Actor.Activity);
        Assert.Equal(CharacterContextStatus.Terminal, context.Context.Status);
        Assert.NotNull(context.Context.Opening.PreviousAdvance?.ActivityId);
        Assert.Contains("未完成的活动不再推进", text);
        Assert.DoesNotContain("正在等待", text);
        Assert.DoesNotContain("预计最早致命时刻", text);
        Assert.DoesNotContain("当前趋势", text);
        Assert.DoesNotContain("下次天", text);
    }

    [Fact]
    public async Task Death_during_travel_keeps_the_unfinished_route_as_history_without_promising_arrival() {
        ModelTime start = new(15 * PlaygroundTime.Hour);
        var initial = PreparedNeeds(new PlaygroundSurvival(start, 65 * PlaygroundSurvival.Scale,
            12 * PlaygroundSurvival.Scale, 60 * PlaygroundSurvival.Scale,
            PlaygroundSurvivalRules.FoodRate, PlaygroundSurvivalRules.WaterRate,
            PlaygroundSurvivalRules.WarmthRate(false, true), warnedMask: 2));
        var kernel = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor),
            characterController: new Commands(PlaygroundCommand.Go("e-b9")));
        await Until(kernel, world => world.GetCharacter("a").Ending?.Outcome == PlaygroundExpeditionOutcome.Dead);
        PlaygroundObservation observation = PlaygroundDecisionContext.Create(kernel.World, "a").Observation;
        Assert.NotNull(observation.ArrivalDue);
        Assert.True(observation.ArrivalDue > observation.Time);
        string text = PlayView.Render(observation);
        Assert.Contains("结束时仍在途中", text);
        Assert.Contains("本段行程未完成，不再推进", text);
        foreach (IPlayLayout layout in new IPlayLayout[] { new ClassicPlayLayout(), new DecisionPlayLayout() }) {
            string ended = layout.Render(observation);
            Assert.Contains("从林间营地出发 → 另一端未知（这条入口去向未确认）", ended);
            Assert.Contains("沿窄径攀上风口", ended);
            Assert.Contains("原计划", ended);
            Assert.DoesNotContain("风口山脊", ended);
        }
        Assert.Contains("移动未完成；原计划到达时刻", text);
        Assert.Contains("| 饮水 | 0 |", text);
        Assert.DoesNotContain("正在途中；预计", text);
        Assert.DoesNotContain("正在移动，预计", text);
        Assert.DoesNotContain("continue", text);
        Assert.DoesNotContain("cancel", text);
        Assert.DoesNotContain("预计最早致命时刻", text);
    }

    private static async Task Step(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel) =>
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(96 * PlaygroundTime.Hour)));

    // Fixed needs keep observation contracts independent of new-game balancing.
    private static (PlaygroundWorld World, KernelCursor Cursor) PreparedNeeds(PlaygroundSurvival? survival = null) {
        var initial = PlaygroundGameFactory.CreateSurvival(survival?.BaselineAt ?? new ModelTime(15 * PlaygroundTime.Hour), enableWeather: false);
        PlaygroundCharacter actor = new("a", "阿青", backpack: new(new Dictionary<string, int> {
            ["item.food"] = 1, ["item.water"] = 1
        }), survival: survival ?? PlaygroundSurvival.Create(initial.World.Time, food: 65, water: 65, warmth: 60));
        PlaygroundBusinessState business = initial.World.Business.Replace(initial.World.Time, changedCharacters: [actor]);
        return (PlaygroundEngine.Rebuild(business, business.Time), initial.Cursor);
    }

    private static async Task Until(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel, Func<PlaygroundWorld, bool> predicate) {
        for (int i = 0; i < 100; i++) {
            if (predicate(kernel.World) && kernel.World.Business.InteractionStates["a"].CurrentContext is {
                Status: CharacterContextStatus.Open or CharacterContextStatus.Terminal }) { return; }
            await Step(kernel);
        }
        Assert.Fail("Expected survival boundary was not reached.");
    }

    private sealed class Commands(params PlaygroundCommand[] commands) : IPlaygroundCharacterController {
        private readonly Queue<PlaygroundCommand> _commands = new(commands);
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PlaygroundControllerOperation("call_" + Guid.NewGuid().ToString("N"),
                PlaygroundCommandParser.Format(_commands.Count > 0 ? _commands.Dequeue() : throw new InvalidOperationException("Unexpected decision.")), Guid.NewGuid().ToString("N")));
    }

    private sealed class IgnoreWarnings : IPlaygroundCharacterController {
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PlaygroundControllerOperation("call_" + Guid.NewGuid().ToString("N"),
                PlaygroundCommandParser.Format(context.Check(PlaygroundCommand.Continue()) is null ? PlaygroundCommand.Continue() : PlaygroundCommand.Wait(24)), Guid.NewGuid().ToString("N")));
    }
}
