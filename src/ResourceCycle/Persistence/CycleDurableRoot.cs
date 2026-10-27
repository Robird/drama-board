using Atelia.DurableGraph;
using DramaBoard.Kernel.Simulation;

namespace DramaBoard.ResourceCycle;

/// <summary>One publication root for all world subgraphs and their completed Kernel boundary.</summary>
[DurableType("DramaBoard.ResourceCycle.Root", 1)]
public sealed partial class CycleDurableRoot : IDurableObject {
    [DurableField(1)] private readonly CycleWorld _world;
    [DurableField(2)] private readonly KernelCursor _cursor;

    public CycleDurableRoot(CycleWorld world, KernelCursor cursor) {
        _world = world;
        _cursor = cursor;
        Validate();
    }

    public CycleWorld World => _world;
    public KernelCursor Cursor => _cursor;

    /// <summary>Also validates restored objects, whose constructors were not executed.</summary>
    public void Validate() {
        ArgumentNullException.ThrowIfNull(_world);
        _world.Validate();
        _cursor.Validate();
        if (_world.Time != _cursor.CurrentModelTime) {
            throw new InvalidDataException("The world time must equal its completed Kernel cursor time.");
        }
    }
}
