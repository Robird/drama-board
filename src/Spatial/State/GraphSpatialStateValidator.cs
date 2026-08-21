using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial;

/// <summary>Validates a complete Graph Spatial state against immutable graph content.</summary>
public static class GraphSpatialStateValidator
{
    public static void ValidateComplete(GraphDefinition definition, GraphSpatialState state)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(state);

        EnsureCanonicalUnique(state.Entities, entity => entity.Id, "entities");
        EnsureCanonicalUnique(
            state.PassageEntryAccessOverrides,
            value => value.PassageId,
            "passage entry access overrides");
        EnsureCanonicalSchedules(state.ScheduledPassageEntryChanges);

        foreach (SpatialEntity entity in state.Entities)
        {
            SpatialIdentifier.Require(entity.Id, nameof(state));
            if (entity.MovementGeneration < 0)
            {
                throw new InvalidOperationException(
                    $"Entity '{entity.Id}' has a negative movement generation.");
            }

            ValidateLocation(definition, entity);
        }

        foreach (PassageEntryAccessOverride value in state.PassageEntryAccessOverrides)
        {
            PassageDefinition passage = RequirePassage(definition, value.PassageId);
            if (value.Access == passage.InitialEntryAccess)
            {
                throw new InvalidOperationException(
                    $"Passage '{value.PassageId}' stores a redundant entry-access override.");
            }
        }

        foreach (ScheduledPassageEntryChange schedule in state.ScheduledPassageEntryChanges)
        {
            RequirePassage(definition, schedule.PassageId);
            PassageEntryPatch.Validate(schedule.Patch, nameof(state));
        }
    }

    internal static SpatialEntity RequireEntity(GraphSpatialState state, EntityId entityId) =>
        state.TryGetEntity(entityId, out SpatialEntity? entity)
            ? entity!
            : throw new InvalidOperationException($"Spatial entity '{entityId}' does not exist.");

    internal static PassageDefinition RequirePassage(GraphDefinition definition, PassageId passageId)
    {
        try
        {
            return definition.GetPassage(passageId);
        }
        catch (KeyNotFoundException exception)
        {
            throw new InvalidOperationException($"Spatial passage '{passageId}' does not exist.", exception);
        }
    }

    private static void ValidateLocation(GraphDefinition definition, SpatialEntity entity)
    {
        switch (entity.Location)
        {
            case AtPlaceLocation atPlace:
                if (!definition.Contains(atPlace.PlaceId))
                {
                    throw new InvalidOperationException(
                        $"Entity '{entity.Id}' references undefined place '{atPlace.PlaceId}'.");
                }

                break;

            case TraversingLocation traversing:
                PassageDefinition passage = RequirePassage(definition, traversing.PassageId);
                bool forward = traversing.FromPlaceId == passage.EndpointA &&
                    traversing.ToPlaceId == passage.EndpointB;
                bool reverse = traversing.FromPlaceId == passage.EndpointB &&
                    traversing.ToPlaceId == passage.EndpointA;
                if (!forward && !reverse)
                {
                    throw new InvalidOperationException(
                        $"Entity '{entity.Id}' traversal endpoints do not match passage '{passage.Id}'.");
                }

                ModelTime expectedDue;
                try
                {
                    expectedDue = SpatialMath.ArrivalDue(
                        traversing.StartedAt,
                        passage.Length,
                        traversing.SpeedSnapshot);
                }
                catch (Exception exception) when (exception is ArgumentOutOfRangeException or OverflowException)
                {
                    throw new InvalidOperationException(
                        $"Entity '{entity.Id}' traversal timing cannot be represented.",
                        exception);
                }

                if (traversing.ArrivalDue != expectedDue)
                {
                    throw new InvalidOperationException(
                        $"Entity '{entity.Id}' traversal arrival due is inconsistent with length and speed.");
                }

                break;

            case null:
                throw new InvalidOperationException($"Entity '{entity.Id}' has no spatial location.");

            default:
                throw new InvalidOperationException(
                    $"Entity '{entity.Id}' has unsupported location '{entity.Location.GetType().Name}'.");
        }
    }

    private static void EnsureCanonicalUnique<T, TId>(
        IReadOnlyList<T> values,
        Func<T, TId> id,
        string description)
        where TId : IComparable<TId>
    {
        for (int index = 1; index < values.Count; index++)
        {
            if (id(values[index - 1]).CompareTo(id(values[index])) >= 0)
            {
                throw new InvalidOperationException(
                    $"Graph Spatial {description} must be unique and in canonical order.");
            }
        }
    }

    private static void EnsureCanonicalSchedules(IReadOnlyList<ScheduledPassageEntryChange> schedules)
    {
        for (int index = 1; index < schedules.Count; index++)
        {
            ScheduledPassageEntryChange previous = schedules[index - 1];
            ScheduledPassageEntryChange current = schedules[index];
            int passageComparison = previous.PassageId.CompareTo(current.PassageId);
            if (passageComparison > 0 ||
                (passageComparison == 0 && previous.Due >= current.Due))
            {
                throw new InvalidOperationException(
                    "Graph Spatial schedules must be unique and in canonical order.");
            }
        }
    }
}
