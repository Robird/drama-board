namespace DramaBoard.Protocol;

/// <summary>Describes an action affordance and its optional candidate targets.</summary>
/// <param name="ActionKind">The stable action contract identifier.</param>
/// <param name="CandidateActorIds">The optional actor candidates allowed by the affordance.</param>
/// <param name="CandidateObjectIds">The optional object candidates allowed by the affordance.</param>
/// <param name="CandidateExitIds">The optional observed-exit candidates allowed by the affordance.</param>
/// <param name="CandidateDestinationIds">The optional destination candidates allowed by the affordance.</param>
public sealed record AvailableAction
{
    /// <summary>Creates one immutable action-affordance snapshot.</summary>
    public AvailableAction(
        ActionKind ActionKind,
        IReadOnlyList<string>? CandidateActorIds = null,
        IReadOnlyList<string>? CandidateObjectIds = null,
        IReadOnlyList<string>? CandidateExitIds = null,
        IReadOnlyList<string>? CandidateDestinationIds = null)
    {
        this.ActionKind = ActionKind;
        this.CandidateActorIds = FrozenList.OptionalSnapshot(CandidateActorIds);
        this.CandidateObjectIds = FrozenList.OptionalSnapshot(CandidateObjectIds);
        this.CandidateExitIds = FrozenList.OptionalSnapshot(CandidateExitIds);
        this.CandidateDestinationIds = FrozenList.OptionalSnapshot(CandidateDestinationIds);
    }

    public ActionKind ActionKind { get; }

    public IReadOnlyList<string>? CandidateActorIds { get; }

    public IReadOnlyList<string>? CandidateObjectIds { get; }

    public IReadOnlyList<string>? CandidateExitIds { get; }

    public IReadOnlyList<string>? CandidateDestinationIds { get; }
}

/// <summary>Requests one in-flight decision from a Player for an actor.</summary>
/// <param name="DecisionId">The identifier correlating the request and response.</param>
/// <param name="ModelTimeMs">The decision-point model time, where one unit is one millisecond.</param>
/// <param name="ActorId">The identifier of the actor making the decision.</param>
/// <param name="Observation">The actor's legal subjective observation.</param>
/// <param name="AvailableActions">The actions and targets currently afforded to the actor.</param>
public sealed record DecisionRequest
{
    /// <summary>Creates one frozen request and validates its exit affordances.</summary>
    public DecisionRequest(
        DecisionId DecisionId,
        string ActorId,
        long ModelTimeMs,
        Observation Observation,
        IReadOnlyList<AvailableAction> AvailableActions)
    {
        if (string.IsNullOrWhiteSpace(DecisionId.Value))
        {
            throw new ArgumentException("Decision identifier must be initialized.", nameof(DecisionId));
        }

        ArgumentNullException.ThrowIfNull(Observation);
        this.DecisionId = DecisionId;
        this.ActorId = ActorId;
        this.ModelTimeMs = ModelTimeMs;
        this.Observation = Observation;
        this.AvailableActions = FrozenList.Snapshot(AvailableActions);
        ValidateExitAffordances(Observation, this.AvailableActions);
    }

    public DecisionId DecisionId { get; }

    public string ActorId { get; }

    public long ModelTimeMs { get; }

    public Observation Observation { get; }

    public IReadOnlyList<AvailableAction> AvailableActions { get; }

    private static void ValidateExitAffordances(
        Observation observation,
        IReadOnlyList<AvailableAction> availableActions)
    {
        IReadOnlyDictionary<string, ObservedExit> exits = observation.Exits.ToDictionary(
            exit => exit.ExitId,
            StringComparer.Ordinal);
        foreach (AvailableAction action in availableActions)
        {
            if (action.CandidateExitIds is null)
            {
                continue;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string exitId in action.CandidateExitIds)
            {
                if (!seen.Add(exitId))
                {
                    throw new ArgumentException(
                        $"Exit candidate '{exitId}' is duplicated in one affordance.",
                        nameof(availableActions));
                }

                if (!exits.TryGetValue(exitId, out ObservedExit? exit) || !exit.IsAvailable)
                {
                    throw new ArgumentException(
                        $"Exit candidate '{exitId}' must name one available observed exit.",
                        nameof(availableActions));
                }
            }
        }
    }
}

internal static class FrozenList
{
    public static IReadOnlyList<T> Snapshot<T>(IEnumerable<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Array.AsReadOnly(values.ToArray());
    }

    public static IReadOnlyList<T>? OptionalSnapshot<T>(IEnumerable<T>? values) =>
        values is null ? null : Snapshot(values);
}
