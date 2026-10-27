using System.Collections;
using System.Reflection;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Runtime;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.ResourceCycle;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class CycleStorageShapeTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_disk_graphs_and_catalog_exclude_runtime_predictions_and_routing(bool completeState) {
        using var store = new TemporaryStore();
        CycleWorld initial = CycleWorld.CreatePlayable();
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: new WaitingCharacterController());
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(96)));
        using (var history = CycleDurableHistory.Create(store.Path, initial, InitialCursor(initial))) {
            history.CommitEvent(kernel.LastCompletion!.Event);
            if (completeState) { history.CommitState(kernel.World, kernel.Cursor); }
        }

        // Inspect the stored objects directly, without the adapter's rebuilding step.
        using Repository repository = Repository.OpenReadOnlyExisting(store.Path, Models());
        BranchInfo branch = Assert.Single(repository.ListBranches());
        CommitInfo head = repository.ReadCurrentCommit(branch.Id).Commit;
        CommitHistoryPage ancestors = repository.ReadHistoryPage(
            HistoryStart.Before(head.Id), new HistoryPageLimits(3, long.MaxValue));
        CommitInfo stateCommit = head.Kind == CommitKind.State ? head : ancestors.Items.First(item => item.Kind == CommitKind.State);
        CommitInfo eventCommit = head.Kind == CommitKind.Event ? head : ancestors.Items.First(item => item.Kind == CommitKind.Event);
        var root = Assert.IsType<CycleDurableRoot>(repository.LoadState(stateCommit.Id));
        CycleWorld expected = completeState ? kernel.World : initial;
        Assert.Equal(expected.DescribeState(), CycleEngine.Rebuild(root.Business, root.Cursor.CurrentModelTime).DescribeState());
        Assert.NotSame(expected.Business, root.Business);
        Assert.Equal(CycleGameRevision.Current, root.GameRevision);
        Assert.Equal(new[] { typeof(CycleBusinessState), typeof(KernelCursor), typeof(int) },
            DurableFields(typeof(CycleDurableRoot)).Select(field => field.FieldType));
        var envelope = Assert.IsType<CycleDurableEvent>(repository.LoadEvent(eventCommit.Id));
        Assert.Equal(CycleGameRevision.Current, envelope.GameRevision);
        var occurrence = envelope.Occurrence;
        Assert.Equal(Describe(kernel.LastCompletion!.Event), Describe(occurrence));
        Assert.Equal(CycleEventKind.WaitStarted, Assert.Single(occurrence.Facts).Kind);

        Type[] stateTypes = ReachablePersistedObjects(root).Select(value => value.GetType()).Distinct().ToArray();
        Type[] eventTypes = ReachablePersistedObjects(envelope).Select(value => value.GetType()).Distinct().ToArray();
        Assert.Contains(typeof(CycleBusinessState), stateTypes);
        Assert.Contains(typeof(CycleCharacter), stateTypes);
        Assert.Contains(typeof(CycleFact), eventTypes);
        Assert.Contains(typeof(CycleCharacter), eventTypes);
        var catalog = new DefinitionCatalog();
        CycleDurableModels.Register(catalog);
        Assert.Contains(typeof(CycleBusinessState), catalog.Types);
        Assert.Contains(typeof(CycleDurableRoot), catalog.Types);
        Assert.Contains(typeof(CycleDurableEvent), catalog.Types);
        foreach (Type runtime in new[] { typeof(CycleWorld), typeof(CycleForecasts), typeof(CyclePrediction), typeof(CycleBinding), typeof(CyclePredictionKind) }) {
            Assert.DoesNotContain(runtime, stateTypes);
            Assert.DoesNotContain(runtime, eventTypes);
            Assert.DoesNotContain(runtime, catalog.Types);
            Assert.False(typeof(IDurableObject).IsAssignableFrom(runtime));
            Assert.Null(runtime.GetCustomAttribute<DurableTypeAttribute>());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_or_expired_runtime_predictions_and_routing_are_discarded_on_cold_open(bool stale) {
        using var store = new TemporaryStore();
        CycleWorld initial = CycleWorld.CreatePlayable();
        CyclePrediction[] predictions = stale
            ? [new("resource:sunflower:regen", CyclePredictionKind.RegenerateResource, "sunflower", Hour(0), 999)] : [];
        var obsolete = new CycleWorld(initial.Business, new CycleForecasts(predictions, []));
        using (var created = CycleDurableHistory.Create(store.Path, obsolete, InitialCursor(initial))) {
            Assert.Equal(initial.DescribeState(), created.State.DescribeState());
            Assert.Same(created.State, created.State);
        }
        using var reopened = CycleDurableHistory.Open(store.Path);
        Assert.Equal(initial.DescribeState(), reopened.State.DescribeState());
        Assert.Same(reopened.State, reopened.State);
        Assert.NotEmpty(reopened.State.Forecasts.Bindings);
        Assert.Empty(obsolete.Forecasts.Bindings);
        Assert.Equal(stale ? 1 : 0, obsolete.Forecasts.Predictions.Count);
    }

    [Fact]
    public void Durable_root_rejects_expired_business_before_it_can_be_published() {
        CycleWorld initial = CycleWorld.Create(start: Hour(6), script: []);
        CycleBusinessState invalid = initial.Business.Replace(Hour(10));
        var cursor = new KernelCursor(new WorldVersion(17, 0), Hour(10), null, null);
        // Regeneration was due at hour 9. The stronger business invariant rejects this even before persistence.
        Assert.Throws<InvalidOperationException>(() => new CycleDurableRoot(invalid, cursor));
    }
    private static StateModelRegistry Models() {
        var registry = new StateModelRegistry();
        CycleDurableModels.Register(registry);
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

    private sealed class WaitingCharacterController : ICycleCharacterController {
        public ValueTask<CycleCommand> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(CycleCommand.Wait(6));
    }

    private sealed class TemporaryStore : IDisposable {
        private readonly string _parent = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "DramaBoard.CycleStorage.Tests", Guid.NewGuid().ToString("N"));
        public TemporaryStore() { Directory.CreateDirectory(_parent); }
        public string Path => System.IO.Path.Combine(_parent, "repository");
        public void Dispose() { Directory.Delete(_parent, recursive: true); }
    }
}
