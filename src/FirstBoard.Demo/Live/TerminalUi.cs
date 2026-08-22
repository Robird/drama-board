using System.Globalization;
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
            Console.WriteLine();
            Console.WriteLine(
                $"[decision] {request.ActorId} @ {FormatTime(request.ModelTimeMs)} " +
                $"({request.Observation.LocationId})");
            if (request.Observation.VisibleActorIds.Count > 0)
            {
                Console.WriteLine(
                    $"  visible actors: {string.Join(", ", request.Observation.VisibleActorIds)}");
            }

            if (request.Observation.VisibleObjectIds.Count > 0)
            {
                Console.WriteLine(
                    $"  visible objects: {string.Join(", ", request.Observation.VisibleObjectIds)}");
            }

            foreach (KnownFact fact in request.Observation.KnownFacts)
            {
                Console.WriteLine($"  known: {fact.Text}");
            }

            Console.WriteLine(
                "  Type 'help' to list legal command forms, or press Ctrl+C to end the session.");
        }
        finally
        {
            _output.Release();
        }
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
