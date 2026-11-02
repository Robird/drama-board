using System.Collections;
using System.Reflection;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Runtime;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Playground;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class PlaygroundStorageShapeTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_disk_graphs_and_catalog_exclude_runtime_predictions_and_routing(bool completeState) {
        using var store = new TemporaryStore();
        PlaygroundWorld initial = PlaygroundWorld.CreatePlayable();
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: new WaitingCharacterController());
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(96)));
        initial = kernel.World;
        KernelCursor openedCursor = kernel.Cursor;
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(96)));
        using (var history = PlaygroundDurableHistory.Create(store.Path, initial, openedCursor)) {
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
        var root = Assert.IsType<PlaygroundDurableRoot>(repository.LoadState(stateCommit.Id));
        PlaygroundWorld expected = completeState ? kernel.World : initial;
        Assert.Equal(expected.DescribeState(), PlaygroundEngine.Rebuild(root.Business, root.Cursor.CurrentModelTime).DescribeState());
        Assert.NotSame(expected.Business, root.Business);
        Assert.Equal(PlaygroundGameRevision.Current, root.GameRevision);
        Assert.Equal(new[] { typeof(PlaygroundBusinessState), typeof(KernelCursor), typeof(int) },
            DurableFields(typeof(PlaygroundDurableRoot)).Select(field => field.FieldType));
        var envelope = Assert.IsType<PlaygroundDurableEvent>(repository.LoadEvent(eventCommit.Id));
        Assert.Equal(PlaygroundGameRevision.Current, envelope.GameRevision);
        var occurrence = envelope.Occurrence;
        Assert.Equal(Describe(kernel.LastCompletion!.Event), Describe(occurrence));
        Assert.Equal(PlaygroundEventKind.WaitStarted, Assert.Single(occurrence.Facts).Kind);

        Type[] stateTypes = ReachablePersistedObjects(root).Select(value => value.GetType()).Distinct().ToArray();
        Type[] eventTypes = ReachablePersistedObjects(envelope).Select(value => value.GetType()).Distinct().ToArray();
        Assert.Contains(typeof(PlaygroundBusinessState), stateTypes);
        Assert.Contains(typeof(PlaygroundCharacter), stateTypes);
        Assert.Contains(typeof(PlaygroundFact), eventTypes);
        Assert.Contains(typeof(PlaygroundCharacter), eventTypes);
        Assert.Contains(typeof(CharacterContext), stateTypes);
        Assert.Contains(typeof(CharacterContext), eventTypes);
        var catalog = new DefinitionCatalog();
        PlaygroundDurableModels.Register(catalog);
        Assert.Contains(typeof(PlaygroundBusinessState), catalog.Types);
        Assert.Contains(typeof(PlaygroundDurableRoot), catalog.Types);
        Assert.Contains(typeof(PlaygroundDurableEvent), catalog.Types);
        foreach (Type runtime in new[] { typeof(PlaygroundWorld), typeof(PlaygroundForecasts), typeof(PlaygroundPrediction), typeof(PlaygroundBinding), typeof(PlaygroundPredictionKind) }) {
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
        PlaygroundWorld initial = PlaygroundWorld.CreatePlayable();
        PlaygroundPrediction[] predictions = stale
            ? [new("resource:sunflower:regen", PlaygroundPredictionKind.RegenerateResource, "sunflower", Hour(0), 999)] : [];
        var obsolete = new PlaygroundWorld(initial.Business, new PlaygroundForecasts(predictions, []));
        using (var created = PlaygroundDurableHistory.Create(store.Path, obsolete, InitialCursor(initial))) {
            Assert.Equal(initial.DescribeState(), created.State.DescribeState());
            Assert.Same(created.State, created.State);
        }
        using var reopened = PlaygroundDurableHistory.Open(store.Path);
        Assert.Equal(initial.DescribeState(), reopened.State.DescribeState());
        Assert.Same(reopened.State, reopened.State);
        Assert.NotEmpty(reopened.State.Forecasts.Bindings);
        Assert.Empty(obsolete.Forecasts.Bindings);
        Assert.Equal(stale ? 1 : 0, obsolete.Forecasts.Predictions.Count);
    }

    [Fact]
    public void Durable_root_rejects_expired_business_before_it_can_be_published() {
        PlaygroundWorld initial = PlaygroundWorld.Create(start: Hour(6), script: []);
        PlaygroundBusinessState invalid = initial.Business.Replace(Hour(10));
        var cursor = new KernelCursor(new WorldVersion(17, 0), Hour(10), null, null);
        // Regeneration was due at hour 9. The stronger business invariant rejects this even before persistence.
        Assert.Throws<InvalidOperationException>(() => new PlaygroundDurableRoot(invalid, cursor));
    }
    private static StateModelRegistry Models() {
        var registry = new StateModelRegistry();
        PlaygroundDurableModels.Register(registry);
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
            if (item is IDictionary dictionary) {
                foreach (object? value in dictionary.Values) { if (value is not null) { pending.Push(value); } }
            } else if (item is IEnumerable sequence) {
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

    private sealed class WaitingCharacterController : IPlaygroundCharacterController {
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Operation(PlaygroundCommand.Wait(6)));
    }

    private sealed class TemporaryStore : IDisposable {
        private readonly string _parent = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "DramaBoard.PlaygroundStorage.Tests", Guid.NewGuid().ToString("N"));
        public TemporaryStore() { Directory.CreateDirectory(_parent); }
        public string Path => System.IO.Path.Combine(_parent, "repository");
        public void Dispose() { Directory.Delete(_parent, recursive: true); }
    }
}
