using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial.Tests;

internal static class SpatialEventTestHarness
{
    public static SpatialState Apply(
        SpatialDefinition definition,
        SpatialState state,
        SpatialEvent payload,
        ModelTime? time = null,
        long causalOrdinal = 0)
    {
        return new SpatialReducer(definition).Apply(
            state,
            new LogicalInstant(time ?? ModelTime.Zero, causalOrdinal),
            payload);
    }

    public static SpatialState Place(
        SpatialDefinition definition,
        SpatialState state,
        long entityId = 1,
        CellRef? cell = null,
        bool observationEnabled = true) =>
        Apply(
            definition,
            state,
            new EntityPlacedEvent(new SpatialEntityState(
                new EntityId(entityId),
                cell ?? TestSpatialDefinitionBuilder.Cell("world", 0, 0),
                observationEnabled,
                movementGeneration: 0)));

    public static CurrentLeg Leg(
        CellRef from,
        CellRef to,
        long generation,
        long startedAtSeconds = 0,
        long dueSeconds = 1,
        SpatialEdgeKind edgeKind = SpatialEdgeKind.Orthogonal,
        PortalId? portalId = null) =>
        new(
            from,
            to,
            edgeKind,
            portalId,
            AtSecond(startedAtSeconds),
            AtSecond(dueSeconds),
            generation);

    public static ModelTime AtSecond(long seconds) => ModelTime.Zero + ModelDuration.FromSeconds(seconds);
}
