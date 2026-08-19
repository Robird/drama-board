namespace DramaBoard.Spatial;

/// <summary>Pins runtime state to immutable spatial content and interpretation rules.</summary>
public readonly record struct SpatialDefinitionStamp
{
    /// <summary>Initializes a runtime definition stamp.</summary>
    public SpatialDefinitionStamp(
        SpatialDefinitionId definitionId,
        SpatialContentHash contentHash,
        ushort rulesVersion)
    {
        if (string.IsNullOrWhiteSpace(definitionId.Value))
        {
            throw new ArgumentException("Spatial definition identifier must be initialized.", nameof(definitionId));
        }

        if (string.IsNullOrWhiteSpace(contentHash.Value))
        {
            throw new ArgumentException("Spatial content hash must be initialized.", nameof(contentHash));
        }

        if (rulesVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(rulesVersion), "Rules version must be at least 1.");
        }

        DefinitionId = definitionId;
        ContentHash = contentHash;
        RulesVersion = rulesVersion;
    }

    /// <summary>Gets the stable definition identifier.</summary>
    public SpatialDefinitionId DefinitionId { get; }

    /// <summary>Gets the canonical content digest.</summary>
    public SpatialContentHash ContentHash { get; }

    /// <summary>Gets the spatial interpretation-rules version.</summary>
    public ushort RulesVersion { get; }

    /// <summary>Creates the runtime stamp for a definition.</summary>
    public static SpatialDefinitionStamp From(SpatialDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new SpatialDefinitionStamp(definition.Id, definition.ContentHash, definition.RulesVersion);
    }
}
