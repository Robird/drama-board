using System.Buffers;
using System.Text.Json;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial;

/// <summary>Private immutable data for one forecast Spatial occurrence.</summary>
public abstract record SpatialOccurrenceData(long ExpectedSpatialRevision);

/// <summary>Captures one exact scheduled mutation to consume.</summary>
public sealed record SpatialMutationOccurrenceData(
    long ExpectedRevision,
    ScheduledSpatialMutationState Mutation) : SpatialOccurrenceData(ExpectedRevision);

/// <summary>Captures one exact current journey leg to settle.</summary>
public sealed record SpatialArrivalOccurrenceData(
    long ExpectedRevision,
    JourneyState Journey) : SpatialOccurrenceData(ExpectedRevision);

/// <summary>Forecasts every pending Grid Spatial mutation and journey arrival independently.</summary>
public sealed class SpatialOccurrenceRule :
    IOccurrenceRule<SpatialState, SpatialOccurrenceData, SpatialEvent>
{
    private readonly SpatialDefinition _definition;

    /// <summary>Initializes the rule against immutable spatial content.</summary>
    public SpatialOccurrenceRule(SpatialDefinition definition)
    {
        SpatialRules.EnsureSupported(definition);
        _definition = definition;
    }

    /// <inheritdoc />
    public IReadOnlyList<OccurrenceCandidate<SpatialOccurrenceData>> Forecast(
        SpatialState world,
        SimulationRules rules)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(rules);
        SpatialStateValidator.ValidateComplete(_definition, world);

        var candidates = new List<OccurrenceCandidate<SpatialOccurrenceData>>(
            world.ScheduledMutations.Count + world.Journeys.Count);
        foreach (ScheduledSpatialMutationState mutation in world.ScheduledMutations)
        {
            candidates.Add(new OccurrenceCandidate<SpatialOccurrenceData>(
                CreateMutationKey(mutation),
                new CandidateDue(mutation.Due),
                new SpatialMutationOccurrenceData(world.Revision, mutation)));
        }

        foreach (JourneyState journey in world.Journeys)
        {
            candidates.Add(new OccurrenceCandidate<SpatialOccurrenceData>(
                CreateArrivalKey(journey),
                new CandidateDue(journey.CurrentLeg.Due),
                new SpatialArrivalOccurrenceData(world.Revision, journey)));
        }

        return candidates.AsReadOnly();
    }

    /// <inheritdoc />
    public ValueTask<TransitionDraft<SpatialEvent>> PlanSelectedAsync(
        SpatialState world,
        OccurrenceCandidate<SpatialOccurrenceData> winner,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(winner);
        cancellationToken.ThrowIfCancellationRequested();
        SpatialStateValidator.ValidateComplete(_definition, world);

        TransitionDraft<SpatialEvent> draft = winner.Data switch
        {
            SpatialMutationOccurrenceData mutation => PlanMutation(world, winner, mutation),
            SpatialArrivalOccurrenceData arrival => PlanArrival(world, winner, arrival),
            null => throw new InvalidOperationException("Spatial occurrence data is required."),
            _ => throw new InvalidOperationException(
                $"Unsupported Spatial occurrence data '{winner.Data.GetType().Name}'."),
        };
        return ValueTask.FromResult(draft);
    }

    private TransitionDraft<SpatialEvent> PlanMutation(
        SpatialState world,
        OccurrenceCandidate<SpatialOccurrenceData> winner,
        SpatialMutationOccurrenceData data)
    {
        if (data.ExpectedSpatialRevision != world.Revision)
        {
            throw new InvalidOperationException("The selected Spatial mutation candidate is stale.");
        }

        ScheduledSpatialMutationState current = world.ScheduledMutations.SingleOrDefault(
            mutation => mutation.Id == data.Mutation.Id)
            ?? throw new InvalidOperationException("The selected Spatial mutation no longer exists.");
        if (current != data.Mutation ||
            winner.Due.ModelTime != current.Due ||
            winner.Key != CreateMutationKey(current))
        {
            throw new InvalidOperationException("The selected Spatial mutation does not match current state.");
        }

        var body = new List<SpatialEvent>(2);
        SpatialEvent? valueEvent = CreateMutationValueEvent(world, current.Mutation);
        if (valueEvent is not null)
        {
            body.Add(valueEvent);
        }

        // Consumption is authoritative local progress even when the target value is already effective.
        body.Add(new MutationConsumedEvent(current));
        SpatialTransitionResult transition = SpatialTransition.Complete(
            _definition,
            world,
            current.Due,
            body);
        return new TransitionDraft<SpatialEvent>(transition.Facts);
    }

    private TransitionDraft<SpatialEvent> PlanArrival(
        SpatialState world,
        OccurrenceCandidate<SpatialOccurrenceData> winner,
        SpatialArrivalOccurrenceData data)
    {
        if (data.ExpectedSpatialRevision != world.Revision)
        {
            throw new InvalidOperationException("The selected Spatial arrival candidate is stale.");
        }

        JourneyState current = SpatialStateValidator.RequireJourney(
            world,
            data.Journey.EntityId,
            data.Journey.Id);
        if (current != data.Journey ||
            winner.Due.ModelTime != current.CurrentLeg.Due ||
            winner.Key != CreateArrivalKey(current))
        {
            throw new InvalidOperationException("The selected Spatial arrival does not match current state.");
        }

        ModelTime modelTime = current.CurrentLeg.Due;
        var topology = new EffectiveSpatialTopology(_definition, world);
        bool stepped = topology.IsLegPassable(current.CurrentLeg);
        SpatialState working = world;
        var body = new List<SpatialEvent>(2);
        if (stepped)
        {
            CurrentLeg leg = current.CurrentLeg;
            var step = new EntitySteppedEvent(
                current.EntityId,
                current.Id,
                leg.From,
                leg.To,
                current.Generation);
            working = SpatialProjector.Apply(_definition, working, step, modelTime);
            body.Add(step);
        }

        body.Add(CreateJourneyOutcome(working, current, stepped, modelTime));
        SpatialTransitionResult transition = SpatialTransition.Complete(
            _definition,
            world,
            modelTime,
            body);
        return new TransitionDraft<SpatialEvent>(transition.Facts);
    }

    private SpatialEvent? CreateMutationValueEvent(
        SpatialState state,
        ScheduledSpatialMutation mutation) =>
        mutation switch
        {
            SetPortalStateMutation portal => CreatePortalValueEvent(state, portal),
            SetCellOverrideMutation cell => CreateCellValueEvent(state, cell),
            _ => throw new InvalidOperationException(
                $"Unsupported scheduled Spatial mutation '{mutation.GetType().Name}'."),
        };

    private PortalStateChangedEvent? CreatePortalValueEvent(
        SpatialState state,
        SetPortalStateMutation mutation)
    {
        PortalDefinition definition = SpatialStateValidator.RequirePortal(_definition, mutation.PortalId);
        PortalOverrideState? current = state.PortalOverrides.SingleOrDefault(
            value => value.PortalId == mutation.PortalId);
        bool effective = current?.IsEnabled ?? definition.InitiallyEnabled;
        if (effective == mutation.IsEnabled)
        {
            return null;
        }

        bool? resultingOverride = mutation.IsEnabled == definition.InitiallyEnabled
            ? null
            : mutation.IsEnabled;
        return new PortalStateChangedEvent(
            mutation.PortalId,
            current?.IsEnabled,
            resultingOverride);
    }

    private static CellStateChangedEvent? CreateCellValueEvent(
        SpatialState state,
        SetCellOverrideMutation mutation)
    {
        CellOverride? current = state.CellOverrides.SingleOrDefault(
            value => value.Cell == mutation.Cell)?.Value;
        return current == mutation.Value
            ? null
            : new CellStateChangedEvent(mutation.Cell, current, mutation.Value);
    }

    private SpatialEvent CreateJourneyOutcome(
        SpatialState state,
        JourneyState originalJourney,
        bool stepped,
        ModelTime modelTime)
    {
        SpatialEntityState entity = SpatialStateValidator.RequireEntity(state, originalJourney.EntityId);
        if (stepped && SpatialStateValidator.IsGoalSatisfied(
                _definition,
                entity.Cell,
                originalJourney.Goal))
        {
            return new JourneyCompletedEvent(
                originalJourney.EntityId,
                originalJourney.Id,
                originalJourney.Goal,
                originalJourney.Generation,
                originalJourney.Generation,
                JourneyCompletionReason.ReachedGoal,
                originalJourney.CurrentLeg);
        }

        PathSearchResult search = SpatialNavigator.FindNextStep(
            _definition,
            state,
            entity.Cell,
            originalJourney.Goal);
        if (search is PathSearchResult.NextStep next &&
            TryCreateLeg(next.Edge, modelTime, originalJourney.Generation, out CurrentLeg resultingLeg))
        {
            return stepped
                ? new JourneyContinuedEvent(
                    originalJourney.EntityId,
                    originalJourney.Id,
                    originalJourney.CurrentLeg,
                    resultingLeg)
                : new JourneyReroutedEvent(
                    originalJourney.EntityId,
                    originalJourney.Id,
                    originalJourney.CurrentLeg,
                    resultingLeg);
        }

        if (search is PathSearchResult.AlreadySatisfied)
        {
            throw new InvalidOperationException(
                $"Journey '{originalJourney.Id}' navigation satisfaction disagrees with its authoritative goal.");
        }

        return new JourneyBlockedEvent(
            originalJourney.EntityId,
            originalJourney.Id,
            originalJourney.CurrentLeg,
            stepped
                ? JourneyBlockedReason.NoContinuationAfterStep
                : JourneyBlockedReason.LegInvalidNoRoute);
    }

    private static bool TryCreateLeg(
        NavigationEdge edge,
        ModelTime modelTime,
        long generation,
        out CurrentLeg leg)
    {
        try
        {
            leg = new CurrentLeg(
                edge.From,
                edge.To,
                edge.EdgeKind,
                edge.PortalId,
                modelTime,
                modelTime + edge.Duration,
                generation);
            return true;
        }
        catch (OverflowException)
        {
            leg = null!;
            return false;
        }
    }

    private static CandidateKey CreateMutationKey(ScheduledSpatialMutationState mutation)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartArray();
        writer.WriteStringValue("spatial/mutation/v1");
        writer.WriteNumberValue(mutation.Id.Value);
        writer.WriteNumberValue(mutation.Due.Ticks);
        switch (mutation.Mutation)
        {
            case SetPortalStateMutation portal:
                writer.WriteStringValue("portal");
                writer.WriteStringValue(portal.PortalId.Value);
                writer.WriteBooleanValue(portal.IsEnabled);
                break;
            case SetCellOverrideMutation cell:
                writer.WriteStringValue("cell");
                WriteCell(writer, cell.Cell);
                if (cell.Value is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    writer.WriteStartArray();
                    WriteNullableBoolean(writer, cell.Value.BlocksMovement);
                    WriteNullableBoolean(writer, cell.Value.BlocksSight);
                    if (cell.Value.MoveCost is int moveCost)
                    {
                        writer.WriteNumberValue(moveCost);
                    }
                    else
                    {
                        writer.WriteNullValue();
                    }

                    writer.WriteEndArray();
                }

                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported scheduled Spatial mutation '{mutation.Mutation.GetType().Name}'.");
        }

        writer.WriteEndArray();
        writer.Flush();
        return CandidateKey.FromBytes(buffer.WrittenSpan);
    }

    private static CandidateKey CreateArrivalKey(JourneyState journey)
    {
        CurrentLeg leg = journey.CurrentLeg;
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartArray();
        writer.WriteStringValue("spatial/arrival/v1");
        writer.WriteNumberValue(journey.EntityId.Value);
        writer.WriteNumberValue(journey.Id.Value);
        writer.WriteNumberValue(journey.Generation);
        WriteGoal(writer, journey.Goal);
        WriteCell(writer, leg.From);
        WriteCell(writer, leg.To);
        writer.WriteNumberValue((int)leg.EdgeKind);
        if (leg.PortalId is PortalId portalId)
        {
            writer.WriteStringValue(portalId.Value);
        }
        else
        {
            writer.WriteNullValue();
        }

        writer.WriteNumberValue(leg.StartedAt.Ticks);
        writer.WriteNumberValue(leg.Due.Ticks);
        writer.WriteEndArray();
        writer.Flush();
        return CandidateKey.FromBytes(buffer.WrittenSpan);
    }

    private static void WriteGoal(Utf8JsonWriter writer, MoveGoal goal)
    {
        writer.WriteStartArray();
        switch (goal)
        {
            case CellGoal cell:
                writer.WriteStringValue("cell");
                WriteCell(writer, cell.Cell);
                break;
            case AnchorGoal anchor:
                writer.WriteStringValue("anchor");
                writer.WriteStringValue(anchor.AnchorId.Value);
                break;
            case ZoneGoal zone:
                writer.WriteStringValue("zone");
                writer.WriteStringValue(zone.ZoneId.Value);
                break;
            default:
                throw new InvalidOperationException($"Unsupported movement goal '{goal.GetType().Name}'.");
        }

        writer.WriteEndArray();
    }

    private static void WriteCell(Utf8JsonWriter writer, CellRef cell)
    {
        writer.WriteStartArray();
        writer.WriteStringValue(cell.MapId.Value);
        writer.WriteNumberValue(cell.X);
        writer.WriteNumberValue(cell.Y);
        writer.WriteEndArray();
    }

    private static void WriteNullableBoolean(Utf8JsonWriter writer, bool? value)
    {
        if (value is bool present)
        {
            writer.WriteBooleanValue(present);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
