namespace DramaBoard.Spatial;

/// <summary>Stores the two independent directions in which new passage segments may be created.</summary>
public readonly record struct PassageEntryAccess(bool EnterableFromA, bool EnterableFromB);

/// <summary>Describes a non-empty partial replacement of passage entry access.</summary>
public readonly record struct PassageEntryPatch
{
    public PassageEntryPatch(bool? enterableFromA, bool? enterableFromB)
    {
        if (enterableFromA is null && enterableFromB is null)
        {
            throw new ArgumentException("A passage entry patch must specify at least one direction.");
        }

        EnterableFromA = enterableFromA;
        EnterableFromB = enterableFromB;
    }

    public bool? EnterableFromA { get; }

    public bool? EnterableFromB { get; }

    internal PassageEntryAccess Apply(PassageEntryAccess current) => new(
        EnterableFromA ?? current.EnterableFromA,
        EnterableFromB ?? current.EnterableFromB);

    internal static void Validate(PassageEntryPatch patch, string parameterName)
    {
        if (patch.EnterableFromA is null && patch.EnterableFromB is null)
        {
            throw new ArgumentException("A passage entry patch must specify at least one direction.", parameterName);
        }
    }
}

/// <summary>Defines one finite, distinguishable connection between two semantic places.</summary>
public sealed record PassageDefinition
{
    public PassageDefinition(
        PassageId id,
        PlaceId endpointA,
        PlaceId endpointB,
        long length,
        PassageEntryAccess initialEntryAccess)
    {
        SpatialIdentifier.Require(id, nameof(id));
        SpatialIdentifier.Require(endpointA, nameof(endpointA));
        SpatialIdentifier.Require(endpointB, nameof(endpointB));
        if (endpointA == endpointB)
        {
            throw new ArgumentException("A passage must connect two different places.", nameof(endpointB));
        }

        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Passage length must be positive.");
        }

        Id = id;
        EndpointA = endpointA;
        EndpointB = endpointB;
        Length = length;
        InitialEntryAccess = initialEntryAccess;
    }

    public PassageId Id { get; }

    public PlaceId EndpointA { get; }

    public PlaceId EndpointB { get; }

    public long Length { get; }

    public PassageEntryAccess InitialEntryAccess { get; }
}

/// <summary>Owns canonical immutable Place and Passage content for a graph spatial world.</summary>
public sealed class GraphDefinition
{
    private readonly IReadOnlyDictionary<PlaceId, PlaceId> _placesById;
    private readonly IReadOnlyDictionary<PassageId, PassageDefinition> _passagesById;

    private GraphDefinition(PlaceId[] places, PassageDefinition[] passages)
    {
        Places = Array.AsReadOnly(places);
        Passages = Array.AsReadOnly(passages);
        _placesById = places.ToDictionary(id => id);
        _passagesById = passages.ToDictionary(passage => passage.Id);
    }

    public IReadOnlyList<PlaceId> Places { get; }

    public IReadOnlyList<PassageDefinition> Passages { get; }

    public static GraphDefinition Create(
        IEnumerable<PlaceId> places,
        IEnumerable<PassageDefinition> passages)
    {
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(passages);

        PlaceId[] canonicalPlaces = [.. places.Order()];
        foreach (PlaceId place in canonicalPlaces)
        {
            SpatialIdentifier.Require(place, nameof(places));
        }

        EnsureUnique(canonicalPlaces, value => value, nameof(places));

        PassageDefinition[] passageArray = [.. passages];
        if (passageArray.Any(passage => passage is null))
        {
            throw new ArgumentException("Passage definitions cannot contain null entries.", nameof(passages));
        }

        PassageDefinition[] canonicalPassages = [.. passageArray.OrderBy(passage => passage.Id)];
        EnsureUnique(canonicalPassages, passage => passage.Id, nameof(passages));
        var knownPlaces = canonicalPlaces.ToHashSet();
        foreach (PassageDefinition passage in canonicalPassages)
        {
            if (!knownPlaces.Contains(passage.EndpointA) || !knownPlaces.Contains(passage.EndpointB))
            {
                throw new ArgumentException(
                    $"Passage '{passage.Id}' references an undefined endpoint.",
                    nameof(passages));
            }
        }

        return new GraphDefinition(canonicalPlaces, canonicalPassages);
    }

    public bool Contains(PlaceId placeId) => _placesById.ContainsKey(placeId);

    public bool Contains(PassageId passageId) => _passagesById.ContainsKey(passageId);

    public PassageDefinition GetPassage(PassageId passageId) =>
        _passagesById.TryGetValue(passageId, out PassageDefinition? passage)
            ? passage
            : throw new KeyNotFoundException($"Passage '{passageId}' does not exist.");

    private static void EnsureUnique<T, TId>(T[] values, Func<T, TId> id, string parameterName)
        where TId : IComparable<TId>
    {
        for (int index = 1; index < values.Length; index++)
        {
            if (id(values[index - 1]).CompareTo(id(values[index])) == 0)
            {
                throw new ArgumentException(
                    $"Definition collection contains duplicate identifier '{id(values[index])}'.",
                    parameterName);
            }
        }
    }
}
