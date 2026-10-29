using DramaBoard.Kernel.Time;
using DramaBoard.ResourceCycle;

namespace DramaBoard.ResourceCycle.Cli.Tests;

public sealed class PlayViewTests {
    private static ModelTime Hour(int hour) => new(hour * CycleTime.Hour);

    [Fact]
    public void Finite_capacity_is_visible_only_to_its_owner() {
        CycleCharacter viewer = new("a", "Alice",
            backpack: new(new Dictionary<string, int> { ["ore"] = 1 }, unitCapacity: 3));
        CycleCharacter other = new("b", "Bob", backpack: new(unitCapacity: 5));
        CycleWorld world = World(viewer, other);

        string observation = Render(world, "a");
        Assert.Contains("- 容量：1 / 3 件。", observation);
        Assert.Equal(observation, Render(World(viewer,
            new("b", "Bob", backpack: new(unitCapacity: 99))), "a"));
        Assert.Contains("- 容量：0 / 5 件。", Render(world, "b"));
        Assert.DoesNotContain("- 容量：", Render(World(new CycleCharacter("a", "Alice")), "a"));
    }

    [Fact]
    public void Other_characters_private_state_does_not_change_the_observation() {
        CycleCharacter viewer = new("a", "Alice", notices: [Joined()]);
        CycleCharacter other = new("b", "Bob", activity: Gather(1), nextActivityId: 2);
        CycleCharacter privateVariant = new("b", "Bob", skillLevel: 47,
            backpack: new(new Dictionary<string, int> { ["private.gem"] = 12345 }),
            activity: Gather(3, target: 99), notices: [Depleted()], lastResult: "private result",
            decisionGeneration: 987, nextActivityId: 999);
        CycleWorld baseline = World(viewer, other);
        CycleWorld variant = World(viewer, privateVariant);

        string before = variant.DescribeState();
        string rendered = Render(variant, "a");
        Assert.Equal(Render(baseline, "a"), rendered);
        Assert.Equal(RenderCommands(baseline, "a"), RenderCommands(variant, "a"));
        Assert.Contains("Bob（b），共 1 人", rendered);
        Assert.Contains("Bob（`b`）：正在采集 太阳花", rendered);
        Assert.DoesNotContain("private.gem", rendered);
        Assert.DoesNotContain("private result", rendered);
        Assert.Equal(before, variant.DescribeState());
        Assert.Same(privateVariant, variant.GetCharacter("b"));

        string ownView = Render(variant, "b");
        Assert.Contains("采药等级：47", ownView);
        Assert.Contains("private.gem", ownView);
        Assert.Contains("×12345", ownView);
        Assert.Contains("目标为 99 份", ownView);
        Assert.Contains("private result", ownView);
    }

    [Theory]
    [InlineData(true, "目前仍在此点采集")]
    [InlineData(false, "目前已离开此点的采集")]
    public void Joining_notice_distinguishes_the_historical_event_from_current_activity(bool stillGathering, string currentStatus) {
        CycleCharacter viewer = new("a", "Alice", activity: Gather(1), notices: [Joined(count: 2)], nextActivityId: 2);
        CycleActivity otherActivity = stillGathering ? Gather(2) : new(CycleActivityKind.Wait, 1, Hour(16), CycleTime.Hour, Hour(17));
        string text = Render(World(viewer, new("b", "Bob", activity: otherActivity, nextActivityId: 2)), "a");
        Assert.Contains("第 0 天 15:00：Bob（b）曾加入 太阳花 的采集", text);
        Assert.Contains(currentStatus, text);
        Assert.Contains("待回应同类变化共 2 次，此处为最近一次", text);
        Assert.Contains("你的活动没有中断；当前周期仍于 第 0 天 17:00 结算", text);
    }

    [Fact]
    public void Depletion_notice_keeps_history_separate_from_replenished_stock_and_last_result() {
        CycleCharacter viewer = new("a", "Alice", notices: [Depleted()], lastResult: "当时背包为空");
        CycleWorld world = World(viewer, new("b", "Bob"));
        string text = Render(world, "a");
        Assert.Contains("第 0 天 15:00：太阳花曾被取空", text);
        Assert.Contains("原因：取走最后一份。当前余量 3/5", text);
        Assert.Contains("消息描述已发生的耗尽，不会取消你的在途周期", text);
        Assert.Contains("上次结果（记录当时状态）：当时背包为空", text);
        Assert.Single(viewer.Notices);
        Assert.Null(viewer.Activity);
    }

    [Fact]
    public void Commands_report_current_blockers_without_acknowledging_notices() {
        CycleCharacter viewer = new("a", "Alice", activity: Gather(1), notices: [Joined()], nextActivityId: 2);
        CycleWorld world = World(viewer, new("b", "Bob"));
        string commands = RenderCommands(world, "a");
        Assert.Contains("- `continue`：确认感知并交还控制权，保留活动和进度；可执行。", commands);
        Assert.Contains("- `gather sunflower all`", commands);
        Assert.Contains("受阻：已有耗时活动，请先 cancel。", commands);
        Assert.Contains("小时数 1–24 受阻", commands);
        Assert.Single(viewer.Notices);
        Assert.Same(viewer.Activity, world.GetCharacter("a").Activity);
    }

    private static string Render(CycleWorld world, string actorId) {
        CycleDecisionContext context = ViewContextFixture.Create(world, actorId);
        return PlayView.Render(context.Observation, context.InteractionState);
    }
    private static string RenderCommands(CycleWorld world, string actorId) => PlayView.RenderCommands(ViewContextFixture.Create(world, actorId).Observation);

    private static CycleWorld World(params CycleCharacter[] actors) => CycleGameFactory.CreatePlayable(Hour(16), actors).World;
    private static CycleActivity Gather(int hours, int target = 3) =>
        new(CycleActivityKind.Gather, 1, Hour(16), hours * CycleTime.Hour, Hour(16 + hours), "sunflower", targetCount: target);
    private static CycleNotice Joined(int count = 1) =>
        new(CycleNoticeKind.CollectorJoined, "sunflower", "b", "b:1", Hour(15), "joined", count);
    private static CycleNotice Depleted() =>
        new(CycleNoticeKind.ResourceDepleted, "sunflower", "b", "b:yield", Hour(15), "取走最后一份！ ");
}
