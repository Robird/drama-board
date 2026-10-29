using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.ResourceCycle;

namespace DramaBoard.ResourceCycle.Cli.Tests;

public sealed class PlayControllerOperationTests {
    [Fact]
    public async Task Every_submitted_operation_is_recorded_before_dispatch_and_game_result_waits_for_commit() {
        using var journal = CycleOperationJournal.CreateInMemory();
        using var input = new StringReader("\n \t\n:help\ninvalid\n:layout unknown\nwait 0\nhandoff 观察保暖\n");
        using var output = new StringWriter();
        var controller = new PlayConsole.ConsoleCharacterController(PlayPresentation.ForMode(true), ConsoleMetaCommands.HumanDefault, input, output,
            operations: journal, worldVersion: () => "1:0");
        CycleWorld world = CycleGameFactory.CreateSurvival().World;
        string before = world.DescribeState();

        CycleControllerOperation operation = await controller.ChooseCommandAsync(ViewContextFixture.Create(world, "a"), default);

        Assert.Equal(CycleCommand.Handoff("观察保暖"), operation.Command);
        Assert.Equal(5, journal.Entries.Count);
        Assert.Equal(5, journal.Entries.Select(entry => entry.Received.ToolCallId).Distinct().Count());
        Assert.All(journal.Entries, entry => {
            Assert.Equal("a", entry.Received.ActorId);
            Assert.Equal(1L, entry.Received.ContextSequence);
            Assert.False(string.IsNullOrWhiteSpace(entry.Received.ToolCallId));
        });
        Assert.Equal(CycleOperationStatus.Succeeded, journal.Entries[0].Completed!.Status);
        Assert.All(journal.Entries.Skip(1).Take(3), entry => Assert.Equal(CycleOperationStatus.Rejected, entry.Completed!.Status));
        Assert.All(journal.Entries.Take(4), entry => {
            Assert.Equal(entry.Received.ToolCallId, entry.Completed!.ToolCallId);
            Assert.Equal("1:0", entry.Completed.WorldVersion);
            Assert.Contains(entry.Completed.Result, output.ToString());
        });
        Assert.Equal(operation.ToolCallId, journal.Entries[^1].Received.ToolCallId);
        Assert.Equal(operation.OperationRecordId, journal.Entries[^1].Received.OperationRecordId);
        Assert.Null(journal.Entries[^1].Completed);
        Assert.Equal(before, world.DescribeState());
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("\n \n", 0)]
    [InlineData(":exit\n", 1)]
    [InlineData(":quit\n", 1)]
    public async Task Eof_does_not_invent_a_call_but_explicit_exit_has_a_paired_result(string submitted, int count) {
        using var journal = CycleOperationJournal.CreateInMemory();
        using var input = new StringReader(submitted);
        using var output = new StringWriter();
        var controller = new PlayConsole.ConsoleCharacterController(PlayPresentation.ForMode(true), ConsoleMetaCommands.HumanDefault, input, output, operations: journal);

        await Assert.ThrowsAsync<PlayConsole.PlayEndedException>(() => controller.ChooseCommandAsync(
            ViewContextFixture.Create(CycleGameFactory.CreateSurvival().World, "a"), default).AsTask());

        Assert.Equal(count, journal.Entries.Count);
        Assert.All(journal.Entries, entry => {
            Assert.Equal(entry.Received.ToolCallId, entry.Completed!.ToolCallId);
            Assert.Equal(CycleOperationStatus.Succeeded, entry.Completed.Status);
        });
    }

    [Fact]
    public async Task Received_write_failure_prevents_game_dispatch_and_state_publication() {
        using var journal = CycleOperationJournal.CreateForTesting(new FailingFlushStream());
        using var input = new StringReader("wait 1\n");
        using var output = new StringWriter();
        var controller = new PlayConsole.ConsoleCharacterController(PlayPresentation.ForMode(true), ConsoleMetaCommands.HumanDefault, input, output, operations: journal);
        var initial = CycleGameFactory.CreateSurvival();
        var history = new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor);
        var kernel = CycleEngine.CreateKernel(history, characterController: controller);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
        CycleWorld openedWorld = kernel.World;
        KernelCursor openedCursor = kernel.Cursor;
        await Assert.ThrowsAsync<IOException>(() => kernel.StepAsync(new ModelTime(20 * CycleTime.Hour)).AsTask());

        Assert.Same(openedWorld, history.State);
        Assert.Equal(openedCursor, history.Cursor);
        Assert.Null(history.PendingEvent);
        Assert.True(journal.IsFaulted);
    }

    [Fact]
    public async Task Disabled_meta_commands_reject_without_exit_resync_or_preference_changes_then_return_action() {
        string[] disabled = [":help", ":look", ":details", ":layout", ":mode", ":exit", ":quit",
            ":layout classic", ":mode full", ":layout decision", ":mode delta", ":layout classic once", ":layout decision once"];
        using var journal = CycleOperationJournal.CreateInMemory();
        using var output = new StringWriter();
        var presentation = PlayPresentation.ForMode(true);
        IPlayLayout layout = presentation.Current;
        int boundaries = 0;
        CycleViewSnapshot? baseline = null;
        PlayConsole.ConsoleCharacterController? controller = null;
        using var input = new CallbackReader(disabled.Append("handoff :exit"), () => {
            Assert.Same(layout, presentation.Current);
            Assert.Equal(PlayOutputMode.Delta, presentation.Mode);
            if (baseline is null) { baseline = controller!.Baseline; }
            Assert.NotNull(baseline);
            Assert.Same(baseline, controller!.Baseline);
        });
        controller = new(presentation, ConsoleMetaCommands.None, input, output, () => boundaries++, journal);
        CycleWorld world = CycleGameFactory.CreateSurvival().World;
        string before = world.DescribeState();

        CycleControllerOperation action = await controller.ChooseCommandAsync(ViewContextFixture.Create(world, "a"), default);

        Assert.Equal(CycleCommand.Handoff(":exit"), action.Command);
        Assert.Equal(disabled.Length + 1, journal.Entries.Count);
        Assert.All(journal.Entries.Take(disabled.Length), entry => {
            Assert.Equal(CycleOperationStatus.Rejected, entry.Completed!.Status);
            Assert.Equal(entry.Received.ToolCallId, entry.Completed.ToolCallId);
            Assert.Contains(entry.Completed.Result, output.ToString());
        });
        Assert.Null(journal.Entries[^1].Completed);
        Assert.Equal(action.ToolCallId, journal.Entries[^1].Received.ToolCallId);
        Assert.Equal(1, boundaries);
        Assert.Equal(before, world.DescribeState());
        // This fixture contains no literal command text in its game data; input is not echoed.
        Assert.DoesNotContain(":help", output.ToString());
        Assert.DoesNotContain(":exit", output.ToString());
        Assert.DoesNotContain(":layout", output.ToString());
        Assert.DoesNotContain(":mode", output.ToString());
    }

    [Fact]
    public async Task Console_queries_rejections_and_exit_do_not_publish_game_state_or_events() {
        using var journal = CycleOperationJournal.CreateInMemory();
        using var input = new StringReader(":help\n:layout classic once\n:layout\n:mode\n:look\n:layout classic\n:mode full\n:mode invalid\n:exit\n");
        using var output = new StringWriter();
        var controller = new PlayConsole.ConsoleCharacterController(PlayPresentation.ForMode(true),
            ConsoleMetaCommands.HumanDefault, input, output, operations: journal);
        var initial = CycleGameFactory.CreateSurvival();
        var history = new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor);
        var kernel = CycleEngine.CreateKernel(history, characterController: controller);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
        CycleWorld openedWorld = kernel.World;
        KernelCursor openedCursor = kernel.Cursor;
        await Assert.ThrowsAsync<PlayConsole.PlayEndedException>(() => kernel.StepAsync(new ModelTime(long.MaxValue)).AsTask());

        Assert.Same(openedWorld, history.State);
        Assert.Equal(openedCursor, history.Cursor);
        Assert.Null(history.PendingEvent);
        Assert.Equal(9, journal.Entries.Count);
        Assert.All(journal.Entries, entry => Assert.Equal(entry.Received.ToolCallId, entry.Completed!.ToolCallId));
    }

    [Fact]
    public async Task Cancellation_during_read_does_not_record_or_dispatch_the_returned_line() {
        using var cancellation = new CancellationTokenSource();
        using var journal = CycleOperationJournal.CreateInMemory();
        using var input = new CallbackReader([":exit"], cancellation.Cancel);
        using var output = new StringWriter();
        var controller = new PlayConsole.ConsoleCharacterController(PlayPresentation.ForMode(true),
            ConsoleMetaCommands.HumanDefault, input, output, operations: journal);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.ChooseCommandAsync(
            ViewContextFixture.Create(CycleGameFactory.CreateSurvival().World, "a"), cancellation.Token).AsTask());

        Assert.Empty(journal.Entries);
    }

    [Fact]
    public async Task Resync_journal_result_contains_the_complete_console_composition_written_to_output() {
        using var journal = CycleOperationJournal.CreateInMemory();
        using var output = new StringWriter();
        int beforeResync = 0;
        int afterResync = 0;
        int reads = 0;
        using var input = new CallbackReader([":look", "wait 1"], () => {
            if (reads++ == 0) { beforeResync = output.GetStringBuilder().Length; }
            else { afterResync = output.GetStringBuilder().Length; }
        });
        var controller = new PlayConsole.ConsoleCharacterController(PlayPresentation.ForMode(true),
            ConsoleMetaCommands.HumanDefault, input, output, operations: journal);
        CycleObservation observation = ViewContextFixture.Create(CycleGameFactory.CreateSurvival().World, "a").Observation;

        await controller.ChooseCommandAsync(ViewContextFixture.Create(CycleGameFactory.CreateSurvival().World, "a"), default);

        string response = journal.Entries[0].Completed!.Result;
        Assert.Contains(":help", response);
        Assert.Contains("现在可输入", response);
        Assert.Equal(response + output.NewLine + observation.Actor.Id + "> ",
            output.ToString()[beforeResync..afterResync]);
    }

    [Theory]
    [InlineData("classic")]
    [InlineData("decision")]
    public async Task Layout_once_is_a_logged_query_and_preserves_preferences_and_baseline(string target) {
        using var journal = CycleOperationJournal.CreateInMemory();
        using var output = new StringWriter();
        var presentation = PlayPresentation.ForMode(true);
        IPlayLayout original = presentation.Current;
        CycleViewSnapshot? baseline = null;
        PlayConsole.ConsoleCharacterController? controller = null;
        string[] commands = [$":layout {target} once", ":layout missing once", ":layout once", ":details", "handoff 保持原布局"];
        using var input = new CallbackReader(commands, () => {
            Assert.Same(original, presentation.Current);
            Assert.Equal(PlayOutputMode.Delta, presentation.Mode);
            baseline ??= controller!.Baseline;
            Assert.NotNull(baseline);
            Assert.Same(baseline, controller!.Baseline);
        });
        controller = new(presentation, ConsoleMetaCommands.HumanDefault, input, output, operations: journal);
        var context = ViewContextFixture.Create(CycleGameFactory.CreateSurvival().World, "a");

        var action = await controller.ChooseCommandAsync(context, default);

        Assert.Equal(CycleCommand.Handoff("保持原布局"), action.Command);
        var preview = journal.Entries[0];
        Assert.Equal(CycleOperationStatus.Succeeded, preview.Completed!.Status);
        Assert.Equal(preview.Received.ToolCallId, preview.Completed.ToolCallId);
        Assert.Contains(presentation.FindLayout(target)!.RenderFull(CycleViewSnapshot.Create(context.Observation, context.InteractionState)), preview.Completed.Result);
        Assert.Contains(preview.Completed.Result, output.ToString());
        Assert.All(journal.Entries.Skip(1).Take(3), entry => Assert.Equal(CycleOperationStatus.Rejected, entry.Completed!.Status));
        Assert.Equal(1, output.ToString().Split(PlayPresentationSession.TurnBoundary).Length - 1);
    }

    private sealed class CallbackReader(IEnumerable<string> lines, Action beforeRead) : TextReader {
        private readonly Queue<string> _lines = new(lines);
        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) {
            beforeRead();
            return ValueTask.FromResult(_lines.TryDequeue(out string? line) ? line : null);
        }
    }

    private sealed class FailingFlushStream : MemoryStream {
        public override void Flush() => throw new IOException("Injected receipt flush failure.");
    }
}
