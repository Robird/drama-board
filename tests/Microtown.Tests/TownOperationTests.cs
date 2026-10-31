using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;

namespace DramaBoard.Microtown.Tests;

public sealed class TownOperationTests {
    [Fact]
    public async Task RejectedRawInputIsSavedAndDeliveredToSameDecisionBeforeRetry() {
        var start = TownGameFactory.Create(1);
        var history = new InMemoryOccurrenceHistory<TownWorld, TownFact>(start.World, start.Cursor);
        int calls = 0;
        var kernel = TownEngine.CreateControlledKernel(history, (observation, _) => {
            Assert.Null(history.PendingEvent);
            Assert.Equal("a", observation.ActorId);
            Assert.Equal(1, observation.DecisionSequence);
            calls++;
            if (calls == 1) { return ValueTask.FromResult(new TownControllerOperation("  nonsense\n", "tool-1", "record-1")); }
            TownOperationReceipt previous = Assert.IsType<TownOperationReceipt>(observation.LastOperationReceipt);
            Assert.False(previous.Accepted);
            Assert.Equal("  nonsense\n", previous.Operation.RawInput);
            Assert.Equal("tool-1", previous.Operation.ToolCallId);
            Assert.Equal("record-1", previous.Operation.OperationRecordId);
            Assert.Equal(1, previous.OperationSequence);
            return ValueTask.FromResult(new TownControllerOperation("buy grain 2", "tool-2", "record-2"));
        });

        await kernel.StepAsync(start.World.Business.EndTime); // Publish the decision before calling anyone.
        Assert.Equal(0, calls);
        await kernel.StepAsync(start.World.Business.EndTime);
        TownFact rejection = Assert.Single(history.CompletedEvents.Last().Facts);
        Assert.Same(rejection.OperationReceipt, history.State.Business.Character("a").LastOperationReceipt);
        Assert.Equal("a", history.State.Business.OpenDecisionActorId);
        await kernel.StepAsync(start.World.Business.EndTime);

        Assert.Equal(2, calls);
        TownOperationReceipt accepted = history.State.Business.Character("a").LastOperationReceipt!;
        Assert.True(accepted.Accepted);
        Assert.Equal(1, accepted.DecisionSequence);
        Assert.Equal(2, accepted.OperationSequence);
        Assert.Equal("record-2", accepted.Operation.OperationRecordId);
        Assert.Equal(2, history.State.Business.Character("a").Inventory.Count("grain"));
        Assert.Null(history.State.Business.Character("b").LastOperationReceipt);
        Assert.Null(start.World.Business.Character("a").LastOperationReceipt);
    }

    [Theory]
    [InlineData("buy grain 2", true)]
    [InlineData("", false)]
    [InlineData("buy grain 1000", false)]
    [InlineData("create-goal {\"title\":\"Have food\",\"criteria\":\"Have two meals\"}", true)]
    public async Task PendingOperationRestoresSavedReceiptWithoutAskingOrApplyingTwice(string raw, bool accepted) {
        var start = TownGameFactory.Create(1);
        var inner = new InMemoryOccurrenceHistory<TownWorld, TownFact>(start.World, start.Cursor);
        var history = new InterruptibleHistory(inner);
        int calls = 0;
        var kernel = TownEngine.CreateControlledKernel(history, (_, _) => {
            calls++;
            return ValueTask.FromResult(new TownControllerOperation(raw, "pending-tool", "pending-record"));
        });
        await kernel.StepAsync(start.World.Business.EndTime);
        history.FailNextState = true;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await kernel.StepAsync(start.World.Business.EndTime));
        Assert.True(kernel.IsFaulted);
        Assert.Equal(1, calls);
        Assert.NotNull(history.PendingEvent);
        TownOperationReceipt expected = Assert.Single(history.PendingEvent.Facts).OperationReceipt!;
        Assert.Equal(raw, expected.Operation.RawInput);
        Assert.Equal(accepted, expected.Accepted);
        Assert.Null(history.State.Business.Character("a").LastOperationReceipt);

        var resumed = TownEngine.CreateControlledKernel(history, (_, _) => throw new InvalidOperationException("Recovery must not ask the controller."));
        Assert.True(resumed.RecoverPending());
        Assert.False(resumed.RecoverPending());
        Assert.Same(expected, history.State.Business.Character("a").LastOperationReceipt);
        Assert.Equal(1, history.State.Business.Character("a").OperationSequence);
        Assert.Equal(1, calls);
        history.State.Business.Economy.Verify(history.State.Business);
        Assert.Equal(raw == "buy grain 2" ? 2 : 0, history.State.Business.Character("a").Inventory.Count("grain"));
    }

    [Theory]
    [InlineData("", "record")]
    [InlineData("tool", " ")]
    public async Task MissingControllerIdentityFailsBeforePublishingAnOperation(string toolId, string recordId) {
        var start = TownGameFactory.Create(1);
        var history = new InMemoryOccurrenceHistory<TownWorld, TownFact>(start.World, start.Cursor);
        var kernel = TownEngine.CreateControlledKernel(history, (_, _) => ValueTask.FromResult(
            new TownControllerOperation("buy grain 2", toolId, recordId)));
        await kernel.StepAsync(start.World.Business.EndTime);
        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await kernel.StepAsync(start.World.Business.EndTime));
        Assert.Null(history.PendingEvent);
        Assert.Single(history.CompletedEvents);
        Assert.Equal(0, history.State.Business.Character("a").OperationSequence);
        Assert.Equal(18, history.State.Business.Character("a").Money);
    }

    [Fact]
    public async Task OwnActivityOutcomeSurvivesUntilNextObservationWithoutOverwritingCommandReceipt() {
        var start = TownGameFactory.Create(2);
        var history = new InMemoryOccurrenceHistory<TownWorld, TownFact>(start.World, start.Cursor);
        var commands = new Queue<string>(["buy grain 2", "go market-workshop", "produce meal 1"]);
        bool sawCompletion = false;
        int operation = 0;
        var kernel = TownEngine.CreateControlledKernel(history, (observation, _) => {
            string raw = "wait 2";
            if (observation.ActorId == "a") {
                if (!commands.TryDequeue(out raw!)) {
                    TownExperience experience = Assert.IsType<TownExperience>(observation.LastExperience);
                    Assert.Equal(TownEventKind.ProductionCompleted, experience.Kind);
                    Assert.Contains("2 meal", experience.Text, StringComparison.Ordinal);
                    Assert.Equal("produce meal 1", observation.LastOperationReceipt!.Operation.RawInput);
                    sawCompletion = true;
                    raw = "wait 2";
                }
            } else {
                Assert.Null(observation.LastExperience);
            }
            operation++;
            return ValueTask.FromResult(new TownControllerOperation(raw, $"tool-{operation}", $"record-{operation}"));
        });
        for (int step = 0; step < 100 && !sawCompletion; step++) { await kernel.StepAsync(start.World.Business.EndTime); }
        Assert.True(sawCompletion);
        Assert.Null(history.State.Business.Character("b").LastExperience);
        Assert.Null(history.State.Business.Character("c").LastExperience);
    }

    private sealed class InterruptibleHistory(InMemoryOccurrenceHistory<TownWorld, TownFact> inner) : IOccurrenceHistory<TownWorld, TownFact> {
        public bool FailNextState { get; set; }
        public TownWorld State => inner.State;
        public KernelCursor Cursor => inner.Cursor;
        public OccurrenceEvent<TownFact>? PendingEvent => inner.PendingEvent;
        public void CommitEvent(OccurrenceEvent<TownFact> occurrence) => inner.CommitEvent(occurrence);
        public void CommitState(TownWorld nextState, KernelCursor nextCursor) {
            if (FailNextState) { FailNextState = false; throw new IOException("Interrupted after E, before S."); }
            inner.CommitState(nextState, nextCursor);
        }
    }
}
