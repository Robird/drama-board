using DramaBoard.Kernel.Simulation;

namespace DramaBoard.Spatial;

/// <summary>Identifies how one spatial command was handled.</summary>
public enum SpatialCommandDisposition
{
    /// <summary>The command produced one or more authoritative effects.</summary>
    Accepted,

    /// <summary>The requested value already held and no authoritative event was needed.</summary>
    AcceptedNoChange,

    /// <summary>The command was equivalent to another accepted or already-scheduled intent.</summary>
    AcceptedAlias,

    /// <summary>The command was not applied.</summary>
    Rejected,
}

/// <summary>Identifies a stable, non-localized spatial command rejection reason.</summary>
public enum SpatialCommandRejectionCode
{
    /// <summary>The command was not rejected.</summary>
    None,

    /// <summary>The command conflicts with another command in the simultaneous batch.</summary>
    ConflictingCommands,

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

    /// <summary>A scheduled mutation is not strictly later than the batch model time.</summary>
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
        SpatialCommandId? aliasOfCommandId = null,
        JourneyId? journeyId = null,
        ScheduledMutationId? scheduledMutationId = null)
    {
        SpatialCommandArguments.Validate(commandId);
        ValidateEnum(disposition, nameof(disposition));
        ValidateEnum(rejectionCode, nameof(rejectionCode));
        if (aliasOfCommandId is { } alias)
        {
            SpatialCommandArguments.Validate(alias);
            if (alias.CompareTo(commandId) >= 0)
            {
                throw new ArgumentException(
                    "A command result can only alias an earlier CommandId.",
                    nameof(aliasOfCommandId));
            }
        }

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
                when aliasOfCommandId is not null || journeyId is not null || scheduledMutationId is not null:
                throw new ArgumentException("A rejected command cannot report accepted-result metadata.");
            case not SpatialCommandDisposition.Rejected
                when rejectionCode != SpatialCommandRejectionCode.None:
                throw new ArgumentException("An accepted command cannot provide a rejection code.", nameof(rejectionCode));
            case SpatialCommandDisposition.Accepted when aliasOfCommandId is not null:
                throw new ArgumentException("A directly accepted command cannot alias another command.", nameof(aliasOfCommandId));
            case SpatialCommandDisposition.AcceptedNoChange
                when aliasOfCommandId is not null || journeyId is not null || scheduledMutationId is not null:
                throw new ArgumentException("A no-change result cannot report accepted-result metadata.");
            case SpatialCommandDisposition.AcceptedAlias
                when aliasOfCommandId is null && scheduledMutationId is null:
                throw new ArgumentException(
                    "An alias must identify its canonical command or an existing scheduled mutation.");
            case SpatialCommandDisposition.AcceptedAlias when journeyId is not null:
                throw new ArgumentException("Movement commands cannot alias JourneyIds.", nameof(journeyId));
        }

        CommandId = commandId;
        Disposition = disposition;
        RejectionCode = rejectionCode;
        AliasOfCommandId = aliasOfCommandId;
        JourneyId = journeyId;
        ScheduledMutationId = scheduledMutationId;
    }

    /// <summary>Gets the correlated input command.</summary>
    public SpatialCommandId CommandId { get; }

    /// <summary>Gets how the command was handled.</summary>
    public SpatialCommandDisposition Disposition { get; }

    /// <summary>Gets the stable rejection reason, or None for every accepted disposition.</summary>
    public SpatialCommandRejectionCode RejectionCode { get; }

    /// <summary>Gets the earlier canonical command when this result aliases one.</summary>
    public SpatialCommandId? AliasOfCommandId { get; }

    /// <summary>Gets the allocated or retained journey identity when relevant.</summary>
    public JourneyId? JourneyId { get; }

    /// <summary>Gets the allocated or aliased scheduled-mutation identity when relevant.</summary>
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

/// <summary>Contains the immutable authoritative events and per-command results of one batch.</summary>
public sealed class SpatialCommandBatchResult
{
    internal SpatialCommandBatchResult(
        IEnumerable<UncommittedDomainEvent<SpatialEvent>> events,
        IEnumerable<SpatialCommandResult> results)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(results);
        UncommittedDomainEvent<SpatialEvent>[] eventArray = [.. events];
        SpatialCommandResult[] resultArray = [.. results];
        if (eventArray.Any(value => value is null))
        {
            throw new ArgumentException("A spatial command batch cannot contain null events.", nameof(events));
        }

        if (resultArray.Any(value => value is null))
        {
            throw new ArgumentException("A spatial command batch cannot contain null results.", nameof(results));
        }

        for (int index = 1; index < resultArray.Length; index++)
        {
            if (resultArray[index - 1].CommandId.CompareTo(resultArray[index].CommandId) >= 0)
            {
                throw new ArgumentException(
                    "Spatial command results must have unique CommandIds in strict Ordinal order.",
                    nameof(results));
            }
        }

        HashSet<SpatialCommandId> commandIds = [.. resultArray.Select(result => result.CommandId)];
        if (resultArray.Any(result =>
                result.AliasOfCommandId is { } alias && !commandIds.Contains(alias)))
        {
            throw new ArgumentException(
                "Every aliased CommandId must identify an earlier result in the same batch.",
                nameof(results));
        }

        Events = Array.AsReadOnly(eventArray);
        Results = Array.AsReadOnly(resultArray);
    }

    /// <summary>Gets authoritative events in scratch-fold and commit order.</summary>
    public IReadOnlyList<UncommittedDomainEvent<SpatialEvent>> Events { get; }

    /// <summary>Gets exactly one result per input command in stable CommandId order.</summary>
    public IReadOnlyList<SpatialCommandResult> Results { get; }
}
