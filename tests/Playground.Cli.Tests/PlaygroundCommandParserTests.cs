using DramaBoard.Playground;

namespace DramaBoard.Playground.Cli.Tests;

public sealed class PlaygroundCommandParserTests {
    public static TheoryData<string, PlaygroundCommand> Actions => new() {
        { "wait 24", PlaygroundCommand.Wait(24) },
        { "  wait\t0001\r\n", PlaygroundCommand.Wait(1) },
        { "gather sunflower 1", PlaygroundCommand.Gather("sunflower", 1) },
        { "gather sunflower 2147483647", PlaygroundCommand.Gather("sunflower", int.MaxValue) },
        { "gather sunflower all", PlaygroundCommand.GatherAll("sunflower") },
        { "continue", PlaygroundCommand.Continue() },
        { "cancel", PlaygroundCommand.Cancel() },
        { "go trail-1", PlaygroundCommand.Go("trail-1") },
        { "use item.water", PlaygroundCommand.Use("item.water") },
        { " \tuse\titem.food  ", PlaygroundCommand.Use("item.food") },
        { "handoff 抵达后  先检查保暖。", PlaygroundCommand.Handoff("抵达后  先检查保暖。") },
        { "  handoff\t--clear ", PlaygroundCommand.Handoff(null) },
        { "handoff --clear 然后检查", PlaygroundCommand.Handoff("--clear 然后检查") },
        { "handoff :exit", PlaygroundCommand.Handoff(":exit") },
    };

    [Theory]
    [MemberData(nameof(Actions))]
    public void Valid_syntax_preserves_the_intent(string input, PlaygroundCommand expected) {
        PlaygroundCommand? parsed = PlaygroundCommandParser.Parse(input);
        Assert.Equal(expected, parsed);
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
    [InlineData(":look")]
    [InlineData(":help")]
    [InlineData(":details")]
    [InlineData(":exit")]
    [InlineData(":quit")]
    [InlineData(":layout")]
    [InlineData(":layout classic")]
    [InlineData(":mode")]
    [InlineData(":mode full")]
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
        PlaygroundCommand? parsed = PlaygroundCommandParser.Parse(input);
        Assert.Null(parsed);
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
        PlaygroundWorld world = PlaygroundGameFactory.CreatePlayable().World;
        PlaygroundCommand? parsed = PlaygroundCommandParser.Parse(input);
        Assert.NotNull(parsed);
        Assert.NotNull(PlaygroundActions.Check(world, "a", parsed));
    }
}
