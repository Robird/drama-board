using DramaBoard.FirstBoard;
using DramaBoard.FirstBoard.Demo;
using DramaBoard.Host;
using DramaBoard.Kernel.Time;
using DramaBoard.Player.Llm;

try
{
    DemoOptions options = DemoOptions.Parse(args);
    Directory.CreateDirectory(options.OutputDirectory);
    Console.WriteLine(
        $"DramaBoard FirstBoard: Alice={options.AliceBackend.Backend}/" +
        $"{options.AliceBackend.Model}; Bob={options.BobBackend.Backend}/" +
        $"{options.BobBackend.Model}");
    Console.WriteLine($"Output: {options.OutputDirectory}");

    using var overallTimeout = new CancellationTokenSource(options.OverallTimeout);
    var traceSink = new DemoTraceSink(options.OutputDirectory);
    await using DemoBackend aliceBackend = DemoBackend.Create(options, options.AliceBackend);
    DemoBackend? separateBobBackend = null;

    try
    {
        ILlmChatBackend bobBackend;
        if (options.BobBackend == options.AliceBackend)
        {
            bobBackend = aliceBackend.Client;
        }
        else
        {
            separateBobBackend = DemoBackend.Create(options, options.BobBackend);
            bobBackend = separateBobBackend.Client;
        }

        var aliceLlm = new LlmPlayerDriver(
            new CharacterCard(
                "爱丽丝",
                "谨慎、敏锐，不轻易相信别人；在压力下仍保持克制",
                "在地窖封闭前找到公爵夫人的密信；必要时与鲍勃交涉",
                "简短、克制，习惯用试探性问题"),
            """
            我在酒馆。传闻公爵夫人的密信锁在地窖的箱子里，黄铜钥匙最近出现在集市。
            地窖会在一小时后永久封闭。鲍勃常在集市倒卖稀罕物，我不确定他会帮我还是借机要挟。
            我随身带着两枚可分别交付的银币（silver-coin-1、silver-coin-2），它们是有限而真实的交易筹码。
            鲍勃离开摊位后通常会很快回集市；若钥匙和他都不在，应在集市至少等一个完整路程时长，避免彼此错身。
            与鲍勃同场时优先交谈；听见他对我说话后，应直接回应他的具体条件或试探。
            口头承诺不会改变世界；若接受交易，应使用 give 逐件实际交付，且只承诺自己真正持有的物品。
            我应根据眼前能做的行动逐步接近钥匙与地窖；若目标已经无望或完成，就长时间等待让本场结束。
            """,
            aliceBackend.Client,
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
            开箱成功后，公爵夫人的密信会成为我实际持有的 duchess-letter；口头承诺不会转移它。
            若接受交易，应使用 give 逐件交付密信或钥匙，并根据对方是否实际交付银币决定是否履约或背叛。
            我应只使用眼前允许的行动；若局面已经结束，就长时间等待让本场结束。
            """,
            bobBackend,
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
        if (separateBobBackend is not null)
        {
            await separateBobBackend.DisposeAsync();
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
