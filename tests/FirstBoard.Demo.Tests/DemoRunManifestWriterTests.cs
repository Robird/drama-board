using System.Text.Json;
using DramaBoard.FirstBoard.Demo;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Time;

namespace DramaBoard.FirstBoard.Demo.Tests;

public sealed class DemoRunManifestWriterTests
{
    [Theory]
    [InlineData("alice", BoardIds.Alice, BoardIds.Bob)]
    [InlineData("bob", BoardIds.Bob, BoardIds.Alice)]
    public void HumanRosterAndPresentationAreRecordedWithoutLlmIdentity(
        string humanOption,
        string humanActorId,
        string aiActorId)
    {
        using var output = new TempOutputDirectory();
        DemoOptions options = DemoOptions.Parse(
            [
                "--output", output.Path,
                "--human", humanOption,
                "--presentation", "player",
                "--presentation-interval-ms", "375",
                $"--{humanOption}-backend", "openai",
                $"--{humanOption}-model", "unused-human-model",
            ]);
        var scenario = ScenarioInstance.CreateDefault(options.WorldSeed);

        _ = new DemoRunManifestWriter(output.Path, options, scenario);

        using JsonDocument document = output.ReadManifest();
        JsonElement root = document.RootElement;
        Assert.Equal("dramaboard.run-manifest/2", root.GetProperty("schema").GetString());
        JsonElement presentation = root.GetProperty("presentation");
        Assert.Equal(humanActorId, presentation.GetProperty("humanActorId").GetString());
        Assert.Equal("player", presentation.GetProperty("mode").GetString());
        Assert.Equal(375, presentation.GetProperty("intervalMs").GetInt64());

        JsonElement[] players = [.. root.GetProperty("players").EnumerateArray()];
        JsonElement human = players.Single(player =>
            player.GetProperty("actorId").GetString() == humanActorId);
        Assert.Equal("human", human.GetProperty("driverKind").GetString());
        Assert.Equal(JsonValueKind.Null, human.GetProperty("backend").ValueKind);
        Assert.Equal(JsonValueKind.Null, human.GetProperty("model").ValueKind);
        Assert.Equal(JsonValueKind.Null, human.GetProperty("thinkingEffort").ValueKind);

        JsonElement ai = players.Single(player =>
            player.GetProperty("actorId").GetString() == aiActorId);
        Assert.Equal("llm", ai.GetProperty("driverKind").GetString());
        Assert.False(string.IsNullOrWhiteSpace(ai.GetProperty("backend").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(ai.GetProperty("model").GetString()));
    }

    [Fact]
    public void AllAiManifestHasNoHumanViewpoint()
    {
        using var output = new TempOutputDirectory();
        DemoOptions options = DemoOptions.Parse(["--output", output.Path]);
        var scenario = ScenarioInstance.CreateDefault(options.WorldSeed);

        _ = new DemoRunManifestWriter(output.Path, options, scenario);

        using JsonDocument document = output.ReadManifest();
        JsonElement root = document.RootElement;
        Assert.Equal(
            JsonValueKind.Null,
            root.GetProperty("presentation").GetProperty("humanActorId").ValueKind);
        Assert.Equal("developer", root.GetProperty("presentation").GetProperty("mode").GetString());
        Assert.All(
            root.GetProperty("players").EnumerateArray(),
            player => Assert.Equal("llm", player.GetProperty("driverKind").GetString()));
    }

    [Fact]
    public void ConfigurationHashIgnoresUnusedHumanBackendOverride()
    {
        DemoOptions baseline = DemoOptions.Parse(
            [
                "--human", "alice",
                "--presentation", "player",
                "--alice-backend", "codex",
                "--alice-model", "unused-one",
                "--bob-backend", "codex",
                "--bob-model", "actual-ai",
                "--memory-backend", "codex",
                "--memory-model", "actual-memory",
            ]);
        DemoOptions changedHumanOnly = baseline with
        {
            AliceBackend = new DemoBackendOptions("openai", "unused-two"),
            BaseUrl = "https://unused-human-endpoint.example/v1?secret=ignored",
        };

        Assert.Equal(ConfigurationHash(baseline), ConfigurationHash(changedHumanOnly));
    }

    [Fact]
    public void ConfigurationHashIncludesActualAiAndPresentationConfiguration()
    {
        DemoOptions baseline = DemoOptions.Parse(
            ["--human", "alice", "--presentation", "player"]);
        DemoOptions changedAi = baseline with
        {
            BobBackend = new DemoBackendOptions("codex", "changed-ai-model"),
        };
        DemoOptions changedMode = baseline with
        {
            PresentationMode = PresentationMode.Developer,
        };
        DemoOptions changedInterval = baseline with
        {
            PresentationInterval = TimeSpan.FromMilliseconds(999),
        };
        string hash = ConfigurationHash(baseline);

        Assert.NotEqual(hash, ConfigurationHash(changedAi));
        Assert.NotEqual(hash, ConfigurationHash(changedMode));
        Assert.NotEqual(hash, ConfigurationHash(changedInterval));
    }

    [Fact]
    public void CanceledRunHasExplicitTerminalStatus()
    {
        using var output = new TempOutputDirectory();
        DemoOptions options = DemoOptions.Parse(["--output", output.Path]);
        var scenario = ScenarioInstance.CreateDefault(options.WorldSeed);
        var manifest = new DemoRunManifestWriter(output.Path, options, scenario);

        FirstBoardWorld initial = scenario.CreateInitialWorld();
        var canceled = new LiveSessionCanceledCapture(
            initial,
            initial,
            new WorldVersion(FirstBoardScenario.LineageId, 0),
            ModelTime.Zero,
            new InMemoryJournal<FirstBoardFact>(FirstBoardScenario.LineageId));
        manifest.Cancel(canceled, llmTurnCount: 2, forcedSceneEndCount: 1);

        using JsonDocument document = output.ReadManifest();
        JsonElement root = document.RootElement;
        Assert.Equal("canceled", root.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("errorType").ValueKind);
        Assert.NotEqual(
            JsonValueKind.Null,
            root.GetProperty("finishedAtUtc").ValueKind);
        JsonElement result = root.GetProperty("result");
        Assert.Equal("Canceled", result.GetProperty("status").GetString());
        Assert.Equal(0, result.GetProperty("worldTransitionCount").GetInt32());
        Assert.Equal(2, result.GetProperty("llmTurnCount").GetInt32());
    }

    [Fact]
    public void CancellationAfterSimulationCompletionPreservesItsResultSummary()
    {
        using var output = new TempOutputDirectory();
        DemoOptions options = DemoOptions.Parse(["--output", output.Path]);
        var scenario = ScenarioInstance.CreateDefault(options.WorldSeed);
        var manifest = new DemoRunManifestWriter(output.Path, options, scenario);
        FirstBoardWorld initial = scenario.CreateInitialWorld();
        var journal = new InMemoryJournal<FirstBoardFact>(FirstBoardScenario.LineageId);
        var result = new DramaBoard.Host.HostRunResult<FirstBoardWorld>(
            initial,
            new WorldVersion(FirstBoardScenario.LineageId, 0),
            initial.Now,
            StepStatus.BoundaryReached,
            CommittedTransitionCount: 0);

        manifest.Cancel(
            new BoardRunCapture(initial, result, journal),
            llmTurnCount: 1,
            forcedSceneEndCount: 0);

        using JsonDocument document = output.ReadManifest();
        JsonElement summary = document.RootElement.GetProperty("result");
        Assert.Equal("CanceledAfterBoundaryReached", summary.GetProperty("status").GetString());
        Assert.Equal(0, summary.GetProperty("worldTransitionCount").GetInt32());
    }

    private static string ConfigurationHash(DemoOptions options)
    {
        using var output = new TempOutputDirectory();
        var scenario = ScenarioInstance.CreateDefault(options.WorldSeed);
        _ = new DemoRunManifestWriter(output.Path, options, scenario);
        using JsonDocument document = output.ReadManifest();
        return document.RootElement.GetProperty("runConfigurationSha256").GetString()!;
    }

    private sealed class TempOutputDirectory : IDisposable
    {
        public TempOutputDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "dramaboard-manifest-tests",
                Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public JsonDocument ReadManifest() =>
            JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(Path, "run-manifest.json")));

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
