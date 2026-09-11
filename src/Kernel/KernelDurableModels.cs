using Atelia.DurableGraph;

namespace DramaBoard.Kernel;

/// <summary>Registers the Kernel's durable definitions with a host-owned registry.</summary>
public static class KernelDurableModels
{
    public static void Register(IStateModelRegistration registration) =>
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(registration);
}
