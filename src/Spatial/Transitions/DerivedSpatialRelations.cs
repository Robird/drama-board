namespace DramaBoard.Spatial;

/// <summary>Computes non-authoritative spatial relationship outcomes from two complete states.</summary>
internal static class DerivedSpatialRelations
{
    /// <summary>
    /// Returns one canonical Zone, CoPresence, and tracked-visibility delta sequence.
    /// </summary>
    internal static IReadOnlyList<SpatialEvent> Diff(
        SpatialDefinition definition,
        SpatialState preState,
        SpatialState postState)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(preState);
        ArgumentNullException.ThrowIfNull(postState);
        SpatialStateValidator.ValidateComplete(definition, preState);
        SpatialStateValidator.ValidateComplete(definition, postState);
        return DiffValidated(definition, preState, postState);
    }

    internal static IReadOnlyList<SpatialEvent> DiffValidated(
        SpatialDefinition definition,
        SpatialState preState,
        SpatialState postState)
    {
        HashSet<ZoneRelation> preZones = ProjectZones(definition, preState);
        HashSet<ZoneRelation> postZones = ProjectZones(definition, postState);
        HashSet<CoPresenceRelation> preCoPresence = ProjectCoPresence(preState);
        HashSet<CoPresenceRelation> postCoPresence = ProjectCoPresence(postState);
        var events = new List<SpatialEvent>();

        events.AddRange(preZones
            .Except(postZones)
            .Select(relation => new ZoneDelta(relation, IsEntered: false))
            .Concat(postZones
                .Except(preZones)
                .Select(relation => new ZoneDelta(relation, IsEntered: true)))
            .OrderBy(delta => delta.Relation.EntityId)
            .ThenBy(delta => delta.Relation.ZoneId)
            .ThenBy(delta => delta.IsEntered)
            .Select(delta => delta.IsEntered
                ? new ZoneEnteredEvent(delta.Relation.EntityId, delta.Relation.ZoneId)
                : (SpatialEvent)new ZoneLeftEvent(delta.Relation.EntityId, delta.Relation.ZoneId)));

        events.AddRange(preCoPresence
            .Except(postCoPresence)
            .Select(relation => new CoPresenceDelta(relation, IsStarted: false))
            .Concat(postCoPresence
                .Except(preCoPresence)
                .Select(relation => new CoPresenceDelta(relation, IsStarted: true)))
            .OrderBy(delta => delta.Relation.FirstEntityId)
            .ThenBy(delta => delta.Relation.SecondEntityId)
            .ThenBy(delta => delta.IsStarted)
            .Select(delta => delta.IsStarted
                ? new CoPresenceStartedEvent(
                    delta.Relation.FirstEntityId,
                    delta.Relation.SecondEntityId)
                : (SpatialEvent)new CoPresenceEndedEvent(
                    delta.Relation.FirstEntityId,
                    delta.Relation.SecondEntityId)));

        var queries = new SpatialQueries(definition);
        EntityId[] trackedObservers =
        [
            .. preState.Entities
                .Where(entity => entity.ObservationEnabled)
                .Select(entity => entity.Id)
                .Concat(postState.Entities
                    .Where(entity => entity.ObservationEnabled)
                    .Select(entity => entity.Id))
                .Distinct()
                .Order(),
        ];
        foreach (EntityId observerId in trackedObservers)
        {
            HashSet<EntityId> before = ProjectTrackedVisibility(queries, preState, observerId);
            HashSet<EntityId> after = ProjectTrackedVisibility(queries, postState, observerId);
            EntityId[] added = [.. after.Except(before).Order()];
            EntityId[] removed = [.. before.Except(after).Order()];
            if (added.Length != 0 || removed.Length != 0)
            {
                events.Add(new GeometricVisibilityChangedEvent(observerId, added, removed));
            }
        }

        return Array.AsReadOnly(events.ToArray());
    }

    private static HashSet<ZoneRelation> ProjectZones(
        SpatialDefinition definition,
        SpatialState state)
    {
        var result = new HashSet<ZoneRelation>();
        foreach (SpatialEntityState entity in state.Entities)
        {
            foreach (ZoneDefinition zone in definition.Zones)
            {
                if (zone.Cells.Contains(entity.Cell))
                {
                    result.Add(new ZoneRelation(entity.Id, zone.Id));
                }
            }
        }

        return result;
    }

    private static HashSet<CoPresenceRelation> ProjectCoPresence(SpatialState state)
    {
        var result = new HashSet<CoPresenceRelation>();
        foreach (IGrouping<CellRef, SpatialEntityState> cellGroup in state.Entities.GroupBy(entity => entity.Cell))
        {
            EntityId[] entityIds = [.. cellGroup.Select(entity => entity.Id).Order()];
            for (int firstIndex = 0; firstIndex < entityIds.Length; firstIndex++)
            {
                for (int secondIndex = firstIndex + 1; secondIndex < entityIds.Length; secondIndex++)
                {
                    result.Add(new CoPresenceRelation(
                        entityIds[firstIndex],
                        entityIds[secondIndex]));
                }
            }
        }

        return result;
    }

    private static HashSet<EntityId> ProjectTrackedVisibility(
        SpatialQueries queries,
        SpatialState state,
        EntityId observerId)
    {
        if (!state.TryGetEntity(observerId, out SpatialEntityState? observer) ||
            !observer!.ObservationEnabled)
        {
            return [];
        }

        return queries.GetVisibleEntities(state, observerId).ToHashSet();
    }

    private readonly record struct ZoneRelation(EntityId EntityId, ZoneId ZoneId);

    private readonly record struct ZoneDelta(ZoneRelation Relation, bool IsEntered);

    private readonly record struct CoPresenceRelation(EntityId FirstEntityId, EntityId SecondEntityId);

    private readonly record struct CoPresenceDelta(CoPresenceRelation Relation, bool IsStarted);
}
