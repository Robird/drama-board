using Atelia.DurableGraph;

namespace DramaBoard.Spatial;

/// <summary>Registers the durable model families owned by Spatial.</summary>
public static class SpatialDurableModels
{
    public static void Register(IStateModelRegistration models)
    {
        ArgumentNullException.ThrowIfNull(models);
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
    }
}
