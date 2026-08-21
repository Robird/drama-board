namespace DramaBoard.Spatial;

/// <summary>Identifies how one spatial command was handled.</summary>
public enum SpatialCommandDisposition
{
    /// <summary>The command produced one or more authoritative effects.</summary>
    Accepted,

    /// <summary>The requested value already held and no authoritative fact was needed.</summary>
    AcceptedNoChange,

    /// <summary>The command was not applied.</summary>
    Rejected,
}

/// <summary>Identifies a stable, non-localized spatial command rejection reason.</summary>
public enum SpatialCommandRejectionCode
{
    /// <summary>The command was not rejected.</summary>
    None,

    /// <summary>The requested entity identifier is already placed.</summary>
    EntityAlreadyExists,

    /// <summary>The requested entity is not placed.</summary>
    EntityNotFound,

    /// <summary>The entity already owns an active journey.</summary>
    EntityHasActiveJourney,

    /// <summary>The entity does not own an active journey.</summary>
    EntityHasNoActiveJourney,

    /// <summary>The active journey must be resolved before it can be changed by an immediate command.</summary>
    JourneyLegOverdue,

    /// <summary>The referenced map does not exist in the bound definition.</summary>
    UnknownMap,

    /// <summary>The referenced coordinates are outside their finite map.</summary>
    CellOutOfBounds,

    /// <summary>A dynamic cell move cost cannot be represented as one traversal duration.</summary>
    CellTraversalCostOverflow,

    /// <summary>The referenced portal does not exist in the bound definition.</summary>
    UnknownPortal,

    /// <summary>The referenced anchor does not exist in the bound definition.</summary>
    UnknownAnchor,

    /// <summary>The referenced zone does not exist in the bound definition.</summary>
    UnknownZone,

    /// <summary>A scheduled mutation is not strictly later than the command model time.</summary>
    ScheduledMutationDueNotFuture,

    /// <summary>A different value is already scheduled for the same target and due time.</summary>
    ScheduledMutationConflict,

    /// <summary>No route exists from the entity's current cell to the requested goal.</summary>
    JourneyUnreachable,

    /// <summary>The selected route's exact accumulated navigation cost exceeds the V1 duration range.</summary>
    NavigationCostOverflow,

    /// <summary>The requested effect would overflow model time.</summary>
    ModelTimeOverflow,

    /// <summary>The entity's movement generation cannot be advanced.</summary>
    MovementGenerationOverflow,

    /// <summary>No JourneyId remains available for allocation.</summary>
    JourneyAllocatorExhausted,

    /// <summary>No ScheduledMutationId remains available for allocation.</summary>
    ScheduledMutationAllocatorExhausted,
}

/// <summary>Reports the deterministic handling result of one spatial command.</summary>
public sealed record SpatialCommandResult
{
    internal SpatialCommandResult(
        SpatialCommandId commandId,
        SpatialCommandDisposition disposition,
        SpatialCommandRejectionCode rejectionCode = SpatialCommandRejectionCode.None,
        JourneyId? journeyId = null,
        ScheduledMutationId? scheduledMutationId = null)
    {
        SpatialCommandArguments.Validate(commandId);
        ValidateEnum(disposition, nameof(disposition));
        ValidateEnum(rejectionCode, nameof(rejectionCode));
        if (journeyId is { Value: <= 0 })
        {
            throw new ArgumentException("Journey identifier must be initialized.", nameof(journeyId));
        }

        if (scheduledMutationId is { Value: <= 0 })
        {
            throw new ArgumentException(
                "Scheduled mutation identifier must be initialized.",
                nameof(scheduledMutationId));
        }

        if (journeyId is not null && scheduledMutationId is not null)
        {
            throw new ArgumentException("One command result cannot report two allocator domains.");
        }

        switch (disposition)
        {
            case SpatialCommandDisposition.Rejected when rejectionCode == SpatialCommandRejectionCode.None:
                throw new ArgumentException("A rejected command must provide a rejection code.", nameof(rejectionCode));
            case SpatialCommandDisposition.Rejected
                when journeyId is not null || scheduledMutationId is not null:
                throw new ArgumentException("A rejected command cannot report accepted-result metadata.");
            case not SpatialCommandDisposition.Rejected
                when rejectionCode != SpatialCommandRejectionCode.None:
                throw new ArgumentException("An accepted command cannot provide a rejection code.", nameof(rejectionCode));
            case SpatialCommandDisposition.AcceptedNoChange when journeyId is not null:
                throw new ArgumentException("A no-change result cannot allocate a JourneyId.", nameof(journeyId));
        }

        CommandId = commandId;
        Disposition = disposition;
        RejectionCode = rejectionCode;
        JourneyId = journeyId;
        ScheduledMutationId = scheduledMutationId;
    }

    /// <summary>Gets the correlated input command.</summary>
    public SpatialCommandId CommandId { get; }

    /// <summary>Gets how the command was handled.</summary>
    public SpatialCommandDisposition Disposition { get; }

    /// <summary>Gets the stable rejection reason, or None for every accepted disposition.</summary>
    public SpatialCommandRejectionCode RejectionCode { get; }

    /// <summary>Gets the allocated or retained journey identity when relevant.</summary>
    public JourneyId? JourneyId { get; }

    /// <summary>Gets the allocated or already-scheduled mutation identity when relevant.</summary>
    public ScheduledMutationId? ScheduledMutationId { get; }

    private static void ValidateEnum<T>(T value, string parameterName)
        where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Enumeration value is not defined.");
        }
    }
}

/// <summary>Contains the raw facts and outcome planned for one external command.</summary>
public sealed class SpatialCommandPlan
{
    internal SpatialCommandPlan(
        IEnumerable<SpatialEvent> facts,
        SpatialCommandResult result)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(result);
        SpatialEvent[] factArray = [.. facts];
        if (factArray.Any(value => value is null))
        {
            throw new ArgumentException("A spatial command plan cannot contain null facts.", nameof(facts));
        }

        Facts = Array.AsReadOnly(factArray);
        Result = result;
    }

    /// <summary>Gets authoritative raw facts in scratch-fold and commit order.</summary>
    public IReadOnlyList<SpatialEvent> Facts { get; }

    /// <summary>Gets the single command outcome.</summary>
    public SpatialCommandResult Result { get; }
}
