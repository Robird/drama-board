using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class FollowingRulesTests {
    [Theory]
    [InlineData("follow a", PlaygroundCommandKind.Follow)]
    [InlineData("unfollow", PlaygroundCommandKind.Unfollow)]
    [InlineData("dismiss-followers", PlaygroundCommandKind.DismissFollowers)]
    public void Following_commands_roundtrip_through_the_shared_parser(string text, PlaygroundCommandKind kind) {
        PlaygroundCommand command = Assert.IsType<PlaygroundCommand>(PlaygroundCommandParser.Parse(text));
        Assert.Equal(kind, command.Kind);
        Assert.Equal(text, PlaygroundCommandParser.Format(command));
    }

    [Fact]
    public void Following_is_expedition_only_and_rejects_invalid_or_remote_targets_without_disclosure() {
        PlaygroundWorld gathering = Solo();
        Assert.NotNull(PlaygroundActions.Check(gathering, "a", PlaygroundCommand.Follow("b")));
        PlaygroundWorld world = World();
        Assert.NotNull(PlaygroundActions.Check(world, "a", PlaygroundCommand.Follow("a")));
        Assert.NotNull(PlaygroundActions.Check(world, "a", PlaygroundCommand.Unfollow()));
        Assert.NotNull(PlaygroundActions.Check(world, "a", PlaygroundCommand.DismissFollowers()));
        PlaygroundWorld remote = World(places: ["camp", "stream", "camp"]);
        Assert.Equal(PlaygroundActions.Check(remote, "a", PlaygroundCommand.Follow("missing")), PlaygroundActions.Check(remote, "a", PlaygroundCommand.Follow("b")));
        Assert.NotNull(PlaygroundActions.Check(world, "a", new(PlaygroundCommandKind.Follow, TargetActorId: "b", Hours: 1)));
    }

    [Fact]
    public void Validation_rejects_chains_cycles_missing_leaders_and_independent_follower_activities() {
        Assert.Throws<InvalidOperationException>(() => World(leaders: ["b", "a", null]));
        Assert.Throws<InvalidOperationException>(() => World(leaders: [null, "a", "b"]));
        Assert.Throws<InvalidOperationException>(() => World(leaders: [null, "missing", null]));
        Assert.Throws<InvalidOperationException>(() => World(leaders: [null, "a", null], places: ["camp", "stream", "camp"]));
        Assert.Throws<InvalidOperationException>(() => World(leaders: [null, "a", null], waiting: [0, 1, 0]));
        PlaygroundWorld world = World(leaders: [null, "a", null], attention: [true, true, false]);
        Assert.NotNull(PlaygroundActions.Check(world, "a", PlaygroundCommand.Follow("c")));
        Assert.NotNull(PlaygroundActions.Check(world, "b", PlaygroundCommand.Follow("c")));
        Assert.NotNull(PlaygroundActions.Check(world, "c", PlaygroundCommand.Follow("b")));
        Assert.NotNull(PlaygroundActions.Check(world, "b", PlaygroundCommand.Wait(1)));
        Assert.NotNull(PlaygroundActions.Check(world, "b", PlaygroundCommand.Gather("berries")));
        Assert.NotNull(PlaygroundActions.Check(world, "b", PlaygroundCommand.Go("e-c7")));
    }

    [Fact]
    public async Task Follow_acknowledges_own_communication_and_sleeps_while_the_busy_leader_keeps_working() {
        PlaygroundWorld world = World(waiting: [2, 0, 2], attention: [false, true, false]);
        var controller = Commands(PlaygroundCommand.Follow("a"));
        var kernel = PlaygroundEngine.CreateKernel(Memory(world), characterController: controller);
        await StepWorldEvent(kernel);
        Assert.Equal(PlaygroundEventKind.FollowStarted, Assert.Single(kernel.LastCompletion!.Event.Facts).Kind);
        Assert.Equal("a", kernel.World.GetCharacter("b").FollowingLeaderId);
        Assert.False(kernel.World.GetCharacter("b").Communication.AttentionPending);
        Assert.False(PlaygroundActions.NeedsDecision(kernel.World, "b"));
        Assert.Equal(Hour(10), kernel.World.GetCharacter("a").Activity!.Due);
        Assert.Equal(CharacterContextStatus.Closed, kernel.World.Business.InteractionStates["b"].CurrentContext!.Status);
        Assert.Equal(1, controller.Calls);
    }

    [Fact]
    public async Task Dismiss_keeps_busy_leaders_and_followers_reminders_and_then_continue_remains_legal() {
        PlaygroundWorld world = World(leaders: [null, "a", "a"], waiting: [2, 0, 0], attention: [true, true, true]);
        var kernel = PlaygroundEngine.CreateKernel(Memory(world), characterController: Commands(PlaygroundCommand.DismissFollowers()));
        await StepWorldEvent(kernel);
        Assert.All(kernel.World.Characters, c => Assert.Null(c.FollowingLeaderId));
        Assert.All(kernel.World.Characters, c => Assert.True(c.Communication.AttentionPending));
        Assert.Equal(Hour(10), kernel.World.GetCharacter("a").Activity!.Due);
        Assert.Equal(CharacterContextStatus.Open, kernel.World.Business.InteractionStates["a"].CurrentContext!.Status);
        Assert.Null(PlaygroundActions.Check(kernel.World, "a", PlaygroundCommand.Continue()));
        Assert.True(PlaygroundActions.NeedsDecision(kernel.World, "b"));
        Assert.True(PlaygroundActions.NeedsDecision(kernel.World, "c"));
    }

    [Fact]
    public async Task Unfollow_is_an_instant_action_that_preserves_the_followers_existing_attention() {
        PlaygroundWorld world = World(leaders: [null, "a", "a"], waiting: [2, 0, 0], attention: [false, true, false]);
        var kernel = PlaygroundEngine.CreateKernel(Memory(world), characterController: Commands(PlaygroundCommand.Unfollow()));
        await StepWorldEvent(kernel);
        Assert.Null(kernel.World.GetCharacter("b").FollowingLeaderId);
        Assert.Equal("a", kernel.World.GetCharacter("c").FollowingLeaderId);
        Assert.True(kernel.World.GetCharacter("b").Communication.AttentionPending);
        Assert.Equal(CharacterContextStatus.Open, kernel.World.Business.InteractionStates["b"].CurrentContext!.Status);
        Assert.Null(PlaygroundActions.Check(kernel.World, "b", PlaygroundCommand.Wait(1)));
        Assert.Equal(Hour(8), kernel.World.Time);
    }

    internal static PlaygroundWorld World(string?[]? leaders = null, string[]? places = null, int[]? waiting = null,
        bool[]? attention = null, PlaygroundSurvival?[]? bodies = null, PlaygroundEnvironment? environment = null, string? lastServed = null) {
        PlaygroundWorld basis = PlaygroundGameFactory.CreateSurvival(Hour(8), enableNeeds: false, enableWeather: false, characterCount: 3).World;
        string[] ids = ["a", "b", "c"];
        places ??= ["camp", "camp", "camp"];
        PlaygroundCharacter[] actors = ids.Select((id, index) => new PlaygroundCharacter(id, id,
            activity: waiting?[index] > 0 ? new(PlaygroundActivityKind.Wait, 1, Hour(8), waiting[index] * PlaygroundTime.Hour, Hour(8 + waiting[index])) : null,
            nextActivityId: waiting?[index] > 0 ? 2 : 1,
            survival: bodies?[index], communication: new(Hour(8), 0, attention?[index] ?? false), followingLeaderId: leaders?[index])).ToArray();
        var expedition = new PlaygroundExpedition(PlaygroundExpeditionMap.ScenarioId,
            GraphSpatialState.Create(PlaygroundExpeditionMap.Graph, ids.Select((id, index) => new EntityPlacement(new(id), new(places[index])))),
            ids.Select((id, index) => new PlaygroundVisit(id, places[index], Hour(8), [])), environment);
        return PlaygroundEngine.Rebuild(new(Hour(8), basis.Light, basis.Resources, basis.Script, actors,
            lastServedActorId: lastServed, expedition: expedition), Hour(8));
    }
}
