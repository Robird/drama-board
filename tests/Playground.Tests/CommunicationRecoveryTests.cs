using DramaBoard.Inventory;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

/// <summary>Communication is one recorded game fact, including both inventories and actor-local delivery.</summary>
public sealed class CommunicationRecoveryTests {
    private const string Claim = "The road is beyond a secret ravine; this is my claim, not a confirmed map.";
    private const string OverBudget = "say a beyond-budget";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fourth_communication_event_recovers_once_then_cold_open_preserves_budget_and_original_wait(bool gift) {
        using var store = new TemporaryStore();
        PlaygroundWorld initial = CreateWorld();
        var baselineHistory = Memory(initial);
        var baseline = PlaygroundEngine.CreateKernel(baselineHistory, characterController: new ExchangeScript(gift));
        await UntilState(baseline, world => world.GetCharacter("b").Communication.SentCount == 4);
        PlaygroundFact finalFact = Assert.Single(baseline.LastCompletion!.Event.Facts);
        Assert.Equal(gift ? PlaygroundEventKind.ItemGiven : PlaygroundEventKind.SpeechSent, finalFact.Kind);
        Assert.Equal("b", finalFact.ActorId);
        Assert.Equal("a", finalFact.TargetActorId);
        Assert.Equal(gift ? null : Claim, finalFact.SpeechText);
        Assert.Equal(gift ? "item.water" : null, finalFact.ItemId);
        Assert.Equal(gift ? 2 : 0, finalFact.ItemCount);
        Assert.Equal(Hour(8), baseline.World.Time);
        AssertDelivery(finalFact, gift);

        KernelCursor beforeCursor = default;
        string? beforeMaterials = null;
        using (var history = PlaygroundDurableHistory.Create(store.StorePath, initial, InitialCursor(initial))) {
            var interrupted = PlaygroundEngine.CreateKernel(new StopAfterFourthCommunication(history),
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

        using (var history = PlaygroundDurableHistory.Open(store.StorePath)) {
            Assert.Equal(beforeCursor, history.Cursor);
            Assert.Equal(beforeMaterials, MaterialSnapshot(history.State.Business.InteractionStates));
            var recovery = new SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact>(history,
                new SimulationRules(0, 1024), [new ForbiddenPlanning()], PlaygroundEngine.Fold,
                (before, instant, facts, folded) => PlaygroundEngine.Complete(before, instant, facts, folded, PredictionMode.Incremental),
                PlaygroundEngine.Validate);
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
        using (var history = PlaygroundDurableHistory.Open(store.StorePath)) {
            Assert.Null(history.PendingEvent);
            AssertBalancesAndBudget(history.State, gift);
            Assert.True(history.State.GetCharacter("a").Communication.AttentionPending);
            AssertOriginalWait(history.State);
            AssertPrivateKnowledge(history.State);
            AssertContextDelivery(history.State, gift);
            Assert.NotNull(PlaygroundActions.Check(history.State, "b", PlaygroundCommand.Say("a", "one extra")));
            Assert.NotNull(PlaygroundActions.Check(history.State, "b", PlaygroundCommand.Give("a", "item.water", 1)));
            var resumed = PlaygroundEngine.CreateKernel(history, characterController: new ReleaseScript());
            baseline = PlaygroundEngine.CreateKernel(baselineHistory, characterController: new ReleaseScript());
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
                fact.Kind == PlaygroundEventKind.OperationRejected && fact.ActorId == "b" && fact.RawInput == OverBudget);
            // The stored count is not eagerly cleared. The effective next-instant allowance is a pure query.
            Assert.Null(PlaygroundActions.Check(resumed.World, "b", PlaygroundCommand.Say("a", "new instant")));
            AssertPrivateKnowledge(resumed.World);
            finalMaterials = MaterialSnapshot(resumed.World.Business.InteractionStates);
        }

        using var reopened = PlaygroundDurableHistory.Open(store.StorePath);
        Assert.Null(reopened.PendingEvent);
        Assert.Equal(Hour(14), reopened.State.Time);
        AssertBalancesAndBudget(reopened.State, gift);
        Assert.Equal(finalMaterials, MaterialSnapshot(reopened.State.Business.InteractionStates));
        Assert.Single(reopened.ReadCommittedOperations([finalFact.OperationRecordId!]));
    }

    private static PlaygroundWorld CreateWorld() {
        var initial = PlaygroundGameFactory.CreateSurvival(Hour(8), enableNeeds: false, enableWeather: false, characterCount: 2);
        var a = new PlaygroundCharacter("a", "Receiver", survival: PlaygroundSurvival.Create(Hour(8), 100, 100, 100));
        var b = new PlaygroundCharacter("b", "Sender", backpack: new ItemInventory(new Dictionary<string, int> { ["item.water"] = 3 }),
            survival: PlaygroundSurvival.Create(Hour(8), 100, 100, 100));
        PlaygroundExpedition expedition = initial.World.Expedition!;
        var remembered = new PlaygroundExpedition(expedition.ScenarioId, expedition.Spatial,
            expedition.Visits.Append(new PlaygroundVisit("b", "ravine", Hour(7), [])));
        return PlaygroundEngine.Rebuild(initial.World.Business.Replace(Hour(8), changedCharacters: [a, b], expedition: remembered), Hour(8));
    }

    private static void AssertBalancesAndBudget(PlaygroundWorld world, bool gift) {
        Assert.Equal(gift ? 2 : 0, world.GetCharacter("a").Backpack.Count("item.water"));
        Assert.Equal(gift ? 1 : 3, world.GetCharacter("b").Backpack.Count("item.water"));
        Assert.Equal(3, world.Characters.Sum(actor => actor.Backpack.Count("item.water")));
        Assert.Equal(Hour(8), world.GetCharacter("b").Communication.BudgetAt);
        Assert.Equal(4, world.GetCharacter("b").Communication.SentCount);
        Assert.Equal(0, world.GetCharacter("a").Communication.SentCount);
    }

    private static void AssertOriginalWait(PlaygroundWorld world) {
        PlaygroundActivity wait = Assert.IsType<PlaygroundActivity>(world.GetCharacter("a").Activity);
        Assert.Equal(PlaygroundActivityKind.Wait, wait.Kind);
        Assert.Equal(1, wait.Id);
        Assert.Equal(Hour(8), wait.StartedAt);
        Assert.Equal(Hour(14), wait.Due);
        Assert.Equal(6 * PlaygroundTime.Hour, wait.DurationTicks);
    }

    private static void AssertPrivateKnowledge(PlaygroundWorld world) {
        string receiver = MaterialSnapshot(world.Business.InteractionStates["a"]);
        string sender = MaterialSnapshot(world.Business.InteractionStates["b"]);
        Assert.DoesNotContain("sender-private-notebook", receiver, StringComparison.Ordinal);
        Assert.DoesNotContain("receiver-private-notebook", sender, StringComparison.Ordinal);
        Assert.DoesNotContain(world.Expedition!.Visits, visit => visit.ActorId == "a" && visit.PlaceId == "ravine");
        Assert.DoesNotContain(PlaygroundDecisionContext.Observe(world, "a").Memories, memory => memory.PlaceId == "ravine");
        Assert.Contains(world.Expedition.Visits, visit => visit.ActorId == "b" && visit.PlaceId == "ravine");
    }

    private static void AssertDelivery(PlaygroundFact fact, bool gift) {
        PlaygroundObservedEvent[] delivered = fact.ObservedEvents.Where(entry => entry.Communication is not null).ToArray();
        Assert.Equal(new[] { "a", "b" }, delivered.Select(entry => entry.ObserverId).Order(StringComparer.Ordinal));
        foreach (PlaygroundObservedEvent entry in delivered) {
            var message = entry.Communication!;
            Assert.Equal("b", message.SenderId);
            Assert.Equal("Sender", message.SenderName);
            Assert.Equal("a", message.RecipientId);
            Assert.Equal("Receiver", message.RecipientName);
            Assert.Equal(gift ? null : Claim, message.SpeechText);
            Assert.Equal(gift ? "item.water" : null, message.ItemId);
            Assert.Equal(gift ? 2 : 0, message.ItemCount);
            Assert.Equal(gift ? PlaygroundObservedEventKind.ItemGiven : PlaygroundObservedEventKind.SpeechSent, entry.Kind);
            Assert.DoesNotContain(entry.Effects, effect => effect.Key.Category is "location" or "memory");
        }
    }

    private static void AssertContextDelivery(PlaygroundWorld world, bool gift) {
        foreach (string actorId in new[] { "a", "b" }) {
            CharacterInteractionState state = world.Business.InteractionStates[actorId];
            IEnumerable<CharacterExperience> experiences = state.UndeliveredExperiences;
            if (state.CurrentContext is { } context) {
                experiences = experiences.Concat(context.Opening.Experiences).Concat(context.Items.SelectMany(item => item.Experiences));
            }
            PlaygroundObservedEvent delivery = Assert.Single(experiences.Select(experience => experience.Event), entry =>
                entry.Communication is { } message && (gift ? message.ItemId == "item.water" && message.ItemCount == 2 : message.SpeechText == Claim));
            Assert.Equal(actorId, delivery.ObserverId);
            Assert.Equal("b", delivery.Communication!.SenderId);
            Assert.Equal("a", delivery.Communication.RecipientId);
        }
    }

    private static void AssertWorldEqual(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> expected,
        SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> actual) {
        Assert.Equal(expected.Cursor, actual.Cursor);
        Assert.Equal(MaterialSnapshot(expected.World.Business), MaterialSnapshot(actual.World.Business));
        Assert.Equal(expected.World.Forecasts.ToString(), actual.World.Forecasts.ToString());
        Assert.Equal(expected.World.Spatial!.Contacts, actual.World.Spatial!.Contacts);
    }

    private static async Task Advance(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel) {
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(24)));
        kernel.World.Validate();
        PlaygroundWorld rebuilt = PlaygroundEngine.Rebuild(kernel.World.Business, kernel.World.Time);
        Assert.Equal(rebuilt.Forecasts.ToString(), kernel.World.Forecasts.ToString());
        Assert.Equal(rebuilt.Spatial!.Contacts, kernel.World.Spatial!.Contacts);
    }

    private static async Task UntilState(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel, Func<PlaygroundWorld, bool> predicate) {
        for (int steps = 0; steps < 80; steps++) {
            if (predicate(kernel.World)) { return; }
            await Advance(kernel);
        }
        Assert.Fail("Expected communication boundary was not reached.");
    }

    private static PlaygroundControllerOperation OperationFor(PlaygroundDecisionContext context, string raw) {
        string id = $"communication/{context.Observation.Actor.Id}/{context.Context.ContextSequence}/{context.Context.LastOperationSequence + 1}";
        return new(id, raw, id);
    }

    private sealed class ExchangeScript(bool gift) : IPlaygroundCharacterController {
        private int _aCalls;
        private int _bCalls;
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) {
            PlaygroundCommand command;
            if (context.Observation.Actor.Id == "a") {
                command = ++_aCalls switch {
                    1 => PlaygroundCommand.Handoff("receiver-private-notebook"),
                    2 => PlaygroundCommand.Wait(6),
                    _ => PlaygroundCommand.Continue()
                };
            } else {
                int call = ++_bCalls;
                command = call switch {
                    1 => PlaygroundCommand.Handoff("sender-private-notebook"),
                    < 5 => PlaygroundCommand.Say("a", $"message {call - 1}"),
                    5 => gift ? PlaygroundCommand.Give("a", "item.water", 2) : PlaygroundCommand.Say("a", Claim),
                    _ => throw new InvalidOperationException("The fixture must stop at the fourth communication.")
                };
            }
            Assert.Null(context.Check(command));
            return ValueTask.FromResult(OperationFor(context, PlaygroundCommandParser.Format(command)));
        }
    }

    private sealed class ReleaseScript : IPlaygroundCharacterController {
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) {
            string raw = context.Observation.Actor.Id == "a" ? "continue" :
                context.Context.Items.Any(item => item.IsRejected && item.RawInput == OverBudget) ? "wait 6" : OverBudget;
            if (raw == OverBudget) { Assert.NotNull(context.Check(PlaygroundCommandParser.Parse(raw))); }
            else { Assert.Null(context.Check(PlaygroundCommandParser.Parse(raw))); }
            return ValueTask.FromResult(OperationFor(context, raw));
        }
    }

    private sealed class ForbiddenPlanning : IOccurrenceRule<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> {
        public IReadOnlyList<OccurrenceCandidate<PlaygroundPrediction>> Forecast(PlaygroundWorld world, SimulationRules rules) =>
            throw new InvalidOperationException("Pending communication recovery must not forecast or request a controller.");
        public ValueTask<TransitionDraft<PlaygroundFact>> PlanSelectedAsync(PlaygroundWorld world,
            OccurrenceCandidate<PlaygroundPrediction> winner, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Pending communication recovery must not revalidate or transfer inventory again.");
    }

    private sealed class InjectedCrash : IOException { }
    private sealed class StopAfterFourthCommunication(IOccurrenceHistory<PlaygroundWorld, PlaygroundFact> inner)
        : IOccurrenceHistory<PlaygroundWorld, PlaygroundFact> {
        public PlaygroundWorld State => inner.State;
        public KernelCursor Cursor => inner.Cursor;
        public OccurrenceEvent<PlaygroundFact>? PendingEvent => inner.PendingEvent;
        public void CommitEvent(OccurrenceEvent<PlaygroundFact> occurrence) {
            inner.CommitEvent(occurrence);
            if (occurrence.Facts.Any(fact => fact.Kind is PlaygroundEventKind.SpeechSent or PlaygroundEventKind.ItemGiven &&
                fact.ChangedCharacters.Any(actor => actor.Id == "b" && actor.Communication.SentCount == 4))) { throw new InjectedCrash(); }
        }
        public void CommitState(PlaygroundWorld state, KernelCursor cursor) => inner.CommitState(state, cursor);
    }
}
