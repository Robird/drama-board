using DramaBoard.ResourceCycle;

namespace DramaBoard.ResourceCycle.Cli.Tests;

public sealed class SharedGameViewBoundaryTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shared_views_do_not_generate_console_guidance(bool terminal) {
        CycleObservation observation = Observe(terminal);
        var continuity = new CycleDecisionContinuity();
        CycleViewSnapshot snapshot = CycleViewSnapshot.Create(observation, continuity);
        foreach (IPlayLayout layout in new IPlayLayout[] { new ClassicPlayLayout(), new DecisionPlayLayout() }) {
            AssertGameContent(layout.RenderFull(snapshot));
            AssertGameContent(layout.RenderActionHelp(observation));
            foreach (bool fullTail in new[] { false, true }) {
                var update = CycleViewUpdate.Create(CycleViewSnapshot.Create(Observe(), new CycleDecisionContinuity()), snapshot, [],
                    PlayDeliveryPhase.Advancing, fullTail);
                AssertGameContent(layout.RenderUpdate(update));
            }
        }
        AssertGameContent(PlayView.Render(observation, continuity));
        AssertGameContent(PlayView.RenderCommands(observation));
        AssertGameContent(ContinuityPlayView.RenderHistory(observation, continuity) + ContinuityPlayView.RenderCurrent(observation, continuity));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Command_shaped_handoff_data_survives_full_and_incremental_views(bool terminal) {
        const string handoff = "记录 :exit 与 :help 都是这段文字的一部分。";
        CycleObservation observation = Observe(terminal);
        var continuity = new CycleDecisionContinuity(draftHandoff: handoff);
        CycleViewSnapshot snapshot = CycleViewSnapshot.Create(observation, continuity);
        foreach (IPlayLayout layout in new IPlayLayout[] { new ClassicPlayLayout(), new DecisionPlayLayout() }) {
            Assert.Contains(handoff, layout.RenderFull(snapshot));
            var update = CycleViewUpdate.Create(CycleViewSnapshot.Create(Observe(), new CycleDecisionContinuity()), snapshot, [],
                PlayDeliveryPhase.Advancing);
            Assert.Contains(handoff, layout.RenderUpdate(update));
        }
        Assert.Contains(handoff, ContinuityPlayView.RenderHistory(observation, continuity) + ContinuityPlayView.RenderCurrent(observation, continuity));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Session_uses_its_directory_for_full_screen_hints_and_records_the_complete_reply(bool enabled) {
        var presentation = PlayPresentation.ForMode(true);
        ConsoleMetaCommands commands = enabled ? ConsoleMetaCommands.HumanDefault : ConsoleMetaCommands.None;
        using var writer = new StringWriter();
        var session = new PlayPresentationSession(presentation, commands, writer);
        CycleObservation observation = Observe();
        session.EnterInput(observation, new CycleDecisionContinuity());
        if (enabled) { Assert.Contains(":help", writer.ToString()); }
        else { AssertGameContent(writer.ToString(), checkPhase: false); }
        writer.GetStringBuilder().Clear();
        string? recorded = null;
        session.ShowFull(observation, text => recorded = text, "重绘完成。\n", new CycleDecisionContinuity());
        Assert.NotNull(recorded);
        Assert.Equal(recorded + writer.NewLine, writer.ToString());
        Assert.Contains("重绘完成。", recorded);
        if (enabled) { Assert.Contains(commands.RenderHint(presentation), recorded); }
        else { AssertGameContent(recorded, checkPhase: false); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Terminal_delivery_has_frozen_state_without_interactive_advertising(bool coldOpen) {
        using var writer = new StringWriter();
        var session = new PlayPresentationSession(PlayPresentation.ForMode(true), ConsoleMetaCommands.HumanDefault, writer);
        if (!coldOpen) {
            session.EnterInput(Observe(), new CycleDecisionContinuity());
            writer.GetStringBuilder().Clear();
            session.BeginAdvancing();
            session.Commit(Observe(terminal: true), [], continuity: new CycleDecisionContinuity());
        } else {
            session.ShowFull(Observe(terminal: true), continuity: new CycleDecisionContinuity());
        }
        Assert.True(session.TerminalDelivered);
        Assert.Contains("冻结", writer.ToString());
        AssertGameContent(writer.ToString());
    }

    private static void AssertGameContent(string text, bool checkPhase = true) {
        foreach (string command in new[] { ":look", ":help", ":details", ":layout", ":mode", ":exit", ":quit", "EOF" }) {
            Assert.DoesNotContain(command, text);
        }
        if (checkPhase) {
            Assert.DoesNotContain("现在可输入", text);
            Assert.DoesNotContain("等待输入", text);
            Assert.DoesNotContain("交互阶段", text);
        }
    }

    private static CycleObservation Observe(bool terminal = false) {
        CycleObservation initial = CycleDecisionContext.Create(CycleGameFactory.CreateSurvival().World, "a").Observation;
        return new CycleObservation {
            Time = initial.Time, IsDay = initial.IsDay, NextLightChange = initial.NextLightChange,
            Actor = initial.Actor, Resources = initial.Resources, HarvestDurations = initial.HarvestDurations,
            ItemNames = initial.ItemNames, OtherCharacters = initial.OtherCharacters, Notices = initial.Notices,
            Commands = initial.Commands, IsExpedition = true, PlaceId = initial.PlaceId, PlaceName = initial.PlaceName,
            PlaceDescription = initial.PlaceDescription, Passages = initial.Passages,
            Survival = initial.Survival, Weather = initial.Weather,
            Outcome = terminal ? CycleExpeditionOutcome.Dead : CycleExpeditionOutcome.Exploring,
            EndingReason = terminal ? "测试终局" : null
        };
    }
}
