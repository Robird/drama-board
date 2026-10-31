using Atelia.DurableGraph;
using DramaBoard.Goals;
using DramaBoard.Inventory;
using DramaBoard.Kernel;
using DramaBoard.Spatial;

namespace DramaBoard.Microtown;

public static class TownDurableModels {
    public static void Register(IStateModelRegistration models) {
        ArgumentNullException.ThrowIfNull(models);
        KernelDurableModels.Register(models);
        SpatialDurableModels.Register(models);
        InventoryDurableModels.Register(models);
        GoalDurableModels.Register(models);
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
    }
}

/// <summary>Application identity and rules revision; schema revision is an independent representation concern.</summary>
public static class TownGameRevision {
    public const string GameId = "microtown";
    public const int Current = 3;

    internal static void RequireSupported(string gameId, int revision) {
        if (gameId != GameId || revision != Current) {
            throw new InvalidDataException($"Unsupported game/revision '{gameId}'/{revision}; expected '{GameId}'/{Current}.");
        }
    }
}
