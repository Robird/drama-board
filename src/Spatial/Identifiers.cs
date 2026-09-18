using Atelia.DurableGraph;

namespace DramaBoard.Spatial;

/// <summary>Identifies one semantic place in a graph spatial definition.</summary>
[DurableType("DramaBoard.Spatial.PlaceId", 1)]
public readonly partial record struct PlaceId : IComparable<PlaceId> {
    [DurableField(1)] private readonly string _value;

    public PlaceId(string value) {
        _value = SpatialIdentifier.Require(value, nameof(value), "Place identifier");
    }

    public string Value => _value;

    public int CompareTo(PlaceId other) => StringComparer.Ordinal.Compare(Value, other.Value);

    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Identifies one distinguishable passage between two places.</summary>
[DurableType("DramaBoard.Spatial.PassageId", 1)]
public readonly partial record struct PassageId : IComparable<PassageId> {
    [DurableField(1)] private readonly string _value;
    public PassageId(string value) {
        _value = SpatialIdentifier.Require(value, nameof(value), "Passage identifier");
    }

    public string Value => _value;

    public int CompareTo(PassageId other) => StringComparer.Ordinal.Compare(Value, other.Value);

    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Identifies one objectively located entity.</summary>
[DurableType("DramaBoard.Spatial.EntityId", 1)]
public readonly partial record struct EntityId : IComparable<EntityId> {
    [DurableField(1)] private readonly string _value;
    public EntityId(string value) {
        _value = SpatialIdentifier.Require(value, nameof(value), "Entity identifier");
    }

    public string Value => _value;

    public int CompareTo(EntityId other) => StringComparer.Ordinal.Compare(Value, other.Value);

    public override string ToString() => Value ?? string.Empty;
}

internal static class SpatialIdentifier {
    internal static string Require(string value, string parameterName, string description) {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!StringComparer.Ordinal.Equals(value, value.Trim())) {
            throw new ArgumentException($"{description} cannot have leading or trailing whitespace.", parameterName);
        }

        return value;
    }

    internal static void Require(PlaceId value, string parameterName) {
        if (string.IsNullOrWhiteSpace(value.Value)) {
            throw new ArgumentException("Place identifier must be initialized.", parameterName);
        }
    }

    internal static void Require(PassageId value, string parameterName) {
        if (string.IsNullOrWhiteSpace(value.Value)) {
            throw new ArgumentException("Passage identifier must be initialized.", parameterName);
        }
    }

    internal static void Require(EntityId value, string parameterName) {
        if (string.IsNullOrWhiteSpace(value.Value)) {
            throw new ArgumentException("Entity identifier must be initialized.", parameterName);
        }
    }
}
