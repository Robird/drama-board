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
    private readonly ModelTime _genesisTime;
    private FirstBoardWorld _replayWorld;
    private LogicalInstant? _lastPresentedInstant;

    public FirstBoardPresentationLoop(
        ScenarioInstance scenario,
        FirstBoardWorld genesisWorld,
        LiveSessionCoordination coordination,
        ITerminalUi terminal,
        IPresentationPacer pacer,
        PresentationMode mode,
        string? humanActorId)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(genesisWorld);
        ArgumentNullException.ThrowIfNull(coordination);
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(pacer);
        if (mode == PresentationMode.Player && humanActorId is null)
        {
            throw new ArgumentException(
                "Player presentation requires a Human actor viewpoint.",
                nameof(humanActorId));
        }

        if (genesisWorld.WorldSeed != scenario.WorldSeed)
        {
            throw new ArgumentException(
                "Presentation genesis and scenario must use the same world seed.",
                nameof(genesisWorld));
        }

        _reducer = new FirstBoardReducer(scenario.Graph);
        _reducer.Validate(genesisWorld);
        _projector = new FirstBoardPresentationProjector(scenario, humanActorId);
        _coordination = coordination;
        _terminal = terminal;
        _pacer = pacer;
        _mode = mode;
        _humanActorId = humanActorId;
        _genesisTime = genesisWorld.Now;
        _replayWorld = genesisWorld;
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
                if (!bufferingShown && frontiers.BacklogCount == 0)
                {
                    await _terminal.ShowStatusAsync(
                            new TerminalStatus(
                                TerminalStatusKind.Buffering,
                                "Waiting for the next committed world transition."),
                            cancellationToken)
                        .ConfigureAwait(false);
                    bufferingShown = true;
                }

                if (!await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
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
        ModelTime previous = _lastPresentedInstant?.ModelTime ?? _genesisTime;
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
        await _terminal.ShowCueAsync(cue, cancellationToken).ConfigureAwait(false);
        await _pacer.PaceAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask PlayOverlayAsync(
        DeveloperOverlay overlay,
        CancellationToken cancellationToken)
    {
        await _terminal
            .ShowDeveloperOverlayAsync(overlay, cancellationToken)
            .ConfigureAwait(false);
        await _pacer.PaceAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string FormatVersion(WorldVersion version) =>
        $"{version.LineageId.ToString(CultureInfo.InvariantCulture)}/" +
        version.TransitionCount.ToString(CultureInfo.InvariantCulture);

    private static string FormatTime(ModelTime time) =>
        time.Ticks.ToString(CultureInfo.InvariantCulture) + "ms";
}
