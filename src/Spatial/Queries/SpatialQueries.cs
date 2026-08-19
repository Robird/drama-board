namespace DramaBoard.Spatial;

/// <summary>Provides objective spatial queries derived only from committed definition and state.</summary>
public sealed class SpatialQueries
{
    private readonly SpatialDefinition _definition;

    /// <summary>Initializes queries pinned to one immutable spatial definition.</summary>
    public SpatialQueries(SpatialDefinition definition)
    {
        SpatialRules.EnsureSupported(definition);
        _definition = definition;
    }

    /// <summary>
    /// Gets visible cells on the observer's map in stable CellRef order. ObservationEnabled does
    /// not restrict this objective query.
    /// </summary>
    public IReadOnlyList<CellRef> GetVisibleCells(SpatialState state, EntityId observerId)
    {
        EffectiveSpatialTopology topology = RequireCompleteTopology(state);
        SpatialEntityState observer = RequireObserver(state, observerId);
        return ReadOnly(GetVisibleCellsCore(topology, observer.Cell));
    }

    /// <summary>
    /// Gets visible placed entities in stable EntityId order, excluding the observer. Other
    /// entities in the observer's own cell are visible.
    /// </summary>
    public IReadOnlyList<EntityId> GetVisibleEntities(SpatialState state, EntityId observerId)
    {
        EffectiveSpatialTopology topology = RequireCompleteTopology(state);
        SpatialEntityState observer = RequireObserver(state, observerId);
        var visibleCells = new HashSet<CellRef>(GetVisibleCellsCore(topology, observer.Cell));
        EntityId[] result =
        [
            .. state.Entities
                .Where(entity => entity.Id != observerId && visibleCells.Contains(entity.Cell))
                .Select(entity => entity.Id)
                .Order(),
        ];
        return Array.AsReadOnly(result);
    }

    /// <summary>Returns whether one placed observer can objectively see a defined target cell.</summary>
    public bool IsCellVisible(SpatialState state, EntityId observerId, CellRef target)
    {
        EffectiveSpatialTopology topology = RequireCompleteTopology(state);
        SpatialEntityState observer = RequireObserver(state, observerId);
        SpatialStateValidator.EnsureCellExists(_definition, target, "Visibility target");
        return IsCellVisibleCore(topology, observer.Cell, target);
    }

    /// <summary>
    /// Tests range-limited strict-supercover line of sight between two defined cells. Sight never
    /// crosses maps or propagates through portals.
    /// </summary>
    public bool HasLineOfSight(SpatialState state, CellRef firstCell, CellRef secondCell)
    {
        EffectiveSpatialTopology topology = RequireCompleteTopology(state);
        SpatialStateValidator.EnsureCellExists(_definition, firstCell, "Line-of-sight source");
        SpatialStateValidator.EnsureCellExists(_definition, secondCell, "Line-of-sight target");
        return IsCellVisibleCore(topology, firstCell, secondCell);
    }

    private IEnumerable<CellRef> GetVisibleCellsCore(
        EffectiveSpatialTopology topology,
        CellRef source)
    {
        GridMapDefinition map = _definition.GetMap(source.MapId);
        int range = map.VisionRange;
        int minimumX = (int)Math.Max(0L, (long)source.X - range);
        int maximumX = (int)Math.Min((long)map.Width - 1, (long)source.X + range);
        int minimumY = (int)Math.Max(0L, (long)source.Y - range);
        int maximumY = (int)Math.Min((long)map.Height - 1, (long)source.Y + range);

        for (int y = minimumY; y <= maximumY; y++)
        {
            for (int x = minimumX; x <= maximumX; x++)
            {
                var target = new CellRef(map.Id, x, y);
                if (ManhattanDistance(source, target) <= range &&
                    StrictSupercover.HasLineOfSight(source, target, topology.BlocksSight))
                {
                    yield return target;
                }
            }
        }
    }

    private bool IsCellVisibleCore(
        EffectiveSpatialTopology topology,
        CellRef source,
        CellRef target)
    {
        if (source.MapId != target.MapId)
        {
            return false;
        }

        GridMapDefinition map = _definition.GetMap(source.MapId);
        return ManhattanDistance(source, target) <= map.VisionRange &&
            StrictSupercover.HasLineOfSight(source, target, topology.BlocksSight);
    }

    private SpatialEntityState RequireObserver(SpatialState state, EntityId observerId)
    {
        SpatialStateValidator.EnsureEntityId(observerId, "Visibility observer");
        return SpatialStateValidator.RequireEntity(state, observerId);
    }

    private EffectiveSpatialTopology RequireCompleteTopology(SpatialState state)
    {
        SpatialStateValidator.ValidateComplete(_definition, state);
        return new EffectiveSpatialTopology(_definition, state);
    }

    private static long ManhattanDistance(CellRef first, CellRef second) =>
        Math.Abs((long)first.X - second.X) + Math.Abs((long)first.Y - second.Y);

    private static IReadOnlyList<T> ReadOnly<T>(IEnumerable<T> values) =>
        Array.AsReadOnly(values.ToArray());
}
