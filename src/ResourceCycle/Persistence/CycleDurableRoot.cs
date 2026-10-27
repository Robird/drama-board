using Atelia.DurableGraph;
using DramaBoard.Kernel.Simulation;

namespace DramaBoard.ResourceCycle;

/// <summary>Business subgraphs and their completed Kernel boundary; predictions are rebuilt after loading.</summary>
[DurableType("DramaBoard.ResourceCycle.Root", 1)]
public sealed partial class CycleDurableRoot : IDurableObject {
    [DurableField(1)] private readonly CycleBusinessState _business;
    [DurableField(2)] private readonly KernelCursor _cursor;

    public CycleDurableRoot(CycleBusinessState business, KernelCursor cursor) {
        _business = business;
        _cursor = cursor;
        Validate();
    }

    public CycleBusinessState Business => _business;
    public KernelCursor Cursor => _cursor;

    /// <summary>Also validates restored objects, whose constructors were not executed.</summary>
    public void Validate() {
        ArgumentNullException.ThrowIfNull(_business);
        _business.Validate();
        _cursor.Validate();
        if (_business.Time != _cursor.CurrentModelTime) {
            throw new InvalidDataException("The world time must equal its completed Kernel cursor time.");
        }
    }
}
