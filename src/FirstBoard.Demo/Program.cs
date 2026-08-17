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
        $"{options.MemoryBackend.Model}; MemoryMode={options.MemoryMaintenanceMode}");
    Console.WriteLine($"Output: {options.OutputDirectory}");

    using var overallTimeout = new CancellationTokenSource(options.OverallTimeout);
    var traceSink = new DemoTraceSink(options.OutputDirectory);
    var profiler = new DemoLlmProfiler(options.OutputDirectory);
    await using DemoBackend aliceBackend = DemoBackend.Create(options, options.AliceBackend);
    DemoBackend? separateBobBackend = null;
    DemoBackend? separateMemoryBackend = null;
    LlmPlayerDriver? aliceLlm = null;
    LlmPlayerDriver? bobLlm = null;

    try
    {
        ILlmChatBackend rawBobBackend;
        if (options.BobBackend == options.AliceBackend)
        {
            rawBobBackend = aliceBackend.Client;
        }
        else
        {
            separateBobBackend = DemoBackend.Create(options, options.BobBackend);
            rawBobBackend = separateBobBackend.Client;
        }

        ILlmChatBackend rawMemoryBackend;
        if (options.MemoryBackend == options.AliceBackend)
        {
            rawMemoryBackend = aliceBackend.Client;
        }
        else if (options.MemoryBackend == options.BobBackend)
        {
            rawMemoryBackend = rawBobBackend;
        }
        else
        {
            separateMemoryBackend = DemoBackend.Create(options, options.MemoryBackend);
            rawMemoryBackend = separateMemoryBackend.Client;
        }

        MemoryBank aliceMemory = DemoMemoryProfile.Alice();
        MemoryBank bobMemory = DemoMemoryProfile.Bob();
        ILlmChatBackend aliceDecisionBackend = profiler.Wrap(
            aliceBackend.Client,
            Descriptor(BoardIds.Alice, "role-decision", shardKey: null, options.AliceBackend));
        ILlmChatBackend bobDecisionBackend = profiler.Wrap(
            rawBobBackend,
            Descriptor(BoardIds.Bob, "role-decision", shardKey: null, options.BobBackend));

        aliceLlm = new LlmPlayerDriver(
            new CharacterCard(
                "爱丽丝",
                "谨慎、敏锐，不轻易相信别人；在压力下仍保持克制",
                "查明公爵夫人密信的下落与内容，并据此保护自己的长期利益",
                "简短、克制，习惯用试探性问题"),
            aliceMemory,
            aliceDecisionBackend,
            DemoMemoryProfile.Maintainers(
                aliceMemory,
                shardKey => profiler.Wrap(
                    rawMemoryBackend,
                    Descriptor(BoardIds.Alice, "memory-maintenance", shardKey, options.MemoryBackend))),
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
            ],
            options.MemoryMaintenanceMode);
        bobLlm = new LlmPlayerDriver(
            new CharacterCard(
                "鲍勃",
                "务实、机会主义，但并非冷酷；喜欢掌握谈判筹码",
                "利用黄铜钥匙和密信线索取得收益，同时避免把自己困在无法兑现的交易中",
                "直率，偶尔讥讽，谈条件时毫不含糊"),
            bobMemory,
            bobDecisionBackend,
            DemoMemoryProfile.Maintainers(
                bobMemory,
                shardKey => profiler.Wrap(
                    rawMemoryBackend,
                    Descriptor(BoardIds.Bob, "memory-maintenance", shardKey, options.MemoryBackend))),
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
            ],
            options.MemoryMaintenanceMode);
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
        await Task.WhenAll(
            aliceLlm.FlushMemoryAsync(overallTimeout.Token),
            bobLlm.FlushMemoryAsync(overallTimeout.Token));
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
        if (aliceLlm is not null)
        {
            await aliceLlm.DisposeAsync();
        }

        if (bobLlm is not null)
        {
            await bobLlm.DisposeAsync();
        }

        if (separateMemoryBackend is not null)
        {
            await separateMemoryBackend.DisposeAsync();
        }

        if (separateBobBackend is not null)
        {
            await separateBobBackend.DisposeAsync();
        }

        profiler.WriteSummary(options);
    }

    DemoLlmCallDescriptor Descriptor(
        string actorId,
        string purpose,
        string? shardKey,
        DemoBackendOptions backend) =>
        new(
            actorId,
            purpose,
            shardKey,
            backend.Backend,
            backend.Model,
            backend.Backend == "codex"
                ? options.ReasoningEffort ?? "provider-default"
                : "provider-default");
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
