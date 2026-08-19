using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial;

/// <summary>Describes a future spatial value replacement owned by Spatial.</summary>
public abstract record ScheduledSpatialMutation;

/// <summary>Replaces the enabled state of a portal at a future model time.</summary>
public sealed record SetPortalStateMutation : ScheduledSpatialMutation
{
    /// <summary>Initializes a future portal value.</summary>
    public SetPortalStateMutation(PortalId portalId, bool isEnabled)
    {
        if (string.IsNullOrWhiteSpace(portalId.Value))
        {
            throw new ArgumentException("Portal identifier must be initialized.", nameof(portalId));
        }

        PortalId = portalId;
        IsEnabled = isEnabled;
    }

    /// <summary>Gets the target portal.</summary>
    public PortalId PortalId { get; }

    /// <summary>Gets the resulting enabled state.</summary>
    public bool IsEnabled { get; }
}

/// <summary>Replaces or clears the complete override of a cell at a future model time.</summary>
public sealed record SetCellOverrideMutation(
    CellRef Cell,
    CellOverride? Value) : ScheduledSpatialMutation;

/// <summary>Stores one allocated future spatial mutation.</summary>
public sealed record ScheduledSpatialMutationState
{
    /// <summary>Initializes one future mutation.</summary>
    public ScheduledSpatialMutationState(
        ScheduledMutationId id,
        ModelTime due,
        ScheduledSpatialMutation mutation)
    {
        if (id.Value <= 0)
        {
            throw new ArgumentException("Scheduled mutation identifier must be initialized.", nameof(id));
        }

        ArgumentNullException.ThrowIfNull(mutation);
        Id = id;
        Due = due;
        Mutation = mutation;
    }

    /// <summary>Gets the persistent allocated mutation identifier.</summary>
    public ScheduledMutationId Id { get; }

    /// <summary>Gets the absolute model time at which the mutation is consumed.</summary>
    public ModelTime Due { get; }

    /// <summary>Gets the complete resulting target value.</summary>
    public ScheduledSpatialMutation Mutation { get; }
}
