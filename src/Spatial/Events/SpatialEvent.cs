namespace DramaBoard.Spatial;

/// <summary>Base payload for authoritative spatial facts and derived spatial outcomes.</summary>
public abstract record SpatialEvent;

/// <summary>Places one new entity for a fresh placement lifetime.</summary>
public sealed record EntityPlacedEvent : SpatialEvent
{
    internal EntityPlacedEvent(SpatialEntityState entity) => Entity = entity;

    /// <summary>Gets the complete placed entity state.</summary>
    public SpatialEntityState Entity { get; }
}

/// <summary>Removes one entity and its exact optional active journey atomically.</summary>
public sealed record EntityRemovedEvent : SpatialEvent
{
    internal EntityRemovedEvent(
        EntityId entityId,
        long expectedMovementGeneration,
        JourneyId? expectedActiveJourneyId)
    {
        EntityId = entityId;
        ExpectedMovementGeneration = expectedMovementGeneration;
        ExpectedActiveJourneyId = expectedActiveJourneyId;
    }

    /// <summary>Gets the removed entity.</summary>
    public EntityId EntityId { get; }

    /// <summary>Gets the generation that must still be current.</summary>
    public long ExpectedMovementGeneration { get; }

    /// <summary>Gets the exact active journey expected at removal, if any.</summary>
    public JourneyId? ExpectedActiveJourneyId { get; }
}

/// <summary>Changes whether one entity emits geometric visibility deltas.</summary>
public sealed record ObservationStateChangedEvent : SpatialEvent
{
    internal ObservationStateChangedEvent(EntityId entityId, bool expectedEnabled, bool resultingEnabled)
    {
        EntityId = entityId;
        ExpectedEnabled = expectedEnabled;
        ResultingEnabled = resultingEnabled;
    }

    /// <summary>Gets the affected entity.</summary>
    public EntityId EntityId { get; }

    /// <summary>Gets the exact prior value.</summary>
    public bool ExpectedEnabled { get; }

    /// <summary>Gets the resulting value.</summary>
    public bool ResultingEnabled { get; }
}

/// <summary>Allocates and starts one complete journey.</summary>
public sealed record JourneyStartedEvent : SpatialEvent
{
    internal JourneyStartedEvent(JourneyState journey) => Journey = journey;

    /// <summary>Gets the complete resulting journey and current leg.</summary>
    public JourneyState Journey { get; }
}

/// <summary>Atomically replaces an active journey goal and current leg.</summary>
public sealed record JourneyRetargetedEvent : SpatialEvent
{
    internal JourneyRetargetedEvent(long expectedGeneration, JourneyState resultingJourney)
    {
        ExpectedGeneration = expectedGeneration;
        ResultingJourney = resultingJourney;
    }

    /// <summary>Gets the exact prior movement generation.</summary>
    public long ExpectedGeneration { get; }

    /// <summary>Gets the complete resulting journey with the same JourneyId.</summary>
    public JourneyState ResultingJourney { get; }
}

/// <summary>Cancels one exact active journey and advances movement generation.</summary>
public sealed record JourneyCancelledEvent : SpatialEvent
{
    internal JourneyCancelledEvent(
        EntityId entityId,
        JourneyId journeyId,
        long expectedGeneration,
        long resultingGeneration)
    {
        EntityId = entityId;
        JourneyId = journeyId;
        ExpectedGeneration = expectedGeneration;
        ResultingGeneration = resultingGeneration;
    }

    /// <summary>Gets the affected entity.</summary>
    public EntityId EntityId { get; }

    /// <summary>Gets the exact active journey.</summary>
    public JourneyId JourneyId { get; }

    /// <summary>Gets the exact prior movement generation.</summary>
    public long ExpectedGeneration { get; }

    /// <summary>Gets the resulting movement generation.</summary>
    public long ResultingGeneration { get; }
}

/// <summary>Interrupts one exact active journey for a stable external reason.</summary>
public sealed record JourneyInterruptedEvent : SpatialEvent
{
    internal JourneyInterruptedEvent(
        EntityId entityId,
        JourneyId journeyId,
        long expectedGeneration,
        long resultingGeneration,
        string reason)
    {
        EntityId = entityId;
        JourneyId = journeyId;
        ExpectedGeneration = expectedGeneration;
        ResultingGeneration = resultingGeneration;
        Reason = StableIdentifier.Validate(reason, nameof(reason), "Journey interruption reason");
    }

    /// <summary>Gets the affected entity.</summary>
    public EntityId EntityId { get; }

    /// <summary>Gets the exact active journey.</summary>
    public JourneyId JourneyId { get; }

    /// <summary>Gets the exact prior movement generation.</summary>
    public long ExpectedGeneration { get; }

    /// <summary>Gets the resulting movement generation.</summary>
    public long ResultingGeneration { get; }

    /// <summary>Gets the stable non-localized interruption reason.</summary>
    public string Reason { get; }
}

/// <summary>Atomically moves one entity from a due leg's source to destination.</summary>
public sealed record EntitySteppedEvent : SpatialEvent
{
    internal EntitySteppedEvent(
        EntityId entityId,
        JourneyId journeyId,
        CellRef from,
        CellRef to,
        long journeyGeneration)
    {
        EntityId = entityId;
        JourneyId = journeyId;
        From = from;
        To = to;
        JourneyGeneration = journeyGeneration;
    }

    /// <summary>Gets the moving entity.</summary>
    public EntityId EntityId { get; }

    /// <summary>Gets the owning journey.</summary>
    public JourneyId JourneyId { get; }

    /// <summary>Gets the exact authoritative source.</summary>
    public CellRef From { get; }

    /// <summary>Gets the resulting authoritative destination.</summary>
    public CellRef To { get; }

    /// <summary>Gets the exact journey generation.</summary>
    public long JourneyGeneration { get; }
}

/// <summary>Records a failed due leg and its complete replacement without moving the entity.</summary>
public sealed record JourneyReroutedEvent : SpatialEvent
{
    internal JourneyReroutedEvent(
        EntityId entityId,
        JourneyId journeyId,
        CurrentLeg failedLeg,
        CurrentLeg resultingLeg)
    {
        EntityId = entityId;
        JourneyId = journeyId;
        FailedLeg = failedLeg;
        ResultingLeg = resultingLeg;
    }

    /// <summary>Gets the journey entity.</summary>
    public EntityId EntityId { get; }

    /// <summary>Gets the exact active journey.</summary>
    public JourneyId JourneyId { get; }

    /// <summary>Gets the exact failed current leg.</summary>
    public CurrentLeg FailedLeg { get; }

    /// <summary>Gets the complete replacement current leg.</summary>
    public CurrentLeg ResultingLeg { get; }
}

/// <summary>Starts the complete next leg after a successful step.</summary>
public sealed record JourneyContinuedEvent : SpatialEvent
{
    internal JourneyContinuedEvent(
        EntityId entityId,
        JourneyId journeyId,
        CurrentLeg completedLeg,
        CurrentLeg resultingLeg)
    {
        EntityId = entityId;
        JourneyId = journeyId;
        CompletedLeg = completedLeg;
        ResultingLeg = resultingLeg;
    }

    /// <summary>Gets the journey entity.</summary>
    public EntityId EntityId { get; }

    /// <summary>Gets the exact active journey.</summary>
    public JourneyId JourneyId { get; }

    /// <summary>Gets the exact leg just completed by a preceding step.</summary>
    public CurrentLeg CompletedLeg { get; }

    /// <summary>Gets the complete next current leg.</summary>
    public CurrentLeg ResultingLeg { get; }
}

/// <summary>Identifies the three authoritative journey completion paths.</summary>
public enum JourneyCompletionReason
{
    /// <summary>A due leg reached the active journey's goal.</summary>
    ReachedGoal,

    /// <summary>A newly assigned goal was already satisfied and consumes a new JourneyId.</summary>
    AssignedAlreadySatisfied,

    /// <summary>A retarget goal was already satisfied by an existing JourneyId.</summary>
    RetargetedAlreadySatisfied,
}

/// <summary>Completes a reached, newly satisfied, or retarget-satisfied journey.</summary>
public sealed record JourneyCompletedEvent : SpatialEvent
{
    internal JourneyCompletedEvent(
        EntityId entityId,
        JourneyId journeyId,
        MoveGoal goal,
        long expectedGeneration,
        long resultingGeneration,
        JourneyCompletionReason reason,
        CurrentLeg? completedLeg = null)
    {
        EntityId = entityId;
        JourneyId = journeyId;
        Goal = goal;
        ExpectedGeneration = expectedGeneration;
        ResultingGeneration = resultingGeneration;
        Reason = reason;
        CompletedLeg = completedLeg;
    }

    /// <summary>Gets the journey entity.</summary>
    public EntityId EntityId { get; }

    /// <summary>Gets the allocated or existing journey identity.</summary>
    public JourneyId JourneyId { get; }

    /// <summary>Gets the completed goal.</summary>
    public MoveGoal Goal { get; }

    /// <summary>Gets the exact prior generation.</summary>
    public long ExpectedGeneration { get; }

    /// <summary>Gets the resulting generation.</summary>
    public long ResultingGeneration { get; }

    /// <summary>Gets the completion path.</summary>
    public JourneyCompletionReason Reason { get; }

    /// <summary>Gets the completed leg exactly for ReachedGoal.</summary>
    public CurrentLeg? CompletedLeg { get; }
}

/// <summary>Identifies whether blocking happened before or after a successful step.</summary>
public enum JourneyBlockedReason
{
    /// <summary>The due current leg became invalid and no alternative route existed.</summary>
    LegInvalidNoRoute,

    /// <summary>The entity stepped successfully but no continuation route existed.</summary>
    NoContinuationAfterStep,
}

/// <summary>Ends an active journey after one exact due leg could not proceed.</summary>
public sealed record JourneyBlockedEvent : SpatialEvent
{
    internal JourneyBlockedEvent(
        EntityId entityId,
        JourneyId journeyId,
        CurrentLeg leg,
        JourneyBlockedReason reason)
    {
        EntityId = entityId;
        JourneyId = journeyId;
        Leg = leg;
        Reason = reason;
    }

    /// <summary>Gets the journey entity.</summary>
    public EntityId EntityId { get; }

    /// <summary>Gets the exact active journey.</summary>
    public JourneyId JourneyId { get; }

    /// <summary>Gets the exact due leg associated with blocking.</summary>
    public CurrentLeg Leg { get; }

    /// <summary>Gets the blocking phase.</summary>
    public JourneyBlockedReason Reason { get; }
}

/// <summary>Replaces the sparse enabled override of one portal.</summary>
public sealed record PortalStateChangedEvent : SpatialEvent
{
    internal PortalStateChangedEvent(
        PortalId portalId,
        bool? expectedOverride,
        bool? resultingOverride)
    {
        PortalId = portalId;
        ExpectedOverride = expectedOverride;
        ResultingOverride = resultingOverride;
    }

    /// <summary>Gets the target portal.</summary>
    public PortalId PortalId { get; }

    /// <summary>Gets the exact sparse override currently expected, or null for Definition.</summary>
    public bool? ExpectedOverride { get; }

    /// <summary>Gets the resulting sparse override, or null to return to Definition.</summary>
    public bool? ResultingOverride { get; }
}

/// <summary>Replaces the complete sparse override of one cell.</summary>
public sealed record CellStateChangedEvent : SpatialEvent
{
    internal CellStateChangedEvent(
        CellRef cell,
        CellOverride? expectedOverride,
        CellOverride? resultingOverride)
    {
        Cell = cell;
        ExpectedOverride = expectedOverride;
        ResultingOverride = resultingOverride;
    }

    /// <summary>Gets the target cell.</summary>
    public CellRef Cell { get; }

    /// <summary>Gets the exact complete sparse override currently expected.</summary>
    public CellOverride? ExpectedOverride { get; }

    /// <summary>Gets the resulting complete sparse override, or null to clear.</summary>
    public CellOverride? ResultingOverride { get; }
}

/// <summary>Allocates one future spatial mutation.</summary>
public sealed record MutationScheduledEvent : SpatialEvent
{
    internal MutationScheduledEvent(ScheduledSpatialMutationState mutation) => Mutation = mutation;

    /// <summary>Gets the complete allocated future mutation.</summary>
    public ScheduledSpatialMutationState Mutation { get; }
}

/// <summary>Consumes one exact due spatial mutation after its value event was emitted if needed.</summary>
public sealed record MutationConsumedEvent : SpatialEvent
{
    internal MutationConsumedEvent(ScheduledSpatialMutationState mutation) => Mutation = mutation;

    /// <summary>Gets the exact due mutation removed from state.</summary>
    public ScheduledSpatialMutationState Mutation { get; }
}

/// <summary>Consumes one successful SpatialMoment identity.</summary>
public sealed record MomentResolvedEvent : SpatialEvent
{
    internal MomentResolvedEvent(long momentOrdinal, int resolvedWorkCount)
    {
        if (momentOrdinal <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(momentOrdinal));
        }

        if (resolvedWorkCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(resolvedWorkCount),
                "A resolved SpatialMoment must consume positive work.");
        }

        MomentOrdinal = momentOrdinal;
        ResolvedWorkCount = resolvedWorkCount;
    }

    /// <summary>Gets the exact persistent moment identity.</summary>
    public long MomentOrdinal { get; }

    /// <summary>Gets the positive number of due work items resolved by the moment.</summary>
    public int ResolvedWorkCount { get; }
}

/// <summary>Reports that an entity entered a semantic zone.</summary>
public sealed record ZoneEnteredEvent : SpatialEvent
{
    internal ZoneEnteredEvent(EntityId entityId, ZoneId zoneId)
    {
        EntityId = entityId;
        ZoneId = zoneId;
    }

    /// <summary>Gets the entity.</summary>
    public EntityId EntityId { get; }

    /// <summary>Gets the entered zone.</summary>
    public ZoneId ZoneId { get; }
}

/// <summary>Reports that an entity left a semantic zone.</summary>
public sealed record ZoneLeftEvent : SpatialEvent
{
    internal ZoneLeftEvent(EntityId entityId, ZoneId zoneId)
    {
        EntityId = entityId;
        ZoneId = zoneId;
    }

    /// <summary>Gets the entity.</summary>
    public EntityId EntityId { get; }

    /// <summary>Gets the left zone.</summary>
    public ZoneId ZoneId { get; }
}

/// <summary>Reports that a canonical pair began sharing a cell.</summary>
public sealed record CoPresenceStartedEvent : SpatialEvent
{
    internal CoPresenceStartedEvent(EntityId firstEntityId, EntityId secondEntityId)
    {
        if (firstEntityId == secondEntityId)
        {
            throw new ArgumentException("Co-presence pair must contain two distinct entities.");
        }

        (FirstEntityId, SecondEntityId) = firstEntityId.CompareTo(secondEntityId) < 0
            ? (firstEntityId, secondEntityId)
            : (secondEntityId, firstEntityId);
    }

    /// <summary>Gets the smaller entity identifier.</summary>
    public EntityId FirstEntityId { get; }

    /// <summary>Gets the larger entity identifier.</summary>
    public EntityId SecondEntityId { get; }
}

/// <summary>Reports that a canonical pair stopped sharing a cell.</summary>
public sealed record CoPresenceEndedEvent : SpatialEvent
{
    internal CoPresenceEndedEvent(EntityId firstEntityId, EntityId secondEntityId)
    {
        if (firstEntityId == secondEntityId)
        {
            throw new ArgumentException("Co-presence pair must contain two distinct entities.");
        }

        (FirstEntityId, SecondEntityId) = firstEntityId.CompareTo(secondEntityId) < 0
            ? (firstEntityId, secondEntityId)
            : (secondEntityId, firstEntityId);
    }

    /// <summary>Gets the smaller entity identifier.</summary>
    public EntityId FirstEntityId { get; }

    /// <summary>Gets the larger entity identifier.</summary>
    public EntityId SecondEntityId { get; }
}

/// <summary>Reports an objective geometric visible-entity set delta for one observer.</summary>
public sealed record GeometricVisibilityChangedEvent : SpatialEvent
{
    internal GeometricVisibilityChangedEvent(
        EntityId observerId,
        IEnumerable<EntityId> addedEntityIds,
        IEnumerable<EntityId> removedEntityIds)
    {
        ArgumentNullException.ThrowIfNull(addedEntityIds);
        ArgumentNullException.ThrowIfNull(removedEntityIds);
        EntityId[] added = [.. addedEntityIds.Order()];
        EntityId[] removed = [.. removedEntityIds.Order()];
        if (added.Length + removed.Length == 0)
        {
            throw new ArgumentException("Visibility delta cannot be empty.");
        }

        if (added.Distinct().Count() != added.Length || removed.Distinct().Count() != removed.Length)
        {
            throw new ArgumentException("Visibility delta entity sets cannot contain duplicates.");
        }

        if (added.Intersect(removed).Any())
        {
            throw new ArgumentException("Visibility delta cannot add and remove the same entity.");
        }

        ObserverId = observerId;
        AddedEntityIds = Array.AsReadOnly(added);
        RemovedEntityIds = Array.AsReadOnly(removed);
    }

    /// <summary>Gets the observer whose objective geometric set changed.</summary>
    public EntityId ObserverId { get; }

    /// <summary>Gets visible entities added in stable identifier order.</summary>
    public IReadOnlyList<EntityId> AddedEntityIds { get; }

    /// <summary>Gets visible entities removed in stable identifier order.</summary>
    public IReadOnlyList<EntityId> RemovedEntityIds { get; }

    /// <inheritdoc />
    public bool Equals(GeometricVisibilityChangedEvent? other) =>
        other is not null &&
        ObserverId == other.ObserverId &&
        AddedEntityIds.SequenceEqual(other.AddedEntityIds) &&
        RemovedEntityIds.SequenceEqual(other.RemovedEntityIds);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ObserverId);
        foreach (EntityId entityId in AddedEntityIds)
        {
            hash.Add(entityId);
        }

        hash.Add(-1);
        foreach (EntityId entityId in RemovedEntityIds)
        {
            hash.Add(entityId);
        }

        return hash.ToHashCode();
    }
}
