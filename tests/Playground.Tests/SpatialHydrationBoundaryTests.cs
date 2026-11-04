using System.Reflection;
using System.Security.Cryptography;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using DramaBoard.Spatial;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class SpatialHydrationBoundaryTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cold_open_revalidates_anchored_traversal_structure_before_runtime_use(bool corrupt) {
        using var store = new TemporaryStore();
        var initial = PlaygroundGameFactory.CreateSurvival(Hour(8), enableNeeds: false, enableWeather: false);
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial.World), characterController: Commands(PlaygroundCommand.Go("e-c7")));
        await StepWorldEvent(kernel);
        Assert.Equal(PlaygroundEventKind.MoveStarted, Assert.Single(kernel.LastCompletion!.Event.Facts).Kind);
        var traversal = Assert.IsType<TraversingLocation>(Assert.Single(kernel.World.Expedition!.Spatial.Entities).Location);
        var passage = kernel.World.Definition.Runtime.Graph.GetPassage(traversal.PassageId);
        Assert.Equal(passage.EndpointB, traversal.TargetPlaceId);
        Assert.Equal(0, traversal.AnchorOffset);
        Assert.Equal(1, traversal.SpeedSnapshot);
        if (corrupt) {
            // Isolate the missing offset lower bound: keep length + 1 travel ticks consistent with speed 1.
            typeof(TraversingLocation).GetField("_anchorOffset", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(traversal, -1L);
            typeof(TraversingLocation).GetField("_arrivalDue", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(traversal, new DramaBoard.Kernel.Time.ModelTime(traversal.AnchorTime.Ticks + passage.Length + 1));
        }
        using (Repository repository = Repository.CreateNew(store.StorePath, Models())) {
            repository.CreateRootBranchFromState("spatial-boundary",
                PlaygroundDurableRoot.CreateRevisionCandidate(kernel.World.Business, kernel.Cursor, PlaygroundGameRevision.Current));
        }
        string[] before = HashFiles(store.StorePath);
        using (Repository repository = Repository.OpenReadOnlyExisting(store.StorePath, Models())) {
            BranchInfo branch = Assert.Single(repository.ListBranches());
            var root = Assert.IsType<PlaygroundDurableRoot>(repository.LoadState(repository.ReadCurrentCommit(branch.Id).Commit.Id));
            var restored = Assert.IsType<TraversingLocation>(Assert.Single(root.Business.Expedition!.Spatial.Entities).Location);
            Assert.NotSame(traversal, restored);
            Assert.Equal(corrupt ? -1 : 0, restored.AnchorOffset);
            Assert.Equal(traversal.ArrivalDue, restored.ArrivalDue);
        }

        if (corrupt) {
            Assert.Throws<InvalidOperationException>(() => { using var unexpected = PlaygroundDurableHistory.Open(store.StorePath); });
        } else {
            using var reopened = PlaygroundDurableHistory.Open(store.StorePath);
            reopened.State.Validate();
            Assert.Equal(0, Assert.IsType<TraversingView>(new SpatialQueries(reopened.State.Definition.Runtime.Graph)
                .GetLocation(reopened.State.Expedition!.Spatial, new("a"), reopened.State.Time)).Offset);
        }
        Assert.Equal(before, HashFiles(store.StorePath));
    }

    private static StateModelRegistry Models() {
        var models = new StateModelRegistry();
        PlaygroundDurableModels.Register(models);
        return models;
    }
    private static string[] HashFiles(string path) => Directory.GetFiles(path, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(file => Path.GetRelativePath(path, file) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))).ToArray();
}
