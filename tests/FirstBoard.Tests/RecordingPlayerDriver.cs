using DramaBoard.Player;
using DramaBoard.Protocol;

namespace DramaBoard.FirstBoard.Tests;

internal sealed class RecordingPlayerDriver : IPlayerDriver
{
    private readonly IPlayerDriver _inner;
    private readonly List<DecisionRequest> _requests = [];

    public RecordingPlayerDriver(IPlayerDriver inner)
    {
        _inner = inner;
    }

    public IReadOnlyList<DecisionRequest> Requests => _requests.AsReadOnly();

    public async ValueTask<PlayerDecision> DecideAsync(
        DecisionRequest request,
        CancellationToken cancellationToken)
    {
        _requests.Add(request);
        return await _inner.DecideAsync(request, cancellationToken);
    }
}
