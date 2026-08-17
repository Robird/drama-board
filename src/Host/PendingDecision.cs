using DramaBoard.Kernel.Journal;
using DramaBoard.Protocol;

namespace DramaBoard.Host;

/// <summary>Describes the lifecycle state of a decision request held by a Host session.</summary>
public enum PendingDecisionStatus
{
    /// <summary>The request still needs a valid Player answer.</summary>
    Open,

    /// <summary>The request has a valid answer waiting for its batch to be submitted.</summary>
    Answered,

    /// <summary>The request or its latest answer was invalidated.</summary>
    Invalidated,
}

/// <summary>Explains why a pending decision was invalidated.</summary>
public enum PendingDecisionInvalidationReason
{
    /// <summary>The request no longer exists in the current world projection.</summary>
    StaleRequest,

    /// <summary>The Player answer did not correlate with its request.</summary>
    ValidationFailed,
}

/// <summary>Exposes one decision request retained by a Host session.</summary>
public sealed class PendingDecision<TEventPayload>
{
    internal PendingDecision(DomainEvent<TEventPayload> requestEvent, string actorId)
    {
        ArgumentNullException.ThrowIfNull(requestEvent);
        RequestEvent = requestEvent;
        ActorId = actorId;
    }

    /// <summary>Gets the committed domain event that opened the decision.</summary>
    public DomainEvent<TEventPayload> RequestEvent { get; }

    /// <summary>Gets the actor routed from the request event.</summary>
    public string ActorId { get; }

    /// <summary>Gets the current lifecycle state.</summary>
    public PendingDecisionStatus Status { get; internal set; }

    /// <summary>Gets the latest invalidation reason, if any.</summary>
    public PendingDecisionInvalidationReason? InvalidationReason { get; internal set; }

    internal PlayerDecision? Answer { get; set; }

    internal bool IsForced { get; set; }

    internal int ValidationFailureCount { get; set; }

    internal bool IsTerminalInvalidation =>
        Status == PendingDecisionStatus.Invalidated &&
        InvalidationReason == PendingDecisionInvalidationReason.StaleRequest;
}