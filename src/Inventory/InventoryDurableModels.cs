using Atelia.DurableGraph;

namespace DramaBoard.Inventory;

/// <summary>Registers inventory definitions with the registry owned by the composing application.</summary>
public static class InventoryDurableModels {
    public static void Register(IStateModelRegistration models) {
        ArgumentNullException.ThrowIfNull(models);
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
    }
}
