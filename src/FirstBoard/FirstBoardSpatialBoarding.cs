using System.Buffers;
using System.Text.Json;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.FirstBoard;

/// <summary>Combines the current FirstBoard game projection and Grid Spatial projection.</summary>
public sealed record FirstBoardSpatialWorld(FirstBoardWorld Game, SpatialState Spatial);

/// <summary>Base fact for the first concrete FirstBoard + Spatial atomic transition.</summary>
public abstract record FirstBoardSpatialFact;

/// <summary>Consumes one exact game object as the ticket for a traversal.</summary>
public sealed record TicketConsumedFact(
    string ActorId,
    string TicketObjectId) : FirstBoardSpatialFact;

/// <summary>Wraps one raw Spatial fact in the same cross-domain transition.</summary>
public sealed record SpatialBoardingFact(SpatialEvent Value) : FirstBoardSpatialFact;

/// <summary>Frozen data owned by one ticketed-traversal occurrence.</summary>
public sealed record TicketedTraversalCandidate(
    BoardActor Actor,
    BoardObject Ticket,
    SpatialEntityState Entity,
    CellRef Destination);

/// <summary>Folds the first concrete cross-domain fact union.</summary>
public sealed class FirstBoardSpatialReducer
{
    private readonly SpatialDefinition _definition;
    private readonly SpatialReducer _spatialReducer;

    /// <summary>Creates a reducer pinned to immutable Spatial content.</summary>
    public FirstBoardSpatialReducer(SpatialDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _definition = definition;
        _spatialReducer = new SpatialReducer(definition);
    }

    /// <summary>Applies one game or Spatial fact at the batch-shared instant.</summary>
    public FirstBoardSpatialWorld Apply(
        FirstBoardSpatialWorld world,
        LogicalInstant instant,
        FirstBoardSpatialFact fact)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(fact);
        return fact switch
        {
            TicketConsumedFact ticket => world with
            {
                Game = ConsumeTicket(world.Game, ticket, instant.ModelTime),
            },
            SpatialBoardingFact spatial => world with
            {
                Spatial = _spatialReducer.Apply(world.Spatial, instant, spatial.Value),
            },
            _ => throw new InvalidOperationException(
                $"Unknown FirstBoard + Spatial fact '{fact.GetType().Name}'."),
        };
    }

    /// <summary>Validates both projections at a complete transition boundary.</summary>
    public void Validate(FirstBoardSpatialWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        SpatialStateValidator.ValidateComplete(_definition, world.Spatial);
        EnsureUnique(world.Game.Actors.Select(actor => actor.Id), "actor id");
        EnsureUnique(world.Game.Actors.Select(actor => actor.Key), "actor key");
        EnsureUnique(world.Game.Objects.Select(item => item.Id), "object id");
        EnsureUnique(world.Game.Objects.Select(item => item.Key), "object key");
        if (world.Game.Objects.Any(item =>
                item.OwnerActorId is long ownerId &&
                world.Game.Actors.All(actor => actor.Id != ownerId)))
        {
            throw new InvalidOperationException("A FirstBoard object owner must exist.");
        }
    }

    private static FirstBoardWorld ConsumeTicket(
        FirstBoardWorld game,
        TicketConsumedFact fact,
        ModelTime modelTime)
    {
        BoardActor actor = game.Actors.SingleOrDefault(value => value.Key == fact.ActorId)
            ?? throw new InvalidOperationException($"Ticket actor '{fact.ActorId}' does not exist.");
        BoardObject ticket = game.Objects.SingleOrDefault(value => value.Key == fact.TicketObjectId)
            ?? throw new InvalidOperationException($"Ticket '{fact.TicketObjectId}' does not exist.");
        if (ticket.OwnerActorId != actor.Id)
        {
            throw new InvalidOperationException(
                $"Ticket '{fact.TicketObjectId}' is not owned by actor '{fact.ActorId}'.");
        }

        return game with
        {
            Now = modelTime,
            Objects = Array.AsReadOnly(game.Objects.Where(value => value.Id != ticket.Id).ToArray()),
        };
    }

    private static void EnsureUnique<T>(IEnumerable<T> values, string description)
    {
        var known = new HashSet<T>();
        if (values.Any(value => !known.Add(value)))
        {
            throw new InvalidOperationException($"FirstBoard {description} values must be unique.");
        }
    }
}

/// <summary>Plans one ticket consumption and one real Grid traversal start as one draft.</summary>
public sealed class TicketedTraversalRule :
    IOccurrenceRule<FirstBoardSpatialWorld, TicketedTraversalCandidate, FirstBoardSpatialFact>
{
    private readonly SpatialDefinition _definition;
    private readonly SpatialCommandHandler _spatialCommands;
    private readonly string _actorId;
    private readonly string _ticketObjectId;
    private readonly EntityId _entityId;
    private readonly CellRef _destination;

    /// <summary>Creates the first concrete Game + Spatial atomic occurrence.</summary>
    public TicketedTraversalRule(
        SpatialDefinition definition,
        string actorId,
        string ticketObjectId,
        EntityId entityId,
        CellRef destination)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticketObjectId);
        if (!definition.Contains(destination))
        {
            throw new ArgumentException("Traversal destination must exist in the Spatial definition.", nameof(destination));
        }

        _definition = definition;
        _spatialCommands = new SpatialCommandHandler(definition);
        _actorId = actorId;
        _ticketObjectId = ticketObjectId;
        _entityId = entityId;
        _destination = destination;
    }

    /// <inheritdoc />
    public IReadOnlyList<OccurrenceCandidate<TicketedTraversalCandidate>> Forecast(
        FirstBoardSpatialWorld world,
        SimulationRules rules)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(rules);
        SpatialStateValidator.ValidateComplete(_definition, world.Spatial);
        OccurrenceCandidate<TicketedTraversalCandidate>? candidate = CreateCurrentCandidate(world);
        return candidate is null ? [] : [candidate];
    }

    /// <inheritdoc />
    public ValueTask<TransitionDraft<FirstBoardSpatialFact>> PlanSelectedAsync(
        FirstBoardSpatialWorld world,
        OccurrenceCandidate<TicketedTraversalCandidate> winner,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(winner);
        cancellationToken.ThrowIfCancellationRequested();

        TicketedTraversalCandidate expected = winner.Data
            ?? throw new InvalidOperationException("Ticketed traversal candidate data is required.");
        OccurrenceCandidate<TicketedTraversalCandidate> current = CreateCurrentCandidate(world)
            ?? throw new InvalidOperationException("The selected ticketed traversal is stale.");
        if (current.Key != winner.Key ||
            current.Due != winner.Due ||
            current.Data != expected)
        {
            throw new InvalidOperationException("The selected ticketed traversal no longer matches the world.");
        }

        SpatialCommandPlan movement = _spatialCommands.Handle(
            world.Spatial,
            new AssignMoveGoalCommand(
                new SpatialCommandId($"ticketed-traversal-{expected.Ticket.Id}"),
                expected.Entity.Id,
                new CellGoal(expected.Destination)),
            winner.Due.ModelTime);
        if (movement.Result.Disposition != SpatialCommandDisposition.Accepted)
        {
            throw new InvalidOperationException(
                $"Spatial traversal was rejected: {movement.Result.RejectionCode}.");
        }

        if (!movement.Facts.Any(fact => fact is JourneyStartedEvent))
        {
            throw new InvalidOperationException("A ticketed traversal must start one real Spatial journey.");
        }

        FirstBoardSpatialFact[] facts =
        [
            new TicketConsumedFact(expected.Actor.Key, expected.Ticket.Key),
            .. movement.Facts.Select(fact => new SpatialBoardingFact(fact)),
        ];
        return ValueTask.FromResult(new TransitionDraft<FirstBoardSpatialFact>(facts));
    }

    private OccurrenceCandidate<TicketedTraversalCandidate>? CreateCurrentCandidate(
        FirstBoardSpatialWorld world)
    {
        BoardActor? actor = world.Game.Actors.SingleOrDefault(value => value.Key == _actorId);
        BoardObject? ticket = world.Game.Objects.SingleOrDefault(value => value.Key == _ticketObjectId);
        if (actor is null ||
            ticket?.OwnerActorId != actor.Id ||
            !world.Game.IsIdle(actor) ||
            !world.Spatial.TryGetEntity(_entityId, out SpatialEntityState? entity) ||
            entity!.Cell == _destination ||
            world.Spatial.TryGetJourney(_entityId, out _))
        {
            return null;
        }

        var data = new TicketedTraversalCandidate(actor, ticket, entity, _destination);
        return new OccurrenceCandidate<TicketedTraversalCandidate>(
            CreateKey(data),
            new CandidateDue(world.Game.Now),
            data);
    }

    private static CandidateKey CreateKey(TicketedTraversalCandidate data)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartArray();
        writer.WriteStringValue("firstboard/ticketed-traversal/v1");
        writer.WriteStringValue(data.Actor.Key);
        writer.WriteNumberValue(data.Actor.Generation);
        writer.WriteStringValue(data.Ticket.Key);
        writer.WriteNumberValue(data.Ticket.Id);
        writer.WriteNumberValue(data.Entity.Id.Value);
        writer.WriteNumberValue(data.Entity.MovementGeneration);
        writer.WriteStringValue(data.Destination.MapId.Value);
        writer.WriteNumberValue(data.Destination.X);
        writer.WriteNumberValue(data.Destination.Y);
        writer.WriteEndArray();
        writer.Flush();
        return CandidateKey.FromBytes(buffer.WrittenSpan);
    }
}

/// <summary>Builds the concrete ticketed-traversal Kernel at a committed boundary.</summary>
public static class FirstBoardSpatialBoarding
{
    /// <summary>Creates one production Game + Spatial vertical slice.</summary>
    public static SimulationKernel<
        FirstBoardSpatialWorld,
        TicketedTraversalCandidate,
        FirstBoardSpatialFact> CreateKernel(
        FirstBoardSpatialWorld world,
        SpatialDefinition definition,
        string actorId,
        string ticketObjectId,
        EntityId entityId,
        CellRef destination,
        IJournalSink<FirstBoardSpatialFact> journal,
        WorldVersion? version = null,
        LogicalInstant? lastCommittedInstant = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(journal);
        var reducer = new FirstBoardSpatialReducer(definition);
        return new SimulationKernel<
            FirstBoardSpatialWorld,
            TicketedTraversalCandidate,
            FirstBoardSpatialFact>(
            world,
            version ?? new WorldVersion(journal.LineageId, journal.Batches.Count),
            world.Game.Now,
            lastCommittedInstant,
            new SimulationRules(world.Game.WorldSeed, maxTransitionsPerModelTime: 100),
            [new TicketedTraversalRule(definition, actorId, ticketObjectId, entityId, destination)],
            journal,
            reducer.Apply,
            reducer.Validate);
    }
}
