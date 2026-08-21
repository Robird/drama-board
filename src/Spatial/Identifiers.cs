namespace DramaBoard.Spatial;

/// <summary>Identifies one semantic place in a graph spatial definition.</summary>
public readonly record struct PlaceId : IComparable<PlaceId>
{
    public PlaceId(string value)
    {
        Value = SpatialIdentifier.Require(value, nameof(value), "Place identifier");
    }

    public string Value { get; }

    public int CompareTo(PlaceId other) => StringComparer.Ordinal.Compare(Value, other.Value);

    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Identifies one distinguishable passage between two places.</summary>
public readonly record struct PassageId : IComparable<PassageId>
{
    public PassageId(string value)
    {
        Value = SpatialIdentifier.Require(value, nameof(value), "Passage identifier");
    }

    public string Value { get; }

    public int CompareTo(PassageId other) => StringComparer.Ordinal.Compare(Value, other.Value);

    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Identifies one objectively located entity.</summary>
public readonly record struct EntityId : IComparable<EntityId>
{
    public EntityId(string value)
    {
        Value = SpatialIdentifier.Require(value, nameof(value), "Entity identifier");
    }

    public string Value { get; }

    public int CompareTo(EntityId other) => StringComparer.Ordinal.Compare(Value, other.Value);

    public override string ToString() => Value ?? string.Empty;
}

internal static class SpatialIdentifier
{
    internal static string Require(string value, string parameterName, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!StringComparer.Ordinal.Equals(value, value.Trim()))
        {
            throw new ArgumentException($"{description} cannot have leading or trailing whitespace.", parameterName);
        }

        return value;
    }

    internal static void Require(PlaceId value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value.Value))
        {
            throw new ArgumentException("Place identifier must be initialized.", parameterName);
        }
    }

    internal static void Require(PassageId value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value.Value))
        {
            throw new ArgumentException("Passage identifier must be initialized.", parameterName);
        }
    }

    internal static void Require(EntityId value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value.Value))
        {
            throw new ArgumentException("Entity identifier must be initialized.", parameterName);
        }
    }
}
