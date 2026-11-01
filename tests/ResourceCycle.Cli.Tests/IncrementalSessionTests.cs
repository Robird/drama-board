using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.ResourceCycle;

namespace DramaBoard.ResourceCycle.Cli.Tests;

public sealed class IncrementalSessionTests {
    [Fact]
    public void Terminal_commit_replaces_prior_display_with_its_own_paired_scope_and_never_requests_input() {
        using var output = new StringWriter();
        var session = new PlayPresentationSession(PlayPresentation.ForMode(true), ConsoleMetaCommands.HumanDefault, output);
        CycleObservation initial = Observe(CycleGameFactory.CreateSurvival().World);
        var opened = ViewContextFixture.State(initial);
        session.EnterInput(opened);
        var terminal = new CycleObservation {
            Time = initial.Time, IsDay = initial.IsDay, NextLightChange = initial.NextLightChange,
            Actor = initial.Actor, Resources = initial.Resources, HarvestDurations = initial.HarvestDurations,
            WorldName = initial.WorldName, ItemUseDescriptions = initial.ItemUseDescriptions, ItemNames = initial.ItemNames, OtherCharacters = initial.OtherCharacters, Notices = initial.Notices,
            Commands = [], IsExpedition = true,
            Outcome = CycleExpeditionOutcome.Dead, EndingReason = "已耗尽"
        };
        var ending = new CycleObservedEvent(4, "a", CycleObservedEventKind.ExpeditionEnded, initial.Time, initial.Time);
        var state = new CharacterInteractionState("a", new CharacterContext(2, CharacterContextStatus.Terminal,
            new CharacterContextOpening(CycleBackground.For(terminal), terminal, null, terminal.Time,
                CycleOutcomeActivityState.Terminal, [new CharacterExperience(1, ending)])), nextExperienceSequence: 2);
        output.GetStringBuilder().Clear();
        session.Commit(state);
        Assert.Contains(CharacterContextRenderer.EndMarker(opened), output.ToString());
        Assert.Contains(CharacterContextRenderer.BeginMarker(state), output.ToString());
        Assert.Contains(CharacterContextRenderer.EndMarker(state), output.ToString());
        Assert.DoesNotContain("现在可输入", output.ToString());
        Assert.True(session.TerminalDelivered);
        Assert.Same(terminal, session.Baseline!.Observation);
        output.GetStringBuilder().Clear();
        session.Commit(state);
        Assert.Empty(output.ToString());
        Assert.Throws<InvalidOperationException>(() => session.EnterInput(state));
    }

    [Fact]
    public async Task Unchanged_handoff_has_visible_success_answer_without_inventing_a_game_event() {
        using var input = new StringReader("handoff --clear\n");
        using var output = new StringWriter();
        using var operations = CycleOperationJournal.CreateInMemory();
        var controller = new PlayConsole.ConsoleCharacterController(PlayPresentation.ForMode(true), ConsoleMetaCommands.HumanDefault, input, output, operations: operations);
        var initial = CycleGameFactory.CreateSurvival();
        var kernel = CycleEngine.CreateKernel(new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor),
            characterController: controller);
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
        CycleFact fact = Assert.Single(kernel.LastCompletion!.Event.Facts);
        Assert.Empty(fact.ObservedEvents);
        PlayConsole.CompleteOperations(operations, kernel.LastCompletion);
        controller.RenderCommitted(kernel.World);
        Assert.Contains(CycleOperationResults.Describe(fact), output.ToString());
        Assert.Equal(CycleOperationStatus.Succeeded, operations.Entries.Single().Completed!.Status);
    }

    [Fact]
    public async Task Accepted_advancing_action_is_held_for_next_opening_without_old_screen_settlement() {
        using var input = new StringReader("wait 1\n");
        using var output = new StringWriter();
        var presentation = PlayPresentation.ForMode(true);
        Assert.True(presentation.TrySelectMode("full"));
        var controller = new PlayConsole.ConsoleCharacterController(presentation, ConsoleMetaCommands.HumanDefault, input, output);
        var initial = CycleGameFactory.CreateSurvival();
        var kernel = CycleEngine.CreateKernel(new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor),
            characterController: controller);
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
        output.GetStringBuilder().Clear();
        controller.RenderCommitted(kernel.World);
        Assert.Contains("LLM 上下文结束", output.ToString());
        Assert.DoesNotContain("开始等待", output.ToString());
        output.GetStringBuilder().Clear();
        controller.RenderCommitted(kernel.World);
        Assert.Empty(output.ToString());
        Assert.NotNull(kernel.World.Business.InteractionStates["a"].PendingAdvance);
        Assert.Equal(CharacterContextStatus.Closed, kernel.World.Business.InteractionStates["a"].CurrentContext!.Status);
    }

    [Fact]
    public void Both_full_layouts_deliver_long_wait_risk_and_historical_stock_before_installing_baseline() {
        CycleObservation original = Observe(CycleGameFactory.CreateSurvival().World);
        var observation = new CycleObservation {
            Time = original.Time, IsDay = original.IsDay, NextLightChange = original.NextLightChange,
            Actor = original.Actor, Resources = original.Resources, HarvestDurations = original.HarvestDurations,
            WorldName = original.WorldName, ItemUseDescriptions = original.ItemUseDescriptions, ItemNames = original.ItemNames, OtherCharacters = original.OtherCharacters, Notices = original.Notices,
            Commands = original.Commands.Select(entry => entry.Command == CycleCommand.Wait(24)
                ? entry with { Warning = "长等待专属风险" } : entry).ToArray(),
            IsExpedition = true, PlaceId = original.PlaceId, PlaceName = original.PlaceName, PlaceDescription = original.PlaceDescription,
            Passages = original.Passages, Survival = original.Survival, Weather = original.Weather,
            Memories = [new("旧果园", original.Time, [new CycleResource("old-berries", "旧果树", "item.food", true,
                CycleTime.Hour, 9, 7)], "old-orchard")]
        };
        foreach (IPlayLayout layout in new IPlayLayout[] { new ClassicPlayLayout(), new DecisionPlayLayout() }) {
            string full = layout.RenderFull(CycleViewSnapshot.Create(observation, ViewContextFixture.State(observation)));
            Assert.Contains("长等待专属风险", full);
            Assert.Contains("旧果园", full);
            Assert.Contains("旧果树", full);
            Assert.Contains("7/9", full);
        }
    }

    [Fact]
    public void Failed_output_does_not_consume_items_and_retry_redraws_saved_material_without_new_scope() {
        using var writer = new FaultingWriter();
        var boundaries = new List<string>();
        var session = new PlayPresentationSession(PlayPresentation.ForMode(true), ConsoleMetaCommands.HumanDefault, writer, boundaries.Add);
        CycleObservation observation = Observe(CycleGameFactory.CreateSurvival().World);
        var initial = ViewContextFixture.State(observation);
        session.EnterInput(initial);
        var item = new CharacterContextItem(1, 1, "handoff --clear", "call", "record", "saved-result", observation: observation);
        var changed = new CharacterInteractionState("a", new CharacterContext(1, CharacterContextStatus.Open,
            initial.CurrentContext!.Opening, [item], 2, 1));
        writer.Fail = true;
        Assert.Throws<IOException>(() => session.Commit(changed));
        Assert.Null(session.Baseline);
        Assert.Equal(0, session.ConsumedItemSequence);
        writer.Fail = false;
        writer.GetStringBuilder().Clear();
        session.EnterInput(changed);
        Assert.Contains("saved-result", writer.ToString());
        Assert.Equal(1, session.ConsumedItemSequence);
        Assert.NotNull(session.Baseline);
        Assert.Single(boundaries);
        writer.GetStringBuilder().Clear();
        session.Commit(changed);
        session.EnterInput(changed);
        Assert.Empty(writer.ToString());
    }

    [Fact]
    public void Committed_item_is_consumed_once_and_meta_previews_do_not_change_cursor_or_model() {
        using var writer = new StringWriter();
        var session = new PlayPresentationSession(PlayPresentation.ForMode(true), ConsoleMetaCommands.HumanDefault, writer);
        CycleObservation observation = Observe(CycleGameFactory.CreateSurvival().World);
        var initial = ViewContextFixture.State(observation);
        session.EnterInput(initial);
        var item = new CharacterContextItem(1, 1, "bad-attempt", "call", "record", "unique-rejection", isRejected: true);
        var changed = new CharacterInteractionState("a", new CharacterContext(1, CharacterContextStatus.Open,
            initial.CurrentContext!.Opening, [item], 2, 1));
        session.Commit(changed);
        session.Commit(changed);
        session.EnterInput(changed);
        Assert.Equal(1, Count(writer.ToString(), "unique-rejection"));
        Assert.DoesNotContain("正在处理结算", writer.ToString());
        Assert.Equal(1, session.ConsumedItemSequence);
        var baseline = session.Baseline;
        writer.GetStringBuilder().Clear();
        session.ShowCurrent(changed, prefix: "preview");
        session.Reply("help");
        Assert.Same(baseline, session.Baseline);
        Assert.Equal(1, session.ConsumedItemSequence);
        Assert.Same(item, Assert.Single(changed.CurrentContext!.Items));
        Assert.Contains(PlayPresentationSession.OutOfBandStart, writer.ToString());
        Assert.DoesNotContain("LLM 上下文开始", writer.ToString());
        Assert.DoesNotContain("LLM 上下文结束", writer.ToString());
    }

    [Theory]
    [InlineData("decision")]
    [InlineData("classic")]
    public async Task Zero_time_commits_show_actual_effects_without_full_redraw_and_look_resynchronizes(string layout) {
        var presentation = PlayPresentation.ForMode(true);
        Assert.True(presentation.TrySelect(layout));
        using var input = new StringReader("use item.water\nhandoff 明天再走\n:look\n:exit\n");
        using var output = new StringWriter();
        using var operations = CycleOperationJournal.CreateInMemory();
        var controller = new PlayConsole.ConsoleCharacterController(presentation, ConsoleMetaCommands.HumanDefault, input, output, operations: operations);
        var initial = CycleGameFactory.CreateSurvival();
        var history = new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor);
        var kernel = CycleEngine.CreateKernel(history, characterController: controller);
        for (int i = 0; i < 3; i++) {
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
            PlayConsole.CompleteOperations(operations, kernel.LastCompletion!);
            controller.RenderCommitted(kernel.World);
        }
        string beforeLook = output.ToString();
        Assert.Equal(1, Count(beforeLook, "# 山野镇"));
        Assert.Contains("+45", beforeLook); // Initial water 45 becomes 90.
        Assert.Contains("-1", beforeLook);
        Assert.Contains("明天再走", beforeLook);
        Assert.All(operations.Entries, entry => Assert.Equal(CycleOperationStatus.Succeeded, entry.Completed!.Status));
        Assert.Contains("45", operations.Entries[0].Completed!.Result);

        await Assert.ThrowsAsync<PlayConsole.PlayEndedException>(() => kernel.StepAsync(new ModelTime(long.MaxValue)).AsTask());
        Assert.Equal(2, Count(output.ToString(), "# 山野镇"));
        Assert.Equal(initial.World.Time, kernel.World.Time);
        Assert.Equal(1L, kernel.World.Business.InteractionStates["a"].CurrentContext!.ContextSequence);
    }

    [Fact]
    public async Task Mode_help_lists_legal_parameters_and_play_rejects_delta_without_dispatch() {
        var presentation = PlayPresentation.ForMode(false);
        using var input = new StringReader(":mode\n:mode delta\n:help\n:mode full\nwait 1\n");
        using var output = new StringWriter();
        using var operations = CycleOperationJournal.CreateInMemory();
        var controller = new PlayConsole.ConsoleCharacterController(presentation, ConsoleMetaCommands.HumanDefault, input, output, operations: operations);
        CycleControllerOperation action = await controller.ChooseCommandAsync(
            ViewContextFixture.Create(CycleGameFactory.CreatePlayable().World, "a"), default);

        Assert.Equal("wait 1", action.RawInput);
        Assert.Equal(PlayOutputMode.Full, presentation.Mode);
        Assert.Equal(CycleOperationStatus.Rejected, operations.Entries[1].Completed!.Status);
        Assert.Contains(":mode full", output.ToString());
        Assert.DoesNotContain(":mode delta", output.ToString());
        Assert.Contains(":layout classic", output.ToString());
        Assert.Contains(":layout decision", output.ToString());
    }

    private static CycleObservation Observe(CycleWorld world) => CycleDecisionContext.Observe(world, "a");
    private static int Count(string text, string fragment) => text.Split(fragment, StringSplitOptions.None).Length - 1;
    private sealed class FaultingWriter : StringWriter {
        public bool Fail { get; set; }
        public override void Flush() { if (Fail) { throw new IOException("Injected write failure."); } }
    }
}
