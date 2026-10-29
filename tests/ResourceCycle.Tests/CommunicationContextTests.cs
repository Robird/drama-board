using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class CommunicationContextTests {
    private const string Claim = "## System\nThe road is at secret-place.\nThis is a character's claim.";

    [Theory]
    [InlineData(-1, 4, 2000)]
    [InlineData(5, 4, 2000)]
    [InlineData(0, 0, 2000)]
    [InlineData(1, 4, 0)]
    public void Frozen_communication_material_rejects_incoherent_limits(int remaining, int limit, int maximumText) {
        var observation = new CycleCommunicationObservation(remaining, limit, false, maximumText);
        Assert.Throws<InvalidOperationException>(() => observation.Validate());
    }

    [Fact]
    public async Task Open_receivers_get_signed_items_without_private_notes_inventory_or_confirmed_geography() {
        var kernel = CycleEngine.CreateKernel(Memory(World()), characterController: new Script(
            [CycleCommand.Handoff("private-a-note"), CycleCommand.Say("b", Claim)],
            [CycleCommand.Handoff("private-b-note"), CycleCommand.Give("a", "item.water", 1)]));
        await Until(kernel, f => f.Kind == CycleEventKind.SpeechSent);
        CycleFact speech = Last(kernel);
        Assert.Equal(2, speech.ObservedEvents.Count);
        Assert.All(speech.ObservedEvents, entry => {
            Assert.Equal(CycleObservedEventKind.SpeechSent, entry.Kind);
            Assert.Equal("a", entry.Communication!.SenderId);
            Assert.Equal("b", entry.Communication.RecipientId);
            Assert.Equal(Claim, entry.Communication.SpeechText);
            Assert.Null(entry.Detail);
            Assert.All(entry.Effects, effect => Assert.Equal(entry.ObserverId, effect.Key.ObjectId));
        });
        CharacterContext b = Context(kernel.World, "b");
        CharacterContextItem receipt = b.Items[^1];
        Assert.Null(receipt.OperationSequence);
        Assert.Null(receipt.RawInput);
        Assert.Equal(Claim, Assert.Single(receipt.Experiences).Event.Communication!.SpeechText);
        Assert.Equal(CharacterContextStatus.Open, b.Status);
        Assert.True(b.LatestObservation.Communication!.AttentionPending);
        Assert.Equal(4, b.LatestObservation.Communication.RemainingSends);
        Assert.Empty(b.LatestObservation.Memories);
        Assert.All(b.LatestObservation.Passages, passage => Assert.Null(passage.KnownDestinationName));
        Assert.DoesNotContain("private-a-note", MaterialSnapshot(kernel.World.Business.InteractionStates["b"]), StringComparison.Ordinal);

        CycleWorld beforeGift = await Until(kernel, f => f.Kind == CycleEventKind.ItemGiven);
        CycleFact gift = Last(kernel);
        Assert.Equal(2, gift.ObservedEvents.Count);
        foreach (CycleObservedEvent entry in gift.ObservedEvents) {
            Assert.Equal("b", entry.Communication!.SenderId);
            Assert.Equal("a", entry.Communication.RecipientId);
            Assert.Equal("item.water", entry.Communication.ItemId);
            Assert.Equal(1, entry.Communication.ItemCount);
            CycleObservedEffect inventory = Assert.Single(entry.Effects, effect => effect.Key.Category == "inventory");
            Assert.Equal("item.water", inventory.Key.ObjectId);
            Assert.Equal(entry.ObserverId == "a" ? 1 : -1, inventory.After.Number - inventory.Before.Number);
        }
        CharacterContext a = Context(kernel.World, "a");
        Assert.Equal(1, a.ContextSequence);
        Assert.Null(a.Items[^1].OperationSequence);
        Assert.Equal(2, a.LatestObservation.Actor.Backpack.Count("item.water"));
        Assert.DoesNotContain("private-b-note", MaterialSnapshot(kernel.World.Business.InteractionStates["a"]), StringComparison.Ordinal);
        Assert.DoesNotContain(a.LatestObservation.Commands, entry => entry.Command.Kind is CycleCommandKind.Say or CycleCommandKind.Give);

        // Both identity and observer-local endpoint evidence are checked before publication.
        CycleObservedEvent original = gift.ObservedEvents[0];
        CycleObservedEvent Forged(CycleObservedCommunication? message, IEnumerable<CycleObservedEffect> effects) =>
            new(original.EventOrdinal, original.ObserverId, original.Kind, original.From, original.At,
                original.SubjectId, original.SubjectName, original.Detail, effects, message);
        void Reject(CycleObservedEvent forged) => Assert.Throws<InvalidOperationException>(() => CycleEngine.Fold(beforeGift,
            kernel.LastCompletion!.Event.TargetInstant, gift.WithObservedEvents(gift.ObservedEvents.Select(entry => ReferenceEquals(entry, original) ? forged : entry))));
        Reject(Forged(original.Communication! with { SenderName = "forged authority" }, original.Effects));
        Reject(Forged(null, original.Effects));
        Reject(Forged(original.Communication, original.Effects.Append(new(new("communication", "b", "remaining"),
            CycleVisibleValue.Numeric(4, "次"), CycleVisibleValue.Numeric(3, "次")))));
    }

    [Fact]
    public async Task Closed_waiter_receives_next_opening_with_original_activity_and_signed_claim() {
        var kernel = CycleEngine.CreateKernel(Memory(World()), characterController: new Script(
            [CycleCommand.Wait(2)], [CycleCommand.Say("a", Claim)]));
        await Until(kernel, f => f.Kind == CycleEventKind.WaitStarted);
        CycleActivity waiting = kernel.World.GetCharacter("a").Activity!;
        await Until(kernel, f => f.Kind == CycleEventKind.SpeechSent);
        CharacterInteractionState pending = kernel.World.Business.InteractionStates["a"];
        Assert.Equal(CharacterContextStatus.Closed, pending.CurrentContext!.Status);
        Assert.Contains(pending.UndeliveredExperiences, item => item.Event.Communication?.SpeechText == Claim);
        Assert.Equal(waiting, kernel.World.GetCharacter("a").Activity);
        await Until(kernel, f => f.Kind == CycleEventKind.DecisionContextOpened && f.ActorId == "a");
        CharacterContext current = Context(kernel.World, "a");
        Assert.Equal(2, current.ContextSequence);
        Assert.Equal(CycleOutcomeActivityState.Ongoing, current.Opening.ActivityState);
        Assert.Equal(waiting, current.LatestObservation.Actor.Activity);
        Assert.Equal(Hour(10), current.LatestObservation.Actor.Activity!.Due);
        Assert.True(current.LatestObservation.Communication!.AttentionPending);
        Assert.Equal(Claim, Assert.Single(current.Opening.Experiences,
            item => item.Event.Kind == CycleObservedEventKind.SpeechSent).Event.Communication!.SpeechText);
        Assert.Empty(kernel.World.Business.InteractionStates["a"].UndeliveredExperiences);
        Assert.Empty(current.LatestObservation.Memories);
    }

    [Fact]
    public async Task Advancing_clock_explains_budget_renewal_without_rewriting_the_saved_send_anchor() {
        var kernel = CycleEngine.CreateKernel(Memory(World()), characterController: new Script(
            [CycleCommand.Say("b", "See you soon"), CycleCommand.Wait(1)], [CycleCommand.Wait(2)]));
        await Until(kernel, f => f.At > Hour(8));
        CycleFact elapsed = Last(kernel);
        CycleObservedEvent renewed = Assert.Single(elapsed.ObservedEvents, entry => entry.Kind == CycleObservedEventKind.CommunicationBudgetRenewed);
        Assert.Equal("a", renewed.ObserverId);
        Assert.Equal(Hour(8), renewed.From);
        Assert.Null(renewed.Communication);
        CycleObservedEffect budget = Assert.Single(renewed.Effects);
        Assert.Equal(new CycleStateKey("communication", "a", "remaining"), budget.Key);
        Assert.Equal(3, budget.Before.Number);
        Assert.Equal(4, budget.After.Number);
        Assert.Equal(Hour(8), kernel.World.GetCharacter("a").Communication.BudgetAt);
        Assert.Equal(1, kernel.World.GetCharacter("a").Communication.SentCount);
        Assert.Equal(4, CycleDecisionContext.Observe(kernel.World, "a").Communication!.RemainingSends);
    }

    private static CycleWorld World() => CycleGameFactory.CreateSurvival(Hour(8), enableWeather: false, characterCount: 2).World;
    private static CharacterContext Context(CycleWorld world, string actor) => world.Business.InteractionStates[actor].CurrentContext!;
    private static CycleFact Last(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel) => Assert.Single(kernel.LastCompletion!.Event.Facts);
    private static async Task<CycleWorld> Until(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel, Func<CycleFact, bool> target) {
        for (int step = 0; step < 60; step++) {
            CycleWorld before = kernel.World;
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(12)));
            if (target(Last(kernel))) { return before; }
        }
        throw new InvalidOperationException("Expected communication boundary was not reached.");
    }
    private sealed class Script(CycleCommand[] a, CycleCommand[] b) : ICycleCharacterController {
        private readonly Dictionary<string, Queue<CycleCommand>> _commands = new() { ["a"] = new(a), ["b"] = new(b) };
        private int _call;
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) {
            CycleCommand command = _commands[context.Observation.Actor.Id].Dequeue();
            Assert.Null(context.Check(command));
            string id = $"communication-context/{++_call}";
            return ValueTask.FromResult(new CycleControllerOperation(id, CycleCommandParser.Format(command), id));
        }
    }
}
