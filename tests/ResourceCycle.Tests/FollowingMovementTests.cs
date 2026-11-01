using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class FollowingMovementTests {
    [Fact]
    public async Task Three_members_depart_atomically_and_all_arrivals_settle_before_another_decision() {
        CycleWorld initial = FollowingRulesTests.World(leaders: [null, "a", "a"]);
        // Movement generations are local histories, not a shared party identity.
        initial = WithGenerations(initial, [2, 9, 0]);
        var controller = Commands(CycleCommand.Go("trail-1"));
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: controller);
        await UntilFact(kernel, CycleEventKind.MoveStarted);
        CycleFact move = Assert.Single(kernel.LastCompletion!.Event.Facts);
        Assert.Equal(new[] { "a", "b", "c" }, move.SpatialFacts.OfType<TraversalStartedFact>().Select(f => f.EntityId.Value));
        Assert.Equal(new[] { "a", "b", "c" }, move.ChangedCharacters.Select(c => c.Id));
        Assert.Equal("a", move.ActorId);
        Assert.Equal("call_fixture_1", move.ToolCallId);
        Assert.All(kernel.World.Expedition!.Spatial.Entities, entity => {
            TraversingLocation travel = Assert.IsType<TraversingLocation>(entity.Location);
            Assert.Equal(Hour(8), travel.AnchorTime);
            Assert.Equal(Hour(9), travel.ArrivalDue);
        });
        Assert.Null(kernel.World.Business.InteractionStates["b"].PendingAdvance);
        Assert.Null(kernel.World.Business.InteractionStates["c"].PendingAdvance);
        Assert.NotNull(CycleActions.Check(kernel.World, "a", CycleCommand.DismissFollowers()));
        Assert.NotNull(CycleActions.Check(kernel.World, "b", CycleCommand.Unfollow()));

        await UntilFact(kernel, CycleEventKind.MoveArrived);
        Assert.Single(kernel.World.Expedition.Spatial.Entities, e => e.Location is AtPlaceLocation);
        Assert.Equal(1, controller.Calls);
        for (int remaining = 2; remaining > 0; remaining--) {
            await Commit(kernel);
            Assert.Equal(CycleEventKind.MoveArrived, Assert.Single(kernel.LastCompletion!.Event.Facts).Kind);
            Assert.Equal(1, controller.Calls);
        }
        Assert.All(kernel.World.Characters, actor => Assert.Equal("stream", CycleActions.CurrentPlace(kernel.World, actor.Id)));
        Assert.False(CycleActions.NeedsDecision(kernel.World, "b"));
        Assert.False(CycleActions.NeedsDecision(kernel.World, "c"));
        Assert.All(new[] { "a", "b", "c" }, id => Assert.Contains(kernel.World.Expedition.Visits, visit => visit.ActorId == id && visit.PlaceId == "stream"));
    }

    [Fact]
    public void A_followers_exhausted_movement_identity_refuses_departure_for_everyone() {
        CycleWorld world = WithGenerations(FollowingRulesTests.World(leaders: [null, "a", "a"]), [0, long.MaxValue, 0]);
        string before = world.DescribeState();
        Assert.NotNull(CycleActions.Check(world, "a", CycleCommand.Go("trail-1")));
        Assert.Equal(before, world.DescribeState());
        Assert.All(world.Expedition!.Spatial.Entities, entity => Assert.IsType<AtPlaceLocation>(entity.Location));
    }

    [Fact]
    public async Task Leader_go_acknowledges_only_the_leader_and_preserves_every_followers_reminders() {
        CycleSurvival Body() => new(Hour(8), 20 * CycleSurvival.Scale, 100 * CycleSurvival.Scale, 100 * CycleSurvival.Scale,
            Rate(0), Rate(0), CycleSurvivalRules.WarmthRate(false, true), warnedMask: 1, pendingMask: 1, pendingSince: Hour(8));
        CycleWorld initial = FollowingRulesTests.World(leaders: [null, "a", "a"], attention: [true, true, true], bodies: [Body(), Body(), Body()]);
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: Commands(CycleCommand.Go("trail-1")));
        await UntilFact(kernel, CycleEventKind.MoveStarted);
        Assert.False(kernel.World.GetCharacter("a").Communication.AttentionPending);
        Assert.Equal(0, kernel.World.GetCharacter("a").Survival!.PendingMask);
        foreach (string id in new[] { "b", "c" }) {
            Assert.True(kernel.World.GetCharacter(id).Communication.AttentionPending);
            Assert.Equal(1, kernel.World.GetCharacter(id).Survival!.PendingMask);
            Assert.True(CycleActions.NeedsDecision(kernel.World, id));
        }
    }

    [Theory]
    [InlineData("a")]
    [InlineData("b")]
    public async Task A_personal_death_cleans_relations_in_the_same_fact_and_retains_survivors_journeys(string dying) {
        CycleSurvival Body(string id) => new(Hour(8), 100 * CycleSurvival.Scale,
            (id == dying ? 8 : 100) * CycleSurvival.Scale, 100 * CycleSurvival.Scale, Rate(0),
            id == dying ? CycleSurvivalRules.WaterRate : Rate(0), CycleSurvivalRules.WarmthRate(false, true), warnedMask: id == dying ? 2 : 0);
        CycleWorld initial = FollowingRulesTests.World(leaders: [null, "a", "a"], bodies: [Body("a"), Body("b"), Body("c")]);
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: Commands(CycleCommand.Go("trail-2")));
        await UntilFact(kernel, CycleEventKind.MoveStarted);
        await UntilFact(kernel, CycleEventKind.SurvivalFailed);
        CycleFact death = Assert.Single(kernel.LastCompletion!.Event.Facts);
        Assert.Equal(dying, death.ActorId);
        Assert.Equal(Hour(9), kernel.World.Time);
        Assert.False(kernel.World.GetCharacter(dying).IsActive);
        foreach (CycleCharacter survivor in kernel.World.Characters.Where(c => c.IsActive)) {
            Assert.Equal(Hour(10), Assert.IsType<TraversingLocation>(CycleActions.Location(kernel.World, survivor.Id)).ArrivalDue);
            Assert.Equal(dying == "a" || survivor.Id == "a" ? null : "a", survivor.FollowingLeaderId);
        }
        while (kernel.World.Expedition!.Spatial.Entities.Any(e => e.Location is TraversingLocation)) { await Commit(kernel); }
        Assert.All(kernel.World.Characters.Where(c => c.IsActive), c => Assert.Equal("ridge", CycleActions.CurrentPlace(kernel.World, c.Id)));
    }

    [Fact]
    public async Task Fatal_weather_clears_leader_relations_without_ending_surviving_followers() {
        CycleSurvival Body(int warmth) => new(Hour(8), 100 * CycleSurvival.Scale, 100 * CycleSurvival.Scale,
            warmth * CycleSurvival.Scale, Rate(0), Rate(0), CycleSurvivalRules.WarmthRate(false, true), warnedMask: warmth < 30 ? 4 : 0);
        CycleWorld initial = FollowingRulesTests.World(leaders: [null, "a", "a"], bodies: [Body(5), Body(100), Body(100)],
            environment: new(0, 0, Hour(8)));
        var kernel = CycleEngine.CreateKernel(Memory(initial));
        await Commit(kernel);
        Assert.Equal(CycleEventKind.WeatherResolved, Assert.Single(kernel.LastCompletion!.Event.Facts).Kind);
        Assert.False(kernel.World.GetCharacter("a").IsActive);
        Assert.True(kernel.World.GetCharacter("b").IsActive);
        Assert.True(kernel.World.GetCharacter("c").IsActive);
        Assert.All(kernel.World.Characters, actor => Assert.Null(actor.FollowingLeaderId));
    }

    [Fact]
    public async Task Former_exit_arrival_preserves_all_members_and_following_for_return_travel() {
        CycleWorld initial = FollowingRulesTests.World(leaders: [null, "a", "a"], places: ["ravine", "ravine", "ravine"]);
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: Commands(CycleCommand.Go("trail-4"), CycleCommand.Go("trail-4")));
        await UntilFact(kernel, CycleEventKind.MoveStarted);
        while (kernel.World.Characters.Any(c => CycleActions.CurrentPlace(kernel.World, c.Id) != "road")) { await Commit(kernel); }
        Assert.Equal(CycleExpeditionSummary.Exploring, kernel.World.ExpeditionSummary);
        Assert.All(kernel.World.Characters, actor => {
            Assert.Null(actor.Ending);
            Assert.Equal(actor.Id == "a" ? null : "a", actor.FollowingLeaderId);
        });
        Assert.Equal(Hour(11), kernel.World.Time);
        await UntilFact(kernel, CycleEventKind.MoveStarted);
        while (kernel.World.Characters.Any(c => CycleActions.CurrentPlace(kernel.World, c.Id) != "ravine")) { await Commit(kernel); }
        Assert.Equal(Hour(14), kernel.World.Time);
        Assert.All(kernel.World.Characters, actor => Assert.True(actor.IsActive));
    }

    [Fact]
    public async Task Three_characters_complete_two_legs_work_independently_regroup_and_depart_again() {
        var queues = new Dictionary<string, Queue<CycleCommand>> {
            ["a"] = new([CycleCommand.Wait(1), CycleCommand.Go("trail-2"), CycleCommand.Go("trail-5"), CycleCommand.DismissFollowers(), CycleCommand.Gather("wood"), CycleCommand.Say("b", "工作结束，准备集合。"), CycleCommand.Go("trail-5")]),
            ["b"] = new([CycleCommand.Follow("a"), CycleCommand.Gather("wood"), CycleCommand.Follow("a")]),
            ["c"] = new([CycleCommand.Follow("a"), CycleCommand.Gather("wood"), CycleCommand.Follow("a")])
        };
        int acknowledgements = 0;
        var controller = new StubCharacterController((observation, actor) => {
            if (observation.Actor.Activity is not null && observation.Actor.Notices.Count > 0) {
                acknowledgements++;
                return CycleCommand.Continue();
            }
            return queues[actor].Dequeue();
        });
        var history = Memory(FollowingRulesTests.World());
        var kernel = CycleEngine.CreateKernel(history, characterController: controller);
        for (int steps = 0; steps < 90; steps++) {
            await Commit(kernel);
            if (history.CompletedEvents.SelectMany(e => e.Facts).Count(f => f.Kind == CycleEventKind.MoveStarted) == 3) { break; }
        }
        Assert.All(queues.Values, queue => Assert.Empty(queue));
        Assert.All(kernel.World.Characters, actor => {
            Assert.Equal(1, actor.Backpack.Count("item.wood"));
            Assert.IsType<TraversingLocation>(CycleActions.Location(kernel.World, actor.Id));
        });
        Assert.Equal("a", kernel.World.GetCharacter("b").FollowingLeaderId);
        Assert.Equal("a", kernel.World.GetCharacter("c").FollowingLeaderId);
        Assert.Equal(13 + acknowledgements, controller.Calls);
        Assert.Equal(3, history.CompletedEvents.SelectMany(e => e.Facts).Count(f => f.Kind == CycleEventKind.MoveStarted));
    }

    private static CycleWorld WithGenerations(CycleWorld world, long[] generations) {
        CycleExpedition expedition = world.Expedition!;
        GraphSpatialState spatial = GraphSpatialState.Restore(CycleExpeditionMap.Graph,
            expedition.Spatial.Entities.Select((entity, index) => new SpatialEntity(entity.Id, generations[index], entity.Location)), [], [], []);
        return CycleEngine.Rebuild(world.Business.Replace(world.Time, expedition: new(expedition.ScenarioId, spatial, expedition.Visits, expedition.Environment)), world.Time);
    }

    private static async Task UntilFact(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel, CycleEventKind kind) {
        for (int step = 0; step < 30; step++) {
            await Commit(kernel);
            if (kernel.LastCompletion!.Event.Facts.Any(fact => fact.Kind == kind)) { return; }
        }
        Assert.Fail($"Did not reach {kind}.");
    }

    private static async Task Commit(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel) {
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(48)));
        kernel.World.Validate();
        CycleWorld rebuilt = CycleEngine.Rebuild(kernel.World.Business, kernel.World.Time);
        Assert.Equal(rebuilt.Forecasts.ToString(), kernel.World.Forecasts.ToString());
        Assert.Equal(rebuilt.Spatial!.Contacts.ToString(), kernel.World.Spatial!.Contacts.ToString());
    }
}
