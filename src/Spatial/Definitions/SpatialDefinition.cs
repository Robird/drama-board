using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace DramaBoard.Spatial;

/// <summary>Owns canonical immutable spatial content for one simulation run.</summary>
public sealed class SpatialDefinition
{
    private readonly IReadOnlyDictionary<MapId, GridMapDefinition> _mapsById;

    private SpatialDefinition(
        SpatialDefinitionId id,
        long revision,
        ushort rulesVersion,
        GridMapDefinition[] maps,
        PortalDefinition[] portals,
        AnchorDefinition[] anchors,
        ZoneDefinition[] zones)
    {
        Id = id;
        Revision = revision;
        RulesVersion = rulesVersion;
        Maps = Array.AsReadOnly(maps);
        Portals = Array.AsReadOnly(portals);
        Anchors = Array.AsReadOnly(anchors);
        Zones = Array.AsReadOnly(zones);
        _mapsById = maps.ToDictionary(map => map.Id);
        ContentHash = ComputeContentHash(maps, portals, anchors, zones);
    }

    /// <summary>Gets the stable definition identifier.</summary>
    public SpatialDefinitionId Id { get; }

    /// <summary>Gets the non-negative content revision declared by the scenario.</summary>
    public long Revision { get; }

    /// <summary>Gets the canonical SHA-256 digest of maps, portals, anchors, and zones.</summary>
    public SpatialContentHash ContentHash { get; }

    /// <summary>Gets the positive version of spatial interpretation rules.</summary>
    public ushort RulesVersion { get; }

    /// <summary>Gets maps ordered by stable identifier.</summary>
    public IReadOnlyList<GridMapDefinition> Maps { get; }

    /// <summary>Gets directed portals ordered by stable identifier.</summary>
    public IReadOnlyList<PortalDefinition> Portals { get; }

    /// <summary>Gets anchors ordered by stable identifier.</summary>
    public IReadOnlyList<AnchorDefinition> Anchors { get; }

    /// <summary>Gets zones ordered by stable identifier.</summary>
    public IReadOnlyList<ZoneDefinition> Zones { get; }

    /// <summary>Creates and completely validates canonical immutable spatial content.</summary>
    public static SpatialDefinition Create(
        SpatialDefinitionId id,
        long revision,
        ushort rulesVersion,
        IEnumerable<GridMapDefinition> maps,
        IEnumerable<PortalDefinition>? portals = null,
        IEnumerable<AnchorDefinition>? anchors = null,
        IEnumerable<ZoneDefinition>? zones = null)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
        {
            throw new ArgumentException("Spatial definition identifier must be initialized.", nameof(id));
        }

        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision), "Definition revision cannot be negative.");
        }

        if (rulesVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(rulesVersion), "Rules version must be at least 1.");
        }

        ArgumentNullException.ThrowIfNull(maps);
        GridMapDefinition[] canonicalMaps = Canonicalize(maps, map => map.Id, nameof(maps));
        if (canonicalMaps.Length == 0)
        {
            throw new ArgumentException("Spatial definition must contain at least one map.", nameof(maps));
        }

        PortalDefinition[] canonicalPortals = Canonicalize(
            portals ?? [],
            portal => portal.Id,
            nameof(portals));
        AnchorDefinition[] canonicalAnchors = Canonicalize(
            anchors ?? [],
            anchor => anchor.Id,
            nameof(anchors));
        ZoneDefinition[] canonicalZones = Canonicalize(
            zones ?? [],
            zone => zone.Id,
            nameof(zones));

        ValidateGloballyUniqueContentIds(canonicalMaps, canonicalPortals, canonicalAnchors, canonicalZones);
        var mapsById = canonicalMaps.ToDictionary(map => map.Id);
        foreach (PortalDefinition portal in canonicalPortals)
        {
            ValidateCellReference(portal.From, mapsById, $"Portal '{portal.Id}' source");
            ValidateCellReference(portal.To, mapsById, $"Portal '{portal.Id}' destination");
        }

        foreach (AnchorDefinition anchor in canonicalAnchors)
        {
            ValidateCellReference(anchor.Cell, mapsById, $"Anchor '{anchor.Id}'");
        }

        foreach (ZoneDefinition zone in canonicalZones)
        {
            foreach (CellRef cell in zone.Cells)
            {
                ValidateCellReference(cell, mapsById, $"Zone '{zone.Id}'");
            }
        }

        return new SpatialDefinition(
            id,
            revision,
            rulesVersion,
            canonicalMaps,
            canonicalPortals,
            canonicalAnchors,
            canonicalZones);
    }

    /// <summary>Gets one map or throws when its identifier is not present.</summary>
    public GridMapDefinition GetMap(MapId id) =>
        _mapsById.TryGetValue(id, out GridMapDefinition? map)
            ? map
            : throw new KeyNotFoundException($"Spatial map '{id}' does not exist.");

    /// <summary>Gets one valid referenced cell.</summary>
    public CellDefinition GetCell(CellRef cell)
    {
        GridMapDefinition map = GetMap(cell.MapId);
        return map.GetCell(cell.X, cell.Y);
    }

    /// <summary>Returns whether a cell reference lies inside a defined map.</summary>
    public bool Contains(CellRef cell) =>
        _mapsById.TryGetValue(cell.MapId, out GridMapDefinition? map) &&
        cell.X < map.Width &&
        cell.Y < map.Height;

    private static T[] Canonicalize<T, TId>(
        IEnumerable<T> values,
        Func<T, TId> selectId,
        string parameterName)
        where T : class
        where TId : IComparable<TId>
    {
        ArgumentNullException.ThrowIfNull(values);
        T[] array = [.. values];
        if (array.Any(value => value is null))
        {
            throw new ArgumentException("Definition collections cannot contain null entries.", parameterName);
        }

        T[] canonical = [.. array.OrderBy(selectId)];
        for (int index = 1; index < canonical.Length; index++)
        {
            if (selectId(canonical[index - 1]).CompareTo(selectId(canonical[index])) == 0)
            {
                throw new ArgumentException(
                    $"Definition collection contains duplicate identifier '{selectId(canonical[index])}'.",
                    parameterName);
            }
        }

        return canonical;
    }

    private static void ValidateGloballyUniqueContentIds(
        IEnumerable<GridMapDefinition> maps,
        IEnumerable<PortalDefinition> portals,
        IEnumerable<AnchorDefinition> anchors,
        IEnumerable<ZoneDefinition> zones)
    {
        string[] ids =
        [
            .. maps.Select(map => map.Id.Value),
            .. portals.Select(portal => portal.Id.Value),
            .. anchors.Select(anchor => anchor.Id.Value),
            .. zones.Select(zone => zone.Id.Value),
        ];
        string? duplicate = ids
            .GroupBy(value => value, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Map, portal, anchor, and zone identifiers must be globally unique; duplicate '{duplicate}'.");
        }
    }

    private static void ValidateCellReference(
        CellRef cell,
        IReadOnlyDictionary<MapId, GridMapDefinition> maps,
        string description)
    {
        if (!maps.TryGetValue(cell.MapId, out GridMapDefinition? map) ||
            cell.X >= map.Width ||
            cell.Y >= map.Height)
        {
            throw new ArgumentException($"{description} references undefined cell '{cell}'.");
        }
    }

    private static SpatialContentHash ComputeContentHash(
        IEnumerable<GridMapDefinition> maps,
        IEnumerable<PortalDefinition> portals,
        IEnumerable<AnchorDefinition> anchors,
        IEnumerable<ZoneDefinition> zones)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            WriteMaps(writer, maps);
            WritePortals(writer, portals);
            WriteAnchors(writer, anchors);
            WriteZones(writer, zones);
            writer.WriteEndObject();
        }

        string hash = Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
        return new SpatialContentHash(hash);
    }

    private static void WriteMaps(Utf8JsonWriter writer, IEnumerable<GridMapDefinition> maps)
    {
        writer.WriteStartArray("maps");
        foreach (GridMapDefinition map in maps)
        {
            writer.WriteStartObject();
            writer.WriteString("id", map.Id.Value);
            writer.WriteNumber("width", map.Width);
            writer.WriteNumber("height", map.Height);
            writer.WriteNumber("orthogonalStepTicks", map.OrthogonalStepDuration.Ticks);
            writer.WriteNumber("visionRange", map.VisionRange);
            writer.WriteStartArray("cells");
            foreach (CellDefinition cell in map.Cells)
            {
                writer.WriteStartObject();
                writer.WriteString("terrainId", cell.TerrainId.Value);
                writer.WriteNumber("moveCost", cell.MoveCost);
                writer.WriteBoolean("blocksMovement", cell.BlocksMovement);
                writer.WriteBoolean("blocksSight", cell.BlocksSight);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WritePortals(Utf8JsonWriter writer, IEnumerable<PortalDefinition> portals)
    {
        writer.WriteStartArray("portals");
        foreach (PortalDefinition portal in portals)
        {
            writer.WriteStartObject();
            writer.WriteString("id", portal.Id.Value);
            WriteCell(writer, "from", portal.From);
            WriteCell(writer, "to", portal.To);
            writer.WriteNumber("traversalTicks", portal.TraversalDuration.Ticks);
            writer.WriteBoolean("initiallyEnabled", portal.InitiallyEnabled);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteAnchors(Utf8JsonWriter writer, IEnumerable<AnchorDefinition> anchors)
    {
        writer.WriteStartArray("anchors");
        foreach (AnchorDefinition anchor in anchors)
        {
            writer.WriteStartObject();
            writer.WriteString("id", anchor.Id.Value);
            WriteCell(writer, "cell", anchor.Cell);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteZones(Utf8JsonWriter writer, IEnumerable<ZoneDefinition> zones)
    {
        writer.WriteStartArray("zones");
        foreach (ZoneDefinition zone in zones)
        {
            writer.WriteStartObject();
            writer.WriteString("id", zone.Id.Value);
            writer.WriteStartArray("cells");
            foreach (CellRef cell in zone.Cells)
            {
                WriteCell(writer, cell);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteCell(Utf8JsonWriter writer, string propertyName, CellRef cell)
    {
        writer.WritePropertyName(propertyName);
        WriteCell(writer, cell);
    }

    private static void WriteCell(Utf8JsonWriter writer, CellRef cell)
    {
        writer.WriteStartObject();
        writer.WriteString("mapId", cell.MapId.Value);
        writer.WriteNumber("x", cell.X);
        writer.WriteNumber("y", cell.Y);
        writer.WriteEndObject();
    }
}
