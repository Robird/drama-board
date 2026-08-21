using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial;

public sealed record RouteLeg(PassageId PassageId, PlaceId FromPlaceId, PlaceId ToPlaceId);

public abstract record RouteResult
{
    private protected RouteResult()
    {
    }
}

public sealed record RouteFound : RouteResult
{
    public RouteFound(ModelDuration totalDuration, IEnumerable<RouteLeg> legs)
    {
        ArgumentNullException.ThrowIfNull(legs);
        RouteLeg[] array = [.. legs];
        if (array.Length == 0 || array.Any(leg => leg is null))
        {
            throw new ArgumentException("A found route requires at least one non-null leg.", nameof(legs));
        }

        TotalDuration = totalDuration;
        Legs = Array.AsReadOnly(array);
    }

    public ModelDuration TotalDuration { get; }

    public IReadOnlyList<RouteLeg> Legs { get; }

    public bool Equals(RouteFound? other) =>
        other is not null &&
        TotalDuration == other.TotalDuration &&
        Legs.SequenceEqual(other.Legs);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(TotalDuration);
        foreach (RouteLeg leg in Legs)
        {
            hash.Add(leg);
        }

        return hash.ToHashCode();
    }
}

public sealed record AlreadyAtGoal : RouteResult;

public sealed record NoRoute : RouteResult;

public sealed record UnknownStart : RouteResult;

public sealed record UnknownGoal : RouteResult;

public sealed record InvalidSpeed : RouteResult;

public sealed record CostOverflow : RouteResult;
