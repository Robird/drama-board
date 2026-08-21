using DramaBoard.FirstBoard;
using DramaBoard.FirstBoard.Demo;
using DramaBoard.Kernel.Time;
using DramaBoard.Player;
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
    ScenarioInstance scenarioInstance = ScenarioInstance.CreateDefault(options.WorldSeed);
    var traceSink = new DemoTraceSink(options.OutputDirectory);
    var profiler = new DemoLlmProfiler(options.OutputDirectory);
    var manifest = new DemoRunManifestWriter(
        options.OutputDirectory,
        options,
        scenarioInstance);
    DemoBackend? aliceBackend = null;
    DemoBackend? separateBobBackend = null;
    DemoBackend? separateMemoryBackend = null;
    LlmPlayerDriver? aliceLlm = null;
    LlmPlayerDriver? bobLlm = null;

    try
    {
        aliceBackend = DemoBackend.Create(options, options.AliceBackend);
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

        ScenarioActorDefinition aliceDefinition = scenarioInstance.Definition.Actor(BoardIds.Alice);
        ScenarioActorDefinition bobDefinition = scenarioInstance.Definition.Actor(BoardIds.Bob);
        MemoryBank aliceMemory = DemoMemoryProfile.Create(aliceDefinition);
        MemoryBank bobMemory = DemoMemoryProfile.Create(bobDefinition);
        ILlmChatBackend aliceDecisionBackend = profiler.Wrap(
            aliceBackend.Client,
            Descriptor(BoardIds.Alice, "role-decision", shardKey: null, options.AliceBackend));
        ILlmChatBackend bobDecisionBackend = profiler.Wrap(
            rawBobBackend,
            Descriptor(BoardIds.Bob, "role-decision", shardKey: null, options.BobBackend));

        aliceLlm = new LlmPlayerDriver(
            new CharacterCard(
                aliceDefinition.Role.Name,
                aliceDefinition.Role.Traits,
                aliceDefinition.Role.Goal,
                aliceDefinition.Role.Voice),
            aliceMemory,
            aliceDecisionBackend,
            DemoMemoryProfile.Maintainers(
                aliceMemory,
                shardKey => profiler.Wrap(
                    rawMemoryBackend,
                    Descriptor(BoardIds.Alice, "memory-maintenance", shardKey, options.MemoryBackend))),
            traceSink.Record,
            Materials(aliceDefinition),
            options.MemoryMaintenanceMode);
        bobLlm = new LlmPlayerDriver(
            new CharacterCard(
                bobDefinition.Role.Name,
                bobDefinition.Role.Traits,
                bobDefinition.Role.Goal,
                bobDefinition.Role.Voice),
            bobMemory,
            bobDecisionBackend,
            DemoMemoryProfile.Maintainers(
                bobMemory,
                shardKey => profiler.Wrap(
                    rawMemoryBackend,
                    Descriptor(BoardIds.Bob, "memory-maintenance", shardKey, options.MemoryBackend))),
            traceSink.Record,
            Materials(bobDefinition),
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
            scenarioInstance,
            new ModelTime(options.UntilModelTimeMs),
            cancellationToken: overallTimeout.Token);
        await Task.WhenAll(
            aliceLlm.FlushMemoryAsync(overallTimeout.Token),
            bobLlm.FlushMemoryAsync(overallTimeout.Token));
        string recordPath = DramaRecordWriter.Write(
            options,
            scenarioInstance,
            capture,
            traceSink.Traces,
            alice.ForcedSceneEndCount + bob.ForcedSceneEndCount);
        manifest.Complete(
            capture,
            traceSink.Traces.Count,
            alice.ForcedSceneEndCount + bob.ForcedSceneEndCount);

        Console.WriteLine(
            $"Completed: {capture.Result.Status}; transitions={capture.Journal.Batches.Count}; " +
            $"llmTurns={traceSink.Traces.Count}");
        Console.WriteLine($"Drama record: {recordPath}");
    }
    catch (Exception exception)
    {
        manifest.Fail(exception);
        throw;
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

        if (aliceBackend is not null)
        {
            await aliceBackend.DisposeAsync();
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

    static IReadOnlyList<ReferenceMaterial> Materials(ScenarioActorDefinition actor) =>
        [
            .. actor.Role.ReferenceMaterials.Select(material => new ReferenceMaterial(
                material.Id,
                material.Source,
                material.Content)),
        ];
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
