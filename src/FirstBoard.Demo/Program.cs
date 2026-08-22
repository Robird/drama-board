using DramaBoard.FirstBoard;
using DramaBoard.FirstBoard.Demo;
using DramaBoard.FirstBoard.Demo.Live;
using DramaBoard.Kernel.Time;

try
{
    DemoOptions options = DemoOptions.Parse(args);
    Directory.CreateDirectory(options.OutputDirectory);
    Console.WriteLine(
        $"DramaBoard FirstBoard Live: Alice={Driver(BoardIds.Alice, options.AliceBackend)}; " +
        $"Bob={Driver(BoardIds.Bob, options.BobBackend)}; " +
        $"Presentation={options.PresentationMode.ToString().ToLowerInvariant()}/" +
        $"{options.PresentationInterval.TotalMilliseconds:0}ms");
    Console.WriteLine($"Output: {options.OutputDirectory}");

    using var overallTimeout = new CancellationTokenSource(options.OverallTimeout);
    ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        overallTimeout.Cancel();
    };
    Console.CancelKeyPress += cancelHandler;
    ScenarioInstance scenarioInstance = ScenarioInstance.CreateDefault(options.WorldSeed);
    var traceSink = new DemoTraceSink(options.OutputDirectory);
    var profiler = new DemoLlmProfiler(options.OutputDirectory);
    var manifest = new DemoRunManifestWriter(
        options.OutputDirectory,
        options,
        scenarioInstance);

    try
    {
        await using DemoLlmComposition llmComposition =
            await DemoLlmComposition.CreateAsync(
                options,
                scenarioInstance,
                profiler,
                traceSink);
        var terminal = new TerminalUi();
        BoardRunCapture capture = await LiveSession.RunAsync(
            scenarioInstance,
            llmComposition.AiDrivers,
            options.HumanActorId,
            options.PresentationMode,
            terminal,
            new FixedIntervalPresentationPacer(options.PresentationInterval),
            new ModelTime(options.UntilModelTimeMs),
            overallTimeout.Token);
        await llmComposition.FlushMemoryAsync(overallTimeout.Token);
        string recordPath = DramaRecordWriter.Write(
            options,
            scenarioInstance,
            capture,
            traceSink.Traces,
            llmComposition.ForcedSceneEndCount);
        manifest.Complete(
            capture,
            traceSink.Traces.Count,
            llmComposition.ForcedSceneEndCount);

        Console.WriteLine(
            $"Completed: {capture.Result.Status}; transitions={capture.Journal.Batches.Count}; " +
            $"llmTurns={traceSink.Traces.Count}");
        Console.WriteLine($"Drama record: {recordPath}");
    }
    catch (OperationCanceledException) when (overallTimeout.IsCancellationRequested)
    {
        manifest.Cancel();
        Console.WriteLine("Live session canceled cleanly.");
    }
    catch (Exception exception)
    {
        manifest.Fail(exception);
        throw;
    }
    finally
    {
        Console.CancelKeyPress -= cancelHandler;
        profiler.WriteSummary(options);
    }

    string Driver(string actorId, DemoBackendOptions backend) =>
        actorId == options.HumanActorId
            ? "human/console"
            : $"llm/{backend.Backend}/{backend.Model}";
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
