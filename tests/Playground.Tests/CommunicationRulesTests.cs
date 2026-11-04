using DramaBoard.Inventory;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

/// <summary>Social decisions pass through the real kernel, including openings, service rotation and rejection.</summary>
public sealed class CommunicationRulesTests {
    [Fact]
    public async Task Gathering_receives_speech_and_gift_then_reply_supply_and_continue_preserve_the_cycle() {
        PlaygroundWorld initial = World(Actor("a", water: 40), Actor("b", waterItems: 2));
        var script = new Script(
            [PlaygroundCommand.Gather("berries", 2), PlaygroundCommand.Say("b", "收到，我会继续采集。"), PlaygroundCommand.Use("item.water"), PlaygroundCommand.Continue()],
            [PlaygroundCommand.Say("a", "先给你饮水。"), PlaygroundCommand.Give("a", "item.water", 1), PlaygroundCommand.Wait(2)]);
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: script);
        await UntilFact(kernel, f => f.Kind == PlaygroundEventKind.GatherStarted);
        PlaygroundActivity activity = kernel.World.GetCharacter("a").Activity!;
        long firstContext = kernel.World.Business.InteractionStates["a"].CurrentContext!.ContextSequence;

        await UntilFact(kernel, f => f.Kind == PlaygroundEventKind.ItemGiven);
        Assert.True(kernel.World.GetCharacter("a").Communication.AttentionPending);
        Assert.Equal(1, kernel.World.GetCharacter("a").Backpack.Count("item.water"));
        Assert.Equal(1, kernel.World.GetCharacter("b").Backpack.Count("item.water"));
        AssertCycle(activity, kernel.World.GetCharacter("a").Activity!);
        await UntilFact(kernel, f => f.Kind == PlaygroundEventKind.AttentionAcknowledged && f.ActorId == "a");

        Assert.Equal(Hour(8), kernel.World.Time);
        AssertCycle(activity, kernel.World.GetCharacter("a").Activity!);
        Assert.False(kernel.World.GetCharacter("a").Communication.AttentionPending);
        Assert.Equal(0, kernel.World.GetCharacter("a").Backpack.Count("item.water"));
        Assert.Equal(85 * PlaygroundSurvival.Scale, kernel.World.GetCharacter("a").Survival!.WaterUnits);
        Assert.Equal(firstContext + 1, kernel.World.Business.InteractionStates["a"].CurrentContext!.ContextSequence);
        Assert.Equal(new[] { "a", "b", "a", "b", "a", "b", "a" }, script.Requests);
        Assert.Equal(6, kernel.World.GetResource("berries").Stock);
    }

    [Fact]
    public async Task Reply_does_not_acknowledge_coexisting_body_warning_or_reset_gathering_deadline() {
        var activity = new PlaygroundActivity(PlaygroundActivityKind.Gather, 1, Hour(8), PlaygroundTime.Hour, Hour(9), "berries", 2);
        var body = new PlaygroundSurvival(Hour(8), 60 * PlaygroundSurvival.Scale, 20 * PlaygroundSurvival.Scale, 60 * PlaygroundSurvival.Scale,
            Rate(0), Rate(0), PlaygroundSurvivalRules.WarmthRate(false, true), warnedMask: 2, pendingMask: 2, pendingSince: Hour(8));
        var a = new PlaygroundCharacter("a", "Alice", activity: activity, nextActivityId: 2, survival: body);
        PlaygroundWorld initial = World(a, Actor("b"), lastServed: "a");
        var script = new Script([PlaygroundCommand.Say("b", "我知道了，先处理补给。"), PlaygroundCommand.Continue()],
            [PlaygroundCommand.Say("a", "你还好吗？"), PlaygroundCommand.Wait(2)]);
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: script);
        await UntilFact(kernel, f => f.Kind == PlaygroundEventKind.SpeechSent && f.ActorId == "a");
        Assert.Equal(2, kernel.World.GetCharacter("a").Survival!.PendingMask);
        Assert.True(kernel.World.GetCharacter("a").Communication.AttentionPending);
        AssertCycle(activity, kernel.World.GetCharacter("a").Activity!);
        await UntilFact(kernel, f => f.Kind == PlaygroundEventKind.AttentionAcknowledged && f.ActorId == "a");
        Assert.Equal(0, kernel.World.GetCharacter("a").Survival!.PendingMask);
        Assert.False(kernel.World.GetCharacter("a").Communication.AttentionPending);
        AssertCycle(activity, kernel.World.GetCharacter("a").Activity!);
    }

    [Fact]
    public async Task First_same_tick_gift_wakes_new_waiter_and_changes_death_to_survival() {
        PlaygroundWorld Initial() => World(new("a", "Alice", survival: new(Hour(8), 60 * PlaygroundSurvival.Scale,
            8 * PlaygroundSurvival.Scale, 60 * PlaygroundSurvival.Scale, Rate(0), PlaygroundSurvivalRules.WaterRate,
            PlaygroundSurvivalRules.WarmthRate(false, true), warnedMask: 2)), Actor("b", waterItems: 1));
        var helped = PlaygroundEngine.CreateKernel(Memory(Initial()), characterController: new Script(
            [PlaygroundCommand.Wait(2), PlaygroundCommand.Use("item.water"), PlaygroundCommand.Continue()],
            [PlaygroundCommand.Give("a", "item.water", 1), PlaygroundCommand.Wait(2)]));
        await UntilFact(helped, f => f.Kind == PlaygroundEventKind.ItemGiven);
        Assert.True(PlaygroundActions.NeedsDecision(helped.World, "a"));
        Assert.Equal(Hour(10), helped.World.GetCharacter("a").Activity!.Due);
        await UntilFact(helped, f => f.Kind == PlaygroundEventKind.WaitCompleted && f.ActorId == "a");
        Assert.Equal(Hour(10), helped.World.Time);
        Assert.True(helped.World.GetCharacter("a").IsActive);
        Assert.Equal(37 * PlaygroundSurvival.Scale, helped.World.GetCharacter("a").Survival!.WaterUnits);

        var alone = PlaygroundEngine.CreateKernel(Memory(Initial()), characterController: new Script(
            [PlaygroundCommand.Wait(2)], [PlaygroundCommand.Wait(2)]));
        await UntilFact(alone, f => f.Kind == PlaygroundEventKind.SurvivalFailed && f.ActorId == "a");
        Assert.Equal(Hour(9), alone.World.Time);
        Assert.Equal(PlaygroundExpeditionOutcome.Dead, alone.World.GetCharacter("a").Ending!.Outcome);
    }

    [Fact]
    public async Task Send_budget_survives_same_time_context_reopening_then_renews_only_when_model_time_advances() {
        var script = new Script(
            [PlaygroundCommand.Say("b", "one"), PlaygroundCommand.Say("b", "two"), PlaygroundCommand.Say("b", "three"), PlaygroundCommand.Say("b", "four"),
                PlaygroundCommand.Wait(1), PlaygroundCommand.Say("b", "fifth must fail"), PlaygroundCommand.Continue(), PlaygroundCommand.Say("b", "new instant")],
            [PlaygroundCommand.Handoff("notes 1"), PlaygroundCommand.Handoff("notes 2"), PlaygroundCommand.Handoff("notes 3"), PlaygroundCommand.Handoff("notes 4"),
                PlaygroundCommand.Say("a", "one bounded wakeup"), PlaygroundCommand.Wait(2)]);
        var history = Memory(World(Actor("a"), Actor("b")));
        var kernel = PlaygroundEngine.CreateKernel(history, characterController: script);
        await UntilFact(kernel, f => f.Kind == PlaygroundEventKind.OperationRejected);
        Assert.Equal(Hour(8), kernel.World.Time);
        Assert.Equal(2, kernel.World.Business.InteractionStates["a"].CurrentContext!.ContextSequence);
        Assert.Equal(PlaygroundCommunicationRules.SendLimit, kernel.World.GetCharacter("a").Communication.SentCount);
        Assert.Equal(0, PlaygroundCommunicationRules.Remaining(kernel.World.GetCharacter("a"), kernel.World.Time));
        Assert.Equal(Hour(9), kernel.World.GetCharacter("a").Activity!.Due);
        Assert.Empty(kernel.LastCompletion!.Event.Facts[0].ObservedEvents);
        await UntilFact(kernel, f => f.Kind == PlaygroundEventKind.WaitCompleted && f.ActorId == "a");
        Assert.Equal(Hour(9), kernel.World.Time);
        // Querying a fresh time does not mutate the saved budget anchor.
        Assert.Equal(PlaygroundCommunicationRules.SendLimit, PlaygroundCommunicationRules.Remaining(kernel.World.GetCharacter("a"), kernel.World.Time));
        Assert.Equal(Hour(8), kernel.World.GetCharacter("a").Communication.BudgetAt);
        Assert.Equal(PlaygroundCommunicationRules.SendLimit, kernel.World.GetCharacter("a").Communication.SentCount);
        await UntilFact(kernel, f => f.Kind == PlaygroundEventKind.SpeechSent && f.ActorId == "a" && f.At == Hour(9));
        Assert.Equal(Hour(9), kernel.World.GetCharacter("a").Communication.BudgetAt);
        Assert.Equal(1, kernel.World.GetCharacter("a").Communication.SentCount);
        Assert.Equal(5, history.CompletedEvents.SelectMany(e => e.Facts).Count(f => f.Kind == PlaygroundEventKind.SpeechSent && f.ActorId == "a"));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("distant")]
    [InlineData("self")]
    [InlineData("traversing")]
    [InlineData("ended")]
    public async Task Communication_requires_a_visible_active_other_character_at_the_same_actual_place(string targetKind) {
        string target = targetKind == "unknown" ? "hidden-id" : targetKind == "self" ? "a" : "b";
        var aCommands = new List<PlaygroundCommand>();
        var bCommands = new List<PlaygroundCommand>();
        if (targetKind == "traversing") { aCommands.Add(PlaygroundCommand.Wait(1)); bCommands.Add(PlaygroundCommand.Go("e-h5")); }
        if (targetKind == "ended") { aCommands.Add(PlaygroundCommand.Wait(4)); }
        aCommands.Add(PlaygroundCommand.Say(target, "private text"));
        aCommands.Add(PlaygroundCommand.Give(target, "item.water", 1));
        PlaygroundWorld initial = World(Actor("a", waterItems: 2), Actor("b", water: targetKind == "ended" ? 0 : 60),
            aPlace: targetKind == "ended" ? "road" : "stream",
            bPlace: targetKind == "ended" ? "road" : targetKind == "distant" ? "cabin" : "stream");
        var history = Memory(initial);
        var kernel = PlaygroundEngine.CreateKernel(history, characterController: new Script(aCommands, bCommands));
        await UntilFact(kernel, f => f.Kind == PlaygroundEventKind.OperationRejected);
        PlaygroundCharacter receiver = kernel.World.GetCharacter("b");
        string firstReason = kernel.LastCompletion!.Event.Facts[0].Result!;
        await UntilFact(kernel, f => f.Kind == PlaygroundEventKind.OperationRejected);
        Assert.Equal(firstReason, kernel.LastCompletion!.Event.Facts[0].Result);
        Assert.Same(receiver, kernel.World.GetCharacter("b"));
        Assert.Equal(2, kernel.World.GetCharacter("a").Backpack.Count("item.water"));
        Assert.Equal(0, kernel.World.GetCharacter("b").Backpack.Count("item.water"));
        Assert.Equal(PlaygroundCommunicationRules.SendLimit, PlaygroundCommunicationRules.Remaining(kernel.World.GetCharacter("a"), kernel.World.Time));
        Assert.DoesNotContain(history.CompletedEvents.SelectMany(e => e.Facts), f => f.Kind is PlaygroundEventKind.SpeechSent or PlaygroundEventKind.ItemGiven);
        Assert.DoesNotContain("private text", firstReason, StringComparison.Ordinal);
        Assert.DoesNotContain("Bob", firstReason, StringComparison.Ordinal);
        Assert.DoesNotContain("cabin", firstReason, StringComparison.Ordinal);
        Assert.DoesNotContain("ravine", firstReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Speech_and_gifts_share_a_finite_budget_and_both_actors_can_explicitly_release_time() {
        PlaygroundCommand[] Exchange(string other) => [PlaygroundCommand.Say(other, "first"), PlaygroundCommand.Give(other, "item.water", 1),
            PlaygroundCommand.Say(other, "last"), PlaygroundCommand.Give(other, "item.water", 1), PlaygroundCommand.Continue(), PlaygroundCommand.Wait(1)];
        var history = Memory(World(Actor("a", waterItems: 2), Actor("b", waterItems: 2)));
        var script = new Script(Exchange("b"), Exchange("a"));
        var kernel = PlaygroundEngine.CreateKernel(history, characterController: script);
        await UntilFact(kernel, f => f.Kind == PlaygroundEventKind.WaitCompleted);
        PlaygroundFact[] exchanges = history.CompletedEvents.SelectMany(e => e.Facts)
            .Where(f => f.Kind is PlaygroundEventKind.SpeechSent or PlaygroundEventKind.ItemGiven).ToArray();
        Assert.Equal(2 * PlaygroundCommunicationRules.SendLimit, exchanges.Length);
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
        PlaygroundWorld initial = World(Actor("a", waterItems: 2), new("b", "Bob", backpack: recipient, survival: Needs()));
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: new Script([PlaygroundCommand.Give("b", "item.water", count)], []));
        await UntilFact(kernel, f => f.Kind == PlaygroundEventKind.OperationRejected);
        Assert.Equal(MaterialSnapshot(initial.GetCharacter("a").Backpack), MaterialSnapshot(kernel.World.GetCharacter("a").Backpack));
        Assert.Same(initial.GetCharacter("b"), kernel.World.GetCharacter("b"));
        Assert.False(kernel.World.GetCharacter("b").Communication.AttentionPending);
        Assert.Equal(PlaygroundCommunicationRules.SendLimit, PlaygroundCommunicationRules.Remaining(kernel.World.GetCharacter("a"), kernel.World.Time));
        Assert.Empty(kernel.LastCompletion!.Event.Facts[0].ObservedEvents);
    }

    [Fact]
    public async Task Multi_item_gift_is_one_atomic_conserving_transfer_and_one_send() {
        PlaygroundWorld initial = World(Actor("a", waterItems: 3), new("b", "Bob",
            backpack: new(new Dictionary<string, int> { ["item.food"] = 1 }, unitCapacity: 3), survival: Needs()));
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: new Script([PlaygroundCommand.Give("b", "item.water", 2)], []));
        await UntilFact(kernel, f => f.Kind == PlaygroundEventKind.ItemGiven);
        PlaygroundFact fact = Assert.Single(kernel.LastCompletion!.Event.Facts);
        Assert.Equal(new[] { "a", "b" }, fact.ChangedCharacters.Select(actor => actor.Id));
        Assert.Equal(1, kernel.World.GetCharacter("a").Backpack.Count("item.water"));
        Assert.Equal(2, kernel.World.GetCharacter("b").Backpack.Count("item.water"));
        Assert.Equal(3, kernel.World.Characters.Sum(actor => actor.Backpack.Count("item.water")));
        Assert.Equal(1, kernel.World.GetCharacter("b").Backpack.Count("item.food"));
        Assert.Equal(PlaygroundCommunicationRules.SendLimit - 1, PlaygroundCommunicationRules.Remaining(kernel.World.GetCharacter("a"), kernel.World.Time));
        Assert.True(kernel.World.GetCharacter("b").Communication.AttentionPending);
        Assert.Equal(Hour(8), kernel.World.Time);
    }

    private static PlaygroundCharacter Actor(string id, int water = 60, int waterItems = 0) => new(id, id == "a" ? "Alice" : "Bob",
        backpack: waterItems == 0 ? new() : new(new Dictionary<string, int> { ["item.water"] = waterItems }), survival: Needs(water));
    private static PlaygroundSurvival Needs(int water = 60) => new(Hour(8), 60 * PlaygroundSurvival.Scale,
        water * PlaygroundSurvival.Scale, 60 * PlaygroundSurvival.Scale, Rate(0), Rate(0), PlaygroundSurvivalRules.WarmthRate(false, true));
    private static PlaygroundWorld World(PlaygroundCharacter a, PlaygroundCharacter b, string aPlace = "stream", string bPlace = "stream", string? lastServed = null) {
        var initial = PlaygroundGameFactory.CreateSurvival(Hour(8), enableNeeds: false, enableWeather: false, characterCount: 2).World;
        var expedition = new PlaygroundExpedition(PlaygroundExpeditionMap.ScenarioId,
            GraphSpatialState.Create(PlaygroundExpeditionMap.Graph, [new(new("a"), new(aPlace)), new(new("b"), new(bPlace))]),
            [new("a", aPlace, Hour(8), initial.Resources.Where(r => PlaygroundExpeditionMap.GetResourcePlace(r.Id) == aPlace)),
                new("b", bPlace, Hour(8), initial.Resources.Where(r => PlaygroundExpeditionMap.GetResourcePlace(r.Id) == bPlace))]);
        return PlaygroundEngine.Rebuild(new(Hour(8), initial.Light, initial.Resources, initial.Script, [a, b], lastServed, expedition), Hour(8));
    }
    private static void AssertCycle(PlaygroundActivity expected, PlaygroundActivity actual) {
        Assert.Equal((expected.Id, expected.StartedAt, expected.Due, expected.DurationTicks, expected.ResourceId,
                expected.TargetCount, expected.CompletedCount, expected.CycleIndex),
            (actual.Id, actual.StartedAt, actual.Due, actual.DurationTicks, actual.ResourceId,
                actual.TargetCount, actual.CompletedCount, actual.CycleIndex));
    }
    private static async Task UntilFact(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel, Func<PlaygroundFact, bool> stop) {
        for (int step = 0; step < 100; step++) {
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(48)));
            PlaygroundWorld rebuilt = PlaygroundEngine.Rebuild(kernel.World.Business, kernel.World.Time);
            AssertForecasts(rebuilt.Forecasts, kernel.World.Forecasts);
            Assert.Equal(rebuilt.Spatial!.Contacts.ToString(), kernel.World.Spatial!.Contacts.ToString());
            if (kernel.LastCompletion!.Event.Facts.Any(stop)) { return; }
        }
        Assert.Fail("Expected communication boundary was not reached.");
    }
    private sealed class Script(IEnumerable<PlaygroundCommand> a, IEnumerable<PlaygroundCommand> b) : IPlaygroundCharacterController {
        private readonly Dictionary<string, Queue<PlaygroundCommand>> _commands = new(StringComparer.Ordinal) { ["a"] = new(a), ["b"] = new(b) };
        public List<string> Requests { get; } = [];
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) {
            string actor = context.Observation.Actor.Id;
            Requests.Add(actor);
            if (!_commands[actor].TryDequeue(out PlaygroundCommand? command)) { throw new InvalidOperationException($"Unexpected decision for {actor} at {context.Observation.Time}."); }
            return ValueTask.FromResult(Operation(command, Requests.Count));
        }
    }
}
