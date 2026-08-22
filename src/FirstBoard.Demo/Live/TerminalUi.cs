using System.Globalization;
using System.Text;
using DramaBoard.Protocol;

namespace DramaBoard.FirstBoard.Demo.Live;

internal sealed class TerminalUi : ITerminalUi
{
    private readonly SemaphoreSlim _output = new(1, 1);
    private readonly SemaphoreSlim _input = new(1, 1);

    public ValueTask ShowCueAsync(
        PresentationCue cue,
        CancellationToken cancellationToken) =>
        WriteAsync($"[story:{cue.Code}] {cue.Text}", cancellationToken);

    public ValueTask ShowDeveloperOverlayAsync(
        DeveloperOverlay overlay,
        CancellationToken cancellationToken) =>
        WriteAsync($"[dev:{overlay.Code}] {overlay.Text}", cancellationToken);

    public ValueTask ShowStatusAsync(
        TerminalStatus status,
        CancellationToken cancellationToken) =>
        WriteAsync(
            $"[status:{status.Kind.ToString().ToLowerInvariant()}] {status.Text}",
            cancellationToken);

    public async ValueTask ShowPromptAsync(
        DecisionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _output.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Console.WriteLine(FormatPrompt(request));
        }
        finally
        {
            _output.Release();
        }
    }

    internal static string FormatPrompt(DecisionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var text = new StringBuilder()
            .AppendLine()
            .Append("[decision] ").Append(request.ActorId)
            .Append(" @ ").Append(FormatTime(request.ModelTimeMs))
            .Append(" (").Append(request.Observation.LocationId).AppendLine(")");
        if (request.Observation.VisibleActorIds.Count > 0)
        {
            text.Append("  visible actors: ")
                .AppendLine(string.Join(", ", request.Observation.VisibleActorIds));
        }

        if (request.Observation.VisibleObjectIds.Count > 0)
        {
            text.Append("  visible objects: ")
                .AppendLine(string.Join(", ", request.Observation.VisibleObjectIds));
        }

        foreach (ObservedExit exit in request.Observation.Exits)
        {
            text.Append("  exit: ").Append(exit.ExitId)
                .Append(" -> ").Append(exit.DestinationId)
                .Append("; duration=").Append(
                    exit.ExpectedDurationMs.ToString(CultureInfo.InvariantCulture))
                .Append("ms; ").AppendLine(exit.IsAvailable ? "available" : "unavailable");
        }

        foreach (KnownFact fact in request.Observation.KnownFacts)
        {
            text.Append("  known: ").AppendLine(fact.Text);
        }

        text.AppendLine("  available actions:");
        foreach (AvailableAction action in request.AvailableActions)
        {
            text.Append("    ").Append(action.ActionKind.Id)
                .AppendLine(FormatCandidates(action));
        }

        return text
            .Append("  Type 'help' to list command forms, or press Ctrl+C to end the session.")
            .ToString();
    }

    public ValueTask ShowInputErrorAsync(
        string message,
        CancellationToken cancellationToken) =>
        WriteAsync($"[input] {message}", cancellationToken);

    public async ValueTask<string?> ReadCommandAsync(
        DecisionId decisionId,
        CancellationToken cancellationToken)
    {
        await _input.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _output.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Console.Write($"{decisionId.Value}> ");
            }
            finally
            {
                _output.Release();
            }

            return await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _input.Release();
        }
    }

    private async ValueTask WriteAsync(string text, CancellationToken cancellationToken)
    {
        await _output.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Console.WriteLine(text);
        }
        finally
        {
            _output.Release();
        }
    }

    private static string FormatTime(long ticks)
    {
        long minutes = ticks / 60_000;
        long seconds = ticks % 60_000 / 1_000;
        return $"{minutes.ToString("00", CultureInfo.InvariantCulture)}:" +
            seconds.ToString("00", CultureInfo.InvariantCulture);
    }

    private static string FormatCandidates(AvailableAction action)
    {
        string[] candidates =
        [
            CandidateList("actors", action.CandidateActorIds),
            CandidateList("objects", action.CandidateObjectIds),
            CandidateList("exits", action.CandidateExitIds),
            CandidateList("destinations", action.CandidateDestinationIds),
        ];
        string[] present = [.. candidates.Where(value => value.Length > 0)];
        return present.Length == 0 ? string.Empty : $" ({string.Join("; ", present)})";
    }

    private static string CandidateList(string label, IReadOnlyList<string>? values) =>
        values is null ? string.Empty : $"{label}=[{string.Join(", ", values)}]";
}

internal sealed class FixedIntervalPresentationPacer : IPresentationPacer
{
    private readonly TimeSpan _interval;

    public FixedIntervalPresentationPacer(TimeSpan interval)
    {
        if (interval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        _interval = interval;
    }

    public ValueTask PaceAsync(CancellationToken cancellationToken) =>
        _interval == TimeSpan.Zero
            ? ValueTask.CompletedTask
            : new ValueTask(Task.Delay(_interval, cancellationToken));
}
