using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class ShoutRulesTests {
    [Theory]
    [InlineData("camp", "ridge", "cabin", "a")]
    [InlineData("camp", "stream", "ridge", "a,b")]
    [InlineData("camp", "camp", "stream", "a,b,c")]
    [InlineData("stream", "camp", "ravine", "a,b")]
    [InlineData("ridge", "cabin", "camp", "a,b")]
    [InlineData("cabin", "ridge", "stream", "a,b")]
    public async Task Shout_uses_only_explicit_sound_relations_and_charges_once(string a, string b, string c, string expected) {
        CycleWorld initial = FollowingRulesTests.World(places: [a, b, c]);
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: Commands(CycleCommand.Shout("集合")));
        await StepWorldEvent(kernel);
        CycleFact fact = Assert.Single(kernel.LastCompletion!.Event.Facts);
        Assert.Equal(CycleEventKind.SpeechShouted, fact.Kind);
        Assert.Equal(expected.Split(','), fact.ChangedCharacters.Select(actor => actor.Id));
        Assert.Equal("已大喊。", fact.Result);
        Assert.Null(fact.TargetActorId);
        Assert.Empty(fact.Visits);
        Assert.Empty(fact.SpatialFacts);
        Assert.Equal(1, kernel.World.GetCharacter("a").Communication.SentCount);
        Assert.False(kernel.World.GetCharacter("a").Communication.AttentionPending);
        foreach (CycleCharacter actor in kernel.World.Characters.Where(actor => actor.Id != "a")) {
            Assert.Equal(expected.Split(',').Contains(actor.Id), actor.Communication.AttentionPending);
            Assert.Equal(0, actor.Communication.SentCount);
        }
        Assert.Equal(initial.Time, kernel.World.Time);
        Assert.Equal(MaterialSnapshot(initial.Expedition!.Visits), MaterialSnapshot(kernel.World.Expedition!.Visits));
    }

    [Fact]
    public async Task Hearing_and_sending_preserve_activity_following_attention_and_custom_body_rate_causes() {
        var warmth = new RateBreakdown([new("fixture.sun", "日照", 18), new("fixture.exposure", "暴露", -12)]);
        CycleSurvival Body() => new(Hour(8), 60 * CycleSurvival.Scale, 60 * CycleSurvival.Scale, 60 * CycleSurvival.Scale,
            Rate(0), Rate(0), warmth);
        CycleWorld initial = FollowingRulesTests.World(leaders: [null, "a", null], waiting: [2, 0, 2],
            attention: [true, false, false], bodies: [Body(), Body(), Body()]);
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: Commands(CycleCommand.Shout("先做完再集合")));
        await StepWorldEvent(kernel);
        Assert.Equal(CycleEventKind.SpeechShouted, Assert.Single(kernel.LastCompletion!.Event.Facts).Kind);
        Assert.Equal("a", kernel.World.GetCharacter("b").FollowingLeaderId);
        Assert.All(kernel.World.Characters, actor => {
            Assert.Same(initial.GetCharacter(actor.Id).Activity, actor.Activity);
            Assert.Same(warmth, actor.Survival!.WarmthRate);
            Assert.True(actor.Communication.AttentionPending);
            Assert.True(CycleActions.NeedsDecision(kernel.World, actor.Id));
        });
        Assert.Equal(Hour(10), kernel.World.GetCharacter("a").Activity!.Due);
        Assert.Equal(Hour(10), kernel.World.GetCharacter("c").Activity!.Due);
    }

    [Fact]
    public async Task Traversing_listener_is_excluded_even_when_arrival_is_due_at_the_query_instant() {
        CycleWorld initial = FollowingRulesTests.World(places: ["camp", "stream", "ridge"]);
        CycleWorld travelling = StartTraversal(initial, "b", "trail-1");
        Assert.Equal(new[] { "a" }, new SpatialQueries(CycleExpeditionMap.Graph)
            .GetEntitiesInOneHop(travelling.Expedition!.Spatial, new("camp"), PlaceRelationCapabilities.Sound).Select(id => id.Value));
        Assert.Empty(CycleCommunicationRules.GetShoutRecipients(travelling.Business, "a"));
        Assert.NotNull(CycleActions.Check(travelling, "b", CycleCommand.Shout("途中")));
        // A location remains Traversing until the arrival fact applies, regardless of query time.
        Assert.Equal(Hour(9), Assert.IsType<TraversingLocation>(CycleActions.Location(travelling, "b")).ArrivalDue);
        CycleWorld dueBeforeArrival = CycleEngine.Rebuild(travelling.Business.Replace(Hour(9)), Hour(9));
        Assert.Empty(CycleCommunicationRules.GetShoutRecipients(dueBeforeArrival.Business, "a"));
        var kernel = CycleEngine.CreateKernel(Memory(travelling), characterController: Commands(CycleCommand.Shout("现在集合")));
        await StepWorldEvent(kernel);
        Assert.Single(Assert.Single(kernel.LastCompletion!.Event.Facts).ChangedCharacters);
        Assert.False(kernel.World.GetCharacter("b").Communication.AttentionPending);
    }

    [Fact]
    public async Task Natural_arrival_at_the_same_time_is_committed_before_shout_selects_listeners() {
        CycleWorld initial = FollowingRulesTests.World(places: ["camp", "stream", "ridge"], waiting: [1, 0, 2]);
        initial = StartTraversal(initial, "b", "trail-1");
        var history = Memory(initial);
        var kernel = CycleEngine.CreateKernel(history, characterController: Commands(CycleCommand.Shout("到了就集合")));
        for (int step = 0; step < 15; step++) {
            await StepWorldEvent(kernel);
            if (kernel.LastCompletion!.Event.Facts.Any(fact => fact.Kind == CycleEventKind.SpeechShouted)) { break; }
        }
        CycleFact shout = Assert.Single(kernel.LastCompletion!.Event.Facts);
        Assert.Equal(CycleEventKind.SpeechShouted, shout.Kind);
        Assert.Equal(Hour(9), shout.At);
        Assert.Equal(new[] { "a", "b" }, shout.ChangedCharacters.Select(actor => actor.Id));
        CycleFact[] facts = history.CompletedEvents.SelectMany(occurrence => occurrence.Facts).ToArray();
        Assert.True(Array.FindIndex(facts, fact => fact.Kind == CycleEventKind.MoveArrived && fact.ActorId == "b") <
            Array.FindIndex(facts, fact => fact.Kind == CycleEventKind.SpeechShouted));
        Assert.Equal("camp", CycleActions.CurrentPlace(kernel.World, "b"));
    }

    [Fact]
    public async Task Retired_character_is_not_a_listener() {
        var fatal = new CycleSurvival(Hour(8), 100 * CycleSurvival.Scale, 0, 100 * CycleSurvival.Scale,
            Rate(0), CycleSurvivalRules.WaterRate, CycleSurvivalRules.WarmthRate(false, true), warnedMask: 2);
        CycleWorld initial = FollowingRulesTests.World(places: ["camp", "camp", "ridge"], waiting: [3, 4, 4], bodies: [null, fatal, null]);
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: Commands(CycleCommand.Shout("有人吗")));
        for (int step = 0; step < 20; step++) {
            await StepWorldEvent(kernel);
            if (kernel.LastCompletion!.Event.Facts.Any(fact => fact.Kind == CycleEventKind.SpeechShouted)) { break; }
        }
        CycleFact shout = Assert.Single(kernel.LastCompletion!.Event.Facts);
        Assert.Equal(CycleEventKind.SpeechShouted, shout.Kind);
        Assert.Single(shout.ChangedCharacters);
        Assert.Equal(CycleExpeditionOutcome.Dead, kernel.World.GetCharacter("b").Ending!.Outcome);
        Assert.Equal(CharacterContextStatus.Terminal, kernel.World.Business.InteractionStates["b"].CurrentContext!.Status);
        Assert.False(kernel.World.GetCharacter("b").Communication.AttentionPending);
        Assert.DoesNotContain(shout.ObservedEvents, entry => entry.ObserverId == "b");
    }

    [Fact]
    public void Say_give_and_shout_share_budget_and_remote_say_or_give_remain_invalid() {
        CycleWorld before = FollowingRulesTests.World();
        CycleCharacter stocked = before.GetCharacter("a").Change(null, backpack: new(new Dictionary<string, int> { ["item.water"] = 1 }));
        before = OpenActor(CycleEngine.Rebuild(before.Business.Replace(before.Time, changedCharacters: [stocked]), before.Time), "a");
        foreach (CycleCommand command in new[] { CycleCommand.Say("b", "一"), CycleCommand.Shout("二"), CycleCommand.Give("b", "item.water", 1), CycleCommand.Shout("四") }) {
            CycleFact fact = Plan(before, command);
            before = Apply(before, fact);
        }
        Assert.Equal(4, before.GetCharacter("a").Communication.SentCount);
        Assert.NotNull(CycleActions.Check(before, "a", CycleCommand.Shout("五")));
        Assert.NotNull(CycleActions.Check(before, "a", CycleCommand.Say("b", "五")));
        Assert.NotNull(CycleActions.Check(before, "a", CycleCommand.Give("b", "item.water", 1)));
        CycleWorld remote = FollowingRulesTests.World(places: ["camp", "stream", "ridge"]);
        Assert.Null(CycleActions.Check(remote, "a", CycleCommand.Shout("跨地")));
        Assert.NotNull(CycleActions.Check(remote, "a", CycleCommand.Say("b", "跨地")));
        Assert.NotNull(CycleActions.Check(remote, "a", CycleCommand.Give("b", "item.water", 1)));
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("experience")]
    [InlineData("open-item")]
    public async Task Any_unrepresentable_listener_rejects_the_whole_shout_without_partial_delivery(string limit) {
        CycleWorld initial = FollowingRulesTests.World();
        if (limit == "generation") {
            initial = CycleEngine.Rebuild(initial.Business.Replace(initial.Time,
                changedCharacters: [new CycleCharacter("b", "b", decisionGeneration: long.MaxValue - 2)]), initial.Time);
        } else {
            if (limit == "open-item") { initial = OpenActor(initial, "b"); }
            CharacterInteractionState state = initial.Business.InteractionStates["b"];
            CharacterContext? context = state.CurrentContext;
            if (context is not null) {
                context = new(context.ContextSequence, context.Status, context.Opening, context.Items, long.MaxValue, context.LastOperationSequence);
            }
            var replacement = new CharacterInteractionState("b", context, state.PendingAdvance, state.UndeliveredExperiences,
                state.DraftHandoff, limit == "experience" ? long.MaxValue : state.NextExperienceSequence, state.Goals);
            initial = CycleEngine.Rebuild(initial.Business.Replace(initial.Time, interactionChanges: [replacement]), initial.Time);
        }
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: Commands(CycleCommand.Shout("集合")));
        await StepWorldEvent(kernel);
        CycleFact fact = Assert.Single(kernel.LastCompletion!.Event.Facts);
        Assert.Equal(CycleEventKind.OperationRejected, fact.Kind);
        Assert.Equal("当前无法完成此次大喊。", fact.Result);
        Assert.Empty(fact.ObservedEvents);
        Assert.Equal(0, kernel.World.GetCharacter("a").Communication.SentCount);
        Assert.Same(initial.GetCharacter("b"), kernel.World.GetCharacter("b"));
        Assert.Same(initial.GetCharacter("c"), kernel.World.GetCharacter("c"));
    }

    [Fact]
    public async Task Sender_with_exhausted_experience_counter_records_rejection_even_without_listeners() {
        CycleWorld initial = OpenActor(FollowingRulesTests.World(places: ["camp", "ridge", "cabin"]), "a");
        CharacterInteractionState state = initial.Business.InteractionStates["a"];
        var exhausted = new CharacterInteractionState("a", state.CurrentContext, state.PendingAdvance, state.UndeliveredExperiences,
            state.DraftHandoff, long.MaxValue, state.Goals);
        initial = CycleEngine.Rebuild(initial.Business.Replace(initial.Time, interactionChanges: [exhausted]), initial.Time);
        Assert.Empty(CycleCommunicationRules.GetShoutRecipients(initial.Business, "a"));
        Assert.NotNull(CycleActions.Check(initial, "a", CycleCommand.Shout("有人吗")));
        var history = Memory(initial);
        var kernel = CycleEngine.CreateKernel(history, characterController: Commands(CycleCommand.Shout("有人吗")));
        await StepWorldEvent(kernel);
        CycleFact fact = Assert.Single(kernel.LastCompletion!.Event.Facts);
        Assert.Equal(CycleEventKind.OperationRejected, fact.Kind);
        Assert.Equal("当前无法完成此次大喊。", fact.Result);
        Assert.Empty(fact.ObservedEvents);
        Assert.Equal(0, kernel.World.GetCharacter("a").Communication.SentCount);
        Assert.Equal(long.MaxValue, kernel.World.Business.InteractionStates["a"].NextExperienceSequence);
        Assert.Single(history.CompletedEvents);
    }

    [Fact]
    public void Fold_rejects_missing_duplicate_extra_or_modified_participants_and_unrelated_effects() {
        CycleWorld before = OpenActor(FollowingRulesTests.World(places: ["camp", "stream", "ridge"], waiting: [0, 2, 0]), "a");
        CycleFact fact = Plan(before, CycleCommand.Shout("原文"));
        _ = Apply(before, fact);
        CycleCharacter sender = fact.ChangedCharacters.Single(actor => actor.Id == "a");
        CycleCharacter receiver = fact.ChangedCharacters.Single(actor => actor.Id == "b");
        void Reject(CycleFact forged) => Assert.Throws<InvalidOperationException>(() => Apply(before, forged));
        Reject(fact.WithCharacters([sender]));
        Reject(fact.WithCharacters([sender, receiver, receiver]));
        Reject(fact.WithCharacters([sender, receiver, before.GetCharacter("c").Change(null, communication: new(before.Time, 0, true))]));
        Reject(fact.WithCharacters([Copy(sender, communication: new(before.Time, 2, false)), receiver]));
        Reject(fact.WithCharacters([sender, Copy(receiver, cancel: true)]));
        Reject(fact.WithCharacters([sender, Copy(receiver, addItem: true)]));
        Reject(fact.WithCharacters([sender, Copy(receiver, following: "a")]));
        Reject(fact.WithVisits([new("a", "stream", before.Time, [])]));
        // A directed command must still have exactly two replacements.
        CycleWorld local = OpenActor(FollowingRulesTests.World(), "a");
        CycleFact directed = Plan(local, CycleCommand.Say("b", "私聊"));
        Assert.Throws<InvalidOperationException>(() => Apply(local, directed.WithCharacters(directed.ChangedCharacters.Append(local.GetCharacter("c").Change(null)))));
    }

    private static CycleCharacter Copy(CycleCharacter actor, CycleCommunicationState? communication = null,
        bool cancel = false, bool addItem = false, string? following = null) => new(actor.Id, actor.Name, actor.SkillLevel,
            addItem ? actor.Backpack.Add("item.water", 1) : actor.Backpack, cancel ? null : actor.Activity, actor.Notices,
            actor.DecisionGeneration, actor.LastResult, actor.NextActivityId, actor.Survival, actor.Ending,
            communication ?? actor.Communication, following ?? actor.FollowingLeaderId);
    private static CycleWorld OpenActor(CycleWorld world, string actor) => Apply(world, CycleInteractionRules.OpenContext(world, actor));
    private static CycleFact Plan(CycleWorld world, CycleCommand command) {
        Assert.Null(CycleActions.Check(world, "a", command));
        var operation = Operation(command, checked((int)(world.Business.InteractionStates["a"].CurrentContext!.LastOperationSequence + 1)));
        CycleFact fact = CyclePlanning.Decision(world, "a", command).WithOperation(operation);
        return CycleInteractionRules.Enrich(world, CyclePlanning.Observe(world, CycleSurvivalRules.Enrich(world, fact)), operation);
    }
    private static CycleWorld Apply(CycleWorld world, CycleFact fact) => CycleEngine.Fold(world, new(world.Time, 0), fact);
    private static CycleWorld StartTraversal(CycleWorld world, string actor, string passage) {
        var plan = Assert.IsType<SpatialPlanAccepted>(CycleExpeditionMap.Planner.TryStartTraversal(world.Expedition!.Spatial,
            new(actor), new(passage), 1, world.Time));
        GraphSpatialState spatial = plan.Facts.Aggregate(world.Expedition.Spatial,
            (state, fact) => CycleExpeditionMap.Reducer.Apply(state, new(world.Time, 0), fact));
        return CycleEngine.Rebuild(world.Business.Replace(world.Time, expedition: world.Expedition.Change(spatial, [])), world.Time);
    }
}
