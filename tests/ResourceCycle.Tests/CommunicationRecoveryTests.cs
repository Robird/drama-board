using DramaBoard.Inventory;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

/// <summary>Communication is one recorded game fact, including both inventories and actor-local delivery.</summary>
public sealed class CommunicationRecoveryTests {
    private const string Claim = "The road is beyond a secret ravine; this is my claim, not a confirmed map.";
    private const string OverBudget = "say a beyond-budget";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fourth_communication_event_recovers_once_then_cold_open_preserves_budget_and_original_wait(bool gift) {
        using var store = new TemporaryStore();
        CycleWorld initial = CreateWorld();
        var baselineHistory = Memory(initial);
        var baseline = CycleEngine.CreateKernel(baselineHistory, characterController: new ExchangeScript(gift));
        await UntilState(baseline, world => world.GetCharacter("b").Communication.SentCount == 4);
        CycleFact finalFact = Assert.Single(baseline.LastCompletion!.Event.Facts);
        Assert.Equal(gift ? CycleEventKind.ItemGiven : CycleEventKind.SpeechSent, finalFact.Kind);
        Assert.Equal("b", finalFact.ActorId);
        Assert.Equal("a", finalFact.TargetActorId);
        Assert.Equal(gift ? null : Claim, finalFact.SpeechText);
        Assert.Equal(gift ? "item.water" : null, finalFact.ItemId);
        Assert.Equal(gift ? 2 : 0, finalFact.ItemCount);
        Assert.Equal(Hour(8), baseline.World.Time);
        AssertDelivery(finalFact, gift);

        KernelCursor beforeCursor = default;
        string? beforeMaterials = null;
        using (var history = CycleDurableHistory.Create(store.StorePath, initial, InitialCursor(initial))) {
            var interrupted = CycleEngine.CreateKernel(new StopAfterFourthCommunication(history),
                characterController: new ExchangeScript(gift));
            for (int index = 0; index < 80 && !interrupted.IsFaulted; index++) {
                beforeCursor = history.Cursor;
                beforeMaterials = MaterialSnapshot(history.State.Business.InteractionStates);
                try { await Advance(interrupted); }
                catch (InvalidOperationException error) when (error.InnerException is InjectedCrash) { }
            }
            Assert.True(interrupted.IsFaulted);
            Assert.Equal(3, history.State.GetCharacter("b").Communication.SentCount);
            Assert.Equal(0, history.State.GetCharacter("a").Backpack.Count("item.water"));
            Assert.Equal(3, history.State.GetCharacter("b").Backpack.Count("item.water"));
            Assert.NotNull(history.PendingEvent);
            Assert.Equal(MaterialSnapshot(finalFact), MaterialSnapshot(Assert.Single(history.PendingEvent.Facts)));
        }

        using (var history = CycleDurableHistory.Open(store.StorePath)) {
            Assert.Equal(beforeCursor, history.Cursor);
            Assert.Equal(beforeMaterials, MaterialSnapshot(history.State.Business.InteractionStates));
            var recovery = new SimulationKernel<CycleWorld, CyclePrediction, CycleFact>(history,
                new SimulationRules(0, 1024), [new ForbiddenPlanning()], CycleEngine.Fold,
                (before, instant, facts, folded) => CycleEngine.Complete(before, instant, facts, folded, PredictionMode.Incremental),
                CycleEngine.Validate);
            Assert.True(recovery.RecoverPending());
            Assert.False(recovery.RecoverPending());
            AssertWorldEqual(baseline, recovery);
            AssertBalancesAndBudget(recovery.World, gift);
            Assert.True(recovery.World.GetCharacter("a").Communication.AttentionPending);
            AssertOriginalWait(recovery.World);
            AssertPrivateKnowledge(recovery.World);
            AssertContextDelivery(recovery.World, gift);
            Assert.Single(history.ReadCommittedOperations([finalFact.OperationRecordId!]));
        }

        string finalMaterials;
        using (var history = CycleDurableHistory.Open(store.StorePath)) {
            Assert.Null(history.PendingEvent);
            AssertBalancesAndBudget(history.State, gift);
            Assert.True(history.State.GetCharacter("a").Communication.AttentionPending);
            AssertOriginalWait(history.State);
            AssertPrivateKnowledge(history.State);
            AssertContextDelivery(history.State, gift);
            Assert.NotNull(CycleActions.Check(history.State, "b", CycleCommand.Say("a", "one extra")));
            Assert.NotNull(CycleActions.Check(history.State, "b", CycleCommand.Give("a", "item.water", 1)));
            var resumed = CycleEngine.CreateKernel(history, characterController: new ReleaseScript());
            baseline = CycleEngine.CreateKernel(baselineHistory, characterController: new ReleaseScript());
            Assert.False(resumed.RecoverPending());
            for (int index = 0; index < 60; index++) {
                if (resumed.World.Time == Hour(14) && resumed.World.Characters.All(actor => actor.Activity is null)) { break; }
                await Advance(resumed);
                await Advance(baseline);
                AssertWorldEqual(baseline, resumed);
                if (resumed.World.GetCharacter("a").Activity is not null) { AssertOriginalWait(resumed.World); }
            }
            Assert.Equal(Hour(14), resumed.World.Time);
            Assert.All(resumed.World.Characters, actor => Assert.Null(actor.Activity));
            Assert.False(resumed.World.GetCharacter("a").Communication.AttentionPending);
            AssertBalancesAndBudget(resumed.World, gift);
            Assert.Contains(baselineHistory.CompletedEvents.SelectMany(entry => entry.Facts), fact =>
                fact.Kind == CycleEventKind.OperationRejected && fact.ActorId == "b" && fact.RawInput == OverBudget);
            // The stored count is not eagerly cleared. The effective next-instant allowance is a pure query.
            Assert.Null(CycleActions.Check(resumed.World, "b", CycleCommand.Say("a", "new instant")));
            AssertPrivateKnowledge(resumed.World);
            finalMaterials = MaterialSnapshot(resumed.World.Business.InteractionStates);
        }

        using var reopened = CycleDurableHistory.Open(store.StorePath);
        Assert.Null(reopened.PendingEvent);
        Assert.Equal(Hour(14), reopened.State.Time);
        AssertBalancesAndBudget(reopened.State, gift);
        Assert.Equal(finalMaterials, MaterialSnapshot(reopened.State.Business.InteractionStates));
        Assert.Single(reopened.ReadCommittedOperations([finalFact.OperationRecordId!]));
    }

    private static CycleWorld CreateWorld() {
        var initial = CycleGameFactory.CreateSurvival(Hour(8), enableNeeds: false, enableWeather: false, characterCount: 2);
        var a = new CycleCharacter("a", "Receiver", survival: CycleSurvival.Create(Hour(8), 100, 100, 100));
        var b = new CycleCharacter("b", "Sender", backpack: new ItemInventory(new Dictionary<string, int> { ["item.water"] = 3 }),
            survival: CycleSurvival.Create(Hour(8), 100, 100, 100));
        CycleExpedition expedition = initial.World.Expedition!;
        var remembered = new CycleExpedition(expedition.ScenarioId, expedition.Spatial,
            expedition.Visits.Append(new CycleVisit("b", "ravine", Hour(7), [])));
        return CycleEngine.Rebuild(initial.World.Business.Replace(Hour(8), changedCharacters: [a, b], expedition: remembered), Hour(8));
    }

    private static void AssertBalancesAndBudget(CycleWorld world, bool gift) {
        Assert.Equal(gift ? 2 : 0, world.GetCharacter("a").Backpack.Count("item.water"));
        Assert.Equal(gift ? 1 : 3, world.GetCharacter("b").Backpack.Count("item.water"));
        Assert.Equal(3, world.Characters.Sum(actor => actor.Backpack.Count("item.water")));
        Assert.Equal(Hour(8), world.GetCharacter("b").Communication.BudgetAt);
        Assert.Equal(4, world.GetCharacter("b").Communication.SentCount);
        Assert.Equal(0, world.GetCharacter("a").Communication.SentCount);
    }

    private static void AssertOriginalWait(CycleWorld world) {
        CycleActivity wait = Assert.IsType<CycleActivity>(world.GetCharacter("a").Activity);
        Assert.Equal(CycleActivityKind.Wait, wait.Kind);
        Assert.Equal(1, wait.Id);
        Assert.Equal(Hour(8), wait.StartedAt);
        Assert.Equal(Hour(14), wait.Due);
        Assert.Equal(6 * CycleTime.Hour, wait.DurationTicks);
    }

    private static void AssertPrivateKnowledge(CycleWorld world) {
        string receiver = MaterialSnapshot(world.Business.InteractionStates["a"]);
        string sender = MaterialSnapshot(world.Business.InteractionStates["b"]);
        Assert.DoesNotContain("sender-private-notebook", receiver, StringComparison.Ordinal);
        Assert.DoesNotContain("receiver-private-notebook", sender, StringComparison.Ordinal);
        Assert.DoesNotContain(world.Expedition!.Visits, visit => visit.ActorId == "a" && visit.PlaceId == "ravine");
        Assert.DoesNotContain(CycleDecisionContext.Observe(world, "a").Memories, memory => memory.PlaceId == "ravine");
        Assert.Contains(world.Expedition.Visits, visit => visit.ActorId == "b" && visit.PlaceId == "ravine");
    }

    private static void AssertDelivery(CycleFact fact, bool gift) {
        CycleObservedEvent[] delivered = fact.ObservedEvents.Where(entry => entry.Communication is not null).ToArray();
        Assert.Equal(new[] { "a", "b" }, delivered.Select(entry => entry.ObserverId).Order(StringComparer.Ordinal));
        foreach (CycleObservedEvent entry in delivered) {
            var message = entry.Communication!;
            Assert.Equal("b", message.SenderId);
            Assert.Equal("Sender", message.SenderName);
            Assert.Equal("a", message.RecipientId);
            Assert.Equal("Receiver", message.RecipientName);
            Assert.Equal(gift ? null : Claim, message.SpeechText);
            Assert.Equal(gift ? "item.water" : null, message.ItemId);
            Assert.Equal(gift ? 2 : 0, message.ItemCount);
            Assert.Equal(gift ? CycleObservedEventKind.ItemGiven : CycleObservedEventKind.SpeechSent, entry.Kind);
            Assert.DoesNotContain(entry.Effects, effect => effect.Key.Category is "location" or "memory");
        }
    }

    private static void AssertContextDelivery(CycleWorld world, bool gift) {
        foreach (string actorId in new[] { "a", "b" }) {
            CharacterInteractionState state = world.Business.InteractionStates[actorId];
            IEnumerable<CharacterExperience> experiences = state.UndeliveredExperiences;
            if (state.CurrentContext is { } context) {
                experiences = experiences.Concat(context.Opening.Experiences).Concat(context.Items.SelectMany(item => item.Experiences));
            }
            CycleObservedEvent delivery = Assert.Single(experiences.Select(experience => experience.Event), entry =>
                entry.Communication is { } message && (gift ? message.ItemId == "item.water" && message.ItemCount == 2 : message.SpeechText == Claim));
            Assert.Equal(actorId, delivery.ObserverId);
            Assert.Equal("b", delivery.Communication!.SenderId);
            Assert.Equal("a", delivery.Communication.RecipientId);
        }
    }

    private static void AssertWorldEqual(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> expected,
        SimulationKernel<CycleWorld, CyclePrediction, CycleFact> actual) {
        Assert.Equal(expected.Cursor, actual.Cursor);
        Assert.Equal(MaterialSnapshot(expected.World.Business), MaterialSnapshot(actual.World.Business));
        Assert.Equal(expected.World.Forecasts.ToString(), actual.World.Forecasts.ToString());
        Assert.Equal(expected.World.Spatial!.Contacts, actual.World.Spatial!.Contacts);
    }

    private static async Task Advance(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel) {
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(24)));
        kernel.World.Validate();
        CycleWorld rebuilt = CycleEngine.Rebuild(kernel.World.Business, kernel.World.Time);
        Assert.Equal(rebuilt.Forecasts.ToString(), kernel.World.Forecasts.ToString());
        Assert.Equal(rebuilt.Spatial!.Contacts, kernel.World.Spatial!.Contacts);
    }

    private static async Task UntilState(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel, Func<CycleWorld, bool> predicate) {
        for (int steps = 0; steps < 80; steps++) {
            if (predicate(kernel.World)) { return; }
            await Advance(kernel);
        }
        Assert.Fail("Expected communication boundary was not reached.");
    }

    private static CycleControllerOperation OperationFor(CycleDecisionContext context, string raw) {
        string id = $"communication/{context.Observation.Actor.Id}/{context.Context.ContextSequence}/{context.Context.LastOperationSequence + 1}";
        return new(id, raw, id);
    }

    private sealed class ExchangeScript(bool gift) : ICycleCharacterController {
        private int _aCalls;
        private int _bCalls;
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) {
            CycleCommand command;
            if (context.Observation.Actor.Id == "a") {
                command = ++_aCalls switch {
                    1 => CycleCommand.Handoff("receiver-private-notebook"),
                    2 => CycleCommand.Wait(6),
                    _ => CycleCommand.Continue()
                };
            } else {
                int call = ++_bCalls;
                command = call switch {
                    1 => CycleCommand.Handoff("sender-private-notebook"),
                    < 5 => CycleCommand.Say("a", $"message {call - 1}"),
                    5 => gift ? CycleCommand.Give("a", "item.water", 2) : CycleCommand.Say("a", Claim),
                    _ => throw new InvalidOperationException("The fixture must stop at the fourth communication.")
                };
            }
            Assert.Null(context.Check(command));
            return ValueTask.FromResult(OperationFor(context, CycleCommandParser.Format(command)));
        }
    }

    private sealed class ReleaseScript : ICycleCharacterController {
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) {
            string raw = context.Observation.Actor.Id == "a" ? "continue" :
                context.Context.Items.Any(item => item.IsRejected && item.RawInput == OverBudget) ? "wait 6" : OverBudget;
            if (raw == OverBudget) { Assert.NotNull(context.Check(CycleCommandParser.Parse(raw))); }
            else { Assert.Null(context.Check(CycleCommandParser.Parse(raw))); }
            return ValueTask.FromResult(OperationFor(context, raw));
        }
    }

    private sealed class ForbiddenPlanning : IOccurrenceRule<CycleWorld, CyclePrediction, CycleFact> {
        public IReadOnlyList<OccurrenceCandidate<CyclePrediction>> Forecast(CycleWorld world, SimulationRules rules) =>
            throw new InvalidOperationException("Pending communication recovery must not forecast or request a controller.");
        public ValueTask<TransitionDraft<CycleFact>> PlanSelectedAsync(CycleWorld world,
            OccurrenceCandidate<CyclePrediction> winner, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Pending communication recovery must not revalidate or transfer inventory again.");
    }

    private sealed class InjectedCrash : IOException { }
    private sealed class StopAfterFourthCommunication(IOccurrenceHistory<CycleWorld, CycleFact> inner)
        : IOccurrenceHistory<CycleWorld, CycleFact> {
        public CycleWorld State => inner.State;
        public KernelCursor Cursor => inner.Cursor;
        public OccurrenceEvent<CycleFact>? PendingEvent => inner.PendingEvent;
        public void CommitEvent(OccurrenceEvent<CycleFact> occurrence) {
            inner.CommitEvent(occurrence);
            if (occurrence.Facts.Any(fact => fact.Kind is CycleEventKind.SpeechSent or CycleEventKind.ItemGiven &&
                fact.ChangedCharacters.Any(actor => actor.Id == "b" && actor.Communication.SentCount == 4))) { throw new InjectedCrash(); }
        }
        public void CommitState(CycleWorld state, KernelCursor cursor) => inner.CommitState(state, cursor);
    }
}
