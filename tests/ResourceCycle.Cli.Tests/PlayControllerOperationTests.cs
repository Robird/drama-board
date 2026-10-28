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
        var controller = new PlayConsole.ConsoleCharacterController(PlayPresentation.ForMode(true), input, output,
            operations: journal, worldVersion: () => "1:0");
        CycleWorld world = CycleGameFactory.CreateSurvival().World;
        string before = world.DescribeState();

        CycleControllerOperation operation = await controller.ChooseCommandAsync(CycleDecisionContext.Create(world, "a"), default);

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
    public async Task Eof_does_not_invent_a_call_but_explicit_exit_has_a_paired_result(string submitted, int count) {
        using var journal = CycleOperationJournal.CreateInMemory();
        using var input = new StringReader(submitted);
        using var output = new StringWriter();
        var controller = new PlayConsole.ConsoleCharacterController(PlayPresentation.ForMode(true), input, output, operations: journal);

        await Assert.ThrowsAsync<PlayConsole.PlayEndedException>(() => controller.ChooseCommandAsync(
            CycleDecisionContext.Create(CycleGameFactory.CreateSurvival().World, "a"), default).AsTask());

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
        var controller = new PlayConsole.ConsoleCharacterController(PlayPresentation.ForMode(true), input, output, operations: journal);
        var initial = CycleGameFactory.CreateSurvival();
        var history = new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor);
        var kernel = CycleEngine.CreateKernel(history, characterController: controller);

        await Assert.ThrowsAsync<IOException>(() => kernel.StepAsync(new ModelTime(20 * CycleTime.Hour)).AsTask());

        Assert.Same(initial.World, history.State);
        Assert.Equal(initial.Cursor, history.Cursor);
        Assert.Null(history.PendingEvent);
        Assert.True(journal.IsFaulted);
    }

    private sealed class FailingFlushStream : MemoryStream {
        public override void Flush() => throw new IOException("Injected receipt flush failure.");
    }
}
