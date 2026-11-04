using DramaBoard.Goals;
using DramaBoard.Inventory;
using DramaBoard.Playground;

namespace DramaBoard.Playground.Cli.Tests;

public sealed class CommandPresentationTests {
    public static TheoryData<PlaygroundCommand> Commands => new() {
        PlaygroundCommand.Gather("药草|rare"), PlaygroundCommand.GatherAll("药草|rare"),
        PlaygroundCommand.Give("b|c", "item:food|rare", 1),
        PlaygroundCommand.Offer("b|c", new("item:food|rare", 1), new("money", 2)),
        PlaygroundCommand.Path("目标|地", "起点|地"), PlaygroundCommand.Navigate("path:a|b:1:2"),
        PlaygroundCommand.Accept("trade:a|b:1:2"), PlaygroundCommand.Handoff(null),
        PlaygroundCommand.Say("b|c", "你好 世界"),
        PlaygroundCommand.ManageGoal(new(GoalOperationKind.Show, "{\"id\":\"goal|one\"}")),
    };

    [Theory]
    [MemberData(nameof(Commands))]
    public void Delta_uses_canonical_command_labels_without_decoding_identity(PlaygroundCommand command) {
        var before = PlaygroundViewSnapshot.Create(Observation([new(command, "暂时受阻")]));
        var after = PlaygroundViewSnapshot.Create(Observation([new(command, null)]));
        var update = PlaygroundViewUpdate.Create(before, after, [], PlayDeliveryPhase.ControllerInput);

        string rendered = PlaygroundUpdateRenderer.Render(update);

        Assert.Contains(PlaygroundCommandParser.Format(command) + " · 条件：条件满足", rendered);
        Assert.DoesNotContain("\"Kind\"", rendered);
    }

    [Fact]
    public void Distinct_gift_items_and_targets_cannot_alias_through_the_separator() {
        PlaygroundCommand first = PlaygroundCommand.Give("a|b", "c", 1);
        PlaygroundCommand second = PlaygroundCommand.Give("b", "c|a", 1);

        Assert.NotEqual(PlaygroundViewSnapshot.CommandId(first), PlaygroundViewSnapshot.CommandId(second));
        var snapshot = PlaygroundViewSnapshot.Create(Observation(new(first, "不可赠送"), new(second, "不可赠送")));
        Assert.Equal(2, snapshot.Values.Count(pair => pair.Key.Category == "command"));
    }

    [Fact]
    public void Command_identity_includes_every_parameter_even_when_display_syntax_matches() {
        PlaygroundCommand original = PlaygroundCommand.Continue();
        PlaygroundCommand[] variants = [
            original, original with { Kind = PlaygroundCommandKind.Cancel }, original with { Hours = 1 },
            original with { ResourceId = "r" }, original with { Count = 1 }, original with { All = true },
            original with { EntranceId = "e" }, original with { ItemId = "i" }, original with { HandoffText = "h" },
            original with { TargetActorId = "b" }, original with { SpeechText = "s" },
            original with { Goal = new(GoalOperationKind.Show, "{}") }, original with { RecipeId = "recipe" },
            original with { MoneyAmount = 1 }, original with { GiveAsset = new("i", 1) },
            original with { WantAsset = new("money", 1) }, original with { TradeId = "trade" },
            original with { TargetPlaceId = "to" }, original with { OriginPlaceId = "from" }, original with { PathId = "path" },
        ];
        Assert.Equal(variants.Length, variants.Select(PlaygroundViewSnapshot.CommandId).Distinct().Count());
        Assert.Equal(PlaygroundViewSnapshot.CommandId(original), PlaygroundViewSnapshot.CommandId(original with { }));
        Assert.NotEqual(PlaygroundViewSnapshot.CommandId(PlaygroundCommand.Handoff(null)),
            PlaygroundViewSnapshot.CommandId(PlaygroundCommand.Handoff("")));
        Assert.NotEqual(PlaygroundViewSnapshot.CommandId(PlaygroundCommand.Handoff(null)),
            PlaygroundViewSnapshot.CommandId(PlaygroundCommand.Handoff("--clear")));
        Assert.NotEqual(PlaygroundViewSnapshot.CommandId(PlaygroundCommand.ManageGoal(new(GoalOperationKind.Show, "{}"))),
            PlaygroundViewSnapshot.CommandId(PlaygroundCommand.ManageGoal(new(GoalOperationKind.Forget, "{}"))));
        Assert.NotEqual(PlaygroundViewSnapshot.CommandId(PlaygroundCommand.ManageGoal(new(GoalOperationKind.Show, "{}"))),
            PlaygroundViewSnapshot.CommandId(PlaygroundCommand.ManageGoal(new(GoalOperationKind.Show, "{\"id\":\"g\"}"))));
    }

    [Fact]
    public void Removed_commands_keep_their_labels_and_warning_history() {
        PlaygroundCommand command = PlaygroundCommand.Gather("herb|rare");
        var before = PlaygroundViewSnapshot.Create(Observation([new(command, null, "旧提示")]));
        var after = PlaygroundViewSnapshot.Create(Observation());
        var update = PlaygroundViewUpdate.Create(before, after, [], PlayDeliveryPhase.Advancing);

        string rendered = PlaygroundUpdateRenderer.Render(update);

        Assert.Contains("gather herb|rare 1 · 条件：不再可见", rendered);
        Assert.Contains("gather herb|rare 1 · 额外提示：原行动提示已不再适用", rendered);
    }

    [Fact]
    public void Wait_ranges_and_accept_caution_survive_identity_changes() {
        PlaygroundCommandCheck[] commands = [
            new(PlaygroundCommand.Wait(1), null), new(PlaygroundCommand.Wait(2), null),
            new(PlaygroundCommand.Wait(3), null), new(PlaygroundCommand.Wait(5), null),
            new(PlaygroundCommand.Accept("trade:a|b:1:2"), null),
        ];
        var update = PlaygroundViewUpdate.Create(PlaygroundViewSnapshot.Create(Observation()),
            PlaygroundViewSnapshot.Create(Observation(commands)), [], PlayDeliveryPhase.ControllerInput);

        string rendered = PlaygroundUpdateRenderer.Render(update);

        Assert.Contains("wait 1–3、wait 5", rendered);
        Assert.Contains("accept trade:a|b:1:2", rendered);
        Assert.Contains("accept 仅可尝试，成交以提交结果为准", rendered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Full_layouts_and_retained_context_rebuild_the_same_command_labels(bool decision) {
        PlaygroundCommand gather = PlaygroundCommand.Gather("herb|rare");
        var before = Observation([new(gather, "暂时受阻"), new(PlaygroundCommand.Handoff(null), null),
            new(PlaygroundCommand.GatherAll("herb|rare"), null), new(PlaygroundCommand.Continue(), null), new(PlaygroundCommand.Cancel(), null),
            .. Enumerable.Range(1, 24).Select(hours => new PlaygroundCommandCheck(PlaygroundCommand.Wait(hours), null))]);
        var after = Observation([new(gather, null)]);
        IPlayLayout layout = decision ? new DecisionPlayLayout() : new ClassicPlayLayout();
        string full = layout.Render(before);
        Assert.Contains("gather herb|rare 1", full);
        Assert.Contains("handoff --clear", full);
        if (decision) { Assert.Contains("wait 1–24", full); }

        CharacterContext context = ViewContextFixture.State(before).CurrentContext!;
        var retained = new CharacterInteractionState("a", new CharacterContext(1, CharacterContextStatus.Open, context.Opening,
            [new CharacterContextItem(1, observation: after)], 2));
        string items = CharacterContextRenderer.RenderItems(retained, layout);
        Assert.Contains("gather herb|rare 1 · 条件：条件满足", items);
        Assert.DoesNotContain("\"Kind\"", items);
    }

    private static PlaygroundObservation Observation(params PlaygroundCommandCheck[] commands) {
        var resource = new PlaygroundResource("herb|rare", "草药", "item.herb", true, PlaygroundTime.Hour, 1, 1);
        return new() {
            Time = new(0), IsDay = true, NextLightChange = new(PlaygroundTime.Hour),
            Actor = new("a", "旅人", 0, new ItemInventory(), null, [], null, 1),
            Resources = [resource], HarvestDurations = new Dictionary<string, long> { [resource.Id] = resource.HarvestTicks },
            ItemNames = new Dictionary<string, string> { [resource.ItemId] = resource.Name },
            OtherCharacters = [], Notices = [], Commands = commands,
            IsExpedition = true, PlaceId = "camp", PlaceName = "营地", PlaceDescription = "测试地点",
        };
    }
}
