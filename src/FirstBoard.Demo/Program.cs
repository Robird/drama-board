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
        $"{options.BobBackend.Model}; Memory={options.MemoryBackend.Backend}/" +
        $"{options.MemoryBackend.Model}");
    Console.WriteLine($"Output: {options.OutputDirectory}");

    using var overallTimeout = new CancellationTokenSource(options.OverallTimeout);
    var traceSink = new DemoTraceSink(options.OutputDirectory);
    await using DemoBackend aliceBackend = DemoBackend.Create(options, options.AliceBackend);
    DemoBackend? separateBobBackend = null;
    DemoBackend? separateMemoryBackend = null;

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

        ILlmChatBackend memoryBackend;
        if (options.MemoryBackend == options.AliceBackend)
        {
            memoryBackend = aliceBackend.Client;
        }
        else if (options.MemoryBackend == options.BobBackend)
        {
            memoryBackend = bobBackend;
        }
        else
        {
            separateMemoryBackend = DemoBackend.Create(options, options.MemoryBackend);
            memoryBackend = separateMemoryBackend.Client;
        }

        MemoryBank aliceMemory = DemoMemoryProfile.Alice();
        MemoryBank bobMemory = DemoMemoryProfile.Bob();

        var aliceLlm = new LlmPlayerDriver(
            new CharacterCard(
                "爱丽丝",
                "谨慎、敏锐，不轻易相信别人；在压力下仍保持克制",
                "查明公爵夫人密信的下落与内容，并据此保护自己的长期利益",
                "简短、克制，习惯用试探性问题"),
            aliceMemory,
            aliceBackend.Client,
            DemoMemoryProfile.Maintainers(aliceMemory, memoryBackend),
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
            bobMemory,
            bobBackend,
            DemoMemoryProfile.Maintainers(bobMemory, memoryBackend),
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
        if (separateMemoryBackend is not null)
        {
            await separateMemoryBackend.DisposeAsync();
        }

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
