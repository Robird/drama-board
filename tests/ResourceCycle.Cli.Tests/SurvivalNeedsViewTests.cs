using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.ResourceCycle;

namespace DramaBoard.ResourceCycle.Cli.Tests;

public sealed class SurvivalNeedsViewTests {
    [Fact]
    public void Default_survival_shows_current_needs_supplies_and_nonblocking_action_risks() {
        CycleWorld world = CycleGameFactory.CreateSurvival().World;
        CycleDecisionContext context = CycleDecisionContext.Create(world, "a");
        string before = world.DescribeState();
        string text = PlayView.Render(context.Observation);
        Assert.NotNull(context.Observation.Survival);
        Assert.Contains("## 身体状态", text);
        Assert.Contains("| 食物 |", text);
        Assert.Contains("| 饮水 |", text);
        Assert.Contains("| 温暖 |", text);
        Assert.Contains("当前趋势", text);
        Assert.Contains("当前暴露在野外", text);
        Assert.Contains("预计最早致命时刻", text);
        Assert.Contains("保持当前暴露或庇护状态、不补给，计入已知昼夜变化", text);
        Assert.Contains("也不预知抵达后的环境", text);
        Assert.DoesNotContain("维持当前趋势", text);
        Assert.DoesNotContain("干木", text);
        Assert.Contains("use item.water", text);
        Assert.Contains(CycleSurvivalRules.UseDescription("item.water")!.TrimEnd('。'), text);
        Assert.Contains("风险提示（不禁止）", text);
        Assert.Null(context.Check(CycleCommand.Wait(24)));
        Assert.NotNull(context.Warning(CycleCommand.Wait(24)));
        Assert.Null(context.Warning(CycleCommand.Go("trail-6")));
        Assert.Null(context.Warning(CycleCommand.Gather("water")));
        Assert.NotNull(context.Check(PlayInput.Parse("use missing").Command));
        Assert.NotNull(context.Check(PlayInput.Parse("use item.wood").Command));
        Assert.Equal(before, world.DescribeState());
    }

    [Fact]
    public async Task Travel_warning_and_replenished_state_remain_distinct_until_continue() {
        var initial = PreparedNeeds();
        var kernel = CycleEngine.CreateKernel(new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor),
            characterController: new Commands(CycleCommand.Wait(4), CycleCommand.Go("trail-2"),
                CycleCommand.Use("item.water"), CycleCommand.Continue()));
        await Until(kernel, world => CycleActions.IsTravelling(world, "a") &&
            CycleDecisionContext.Create(world, "a").Observation.Survival!.PendingWarnings.Count > 0);
        CycleDecisionContext warned = CycleDecisionContext.Create(kernel.World, "a");
        ModelTime warningAt = warned.Observation.Time;
        ModelTime? arrival = warned.Observation.ArrivalDue;
        string warningView = PlayView.Render(warned.Observation);
        Assert.Contains("生存警告（历史记录，待确认）", warningView);
        Assert.Contains("use item.water", warningView);
        Assert.DoesNotContain("- `cancel`", warningView);
        Assert.Contains("移动途中不可 cancel", warningView);
        Assert.Null(warned.Check(CycleCommand.Use("item.water")));
        Assert.Null(warned.Check(CycleCommand.Continue()));

        await Step(kernel); // drink while the journey stays paused
        CycleDecisionContext replenished = CycleDecisionContext.Create(kernel.World, "a");
        Assert.Equal(warningAt, replenished.Observation.Time);
        Assert.Equal(arrival, replenished.Observation.ArrivalDue);
        Assert.True(replenished.Observation.Survival!.Water > warned.Observation.Survival!.Water);
        Assert.Equal(warned.Observation.Survival.PendingWarnings, replenished.Observation.Survival.PendingWarnings);
        string after = PlayView.Render(replenished.Observation);
        Assert.Contains("即使已经补给也会保留", after);
        Assert.Contains("上次结果（记录当时状态）", after);
        Assert.NotNull(replenished.Check(CycleCommand.Use("item.water")));
        Assert.Null(replenished.Check(CycleCommand.Continue()));

        await Step(kernel); // acknowledgement retains the original arrival deadline
        CycleObservation continued = CycleDecisionContext.Create(kernel.World, "a").Observation;
        Assert.Empty(continued.Survival!.PendingWarnings);
        Assert.Equal(arrival, continued.ArrivalDue);
        Assert.DoesNotContain("生存警告（历史记录，待确认）", PlayView.Render(continued));
    }

    [Fact]
    public async Task Cold_warning_during_travel_does_not_offer_food_or_water_as_warming() {
        ModelTime start = new(19 * CycleTime.Hour);
        var initial = PreparedNeeds(new CycleSurvival(start, 65 * CycleSurvival.Scale,
            65 * CycleSurvival.Scale, 37 * CycleSurvival.Scale, warmthPerHour: -12));
        var kernel = CycleEngine.CreateKernel(new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor),
            characterController: new Commands(CycleCommand.Go("trail-2")));
        await Until(kernel, world => CycleDecisionContext.Create(world, "a").Observation.Survival!.WarmthWarningPending);
        CycleObservation observation = CycleDecisionContext.Create(kernel.World, "a").Observation;
        Assert.NotNull(observation.ArrivalDue);
        string text = PlayView.Render(observation);
        Assert.Contains("use 只补充食物或饮水", text);
        Assert.Contains("现有物品不能恢复温暖", text);
        Assert.Contains("途中没有主动保暖操作，抵达前不能安排庇护停留", text);
        Assert.DoesNotContain("可以先 use 补给", text);
        Assert.DoesNotContain("- `cancel`", text);
        Assert.DoesNotContain("废弃林屋", text);
    }

    [Fact]
    public async Task Warning_during_wait_does_not_claim_the_character_is_idle() {
        var initial = PreparedNeeds();
        var kernel = CycleEngine.CreateKernel(new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor),
            characterController: new Commands(CycleCommand.Wait(6)));
        await Until(kernel, world => CycleDecisionContext.Create(world, "a").Observation.Survival!.PendingWarnings.Count > 0);
        CycleObservation observation = CycleDecisionContext.Create(kernel.World, "a").Observation;
        Assert.NotNull(observation.Actor.Activity);
        string text = PlayView.Render(observation);
        Assert.Contains("原活动进行中", text);
        Assert.Contains("正在等待，完成时刻", text);
        Assert.DoesNotContain("当前空闲", text);
    }

    [Fact]
    public async Task Shelter_observation_explains_warming_without_promising_free_food_or_water() {
        var initial = PreparedNeeds();
        var kernel = CycleEngine.CreateKernel(new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor),
            characterController: new Commands(CycleCommand.Go("trail-2"), CycleCommand.Go("trail-5")));
        await Until(kernel, world => CycleActions.CurrentPlace(world, "a") == "cabin");
        CycleObservation observation = CycleDecisionContext.Create(kernel.World, "a").Observation;
        Assert.True(observation.Survival!.Sheltered);
        Assert.True(observation.Survival.WarmthPerHour > 0);
        Assert.True(observation.Survival.FoodPerHour < 0);
        Assert.True(observation.Survival.WaterPerHour < 0);
        string text = PlayView.Render(observation);
        Assert.Contains("当前有庇护，可避风回暖", text);
        Assert.Contains("食物和饮水仍会消耗", text);
        Assert.Contains("干木目前不能生火或直接取暖", text);
        Assert.Contains("use item.food", text);
        Assert.Contains(CycleSurvivalRules.UseDescription("item.food")!.TrimEnd('。'), text);
        Assert.DoesNotContain("- `use item.wood`", text);
    }

    [Fact]
    public async Task Carried_wood_keeps_its_usage_limit_visible_after_leaving_its_resource_point() {
        var initial = PreparedNeeds();
        var kernel = CycleEngine.CreateKernel(new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor),
            characterController: new Commands(CycleCommand.Go("trail-2"), CycleCommand.Go("trail-5"),
                CycleCommand.Use("item.water"), CycleCommand.Gather("wood"), CycleCommand.Go("trail-5")));
        await Until(kernel, world => CycleActions.CurrentPlace(world, "a") == "ridge" && world.GetCharacter("a").Backpack.Count("item.wood") > 0);
        CycleObservation observation = CycleDecisionContext.Create(kernel.World, "a").Observation;
        Assert.DoesNotContain(observation.Resources, resource => resource.ItemId == "item.wood");
        string text = PlayView.Render(observation);
        Assert.Contains("干木（`item.wood`）×1", text);
        Assert.Contains("干木目前不能生火或直接取暖", text);
        Assert.DoesNotContain("- `use item.wood`", text);
    }

    [Fact]
    public async Task Fatal_needs_show_a_reason_and_no_more_legal_action_menu() {
        var initial = CycleGameFactory.CreateSurvival();
        var kernel = CycleEngine.CreateKernel(new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor),
            characterController: new IgnoreWarnings());
        await Until(kernel, world => world.Expedition!.Outcome == CycleExpeditionOutcome.Dead);
        CycleObservation observation = CycleDecisionContext.Create(kernel.World, "a").Observation;
        string text = PlayView.Render(observation);
        Assert.Contains("## 结局", text);
        Assert.False(string.IsNullOrWhiteSpace(observation.EndingReason));
        Assert.Contains(observation.EndingReason!, text);
        Assert.DoesNotContain("## 可用命令", text);
        Assert.DoesNotContain("可以先 use", text);
        Assert.NotNull(observation.Actor.Activity);
        Assert.Contains("等待未完成", text);
        Assert.Contains("此记录已冻结，不再结算", text);
        Assert.DoesNotContain("正在等待", text);
        Assert.DoesNotContain("预计最早致命时刻", text);
        Assert.DoesNotContain("当前趋势", text);
        Assert.DoesNotContain("下次天", text);
    }

    [Fact]
    public async Task Death_during_travel_keeps_the_unfinished_route_as_history_without_promising_arrival() {
        ModelTime start = new(15 * CycleTime.Hour);
        var initial = PreparedNeeds(new CycleSurvival(start, 65 * CycleSurvival.Scale,
            12 * CycleSurvival.Scale, 60 * CycleSurvival.Scale, warnedMask: 2));
        var kernel = CycleEngine.CreateKernel(new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor),
            characterController: new Commands(CycleCommand.Go("trail-2")));
        await Until(kernel, world => world.Expedition!.Outcome == CycleExpeditionOutcome.Dead);
        CycleObservation observation = CycleDecisionContext.Create(kernel.World, "a").Observation;
        Assert.NotNull(observation.ArrivalDue);
        Assert.True(observation.ArrivalDue > observation.Time);
        string text = PlayView.Render(observation);
        Assert.Contains("结束时仍在途中", text);
        Assert.Contains("本段行程未完成，不再推进", text);
        Assert.Contains("移动未完成；原计划到达时刻", text);
        Assert.Contains("| 饮水 | 0 |", text);
        Assert.DoesNotContain("正在途中；预计", text);
        Assert.DoesNotContain("正在移动，预计", text);
        Assert.DoesNotContain("continue", text);
        Assert.DoesNotContain("cancel", text);
        Assert.DoesNotContain("预计最早致命时刻", text);
    }

    private static async Task Step(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel) =>
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(96 * CycleTime.Hour)));

    // Fixed needs keep observation contracts independent of new-game balancing.
    private static (CycleWorld World, KernelCursor Cursor) PreparedNeeds(CycleSurvival? survival = null) {
        var initial = CycleGameFactory.CreateSurvival(survival?.BaselineAt ?? new ModelTime(15 * CycleTime.Hour), enableWeather: false);
        CycleCharacter actor = new("a", "阿青", backpack: new(new Dictionary<string, int> {
            ["item.food"] = 1, ["item.water"] = 1
        }), survival: survival ?? CycleSurvival.Create(initial.World.Time, food: 65, water: 65, warmth: 60));
        CycleBusinessState business = initial.World.Business.Replace(initial.World.Time, changedCharacters: [actor]);
        return (CycleEngine.Rebuild(business, business.Time), initial.Cursor);
    }

    private static async Task Until(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel, Func<CycleWorld, bool> predicate) {
        for (int i = 0; i < 100; i++) {
            if (predicate(kernel.World)) { return; }
            await Step(kernel);
        }
        Assert.Fail("Expected survival boundary was not reached.");
    }

    private sealed class Commands(params CycleCommand[] commands) : ICycleCharacterController {
        private readonly Queue<CycleCommand> _commands = new(commands);
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CycleControllerOperation("call_" + Guid.NewGuid().ToString("N"),
                _commands.Count > 0 ? _commands.Dequeue() : throw new InvalidOperationException("Unexpected decision."), Guid.NewGuid().ToString("N")));
    }

    private sealed class IgnoreWarnings : ICycleCharacterController {
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CycleControllerOperation("call_" + Guid.NewGuid().ToString("N"),
                context.Check(CycleCommand.Continue()) is null ? CycleCommand.Continue() : CycleCommand.Wait(24), Guid.NewGuid().ToString("N")));
    }
}
