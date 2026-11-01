using System.Reflection;
using System.Security.Cryptography;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class HillsideContentBindingTests {
    [Fact]
    public void Saved_definition_fingerprint_is_recomputed_before_rebuild_without_rewriting_disk() {
        using var store = new TemporaryStore();
        var initial = CycleGameFactory.CreateLivingWorld(characterCount: 1);
        CorruptFingerprint(initial.World.Definition);
        using (var repository = Repository.CreateNew(store.StorePath, Models())) {
            repository.CreateRootBranchFromState("corrupt-content",
                CycleDurableRoot.CreateRevisionCandidate(initial.World.Business, initial.Cursor, CycleGameRevision.Current));
        }
        string[] before = HashFiles(store.StorePath);
        Assert.Throws<InvalidDataException>(() => CycleDurableHistory.Open(store.StorePath));
        Assert.Equal(before, HashFiles(store.StorePath));
    }

    [Fact]
    public async Task State_publication_cannot_replace_content_even_with_an_individually_valid_world() {
        using var store = new TemporaryStore();
        var initial = CycleGameFactory.CreateLivingWorld(characterCount: 1);
        var memory = CycleEngine.CreateKernel(new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor));
        Assert.Equal(StepStatus.Committed, await memory.StepAsync(Hour(9)));
        using (var history = CycleDurableHistory.Create(store.StorePath, initial.World, initial.Cursor)) {
            history.CommitEvent(memory.LastCompletion!.Event);
        }
        string[] before = HashFiles(store.StorePath);
        using (var history = CycleDurableHistory.Open(store.StorePath)) {
            CycleWorld different = CycleGameFactory.CreateLivingWorld(characterCount: 2).World;
            different.Validate();
            Assert.Throws<InvalidDataException>(() => history.CommitState(different, memory.Cursor));
            Assert.True(history.IsFaulted);
        }
        Assert.Equal(before, HashFiles(store.StorePath));
        using var reopened = CycleDurableHistory.Open(store.StorePath);
        Assert.Equal(initial.World.Definition.Fingerprint, reopened.State.Definition.Fingerprint);
        Assert.NotNull(reopened.PendingEvent);
        Assert.True(CycleEngine.CreateKernel(reopened).RecoverPending());
        Assert.Single(reopened.State.Characters);
    }

    [Theory]
    [InlineData("event")]
    [InlineData("previous")]
    [InlineData("previous-fingerprint")]
    [InlineData("selected")]
    public async Task Fork_checks_content_of_the_entire_immediate_S_E_S_boundary_before_registration(string corruption) {
        using var store = new TemporaryStore();
        var initial = CycleGameFactory.CreateLivingWorld(characterCount: 1);
        var other = CycleGameFactory.CreateLivingWorld(characterCount: 2);
        var memory = CycleEngine.CreateKernel(new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor));
        Assert.Equal(StepStatus.Committed, await memory.StepAsync(Hour(9)));
        var otherMemory = CycleEngine.CreateKernel(new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(other.World, other.Cursor));
        Assert.Equal(StepStatus.Committed, await otherMemory.StepAsync(Hour(9)));
        CycleWorld previous = corruption == "previous" ? other.World : corruption == "previous-fingerprint"
            ? CycleGameFactory.CreateLivingWorld(characterCount: 1).World : initial.World;
        CycleWorld selected = corruption == "selected" ? otherMemory.World : memory.World;
        var occurrence = new CycleDurableEvent(memory.LastCompletion!.Event,
            corruption == "event" ? other.World.Definition : initial.World.Definition);
        // The saved snapshots must have separate definition instances for this corruption probe.
        string branch, state;
        using (var repository = Repository.CreateNew(store.StorePath, Models())) {
            if (corruption == "previous-fingerprint") { CorruptFingerprint(previous.Definition); }
            BranchInfo created = repository.CreateRootBranchFromState("boundary",
                CycleDurableRoot.CreateRevisionCandidate(previous.Business, initial.Cursor, CycleGameRevision.Current));
            branch = CycleDurableHistory.FormatBranchId(created.Id);
            using WorkingCopy work = repository.OpenWorkingCopy(created.Id);
            work.CommitEvent(occurrence);
            state = CycleDurableHistory.FormatCommitId(work.CommitState(new CycleDurableRoot(selected.Business, memory.Cursor)).Id);
        }
        using (var repository = Repository.OpenReadOnlyExisting(store.StorePath, Models())) {
            Assert.IsType<CycleDurableRoot>(repository.LoadState(CycleDurableHistory.ParseCommitId(state))).Validate();
        }
        string[] before = HashFiles(store.StorePath);
        Assert.Throws<InvalidDataException>(() => CycleDurableHistory.Fork(store.StorePath, branch, state, "a", "must-not-register"));
        Assert.Equal(before, HashFiles(store.StorePath));
        Assert.Single(CycleDurableHistory.ListBranches(store.StorePath));
    }

    private static void CorruptFingerprint(CycleWorldDefinition definition) =>
        typeof(CycleWorldDefinition).GetField("_fingerprint", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(definition, new string('0', 64));

    private static StateModelRegistry Models() {
        var models = new StateModelRegistry();
        CycleDurableModels.Register(models);
        return models;
    }

    private static string[] HashFiles(string path) => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(file => $"{Path.GetRelativePath(path, file)}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))}").ToArray();
}
