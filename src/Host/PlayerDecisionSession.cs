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
    private readonly Func<TWorld, DomainEvent<TEventPayload>, WorldVersion, DecisionRequest> _requestBuilder;
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
        Func<TWorld, DomainEvent<TEventPayload>, WorldVersion, DecisionRequest> requestBuilder,
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
        IReadOnlyList<UncommittedDomainEvent<TEventPayload>>? externalInputs = null;
        int decisionCount = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SimulationRunResult<TWorld, TEventPayload> run = _loop.Run(
                _world,
                _cursor,
                until,
                _journal,
                externalInputs);
            _world = run.World;
            _cursor = run.Cursor;
            externalInputs = null;

            if (run.StopReason != StopReason.DecisionRequired)
            {
                return new PlayerDecisionSessionResult<TWorld>(
                    _world,
                    _cursor,
                    run.Version,
                    run.StopReason,
                    decisionCount);
            }

            LogicalTimestamp currentTimestamp = _journal.Events[^1].Timestamp;
            var translatedInputs = new List<UncommittedDomainEvent<TEventPayload>>();
            foreach (DomainEvent<TEventPayload> decisionEvent in run.DecisionEvents)
            {
                string actorId = _actorSelector(decisionEvent);
                if (string.IsNullOrWhiteSpace(actorId))
                {
                    throw new InvalidOperationException("The decision actor selector returned an empty actor identifier.");
                }

                if (!_drivers.TryGetValue(actorId, out IPlayerDriver? driver))
                {
                    throw new InvalidOperationException($"No Player driver is registered for actor '{actorId}'.");
                }

                DecisionRequest request = BuildRequest(decisionEvent, run.Version, currentTimestamp, actorId);
                PlayerDecision decision = await driver.DecideAsync(request, cancellationToken)
                    ?? throw new InvalidOperationException("A Player driver returned null.");
                ValidateDecision(decision, request, run.Version);

                IReadOnlyList<UncommittedDomainEvent<TEventPayload>> decisionInputs =
                    _decisionTranslator(decision, _world)
                    ?? throw new InvalidOperationException("The decision translator returned null.");
                translatedInputs.AddRange(decisionInputs);
                decisionCount = checked(decisionCount + 1);
            }

            externalInputs = translatedInputs;
        }
    }

    private DecisionRequest BuildRequest(
        DomainEvent<TEventPayload> decisionEvent,
        WorldVersion version,
        LogicalTimestamp currentTimestamp,
        string actorId)
    {
        DecisionRequest built = _requestBuilder(_world, decisionEvent, version)
            ?? throw new InvalidOperationException("The decision request builder returned null.");
        if (!string.Equals(built.ActorId, actorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The decision request actor '{built.ActorId}' does not match routed actor '{actorId}'.");
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
        DecisionRequest request,
        WorldVersion version)
    {
        if (decision.DecisionId != request.DecisionId)
        {
            throw new InvalidOperationException("The Player decision does not match the requested DecisionId.");
        }

        if (decision.BasedOnWorldVersion != version.EventCount)
        {
            throw new InvalidOperationException("The Player decision is based on a stale world version.");
        }

        if (decision.LineageId != version.LineageId)
        {
            throw new InvalidOperationException("The Player decision belongs to a different world lineage.");
        }
    }
}