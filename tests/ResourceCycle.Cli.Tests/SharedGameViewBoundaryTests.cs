using DramaBoard.ResourceCycle;

namespace DramaBoard.ResourceCycle.Cli.Tests;

public sealed class SharedGameViewBoundaryTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shared_views_do_not_generate_console_guidance(bool terminal) {
        CycleObservation observation = Observe(terminal);
        var interactionState = ViewContextFixture.State(Observe());
        CycleViewSnapshot snapshot = CycleViewSnapshot.Create(observation, interactionState);
        foreach (IPlayLayout layout in new IPlayLayout[] { new ClassicPlayLayout(), new DecisionPlayLayout() }) {
            AssertGameContent(layout.RenderFull(snapshot));
            AssertGameContent(layout.RenderActionHelp(observation));
            foreach (bool fullTail in new[] { false, true }) {
                var update = CycleViewUpdate.Create(CycleViewSnapshot.Create(Observe(), ViewContextFixture.State(Observe())), snapshot, [],
                    PlayDeliveryPhase.Advancing, fullTail);
                AssertGameContent(layout.RenderUpdate(update));
            }
        }
        AssertGameContent(PlayView.Render(observation, interactionState));
        AssertGameContent(PlayView.RenderCommands(observation));
        AssertGameContent(ContextHistoryPlayView.RenderHistory(observation, interactionState) + ContextHistoryPlayView.RenderCurrent(observation, interactionState));
    }

    [Fact]
    public void Command_shaped_draft_data_survives_full_and_incremental_views_while_context_is_open() {
        const string handoff = "记录 :exit 与 :help 都是这段文字的一部分。";
        CycleObservation observation = Observe();
        var interactionState = ViewContextFixture.State(Observe(), draftHandoff: handoff);
        CycleViewSnapshot snapshot = CycleViewSnapshot.Create(observation, interactionState);
        foreach (IPlayLayout layout in new IPlayLayout[] { new ClassicPlayLayout(), new DecisionPlayLayout() }) {
            Assert.Contains(handoff, layout.RenderFull(snapshot));
            var update = CycleViewUpdate.Create(CycleViewSnapshot.Create(Observe(), ViewContextFixture.State(Observe())), snapshot, [],
                PlayDeliveryPhase.Advancing);
            Assert.Contains(handoff, layout.RenderUpdate(update));
        }
        Assert.Contains(handoff, ContextHistoryPlayView.RenderHistory(observation, interactionState) + ContextHistoryPlayView.RenderCurrent(observation, interactionState));
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
        session.EnterInput(ViewContextFixture.State(observation));
        if (enabled) { Assert.Contains(":help", writer.ToString()); }
        else { AssertGameContent(writer.ToString(), checkPhase: false); }
        writer.GetStringBuilder().Clear();
        string? recorded = null;
        session.ShowCurrent(ViewContextFixture.State(observation), text => recorded = text, "重绘完成。\n");
        Assert.NotNull(recorded);
        Assert.Contains(recorded, writer.ToString());
        Assert.Contains(PlayPresentationSession.OutOfBandStart, writer.ToString());
        Assert.Contains(PlayPresentationSession.OutOfBandEnd, writer.ToString());
        Assert.Contains("重绘完成。", recorded);
        AssertGameContent(recorded, checkPhase: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Terminal_delivery_has_frozen_state_without_interactive_advertising(bool coldOpen) {
        using var writer = new StringWriter();
        var session = new PlayPresentationSession(PlayPresentation.ForMode(true), ConsoleMetaCommands.HumanDefault, writer);
        if (!coldOpen) {
            session.EnterInput(ViewContextFixture.State(Observe()));
            writer.GetStringBuilder().Clear();
            session.Commit(Terminal());
        } else {
            session.ShowFull(Terminal());
        }
        Assert.True(session.TerminalDelivered);
        Assert.Contains("冻结", writer.ToString());
        AssertGameContent(writer.ToString());
    }

    private static CharacterInteractionState Terminal() {
        var observation = Observe(terminal: true);
        return new("a", new CharacterContext(2, CharacterContextStatus.Terminal,
            new CharacterContextOpening(CycleBackground.For(observation), observation, null, observation.Time,
                CycleOutcomeActivityState.Terminal)));
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
        CycleObservation initial = ViewContextFixture.Create(CycleGameFactory.CreateSurvival().World, "a").Observation;
        return new CycleObservation {
            Time = initial.Time, IsDay = initial.IsDay, NextLightChange = initial.NextLightChange,
            Actor = initial.Actor, Resources = initial.Resources, HarvestDurations = initial.HarvestDurations,
            WorldName = initial.WorldName, ItemUseDescriptions = initial.ItemUseDescriptions, ItemNames = initial.ItemNames, OtherCharacters = initial.OtherCharacters, Notices = initial.Notices,
            Commands = initial.Commands, IsExpedition = true, PlaceId = initial.PlaceId, PlaceName = initial.PlaceName,
            PlaceDescription = initial.PlaceDescription, Passages = initial.Passages,
            Survival = initial.Survival, Weather = initial.Weather,
            Outcome = terminal ? CycleExpeditionOutcome.Dead : CycleExpeditionOutcome.Exploring,
            EndingReason = terminal ? "测试终局" : null
        };
    }
}
