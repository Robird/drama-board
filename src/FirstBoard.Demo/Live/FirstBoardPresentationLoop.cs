using System.Globalization;
using System.Threading.Channels;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.FirstBoard.Demo.Live;

/// <summary>Replays and presents only complete committed FirstBoard transitions.</summary>
internal sealed class FirstBoardPresentationLoop
{
    private readonly FirstBoardReducer _reducer;
    private readonly FirstBoardPresentationProjector _projector;
    private readonly LiveSessionCoordination _coordination;
    private readonly ITerminalUi _terminal;
    private readonly IPresentationPacer _pacer;
    private readonly PresentationMode _mode;
    private readonly string? _humanActorId;
    private readonly ModelTime _baselineTime;
    private FirstBoardWorld _replayWorld;
    private LogicalInstant? _lastPresentedInstant;

    public FirstBoardPresentationLoop(
        ScenarioInstance scenario,
        FirstBoardWorld baselineWorld,
        LiveSessionCoordination coordination,
        ITerminalUi terminal,
        IPresentationPacer pacer,
        PresentationMode mode,
        string? humanActorId)
        : this(
            scenario,
            baselineWorld,
            baselineLastInstant: null,
            coordination,
            terminal,
            pacer,
            mode,
            humanActorId)
    {
    }

    public FirstBoardPresentationLoop(
        ScenarioInstance scenario,
        FirstBoardWorld baselineWorld,
        LogicalInstant? baselineLastInstant,
        LiveSessionCoordination coordination,
        ITerminalUi terminal,
        IPresentationPacer pacer,
        PresentationMode mode,
        string? humanActorId)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(baselineWorld);
        ArgumentNullException.ThrowIfNull(coordination);
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(pacer);
        if (mode == PresentationMode.Player && humanActorId is null)
        {
            throw new ArgumentException(
                "Player presentation requires a Human actor viewpoint.",
                nameof(humanActorId));
        }

        if (baselineWorld.WorldSeed != scenario.WorldSeed)
        {
            throw new ArgumentException(
                "Presentation baseline and scenario must use the same world seed.",
                nameof(baselineWorld));
        }

        LiveFrontierSnapshot frontiers = coordination.Snapshot();
        if (frontiers.Committed != frontiers.Presented)
        {
            throw new ArgumentException(
                "Presentation baseline requires equal committed and presented frontiers.",
                nameof(coordination));
        }

        bool emptyBaseline = frontiers.Committed.TransitionCount == 0;
        if (emptyBaseline != (baselineLastInstant is null))
        {
            throw new ArgumentException(
                "A zero-transition Presentation baseline requires no last instant, and a " +
                "nonzero baseline requires one.",
                nameof(baselineLastInstant));
        }

        if (baselineLastInstant is LogicalInstant last &&
            baselineWorld.Now != last.ModelTime)
        {
            throw new ArgumentException(
                "Presentation baseline world time must equal the last committed instant.",
                nameof(baselineLastInstant));
        }

        _reducer = new FirstBoardReducer(scenario.Graph);
        _reducer.Validate(baselineWorld);
        _projector = new FirstBoardPresentationProjector(scenario, humanActorId);
        _coordination = coordination;
        _terminal = terminal;
        _pacer = pacer;
        _mode = mode;
        _humanActorId = humanActorId;
        _baselineTime = baselineWorld.Now;
        _replayWorld = baselineWorld;
        _lastPresentedInstant = baselineLastInstant;
    }

    internal FirstBoardWorld ReplayWorld => _replayWorld;

    internal LogicalInstant? LastPresentedInstant => _lastPresentedInstant;

    public async Task RunAsync(
        ChannelReader<CommittedTransition> reader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        bool bufferingShown = false;
        try
        {
            while (true)
            {
                if (reader.TryRead(out CommittedTransition? transition))
                {
                    bufferingShown = false;
                    await PresentAsync(transition, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (reader.Completion.IsCompleted)
                {
                    await reader.Completion.ConfigureAwait(false);
                    return;
                }

                LiveFrontierSnapshot frontiers = _coordination.Snapshot();
                if (!cancellationToken.IsCancellationRequested &&
                    !bufferingShown &&
                    frontiers.BacklogCount == 0)
                {
                    try
                    {
                        await _terminal.ShowStatusAsync(
                                new TerminalStatus(
                                    TerminalStatusKind.Buffering,
                                    "Waiting for the next committed world transition."),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                        when (cancellationToken.IsCancellationRequested)
                    {
                        // Authority will complete the channel; keep draining silently.
                    }

                    bufferingShown = true;
                }

                // Cancellation ends presentation effects, not committed-prefix replay. Keep
                // draining until Authority completes the channel so P can still catch C.
                if (!await reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    await reader.Completion.ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (Exception error)
        {
            _coordination.Fail(error);
            throw;
        }
    }

    private async Task PresentAsync(
        CommittedTransition transition,
        CancellationToken cancellationToken)
    {
        LiveFrontierSnapshot before = _coordination.Snapshot();
        if (transition.Version.LineageId != before.Presented.LineageId ||
            transition.Version.TransitionCount !=
            checked(before.Presented.TransitionCount + 1))
        {
            throw new InvalidOperationException(
                "Presentation received a missing, duplicate, or cross-lineage transition.");
        }

        if (_lastPresentedInstant is LogicalInstant last &&
            transition.Batch.Instant <= last)
        {
            throw new InvalidOperationException(
                "Committed transitions must be presented in strict LogicalInstant order.");
        }

        FirstBoardWorld postWorld = _replayWorld;
        foreach (FirstBoardFact fact in transition.Batch.Facts)
        {
            postWorld = _reducer.Apply(postWorld, transition.Batch.Instant, fact);
        }

        _reducer.Validate(postWorld);
        FirstBoardProjection projection = _projector.Project(
            _replayWorld,
            transition,
            postWorld);
        PresentationCue? intervalCue = CreateIntervalCue(transition.Batch.Instant);

        if (intervalCue is not null)
        {
            await PlayCueAsync(intervalCue, cancellationToken).ConfigureAwait(false);
        }

        if (_humanActorId is not null)
        {
            foreach (PresentationCue cue in projection.PlayerCues)
            {
                await PlayCueAsync(cue, cancellationToken).ConfigureAwait(false);
            }
        }

        if (_mode == PresentationMode.Developer)
        {
            LiveFrontierSnapshot current = _coordination.Snapshot();
            await PlayOverlayAsync(
                    new DeveloperOverlay(
                        "developer.frontiers",
                        $"C={FormatVersion(current.Committed)} " +
                        $"P={FormatVersion(current.Presented)} " +
                        $"backlog={current.BacklogCount}"),
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (DeveloperOverlay overlay in projection.DeveloperOverlays)
            {
                await PlayOverlayAsync(overlay, cancellationToken).ConfigureAwait(false);
            }
        }

        _replayWorld = postWorld;
        _lastPresentedInstant = transition.Batch.Instant;
        _coordination.AcknowledgePresented(transition.Version);
    }

    private PresentationCue? CreateIntervalCue(LogicalInstant next)
    {
        ModelTime previous = _lastPresentedInstant?.ModelTime ?? _baselineTime;
        if (next.ModelTime <= previous)
        {
            return null;
        }

        string context = _humanActorId is null
            ? "Committed world time advanced."
            : HumanIntervalContext();
        return new PresentationCue(
            "time.advance",
            $"World time {FormatTime(previous)} -> {FormatTime(next.ModelTime)}. {context}");
    }

    private string HumanIntervalContext()
    {
        if (!_replayWorld.Spatial.TryGetEntity(
                new EntityId(_humanActorId!),
                out SpatialEntity? entity))
        {
            return "Your location is unavailable.";
        }

        return entity!.Location switch
        {
            AtPlaceLocation atPlace => $"You were at {atPlace.PlaceId.Value}.",
            TraversingLocation traversal =>
                $"You traveled through {traversal.PassageId.Value} toward " +
                $"{traversal.TargetPlaceId.Value}.",
            _ => "Your location changed within committed history.",
        };
    }

    private async ValueTask PlayCueAsync(
        PresentationCue cue,
        CancellationToken cancellationToken)
    {
        await ShowCueForDrainAsync(cue, cancellationToken).ConfigureAwait(false);
        await PaceUnlessCanceledAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask PlayOverlayAsync(
        DeveloperOverlay overlay,
        CancellationToken cancellationToken)
    {
        await ShowOverlayForDrainAsync(overlay, cancellationToken).ConfigureAwait(false);
        await PaceUnlessCanceledAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ShowCueForDrainAsync(
        PresentationCue cue,
        CancellationToken cancellationToken)
    {
        try
        {
            await _terminal.ShowCueAsync(cue, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await _terminal.ShowCueAsync(cue, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async ValueTask ShowOverlayForDrainAsync(
        DeveloperOverlay overlay,
        CancellationToken cancellationToken)
    {
        try
        {
            await _terminal
                .ShowDeveloperOverlayAsync(overlay, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await _terminal
                .ShowDeveloperOverlayAsync(overlay, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask PaceUnlessCanceledAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await _pacer.PaceAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation removes only presentation delay. Every committed cue is still
            // emitted before its batch advances P.
        }
    }

    private static string FormatVersion(WorldVersion version) =>
        $"{version.LineageId.ToString(CultureInfo.InvariantCulture)}/" +
        version.TransitionCount.ToString(CultureInfo.InvariantCulture);

    private static string FormatTime(ModelTime time) =>
        time.Ticks.ToString(CultureInfo.InvariantCulture) + "ms";
}
