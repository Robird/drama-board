using DramaBoard.Protocol;

namespace DramaBoard.FirstBoard.Demo.Live;

internal enum PresentationMode
{
    Player = 0,
    Developer = 1,
}

internal sealed record PresentationCue
{
    public PresentationCue(string code, string text)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("A presentation cue requires a stable code.", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("A presentation cue requires visible text.", nameof(text));
        }

        Code = code;
        Text = text;
    }

    public string Code { get; }

    public string Text { get; }
}

internal sealed record DeveloperOverlay
{
    public DeveloperOverlay(string code, string text)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("A developer overlay requires a stable code.", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("A developer overlay requires visible text.", nameof(text));
        }

        Code = code;
        Text = text;
    }

    public string Code { get; }

    public string Text { get; }
}

internal sealed record FirstBoardProjection
{
    public FirstBoardProjection(
        IEnumerable<PresentationCue> playerCues,
        IEnumerable<DeveloperOverlay> developerOverlays)
    {
        ArgumentNullException.ThrowIfNull(playerCues);
        ArgumentNullException.ThrowIfNull(developerOverlays);
        PresentationCue[] cues = [.. playerCues];
        DeveloperOverlay[] overlays = [.. developerOverlays];
        if (cues.Any(cue => cue is null) || overlays.Any(overlay => overlay is null))
        {
            throw new ArgumentException("Presentation output cannot contain null entries.");
        }

        PlayerCues = Array.AsReadOnly(cues);
        DeveloperOverlays = Array.AsReadOnly(overlays);
    }

    public IReadOnlyList<PresentationCue> PlayerCues { get; }

    public IReadOnlyList<DeveloperOverlay> DeveloperOverlays { get; }
}

internal enum TerminalStatusKind
{
    Buffering = 0,
    Completed = 1,
    Canceled = 2,
    Faulted = 3,
}

internal sealed record TerminalStatus(TerminalStatusKind Kind, string Text);

internal interface IPresentationPacer
{
    ValueTask PaceAsync(CancellationToken cancellationToken);
}

/// <summary>Receives only projected output and already-legal Human requests.</summary>
internal interface ITerminalUi
{
    ValueTask ShowCueAsync(PresentationCue cue, CancellationToken cancellationToken);

    ValueTask ShowDeveloperOverlayAsync(
        DeveloperOverlay overlay,
        CancellationToken cancellationToken);

    ValueTask ShowStatusAsync(TerminalStatus status, CancellationToken cancellationToken);

    ValueTask ShowPromptAsync(DecisionRequest request, CancellationToken cancellationToken);

    ValueTask ShowInputErrorAsync(string message, CancellationToken cancellationToken);

    ValueTask<string?> ReadCommandAsync(
        DecisionId decisionId,
        CancellationToken cancellationToken);
}
