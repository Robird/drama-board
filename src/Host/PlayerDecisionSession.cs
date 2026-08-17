using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Protocol;

namespace DramaBoard.Host;

/// <summary>Sequentially advances a simulation and turns decision requests into external input events.</summary>
public sealed class PlayerDecisionSession<TWorld, TCandidatePayload, TEventPayload>
{
    private readonly SimulationLoop<TWorld, TCandidatePayload, TEventPayload> _loop;
    private readonly IJournalSink<TEventPayload> _journal;
    private readonly Func<DomainEvent<TEventPayload>, string> _actorSelector;
    private readonly IReadOnlyDictionary<string, IPlayerDriver> _drivers;
    private readonly Func<TWorld, DomainEvent<TEventPayload>, WorldVersion, DecisionRequest?> _requestBuilder;
    private readonly Func<PlayerDecision, TWorld, IReadOnlyList<UncommittedDomainEvent<TEventPayload>>> _decisionTranslator;
    private TWorld _world;
    private SimulationCursor _cursor;

    /// <summary>Initializes a session from its simulation state and domain translation functions.</summary>
    public PlayerDecisionSession(
        SimulationLoop<TWorld, TCandidatePayload, TEventPayload> loop,
        IJournalSink<TEventPayload> journal,
        TWorld initialWorld,
        SimulationCursor initialCursor,
        Func<DomainEvent<TEventPayload>, string> actorSelector,
        IReadOnlyDictionary<string, IPlayerDriver> drivers,
        Func<TWorld, DomainEvent<TEventPayload>, WorldVersion, DecisionRequest?> requestBuilder,
        Func<PlayerDecision, TWorld, IReadOnlyList<UncommittedDomainEvent<TEventPayload>>> decisionTranslator)
    {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(initialCursor);
        ArgumentNullException.ThrowIfNull(actorSelector);
        ArgumentNullException.ThrowIfNull(drivers);
        ArgumentNullException.ThrowIfNull(requestBuilder);
        ArgumentNullException.ThrowIfNull(decisionTranslator);

        var driverCopy = new Dictionary<string, IPlayerDriver>(StringComparer.Ordinal);
        foreach ((string actorId, IPlayerDriver driver) in drivers)
        {
            if (string.IsNullOrWhiteSpace(actorId))
            {
                throw new ArgumentException("Player driver actor identifiers cannot be empty.", nameof(drivers));
            }

            if (driver is null)
            {
                throw new ArgumentException("Player drivers cannot contain null values.", nameof(drivers));
            }

            driverCopy.Add(actorId, driver);
        }

        _loop = loop;
        _journal = journal;
        _world = initialWorld;
        _cursor = initialCursor;
        _actorSelector = actorSelector;
        _drivers = driverCopy;
        _requestBuilder = requestBuilder;
        _decisionTranslator = decisionTranslator;
    }

    /// <summary>Runs through decision points until the simulation is exhausted or reaches the boundary.</summary>
    public async ValueTask<PlayerDecisionSessionResult<TWorld>> RunUntilAsync(
        ModelTime until,
        CancellationToken cancellationToken = default)
    {
        var pendingDecisionEvents = new Queue<DomainEvent<TEventPayload>>();
        int decisionCount = 0;
        int skippedDecisionCount = 0;

        cancellationToken.ThrowIfCancellationRequested();
        SimulationRunResult<TWorld, TEventPayload> run = RunSimulation(until, externalInputs: null);
        EnqueueDecisionEvents(run, pendingDecisionEvents);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!pendingDecisionEvents.TryDequeue(out DomainEvent<TEventPayload>? decisionEvent))
            {
                if (run.StopReason != StopReason.DecisionRequired)
                {
                    return new PlayerDecisionSessionResult<TWorld>(
                        _world,
                        _cursor,
                        run.Version,
                        run.StopReason,
                        decisionCount,
                        skippedDecisionCount);
                }

                run = RunSimulation(until, externalInputs: null);
                EnqueueDecisionEvents(run, pendingDecisionEvents);
                continue;
            }

            LogicalTimestamp currentTimestamp = _journal.Events[^1].Timestamp;
            DecisionRequest? request = BuildRequest(decisionEvent, run.Version, currentTimestamp);
            if (request is null)
            {
                skippedDecisionCount = checked(skippedDecisionCount + 1);
                continue;
            }

            string actorId = _actorSelector(decisionEvent);
            if (string.IsNullOrWhiteSpace(actorId))
            {
                throw new InvalidOperationException("The decision actor selector returned an empty actor identifier.");
            }

            if (!string.Equals(request.ActorId, actorId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The decision request actor '{request.ActorId}' does not match routed actor '{actorId}'.");
            }

            if (!_drivers.TryGetValue(actorId, out IPlayerDriver? driver))
            {
                throw new InvalidOperationException($"No Player driver is registered for actor '{actorId}'.");
            }

            PlayerDecision decision = await driver.DecideAsync(request, cancellationToken)
                ?? throw new InvalidOperationException("A Player driver returned null.");
            ValidateDecision(decision, request);

            IReadOnlyList<UncommittedDomainEvent<TEventPayload>> decisionInputs =
                _decisionTranslator(decision, _world)
                ?? throw new InvalidOperationException("The decision translator returned null.");
            decisionCount = checked(decisionCount + 1);
            if (decisionInputs.Count == 0)
            {
                continue;
            }

            run = RunSimulation(until, decisionInputs);
            EnqueueDecisionEvents(run, pendingDecisionEvents);
        }
    }

    private SimulationRunResult<TWorld, TEventPayload> RunSimulation(
        ModelTime until,
        IReadOnlyList<UncommittedDomainEvent<TEventPayload>>? externalInputs)
    {
        SimulationRunResult<TWorld, TEventPayload> run = _loop.Run(
            _world,
            _cursor,
            until,
            _journal,
            externalInputs);
        _world = run.World;
        _cursor = run.Cursor;
        return run;
    }

    private static void EnqueueDecisionEvents(
        SimulationRunResult<TWorld, TEventPayload> run,
        Queue<DomainEvent<TEventPayload>> pendingDecisionEvents)
    {
        foreach (DomainEvent<TEventPayload> decisionEvent in run.DecisionEvents)
        {
            pendingDecisionEvents.Enqueue(decisionEvent);
        }
    }

    private DecisionRequest? BuildRequest(
        DomainEvent<TEventPayload> decisionEvent,
        WorldVersion version,
        LogicalTimestamp currentTimestamp)
    {
        DecisionRequest? built = _requestBuilder(_world, decisionEvent, version);
        if (built is null)
        {
            return null;
        }

        Observation observation = built.Observation
            ?? throw new InvalidOperationException("The decision request builder returned a null observation.");
        observation = observation with
        {
            ModelTimeMs = currentTimestamp.ModelTime.Ticks,
            Microstep = currentTimestamp.Microstep.Value,
        };

        return built with
        {
            BasedOnWorldVersion = version.EventCount,
            LineageId = version.LineageId,
            ModelTimeMs = currentTimestamp.ModelTime.Ticks,
            Microstep = currentTimestamp.Microstep.Value,
            Observation = observation,
        };
    }

    private static void ValidateDecision(
        PlayerDecision decision,
        DecisionRequest request)
    {
        if (decision.DecisionId != request.DecisionId)
        {
            throw new InvalidOperationException("The Player decision does not match the requested DecisionId.");
        }

        if (decision.BasedOnWorldVersion != request.BasedOnWorldVersion)
        {
            throw new InvalidOperationException("The Player decision is based on a stale world version.");
        }

        if (decision.LineageId != request.LineageId)
        {
            throw new InvalidOperationException("The Player decision belongs to a different world lineage.");
        }
    }
}
