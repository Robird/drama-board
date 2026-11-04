using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class FollowingMovementTests {
    [Fact]
    public async Task Three_members_depart_atomically_and_all_arrivals_settle_before_another_decision() {
        PlaygroundWorld initial = FollowingRulesTests.World(leaders: [null, "a", "a"]);
        // Movement generations are local histories, not a shared party identity.
        initial = WithGenerations(initial, [2, 9, 0]);
        var controller = Commands(PlaygroundCommand.Go("e-c7"));
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: controller);
        await UntilFact(kernel, PlaygroundEventKind.MoveStarted);
        PlaygroundFact move = Assert.Single(kernel.LastCompletion!.Event.Facts);
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
        Assert.NotNull(PlaygroundActions.Check(kernel.World, "a", PlaygroundCommand.DismissFollowers()));
        Assert.NotNull(PlaygroundActions.Check(kernel.World, "b", PlaygroundCommand.Unfollow()));

        await UntilFact(kernel, PlaygroundEventKind.MoveArrived);
        Assert.Single(kernel.World.Expedition.Spatial.Entities, e => e.Location is AtPlaceLocation);
        Assert.Equal(1, controller.Calls);
        for (int remaining = 2; remaining > 0; remaining--) {
            await Commit(kernel);
            Assert.Equal(PlaygroundEventKind.MoveArrived, Assert.Single(kernel.LastCompletion!.Event.Facts).Kind);
            Assert.Equal(1, controller.Calls);
        }
        Assert.All(kernel.World.Characters, actor => Assert.Equal("stream", PlaygroundActions.CurrentPlace(kernel.World, actor.Id)));
        Assert.False(PlaygroundActions.NeedsDecision(kernel.World, "b"));
        Assert.False(PlaygroundActions.NeedsDecision(kernel.World, "c"));
        Assert.All(new[] { "a", "b", "c" }, id => Assert.Contains(kernel.World.Expedition.Visits, visit => visit.ActorId == id && visit.PlaceId == "stream"));
    }

    [Fact]
    public void A_followers_exhausted_movement_identity_refuses_departure_for_everyone() {
        PlaygroundWorld world = WithGenerations(FollowingRulesTests.World(leaders: [null, "a", "a"]), [0, long.MaxValue, 0]);
        string before = world.DescribeState();
        Assert.NotNull(PlaygroundActions.Check(world, "a", PlaygroundCommand.Go("e-c7")));
        Assert.Equal(before, world.DescribeState());
        Assert.All(world.Expedition!.Spatial.Entities, entity => Assert.IsType<AtPlaceLocation>(entity.Location));
    }

    [Fact]
    public async Task Leader_go_acknowledges_only_the_leader_and_preserves_every_followers_reminders() {
        PlaygroundSurvival Body() => new(Hour(8), 20 * PlaygroundSurvival.Scale, 100 * PlaygroundSurvival.Scale, 100 * PlaygroundSurvival.Scale,
            Rate(0), Rate(0), PlaygroundSurvivalRules.WarmthRate(false, true), warnedMask: 1, pendingMask: 1, pendingSince: Hour(8));
        PlaygroundWorld initial = FollowingRulesTests.World(leaders: [null, "a", "a"], attention: [true, true, true], bodies: [Body(), Body(), Body()]);
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Commands(PlaygroundCommand.Go("e-c7")));
        await UntilFact(kernel, PlaygroundEventKind.MoveStarted);
        Assert.False(kernel.World.GetCharacter("a").Communication.AttentionPending);
        Assert.Equal(0, kernel.World.GetCharacter("a").Survival!.PendingMask);
        foreach (string id in new[] { "b", "c" }) {
            Assert.True(kernel.World.GetCharacter(id).Communication.AttentionPending);
            Assert.Equal(1, kernel.World.GetCharacter(id).Survival!.PendingMask);
            Assert.True(PlaygroundActions.NeedsDecision(kernel.World, id));
        }
    }

    [Theory]
    [InlineData("a")]
    [InlineData("b")]
    public async Task A_personal_death_cleans_relations_in_the_same_fact_and_retains_survivors_journeys(string dying) {
        PlaygroundSurvival Body(string id) => new(Hour(8), 100 * PlaygroundSurvival.Scale,
            (id == dying ? 8 : 100) * PlaygroundSurvival.Scale, 100 * PlaygroundSurvival.Scale, Rate(0),
            id == dying ? PlaygroundSurvivalRules.WaterRate : Rate(0), PlaygroundSurvivalRules.WarmthRate(false, true), warnedMask: id == dying ? 2 : 0);
        PlaygroundWorld initial = FollowingRulesTests.World(leaders: [null, "a", "a"], bodies: [Body("a"), Body("b"), Body("c")]);
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Commands(PlaygroundCommand.Go("e-b9")));
        await UntilFact(kernel, PlaygroundEventKind.MoveStarted);
        await UntilFact(kernel, PlaygroundEventKind.SurvivalFailed);
        PlaygroundFact death = Assert.Single(kernel.LastCompletion!.Event.Facts);
        Assert.Equal(dying, death.ActorId);
        Assert.Equal(Hour(9), kernel.World.Time);
        Assert.False(kernel.World.GetCharacter(dying).IsActive);
        foreach (PlaygroundCharacter survivor in kernel.World.Characters.Where(c => c.IsActive)) {
            Assert.Equal(Hour(10), Assert.IsType<TraversingLocation>(PlaygroundActions.Location(kernel.World, survivor.Id)).ArrivalDue);
            Assert.Equal(dying == "a" || survivor.Id == "a" ? null : "a", survivor.FollowingLeaderId);
        }
        while (kernel.World.Expedition!.Spatial.Entities.Any(e => e.Location is TraversingLocation)) { await Commit(kernel); }
        Assert.All(kernel.World.Characters.Where(c => c.IsActive), c => Assert.Equal("ridge", PlaygroundActions.CurrentPlace(kernel.World, c.Id)));
    }

    [Fact]
    public async Task Fatal_weather_clears_leader_relations_without_ending_surviving_followers() {
        PlaygroundSurvival Body(int warmth) => new(Hour(8), 100 * PlaygroundSurvival.Scale, 100 * PlaygroundSurvival.Scale,
            warmth * PlaygroundSurvival.Scale, Rate(0), Rate(0), PlaygroundSurvivalRules.WarmthRate(false, true), warnedMask: warmth < 30 ? 4 : 0);
        PlaygroundWorld initial = FollowingRulesTests.World(leaders: [null, "a", "a"], bodies: [Body(5), Body(100), Body(100)],
            environment: new(0, 0, Hour(8)));
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial));
        await Commit(kernel);
        Assert.Equal(PlaygroundEventKind.WeatherResolved, Assert.Single(kernel.LastCompletion!.Event.Facts).Kind);
        Assert.False(kernel.World.GetCharacter("a").IsActive);
        Assert.True(kernel.World.GetCharacter("b").IsActive);
        Assert.True(kernel.World.GetCharacter("c").IsActive);
        Assert.All(kernel.World.Characters, actor => Assert.Null(actor.FollowingLeaderId));
    }

    [Fact]
    public async Task Former_exit_arrival_preserves_all_members_and_following_for_return_travel() {
        PlaygroundWorld initial = FollowingRulesTests.World(leaders: [null, "a", "a"], places: ["ravine", "ravine", "ravine"]);
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Commands(PlaygroundCommand.Go("e-s3"), PlaygroundCommand.Go("e-z6")));
        await UntilFact(kernel, PlaygroundEventKind.MoveStarted);
        while (kernel.World.Characters.Any(c => PlaygroundActions.CurrentPlace(kernel.World, c.Id) != "road")) { await Commit(kernel); }
        Assert.Equal(PlaygroundExpeditionSummary.Exploring, kernel.World.ExpeditionSummary);
        Assert.All(kernel.World.Characters, actor => {
            Assert.Null(actor.Ending);
            Assert.Equal(actor.Id == "a" ? null : "a", actor.FollowingLeaderId);
        });
        Assert.Equal(Hour(11), kernel.World.Time);
        await UntilFact(kernel, PlaygroundEventKind.MoveStarted);
        while (kernel.World.Characters.Any(c => PlaygroundActions.CurrentPlace(kernel.World, c.Id) != "ravine")) { await Commit(kernel); }
        Assert.Equal(Hour(14), kernel.World.Time);
        Assert.All(kernel.World.Characters, actor => Assert.True(actor.IsActive));
    }

    [Fact]
    public async Task Three_characters_complete_two_legs_work_independently_regroup_and_depart_again() {
        var queues = new Dictionary<string, Queue<PlaygroundCommand>> {
            ["a"] = new([PlaygroundCommand.Wait(1), PlaygroundCommand.Go("e-b9"), PlaygroundCommand.Go("e-u8"), PlaygroundCommand.DismissFollowers(), PlaygroundCommand.Gather("wood"), PlaygroundCommand.Say("b", "工作结束，准备集合。"), PlaygroundCommand.Go("e-t4")]),
            ["b"] = new([PlaygroundCommand.Follow("a"), PlaygroundCommand.Gather("wood"), PlaygroundCommand.Follow("a")]),
            ["c"] = new([PlaygroundCommand.Follow("a"), PlaygroundCommand.Gather("wood"), PlaygroundCommand.Follow("a")])
        };
        int acknowledgements = 0;
        var controller = new StubCharacterController((observation, actor) => {
            if (observation.Actor.Activity is not null && observation.Actor.Notices.Count > 0) {
                acknowledgements++;
                return PlaygroundCommand.Continue();
            }
            return queues[actor].Dequeue();
        });
        var history = Memory(FollowingRulesTests.World());
        var kernel = PlaygroundEngine.CreateKernel(history, characterController: controller);
        for (int steps = 0; steps < 90; steps++) {
            await Commit(kernel);
            if (history.CompletedEvents.SelectMany(e => e.Facts).Count(f => f.Kind == PlaygroundEventKind.MoveStarted) == 3) { break; }
        }
        Assert.All(queues.Values, queue => Assert.Empty(queue));
        Assert.All(kernel.World.Characters, actor => {
            Assert.Equal(1, actor.Backpack.Count("item.wood"));
            Assert.IsType<TraversingLocation>(PlaygroundActions.Location(kernel.World, actor.Id));
        });
        Assert.Equal("a", kernel.World.GetCharacter("b").FollowingLeaderId);
        Assert.Equal("a", kernel.World.GetCharacter("c").FollowingLeaderId);
        Assert.Equal(13 + acknowledgements, controller.Calls);
        Assert.Equal(3, history.CompletedEvents.SelectMany(e => e.Facts).Count(f => f.Kind == PlaygroundEventKind.MoveStarted));
    }

    private static PlaygroundWorld WithGenerations(PlaygroundWorld world, long[] generations) {
        PlaygroundExpedition expedition = world.Expedition!;
        GraphSpatialState spatial = GraphSpatialState.Restore(PlaygroundExpeditionMap.Graph,
            expedition.Spatial.Entities.Select((entity, index) => new SpatialEntity(entity.Id, generations[index], entity.Location)), [], [], []);
        return PlaygroundEngine.Rebuild(world.Business.Replace(world.Time, expedition: new(expedition.ScenarioId, spatial, expedition.Visits, expedition.Environment)), world.Time);
    }

    private static async Task UntilFact(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel, PlaygroundEventKind kind) {
        for (int step = 0; step < 30; step++) {
            await Commit(kernel);
            if (kernel.LastCompletion!.Event.Facts.Any(fact => fact.Kind == kind)) { return; }
        }
        Assert.Fail($"Did not reach {kind}.");
    }

    private static async Task Commit(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel) {
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(48)));
        kernel.World.Validate();
        PlaygroundWorld rebuilt = PlaygroundEngine.Rebuild(kernel.World.Business, kernel.World.Time);
        AssertForecasts(rebuilt.Forecasts, kernel.World.Forecasts);
        Assert.Equal(rebuilt.Spatial!.Contacts.ToString(), kernel.World.Spatial!.Contacts.ToString());
    }
}
