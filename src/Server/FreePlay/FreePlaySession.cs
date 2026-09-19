using DramaBoard.Decision.Validation;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Player;
using DramaBoard.Player.Agency.Spatial;
using DramaBoard.Protocol;
using DramaBoard.Server.FreePlay.Runtime;
using DramaBoard.Server.Web;
using DramaBoard.Spatial;

namespace DramaBoard.Server.FreePlay;

/// <summary>One process-local game. Only its serial run loop owns the Kernel and history.</summary>
public sealed class FreePlaySession {
    private readonly object _gate = new();
    private readonly string _runId = Guid.NewGuid().ToString("N");
    private readonly FreePlayScene _scene;
    private readonly IDynamicProgrammer _programmer;
    private readonly bool _webProgrammer;
    private readonly InMemoryOccurrenceHistory<FreePlayWorld, FreePlayFact> _history;
    private readonly SimulationKernel<FreePlayWorld, FreePlayCandidate, FreePlayFact> _kernel;
    private PlayerView _player;
    private DevView _dev;
    private PendingProgram? _pending;
    private TaskCompletionSource _changed = NewSignal();
    private CancellationToken _applicationToken;
    private int _started;

    public FreePlaySession(IDynamicProgrammer? programmer = null) {
        _scene = new();
        _webProgrammer = programmer is null;
        _programmer = programmer ?? new WebHumanProgrammer(WaitForHumanAsync);
        FreePlayWorld genesis = _scene.Genesis();
        _history = new(genesis, new KernelCursor(new WorldVersion(1, 0), ModelTime.Zero, null, null));
        _kernel = new(_history, new SimulationRules(1, 100),
            [
                new FreePlaySpatialRule(_scene.Definition),
                new FreePlayProcessRule(_runId, new SessionProgrammer(InterceptProgramAsync),
                    _scene.Planner, _scene.Queries, _scene.Definition)
            ],
            _scene.Fold, _scene.Validate);
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
            Freeze<TrajectoryEntry>([new("A", 0, null)]), string.Empty, Freeze<InstructionView>([]), null);
        _dev = new(_runId, 0, 0, "advancing", _player.Location, 0, null,
            Freeze<OccurrenceView>([]), DescribeProcessMode(genesis.Process.Mode),
            genesis.Process.ControlRevision, null, null, null);
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
                if (result == StepStatus.Committed) {
                    // History is projected on its single owning execution path; no live list escapes.
                    PublishCommitted();
                    continue;
                }
                if (result == StepStatus.Exhausted && _kernel.World.Process.Mode is RunningStayProcess) {
                    // Staying is healthy rest with a kept arrangement; the session keeps serving reads.
                    lock (_gate) { PublishStatus("staying", null); }
                    return;
                }
                throw new InvalidOperationException($"Unexpected simulation terminal state: {result}.");
            }
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            Finish("stopped", null);
        } catch (Exception error) {
            Finish("faulted", $"{error.GetType().Name}: {error.Message}");
        }
    }

    public SubmitResult Submit(ProgrammingInput input) {
        ArgumentNullException.ThrowIfNull(input);
        lock (_gate) {
            if (_player.Status is "faulted" or "stopped" || _applicationToken.IsCancellationRequested) {
                return Reject(503, "session-unavailable", "The session is unavailable.");
            }
            PendingProgram? pending = _pending;
            if (!_webProgrammer || pending is null || input.ActivationId != pending.Request.ActivationId.Value) {
                return Reject(409, "stale-program", "This activation is no longer pending.");
            }
            var program = new List<Instruction>(input.Program.Count);
            try {
                foreach (RawInstruction raw in input.Program) {
                    program.Add(raw.Kind switch {
                        "move" when raw.Passage is { } passage => new MoveInstruction(new PassageHandle(passage)),
                        "think" when raw.Note is { } note => new ThinkInstruction(note),
                        "stay" when raw.Passage is null && raw.Note is null => new StayInstruction(),
                        _ => throw new ArgumentException(
                            $"'{raw.Kind}' with the given fields is not a valid instruction.")
                    });
                }
            } catch (ArgumentException) {
                return Reject(400, "invalid-program",
                    "Each program entry must be a well-formed move, think or stay instruction.");
            }
            var response = new ProgrammingResponse(
                new ActivationId(input.ActivationId), program, new SubjectiveWorkspace(input.Workspace));
            ProgrammingResponseValidationResult validation =
                ProgrammingResponseValidator.Validate(response, pending.Request);
            if (!validation.IsValid) {
                return Reject(400, "invalid-program",
                    validation.Message ?? "The program is not valid for the pending request.");
            }
            // Consume and publish before waking the run loop. No HTTP continuation publishes later.
            _pending = null;
            PublishStatus("advancing", null);
            pending.Answer.SetResult(response);
            return new(202, "accepted", "Program received; read the view for the committed result.");
        }
    }

    private async ValueTask<ProgrammingResponse> InterceptProgramAsync(
        ProgrammingRequest request, CancellationToken cancellationToken) {
        ValueTask<ProgrammingResponse> webAnswer = default;
        lock (_gate) {
            cancellationToken.ThrowIfCancellationRequested();
            _pending = new(request, new(TaskCreationOptions.RunContinuationsAsynchronously));
            // Capture the actual programmer's waiter before any HTTP caller can occupy it.
            if (_webProgrammer) { webAnswer = _programmer.ProgramAsync(request, cancellationToken); }
            PublishStatus("waiting", ViewOf(request));
        }
        ProgrammingResponse answer = _webProgrammer
            ? await webAnswer
            : await _programmer.ProgramAsync(request, cancellationToken);
        if (!_webProgrammer) {
            lock (_gate) {
                _pending = null;
                PublishStatus("advancing", null);
            }
        }
        return answer;
    }

    private ValueTask<ProgrammingResponse> WaitForHumanAsync(
        ProgrammingRequest request, CancellationToken cancellationToken) {
        lock (_gate) {
            if (_pending is null || _pending.Request.ActivationId != request.ActivationId) {
                throw new InvalidOperationException("Human waiter is missing.");
            }
            return new(_pending.Answer.Task.WaitAsync(cancellationToken));
        }
    }

    private void PublishCommitted() {
        var trajectory = new List<TrajectoryEntry> { new("A", 0, null) };
        TraversalStartedFact? departure = null;
        foreach (var occurrence in _history.CompletedEvents) {
            foreach (FreePlayFact fact in occurrence.Facts) {
                if (fact is SpatialFact { Fact: TraversalStartedFact started } && started.EntityId == FreePlayScene.Actor) {
                    departure = started;
                } else if (fact is SpatialFact { Fact: TraversalArrivedFact arrived } && arrived.EntityId == FreePlayScene.Actor) {
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
                Freeze(occurrence.Facts.Select(FreePlayFactKind)))));
        lock (_gate) {
            long revision = _player.ViewRevision + 1;
            string location = _scene.Location(_kernel.World);
            long time = _kernel.CurrentModelTime.Ticks;
            PlayerProcessState process = _kernel.World.Process;
            _player = _player with {
                ViewRevision = revision,
                ModelTimeMs = time,
                Location = location,
                Trajectory = Freeze(trajectory),
                Status = "advancing",
                Workspace = process.Workspace.Text,
                PendingProgram = Freeze(process.Pending.Items.Select(ViewOf)),
                Request = null
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
                ProcessMode = DescribeProcessMode(process.Mode),
                ControlRevision = process.ControlRevision,
                PendingActivationId = null
            };
            SignalChange();
        }
    }

    private static string FreePlayFactKind(FreePlayFact fact) => fact switch {
        SpatialFact spatial => spatial.Fact.GetType().Name,
        ProcessFact process => process.Fact.GetType().Name,
        _ => throw new InvalidOperationException($"The FreePlay scene does not support fact type '{fact.GetType().Name}'.")
    };

    private static InstructionView ViewOf(Instruction instruction) => instruction switch {
        MoveInstruction move => new("move", move.Passage.Value, null),
        ThinkInstruction think => new("think", null, think.Note),
        StayInstruction => new("stay", null, null),
        _ => throw new InvalidOperationException($"Unknown instruction '{instruction.GetType().Name}'.")
    };

    private static ProgrammingRequestView ViewOf(ProgrammingRequest request) => new(
        request.ActivationId.Value,
        request.Reason.ToString(),
        request.ReasonDetail,
        request.PlaceId,
        Freeze(request.KnownPassages.Select(passage => new KnownPassageView(
            passage.Handle.Value, passage.EndpointA, passage.EndpointB, passage.ExpectedDurationMs,
            passage.EnterableFromA, passage.EnterableFromB))),
        request.Workspace.Text,
        Freeze(request.RemainingProgram.Select(ViewOf)));

    private static string DescribeProcessMode(ProcessMode mode) => mode.GetType().Name;

    private SubmitResult Reject(int status, string code, string message) {
        _dev = _dev with { LastRejection = $"{code}: {message}" };
        PublishStatus(_player.Status, _player.Request);
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

    private void PublishStatus(string status, ProgrammingRequestView? request) {
        long revision = _player.ViewRevision + 1;
        PlayerProcessState process = _kernel.World.Process;
        _player = _player with {
            ViewRevision = revision,
            Status = status,
            Workspace = process.Workspace.Text,
            PendingProgram = Freeze(process.Pending.Items.Select(ViewOf)),
            Request = request
        };
        _dev = _dev with {
            ViewRevision = revision,
            Status = status,
            ProcessMode = DescribeProcessMode(process.Mode),
            ControlRevision = process.ControlRevision,
            PendingActivationId = request?.ActivationId
        };
        SignalChange();
    }

    private void SignalChange() {
        TaskCompletionSource previous = _changed;
        _changed = NewSignal();
        previous.SetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values) => Array.AsReadOnly(values.ToArray());
    private sealed record PendingProgram(ProgrammingRequest Request, TaskCompletionSource<ProgrammingResponse> Answer);

    private sealed class SessionProgrammer(
        Func<ProgrammingRequest, CancellationToken, ValueTask<ProgrammingResponse>> intercept) : IDynamicProgrammer {
        public ValueTask<ProgrammingResponse> ProgramAsync(
            ProgrammingRequest request, CancellationToken cancellationToken) =>
            intercept(request, cancellationToken);
    }
}

internal sealed class WebHumanProgrammer(
    Func<ProgrammingRequest, CancellationToken, ValueTask<ProgrammingResponse>> wait) : IDynamicProgrammer {
    public ValueTask<ProgrammingResponse> ProgramAsync(
        ProgrammingRequest request, CancellationToken cancellationToken) =>
        wait(request, cancellationToken);
}
