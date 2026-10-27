using Atelia.DurableGraph;
using DramaBoard.Kernel.Simulation;

namespace DramaBoard.Spatial.Tests.TestSupport;

/// <summary>Test-only publication root: business, predictions and the completed cursor share one State.</summary>
[DurableType("DramaBoard.Spatial.Tests.PublicationRoot", 1)]
internal sealed partial class SpatialDurableRoot : IDurableObject {
    [DurableField(1)] private readonly SpatialSimulationState _world;
    [DurableField(2)] private readonly KernelCursor _cursor;

    internal SpatialDurableRoot(SpatialSimulationState world, KernelCursor cursor) {
        _world = world;
        _cursor = cursor;
        Validate();
    }

    internal SpatialSimulationState World => _world;
    internal KernelCursor Cursor => _cursor;

    internal void Validate() {
        ArgumentNullException.ThrowIfNull(_world);
        _cursor.Validate();
        // The externally supplied graph definition belongs to the simulation factory.
        // Kernel opening validates the full world against Cursor.CurrentModelTime.
    }
}
