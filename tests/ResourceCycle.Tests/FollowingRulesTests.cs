using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class FollowingRulesTests {
    [Theory]
    [InlineData("follow a", CycleCommandKind.Follow)]
    [InlineData("unfollow", CycleCommandKind.Unfollow)]
    [InlineData("dismiss-followers", CycleCommandKind.DismissFollowers)]
    public void Following_commands_roundtrip_through_the_shared_parser(string text, CycleCommandKind kind) {
        CycleCommand command = Assert.IsType<CycleCommand>(CycleCommandParser.Parse(text));
        Assert.Equal(kind, command.Kind);
        Assert.Equal(text, CycleCommandParser.Format(command));
    }

    [Fact]
    public void Following_is_expedition_only_and_rejects_invalid_or_remote_targets_without_disclosure() {
        CycleWorld gathering = Solo();
        Assert.NotNull(CycleActions.Check(gathering, "a", CycleCommand.Follow("b")));
        CycleWorld world = World();
        Assert.NotNull(CycleActions.Check(world, "a", CycleCommand.Follow("a")));
        Assert.NotNull(CycleActions.Check(world, "a", CycleCommand.Unfollow()));
        Assert.NotNull(CycleActions.Check(world, "a", CycleCommand.DismissFollowers()));
        CycleWorld remote = World(places: ["camp", "stream", "camp"]);
        Assert.Equal(CycleActions.Check(remote, "a", CycleCommand.Follow("missing")), CycleActions.Check(remote, "a", CycleCommand.Follow("b")));
        Assert.NotNull(CycleActions.Check(world, "a", new(CycleCommandKind.Follow, TargetActorId: "b", Hours: 1)));
    }

    [Fact]
    public void Validation_rejects_chains_cycles_missing_leaders_and_independent_follower_activities() {
        Assert.Throws<InvalidOperationException>(() => World(leaders: ["b", "a", null]));
        Assert.Throws<InvalidOperationException>(() => World(leaders: [null, "a", "b"]));
        Assert.Throws<InvalidOperationException>(() => World(leaders: [null, "missing", null]));
        Assert.Throws<InvalidOperationException>(() => World(leaders: [null, "a", null], places: ["camp", "stream", "camp"]));
        Assert.Throws<InvalidOperationException>(() => World(leaders: [null, "a", null], waiting: [0, 1, 0]));
        CycleWorld world = World(leaders: [null, "a", null], attention: [true, true, false]);
        Assert.NotNull(CycleActions.Check(world, "a", CycleCommand.Follow("c")));
        Assert.NotNull(CycleActions.Check(world, "b", CycleCommand.Follow("c")));
        Assert.NotNull(CycleActions.Check(world, "c", CycleCommand.Follow("b")));
        Assert.NotNull(CycleActions.Check(world, "b", CycleCommand.Wait(1)));
        Assert.NotNull(CycleActions.Check(world, "b", CycleCommand.Gather("berries")));
        Assert.NotNull(CycleActions.Check(world, "b", CycleCommand.Go("trail-1")));
    }

    [Fact]
    public async Task Follow_acknowledges_own_communication_and_sleeps_while_the_busy_leader_keeps_working() {
        CycleWorld world = World(waiting: [2, 0, 2], attention: [false, true, false]);
        var controller = Commands(CycleCommand.Follow("a"));
        var kernel = CycleEngine.CreateKernel(Memory(world), characterController: controller);
        await StepWorldEvent(kernel);
        Assert.Equal(CycleEventKind.FollowStarted, Assert.Single(kernel.LastCompletion!.Event.Facts).Kind);
        Assert.Equal("a", kernel.World.GetCharacter("b").FollowingLeaderId);
        Assert.False(kernel.World.GetCharacter("b").Communication.AttentionPending);
        Assert.False(CycleActions.NeedsDecision(kernel.World, "b"));
        Assert.Equal(Hour(10), kernel.World.GetCharacter("a").Activity!.Due);
        Assert.Equal(CharacterContextStatus.Closed, kernel.World.Business.InteractionStates["b"].CurrentContext!.Status);
        Assert.Equal(1, controller.Calls);
    }

    [Fact]
    public async Task Dismiss_keeps_busy_leaders_and_followers_reminders_and_then_continue_remains_legal() {
        CycleWorld world = World(leaders: [null, "a", "a"], waiting: [2, 0, 0], attention: [true, true, true]);
        var kernel = CycleEngine.CreateKernel(Memory(world), characterController: Commands(CycleCommand.DismissFollowers()));
        await StepWorldEvent(kernel);
        Assert.All(kernel.World.Characters, c => Assert.Null(c.FollowingLeaderId));
        Assert.All(kernel.World.Characters, c => Assert.True(c.Communication.AttentionPending));
        Assert.Equal(Hour(10), kernel.World.GetCharacter("a").Activity!.Due);
        Assert.Equal(CharacterContextStatus.Open, kernel.World.Business.InteractionStates["a"].CurrentContext!.Status);
        Assert.Null(CycleActions.Check(kernel.World, "a", CycleCommand.Continue()));
        Assert.True(CycleActions.NeedsDecision(kernel.World, "b"));
        Assert.True(CycleActions.NeedsDecision(kernel.World, "c"));
    }

    [Fact]
    public async Task Unfollow_is_an_instant_action_that_preserves_the_followers_existing_attention() {
        CycleWorld world = World(leaders: [null, "a", "a"], waiting: [2, 0, 0], attention: [false, true, false]);
        var kernel = CycleEngine.CreateKernel(Memory(world), characterController: Commands(CycleCommand.Unfollow()));
        await StepWorldEvent(kernel);
        Assert.Null(kernel.World.GetCharacter("b").FollowingLeaderId);
        Assert.Equal("a", kernel.World.GetCharacter("c").FollowingLeaderId);
        Assert.True(kernel.World.GetCharacter("b").Communication.AttentionPending);
        Assert.Equal(CharacterContextStatus.Open, kernel.World.Business.InteractionStates["b"].CurrentContext!.Status);
        Assert.Null(CycleActions.Check(kernel.World, "b", CycleCommand.Wait(1)));
        Assert.Equal(Hour(8), kernel.World.Time);
    }

    internal static CycleWorld World(string?[]? leaders = null, string[]? places = null, int[]? waiting = null,
        bool[]? attention = null, CycleSurvival?[]? bodies = null, CycleEnvironment? environment = null, string? lastServed = null) {
        CycleWorld basis = CycleGameFactory.CreateSurvival(Hour(8), enableNeeds: false, enableWeather: false, characterCount: 3).World;
        string[] ids = ["a", "b", "c"];
        places ??= ["camp", "camp", "camp"];
        CycleCharacter[] actors = ids.Select((id, index) => new CycleCharacter(id, id,
            activity: waiting?[index] > 0 ? new(CycleActivityKind.Wait, 1, Hour(8), waiting[index] * CycleTime.Hour, Hour(8 + waiting[index])) : null,
            nextActivityId: waiting?[index] > 0 ? 2 : 1,
            survival: bodies?[index], communication: new(Hour(8), 0, attention?[index] ?? false), followingLeaderId: leaders?[index])).ToArray();
        var expedition = new CycleExpedition(CycleExpeditionMap.ScenarioId,
            GraphSpatialState.Create(CycleExpeditionMap.Graph, ids.Select((id, index) => new EntityPlacement(new(id), new(places[index])))),
            ids.Select((id, index) => new CycleVisit(id, places[index], Hour(8), [])), environment);
        return CycleEngine.Rebuild(new(Hour(8), basis.Light, basis.Resources, basis.Script, actors,
            lastServedActorId: lastServed, expedition: expedition), Hour(8));
    }
}
