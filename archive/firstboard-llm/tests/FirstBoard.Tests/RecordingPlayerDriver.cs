using DramaBoard.Protocol;

namespace DramaBoard.FirstBoard.Tests;

internal sealed class RecordingPlayerDriver : IPlayerDriver
{
    private readonly Queue<Func<DecisionRequest, PlayerDecision>> _decisions;
    private readonly List<DecisionRequest> _requests = [];

    public RecordingPlayerDriver(params Func<DecisionRequest, PlayerDecision>[] decisions)
    {
        _decisions = new Queue<Func<DecisionRequest, PlayerDecision>>(decisions);
    }

    public IReadOnlyList<DecisionRequest> Requests => _requests.AsReadOnly();

    public ValueTask<PlayerDecision> DecideAsync(
        DecisionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _requests.Add(request);
        if (_decisions.Count == 0)
        {
            throw new InvalidOperationException("The test Player has no scripted decision remaining.");
        }

        return ValueTask.FromResult(_decisions.Dequeue()(request));
    }
}
