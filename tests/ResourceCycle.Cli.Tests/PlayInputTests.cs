using DramaBoard.ResourceCycle;

namespace DramaBoard.ResourceCycle.Cli.Tests;

public sealed class PlayInputTests {
    [Theory]
    [InlineData("look", "Look")]
    [InlineData("\t help  ", "Help")]
    [InlineData(" quit\r\n", "Quit")]
    public void Local_intents_have_no_game_command(string input, string kind) {
        PlayInput parsed = PlayInput.Parse(input);
        Assert.Equal(kind, parsed.Kind.ToString());
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
