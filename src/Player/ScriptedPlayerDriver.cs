using DramaBoard.Protocol;

namespace DramaBoard.Player;

/// <summary>Answers requests from a finite, ordered sequence of decision factories.</summary>
public sealed class ScriptedPlayerDriver : IPlayerDriver
{
    private readonly Queue<Func<DecisionRequest, PlayerDecision>> _script;

    /// <summary>Initializes a driver whose factories are consumed in request order.</summary>
    public ScriptedPlayerDriver(IEnumerable<Func<DecisionRequest, PlayerDecision>> script)
    {
        ArgumentNullException.ThrowIfNull(script);

        Func<DecisionRequest, PlayerDecision>[] factories = [.. script];
        if (factories.Any(factory => factory is null))
        {
            throw new ArgumentException("Decision script cannot contain null factories.", nameof(script));
        }

        _script = new Queue<Func<DecisionRequest, PlayerDecision>>(factories);
    }

    /// <inheritdoc />
    public ValueTask<PlayerDecision> DecideAsync(
        DecisionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_script.TryDequeue(out Func<DecisionRequest, PlayerDecision>? factory))
        {
            throw new InvalidOperationException("The scripted Player has no decision remaining for this request.");
        }

        PlayerDecision decision = factory(request)
            ?? throw new InvalidOperationException("A scripted decision factory returned null.");
        return ValueTask.FromResult(decision);
    }
}
