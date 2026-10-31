using Atelia.DurableGraph;

namespace DramaBoard.Goals;

/// <summary>Registers the goal graph definitions in the registry owned by the composing game.</summary>
public static class GoalDurableModels {
    public static void Register(IStateModelRegistration models) {
        ArgumentNullException.ThrowIfNull(models);
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
    }
}
