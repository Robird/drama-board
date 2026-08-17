using System.Globalization;
using System.Text;
using DramaBoard.Player.Llm;
using DramaBoard.Protocol;

namespace DramaBoard.FirstBoard.Demo;

internal sealed class DemoTraceSink
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly string _outputDirectory;
    private readonly string _tracePath;
    private readonly Dictionary<string, int> _actorTurnCounts = new(StringComparer.Ordinal);
    private readonly List<LlmTurnTrace> _traces = [];

    public DemoTraceSink(string outputDirectory)
    {
        _outputDirectory = outputDirectory;
        _tracePath = Path.Combine(outputDirectory, "turns.md");
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(_tracePath, "# LLM turn trace\n\n", Utf8NoBom);
    }

    public IReadOnlyList<LlmTurnTrace> Traces => _traces.AsReadOnly();

    public void Record(LlmTurnTrace trace)
    {
        int actorTurn = _actorTurnCounts.GetValueOrDefault(trace.Request.ActorId) + 1;
        _actorTurnCounts[trace.Request.ActorId] = actorTurn;
        _traces.Add(trace);

        string actorDirectory = Path.Combine(_outputDirectory, "memory", trace.Request.ActorId);
        Directory.CreateDirectory(actorDirectory);
        string memoryPath = Path.Combine(
            actorDirectory,
            $"{actorTurn:000}-{SafeFileName(trace.Request.DecisionId.Value)}.md");
        File.WriteAllText(memoryPath, trace.Memory + Environment.NewLine, Utf8NoBom);

        var text = new StringBuilder()
            .Append("## ").Append(_traces.Count.ToString("000", CultureInfo.InvariantCulture))
            .Append(" · ").Append(trace.Request.ActorId)
            .Append(" · ").AppendLine(trace.Request.DecisionId.Value)
            .Append("- 模型时间：").Append(trace.Request.ModelTimeMs.ToString(CultureInfo.InvariantCulture))
            .Append("ms；解析尝试：").AppendLine(trace.AttemptCount.ToString(CultureInfo.InvariantCulture))
            .Append("- 行动：").AppendLine(DramaRecordWriter.FormatIntent(trace.Decision.Intent))
            .AppendLine()
            .AppendLine("### 独白")
            .AppendLine(trace.Monologue)
            .AppendLine()
            .AppendLine("### 台词")
            .AppendLine(trace.Dialogue ?? "（无）")
            .AppendLine()
            .AppendLine("### 提交后的记忆")
            .AppendLine(trace.Memory)
            .AppendLine();
        File.AppendAllText(_tracePath, text.ToString(), Utf8NoBom);
    }

    private static string SafeFileName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }
}

internal sealed class DecisionBudgetPlayerDriver : DramaBoard.Host.IPlayerDriver
{
    private const long SceneEndingWaitMs = 5_000_000;
    private readonly DramaBoard.Host.IPlayerDriver _inner;
    private readonly int _maxTurns;
    private int _turns;

    public DecisionBudgetPlayerDriver(DramaBoard.Host.IPlayerDriver inner, int maxTurns)
    {
        _inner = inner;
        _maxTurns = maxTurns;
    }

    public int ForcedSceneEndCount { get; private set; }

    public ValueTask<PlayerDecision> DecideAsync(
        DecisionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_turns >= _maxTurns)
        {
            ForcedSceneEndCount = checked(ForcedSceneEndCount + 1);
            return ValueTask.FromResult(new PlayerDecision(
                request.DecisionId,
                request.BasedOnWorldVersion,
                request.LineageId,
                new Intent(ActionKinds.Wait, DurationMs: SceneEndingWaitMs)));
        }

        _turns = checked(_turns + 1);
        return _inner.DecideAsync(request, cancellationToken);
    }
}
