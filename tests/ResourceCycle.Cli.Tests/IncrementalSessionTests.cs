using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.ResourceCycle;

namespace DramaBoard.ResourceCycle.Cli.Tests;

public sealed class IncrementalSessionTests {
    [Fact]
    public void Terminal_commit_delivers_events_with_a_full_frozen_tail_and_marks_delivery_complete() {
        var layout = new RecordingLayout();
        using var output = new StringWriter();
        var session = new PlayPresentationSession(new PlayPresentation(layout), ConsoleMetaCommands.HumanDefault, output);
        CycleObservation initial = Observe(CycleGameFactory.CreateSurvival().World);
        session.EnterInput(initial, ViewContextFixture.State(initial));
        var terminal = new CycleObservation {
            Time = initial.Time, IsDay = initial.IsDay, NextLightChange = initial.NextLightChange,
            Actor = initial.Actor, Resources = initial.Resources, HarvestDurations = initial.HarvestDurations,
            ItemNames = initial.ItemNames, OtherCharacters = initial.OtherCharacters, Notices = initial.Notices,
            Commands = [], IsExpedition = true,
            Outcome = CycleExpeditionOutcome.Dead, EndingReason = "已耗尽"
        };
        var ending = new CycleObservedEvent(4, "a", CycleObservedEventKind.ExpeditionEnded, initial.Time, initial.Time);
        session.BeginAdvancing();
        session.Commit(terminal, [ending], interactionState: ViewContextFixture.State(terminal));

        CycleViewUpdate update = Assert.Single(layout.Updates);
        Assert.True(update.FullTail);
        Assert.Same(ending, Assert.Single(update.Events));
        Assert.True(session.TerminalDelivered);
        Assert.Same(terminal, session.Baseline!.Observation);
        Assert.Equal(PlayDeliveryPhase.Advancing, update.Phase);
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
        controller.RenderCommitted(kernel.World, kernel.LastCompletion, survival: true);
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
        controller.RenderCommitted(kernel.World, kernel.LastCompletion!, survival: true);
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
            ItemNames = original.ItemNames, OtherCharacters = original.OtherCharacters, Notices = original.Notices,
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
    public void Failed_write_invalidates_baseline_and_next_delivery_is_full() {
        var layout = new RecordingLayout();
        using var writer = new FaultingWriter();
        int boundaries = 0;
        var session = new PlayPresentationSession(new PlayPresentation(layout), ConsoleMetaCommands.HumanDefault, writer, () => boundaries++);
        CycleObservation observation = Observe(CycleGameFactory.CreateSurvival().World);
        session.EnterInput(observation, ViewContextFixture.State(observation));
        Assert.NotNull(session.Baseline);
        Assert.Single(layout.Full);
        writer.Fail = true;
        session.BeginAdvancing();

        Assert.Throws<IOException>(() => session.Commit(observation, [], interactionState: ViewContextFixture.State(observation)));
        Assert.Null(session.Baseline);
        writer.Fail = false;
        session.EnterInput(observation, ViewContextFixture.State(observation));

        Assert.Equal(2, layout.Full.Count);
        Assert.NotNull(session.Baseline);
        Assert.Equal(1, boundaries); // Recovery resynchronizes without inventing a new decision context.
    }

    [Fact]
    public void Commit_never_claims_input_and_input_phase_is_delivered_once_on_actual_entry() {
        var layout = new RecordingLayout();
        using var writer = new StringWriter();
        var session = new PlayPresentationSession(new PlayPresentation(layout), ConsoleMetaCommands.HumanDefault, writer);
        CycleObservation observation = Observe(CycleGameFactory.CreateSurvival().World);
        session.EnterInput(observation, ViewContextFixture.State(observation));
        session.BeginAdvancing();
        session.Commit(observation, [], interactionState: ViewContextFixture.State(observation));
        session.Commit(observation, [], interactionState: ViewContextFixture.State(observation));
        Assert.All(layout.Updates, update => Assert.Equal(PlayDeliveryPhase.Advancing, update.Phase));
        Assert.Equal(1, Count(writer.ToString(), "现在可输入"));

        session.EnterInput(observation, ViewContextFixture.State(observation));
        session.EnterInput(observation, ViewContextFixture.State(observation));

        Assert.Equal(2, Count(writer.ToString(), "现在可输入"));
        Assert.Equal(PlayDeliveryPhase.ControllerInput, layout.Updates[^1].Phase);
        Assert.Single(layout.Full);
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
            controller.RenderCommitted(kernel.World, kernel.LastCompletion!, survival: true);
        }
        string beforeLook = output.ToString();
        Assert.Equal(1, Count(beforeLook, "# 山林求生"));
        Assert.Contains("+45", beforeLook); // Initial water 45 becomes 90.
        Assert.Contains("-1", beforeLook);
        Assert.Contains("明天再走", beforeLook);
        Assert.All(operations.Entries, entry => Assert.Equal(CycleOperationStatus.Succeeded, entry.Completed!.Status));
        Assert.Contains("45", operations.Entries[0].Completed!.Result);

        await Assert.ThrowsAsync<PlayConsole.PlayEndedException>(() => kernel.StepAsync(new ModelTime(long.MaxValue)).AsTask());
        Assert.Equal(2, Count(output.ToString(), "# 山林求生"));
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

        Assert.Equal(CycleCommand.Wait(1), action.Command);
        Assert.Equal(PlayOutputMode.Full, presentation.Mode);
        Assert.Equal(CycleOperationStatus.Rejected, operations.Entries[1].Completed!.Status);
        Assert.Contains(":mode full", output.ToString());
        Assert.DoesNotContain(":mode delta", output.ToString());
        Assert.Contains(":layout classic", output.ToString());
        Assert.Contains(":layout decision", output.ToString());
    }

    [Fact]
    public void Query_details_does_not_install_baseline_or_mutate_phase() {
        using var writer = new StringWriter();
        var session = new PlayPresentationSession(PlayPresentation.ForMode(true), ConsoleMetaCommands.HumanDefault, writer);
        CycleObservation observation = Observe(CycleGameFactory.CreateSurvival().World);
        session.EnterInput(observation, ViewContextFixture.State(observation));
        CycleViewSnapshot baseline = session.Baseline!;
        _ = PlayView.Render(observation, ViewContextFixture.State(observation));
        Assert.Same(baseline, session.Baseline);
        Assert.Equal(PlayDeliveryPhase.ControllerInput, session.Phase);
    }

    private static CycleObservation Observe(CycleWorld world) => CycleDecisionContext.Observe(world, "a");
    private static int Count(string text, string fragment) => text.Split(fragment, StringSplitOptions.None).Length - 1;
    private sealed class FaultingWriter : StringWriter {
        public bool Fail { get; set; }
        public override void Flush() { if (Fail) { throw new IOException("Injected write failure."); } }
    }
    private sealed class RecordingLayout : IPlayLayout {
        public string Id => "recording";
        public string Name => Id;
        public List<CycleObservation> Full { get; } = [];
        public List<CycleViewUpdate> Updates { get; } = [];
        public string Render(CycleObservation observation, CharacterInteractionState? interactionState = null) { Full.Add(observation); return "full"; }
        public string RenderUpdate(CycleViewUpdate update) { Updates.Add(update); return "update"; }
        public string RenderActionHelp(CycleObservation observation) => "help";
    }
}
