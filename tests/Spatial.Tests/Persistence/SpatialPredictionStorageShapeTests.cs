using System.Collections;
using System.Reflection;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Runtime;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial.Tests.TestSupport;
using static DramaBoard.Spatial.Tests.Persistence.SpatialPredictionPersistenceTests;

namespace DramaBoard.Spatial.Tests.Persistence;

public sealed class SpatialPredictionStorageShapeTests {
    [Fact]
    public async Task Actual_disk_state_and_event_contain_only_business_and_cursor_not_prediction_models() {
        using var store = new TemporaryStore();
        var (simulation, initial) = Fixture();
        var memory = new InMemoryOccurrenceHistory<SpatialSimulationState, GraphSpatialFact>(initial, Cursor());
        var kernel = simulation.CreateKernel(new SimulationRules(71, 100), memory);
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(GraphTestWorld.Time(20)));
        using (var history = SpatialDurableHistory.Create(store.Path, initial, Cursor(), simulation)) {
            history.CommitEvent(kernel.LastCompletion!.Event);
        }

        // Load the actual persisted S and E directly, bypassing the adapter's runtime rebuilding.
        using Repository repository = Repository.OpenReadOnlyExisting(store.Path, Models());
        BranchInfo branch = Assert.Single(repository.ListBranches());
        CommitInfo head = repository.ReadCurrentCommit(branch.Id).Commit;
        Assert.Equal(CommitKind.Event, head.Kind);
        CommitInfo state = Assert.Single(repository.ReadHistoryPage(
            HistoryStart.Before(head.Id), new HistoryPageLimits(1, long.MaxValue)).Items);
        var root = Assert.IsType<SpatialDurableRoot>(repository.LoadState(state.Id));
        Assert.Equal(initial.Business, root.Business);
        Assert.NotSame(initial.Business, root.Business);
        Assert.Equal(Cursor(), root.Cursor);
        FieldInfo[] fields = DurableFields(typeof(SpatialDurableRoot));
        Assert.Equal(new[] { typeof(GraphSpatialState), typeof(KernelCursor) }, fields.Select(field => field.FieldType));

        var occurrence = Assert.IsType<OccurrenceEvent<GraphSpatialFact>>(repository.LoadEvent(head.Id));
        Assert.Equal(kernel.LastCompletion!.Event.Facts, occurrence.Facts);
        Assert.IsType<PassageContactOccurredFact>(Assert.Single(occurrence.Facts));
        Type[] stateTypes = ReachablePersistedObjects(root).Select(value => value.GetType()).Distinct().ToArray();
        Type[] eventTypes = ReachablePersistedObjects(occurrence).Select(value => value.GetType()).Distinct().ToArray();
        Assert.Contains(typeof(GraphSpatialState), stateTypes);
        Assert.Contains(typeof(PassageContactOccurredFact), eventTypes);
        foreach (Type runtime in new[] { typeof(SpatialSimulationState), typeof(SpatialContactPredictions), typeof(ContactPrediction) }) {
            Assert.DoesNotContain(runtime, stateTypes);
            Assert.DoesNotContain(runtime, eventTypes);
            Assert.False(typeof(IDurableObject).IsAssignableFrom(runtime));
            Assert.Null(runtime.GetCustomAttribute<DurableTypeAttribute>());
        }

        var catalog = new DefinitionCatalog();
        SpatialTestDurableModels.Register(catalog);
        Assert.Contains(typeof(GraphSpatialState), catalog.Types);
        Assert.Contains(typeof(SpatialDurableRoot), catalog.Types);
        Assert.DoesNotContain(typeof(SpatialSimulationState), catalog.Types);
        Assert.DoesNotContain(typeof(SpatialContactPredictions), catalog.Types);
        Assert.DoesNotContain(typeof(ContactPrediction), catalog.Types);
        Assert.DoesNotContain(catalog.Types, type => type.Name is "SpatialSimulationFact" or "SpatialBusinessFact" or "ContactPredictionsReplaced");
    }

    [Fact]
    public void Cold_open_rejects_business_cursor_boundary_with_expired_recomputed_contact() {
        using var store = new TemporaryStore();
        var (simulation, initial) = Fixture();
        var invalidCursor = new KernelCursor(new WorldVersion(17, 0), GraphTestWorld.Time(6), null, null);
        using (Repository repository = Repository.CreateNew(store.Path, Models())) {
            repository.CreateRootBranchFromState("invalid-boundary", new SpatialDurableRoot(initial.Business, invalidCursor));
        }

        Assert.Throws<InvalidOperationException>(() => SpatialDurableHistory.Open(store.Path, simulation));
    }

    [Fact]
    public void Missing_in_memory_predictions_are_not_saved_and_cold_open_uses_current_full_rebuild() {
        using var store = new TemporaryStore();
        var (simulation, initial) = Fixture();
        Assert.Single(initial.Contacts.Entries);
        var incomplete = new SpatialSimulationState(initial.Business, new SpatialContactPredictions([]));
        // A former incomplete calculation does not become durable authority. Business is unchanged.
        using (var created = SpatialDurableHistory.Create(store.Path, incomplete, Cursor(), simulation)) {
            EqualWorld(initial, created.State);
        }
        using var reopened = SpatialDurableHistory.Open(store.Path, simulation);
        EqualWorld(initial, reopened.State);
        Assert.Single(reopened.State.Contacts.Entries);
        Assert.Same(reopened.State, reopened.State);
        Assert.Empty(incomplete.Contacts.Entries);
    }

    private static StateModelRegistry Models() {
        var registry = new StateModelRegistry();
        SpatialTestDurableModels.Register(registry);
        return registry;
    }

    private static FieldInfo[] DurableFields(Type type) {
        var fields = new List<FieldInfo>();
        for (Type? current = type; current is not null; current = current.BaseType) {
            fields.AddRange(current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(field => field.IsDefined(typeof(DurableFieldAttribute))));
        }
        return fields.OrderBy(field => field.GetCustomAttribute<DurableFieldAttribute>()!.FieldId).ToArray();
    }

    private static IEnumerable<object> ReachablePersistedObjects(object root) {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<object>();
        pending.Push(root);
        while (pending.TryPop(out object? item)) {
            if (!seen.Add(item)) { continue; }
            yield return item;
            if (item is string) { continue; }
            if (item is IEnumerable sequence) {
                foreach (object? value in sequence) {
                    if (value is not null) { pending.Push(value); }
                }
            } else {
                foreach (FieldInfo field in DurableFields(item.GetType())) {
                    if (field.GetValue(item) is { } value) { pending.Push(value); }
                }
            }
        }
    }

    private sealed class DefinitionCatalog : IStateModelRegistration {
        public HashSet<Type> Types { get; } = [];
        public void Register(StateModelBinding model) => Types.Add(model.DomainType);
        public void Register(StateDefinitionBinding definition) {
            if (definition.DomainTypeDefinition is { } type) { Types.Add(type); }
        }
    }
}
