using Atelia.DurableGraph;
using DramaBoard.Goals;
using DramaBoard.Inventory;
using DramaBoard.Kernel;
using DramaBoard.Spatial;

namespace DramaBoard.Playground;

/// <summary>Registers the complete Playground graph, including inventory, Kernel values and occurrence events.</summary>
public static class PlaygroundDurableModels {
    public static void Register(IStateModelRegistration models) {
        ArgumentNullException.ThrowIfNull(models);
        KernelDurableModels.Register(models);
        InventoryDurableModels.Register(models);
        GoalDurableModels.Register(models);
        SpatialDurableModels.Register(models);
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
    }
}
