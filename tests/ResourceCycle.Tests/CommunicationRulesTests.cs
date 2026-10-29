using DramaBoard.Inventory;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

/// <summary>Social decisions pass through the real kernel, including openings, service rotation and rejection.</summary>
public sealed class CommunicationRulesTests {
    [Fact]
    public async Task Gathering_receives_speech_and_gift_then_reply_supply_and_continue_preserve_the_cycle() {
        CycleWorld initial = World(Actor("a", water: 40), Actor("b", waterItems: 2));
        var script = new Script(
            [CycleCommand.Gather("berries", 2), CycleCommand.Say("b", "收到，我会继续采集。"), CycleCommand.Use("item.water"), CycleCommand.Continue()],
            [CycleCommand.Say("a", "先给你饮水。"), CycleCommand.Give("a", "item.water", 1), CycleCommand.Wait(2)]);
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: script);
        await UntilFact(kernel, f => f.Kind == CycleEventKind.GatherStarted);
        CycleActivity activity = kernel.World.GetCharacter("a").Activity!;
        long firstContext = kernel.World.Business.InteractionStates["a"].CurrentContext!.ContextSequence;

        await UntilFact(kernel, f => f.Kind == CycleEventKind.ItemGiven);
        Assert.True(kernel.World.GetCharacter("a").Communication.AttentionPending);
        Assert.Equal(1, kernel.World.GetCharacter("a").Backpack.Count("item.water"));
        Assert.Equal(1, kernel.World.GetCharacter("b").Backpack.Count("item.water"));
        AssertCycle(activity, kernel.World.GetCharacter("a").Activity!);
        await UntilFact(kernel, f => f.Kind == CycleEventKind.AttentionAcknowledged && f.ActorId == "a");

        Assert.Equal(Hour(8), kernel.World.Time);
        AssertCycle(activity, kernel.World.GetCharacter("a").Activity!);
        Assert.False(kernel.World.GetCharacter("a").Communication.AttentionPending);
        Assert.Equal(0, kernel.World.GetCharacter("a").Backpack.Count("item.water"));
        Assert.Equal(85 * CycleSurvival.Scale, kernel.World.GetCharacter("a").Survival!.WaterUnits);
        Assert.Equal(firstContext + 1, kernel.World.Business.InteractionStates["a"].CurrentContext!.ContextSequence);
        Assert.Equal(new[] { "a", "b", "a", "b", "a", "b", "a" }, script.Requests);
        Assert.Equal(6, kernel.World.GetResource("berries").Stock);
    }

    [Fact]
    public async Task Reply_does_not_acknowledge_coexisting_body_warning_or_reset_gathering_deadline() {
        var activity = new CycleActivity(CycleActivityKind.Gather, 1, Hour(8), CycleTime.Hour, Hour(9), "berries", 2);
        var body = new CycleSurvival(Hour(8), 60 * CycleSurvival.Scale, 20 * CycleSurvival.Scale, 60 * CycleSurvival.Scale,
            Rate(0), Rate(0), CycleSurvivalRules.WarmthRate(false, true), warnedMask: 2, pendingMask: 2, pendingSince: Hour(8));
        var a = new CycleCharacter("a", "Alice", activity: activity, nextActivityId: 2, survival: body);
        CycleWorld initial = World(a, Actor("b"), lastServed: "a");
        var script = new Script([CycleCommand.Say("b", "我知道了，先处理补给。"), CycleCommand.Continue()],
            [CycleCommand.Say("a", "你还好吗？"), CycleCommand.Wait(2)]);
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: script);
        await UntilFact(kernel, f => f.Kind == CycleEventKind.SpeechSent && f.ActorId == "a");
        Assert.Equal(2, kernel.World.GetCharacter("a").Survival!.PendingMask);
        Assert.True(kernel.World.GetCharacter("a").Communication.AttentionPending);
        AssertCycle(activity, kernel.World.GetCharacter("a").Activity!);
        await UntilFact(kernel, f => f.Kind == CycleEventKind.AttentionAcknowledged && f.ActorId == "a");
        Assert.Equal(0, kernel.World.GetCharacter("a").Survival!.PendingMask);
        Assert.False(kernel.World.GetCharacter("a").Communication.AttentionPending);
        AssertCycle(activity, kernel.World.GetCharacter("a").Activity!);
    }

    [Fact]
    public async Task First_same_tick_gift_wakes_new_waiter_and_changes_death_to_survival() {
        CycleWorld Initial() => World(new("a", "Alice", survival: new(Hour(8), 60 * CycleSurvival.Scale,
            8 * CycleSurvival.Scale, 60 * CycleSurvival.Scale, Rate(0), CycleSurvivalRules.WaterRate,
            CycleSurvivalRules.WarmthRate(false, true), warnedMask: 2)), Actor("b", waterItems: 1));
        var helped = CycleEngine.CreateKernel(Memory(Initial()), characterController: new Script(
            [CycleCommand.Wait(2), CycleCommand.Use("item.water"), CycleCommand.Continue()],
            [CycleCommand.Give("a", "item.water", 1), CycleCommand.Wait(2)]));
        await UntilFact(helped, f => f.Kind == CycleEventKind.ItemGiven);
        Assert.True(CycleActions.NeedsDecision(helped.World, "a"));
        Assert.Equal(Hour(10), helped.World.GetCharacter("a").Activity!.Due);
        await UntilFact(helped, f => f.Kind == CycleEventKind.WaitCompleted && f.ActorId == "a");
        Assert.Equal(Hour(10), helped.World.Time);
        Assert.True(helped.World.GetCharacter("a").IsActive);
        Assert.Equal(37 * CycleSurvival.Scale, helped.World.GetCharacter("a").Survival!.WaterUnits);

        var alone = CycleEngine.CreateKernel(Memory(Initial()), characterController: new Script(
            [CycleCommand.Wait(2)], [CycleCommand.Wait(2)]));
        await UntilFact(alone, f => f.Kind == CycleEventKind.SurvivalFailed && f.ActorId == "a");
        Assert.Equal(Hour(9), alone.World.Time);
        Assert.Equal(CycleExpeditionOutcome.Dead, alone.World.GetCharacter("a").Ending!.Outcome);
    }

    [Fact]
    public async Task Send_budget_survives_same_time_context_reopening_then_renews_only_when_model_time_advances() {
        var script = new Script(
            [CycleCommand.Say("b", "one"), CycleCommand.Say("b", "two"), CycleCommand.Say("b", "three"), CycleCommand.Say("b", "four"),
                CycleCommand.Wait(1), CycleCommand.Say("b", "fifth must fail"), CycleCommand.Continue(), CycleCommand.Say("b", "new instant")],
            [CycleCommand.Handoff("notes 1"), CycleCommand.Handoff("notes 2"), CycleCommand.Handoff("notes 3"), CycleCommand.Handoff("notes 4"),
                CycleCommand.Say("a", "one bounded wakeup"), CycleCommand.Wait(2)]);
        var history = Memory(World(Actor("a"), Actor("b")));
        var kernel = CycleEngine.CreateKernel(history, characterController: script);
        await UntilFact(kernel, f => f.Kind == CycleEventKind.OperationRejected);
        Assert.Equal(Hour(8), kernel.World.Time);
        Assert.Equal(2, kernel.World.Business.InteractionStates["a"].CurrentContext!.ContextSequence);
        Assert.Equal(CycleCommunicationRules.SendLimit, kernel.World.GetCharacter("a").Communication.SentCount);
        Assert.Equal(0, CycleCommunicationRules.Remaining(kernel.World.GetCharacter("a"), kernel.World.Time));
        Assert.Equal(Hour(9), kernel.World.GetCharacter("a").Activity!.Due);
        Assert.Empty(kernel.LastCompletion!.Event.Facts[0].ObservedEvents);
        await UntilFact(kernel, f => f.Kind == CycleEventKind.WaitCompleted && f.ActorId == "a");
        Assert.Equal(Hour(9), kernel.World.Time);
        // Querying a fresh time does not mutate the saved budget anchor.
        Assert.Equal(CycleCommunicationRules.SendLimit, CycleCommunicationRules.Remaining(kernel.World.GetCharacter("a"), kernel.World.Time));
        Assert.Equal(Hour(8), kernel.World.GetCharacter("a").Communication.BudgetAt);
        Assert.Equal(CycleCommunicationRules.SendLimit, kernel.World.GetCharacter("a").Communication.SentCount);
        await UntilFact(kernel, f => f.Kind == CycleEventKind.SpeechSent && f.ActorId == "a" && f.At == Hour(9));
        Assert.Equal(Hour(9), kernel.World.GetCharacter("a").Communication.BudgetAt);
        Assert.Equal(1, kernel.World.GetCharacter("a").Communication.SentCount);
        Assert.Equal(5, history.CompletedEvents.SelectMany(e => e.Facts).Count(f => f.Kind == CycleEventKind.SpeechSent && f.ActorId == "a"));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("distant")]
    [InlineData("self")]
    [InlineData("traversing")]
    [InlineData("ended")]
    public async Task Communication_requires_a_visible_active_other_character_at_the_same_actual_place(string targetKind) {
        string target = targetKind == "unknown" ? "hidden-id" : targetKind == "self" ? "a" : "b";
        var aCommands = new List<CycleCommand>();
        var bCommands = new List<CycleCommand>();
        if (targetKind == "traversing") { aCommands.Add(CycleCommand.Wait(1)); bCommands.Add(CycleCommand.Go("trail-3")); }
        if (targetKind == "ended") { aCommands.Add(CycleCommand.Wait(4)); bCommands.Add(CycleCommand.Go("trail-4")); }
        aCommands.Add(CycleCommand.Say(target, "private text"));
        aCommands.Add(CycleCommand.Give(target, "item.water", 1));
        CycleWorld initial = World(Actor("a", waterItems: 2), Actor("b"),
            aPlace: targetKind == "ended" ? "road" : "stream",
            bPlace: targetKind == "ended" ? "ravine" : targetKind == "distant" ? "cabin" : "stream");
        var history = Memory(initial);
        var kernel = CycleEngine.CreateKernel(history, characterController: new Script(aCommands, bCommands));
        await UntilFact(kernel, f => f.Kind == CycleEventKind.OperationRejected);
        CycleCharacter receiver = kernel.World.GetCharacter("b");
        string firstReason = kernel.LastCompletion!.Event.Facts[0].Result!;
        await UntilFact(kernel, f => f.Kind == CycleEventKind.OperationRejected);
        Assert.Equal(firstReason, kernel.LastCompletion!.Event.Facts[0].Result);
        Assert.Same(receiver, kernel.World.GetCharacter("b"));
        Assert.Equal(2, kernel.World.GetCharacter("a").Backpack.Count("item.water"));
        Assert.Equal(0, kernel.World.GetCharacter("b").Backpack.Count("item.water"));
        Assert.Equal(CycleCommunicationRules.SendLimit, CycleCommunicationRules.Remaining(kernel.World.GetCharacter("a"), kernel.World.Time));
        Assert.DoesNotContain(history.CompletedEvents.SelectMany(e => e.Facts), f => f.Kind is CycleEventKind.SpeechSent or CycleEventKind.ItemGiven);
        Assert.DoesNotContain("private text", firstReason, StringComparison.Ordinal);
        Assert.DoesNotContain("Bob", firstReason, StringComparison.Ordinal);
        Assert.DoesNotContain("cabin", firstReason, StringComparison.Ordinal);
        Assert.DoesNotContain("ravine", firstReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Speech_and_gifts_share_a_finite_budget_and_both_actors_can_explicitly_release_time() {
        CycleCommand[] Exchange(string other) => [CycleCommand.Say(other, "first"), CycleCommand.Give(other, "item.water", 1),
            CycleCommand.Say(other, "last"), CycleCommand.Give(other, "item.water", 1), CycleCommand.Continue(), CycleCommand.Wait(1)];
        var history = Memory(World(Actor("a", waterItems: 2), Actor("b", waterItems: 2)));
        var script = new Script(Exchange("b"), Exchange("a"));
        var kernel = CycleEngine.CreateKernel(history, characterController: script);
        await UntilFact(kernel, f => f.Kind == CycleEventKind.WaitCompleted);
        CycleFact[] exchanges = history.CompletedEvents.SelectMany(e => e.Facts)
            .Where(f => f.Kind is CycleEventKind.SpeechSent or CycleEventKind.ItemGiven).ToArray();
        Assert.Equal(2 * CycleCommunicationRules.SendLimit, exchanges.Length);
        Assert.All(exchanges, f => Assert.Equal(Hour(8), f.At));
        Assert.Equal(Hour(9), kernel.World.Time);
        Assert.Equal(4, kernel.World.Characters.Sum(actor => actor.Backpack.Count("item.water")));
        Assert.All(kernel.World.Characters, actor => {
            Assert.Equal(4, actor.Communication.SentCount);
            Assert.Equal(Hour(8), actor.Communication.BudgetAt);
            Assert.False(actor.Communication.AttentionPending);
        });
        Assert.Equal(12, script.Requests.Count);
        Assert.Equal(Enumerable.Range(0, 12).Select(i => i % 2 == 0 ? "a" : "b"), script.Requests);
    }

    [Theory]
    [InlineData("capacity")]
    [InlineData("representation")]
    [InlineData("donor-shortage")]
    [InlineData("nonpositive")]
    public async Task Rejected_gift_neither_transfers_items_nor_charges_budget_or_wakes_recipient(string failure) {
        ItemInventory recipient = failure switch {
            "capacity" => new(new Dictionary<string, int> { ["item.food"] = 1 }, unitCapacity: 1),
            "representation" => new(new Dictionary<string, int> { ["item.water"] = int.MaxValue }),
            _ => new()
        };
        int count = failure == "donor-shortage" ? 3 : failure == "nonpositive" ? 0 : 1;
        CycleWorld initial = World(Actor("a", waterItems: 2), new("b", "Bob", backpack: recipient, survival: Needs()));
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: new Script([CycleCommand.Give("b", "item.water", count)], []));
        await UntilFact(kernel, f => f.Kind == CycleEventKind.OperationRejected);
        Assert.Equal(MaterialSnapshot(initial.GetCharacter("a").Backpack), MaterialSnapshot(kernel.World.GetCharacter("a").Backpack));
        Assert.Same(initial.GetCharacter("b"), kernel.World.GetCharacter("b"));
        Assert.False(kernel.World.GetCharacter("b").Communication.AttentionPending);
        Assert.Equal(CycleCommunicationRules.SendLimit, CycleCommunicationRules.Remaining(kernel.World.GetCharacter("a"), kernel.World.Time));
        Assert.Empty(kernel.LastCompletion!.Event.Facts[0].ObservedEvents);
    }

    [Fact]
    public async Task Multi_item_gift_is_one_atomic_conserving_transfer_and_one_send() {
        CycleWorld initial = World(Actor("a", waterItems: 3), new("b", "Bob",
            backpack: new(new Dictionary<string, int> { ["item.food"] = 1 }, unitCapacity: 3), survival: Needs()));
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: new Script([CycleCommand.Give("b", "item.water", 2)], []));
        await UntilFact(kernel, f => f.Kind == CycleEventKind.ItemGiven);
        CycleFact fact = Assert.Single(kernel.LastCompletion!.Event.Facts);
        Assert.Equal(new[] { "a", "b" }, fact.ChangedCharacters.Select(actor => actor.Id));
        Assert.Equal(1, kernel.World.GetCharacter("a").Backpack.Count("item.water"));
        Assert.Equal(2, kernel.World.GetCharacter("b").Backpack.Count("item.water"));
        Assert.Equal(3, kernel.World.Characters.Sum(actor => actor.Backpack.Count("item.water")));
        Assert.Equal(1, kernel.World.GetCharacter("b").Backpack.Count("item.food"));
        Assert.Equal(CycleCommunicationRules.SendLimit - 1, CycleCommunicationRules.Remaining(kernel.World.GetCharacter("a"), kernel.World.Time));
        Assert.True(kernel.World.GetCharacter("b").Communication.AttentionPending);
        Assert.Equal(Hour(8), kernel.World.Time);
    }

    private static CycleCharacter Actor(string id, int water = 60, int waterItems = 0) => new(id, id == "a" ? "Alice" : "Bob",
        backpack: waterItems == 0 ? new() : new(new Dictionary<string, int> { ["item.water"] = waterItems }), survival: Needs(water));
    private static CycleSurvival Needs(int water = 60) => new(Hour(8), 60 * CycleSurvival.Scale,
        water * CycleSurvival.Scale, 60 * CycleSurvival.Scale, Rate(0), Rate(0), CycleSurvivalRules.WarmthRate(false, true));
    private static CycleWorld World(CycleCharacter a, CycleCharacter b, string aPlace = "stream", string bPlace = "stream", string? lastServed = null) {
        var initial = CycleGameFactory.CreateSurvival(Hour(8), enableNeeds: false, enableWeather: false, characterCount: 2).World;
        var expedition = new CycleExpedition(CycleExpeditionMap.ScenarioId,
            GraphSpatialState.Create(CycleExpeditionMap.Graph, [new(new("a"), new(aPlace)), new(new("b"), new(bPlace))]),
            [new("a", aPlace, Hour(8), initial.Resources.Where(r => CycleExpeditionMap.GetResourcePlace(r.Id) == aPlace)),
                new("b", bPlace, Hour(8), initial.Resources.Where(r => CycleExpeditionMap.GetResourcePlace(r.Id) == bPlace))]);
        return CycleEngine.Rebuild(new(Hour(8), initial.Light, initial.Resources, initial.Script, [a, b], lastServed, expedition), Hour(8));
    }
    private static void AssertCycle(CycleActivity expected, CycleActivity actual) {
        Assert.Equal((expected.Id, expected.StartedAt, expected.Due, expected.DurationTicks, expected.ResourceId,
                expected.TargetCount, expected.CompletedCount, expected.CycleIndex),
            (actual.Id, actual.StartedAt, actual.Due, actual.DurationTicks, actual.ResourceId,
                actual.TargetCount, actual.CompletedCount, actual.CycleIndex));
    }
    private static async Task UntilFact(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel, Func<CycleFact, bool> stop) {
        for (int step = 0; step < 100; step++) {
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(48)));
            CycleWorld rebuilt = CycleEngine.Rebuild(kernel.World.Business, kernel.World.Time);
            Assert.Equal(rebuilt.Forecasts.ToString(), kernel.World.Forecasts.ToString());
            Assert.Equal(rebuilt.Spatial!.Contacts.ToString(), kernel.World.Spatial!.Contacts.ToString());
            if (kernel.LastCompletion!.Event.Facts.Any(stop)) { return; }
        }
        Assert.Fail("Expected communication boundary was not reached.");
    }
    private sealed class Script(IEnumerable<CycleCommand> a, IEnumerable<CycleCommand> b) : ICycleCharacterController {
        private readonly Dictionary<string, Queue<CycleCommand>> _commands = new(StringComparer.Ordinal) { ["a"] = new(a), ["b"] = new(b) };
        public List<string> Requests { get; } = [];
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) {
            string actor = context.Observation.Actor.Id;
            Requests.Add(actor);
            if (!_commands[actor].TryDequeue(out CycleCommand? command)) { throw new InvalidOperationException($"Unexpected decision for {actor} at {context.Observation.Time}."); }
            return ValueTask.FromResult(Operation(command, Requests.Count));
        }
    }
}
