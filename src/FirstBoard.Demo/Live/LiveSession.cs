using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using DramaBoard.Host;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Player;

namespace DramaBoard.FirstBoard.Demo.Live;

/// <summary>Composes the app-local Authority and Presentation loops for one new lineage.</summary>
internal static class LiveSession
{
    public static async Task<BoardRunCapture> RunAsync(
        ScenarioInstance instance,
        IReadOnlyDictionary<string, IPlayerDriver> aiDrivers,
        string? humanActorId,
        PresentationMode mode,
        ITerminalUi terminal,
        IPresentationPacer pacer,
        ModelTime notAfter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(aiDrivers);
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(pacer);
        FirstBoardWorld genesis = instance.CreateInitialWorld();
        var journal = new InMemoryJournal<FirstBoardFact>(FirstBoardScenario.LineageId);
        var genesisVersion = new WorldVersion(journal.LineageId, 0);
        var coordination = new LiveSessionCoordination(genesisVersion);
        IReadOnlyDictionary<string, IPlayerDriver> drivers = ComposeDrivers(
            instance,
            aiDrivers,
            humanActorId,
            coordination,
            terminal);
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> kernel =
            FirstBoardScenario.CreateKernel(drivers, instance, journal, genesis);
        Channel<CommittedTransition> channel = Channel.CreateUnbounded<CommittedTransition>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });
        var presentation = new FirstBoardPresentationLoop(
            instance,
            genesis,
            coordination,
            terminal,
            pacer,
            mode,
            humanActorId);
        using var authorityStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<HostRunResult<FirstBoardWorld>> authorityTask = LiveAuthorityLoop.RunAsync(
            kernel,
            journal,
            notAfter,
            channel.Writer,
            coordination,
            authorityStop.Token);
        // Presentation uses cancellation to stop effects/pacing, then silently drains the
        // already-published prefix so the replay frontier still catches Authority.
        Task presentationTask = presentation.RunAsync(channel.Reader, cancellationToken);

        Task first = await Task.WhenAny(authorityTask, presentationTask).ConfigureAwait(false);
        bool presentationEndedFirst = ReferenceEquals(first, presentationTask);
        if (presentationEndedFirst && !presentationTask.IsCompletedSuccessfully)
        {
            authorityStop.Cancel();
        }

        HostRunResult<FirstBoardWorld>? result = null;
        Exception? authorityError = null;
        try
        {
            result = await authorityTask.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            authorityError = error;
        }

        Exception? presentationError = null;
        try
        {
            await presentationTask.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            presentationError = error;
        }

        Exception? terminalError = presentationEndedFirst && presentationError is not null
            ? presentationError
            : authorityError ?? presentationError;
        if (terminalError is not null)
        {
            TerminalStatus status = terminalError is OperationCanceledException
                ? new TerminalStatus(TerminalStatusKind.Canceled, "Live session canceled.")
                : new TerminalStatus(
                    TerminalStatusKind.Faulted,
                    $"Live session faulted: {terminalError.GetType().Name}: " +
                    terminalError.Message);
            await TryReportFailureStatusAsync(terminal, status).ConfigureAwait(false);
            if (terminalError is OperationCanceledException canceled &&
                cancellationToken.IsCancellationRequested)
            {
                LiveFrontierSnapshot canceledFrontiers = coordination.Snapshot();
                if (canceledFrontiers.Presented != kernel.Version)
                {
                    throw new InvalidOperationException(
                        "Canceled Presentation did not drain the committed session prefix.",
                        canceled);
                }

                throw new LiveSessionCanceledException(
                    new LiveSessionCanceledCapture(
                        genesis,
                        kernel.World,
                        kernel.Version,
                        kernel.CurrentModelTime,
                        journal),
                    canceled);
            }

            ExceptionDispatchInfo.Capture(terminalError).Throw();
        }

        if (result is null)
        {
            throw new InvalidOperationException("Live session ended without an Authority result.");
        }

        LiveFrontierSnapshot finalFrontiers = coordination.Snapshot();
        if (finalFrontiers.Presented != result.Version)
        {
            throw new InvalidOperationException(
                "Presentation completed without acknowledging the full committed session prefix.");
        }

        await terminal.ShowStatusAsync(
                new TerminalStatus(
                    TerminalStatusKind.Completed,
                    $"Live session completed at version " +
                    $"{result.Version.LineageId}/{result.Version.TransitionCount}."),
                CancellationToken.None)
            .ConfigureAwait(false);
        return new BoardRunCapture(genesis, result, journal);
    }

    private static IReadOnlyDictionary<string, IPlayerDriver> ComposeDrivers(
        ScenarioInstance instance,
        IReadOnlyDictionary<string, IPlayerDriver> aiDrivers,
        string? humanActorId,
        LiveSessionCoordination coordination,
        ITerminalUi terminal)
    {
        if (humanActorId is not null &&
            !instance.Definition.Actors.Any(actor => actor.Id == humanActorId))
        {
            throw new ArgumentException(
                $"Human actor '{humanActorId}' is not defined by the scenario.",
                nameof(humanActorId));
        }

        var drivers = new Dictionary<string, IPlayerDriver>(StringComparer.Ordinal);
        foreach ((string actorId, IPlayerDriver driver) in aiDrivers)
        {
            ArgumentNullException.ThrowIfNull(driver);
            if (!instance.Definition.Actors.Any(actor => actor.Id == actorId))
            {
                throw new ArgumentException(
                    $"AI driver actor '{actorId}' is not defined by the scenario.",
                    nameof(aiDrivers));
            }

            if (actorId == humanActorId)
            {
                throw new ArgumentException(
                    $"Human actor '{actorId}' cannot also own an AI driver.",
                    nameof(aiDrivers));
            }

            drivers.Add(actorId, driver);
        }

        if (humanActorId is not null)
        {
            drivers.Add(
                humanActorId,
                new PresentationGatedHumanPlayerDriver(coordination, terminal));
        }

        string[] missing =
        [
            .. instance.Definition.Actors
                .Select(actor => actor.Id)
                .Where(actorId => !drivers.ContainsKey(actorId)),
        ];
        if (missing.Length > 0)
        {
            throw new ArgumentException(
                $"Live session has no Player driver for: {string.Join(", ", missing)}.",
                nameof(aiDrivers));
        }

        return drivers;
    }

    private static async Task TryReportFailureStatusAsync(
        ITerminalUi terminal,
        TerminalStatus status)
    {
        try
        {
            await terminal.ShowStatusAsync(status, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the causal Authority/Presentation failure over a best-effort status write.
        }
    }
}

internal sealed record LiveSessionCanceledCapture(
    FirstBoardWorld InitialWorld,
    FirstBoardWorld World,
    WorldVersion Version,
    ModelTime CurrentModelTime,
    InMemoryJournal<FirstBoardFact> Journal);

internal sealed class LiveSessionCanceledException : OperationCanceledException
{
    public LiveSessionCanceledException(
        LiveSessionCanceledCapture capture,
        OperationCanceledException cause)
        : base("Live session canceled after preserving its committed prefix.", cause,
            cause.CancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capture);
        Capture = capture;
    }

    public LiveSessionCanceledCapture Capture { get; }
}
