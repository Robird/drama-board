namespace DramaBoard.Playground.Cli.Tests;

public sealed class OptionsTests {
    [Fact]
    public void Explicit_cli_replaces_only_selected_home_settings_and_uses_launch_paths() {
        PlaygroundHomeTests.WithDirectory(path => {
            string home = Directory.CreateDirectory(Path.Combine(path, "home with spaces")).FullName;
            File.WriteAllText(Path.Combine(home, "playground.json"), """
                {"version":1,"gym":"record","maxCalls":7,"steps":42,"store":"world","llmConnections":"connections.json","characterControls":"controls.json"}
                """);
            var baseline = Options.Parse([], home).Resolve();
            var selected = Options.Parse(["--character-controls", "temporary.json", "--gym", "off", "--home", home], path).Resolve();
            Assert.Equal(baseline.Store, selected.Store);
            Assert.Equal(Path.Combine(home, "world"), selected.Store);
            Assert.Equal(Path.Combine(home, "connections.json"), selected.Host.ConnectionsPath);
            Assert.Equal(Path.Combine(path, "temporary.json"), selected.Host.CharacterControlsPath);
            Assert.Equal(7, selected.Host.MaxCalls);
            Assert.Equal(42, selected.Host.MaxSteps);
            Assert.False(selected.Host.Gym);
            Assert.True(baseline.Host.Gym);
            Assert.Null(Options.Parse(["--home", home, "--no-character-controls"], path).Resolve().Host.CharacterControlsPath);
        });
    }

    [Fact]
    public void Management_direct_store_bypasses_bad_home_and_home_management_ignores_models() {
        PlaygroundHomeTests.WithDirectory(path => {
            File.WriteAllText(Path.Combine(path, "playground.json"), "broken");
            var direct = Options.Parse(["--home", path, "--store", "world", "--branches"], path).Resolve();
            Assert.Null(direct.HomePath);
            Assert.Equal(Path.Combine(path, "world"), direct.Store);
            File.WriteAllText(Path.Combine(path, "playground.json"), "{\"version\":1,\"llmConnections\":\"missing.json\",\"characterControls\":\"also-missing.json\"}");
            var managed = Options.Parse(["--branches"], path).Resolve();
            Assert.Equal(Path.Combine(path, "store"), managed.Store);
            Assert.NotNull(managed.Management);
            Assert.Empty(Directory.GetDirectories(path));
        });
    }

    [Fact]
    public void Ephemeral_retains_home_configuration_and_requires_explicit_gym_off() {
        PlaygroundHomeTests.WithDirectory(path => {
            File.WriteAllText(Path.Combine(path, "playground.json"), "{\"version\":1,\"gym\":\"record\"}");
            Assert.Throws<ArgumentException>(() => Options.Parse(["--ephemeral"], path).Resolve());
            var selected = Options.Parse(["--ephemeral", "--gym", "off"], path).Resolve();
            Assert.Null(selected.Store);
            Assert.Equal(path, selected.Host.HomePath);
            Assert.Equal(Path.Combine(path, "llm-connections.json"), selected.Host.ConnectionsPath);
            Assert.Empty(Directory.GetDirectories(path));
        });
    }

    [Fact]
    public void Ephemeral_feedback_query_selects_home_and_does_not_require_models() {
        PlaygroundHomeTests.WithDirectory(path => {
            File.WriteAllText(Path.Combine(path, "playground.json"), "{\"version\":1,\"gym\":\"record\",\"llmConnections\":\"missing.json\"}");
            var selected = Options.Parse(["--ephemeral", "--feedbacks"], path).Resolve();
            Assert.Null(selected.Store);
            Assert.Equal(path, selected.HomePath);
            Assert.Equal(GymManagementKind.Feedbacks, selected.Management!.Kind);
            Assert.Empty(Directory.GetDirectories(path));
        });
    }

    [Theory]
    [InlineData("--llm")]
    [InlineData("--human", "a")]
    [InlineData("--character-connections", "old.json")]
    [InlineData("--feedback")]
    [InlineData("--gym")]
    [InlineData("--gym", "feedback")]
    [InlineData("--gym-feedbacks")]
    [InlineData("--gym-fork", "old-feedback")]
    [InlineData("--ephemeral", "--feedbacks", "--branch", "01000000")]
    [InlineData("--gym", "record", "--gym", "off")]
    [InlineData("--character-controls", "x", "--no-character-controls")]
    [InlineData("--store", "x", "--ephemeral")]
    [InlineData("--branches", "--max-calls", "1")]
    [InlineData("init", "--store", "x")]
    public void Invalid_or_removed_cli_is_rejected(params string[] args) {
        Assert.Throws<ArgumentException>(() => Options.Parse(args));
    }
}
