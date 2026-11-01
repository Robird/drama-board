using DramaBoard.ResourceCycle;

namespace DramaBoard.ResourceCycle.Cli.Tests;

public sealed class FollowingViewTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Layout_and_help_expose_following_commands_and_frozen_member_status(bool decision) {
        CycleWorld world = World(following: true);
        IPlayLayout layout = decision ? new DecisionPlayLayout() : new ClassicPlayLayout();
        CycleObservation follower = CycleDecisionContext.Observe(world, "b");
        string rendered = layout.Render(follower);
        Assert.Contains("正在跟随 阿青（`a`）", rendered);
        string restrictions = Assert.Single(rendered.Split('。'), part => part.Contains("不能独立", StringComparison.Ordinal));
        foreach (string command in new[] { "go", "gather", "produce", "wait" }) {
            Assert.Contains(command, restrictions);
        }
        foreach (string text in new[] { rendered, layout.RenderActionHelp(follower) }) {
            Assert.Contains("follow <actor-id>", text);
            Assert.Contains("unfollow", text);
            Assert.Contains("dismiss-followers", text);
            Assert.Contains("途中不能改变关系", text);
        }
        string leader = layout.Render(CycleDecisionContext.Observe(world, "a"));
        Assert.Contains("直属队员：阿白（`b`）", leader);
        Assert.Contains("正在跟随 `a`", leader);
        Assert.DoesNotContain("private-cargo", leader);
    }

    [Fact]
    public void Candidate_command_keys_preserve_each_target_and_dismiss_uses_parser_syntax() {
        CycleObservation observation = CycleDecisionContext.Observe(World(following: false), "a");
        var snapshot = CycleViewSnapshot.Create(observation);
        var candidates = observation.Commands.Where(check => check.Command.Kind == CycleCommandKind.Follow).ToArray();
        Assert.Equal(new[] { "b", "c" }, candidates.Select(check => check.Command.TargetActorId).ToArray());
        Assert.Equal(2, candidates.Select(check => CycleViewSnapshot.CommandId(check.Command)).Distinct().Count());
        string text = CycleUpdateRenderer.RenderCommandStates(snapshot.Values);
        Assert.Contains("follow b", text);
        Assert.Contains("follow c", text);
        Assert.Contains("dismiss-followers", text);
        Assert.DoesNotContain("dismissfollowers", text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Delta_and_saved_context_expose_relation_changes_without_access_to_world(bool decision) {
        CycleObservation before = CycleDecisionContext.Observe(World(following: false), "b");
        CycleObservation after = CycleDecisionContext.Observe(World(following: true), "b");
        var changed = new CycleObservedEvent(1, "b", CycleObservedEventKind.FollowStarted,
            before.Time, after.Time, effects: [new(new("following", "b", "leader"),
                CycleVisibleValue.KnownAbsent, CycleVisibleValue.Literal("a"))]);
        IPlayLayout layout = decision ? new DecisionPlayLayout() : new ClassicPlayLayout();
        var update = CycleViewUpdate.Create(CycleViewSnapshot.Create(before), CycleViewSnapshot.Create(after), [changed],
            PlayDeliveryPhase.ControllerInput);
        string delta = layout.RenderUpdate(update);
        Assert.Contains("已建立跟随关系", delta);
        Assert.Contains("跟随领队", delta);
        Assert.Equal("a", update.CurrentValues[new("following", "b", "leader")].Text);
        CharacterInteractionState initial = ViewContextFixture.State(before);
        var state = new CharacterInteractionState("b", new CharacterContext(1, CharacterContextStatus.Open,
            initial.CurrentContext!.Opening,
            [new CharacterContextItem(1, experiences: [new CharacterExperience(1, changed)], observation: after)], 2));
        string context = CharacterContextRenderer.RenderFull(state, layout);
        Assert.Contains("follow <actor-id>", context);
        Assert.Contains("已建立跟随关系", context);
        Assert.Contains("跟随领队", context);
        string latest = CharacterContextRenderer.RenderLatestState(state, layout);
        Assert.Contains("正在跟随 阿青（`a`）", latest);
    }

    [Fact]
    public void Gathering_scene_does_not_offer_following() {
        CycleObservation observation = CycleDecisionContext.Observe(CycleGameFactory.CreatePlayable().World, "a");
        Assert.DoesNotContain("follow", new DecisionPlayLayout().Render(observation));
        Assert.DoesNotContain("同行状态", new ClassicPlayLayout().Render(observation));
    }

    private static CycleWorld World(bool following) {
        var initial = CycleGameFactory.CreateSurvival(enableNeeds: false, characterCount: 3);
        CycleCharacter actor = initial.World.GetCharacter("b");
        var follower = new CycleCharacter(actor.Id, actor.Name, backpack: new(new Dictionary<string, int> { ["private-cargo"] = 1 }),
            communication: actor.Communication, followingLeaderId: following ? "a" : null);
        CycleBusinessState business = initial.World.Business.Replace(initial.World.Time, changedCharacters: [follower]);
        return CycleEngine.Rebuild(business, business.Time);
    }
}
