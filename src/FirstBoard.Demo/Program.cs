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
                "查明公爵夫人密信的下落与内容，并据此保护自己的长期利益",
                "简短、克制，习惯用试探性问题"),
            """
            我在酒馆，准备按案情笔记去集市寻找钥匙和鲍勃。我目前打算遵守会面备忘，除非新情况使它明显不再合适。
            鲍勃常倒卖稀罕物，我不确定他会帮我还是借机要挟。我随身的两枚银币是有限而真实的交易筹码。
            与鲍勃同场时优先交谈；听见他对我说话后，应直接回应他的具体条件或试探。
            show 可以向同场角色展示自己持有的物品，让对方获得可验证证据，但不会转移所有权；若对方要求验货，可用它展示银币。
            observe 不指定物品时观察现场；指定自己持有的 targetObject 时会仔细检查该物品。拿到密信后应先这样确认真伪与内容，再决定是否支付余款。
            口头承诺不会改变世界；若接受交易，应使用 give 逐件实际交付，且只承诺自己真正持有的物品。
            我应根据眼前能做的行动逐步接近钥匙与地窖；若目标已经无望或完成，就长时间等待让本场结束。
            """,
            aliceBackend.Client,
            traceSink.Record,
            referenceMaterials:
            [
                new ReferenceMaterial(
                    "alice.case-notes",
                    "爱丽丝在酒馆根据零散传闻写下的案情笔记",
                    "公爵夫人的密信可能锁在地窖箱中；黄铜钥匙最近在集市出现；地窖门口公告称一小时后永久封闭。"),
                new ReferenceMaterial(
                    "alice.meeting-note",
                    "爱丽丝昨夜与鲍勃谈过后留下的会面备忘",
                    "先去集市摊位会面；若钥匙和鲍勃都不在，至少等待五分钟，避免与返回摊位的鲍勃错身。"),
            ]);
        var bobLlm = new LlmPlayerDriver(
            new CharacterCard(
                "鲍勃",
                "务实、机会主义，但并非冷酷；喜欢掌握谈判筹码",
                "利用黄铜钥匙和密信线索取得收益，同时避免把自己困在无法兑现的交易中",
                "直率，偶尔讥讽，谈条件时毫不含糊"),
            """
            我在集市，准备先确认摊位附近的钥匙是否还在。我目前愿意按会面备忘返回摊位等爱丽丝，但会根据风险和收益改主意。
            钥匙可能让我获得筹码，拖得太久也可能一无所获。与爱丽丝同场时优先交谈。
            听见爱丽丝对我说话后，应直接回应她的具体条件或试探，而不是继续独自行动。
            开箱成功后，公爵夫人的密信会成为我实际持有的 duchess-letter；口头承诺不会转移它。
            show 可以向同场角色展示自己持有的物品，让对方获得可验证证据，但不会转移所有权；若对方要求验货，应优先用它展示密信或钥匙。
            observe 只有在指定自己持有的 targetObject 时才能仔细检查该物品；展示给别人并不自动赋予对方持续检查权。
            若接受交易，应使用 give 逐件交付密信或钥匙，并根据对方是否实际交付银币决定是否履约或背叛。
            我应只使用眼前允许的行动；若局面已经结束，就长时间等待让本场结束。
            """,
            bobBackend,
            traceSink.Record,
            referenceMaterials:
            [
                new ReferenceMaterial(
                    "bob.lead-ledger",
                    "鲍勃自己的生意账本边角记录",
                    "摊位附近可能有一把黄铜钥匙；爱丽丝正在追查地窖中的密信；地窖门口公告称一小时后封闭。"),
                new ReferenceMaterial(
                    "bob.meeting-note",
                    "鲍勃记下的昨夜会面安排",
                    "若先拿钥匙去地窖，开箱后回集市摊位至少等待十分钟；爱丽丝会先去摊位寻找鲍勃。"),
            ]);
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
