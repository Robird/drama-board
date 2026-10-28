using DramaBoard.Kernel.Time;
using DramaBoard.ResourceCycle;

namespace DramaBoard.ResourceCycle.Cli.Tests;

public sealed class ContinuityViewTests {
    [Fact]
    public void Both_layouts_expose_sealed_action_handoff_and_current_draft_without_mutating_history() {
        CycleWorld world = CycleGameFactory.CreateSurvival().World;
        CycleCharacter actor = world.GetCharacter("a");
        var action = new CycleAcceptedAction("a", 1, "endpoint-call", "record", CycleAcceptedActionKind.Wait,
            new ModelTime(14 * CycleTime.Hour), hours: 1, activityId: 1);
        var before = new CycleContinuitySnapshot(60 * CycleSurvival.Scale, 50 * CycleSurvival.Scale, 40 * CycleSurvival.Scale,
            new Dictionary<string, int> { ["item.water"] = 2 }, placeId: "old-place", placeName: "历史出发点");
        var after = new CycleContinuitySnapshot(55 * CycleSurvival.Scale, 45 * CycleSurvival.Scale, 35 * CycleSurvival.Scale,
            new Dictionary<string, int> { ["item.water"] = 2 }, placeId: "old-place", placeName: "历史交付点");
        var summary = new CycleOutcomeSummary(calmWeatherCount: 2, coldWeatherCount: 3,
            weatherWarmthLossUnits: 5 * CycleSurvival.Scale, lightChangeCount: 1,
            reasons: CycleWakeReason.SurvivalWarning, warningMask: 4);
        var incoming = new CycleActionOutcome(new CyclePendingAction(action, before, summary, "交接：先检查保暖"),
            world.Time, after, CycleOutcomeActivityState.Ongoing);
        var continuity = new CycleDecisionContinuity(2, incoming, "草稿：下次考虑补给");
        var replacement = new CycleCharacter(actor.Id, actor.Name, actor.SkillLevel, backpack: new(),
            survival: actor.Survival, continuity: continuity);
        world = CycleEngine.Rebuild(world.Business.Replace(world.Time, changedCharacters: [replacement]), world.Time);
        CycleObservation observation = CycleDecisionContext.Create(world, "a").Observation;
        Assert.Empty(observation.Actor.Backpack.Items);
        Assert.Equal("饮水", observation.ItemNames["item.water"]);
        string historyBefore = incoming.ToString();

        foreach (IPlayLayout layout in new IPlayLayout[] { new ClassicPlayLayout(), new DecisionPlayLayout() }) {
            string rendered = layout.Render(observation);
            // Semantic presence only: no section ordering, coordinates, line count or screen snapshot.
            Assert.Contains("wait 1", rendered);
            Assert.Contains("历史出发点", rendered);
            Assert.Contains("历史交付点", rendered);
            Assert.Contains(incoming.Handoff!, rendered);
            Assert.Contains(continuity.DraftHandoff!, rendered);
            Assert.Contains("handoff --clear", rendered);
            Assert.Contains("原活动仍在进行", rendered);
            Assert.Contains("天气实际温暖损失", rendered);
        }
        Assert.Equal(historyBefore, incoming.ToString());
        Assert.Same(incoming, observation.Continuity!.Incoming);
    }
}
