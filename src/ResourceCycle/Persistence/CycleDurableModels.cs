using Atelia.DurableGraph;
using DramaBoard.Kernel;

namespace DramaBoard.ResourceCycle;

/// <summary>Registers the complete ResourceCycle graph, including Kernel values and occurrence events.</summary>
public static class CycleDurableModels {
    public static void Register(IStateModelRegistration models) {
        ArgumentNullException.ThrowIfNull(models);
        KernelDurableModels.Register(models);
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
    }
}
