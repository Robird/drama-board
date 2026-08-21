namespace DramaBoard.Spatial;

/// <summary>Represents one immutable authoritative spatial event projection.</summary>
public sealed class SpatialState : IEquatable<SpatialState>
{
    private SpatialState(
        SpatialDefinitionStamp definition,
        long revision,
        long nextJourneyOrdinal,
        long nextMutationOrdinal,
        IEnumerable<SpatialEntityState> entities,
        IEnumerable<JourneyState> journeys,
        IEnumerable<PortalOverrideState> portalOverrides,
        IEnumerable<CellOverrideState> cellOverrides,
        IEnumerable<ScheduledSpatialMutationState> scheduledMutations)
    {
        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        if (nextJourneyOrdinal <= 0 || nextMutationOrdinal <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nextJourneyOrdinal),
                "All persistent spatial ordinals must be positive.");
        }

        SpatialEntityState[] entityArray = [.. entities];
        JourneyState[] journeyArray = [.. journeys];
        PortalOverrideState[] portalArray = [.. portalOverrides];
        CellOverrideState[] cellArray = [.. cellOverrides];
        ScheduledSpatialMutationState[] mutationArray = [.. scheduledMutations];
        EnsureUnique(entityArray.Select(value => value.Id), "entity");
        EnsureUnique(journeyArray.Select(value => value.EntityId), "journey entity");
        EnsureUnique(journeyArray.Select(value => value.Id), "journey");
        EnsureUnique(portalArray.Select(value => value.PortalId), "portal override");
        EnsureUnique(cellArray.Select(value => value.Cell), "cell override");
        EnsureUnique(mutationArray.Select(value => value.Id), "scheduled mutation");
        EnsureUniqueMutationTargets(mutationArray);

        Definition = definition;
        Revision = revision;
        NextJourneyOrdinal = nextJourneyOrdinal;
        NextMutationOrdinal = nextMutationOrdinal;
        Entities = ReadOnly(entityArray.OrderBy(entity => entity.Id));
        Journeys = ReadOnly(journeyArray
            .OrderBy(journey => journey.EntityId)
            .ThenBy(journey => journey.Id));
        PortalOverrides = ReadOnly(portalArray.OrderBy(value => value.PortalId));
        CellOverrides = ReadOnly(cellArray.OrderBy(value => value.Cell));
        ScheduledMutations = ReadOnly(mutationArray
            .OrderBy(value => value.Due)
            .ThenBy(value => value.Id));
    }

    /// <summary>Gets the immutable definition stamp bound to this run.</summary>
    public SpatialDefinitionStamp Definition { get; }

    /// <summary>Gets the number of state-changing spatial events applied to this projection.</summary>
    public long Revision { get; }

    /// <summary>Gets the next persistent Journey identity.</summary>
    public long NextJourneyOrdinal { get; }

    /// <summary>Gets the next persistent scheduled-mutation identity.</summary>
    public long NextMutationOrdinal { get; }

    /// <summary>Gets placed entities in stable identifier order.</summary>
    public IReadOnlyList<SpatialEntityState> Entities { get; }

    /// <summary>Gets active journeys in stable entity and journey order.</summary>
    public IReadOnlyList<JourneyState> Journeys { get; }

    /// <summary>Gets non-default portal overrides in stable identifier order.</summary>
    public IReadOnlyList<PortalOverrideState> PortalOverrides { get; }

    /// <summary>Gets non-empty cell overrides in stable cell order.</summary>
    public IReadOnlyList<CellOverrideState> CellOverrides { get; }

    /// <summary>Gets future mutations in due-time and identifier order.</summary>
    public IReadOnlyList<ScheduledSpatialMutationState> ScheduledMutations { get; }

    /// <summary>Creates an empty state pinned to the supplied definition and rules.</summary>
    public static SpatialState Create(SpatialDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new SpatialState(
            SpatialDefinitionStamp.From(definition),
            revision: 0,
            nextJourneyOrdinal: 1,
            nextMutationOrdinal: 1,
            entities: [],
            journeys: [],
            portalOverrides: [],
            cellOverrides: [],
            scheduledMutations: []);
    }

    /// <summary>Finds one placed entity by stable identifier.</summary>
    public bool TryGetEntity(EntityId entityId, out SpatialEntityState? entity)
    {
        entity = Entities.SingleOrDefault(value => value.Id == entityId);
        return entity is not null;
    }

    /// <summary>Finds the active journey of one entity.</summary>
    public bool TryGetJourney(EntityId entityId, out JourneyState? journey)
    {
        journey = Journeys.SingleOrDefault(value => value.EntityId == entityId);
        return journey is not null;
    }

    /// <inheritdoc />
    public bool Equals(SpatialState? other) =>
        other is not null &&
        Definition == other.Definition &&
        Revision == other.Revision &&
        NextJourneyOrdinal == other.NextJourneyOrdinal &&
        NextMutationOrdinal == other.NextMutationOrdinal &&
        Entities.SequenceEqual(other.Entities) &&
        Journeys.SequenceEqual(other.Journeys) &&
        PortalOverrides.SequenceEqual(other.PortalOverrides) &&
        CellOverrides.SequenceEqual(other.CellOverrides) &&
        ScheduledMutations.SequenceEqual(other.ScheduledMutations);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SpatialState other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Definition);
        hash.Add(Revision);
        hash.Add(NextJourneyOrdinal);
        hash.Add(NextMutationOrdinal);
        AddSequence(ref hash, Entities);
        AddSequence(ref hash, Journeys);
        AddSequence(ref hash, PortalOverrides);
        AddSequence(ref hash, CellOverrides);
        AddSequence(ref hash, ScheduledMutations);
        return hash.ToHashCode();
    }

    internal SpatialState Rebuild(
        long? revision = null,
        long? nextJourneyOrdinal = null,
        long? nextMutationOrdinal = null,
        IEnumerable<SpatialEntityState>? entities = null,
        IEnumerable<JourneyState>? journeys = null,
        IEnumerable<PortalOverrideState>? portalOverrides = null,
        IEnumerable<CellOverrideState>? cellOverrides = null,
        IEnumerable<ScheduledSpatialMutationState>? scheduledMutations = null) =>
        new(
            Definition,
            revision ?? Revision,
            nextJourneyOrdinal ?? NextJourneyOrdinal,
            nextMutationOrdinal ?? NextMutationOrdinal,
            entities ?? Entities,
            journeys ?? Journeys,
            portalOverrides ?? PortalOverrides,
            cellOverrides ?? CellOverrides,
            scheduledMutations ?? ScheduledMutations);

    private static IReadOnlyList<T> ReadOnly<T>(IEnumerable<T> values) =>
        Array.AsReadOnly(values.ToArray());

    private static void AddSequence<T>(ref HashCode hash, IEnumerable<T> values)
    {
        foreach (T value in values)
        {
            hash.Add(value);
        }
    }

    private static void EnsureUnique<T>(IEnumerable<T> values, string description)
    {
        var set = new HashSet<T>();
        if (values.Any(value => !set.Add(value)))
        {
            throw new InvalidOperationException($"Spatial {description} identities must be unique.");
        }
    }

    private static void EnsureUniqueMutationTargets(IEnumerable<ScheduledSpatialMutationState> mutations)
    {
        ScheduledSpatialMutationState[] array = [.. mutations];
        for (int first = 0; first < array.Length; first++)
        {
            for (int second = first + 1; second < array.Length; second++)
            {
                if (array[first].Due == array[second].Due && SameTarget(array[first].Mutation, array[second].Mutation))
                {
                    throw new InvalidOperationException(
                        "Scheduled mutation target and due-time pairs must be unique.");
                }
            }
        }
    }

    private static bool SameTarget(ScheduledSpatialMutation first, ScheduledSpatialMutation second) =>
        (first, second) switch
        {
            (SetPortalStateMutation left, SetPortalStateMutation right) => left.PortalId == right.PortalId,
            (SetCellOverrideMutation left, SetCellOverrideMutation right) => left.Cell == right.Cell,
            _ => false,
        };
}
