namespace DramaBoard.Protocol;

/// <summary>Describes one fact currently known to the observing actor.</summary>
/// <param name="FactKind">The stable fact contract identifier.</param>
/// <param name="RelatedId">The optional identifier of the entity or place to which the fact relates.</param>
/// <param name="Text">The actor-visible text of the fact.</param>
public sealed record KnownFact(
    FactKind FactKind,
    string? RelatedId,
    string Text);

/// <summary>Contains only the world information legally visible or known to one actor.</summary>
/// <param name="ActorId">The identifier of the observing actor.</param>
/// <param name="LocationId">The actor's current location identifier.</param>
/// <param name="ModelTimeMs">The current model time, where one unit is one millisecond.</param>
/// <param name="VisibleActorIds">The identifiers of actors currently visible to the observer.</param>
/// <param name="VisibleObjectIds">The identifiers of objects currently visible to the observer.</param>
/// <param name="KnownFacts">The facts currently known to the observer.</param>
public sealed record Observation(
    string ActorId,
    string LocationId,
    long ModelTimeMs,
    IReadOnlyList<string> VisibleActorIds,
    IReadOnlyList<string> VisibleObjectIds,
    IReadOnlyList<KnownFact> KnownFacts);
