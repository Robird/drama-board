using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial;

/// <summary>Represents one immutable spatial intent correlated by a stable command identifier.</summary>
public abstract record SpatialCommand
{
    private protected SpatialCommand(SpatialCommandId commandId)
    {
        SpatialCommandArguments.Validate(commandId);
        CommandId = commandId;
    }

    /// <summary>Gets the stable identifier used to normalize a simultaneous command batch.</summary>
    public SpatialCommandId CommandId { get; }
}

/// <summary>Places one entity for a fresh spatial lifetime.</summary>
public sealed record PlaceEntityCommand : SpatialCommand
{
    /// <summary>Initializes a placement intent.</summary>
    public PlaceEntityCommand(
        SpatialCommandId commandId,
        EntityId entityId,
        CellRef cell,
        bool observationEnabled)
        : base(commandId)
    {
        SpatialCommandArguments.Validate(entityId);
        SpatialCommandArguments.Validate(cell);
        EntityId = entityId;
        Cell = cell;
        ObservationEnabled = observationEnabled;
    }

    /// <summary>Gets the entity to place.</summary>
    public EntityId EntityId { get; }

    /// <summary>Gets the requested authoritative cell.</summary>
    public CellRef Cell { get; }

    /// <summary>Gets whether the new placement emits geometric visibility outcomes.</summary>
    public bool ObservationEnabled { get; }
}

/// <summary>Removes one placed entity and any active journey it owns.</summary>
public sealed record RemoveEntityCommand : SpatialCommand
{
    /// <summary>Initializes a removal intent.</summary>
    public RemoveEntityCommand(SpatialCommandId commandId, EntityId entityId)
        : base(commandId)
    {
        SpatialCommandArguments.Validate(entityId);
        EntityId = entityId;
    }

    /// <summary>Gets the entity to remove.</summary>
    public EntityId EntityId { get; }
}

/// <summary>Changes whether one placed entity emits geometric visibility outcomes.</summary>
public sealed record SetObservationEnabledCommand : SpatialCommand
{
    /// <summary>Initializes an observation-state intent.</summary>
    public SetObservationEnabledCommand(
        SpatialCommandId commandId,
        EntityId entityId,
        bool observationEnabled)
        : base(commandId)
    {
        SpatialCommandArguments.Validate(entityId);
        EntityId = entityId;
        ObservationEnabled = observationEnabled;
    }

    /// <summary>Gets the affected entity.</summary>
    public EntityId EntityId { get; }

    /// <summary>Gets the requested observation state.</summary>
    public bool ObservationEnabled { get; }
}

/// <summary>Assigns a new movement goal to an entity without an active journey.</summary>
public sealed record AssignMoveGoalCommand : SpatialCommand
{
    /// <summary>Initializes a movement-assignment intent.</summary>
    public AssignMoveGoalCommand(
        SpatialCommandId commandId,
        EntityId entityId,
        MoveGoal goal)
        : base(commandId)
    {
        SpatialCommandArguments.Validate(entityId);
        SpatialCommandArguments.Validate(goal);
        EntityId = entityId;
        Goal = goal;
    }

    /// <summary>Gets the entity that should begin moving.</summary>
    public EntityId EntityId { get; }

    /// <summary>Gets the semantic or geometric destination.</summary>
    public MoveGoal Goal { get; }
}

/// <summary>Atomically replaces the goal of one active journey.</summary>
public sealed record RetargetMoveGoalCommand : SpatialCommand
{
    /// <summary>Initializes a movement-retarget intent.</summary>
    public RetargetMoveGoalCommand(
        SpatialCommandId commandId,
        EntityId entityId,
        MoveGoal goal)
        : base(commandId)
    {
        SpatialCommandArguments.Validate(entityId);
        SpatialCommandArguments.Validate(goal);
        EntityId = entityId;
        Goal = goal;
    }

    /// <summary>Gets the entity whose active journey should be retargeted.</summary>
    public EntityId EntityId { get; }

    /// <summary>Gets the replacement destination.</summary>
    public MoveGoal Goal { get; }
}

/// <summary>Cancels one entity's active journey.</summary>
public sealed record CancelMoveGoalCommand : SpatialCommand
{
    /// <summary>Initializes a cancellation intent.</summary>
    public CancelMoveGoalCommand(SpatialCommandId commandId, EntityId entityId)
        : base(commandId)
    {
        SpatialCommandArguments.Validate(entityId);
        EntityId = entityId;
    }

    /// <summary>Gets the entity whose active journey should be cancelled.</summary>
    public EntityId EntityId { get; }
}

/// <summary>Sets the immediate effective enabled state of one portal.</summary>
public sealed record SetPortalStateCommand : SpatialCommand
{
    /// <summary>Initializes a portal-state intent.</summary>
    public SetPortalStateCommand(
        SpatialCommandId commandId,
        PortalId portalId,
        bool isEnabled)
        : base(commandId)
    {
        SpatialCommandArguments.Validate(portalId);
        PortalId = portalId;
        IsEnabled = isEnabled;
    }

    /// <summary>Gets the target portal.</summary>
    public PortalId PortalId { get; }

    /// <summary>Gets the requested effective enabled state.</summary>
    public bool IsEnabled { get; }
}

/// <summary>Replaces or clears the immediate complete override of one cell.</summary>
public sealed record SetCellOverrideCommand : SpatialCommand
{
    /// <summary>Initializes a cell-override intent.</summary>
    public SetCellOverrideCommand(
        SpatialCommandId commandId,
        CellRef cell,
        CellOverride? value)
        : base(commandId)
    {
        SpatialCommandArguments.Validate(cell);
        SpatialCommandArguments.Validate(value, nameof(value));
        Cell = cell;
        Value = value;
    }

    /// <summary>Gets the target cell.</summary>
    public CellRef Cell { get; }

    /// <summary>Gets the complete replacement override, or null to clear it.</summary>
    public CellOverride? Value { get; }
}

/// <summary>Schedules one future spatial value replacement.</summary>
public sealed record ScheduleSpatialMutationCommand : SpatialCommand
{
    /// <summary>Initializes a future-mutation intent.</summary>
    public ScheduleSpatialMutationCommand(
        SpatialCommandId commandId,
        ModelTime due,
        ScheduledSpatialMutation mutation)
        : base(commandId)
    {
        SpatialCommandArguments.Validate(mutation);
        Due = due;
        Mutation = mutation;
    }

    /// <summary>Gets the requested absolute due time.</summary>
    public ModelTime Due { get; }

    /// <summary>Gets the future spatial value replacement.</summary>
    public ScheduledSpatialMutation Mutation { get; }
}

/// <summary>Interrupts one entity's active journey for a stable external reason.</summary>
public sealed record InterruptMovementCommand : SpatialCommand
{
    /// <summary>Initializes a movement-interruption intent.</summary>
    public InterruptMovementCommand(
        SpatialCommandId commandId,
        EntityId entityId,
        string reason)
        : base(commandId)
    {
        SpatialCommandArguments.Validate(entityId);
        EntityId = entityId;
        Reason = StableIdentifier.Validate(reason, nameof(reason), "Movement interruption reason");
    }

    /// <summary>Gets the entity whose active journey should be interrupted.</summary>
    public EntityId EntityId { get; }

    /// <summary>Gets the stable, non-localized interruption reason.</summary>
    public string Reason { get; }
}

internal static class SpatialCommandArguments
{
    public static void Validate(SpatialCommandId commandId)
    {
        if (string.IsNullOrWhiteSpace(commandId.Value))
        {
            throw new ArgumentException("Spatial command identifier must be initialized.", nameof(commandId));
        }
    }

    public static void Validate(EntityId entityId)
    {
        if (entityId.Value <= 0)
        {
            throw new ArgumentException("Entity identifier must be initialized.", nameof(entityId));
        }
    }

    public static void Validate(PortalId portalId)
    {
        if (string.IsNullOrWhiteSpace(portalId.Value))
        {
            throw new ArgumentException("Portal identifier must be initialized.", nameof(portalId));
        }
    }

    public static void Validate(CellRef cell)
    {
        if (string.IsNullOrWhiteSpace(cell.MapId.Value))
        {
            throw new ArgumentException("Cell reference must be initialized.", nameof(cell));
        }
    }

    public static void Validate(MoveGoal goal)
    {
        ArgumentNullException.ThrowIfNull(goal);
        switch (goal)
        {
            case CellGoal cellGoal:
                Validate(cellGoal.Cell);
                break;
            case AnchorGoal anchorGoal when !string.IsNullOrWhiteSpace(anchorGoal.AnchorId.Value):
                break;
            case ZoneGoal zoneGoal when !string.IsNullOrWhiteSpace(zoneGoal.ZoneId.Value):
                break;
            case AnchorGoal:
                throw new ArgumentException("Anchor goal must be initialized.", nameof(goal));
            case ZoneGoal:
                throw new ArgumentException("Zone goal must be initialized.", nameof(goal));
            default:
                throw new ArgumentException("Move goal type is not supported by this rules version.", nameof(goal));
        }
    }

    public static void Validate(CellOverride? value, string parameterName)
    {
        if (value?.IsEmpty == true)
        {
            throw new ArgumentException("An empty cell override must be represented by null.", parameterName);
        }
    }

    public static void Validate(ScheduledSpatialMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        switch (mutation)
        {
            case SetPortalStateMutation portal:
                Validate(portal.PortalId);
                break;
            case SetCellOverrideMutation cell:
                Validate(cell.Cell);
                Validate(cell.Value, nameof(mutation));
                break;
            default:
                throw new ArgumentException(
                    "Scheduled spatial mutation type is not supported by this rules version.",
                    nameof(mutation));
        }
    }
}
