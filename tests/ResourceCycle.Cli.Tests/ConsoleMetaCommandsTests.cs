namespace DramaBoard.ResourceCycle.Cli.Tests;

public sealed class ConsoleMetaCommandsTests {
    [Theory]
    [InlineData("\t :help  ", "Help", null)]
    [InlineData(" :exit\r\n", "Exit", null)]
    [InlineData(":look", "Look", null)]
    [InlineData(":help", "Help", null)]
    [InlineData(":exit", "Exit", null)]
    [InlineData(":quit", "Exit", null)]
    [InlineData(":details", "Details", null)]
    [InlineData(":layout", "Layout", null)]
    [InlineData(":layout classic", "Layout", "classic")]
    [InlineData(" :layout\tdecision ", "Layout", "decision")]
    [InlineData(":layout unknown", "Layout", "unknown")]
    [InlineData(":mode", "Mode", null)]
    [InlineData(":mode full", "Mode", "full")]
    [InlineData(":mode delta", "Mode", "delta")]
    [InlineData(":mode unknown", "Mode", "unknown")]
    public void Enabled_directory_recognizes_syntax_and_disabled_directory_rejects_it(string input, string kind, string? argument) {
        ConsoleMetaCommand parsed = Assert.IsType<ConsoleMetaCommand>(ConsoleMetaCommands.HumanDefault.Parse(input));
        Assert.Equal(kind, parsed.Kind.ToString());
        Assert.Equal(argument, parsed.Argument);
        Assert.Null(ConsoleMetaCommands.None.Parse(input));
        Assert.Null(CycleCommandParser.Parse(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t")]
    [InlineData("look")]
    [InlineData("help")]
    [InlineData("details")]
    [InlineData("exit")]
    [InlineData("quit")]
    [InlineData("layout classic")]
    [InlineData("mode full")]
    [InlineData(":Help")]
    [InlineData(":EXIT")]
    [InlineData(":help extra")]
    [InlineData(":exit extra")]
    [InlineData(":quit extra")]
    [InlineData(":look extra")]
    [InlineData(":details extra")]
    [InlineData(":layout classic extra")]
    [InlineData(":mode full extra")]
    [InlineData(":unknown")]
    [InlineData("wait 1")]
    [InlineData(":wait 1")]
    [InlineData("handoff :exit")]
    public void Non_meta_and_malformed_syntax_is_rejected(string input) {
        Assert.Null(ConsoleMetaCommands.HumanDefault.Parse(input));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Help_discovers_registered_choices_and_only_supported_modes(bool survival) {
        PlayPresentation presentation = PlayPresentation.ForMode(survival);
        ConsoleMetaCommands commands = ConsoleMetaCommands.HumanDefault;
        string help = commands.RenderHelp(presentation);
        foreach (IPlayLayout layout in presentation.Layouts) {
            Assert.Contains($":layout {layout.Id}", help);
            ConsoleMetaCommand command = Assert.IsType<ConsoleMetaCommand>(commands.Parse($":layout {layout.Id}"));
            Assert.True(presentation.TrySelect(command.Argument!));
            Assert.Same(layout, presentation.Current);
        }
        foreach (var mode in presentation.SupportedModes) {
            Assert.Contains($":mode {mode.Key}", help);
            ConsoleMetaCommand command = Assert.IsType<ConsoleMetaCommand>(commands.Parse($":mode {mode.Key}"));
            Assert.True(presentation.TrySelectMode(command.Argument!));
            Assert.Equal(mode.Value, presentation.Mode);
        }
        foreach (string command in new[] { ":look", ":help", ":details", ":exit", ":quit" }) {
            Assert.Contains(command, help);
            Assert.NotNull(commands.Parse(command));
        }
        if (!survival) {
            Assert.DoesNotContain("delta", help);
            Assert.False(presentation.TrySelectMode("delta"));
        }
        PlayOutputMode before = presentation.Mode;
        Assert.False(presentation.TrySelectMode("unknown"));
        Assert.Equal(before, presentation.Mode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Disabled_directory_produces_no_capability_prompts(bool survival) {
        PlayPresentation presentation = PlayPresentation.ForMode(survival);
        ConsoleMetaCommands commands = ConsoleMetaCommands.None;
        Assert.Empty(commands.RenderHint(presentation));
        Assert.Empty(commands.RenderHelp(presentation));
        Assert.Empty(commands.DescribeLayouts(presentation));
        Assert.Empty(commands.DescribeModes(presentation));
        Assert.Empty(commands.RejectionHint);
    }
}
