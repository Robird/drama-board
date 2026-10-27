using Atelia.DurableGraph;
using DramaBoard.Kernel.Simulation;

namespace DramaBoard.Spatial.Tests.TestSupport;

/// <summary>Test-only disk root. Derived contacts are deliberately outside the persisted object graph.</summary>
[DurableType("DramaBoard.Spatial.Tests.PublicationRoot", 1)]
internal sealed partial class SpatialDurableRoot : IDurableObject {
    [DurableField(1)] private readonly GraphSpatialState _business;
    [DurableField(2)] private readonly KernelCursor _cursor;

    internal SpatialDurableRoot(GraphSpatialState business, KernelCursor cursor) {
        _business = business;
        _cursor = cursor;
        Validate();
    }

    internal GraphSpatialState Business => _business;
    internal KernelCursor Cursor => _cursor;

    internal void Validate() {
        ArgumentNullException.ThrowIfNull(_business);
        _cursor.Validate();
        // The externally supplied graph definition belongs to the simulation factory.
        // The adapter rebuilds runtime predictions against Cursor.CurrentModelTime once on opening.
    }
}
