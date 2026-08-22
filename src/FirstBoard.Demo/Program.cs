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
        BoardRunCapture? completedCapture = null;
        try
        {
            var terminal = new TerminalUi();
            completedCapture = await LiveSession.RunAsync(
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
                completedCapture,
                traceSink.Traces,
                llmComposition.ForcedSceneEndCount);
            manifest.Complete(
                completedCapture,
                traceSink.Traces.Count,
                llmComposition.ForcedSceneEndCount);

            Console.WriteLine(
                $"Completed: {completedCapture.Result.Status}; " +
                $"transitions={completedCapture.Journal.Batches.Count}; " +
                $"llmTurns={traceSink.Traces.Count}");
            Console.WriteLine($"Drama record: {recordPath}");
        }
        catch (LiveSessionCanceledException exception)
        {
            string recordPath = DramaRecordWriter.WriteCanceled(
                options,
                scenarioInstance,
                exception.Capture,
                traceSink.Traces,
                llmComposition.ForcedSceneEndCount);
            manifest.Cancel(
                exception.Capture,
                traceSink.Traces.Count,
                llmComposition.ForcedSceneEndCount);
            Console.WriteLine(
                $"Live session canceled cleanly after " +
                $"{exception.Capture.Journal.Batches.Count} committed transitions.");
            Console.WriteLine($"Partial drama record: {recordPath}");
        }
        catch (OperationCanceledException)
            when (overallTimeout.IsCancellationRequested && completedCapture is not null)
        {
            string recordPath = DramaRecordWriter.Write(
                options,
                scenarioInstance,
                completedCapture,
                traceSink.Traces,
                llmComposition.ForcedSceneEndCount);
            manifest.Cancel(
                completedCapture,
                traceSink.Traces.Count,
                llmComposition.ForcedSceneEndCount);
            Console.WriteLine(
                "Simulation completed, but cancellation interrupted final memory cleanup.");
            Console.WriteLine($"Authoritative drama record: {recordPath}");
        }
    }
    catch (OperationCanceledException) when (overallTimeout.IsCancellationRequested)
    {
        manifest.Cancel();
        Console.WriteLine("Live session canceled before a committed prefix was available.");
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
