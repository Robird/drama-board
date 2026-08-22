using DramaBoard.FirstBoard.Demo;
using DramaBoard.FirstBoard.Demo.Live;

namespace DramaBoard.FirstBoard.Demo.Tests;

public sealed class DemoOptionsTests
{
    [Fact]
    public void DefaultsSelectAllAiDeveloperPlaybackAtControlledRate()
    {
        DemoOptions options = DemoOptions.Parse([]);

        Assert.Null(options.HumanActorId);
        Assert.Equal(PresentationMode.Developer, options.PresentationMode);
        Assert.Equal(TimeSpan.FromMilliseconds(250), options.PresentationInterval);
        Assert.Contains("human-none-presentation-developer", options.OutputDirectory);
    }

    [Theory]
    [InlineData("alice", BoardIds.Alice)]
    [InlineData("ALICE", BoardIds.Alice)]
    [InlineData("bob", BoardIds.Bob)]
    [InlineData("BoB", BoardIds.Bob)]
    public void HumanActorIsParsedCaseInsensitively(string value, string expectedActorId)
    {
        DemoOptions options = DemoOptions.Parse(["--human", value]);

        Assert.Equal(expectedActorId, options.HumanActorId);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("NONE")]
    public void ExplicitNoneSelectsAllAi(string value)
    {
        DemoOptions options = DemoOptions.Parse(["--human", value]);

        Assert.Null(options.HumanActorId);
    }

    [Fact]
    public void PlayerPresentationAndZeroPacingAreAcceptedForHuman()
    {
        DemoOptions options = DemoOptions.Parse(
            [
                "--human", "Alice",
                "--presentation", "PLAYER",
                "--presentation-interval-ms", "0",
            ]);

        Assert.Equal(BoardIds.Alice, options.HumanActorId);
        Assert.Equal(PresentationMode.Player, options.PresentationMode);
        Assert.Equal(TimeSpan.Zero, options.PresentationInterval);
        Assert.Contains("human-alice-presentation-player", options.OutputDirectory);
    }

    [Fact]
    public void CustomDeveloperPacingIsAccepted()
    {
        DemoOptions options = DemoOptions.Parse(
            ["--presentation", "DeVeLoPeR", "--presentation-interval-ms", "1250"]);

        Assert.Equal(PresentationMode.Developer, options.PresentationMode);
        Assert.Equal(TimeSpan.FromMilliseconds(1250), options.PresentationInterval);
    }

    [Fact]
    public void HumanAliceMakesBobTheDefaultMemoryBackend()
    {
        DemoOptions options = DemoOptions.Parse(
            [
                "--human", "alice",
                "--alice-backend", "codex",
                "--bob-backend", "openai",
                "--bob-model", "bob-model",
            ]);

        Assert.Equal(options.BobBackend, options.MemoryBackend);
    }

    [Theory]
    [InlineData("charlie")]
    [InlineData("")]
    [InlineData(" ")]
    public void InvalidHumanIsRejected(string value)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            DemoOptions.Parse(["--human", value]));

        Assert.Contains("--human", error.Message);
    }

    [Theory]
    [InlineData("spectator")]
    [InlineData("")]
    [InlineData(" ")]
    public void InvalidPresentationModeIsRejected(string value)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            DemoOptions.Parse(["--presentation", value]));

        Assert.Contains("--presentation", error.Message);
    }

    [Theory]
    [InlineData()]
    [InlineData("--human", "none")]
    public void PlayerPresentationRequiresHuman(params string[] prefix)
    {
        string[] args = [.. prefix, "--presentation", "player"];

        ArgumentException error = Assert.Throws<ArgumentException>(() => DemoOptions.Parse(args));

        Assert.Contains("requires --human alice or bob", error.Message);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("not-a-number")]
    [InlineData("2147483648")]
    [InlineData("9223372036854775807")]
    public void InvalidPresentationIntervalIsRejected(string value)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            DemoOptions.Parse(["--presentation-interval-ms", value]));

        Assert.Contains("--presentation-interval-ms", error.Message);
    }

    [Fact]
    public void UnknownOptionIsRejected()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            DemoOptions.Parse(["--presentaton", "developer"]));

        Assert.Contains("Unknown option '--presentaton'", error.Message);
    }

    [Fact]
    public void ExistingBackendOptionsRemainAvailableForHumanActorConfiguration()
    {
        DemoOptions options = DemoOptions.Parse(
            [
                "--human", "alice",
                "--alice-backend", "openai",
                "--alice-model", "unused-human-model",
                "--bob-backend", "codex",
                "--bob-model", "ai-model",
                "--memory-backend", "deepseek",
                "--memory-model", "memory-model",
            ]);

        Assert.Equal(new DemoBackendOptions("openai", "unused-human-model"), options.AliceBackend);
        Assert.Equal(new DemoBackendOptions("codex", "ai-model"), options.BobBackend);
        Assert.Equal(new DemoBackendOptions("deepseek", "memory-model"), options.MemoryBackend);
    }

    [Fact]
    public void HelpDescribesLiveSessionOptionsAndConstraint()
    {
        Assert.Contains("--human alice|bob|none", DemoOptions.HelpText);
        Assert.Contains("--presentation player|developer", DemoOptions.HelpText);
        Assert.Contains("player requires a Human actor", DemoOptions.HelpText);
        Assert.Contains("--presentation-interval-ms N", DemoOptions.HelpText);
        Assert.Contains("default: 250", DemoOptions.HelpText);
        Assert.Contains("Human-side overrides are not instantiated", DemoOptions.HelpText);
        Assert.Contains("default: first actual AI backend", DemoOptions.HelpText);
    }
}
