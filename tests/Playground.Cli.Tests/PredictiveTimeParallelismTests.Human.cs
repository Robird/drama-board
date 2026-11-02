using Atelia.Completion.Abstractions;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Playground.Cli.Tests;

// Interface tests with a controlled TextReader and offline Completion. These are not human playtests.
public sealed partial class PredictiveTimeParallelismTests {
    [Theory]
    [InlineData("say b human-new-information", "human-new-information")]
    [InlineData("offer b item.food 1 for money 2", "尚未成交")]
    public async Task Human_console_wait_holds_authoritative_frontier_and_released_input_invalidates_completed_candidate(
        string humanInput, string freshInformation) {
        using var run = new Run(PlaygroundGameFactory.CreateLivingWorld(characterCount: 2, enableNeeds: false, enableWeather: false));
        using var journalStore = PlaygroundHostJournal.CreateInMemory();
        var journal = new PlaygroundOperationAudit(journalStore, "human-parallel-branch", "human-parallel-run");
        using var reader = new HumanInputGate();
        using var output = new StringWriter();
        var human = new PlayConsole.ConsoleCharacterController(PlayPresentation.ForMode(true), ConsoleMetaCommands.HumanDefault,
            reader, output, operations: journal);
        var provider = new HeldClient(Act("handoff never-use-human-stale"));
        await using var candidate = new Candidate(Opening(run.History.State, "b"), provider);
        await provider.Invoked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var fallback = new ImmediateClient(Act("handoff fresh-after-human"));
        var gate = new AdoptionClient(candidate, fallback);
        var router = new HumanAndModelController(human, Controller(gate));
        var kernel = PlaygroundEngine.CreateKernel(run.History, characterController: router);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
        Assert.Equal(PlaygroundEventKind.DecisionContextOpened, Assert.Single(kernel.LastCompletion!.Event.Facts).Kind);
        PlaygroundWorld opened = run.History.State;
        var cursor = run.History.Cursor;
        var commitId = run.History.CompletedStateCommitId;
        Task<StepStatus> waiting = kernel.StepAsync(new ModelTime(long.MaxValue)).AsTask();
        await reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        provider.Release.SetResult();
        await candidate.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(waiting.IsCompleted);
        Assert.Same(opened, run.History.State);
        Assert.Equal(cursor, run.History.Cursor);
        Assert.Equal(commitId, run.History.CompletedStateCommitId);
        Assert.Null(run.History.PendingEvent);
        Assert.Null(run.History.State.Business.InteractionStates["b"].CurrentContext);
        Assert.Empty(journal.Entries); // Completion of b did not invent an input for Human a.
        Assert.Equal(0, router.ModelCalls);
        Assert.Equal(0, gate.Adopted + gate.Discarded);

        reader.Release.SetResult(humanInput);
        Assert.Equal(StepStatus.Committed, await waiting.WaitAsync(TimeSpan.FromSeconds(10)));
        PlaygroundFact humanFact = Assert.Single(kernel.LastCompletion!.Event.Facts);
        Assert.Equal("a", humanFact.ActorId);
        Assert.Equal(humanInput, humanFact.RawInput);
        PlayConsole.CompleteOperations(journal, kernel.LastCompletion!);
        var received = Assert.Single(journal.Entries);
        Assert.Equal(humanInput, received.Received.RawInput);
        Assert.Equal(PlaygroundOperationStatus.Succeeded, received.Completed!.Status);
        Assert.Equal(received.Received.OperationRecordId, humanFact.OperationRecordId);
        Assert.Equal(0, router.ModelCalls);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
        Assert.Equal("b", Assert.Single(kernel.LastCompletion!.Event.Facts).ActorId);
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
        PlaygroundFact modelFact = Assert.Single(kernel.LastCompletion!.Event.Facts);
        Assert.Equal("b", modelFact.ActorId);
        Assert.Equal("handoff fresh-after-human", modelFact.RawInput);
        Assert.Equal(1, router.ModelCalls);
        Assert.Equal(0, gate.Adopted);
        Assert.Equal(1, gate.Discarded);
        CompletionRequest fresh = Assert.Single(fallback.Requests);
        Assert.NotEqual(RequestValue(candidate.Request!), RequestValue(fresh));
        string material = Assert.IsType<ObservationMessage>(Assert.Single(fresh.PromptPrefix.SharedContextMessages)).Content!;
        Assert.Contains(freshInformation, material);
        Assert.DoesNotContain("never-use-human-stale", run.FinalMaterial());
        Assert.Contains("fresh-after-human", run.FinalMaterial());
        Assert.Single(journal.Entries); // Model command does not enter the Human Console input audit.
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Human_console_eof_or_cancellation_stops_without_adopting_or_inventing_input_and_drains_candidate(
        bool cancelHuman, bool completeCandidate) {
        using var run = new Run();
        using var journalStore = PlaygroundHostJournal.CreateInMemory();
        var journal = new PlaygroundOperationAudit(journalStore, "human-stop-branch", "human-stop-run");
        using var reader = new HumanInputGate();
        using var output = new StringWriter();
        using var stop = new CancellationTokenSource();
        var human = new PlayConsole.ConsoleCharacterController(PlayPresentation.ForMode(true), ConsoleMetaCommands.HumanDefault,
            reader, output, operations: journal);
        var provider = new HeldClient(Act("handoff never-after-human-stop"));
        var candidate = new Candidate(Opening(run.History.State, "b"), provider);
        try {
            await provider.Invoked.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var fallback = new ImmediateClient(Act("handoff forbidden-fallback"));
            var gate = new AdoptionClient(candidate, fallback);
            var router = new HumanAndModelController(human, Controller(gate));
            var kernel = PlaygroundEngine.CreateKernel(run.History, characterController: router);
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
            PlaygroundWorld opened = run.History.State;
            var cursor = run.History.Cursor;
            var commitId = run.History.CompletedStateCommitId;
            Task<StepStatus> waiting = kernel.StepAsync(new ModelTime(long.MaxValue), stop.Token).AsTask();
            await reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (completeCandidate) {
                provider.Release.SetResult();
                await candidate.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            Assert.False(waiting.IsCompleted);
            if (cancelHuman) {
                stop.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(10)));
            } else {
                reader.Release.SetResult(null);
                await Assert.ThrowsAsync<PlayConsole.PlayEndedException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(10)));
            }
            Assert.Same(opened, run.History.State);
            Assert.Equal(cursor, run.History.Cursor);
            Assert.Equal(commitId, run.History.CompletedStateCommitId);
            Assert.Null(run.History.PendingEvent);
            Assert.Null(run.History.State.Business.InteractionStates["b"].CurrentContext);
            Assert.Empty(journal.Entries);
            Assert.Equal(0, router.ModelCalls);
            Assert.Equal(0, gate.Adopted + gate.Discarded);
            Assert.Empty(fallback.Requests);
            Assert.Equal(1, reader.Reads);
            Assert.DoesNotContain("never-after-human-stop", run.FinalMaterial());
        } finally {
            // The experiment host owns candidate lifetime; EOF/cancel never converts it into a Human operation.
            await candidate.DisposeAsync();
        }
        Assert.True(candidate.Ready.Task.IsCompleted);
        Assert.Equal(!completeCandidate, candidate.Ready.Task.IsCanceled);
    }

    private sealed class HumanInputGate : TextReader {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string?> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Reads { get; private set; }
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) {
            Reads++;
            Entered.TrySetResult();
            return await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class HumanAndModelController(IPlaygroundCharacterController human, IPlaygroundCharacterController model)
        : IPlaygroundCharacterController {
        public int ModelCalls { get; private set; }
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) {
            if (context.InteractionState.ActorId == "a") return human.ChooseCommandAsync(context, cancellationToken);
            ModelCalls++;
            return model.ChooseCommandAsync(context, cancellationToken);
        }
    }
}
