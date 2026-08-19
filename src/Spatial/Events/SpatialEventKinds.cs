using DramaBoard.Kernel.Journal;

namespace DramaBoard.Spatial;

/// <summary>Declares stable version-one routing contracts for all Spatial events.</summary>
public static class SpatialEventKinds
{
    public static EventKind EntityPlaced { get; } = new("spatial.entity-placed", 1);
    public static EventKind EntityRemoved { get; } = new("spatial.entity-removed", 1);
    public static EventKind ObservationStateChanged { get; } = new("spatial.observation-state-changed", 1);
    public static EventKind JourneyStarted { get; } = new("spatial.journey-started", 1);
    public static EventKind JourneyRetargeted { get; } = new("spatial.journey-retargeted", 1);
    public static EventKind JourneyCancelled { get; } = new("spatial.journey-cancelled", 1);
    public static EventKind JourneyInterrupted { get; } = new("spatial.journey-interrupted", 1);
    public static EventKind EntityStepped { get; } = new("spatial.entity-stepped", 1);
    public static EventKind JourneyRerouted { get; } = new("spatial.journey-rerouted", 1);
    public static EventKind JourneyContinued { get; } = new("spatial.journey-continued", 1);
    public static EventKind JourneyCompleted { get; } = new("spatial.journey-completed", 1);
    public static EventKind JourneyBlocked { get; } = new("spatial.journey-blocked", 1);
    public static EventKind PortalStateChanged { get; } = new("spatial.portal-state-changed", 1);
    public static EventKind CellStateChanged { get; } = new("spatial.cell-state-changed", 1);
    public static EventKind MutationScheduled { get; } = new("spatial.mutation-scheduled", 1);
    public static EventKind MutationConsumed { get; } = new("spatial.mutation-consumed", 1);
    public static EventKind MomentResolved { get; } = new("spatial.moment-resolved", 1);
    public static EventKind ZoneEntered { get; } = new("spatial.zone-entered", 1);
    public static EventKind ZoneLeft { get; } = new("spatial.zone-left", 1);
    public static EventKind CoPresenceStarted { get; } = new("spatial.copresence-started", 1);
    public static EventKind CoPresenceEnded { get; } = new("spatial.copresence-ended", 1);
    public static EventKind GeometricVisibilityChanged { get; } = new("spatial.geometric-visibility-changed", 1);

    /// <summary>Gets the exact routing kind for a known Spatial payload type.</summary>
    public static EventKind For(SpatialEvent payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return payload switch
        {
            EntityPlacedEvent => EntityPlaced,
            EntityRemovedEvent => EntityRemoved,
            ObservationStateChangedEvent => ObservationStateChanged,
            JourneyStartedEvent => JourneyStarted,
            JourneyRetargetedEvent => JourneyRetargeted,
            JourneyCancelledEvent => JourneyCancelled,
            JourneyInterruptedEvent => JourneyInterrupted,
            EntitySteppedEvent => EntityStepped,
            JourneyReroutedEvent => JourneyRerouted,
            JourneyContinuedEvent => JourneyContinued,
            JourneyCompletedEvent => JourneyCompleted,
            JourneyBlockedEvent => JourneyBlocked,
            PortalStateChangedEvent => PortalStateChanged,
            CellStateChangedEvent => CellStateChanged,
            MutationScheduledEvent => MutationScheduled,
            MutationConsumedEvent => MutationConsumed,
            MomentResolvedEvent => MomentResolved,
            ZoneEnteredEvent => ZoneEntered,
            ZoneLeftEvent => ZoneLeft,
            CoPresenceStartedEvent => CoPresenceStarted,
            CoPresenceEndedEvent => CoPresenceEnded,
            GeometricVisibilityChangedEvent => GeometricVisibilityChanged,
            _ => throw new ArgumentOutOfRangeException(nameof(payload), payload.GetType().Name, "Unknown Spatial event payload."),
        };
    }
}
