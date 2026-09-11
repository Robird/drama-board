using Atelia.DurableGraph;

namespace DramaBoard.FirstBoard;

/// <summary>Registers the durable FirstBoard model family owned by this assembly.</summary>
public static class FirstBoardDurableModels
{
    public static void Register(IStateModelRegistration models)
    {
        ArgumentNullException.ThrowIfNull(models);
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
    }
}
