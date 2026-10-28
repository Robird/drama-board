using DramaBoard.ResourceCycle;

namespace DramaBoard.ResourceCycle.Cli.Tests;

public sealed class PlayInputTests {
    [Theory]
    [InlineData("\t :help  ", "Help")]
    [InlineData(" :exit\r\n", "Quit")]
    [InlineData(":look", "Look")]
    [InlineData(":help", "Help")]
    [InlineData(":exit", "Quit")]
    [InlineData(":quit", "Quit")]
    [InlineData(":details", "Details")]
    [InlineData(":layout", "Layout")]
    public void Local_intents_have_no_game_command(string input, string kind) {
        PlayInput parsed = PlayInput.Parse(input);
        Assert.Equal(kind, parsed.Kind.ToString());
        Assert.Null(parsed.Command);
    }

    [Theory]
    [InlineData("classic")]
    [InlineData("decision")]
    [InlineData("unknown")]
    public void Layout_selection_is_a_local_intent_even_when_the_name_is_unknown(string id) {
        PlayInput parsed = PlayInput.Parse($" :layout\t{id} ");
        Assert.Equal(PlayInputKind.Layout, parsed.Kind);
        Assert.Equal(id, parsed.LayoutId);
        Assert.Null(parsed.Command);
    }

    public static TheoryData<string, CycleCommand> Actions => new() {
        { "wait 24", CycleCommand.Wait(24) },
        { "  wait\t0001\r\n", CycleCommand.Wait(1) },
        { "gather sunflower 1", CycleCommand.Gather("sunflower", 1) },
        { "gather sunflower 2147483647", CycleCommand.Gather("sunflower", int.MaxValue) },
        { "gather sunflower all", CycleCommand.GatherAll("sunflower") },
        { "continue", CycleCommand.Continue() },
        { "cancel", CycleCommand.Cancel() },
        { "go trail-1", CycleCommand.Go("trail-1") },
        { "use item.water", CycleCommand.Use("item.water") },
        { " \tuse\titem.food  ", CycleCommand.Use("item.food") },
        { "handoff 抵达后  先检查保暖。", CycleCommand.Handoff("抵达后  先检查保暖。") },
        { "  handoff\t--clear ", CycleCommand.Handoff(null) },
        { "handoff --clear 然后检查", CycleCommand.Handoff("--clear 然后检查") },
    };

    [Theory]
    [MemberData(nameof(Actions))]
    public void Valid_syntax_preserves_the_intent(string input, CycleCommand expected) {
        PlayInput parsed = PlayInput.Parse(input);
        Assert.Equal(PlayInputKind.Action, parsed.Kind);
        Assert.Equal(expected, parsed.Command);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t")]
    [InlineData("Look")]
    [InlineData("look")]
    [InlineData("help")]
    [InlineData("quit")]
    [InlineData("exit")]
    [InlineData("details")]
    [InlineData("layout")]
    [InlineData("layout decision")]
    [InlineData(":continue")]
    [InlineData(":cancel")]
    [InlineData(":wait 1")]
    [InlineData(":go trail-1")]
    [InlineData(":use item.water")]
    [InlineData(":gather sunflower 1")]
    [InlineData("WAIT 1")]
    [InlineData("gather sunflower ALL")]
    [InlineData("look extra")]
    [InlineData("help extra")]
    [InlineData("quit extra")]
    [InlineData("continue extra")]
    [InlineData("cancel extra")]
    [InlineData("wait")]
    [InlineData("wait 1 extra")]
    [InlineData("gather sunflower")]
    [InlineData("gather sunflower all extra")]
    [InlineData("gather sunflower 2147483648")]
    [InlineData("wait 2147483648")]
    [InlineData("gather sunflower -1")]
    [InlineData("wait +1")]
    [InlineData("gather sunflower 1.0")]
    [InlineData("gather sunflower 1,000")]
    [InlineData("wait １")]
    [InlineData("unknown")]
    [InlineData("go")]
    [InlineData("go trail-1 extra")]
    [InlineData("Go trail-1")]
    [InlineData("use")]
    [InlineData("use item.water 2")]
    [InlineData("Use item.water")]
    [InlineData(":layout classic extra")]
    [InlineData(":help extra")]
    [InlineData(":exit extra")]
    [InlineData(":details extra")]
    [InlineData(":unknown")]
    [InlineData("handoff")]
    [InlineData("handoff  ")]
    [InlineData(":handoff 备注")]
    [InlineData("handofftext")]
    public void Malformed_or_unrepresentable_input_is_not_an_action(string input) {
        PlayInput parsed = PlayInput.Parse(input);
        Assert.Equal(PlayInputKind.Invalid, parsed.Kind);
        Assert.Null(parsed.Command);
    }

    [Theory]
    [InlineData("wait 0")]
    [InlineData("wait 25")]
    [InlineData("wait 2147483647")]
    [InlineData("gather sunflower 0")]
    [InlineData("gather Sunflower 1")]
    [InlineData("gather missing all")]
    [InlineData("continue")]
    [InlineData("cancel")]
    [InlineData("use item.water")]
    [InlineData("use missing")]
    public void Syntactic_success_does_not_bypass_current_domain_rules(string input) {
        CycleWorld world = CycleGameFactory.CreatePlayable().World;
        PlayInput parsed = PlayInput.Parse(input);
        Assert.Equal(PlayInputKind.Action, parsed.Kind);
        Assert.NotNull(parsed.Command);
        Assert.NotNull(CycleActions.Check(world, "a", parsed.Command));
    }
}
