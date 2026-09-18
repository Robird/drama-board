using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Player;
using DramaBoard.Player.Agency.Spatial;
using DramaBoard.Protocol;
using DramaBoard.Spatial;

namespace DramaBoard.Server.FreePlay;

/// <summary>One process-local game. Only its serial run loop owns the Kernel and history.</summary>
public sealed class FreePlaySession {
    private readonly object _gate = new();
    private readonly string _runId = Guid.NewGuid().ToString("N");
    private readonly FreePlayScene _scene;
    private readonly IPlayerDriver _driver;
    private readonly bool _webDriver;
    private readonly InMemoryOccurrenceHistory<FreePlayWorld, GraphSpatialFact> _history;
    private readonly SimulationKernel<FreePlayWorld, FreePlayCandidate, GraphSpatialFact> _kernel;
    private PlayerView _player;
    private DevView _dev;
    private PendingDecision? _pending;
    private TaskCompletionSource _changed = NewSignal();
    private CancellationToken _applicationToken;
    private int _started;

    public FreePlaySession(IPlayerDriver? driver = null) {
        _scene = new(_runId, DecideAsync);
        _webDriver = driver is null;
        _driver = driver ?? new WebHumanDriver(WaitForHumanAsync);
        FreePlayWorld genesis = _scene.Genesis();
        _history = new(genesis, new KernelCursor(new WorldVersion(1, 0), ModelTime.Zero, null, null));
        _kernel = new(_history, new SimulationRules(1, 100), [_scene], _scene.Fold, _scene.Validate);
        GraphDefinition known = FullMapPlayerSpatialKnowledgeGetter<FreePlayWorld>.Instance
            .GetKnownGraph(genesis, FreePlayScene.Actor.Value, _scene.Definition).KnownGraph;
        var positions = new Dictionary<string, (int X, int Y)> {
            ["A"] = (80, 70),
            ["B"] = (320, 70),
            ["C"] = (80, 230),
            ["D"] = (320, 230)
        };
        var map = new KnownMap(Freeze(known.Places.Select(place =>
            new MapPlace(place.Value, place.Value, positions[place.Value].X, positions[place.Value].Y))),
            Freeze(known.Passages.Select(passage =>
                new MapPassage(passage.Id.Value, passage.EndpointA.Value, passage.EndpointB.Value))));
        _player = new(_runId, 0, 0, "advancing", _scene.Location(genesis), map,
            Freeze<TrajectoryEntry>([new("A", 0, null)]), null);
        _dev = new(_runId, 0, 0, "advancing", _player.Location, 0, null,
            Freeze<OccurrenceView>([]), null, null, null);
    }

    public PlayerView GetPlayerView() { lock (_gate) { return _player; } }
    public DevView GetDevView() { lock (_gate) { return _dev; } }

    /// <summary>A lost-wakeup-free observation seam; cancellation affects only this observer.</summary>
    public async Task<PlayerView> WaitForViewAsync(Func<PlayerView, bool> predicate,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(predicate);
        while (true) {
            Task changed;
            lock (_gate) {
                if (predicate(_player)) { return _player; }
                changed = _changed.Task;
            }
            await changed.WaitAsync(cancellationToken);
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken) {
        if (Interlocked.Exchange(ref _started, 1) != 0) {
            throw new InvalidOperationException("This session has already been started.");
        }
        _applicationToken = cancellationToken;
        try {
            while (true) {
                StepStatus result = await _kernel.StepAsync(new ModelTime(long.MaxValue), cancellationToken);
                if (result != StepStatus.Committed) {
                    throw new InvalidOperationException($"Unexpected simulation terminal state: {result}.");
                }
                // History is projected on its single owning execution path; no live list escapes.
                PublishCommitted();
            }
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            Finish("stopped", null);
        } catch (Exception error) {
            Finish("faulted", $"{error.GetType().Name}: {error.Message}");
        }
    }

    public SubmitResult Submit(string? decisionId, string? actionKind, string? exitId) {
        lock (_gate) {
            if (_player.Status is "faulted" or "stopped" || _applicationToken.IsCancellationRequested) {
                return Reject(503, "session-unavailable", "The session is unavailable.");
            }
            if (string.IsNullOrWhiteSpace(decisionId) || string.IsNullOrWhiteSpace(actionKind) ||
                string.IsNullOrWhiteSpace(exitId)) {
                return Reject(400, "invalid-decision", "decisionId, actionKind and exitId are required.");
            }
            PendingDecision? pending = _pending;
            if (!_webDriver || pending is null || pending.Request.DecisionId.Value != decisionId) {
                return Reject(409, "stale-decision", "This decision is no longer pending.");
            }
            if (actionKind != ActionKinds.Travel.Id) {
                return Reject(400, "invalid-action", "Choose an advertised travel exit.");
            }
            var answer = new PlayerDecision(pending.Request.DecisionId, new Intent(ActionKinds.Travel, ExitId: exitId));
            if (!_scene.TryPlan(pending.World, pending.Request, answer, out _)) {
                return Reject(400, "invalid-action", "Choose an advertised travel exit.");
            }
            // Occupy and publish before waking the run loop. No HTTP continuation publishes later.
            _pending = null;
            PublishStatus("advancing", null);
            pending.Answer.SetResult(answer);
            return new(202, "accepted", "Decision received; read the view for the committed result.");
        }
    }

    private async ValueTask<PlayerDecision> DecideAsync(FreePlayWorld world, DecisionRequest request,
        CancellationToken cancellationToken) {
        ValueTask<PlayerDecision> webAnswer = default;
        lock (_gate) {
            cancellationToken.ThrowIfCancellationRequested();
            _pending = new(world, request, new(TaskCreationOptions.RunContinuationsAsynchronously));
            // Capture the actual driver's waiter before any HTTP caller can occupy it.
            if (_webDriver) { webAnswer = _driver.DecideAsync(request, cancellationToken); }
            var decision = new DecisionView(request.DecisionId.Value, ActionKinds.Travel.Id,
                Freeze(request.Observation.Exits.Select(exit =>
                    new DecisionExit(exit.ExitId, exit.DestinationId, exit.ExpectedDurationMs))));
            PublishStatus("waiting", decision);
        }
        PlayerDecision answer = _webDriver
            ? await webAnswer
            : await _driver.DecideAsync(request, cancellationToken);
        if (!_webDriver) {
            lock (_gate) {
                _pending = null;
                PublishStatus("advancing", null);
            }
        }
        return answer;
    }

    private ValueTask<PlayerDecision> WaitForHumanAsync(DecisionRequest request, CancellationToken cancellationToken) {
        lock (_gate) {
            if (_pending is null || _pending.Request.DecisionId != request.DecisionId) {
                throw new InvalidOperationException("Human waiter is missing.");
            }
            return new(_pending.Answer.Task.WaitAsync(cancellationToken));
        }
    }

    private void PublishCommitted() {
        var trajectory = new List<TrajectoryEntry> { new("A", 0, null) };
        TraversalStartedFact? departure = null;
        foreach (var occurrence in _history.CompletedEvents) {
            foreach (GraphSpatialFact fact in occurrence.Facts) {
                if (fact is TraversalStartedFact started && started.EntityId == FreePlayScene.Actor) {
                    departure = started;
                } else if (fact is TraversalArrivedFact arrived && arrived.EntityId == FreePlayScene.Actor) {
                    if (departure is null) { throw new InvalidOperationException("Arrival has no departure."); }
                    PassageDefinition passage = _scene.Definition.GetPassage(departure.PassageId);
                    PlaceId target = passage.EndpointA == departure.FromPlaceId ? passage.EndpointB : passage.EndpointA;
                    trajectory.Add(new(target.Value, occurrence.TargetInstant.ModelTime.Ticks, passage.Id.Value));
                    departure = null;
                }
            }
        }
        IReadOnlyList<OccurrenceView> records = Freeze(_history.CompletedEvents.TakeLast(100).Select(occurrence =>
            new OccurrenceView(occurrence.TargetInstant.ModelTime.Ticks, occurrence.CauseKey.ToString(),
                Freeze(occurrence.Facts.Select(fact => fact.GetType().Name)))));
        lock (_gate) {
            long revision = _player.ViewRevision + 1;
            string location = _scene.Location(_kernel.World);
            long time = _kernel.CurrentModelTime.Ticks;
            _player = _player with {
                ViewRevision = revision,
                ModelTimeMs = time,
                Location = location,
                Trajectory = Freeze(trajectory),
                Status = "advancing",
                Decision = null
            };
            _dev = _dev with {
                ViewRevision = revision,
                ModelTimeMs = time,
                Location = location,
                TransitionCount = _kernel.Version.TransitionCount,
                Records = records,
                LastCommittedInstant = _kernel.LastCommittedInstant is { } instant
                    ? new(instant.ModelTime.Ticks, instant.CausalOrdinal) : null,
                Status = "advancing",
                PendingDecisionId = null
            };
            SignalChange();
        }
    }

    private SubmitResult Reject(int status, string code, string message) {
        _dev = _dev with { LastRejection = $"{code}: {message}" };
        PublishStatus(_player.Status, _player.Decision);
        return new(status, code, message);
    }

    private void Finish(string status, string? fault) {
        lock (_gate) {
            _pending?.Answer.TrySetCanceled();
            _pending = null;
            _dev = _dev with { Fault = fault };
            PublishStatus(status, null);
        }
    }

    private void PublishStatus(string status, DecisionView? decision) {
        long revision = _player.ViewRevision + 1;
        _player = _player with { ViewRevision = revision, Status = status, Decision = decision };
        _dev = _dev with { ViewRevision = revision, Status = status, PendingDecisionId = decision?.DecisionId };
        SignalChange();
    }

    private void SignalChange() {
        TaskCompletionSource previous = _changed;
        _changed = NewSignal();
        previous.SetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values) => Array.AsReadOnly(values.ToArray());
    private sealed record PendingDecision(FreePlayWorld World, DecisionRequest Request,
        TaskCompletionSource<PlayerDecision> Answer);
}

internal sealed class WebHumanDriver(
    Func<DecisionRequest, CancellationToken, ValueTask<PlayerDecision>> wait) : IPlayerDriver {
    public ValueTask<PlayerDecision> DecideAsync(DecisionRequest request, CancellationToken cancellationToken) =>
        wait(request, cancellationToken);
}
