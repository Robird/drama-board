using DramaBoard.Decision.Validation;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Player;
using DramaBoard.Protocol;

namespace DramaBoard.FirstBoard.Demo.Live;

/// <summary>Exposes one Human request only after Presentation has played its committed basis.</summary>
internal sealed class PresentationGatedHumanPlayerDriver : IPlayerDriver
{
    internal const string CommandHelp =
        "Commands: travel <exit>; travel-to <place>; continue; reverse; " +
        "wait [duration-ms] | wait until <model-time-ms>; talk <actor> <text>; " +
        "observe [object]; take|put|use <object>; give|show <actor> <object>.";

    private readonly LiveSessionCoordination _coordination;
    private readonly ITerminalUi _terminal;
    private readonly object _sync = new();
    private DecisionId? _pendingDecisionId;

    public PresentationGatedHumanPlayerDriver(
        LiveSessionCoordination coordination,
        ITerminalUi terminal)
    {
        ArgumentNullException.ThrowIfNull(coordination);
        ArgumentNullException.ThrowIfNull(terminal);
        _coordination = coordination;
        _terminal = terminal;
    }

    public async ValueTask<PlayerDecision> DecideAsync(
        DecisionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        WorldVersion revealAfter = _coordination.CaptureCommitted();
        BeginInteraction(request.DecisionId);
        try
        {
            await _coordination
                .WaitUntilPresentedAsync(revealAfter, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await _terminal
                .ShowPromptAsync(request, cancellationToken)
                .ConfigureAwait(false);

            while (true)
            {
                string? command = await _terminal
                    .ReadCommandAsync(request.DecisionId, cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (command is null)
                {
                    throw new HumanSessionExitException();
                }

                if (string.Equals(command.Trim(), "help", StringComparison.OrdinalIgnoreCase))
                {
                    await ShowErrorAsync(CommandHelp, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                HumanDecisionParseResult parsed = HumanDecisionParser.Parse(command);
                if (!parsed.IsSuccess)
                {
                    await ShowErrorAsync(parsed.Error!, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var decision = new PlayerDecision(request.DecisionId, parsed.Intent!);
                PlayerDecisionValidationResult validation =
                    PlayerDecisionValidator.Validate(decision, request);
                if (!validation.IsValid)
                {
                    await ShowErrorAsync(
                            validation.Message ?? "The command is not available now.",
                            cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                return decision;
            }
        }
        finally
        {
            EndInteraction(request.DecisionId);
        }
    }

    private void BeginInteraction(DecisionId decisionId)
    {
        lock (_sync)
        {
            if (_pendingDecisionId is not null)
            {
                throw new InvalidOperationException(
                    "Only one Human decision can be pending in a live session.");
            }

            _pendingDecisionId = decisionId;
        }
    }

    private void EndInteraction(DecisionId decisionId)
    {
        lock (_sync)
        {
            if (_pendingDecisionId == decisionId)
            {
                _pendingDecisionId = null;
            }
        }
    }

    private ValueTask ShowErrorAsync(string message, CancellationToken cancellationToken) =>
        _terminal.ShowInputErrorAsync(message, cancellationToken);
}

internal sealed class HumanSessionExitException : OperationCanceledException
{
    public HumanSessionExitException()
        : base("Human input ended; the live session should exit cleanly.")
    {
    }
}
