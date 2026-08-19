namespace DramaBoard.Spatial;

/// <summary>Identifies one immutable spatial definition.</summary>
public readonly record struct SpatialDefinitionId : IComparable<SpatialDefinitionId>
{
    /// <summary>Initializes an identifier from its stable value.</summary>
    public SpatialDefinitionId(string value)
    {
        Value = StableIdentifier.Validate(value, nameof(value), "Spatial definition identifier");
    }

    /// <summary>Gets the stable identifier value.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public int CompareTo(SpatialDefinitionId other) =>
        StringComparer.Ordinal.Compare(Value, other.Value);

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Identifies canonical spatial content by its SHA-256 digest.</summary>
public readonly record struct SpatialContentHash : IComparable<SpatialContentHash>
{
    /// <summary>Initializes a content hash from 64 lowercase hexadecimal characters.</summary>
    public SpatialContentHash(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "Spatial content hash must contain exactly 64 lowercase hexadecimal characters.",
                nameof(value));
        }

        Value = value;
    }

    /// <summary>Gets the lowercase hexadecimal SHA-256 digest.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public int CompareTo(SpatialContentHash other) =>
        StringComparer.Ordinal.Compare(Value, other.Value);

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Identifies one finite grid map.</summary>
public readonly record struct MapId : IComparable<MapId>
{
    /// <summary>Initializes an identifier from its stable value.</summary>
    public MapId(string value)
    {
        Value = StableIdentifier.Validate(value, nameof(value), "Map identifier");
    }

    /// <summary>Gets the stable identifier value.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public int CompareTo(MapId other) => StringComparer.Ordinal.Compare(Value, other.Value);

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Identifies one directed portal edge.</summary>
public readonly record struct PortalId : IComparable<PortalId>
{
    /// <summary>Initializes an identifier from its stable value.</summary>
    public PortalId(string value)
    {
        Value = StableIdentifier.Validate(value, nameof(value), "Portal identifier");
    }

    /// <summary>Gets the stable identifier value.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public int CompareTo(PortalId other) => StringComparer.Ordinal.Compare(Value, other.Value);

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Identifies one semantic point in the spatial definition.</summary>
public readonly record struct AnchorId : IComparable<AnchorId>
{
    /// <summary>Initializes an identifier from its stable value.</summary>
    public AnchorId(string value)
    {
        Value = StableIdentifier.Validate(value, nameof(value), "Anchor identifier");
    }

    /// <summary>Gets the stable identifier value.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public int CompareTo(AnchorId other) => StringComparer.Ordinal.Compare(Value, other.Value);

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Identifies one semantic set of cells.</summary>
public readonly record struct ZoneId : IComparable<ZoneId>
{
    /// <summary>Initializes an identifier from its stable value.</summary>
    public ZoneId(string value)
    {
        Value = StableIdentifier.Validate(value, nameof(value), "Zone identifier");
    }

    /// <summary>Gets the stable identifier value.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public int CompareTo(ZoneId other) => StringComparer.Ordinal.Compare(Value, other.Value);

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Identifies a terrain contract used by map content.</summary>
public readonly record struct TerrainId : IComparable<TerrainId>
{
    /// <summary>Initializes an identifier from its stable value.</summary>
    public TerrainId(string value)
    {
        Value = StableIdentifier.Validate(value, nameof(value), "Terrain identifier");
    }

    /// <summary>Gets the stable identifier value.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public int CompareTo(TerrainId other) => StringComparer.Ordinal.Compare(Value, other.Value);

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Identifies one spatial entity with a stable positive integer.</summary>
public readonly record struct EntityId : IComparable<EntityId>
{
    /// <summary>Initializes an entity identifier.</summary>
    public EntityId(long value)
    {
        Value = PositiveIdentifier.Validate(value, nameof(value), "Entity identifier");
    }

    /// <summary>Gets the positive identifier value.</summary>
    public long Value { get; }

    /// <inheritdoc />
    public int CompareTo(EntityId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies one journey with a stable positive integer.</summary>
public readonly record struct JourneyId : IComparable<JourneyId>
{
    /// <summary>Initializes a journey identifier.</summary>
    public JourneyId(long value)
    {
        Value = PositiveIdentifier.Validate(value, nameof(value), "Journey identifier");
    }

    /// <summary>Gets the positive identifier value.</summary>
    public long Value { get; }

    /// <inheritdoc />
    public int CompareTo(JourneyId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies one scheduled spatial mutation with a stable positive integer.</summary>
public readonly record struct ScheduledMutationId : IComparable<ScheduledMutationId>
{
    /// <summary>Initializes a scheduled-mutation identifier.</summary>
    public ScheduledMutationId(long value)
    {
        Value = PositiveIdentifier.Validate(value, nameof(value), "Scheduled mutation identifier");
    }

    /// <summary>Gets the positive identifier value.</summary>
    public long Value { get; }

    /// <inheritdoc />
    public int CompareTo(ScheduledMutationId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal static class StableIdentifier
{
    private const int MaximumLength = 256;

    public static string Validate(string value, string parameterName, string description)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{description} cannot be empty.", parameterName);
        }

        if (value.Length > MaximumLength)
        {
            throw new ArgumentException(
                $"{description} cannot exceed {MaximumLength} characters.",
                parameterName);
        }

        if (value.Any(char.IsControl))
        {
            throw new ArgumentException($"{description} cannot contain control characters.", parameterName);
        }

        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    throw new ArgumentException($"{description} must contain well-formed UTF-16.", parameterName);
                }

                index++;
            }
            else if (char.IsLowSurrogate(character))
            {
                throw new ArgumentException($"{description} must contain well-formed UTF-16.", parameterName);
            }
        }

        return value;
    }
}

internal static class PositiveIdentifier
{
    public static long Validate(long value, string parameterName, string description)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"{description} must be positive.");
        }

        return value;
    }
}
