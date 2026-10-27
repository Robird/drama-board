using Atelia.DurableGraph;
using DramaBoard.Kernel;

namespace DramaBoard.Spatial.Tests.TestSupport;

internal static class SpatialTestDurableModels {
    internal static void Register(IStateModelRegistration models) {
        KernelDurableModels.Register(models);
        SpatialDurableModels.Register(models);
        // Each generated assembly exports this conventional name. Here the local definition
        // intentionally registers the test-only root; Spatial's definitions were registered above.
#pragma warning disable CS0436
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
#pragma warning restore CS0436
    }
}
