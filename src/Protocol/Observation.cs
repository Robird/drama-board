namespace DramaBoard.Protocol;

/// <summary>Describes one fact currently known to the observing actor.</summary>
/// <param name="FactKind">The stable fact contract identifier.</param>
/// <param name="RelatedId">The optional identifier of the entity or place to which the fact relates.</param>
/// <param name="Text">The actor-visible text of the fact.</param>
public sealed record KnownFact(
    FactKind FactKind,
    string? RelatedId,
    string Text);

/// <summary>Describes one Player-visible way to leave the current place.</summary>
/// <param name="ExitId">The opaque affordance identifier selected by a Player.</param>
/// <param name="DestinationId">The visible destination reached through the exit.</param>
/// <param name="ExpectedDurationMs">The expected travel duration in model-time milliseconds.</param>
/// <param name="IsAvailable">Whether the exit can currently be selected.</param>
public sealed record ObservedExit
{
    /// <summary>Creates one observed exit after validating its stable values.</summary>
    public ObservedExit(
        string exitId,
        string destinationId,
        long expectedDurationMs,
        bool isAvailable)
    {
        ExitId = StableIdentifier.Validate(exitId, nameof(exitId), "Exit identifier");
        DestinationId = StableIdentifier.Validate(
            destinationId,
            nameof(destinationId),
            "Exit destination identifier");
        if (expectedDurationMs < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedDurationMs),
                expectedDurationMs,
                "Expected exit duration must be at least one millisecond.");
        }

        ExpectedDurationMs = expectedDurationMs;
        IsAvailable = isAvailable;
    }

    /// <summary>Gets the opaque affordance identifier selected by a Player.</summary>
    public string ExitId { get; }

    /// <summary>Gets the visible destination reached through the exit.</summary>
    public string DestinationId { get; }

    /// <summary>Gets the expected travel duration in model-time milliseconds.</summary>
    public long ExpectedDurationMs { get; }

    /// <summary>Gets whether the exit can currently be selected.</summary>
    public bool IsAvailable { get; }
}

/// <summary>Contains only the world information legally visible or known to one actor.</summary>
/// <param name="ActorId">The identifier of the observing actor.</param>
/// <param name="LocationId">The actor's current location identifier.</param>
/// <param name="ModelTimeMs">The current model time, where one unit is one millisecond.</param>
/// <param name="Exits">The exits currently observed from this location.</param>
/// <param name="VisibleActorIds">The identifiers of actors currently visible to the observer.</param>
/// <param name="VisibleObjectIds">The identifiers of objects currently visible to the observer.</param>
/// <param name="KnownFacts">The facts currently known to the observer.</param>
public sealed record Observation
{
    /// <summary>Creates one immutable observation snapshot.</summary>
    public Observation(
        string ActorId,
        string LocationId,
        long ModelTimeMs,
        IReadOnlyList<ObservedExit> Exits,
        IReadOnlyList<string> VisibleActorIds,
        IReadOnlyList<string> VisibleObjectIds,
        IReadOnlyList<KnownFact> KnownFacts)
    {
        this.ActorId = ActorId;
        this.LocationId = LocationId;
        this.ModelTimeMs = ModelTimeMs;
        this.Exits = FrozenList.Snapshot(Exits);
        this.VisibleActorIds = FrozenList.Snapshot(VisibleActorIds);
        this.VisibleObjectIds = FrozenList.Snapshot(VisibleObjectIds);
        this.KnownFacts = FrozenList.Snapshot(KnownFacts);

        if (this.Exits.Select(exit => exit.ExitId).Distinct(StringComparer.Ordinal).Count() !=
            this.Exits.Count)
        {
            throw new ArgumentException(
                "Observed exit identifiers must be unique.",
                nameof(Exits));
        }
    }

    public string ActorId { get; }

    public string LocationId { get; }

    public long ModelTimeMs { get; }

    public IReadOnlyList<ObservedExit> Exits { get; }

    public IReadOnlyList<string> VisibleActorIds { get; }

    public IReadOnlyList<string> VisibleObjectIds { get; }

    public IReadOnlyList<KnownFact> KnownFacts { get; }
}
