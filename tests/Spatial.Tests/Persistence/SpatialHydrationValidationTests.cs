using System.Reflection;
using System.Security.Cryptography;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Spatial.Tests.TestSupport;
using static DramaBoard.Spatial.Tests.Persistence.SpatialPredictionPersistenceTests;

namespace DramaBoard.Spatial.Tests.Persistence;

public sealed class SpatialHydrationValidationTests {
    [Theory]
    [InlineData("negative-offset")]
    [InlineData("reversed-contact")]
    [InlineData("self-contact")]
    [InlineData("leading-space")]
    [InlineData("trailing-space")]
    public void Complete_validation_and_rebuild_reject_constructor_invariants_bypassed_by_hydration(string corruption) {
        var (definition, business) = Fixture();
        Corrupt(business, corruption);

        AssertRejected(corruption, () => GraphSpatialStateValidator.ValidateComplete(definition, business));
        AssertRejected(corruption, () => new SpatialSimulation(definition).Rebuild(business, GraphTestWorld.Time(0)));
        AssertRejected(corruption, () => GraphSpatialState.Restore(definition, business.Entities,
            business.PassageEntryAccessOverrides, business.ScheduledPassageEntryChanges, business.ConsumedContacts));
    }

    [Theory]
    [InlineData("negative-offset")]
    [InlineData("reversed-contact")]
    [InlineData("self-contact")]
    [InlineData("leading-space")]
    [InlineData("trailing-space")]
    public void Actual_disk_hydration_preserves_bad_fields_and_adapter_rejects_without_rewriting_them(string corruption) {
        using var store = new TemporaryStore();
        var (definition, business) = Fixture();
        var root = new SpatialDurableRoot(business, new(new WorldVersion(17, 0), GraphTestWorld.Time(0), null, null));
        Corrupt(business, corruption);
        using (Repository repository = Repository.CreateNew(store.Path, Models())) {
            repository.CreateRootBranchFromState("malformed-spatial", root);
        }
        string[] before = HashFiles(store.Path);
        using (Repository repository = Repository.OpenReadOnlyExisting(store.Path, Models())) {
            BranchInfo branch = Assert.Single(repository.ListBranches());
            var hydrated = Assert.IsType<SpatialDurableRoot>(repository.LoadState(repository.ReadCurrentCommit(branch.Id).Commit.Id));
            Assert.NotSame(business, hydrated.Business);
            Assert.Equal(business, hydrated.Business);
            AssertRejected(corruption, () => GraphSpatialStateValidator.ValidateComplete(definition, hydrated.Business));
        }

        AssertRejected(corruption, () => { using var unexpected = SpatialDurableHistory.Open(store.Path, new(definition)); });
        Assert.Equal(before, HashFiles(store.Path));
    }

    [Fact]
    public void Valid_cold_open_keeps_negative_anchor_time_endpoint_offsets_internal_id_spaces_and_consumption() {
        using var store = new TemporaryStore();
        var (definition, business) = Fixture("ali ce");
        // Reverse constructor arguments are canonicalized as whole (entity, generation) pairs.
        PassageContactKey consumed = Assert.Single(business.ConsumedContacts);
        Assert.Equal(new EntityId("ali ce"), consumed.EntityA);
        Assert.Equal(1, consumed.MovementGenerationA);
        Assert.Equal(new EntityId("bob"), consumed.EntityB);
        Assert.Equal(2, consumed.MovementGenerationB);
        using (Repository repository = Repository.CreateNew(store.Path, Models())) {
            repository.CreateRootBranchFromState("valid-spatial", new SpatialDurableRoot(business,
                new(new WorldVersion(17, 0), GraphTestWorld.Time(0), null, null)));
        }
        string[] before = HashFiles(store.Path);
        using (var reopened = SpatialDurableHistory.Open(store.Path, new(definition))) {
            Assert.Equal(business, reopened.State.Business);
            Assert.NotSame(business, reopened.State.Business);
            Assert.Empty(reopened.State.Contacts.Entries);
            var queries = new SpatialQueries(definition);
            Assert.All(reopened.State.Business.Entities, entity => {
                var location = Assert.IsType<TraversingLocation>(entity.Location);
                Assert.Equal(GraphTestWorld.Time(-5), location.AnchorTime);
                Assert.Equal(5, Assert.IsType<TraversingView>(queries.GetLocation(reopened.State.Business, entity.Id, GraphTestWorld.Time(0))).Offset);
            });
            Assert.Equal(0, Assert.IsType<TraversingLocation>(reopened.State.Business.Entities.Single(entity => entity.Id == new EntityId("ali ce")).Location).AnchorOffset);
            Assert.Equal(10, Assert.IsType<TraversingLocation>(reopened.State.Business.Entities.Single(entity => entity.Id == new EntityId("bob")).Location).AnchorOffset);
        }
        Assert.Equal(before, HashFiles(store.Path));
    }

    private static (GraphDefinition Definition, GraphSpatialState Business) Fixture(string firstId = "alice") {
        var definition = GraphDefinition.Create([GraphTestWorld.A, GraphTestWorld.B],
            [GraphTestWorld.Passage(GraphTestWorld.Bridge, GraphTestWorld.A, GraphTestWorld.B, length: 10)]);
        var business = GraphSpatialState.Restore(definition, [
            new SpatialEntity(new(firstId), 1, new TraversingLocation(GraphTestWorld.Bridge, 0, GraphTestWorld.Time(-5), GraphTestWorld.B, 1, GraphTestWorld.Time(5))),
            new SpatialEntity(new("bob"), 2, new TraversingLocation(GraphTestWorld.Bridge, 10, GraphTestWorld.Time(-5), GraphTestWorld.A, 1, GraphTestWorld.Time(5)))
        ], [], [], [new PassageContactKey(GraphTestWorld.Bridge, new("bob"), 2, new(firstId), 1)]);
        return (definition, business);
    }

    private static void Corrupt(GraphSpatialState business, string corruption) {
        PassageContactKey contact = Assert.Single(business.ConsumedContacts);
        switch (corruption) {
            case "negative-offset":
                var traversal = Assert.IsType<TraversingLocation>(business.Entities[0].Location);
                SetField(traversal, "_anchorOffset", -1L);
                // Keep timing self-consistent: old arrival validation must not mask the missing lower bound.
                SetField(traversal, "_arrivalDue", GraphTestWorld.Time(6));
                break;
            case "reversed-contact":
                EntityId first = contact.EntityA;
                long generation = contact.MovementGenerationA;
                SetField(contact, "_entityA", contact.EntityB);
                SetField(contact, "_movementGenerationA", contact.MovementGenerationB);
                SetField(contact, "_entityB", first);
                SetField(contact, "_movementGenerationB", generation);
                break;
            case "self-contact":
                SetField(contact, "_entityB", contact.EntityA);
                SetField(contact, "_movementGenerationB", contact.MovementGenerationA);
                break;
            case "leading-space":
            case "trailing-space":
                object boxedId = new EntityId("alice");
                SetField(boxedId, "_value", corruption == "leading-space" ? " alice" : "alice ");
                SetField(business.Entities[0], "_id", (EntityId)boxedId);
                SetField(contact, "_entityA", (EntityId)boxedId);
                break;
            default: throw new ArgumentException(corruption);
        }
    }

    private static void AssertRejected(string corruption, Action operation) {
        if (corruption is "leading-space" or "trailing-space") { Assert.Throws<ArgumentException>(operation); }
        else { Assert.Throws<InvalidOperationException>(operation); }
    }

    private static void SetField(object target, string field, object value) =>
        target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static StateModelRegistry Models() {
        var models = new StateModelRegistry();
        SpatialTestDurableModels.Register(models);
        return models;
    }
    private static string[] HashFiles(string path) => Directory.GetFiles(path, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(file => Path.GetRelativePath(path, file) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))).ToArray();
}
