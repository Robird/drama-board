using DramaBoard.Kernel.Time;
using DramaBoard.Playground;

namespace DramaBoard.Playground.Cli.Tests;

public sealed class ContextHistoryViewTests {
    [Fact]
    public void Handoff_help_uses_scenario_commands_instead_of_context_presence() {
        PlaygroundObservation survival = PlaygroundDecisionContext.Observe(PlaygroundGameFactory.CreateSurvival().World, "a");
        PlaygroundObservation gathering = PlaygroundDecisionContext.Observe(PlaygroundGameFactory.CreatePlayable().World, "a");
        var state = ViewContextFixture.State(gathering);
        foreach (IPlayLayout layout in new IPlayLayout[] { new ClassicPlayLayout(), new DecisionPlayLayout() }) {
            Assert.Contains("`handoff <文字>`", layout.Render(survival));
            Assert.Contains("`handoff <文字>`", layout.RenderActionHelp(survival));
            Assert.DoesNotContain("`handoff <文字>`", layout.Render(gathering, state));
            Assert.DoesNotContain("`handoff <文字>`", layout.RenderActionHelp(gathering));
            Assert.DoesNotContain("本轮交接草稿", layout.Render(gathering, state));
        }
    }

    [Fact]
    public void Both_layouts_render_frozen_action_and_ordered_experiences_with_current_draft() {
        PlaygroundObservation observation = PlaygroundDecisionContext.Observe(PlaygroundGameFactory.CreateSurvival().World, "a");
        var action = new PendingAdvance("a", 1, 1, PlaygroundCommand.Go("trail-1"), "go trail-1", "endpoint-call", "record",
            new ModelTime(14 * PlaygroundTime.Hour), "接受时的旧通路线索", "历史出发点", movementGeneration: 1,
            handoff: "交接：先检查保暖");
        var first = new CharacterExperience(1, new PlaygroundObservedEvent(0, "a", PlaygroundObservedEventKind.WeatherResolved,
            action.AcceptedAt, observation.Time, detail: "ColdWind", effects: [
                new(new("body", "a", "warmth"), PlaygroundVisibleValue.Numeric(40, "点"), PlaygroundVisibleValue.Numeric(35, "点"))]));
        var second = new CharacterExperience(2, new PlaygroundObservedEvent(1, "a", PlaygroundObservedEventKind.BodyChanged,
            action.AcceptedAt, observation.Time, effects: [
                new(new("body", "a", "warmth"), PlaygroundVisibleValue.Numeric(35, "点"), PlaygroundVisibleValue.Numeric(40, "点"))]));
        var opening = new CharacterContextOpening(PlaygroundBackground.For(observation), observation, action, observation.Time,
            PlaygroundOutcomeActivityState.Ongoing, [first, second]);
        var context = new CharacterContext(2, CharacterContextStatus.Open, opening);
        var state = new CharacterInteractionState("a", context, draftHandoff: "草稿：下次考虑补给", nextExperienceSequence: 3);

        foreach (IPlayLayout layout in new IPlayLayout[] { new ClassicPlayLayout(), new DecisionPlayLayout() }) {
            string rendered = layout.Render(observation, state);
            Assert.Contains("`go trail-1`", rendered);
            Assert.Contains("接受时的旧通路线索", rendered);
            Assert.Contains("历史出发点", rendered);
            Assert.Contains(action.Handoff!, rendered);
            Assert.Contains(state.DraftHandoff!, rendered);
            Assert.Contains("handoff --clear", rendered);
            Assert.Contains("原活动仍在进行", rendered);
            Assert.Contains("寒风袭来", rendered);
            Assert.Contains("温暖：40点 → 35点", rendered);
            Assert.Contains("温暖：35点 → 40点", rendered); // Net-zero process must not disappear.
        }
        Assert.Same(action, state.CurrentContext!.Opening.PreviousAdvance);
        Assert.Equal([first, second], state.CurrentContext.Opening.Experiences);
    }
    [Fact]
    public void Frozen_opening_notices_keep_return_reasons_after_current_acknowledgement() {
        PlaygroundWorld world = PlaygroundGameFactory.CreatePlayable().World;
        PlaygroundCharacter actor = world.GetCharacter("a");
        var replacement = new PlaygroundCharacter(actor.Id, actor.Name, actor.SkillLevel, actor.Backpack,
            notices: [new PlaygroundNotice(PlaygroundNoticeKind.CollectorJoined, "sunflower", "b", "joined", world.Time, "加入"),
                new PlaygroundNotice(PlaygroundNoticeKind.ResourceDepleted, "sunflower", "b", "empty", world.Time, "取空")]);
        PlaygroundWorld notified = PlaygroundEngine.Rebuild(world.Business.Replace(world.Time, changedCharacters: [replacement]), world.Time);
        PlaygroundObservation openingObservation = PlaygroundDecisionContext.Observe(notified, "a");
        var advance = new PendingAdvance("a", 1, 1, PlaygroundCommand.Gather("sunflower"), "gather sunflower 1", "call", "record", world.Time,
            "gather sunflower 1", activityId: 1);
        var opening = new CharacterContextOpening(PlaygroundBackground.For(openingObservation), openingObservation,
            advance, world.Time, PlaygroundOutcomeActivityState.Ongoing);
        var state = new CharacterInteractionState("a", new CharacterContext(2, CharacterContextStatus.Open, opening));
        string history = ContextHistoryPlayView.RenderHistory(PlaygroundDecisionContext.Observe(world, "a"), state);
        Assert.Contains("有人加入采集", history);
        Assert.Contains("资源耗尽", history);
        Assert.Contains("曾加入", history);
        Assert.Contains("曾被取空", history);
    }

    [Fact]
    public void Terminal_without_pending_advance_is_not_described_as_first_decision() {
        PlaygroundObservation observation = PlaygroundDecisionContext.Observe(PlaygroundGameFactory.CreateSurvival().World, "a");
        var opening = new CharacterContextOpening(PlaygroundBackground.For(observation), observation, null,
            observation.Time, PlaygroundOutcomeActivityState.Terminal);
        var state = new CharacterInteractionState("a", new CharacterContext(2, CharacterContextStatus.Terminal, opening));
        string history = ContextHistoryPlayView.RenderHistory(observation, state);
        Assert.Contains("旅程已结束", history);
        Assert.Contains("旅程终局", history);
        Assert.DoesNotContain("首次决策", history);
    }

    [Theory]
    [InlineData("decision")]
    [InlineData("classic")]
    public void Terminal_keeps_historical_handoff_and_final_experience_without_a_future_draft_or_input(string layoutId) {
        var original = PlaygroundDecisionContext.Observe(PlaygroundGameFactory.CreateSurvival().World, "a");
        var observation = new PlaygroundObservation {
            Time = original.Time, IsDay = original.IsDay, NextLightChange = original.NextLightChange,
            Actor = original.Actor, Resources = original.Resources, HarvestDurations = original.HarvestDurations,
            WorldName = original.WorldName, ItemUseDescriptions = original.ItemUseDescriptions, ItemNames = original.ItemNames, OtherCharacters = original.OtherCharacters, Notices = original.Notices,
            Commands = [], IsExpedition = true, Outcome = PlaygroundExpeditionOutcome.Dead, EndingReason = "终局原因"
        };
        var advance = new PendingAdvance("a", 1, 1, PlaygroundCommand.Wait(1), "wait 1", "call", "record", original.Time,
            "原地等待", activityId: 1, handoff: "旧交接里提到 :exit 只是文字");
        var ending = new CharacterExperience(1, new PlaygroundObservedEvent(0, "a", PlaygroundObservedEventKind.ExpeditionEnded,
            original.Time, observation.Time, detail: "末条感知独有文字"));
        var state = new CharacterInteractionState("a", new CharacterContext(2, CharacterContextStatus.Terminal,
            new CharacterContextOpening(PlaygroundBackground.For(observation), observation, advance, observation.Time,
                PlaygroundOutcomeActivityState.Terminal, [ending])), nextExperienceSequence: 2);
        string text = CharacterContextRenderer.RenderFull(state, PlayPresentation.ForMode(true).FindLayout(layoutId)!);
        Assert.Contains(advance.Handoff!, text);
        Assert.Contains("旅程", text);
        Assert.Contains("终局原因", text);
        Assert.Contains("末条感知独有文字", text);
        Assert.Contains(CharacterContextRenderer.BeginMarker(state), text);
        Assert.Contains(CharacterContextRenderer.EndMarker(state), text);
        Assert.DoesNotContain("本轮交接草稿", text);
        Assert.DoesNotContain("下一次放行模拟时发送", text);
        Assert.DoesNotContain("handoff --clear", text);
        Assert.DoesNotContain("现在可输入", text);
    }

}
