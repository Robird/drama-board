using System.Text.Json;

namespace DramaBoard.Playground.Cli.Tests;

public sealed class PlaygroundLookaheadConfigurationTests {
    [Fact]
    public void Defaults_are_off_for_home_host_and_resolved_run() {
        Assert.False(new PlaygroundHostOptions().Lookahead);
        PlaygroundHomeTests.WithDirectory(path => {
            File.WriteAllText(Path.Combine(path, "playground.json"), "{\"version\":1}");
            Assert.False(PlaygroundHome.Load(path).Lookahead);
            Assert.False(Options.Parse([], path).Resolve().Host.Lookahead);
            Assert.False(Options.Parse(["--ephemeral"], path).Resolve().Host.Lookahead);
        });
    }

    [Fact]
    public void Init_explicitly_documents_off_in_manifest_and_output() {
        PlaygroundHomeTests.WithDirectory(path => {
            var output = new StringWriter();
            PlaygroundHome.Initialize(path, output);
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "playground.json")));
            Assert.Equal("off", document.RootElement.GetProperty("lookahead").GetString());
            Assert.Contains("默认为 off", output.ToString());
            Assert.False(PlaygroundHome.Load(path).Lookahead);
        });
    }

    [Theory]
    [InlineData("off", null, false)]
    [InlineData("on", null, true)]
    [InlineData("off", "on", true)]
    [InlineData("on", "off", false)]
    [InlineData("on", "on", true)]
    [InlineData("off", "off", false)]
    public void Cli_overrides_home(string homeValue, string? cliValue, bool expected) {
        PlaygroundHomeTests.WithDirectory(path => {
            File.WriteAllText(Path.Combine(path, "playground.json"), $"{{\"version\":1,\"lookahead\":\"{homeValue}\"}}");
            string[] args = cliValue is null ? [] : ["--lookahead", cliValue];
            Assert.Equal(expected, Options.Parse(args, path).Resolve().Host.Lookahead);
        });
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("ON")]
    [InlineData("Off")]
    [InlineData(" on")]
    [InlineData("on ")]
    [InlineData("invalid")]
    public void Cli_rejects_values_outside_exact_on_off(string value) {
        Assert.Throws<ArgumentException>(() => Options.Parse(["--lookahead", value]));
    }

    [Fact]
    public void Cli_rejects_missing_duplicate_or_disallowed_mode_switches() {
        Assert.Throws<ArgumentException>(() => Options.Parse(["--lookahead"]));
        Assert.Throws<ArgumentException>(() => Options.Parse(["--lookahead", "--help"]));
        Assert.Throws<ArgumentException>(() => Options.Parse(["--lookahead", "on", "--lookahead", "off"]));
        Assert.Throws<ArgumentException>(() => Options.Parse(["init", "--lookahead", "on"]));
        Assert.Throws<ArgumentException>(() => Options.Parse(["--branches", "--lookahead", "on"]));
        Assert.True(Options.Parse(["--help", "--lookahead", "on"]).Help);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData("1")]
    [InlineData("[]")]
    [InlineData("\"\"")]
    [InlineData("\"ON\"")]
    [InlineData("\" on\"")]
    [InlineData("\"on \"")]
    [InlineData("\"invalid\"")]
    public void Home_rejects_invalid_lookahead_even_with_cli_override(string value) {
        PlaygroundHomeTests.WithDirectory(path => {
            File.WriteAllText(Path.Combine(path, "playground.json"), $"{{\"version\":1,\"lookahead\":{value}}}");
            Assert.Throws<ArgumentException>(() => PlaygroundHome.Load(path));
            Assert.Throws<ArgumentException>(() => Options.Parse(["--lookahead", "off"], path).Resolve());
        });
    }

    [Fact]
    public void Home_rejects_duplicate_lookahead() {
        PlaygroundHomeTests.WithDirectory(path => {
            File.WriteAllText(Path.Combine(path, "playground.json"), "{\"version\":1,\"lookahead\":\"on\",\"lookahead\":\"off\"}");
            Assert.Throws<ArgumentException>(() => PlaygroundHome.Load(path));
        });
    }
}
