using System.Reflection;
using System.Security.Cryptography;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class HillsideContentBindingTests {
    [Fact]
    public void Saved_definition_fingerprint_is_recomputed_before_rebuild_without_rewriting_disk() {
        using var store = new TemporaryStore();
        var initial = PlaygroundGameFactory.CreateLivingWorld(characterCount: 1);
        CorruptFingerprint(initial.World.Definition);
        using (var repository = Repository.CreateNew(store.StorePath, Models())) {
            repository.CreateRootBranchFromState("corrupt-content",
                PlaygroundDurableRoot.CreateRevisionCandidate(initial.World.Business, initial.Cursor, PlaygroundGameRevision.Current));
        }
        string[] before = HashFiles(store.StorePath);
        Assert.Throws<InvalidDataException>(() => PlaygroundDurableHistory.Open(store.StorePath));
        Assert.Equal(before, HashFiles(store.StorePath));
    }

    [Fact]
    public async Task State_publication_cannot_replace_content_even_with_an_individually_valid_world() {
        using var store = new TemporaryStore();
        var initial = PlaygroundGameFactory.CreateLivingWorld(characterCount: 1);
        var memory = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor));
        Assert.Equal(StepStatus.Committed, await memory.StepAsync(Hour(9)));
        using (var history = PlaygroundDurableHistory.Create(store.StorePath, initial.World, initial.Cursor)) {
            history.CommitEvent(memory.LastCompletion!.Event);
        }
        string[] before = HashFiles(store.StorePath);
        using (var history = PlaygroundDurableHistory.Open(store.StorePath)) {
            PlaygroundWorld different = PlaygroundGameFactory.CreateLivingWorld(characterCount: 2).World;
            different.Validate();
            Assert.Throws<InvalidDataException>(() => history.CommitState(different, memory.Cursor));
            Assert.True(history.IsFaulted);
        }
        Assert.Equal(before, HashFiles(store.StorePath));
        using var reopened = PlaygroundDurableHistory.Open(store.StorePath);
        Assert.Equal(initial.World.Definition.Fingerprint, reopened.State.Definition.Fingerprint);
        Assert.NotNull(reopened.PendingEvent);
        Assert.True(PlaygroundEngine.CreateKernel(reopened).RecoverPending());
        Assert.Single(reopened.State.Characters);
    }

    [Theory]
    [InlineData("event")]
    [InlineData("previous")]
    [InlineData("previous-fingerprint")]
    [InlineData("selected")]
    public async Task Fork_checks_content_of_the_entire_immediate_S_E_S_boundary_before_registration(string corruption) {
        using var store = new TemporaryStore();
        var initial = PlaygroundGameFactory.CreateLivingWorld(characterCount: 1);
        var other = PlaygroundGameFactory.CreateLivingWorld(characterCount: 2);
        var memory = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor));
        Assert.Equal(StepStatus.Committed, await memory.StepAsync(Hour(9)));
        var otherMemory = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(other.World, other.Cursor));
        Assert.Equal(StepStatus.Committed, await otherMemory.StepAsync(Hour(9)));
        PlaygroundWorld previous = corruption == "previous" ? other.World : corruption == "previous-fingerprint"
            ? PlaygroundGameFactory.CreateLivingWorld(characterCount: 1).World : initial.World;
        PlaygroundWorld selected = corruption == "selected" ? otherMemory.World : memory.World;
        var occurrence = new PlaygroundDurableEvent(memory.LastCompletion!.Event,
            corruption == "event" ? other.World.Definition : initial.World.Definition);
        // The saved snapshots must have separate definition instances for this corruption probe.
        string branch, state;
        using (var repository = Repository.CreateNew(store.StorePath, Models())) {
            if (corruption == "previous-fingerprint") { CorruptFingerprint(previous.Definition); }
            BranchInfo created = repository.CreateRootBranchFromState("boundary",
                PlaygroundDurableRoot.CreateRevisionCandidate(previous.Business, initial.Cursor, PlaygroundGameRevision.Current));
            branch = PlaygroundDurableHistory.FormatBranchId(created.Id);
            using WorkingCopy work = repository.OpenWorkingCopy(created.Id);
            work.CommitEvent(occurrence);
            state = PlaygroundDurableHistory.FormatCommitId(work.CommitState(new PlaygroundDurableRoot(selected.Business, memory.Cursor)).Id);
        }
        using (var repository = Repository.OpenReadOnlyExisting(store.StorePath, Models())) {
            Assert.IsType<PlaygroundDurableRoot>(repository.LoadState(PlaygroundDurableHistory.ParseCommitId(state))).Validate();
        }
        string[] before = HashFiles(store.StorePath);
        Assert.Throws<InvalidDataException>(() => PlaygroundDurableHistory.Fork(store.StorePath, branch, state, "a", "must-not-register"));
        Assert.Equal(before, HashFiles(store.StorePath));
        Assert.Single(PlaygroundDurableHistory.ListBranches(store.StorePath));
    }

    private static void CorruptFingerprint(PlaygroundWorldDefinition definition) =>
        typeof(PlaygroundWorldDefinition).GetField("_fingerprint", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(definition, new string('0', 64));

    private static StateModelRegistry Models() {
        var models = new StateModelRegistry();
        PlaygroundDurableModels.Register(models);
        return models;
    }

    private static string[] HashFiles(string path) => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(file => $"{Path.GetRelativePath(path, file)}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))}").ToArray();
}
