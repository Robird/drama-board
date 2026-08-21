using System.Buffers;
using System.Text.Json;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;

namespace DramaBoard.Spatial;

/// <summary>Forecasts every scheduled entry change and traversal arrival independently.</summary>
public sealed class SpatialOccurrenceRule :
    IOccurrenceRule<GraphSpatialState, SpatialOccurrenceData, GraphSpatialFact>
{
    private readonly GraphDefinition _definition;

    public SpatialOccurrenceRule(GraphDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _definition = definition;
    }

    public IReadOnlyList<OccurrenceCandidate<SpatialOccurrenceData>> Forecast(
        GraphSpatialState world,
        SimulationRules rules)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(rules);
        GraphSpatialStateValidator.ValidateComplete(_definition, world);

        int arrivalCount = world.Entities.Count(entity => entity.Location is TraversingLocation);
        var candidates = new List<OccurrenceCandidate<SpatialOccurrenceData>>(
            world.ScheduledPassageEntryChanges.Count + arrivalCount);
        foreach (ScheduledPassageEntryChange change in world.ScheduledPassageEntryChanges)
        {
            var data = new PassageEntryChangeOccurrenceData(change);
            candidates.Add(new OccurrenceCandidate<SpatialOccurrenceData>(
                CreateEntryChangeKey(change),
                new CandidateDue(change.Due),
                data));
        }

        foreach (SpatialEntity entity in world.Entities)
        {
            if (entity.Location is not TraversingLocation traversal)
            {
                continue;
            }

            var data = new TraversalArrivalOccurrenceData(
                entity.Id,
                entity.MovementGeneration,
                traversal);
            candidates.Add(new OccurrenceCandidate<SpatialOccurrenceData>(
                CreateArrivalKey(data),
                new CandidateDue(traversal.ArrivalDue),
                data));
        }

        return candidates.AsReadOnly();
    }

    public ValueTask<TransitionDraft<GraphSpatialFact>> PlanSelectedAsync(
        GraphSpatialState world,
        OccurrenceCandidate<SpatialOccurrenceData> winner,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(winner);
        cancellationToken.ThrowIfCancellationRequested();
        GraphSpatialStateValidator.ValidateComplete(_definition, world);

        TransitionDraft<GraphSpatialFact> draft = winner.Data switch
        {
            PassageEntryChangeOccurrenceData change => PlanEntryChange(world, winner, change),
            TraversalArrivalOccurrenceData arrival => PlanArrival(world, winner, arrival),
            null => throw new InvalidOperationException("Graph Spatial occurrence data is required."),
            _ => throw new InvalidOperationException(
                $"Unsupported Graph Spatial occurrence data '{winner.Data.GetType().Name}'."),
        };
        return ValueTask.FromResult(draft);
    }

    private static TransitionDraft<GraphSpatialFact> PlanEntryChange(
        GraphSpatialState world,
        OccurrenceCandidate<SpatialOccurrenceData> winner,
        PassageEntryChangeOccurrenceData data)
    {
        ScheduledPassageEntryChange current = world.FindSchedule(
            data.Change.PassageId,
            data.Change.Due)
            ?? throw new InvalidOperationException("The selected passage entry change no longer exists.");
        if (current != data.Change ||
            winner.Due.ModelTime != current.Due ||
            winner.Key != CreateEntryChangeKey(current))
        {
            throw new InvalidOperationException(
                "The selected passage entry change does not match current state.");
        }

        return new TransitionDraft<GraphSpatialFact>(
            [new ScheduledPassageEntryChangeAppliedFact(current.PassageId, current.Due)]);
    }

    private static TransitionDraft<GraphSpatialFact> PlanArrival(
        GraphSpatialState world,
        OccurrenceCandidate<SpatialOccurrenceData> winner,
        TraversalArrivalOccurrenceData data)
    {
        SpatialEntity current = GraphSpatialStateValidator.RequireEntity(world, data.EntityId);
        if (current.MovementGeneration != data.MovementGeneration ||
            current.Location is not TraversingLocation traversal ||
            traversal != data.Traversal)
        {
            throw new InvalidOperationException("The selected traversal arrival no longer exists.");
        }

        var currentData = new TraversalArrivalOccurrenceData(
            current.Id,
            current.MovementGeneration,
            traversal);
        if (winner.Due.ModelTime != traversal.ArrivalDue ||
            winner.Key != CreateArrivalKey(currentData))
        {
            throw new InvalidOperationException("The selected traversal arrival does not match current state.");
        }

        return new TransitionDraft<GraphSpatialFact>(
            [new TraversalArrivedFact(current.Id, current.MovementGeneration)]);
    }

    private static CandidateKey CreateEntryChangeKey(ScheduledPassageEntryChange change)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartArray();
        writer.WriteStringValue("graph-spatial/entry-change");
        writer.WriteStringValue(change.PassageId.Value);
        writer.WriteNumberValue(change.Due.Ticks);
        WriteNullableBoolean(writer, change.Patch.EnterableFromA);
        WriteNullableBoolean(writer, change.Patch.EnterableFromB);
        writer.WriteEndArray();
        writer.Flush();
        return CandidateKey.FromBytes(buffer.WrittenSpan);
    }

    private static CandidateKey CreateArrivalKey(TraversalArrivalOccurrenceData data)
    {
        TraversingLocation traversal = data.Traversal;
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartArray();
        writer.WriteStringValue("graph-spatial/arrival");
        writer.WriteStringValue(data.EntityId.Value);
        writer.WriteNumberValue(data.MovementGeneration);
        writer.WriteStringValue(traversal.PassageId.Value);
        writer.WriteStringValue(traversal.FromPlaceId.Value);
        writer.WriteStringValue(traversal.ToPlaceId.Value);
        writer.WriteNumberValue(traversal.StartedAt.Ticks);
        writer.WriteNumberValue(traversal.SpeedSnapshot);
        writer.WriteNumberValue(traversal.ArrivalDue.Ticks);
        writer.WriteEndArray();
        writer.Flush();
        return CandidateKey.FromBytes(buffer.WrittenSpan);
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
