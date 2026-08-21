namespace DramaBoard.Spatial;

/// <summary>Base immutable data captured by one forecast Graph Spatial occurrence.</summary>
public abstract record SpatialOccurrenceData;

/// <summary>Captures one exact scheduled entry-access change to consume.</summary>
public sealed record PassageEntryChangeOccurrenceData(
    ScheduledPassageEntryChange Change) : SpatialOccurrenceData;

/// <summary>Captures one exact endpoint-to-endpoint segment to arrive.</summary>
public sealed record TraversalArrivalOccurrenceData(
    EntityId EntityId,
    long MovementGeneration,
    TraversingLocation Traversal) : SpatialOccurrenceData;
