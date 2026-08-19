namespace DramaBoard.Spatial;

/// <summary>Stores the authoritative cell and movement generation of one placed entity.</summary>
public sealed record SpatialEntityState
{
    /// <summary>Initializes a placed spatial entity.</summary>
    public SpatialEntityState(
        EntityId id,
        CellRef cell,
        bool observationEnabled,
        long movementGeneration)
    {
        if (id.Value <= 0)
        {
            throw new ArgumentException("Entity identifier must be initialized.", nameof(id));
        }

        if (movementGeneration < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(movementGeneration),
                "Movement generation cannot be negative.");
        }

        Id = id;
        Cell = cell;
        ObservationEnabled = observationEnabled;
        MovementGeneration = movementGeneration;
    }

    /// <summary>Gets the stable entity identifier.</summary>
    public EntityId Id { get; }

    /// <summary>Gets the entity's authoritative interaction-locality cell.</summary>
    public CellRef Cell { get; }

    /// <summary>Gets whether transitions should emit geometric visibility deltas for the entity.</summary>
    public bool ObservationEnabled { get; }

    /// <summary>Gets the movement scheduling generation for this placement lifetime.</summary>
    public long MovementGeneration { get; }

    internal SpatialEntityState With(
        CellRef? cell = null,
        bool? observationEnabled = null,
        long? movementGeneration = null) =>
        new(
            Id,
            cell ?? Cell,
            observationEnabled ?? ObservationEnabled,
            movementGeneration ?? MovementGeneration);
}
