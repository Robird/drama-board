namespace DramaBoard.Protocol;

/// <summary>Describes an action affordance and its optional candidate targets.</summary>
/// <param name="ActionKind">The stable action contract identifier.</param>
/// <param name="CandidateActorIds">The optional actor candidates allowed by the affordance.</param>
/// <param name="CandidateObjectIds">The optional object candidates allowed by the affordance.</param>
/// <param name="CandidateDestinationIds">The optional destination candidates allowed by the affordance.</param>
public sealed record AvailableAction(
    ActionKind ActionKind,
    IReadOnlyList<string>? CandidateActorIds = null,
    IReadOnlyList<string>? CandidateObjectIds = null,
    IReadOnlyList<string>? CandidateDestinationIds = null);

/// <summary>Requests one version-bound decision from a Player for an actor.</summary>
/// <param name="DecisionId">The identifier correlating the request and response.</param>
/// <param name="BasedOnWorldVersion">The committed journal event count on which this request is based.</param>
/// <param name="LineageId">The world lineage containing the journal prefix on which this request is based.</param>
/// <param name="ModelTimeMs">The decision-point model time, where one unit is one millisecond.</param>
/// <param name="Microstep">The decision event's ordering position within its model time.</param>
/// <param name="ActorId">The identifier of the actor making the decision.</param>
/// <param name="Observation">The actor's legal subjective observation.</param>
/// <param name="Reason">The stable reason why this decision point was opened.</param>
/// <param name="AvailableActions">The action affordances available at this decision point.</param>
/// <param name="RejectedIntent">The actor's most recently rejected intent, when this request reports a rejection.</param>
public sealed record DecisionRequest(
    DecisionId DecisionId,
    long BasedOnWorldVersion,
    long LineageId,
    long ModelTimeMs,
    int Microstep,
    string ActorId,
    Observation Observation,
    DecisionReason Reason,
    IReadOnlyList<AvailableAction> AvailableActions,
    Intent? RejectedIntent = null);
