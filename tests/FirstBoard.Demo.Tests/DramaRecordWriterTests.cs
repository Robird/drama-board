using DramaBoard.FirstBoard.Demo;
using DramaBoard.Host;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.FirstBoard.Demo.Tests;

public sealed class DramaRecordWriterTests
{
    [Theory]
    [InlineData(
        "alice",
        "- 爱丽丝 Driver：Human / Console",
        "- 鲍勃 Driver：LLM / codex / ai-model")]
    [InlineData(
        "bob",
        "- 鲍勃 Driver：Human / Console",
        "- 爱丽丝 Driver：LLM / codex / ai-model")]
    public void HumanAndAiDriversAreDescribedAccurately(
        string humanActor,
        string expectedHumanLine,
        string expectedAiLine)
    {
        using var output = new TempOutputDirectory();
        string aiActor = humanActor == "alice" ? "bob" : "alice";
        DemoOptions options = DemoOptions.Parse(
            [
                "--output", output.Path,
                "--human", humanActor,
                "--presentation", "player",
                "--presentation-interval-ms", "80",
                $"--{humanActor}-backend", "openai",
                $"--{humanActor}-model", "unused-human-model",
                $"--{aiActor}-backend", "codex",
                $"--{aiActor}-model", "ai-model",
            ]);
        ScenarioInstance scenario = ScenarioInstance.CreateDefault(options.WorldSeed);

        string recordPath = DramaRecordWriter.Write(
            options,
            scenario,
            EmptyCapture(scenario),
            traces: [],
            budgetForcedCount: 0);
        string text = File.ReadAllText(recordPath);

        Assert.Contains(expectedHumanLine, text);
        Assert.Contains(expectedAiLine, text);
        Assert.Contains("- Presentation：player；Human=" + humanActor + "；interval=80ms", text);
        Assert.DoesNotContain("unused-human-model", text);
        Assert.Contains("- 成功解析的 LLM turn：0", text);
        Assert.Contains("（本局没有 LLM 内心轨迹。）", text);
    }

    [Fact]
    public void AllAiRecordListsBothActualBackendsAndDeveloperPresentation()
    {
        using var output = new TempOutputDirectory();
        DemoOptions options = DemoOptions.Parse(
            [
                "--output", output.Path,
                "--alice-model", "alice-ai",
                "--bob-model", "bob-ai",
            ]);
        ScenarioInstance scenario = ScenarioInstance.CreateDefault(options.WorldSeed);

        string recordPath = DramaRecordWriter.Write(
            options,
            scenario,
            EmptyCapture(scenario),
            traces: [],
            budgetForcedCount: 0);
        string text = File.ReadAllText(recordPath);

        Assert.Contains("- 爱丽丝 Driver：LLM / codex / alice-ai", text);
        Assert.Contains("- 鲍勃 Driver：LLM / codex / bob-ai", text);
        Assert.Contains("- Presentation：developer；Human=none；interval=250ms", text);
    }

    [Fact]
    public void CanceledRecordDescribesThePreservedCommittedPrefix()
    {
        using var output = new TempOutputDirectory();
        DemoOptions options = DemoOptions.Parse(["--output", output.Path]);
        ScenarioInstance scenario = ScenarioInstance.CreateDefault(options.WorldSeed);
        FirstBoardWorld initial = scenario.CreateInitialWorld();
        var journal = new InMemoryJournal<FirstBoardFact>(FirstBoardScenario.LineageId);
        var capture = new LiveSessionCanceledCapture(
            initial,
            initial,
            new WorldVersion(FirstBoardScenario.LineageId, 0),
            ModelTime.Zero,
            journal);

        string recordPath = DramaRecordWriter.WriteCanceled(
            options,
            scenario,
            capture,
            traces: [],
            budgetForcedCount: 0);
        string text = File.ReadAllText(recordPath);

        Assert.Contains("- 结束：Canceled @ 0ms", text);
        Assert.Contains("- 世界 transition：0", text);
    }

    private static BoardRunCapture EmptyCapture(ScenarioInstance scenario)
    {
        FirstBoardWorld initial = scenario.CreateInitialWorld();
        var journal = new InMemoryJournal<FirstBoardFact>(FirstBoardScenario.LineageId);
        var result = new HostRunResult<FirstBoardWorld>(
            initial,
            new WorldVersion(FirstBoardScenario.LineageId, 0),
            initial.Now,
            StepStatus.Exhausted,
            CommittedTransitionCount: 0);
        return new BoardRunCapture(initial, result, journal);
    }

    private sealed class TempOutputDirectory : IDisposable
    {
        public TempOutputDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "dramaboard-record-tests",
                Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
