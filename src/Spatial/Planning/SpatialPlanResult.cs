namespace DramaBoard.Spatial;

/// <summary>Base result of one pure Graph Spatial planning attempt.</summary>
public abstract record SpatialPlanResult
{
    private protected SpatialPlanResult()
    {
    }
}

/// <summary>Contains the non-empty ordered facts accepted by a Spatial planner.</summary>
public sealed record SpatialPlanAccepted : SpatialPlanResult
{
    public SpatialPlanAccepted(IEnumerable<GraphSpatialFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        GraphSpatialFact[] array = [.. facts];
        if (array.Length == 0 || array.Any(fact => fact is null))
        {
            throw new ArgumentException("An accepted Spatial plan requires non-null facts.", nameof(facts));
        }

        Facts = Array.AsReadOnly(array);
    }

    public IReadOnlyList<GraphSpatialFact> Facts { get; }
}

/// <summary>Describes why an objective Spatial proposal could not be planned.</summary>
public sealed record SpatialPlanRejected : SpatialPlanResult
{
    public SpatialPlanRejected(string reason)
    {
        Reason = SpatialIdentifier.Require(reason, nameof(reason), "Spatial rejection reason");
    }

    public string Reason { get; }
}
