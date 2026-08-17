using DramaBoard.FirstBoard;
using DramaBoard.FirstBoard.Demo;
using DramaBoard.Host;
using DramaBoard.Kernel.Time;
using DramaBoard.Player.Llm;

try
{
    DemoOptions options = DemoOptions.Parse(args);
    Directory.CreateDirectory(options.OutputDirectory);
    Console.WriteLine($"DramaBoard FirstBoard: {options.Backend} / {options.Model}");
    Console.WriteLine($"Output: {options.OutputDirectory}");

    using var overallTimeout = new CancellationTokenSource(options.OverallTimeout);
    var traceSink = new DemoTraceSink(options.OutputDirectory);
    HttpClient? httpClient = null;
    CodexAppServerBackend? codexBackend = null;
    ILlmChatBackend backend;
    if (options.Backend == "codex")
    {
        codexBackend = new CodexAppServerBackend(new CodexAppServerOptions(
            options.CodexCommand,
            options.Model,
            WorkingDirectory: Path.GetTempPath(),
            options.ReasoningEffort,
            options.RequestTimeout));
        backend = codexBackend;
    }
    else
    {
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            throw new InvalidOperationException(
                "OpenAI-compatible mode requires --base-url or DEEPSEEK_BASE_URL/BASE_URL.");
        }

        string? apiKey = Environment.GetEnvironmentVariable(options.ApiKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"OpenAI-compatible mode cannot read environment variable " +
                $"'{options.ApiKeyEnvironmentVariable}'.");
        }

        httpClient = new HttpClient { Timeout = options.RequestTimeout };
        backend = new OpenAiCompatBackend(httpClient, new Uri(options.BaseUrl), apiKey, options.Model);
    }

    try
    {
        var aliceLlm = new LlmPlayerDriver(
            new CharacterCard(
                "爱丽丝",
                "谨慎、敏锐，不轻易相信别人；在压力下仍保持克制",
                "在地窖封闭前找到公爵夫人的密信；必要时与鲍勃交涉",
                "简短、克制，习惯用试探性问题"),
            """
            我在酒馆。传闻公爵夫人的密信锁在地窖的箱子里，黄铜钥匙最近出现在集市。
            地窖会在一小时后永久封闭。鲍勃常在集市倒卖稀罕物，我不确定他会帮我还是借机要挟。
            鲍勃离开摊位后通常会很快回集市；若钥匙和他都不在，应在集市至少等一个完整路程时长，避免彼此错身。
            与鲍勃同场时优先交谈；听见他对我说话后，应直接回应他的具体条件或试探。
            我应根据眼前能做的行动逐步接近钥匙与地窖；若目标已经无望或完成，就长时间等待让本场结束。
            """,
            backend,
            traceSink.Record);
        var bobLlm = new LlmPlayerDriver(
            new CharacterCard(
                "鲍勃",
                "务实、机会主义，但并非冷酷；喜欢掌握谈判筹码",
                "拿到黄铜钥匙并决定用它换取利益，或在关键时刻帮助爱丽丝",
                "直率，偶尔讥讽，谈条件时毫不含糊"),
            """
            我在集市。摊位附近有一把黄铜钥匙，爱丽丝似乎在追查一封与地窖有关的密信。
            地窖会在一小时后封闭。钥匙可能让我获得筹码，但拖得太久也会一无所获。
            若我用钥匙打开箱子，应立即回集市摊位并至少等爱丽丝一个完整路程时长；与她同场时优先交谈。
            听见爱丽丝对我说话后，应直接回应她的具体条件或试探，而不是继续独自行动。
            我应只使用眼前允许的行动；若局面已经结束，就长时间等待让本场结束。
            """,
            backend,
            traceSink.Record);
        var alice = new DecisionBudgetPlayerDriver(aliceLlm, options.MaxTurnsPerActor);
        var bob = new DecisionBudgetPlayerDriver(bobLlm, options.MaxTurnsPerActor);
        var drivers = new Dictionary<string, IPlayerDriver>(StringComparer.Ordinal)
        {
            [BoardIds.Alice] = alice,
            [BoardIds.Bob] = bob,
        };

        BoardRunCapture capture = await FirstBoardScenario.RunAsync(
            drivers,
            options.WorldSeed,
            new ModelTime(options.UntilModelTimeMs),
            cancellationToken: overallTimeout.Token);
        string recordPath = DramaRecordWriter.Write(
            options,
            capture,
            traceSink.Traces,
            alice.ForcedSceneEndCount + bob.ForcedSceneEndCount);

        Console.WriteLine(
            $"Completed: {capture.Result.StopReason}; events={capture.Journal.Events.Count}; " +
            $"llmTurns={traceSink.Traces.Count}");
        Console.WriteLine($"Drama record: {recordPath}");
    }
    finally
    {
        httpClient?.Dispose();
        if (codexBackend is not null)
        {
            await codexBackend.DisposeAsync();
        }
    }
}
catch (DemoHelpRequestedException)
{
    Console.WriteLine(DemoOptions.HelpText);
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Demo failed: {exception.GetType().Name}: {exception.Message}");
    Environment.ExitCode = 1;
}
