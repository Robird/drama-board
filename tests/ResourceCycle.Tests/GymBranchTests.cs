using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class GymBranchTests {
    [Fact]
    public async Task Exact_old_decision_state_forks_and_cold_runs_without_parent_future_or_random_changes() {
        using var store = new TemporaryStore();
        var initial = CycleGameFactory.CreateSurvival(Hour(8), enableWeather: true);
        CycleDecisionBoundary source;
        string parentHead, prefixMaterial, expectedWeather;
        using (var history = CycleDurableHistory.Create(store.StorePath, initial.World, initial.Cursor)) {
            var controller = new RecordingController(
                ("prefix", """create-goal {"title":"collect supplies","criteria":"two water","focus":true}"""),
                ("parent-future", "wait 1"));
            var kernel = CycleEngine.CreateKernel(history, characterController: controller);
            await Advance(kernel); // Open Context; no controller call.
            await Advance(kernel); // The inherited operation.
            source = history.CaptureDecisionBoundary("a");
            prefixMaterial = MaterialSnapshot(history.State.Business);
            expectedWeather = MaterialSnapshot(CycleEnvironmentRules.Plan(history.State, history.State.Expedition!.Environment!.NextDue!.Value).Environment);
            Assert.Equal(source.StateCommitId, CycleDurableHistory.FormatCommitId(history.CompletedStateCommitId));
            await Advance(kernel);
            Assert.Equal(2, controller.Calls);
            parentHead = CycleDurableHistory.FormatCommitId(history.HeadCommitId);
            Assert.NotEqual(source.StateCommitId, parentHead);
        }

        Dictionary<string, string> beforeRead = HashFiles(store.StorePath);
        Assert.Equal(source, CycleDurableHistory.InspectDecisionBoundary(store.StorePath, source.BranchId, source.StateCommitId, "a"));
        Assert.Single(CycleDurableHistory.ListBranches(store.StorePath));
        Assert.Equal(beforeRead, HashFiles(store.StorePath));
        CycleBranchForkResult fork = CycleDurableHistory.Fork(store.StorePath, source.BranchId, source.StateCommitId, "a", "alternate");
        Assert.Equal(source, fork.Source);
        Assert.Equal(source.StateCommitId, fork.Branch.HeadCommitId);
        Assert.Equal(source.StateCommitId, fork.Branch.ForkedFromCommitId);
        Assert.Throws<InvalidDataException>(() => CycleDurableHistory.Open(store.StorePath));
        string childHead;
        using (var child = CycleDurableHistory.Open(store.StorePath, fork.Branch.BranchId)) {
            Assert.Null(child.PendingEvent);
            Assert.Equal(prefixMaterial, MaterialSnapshot(child.State.Business));
            Assert.Equal(expectedWeather, MaterialSnapshot(CycleEnvironmentRules.Plan(child.State, child.State.Expedition!.Environment!.NextDue!.Value).Environment));
            Assert.Equal(source.LineageId, child.Cursor.Version.LineageId);
            Assert.Equal(source.TransitionCount, child.Cursor.Version.TransitionCount);
            Assert.Equal(source with { BranchId = fork.Branch.BranchId }, child.CaptureDecisionBoundary("a"));
            Assert.Single(child.ReadCommittedOperations(["gym/prefix"]));
            Assert.Empty(child.ReadCommittedOperations(["gym/parent-future"]));
            var controller = new RecordingController(("child-new", "handoff child continuation"));
            var kernel = CycleEngine.CreateKernel(child, characterController: controller);
            Assert.False(kernel.RecoverPending());
            await Advance(kernel);
            Assert.Equal(1, controller.Calls);
            Assert.Single(child.ReadCommittedOperations(["gym/child-new"]));
            childHead = CycleDurableHistory.FormatCommitId(child.HeadCommitId);
        }
        using (var child = CycleDurableHistory.Open(store.StorePath, CycleDurableHistory.ParseBranchId(fork.Branch.BranchId))) {
            Assert.Equal(childHead, CycleDurableHistory.FormatCommitId(child.HeadCommitId));
            Assert.Equal("child continuation", child.State.Business.InteractionStates["a"].DraftHandoff);
        }
        using (var parent = CycleDurableHistory.Open(store.StorePath, source.BranchId)) {
            Assert.Equal(parentHead, CycleDurableHistory.FormatCommitId(parent.HeadCommitId));
            Assert.Single(parent.ReadCommittedOperations(["gym/parent-future"]));
            Assert.Empty(parent.ReadCommittedOperations(["gym/child-new"]));
        }
        IReadOnlyList<CycleHistoryEntry> childHistory = CycleDurableHistory.ReadBranchHistory(store.StorePath, fork.Branch.BranchId);
        Assert.Contains(childHistory, entry => entry.CommitId == source.StateCommitId && entry.CreatedOnBranchId == source.BranchId);
        Assert.DoesNotContain(childHistory, entry => entry.CommitId == parentHead);
        Assert.Throws<InvalidDataException>(() => CycleDurableHistory.Fork(store.StorePath, fork.Branch.BranchId, parentHead, "a", "wrong ancestor"));
        Assert.Equal(2, CycleDurableHistory.ListBranches(store.StorePath).Count);
    }

    [Fact]
    public async Task Boundary_requires_open_context_for_actual_next_actor_in_duo() {
        using var store = new TemporaryStore();
        var initial = CycleGameFactory.CreateSurvival(Hour(8), characterCount: 2, enableWeather: false);
        CycleDecisionBoundary selected;
        using (var history = CycleDurableHistory.Create(store.StorePath, initial.World, initial.Cursor)) {
            Assert.Throws<InvalidOperationException>(() => history.CaptureDecisionBoundary("a"));
            var kernel = CycleEngine.CreateKernel(history, characterController: new RecordingController(("a-handoff", "handoff plan")));
            await Advance(kernel);
            Assert.Equal("a", history.CaptureDecisionBoundary("a").ActorId);
            Assert.Throws<InvalidOperationException>(() => history.CaptureDecisionBoundary("b"));
            await Advance(kernel); // a instant action rotates service to b.
            Assert.Throws<InvalidOperationException>(() => history.CaptureDecisionBoundary("a"));
            Assert.Throws<InvalidOperationException>(() => history.CaptureDecisionBoundary("b")); // b not opened yet.
            await Advance(kernel);
            selected = history.CaptureDecisionBoundary("b");
            Assert.Equal(CharacterContextStatus.Open, history.State.Business.InteractionStates["a"].CurrentContext!.Status);
        }
        CycleBranchForkResult fork = CycleDurableHistory.Fork(store.StorePath, selected.BranchId, selected.StateCommitId, "b", "serve b");
        using var child = CycleDurableHistory.Open(store.StorePath, fork.Branch.BranchId);
        Assert.Equal(selected with { BranchId = fork.Branch.BranchId }, child.CaptureDecisionBoundary("b"));
        var controller = new RecordingController(("b-first", "handoff b continuation"));
        await Advance(CycleEngine.CreateKernel(child, characterController: controller));
        Assert.Equal(new[] { "b" }, controller.Actors);
    }

    [Fact]
    public async Task Pending_event_cannot_be_selected_or_captured_and_source_recovery_remains_exact() {
        using var store = new TemporaryStore();
        var initial = CycleGameFactory.CreateSurvival(Hour(8), enableWeather: false);
        CycleDecisionBoundary before;
        string pendingId;
        using (var history = CycleDurableHistory.Create(store.StorePath, initial.World, initial.Cursor)) {
            var kernel = CycleEngine.CreateKernel(new FailAfterTargetEvent(history), characterController: new RecordingController(("pending", "handoff published")));
            await Advance(kernel);
            before = history.CaptureDecisionBoundary("a");
            await Assert.ThrowsAsync<InvalidOperationException>(() => Advance(kernel));
            Assert.NotNull(history.PendingEvent);
            pendingId = CycleDurableHistory.FormatCommitId(history.HeadCommitId);
            Assert.Throws<InvalidOperationException>(() => history.CaptureDecisionBoundary("a"));
        }
        Assert.Throws<InvalidDataException>(() => CycleDurableHistory.Fork(store.StorePath, before.BranchId, pendingId, "a", "not supported"));
        Assert.Single(CycleDurableHistory.ListBranches(store.StorePath));
        // Choosing the explicit older S does not discard the source branch's published Event.
        CycleBranchForkResult fork = CycleDurableHistory.Fork(store.StorePath, before.BranchId, before.StateCommitId, "a", "before pending");
        using (var child = CycleDurableHistory.Open(store.StorePath, fork.Branch.BranchId)) {
            Assert.Null(child.PendingEvent);
            Assert.Null(child.State.Business.InteractionStates["a"].DraftHandoff);
            Assert.Empty(child.ReadCommittedOperations(["gym/pending"]));
        }
        using (var parent = CycleDurableHistory.Open(store.StorePath, before.BranchId)) {
            Assert.Equal(pendingId, CycleDurableHistory.FormatCommitId(parent.HeadCommitId));
            Assert.NotNull(parent.PendingEvent);
            var forbidden = new RecordingController();
            var recovery = CycleEngine.CreateKernel(parent, characterController: forbidden);
            Assert.True(recovery.RecoverPending());
            Assert.False(recovery.RecoverPending());
            Assert.Equal(0, forbidden.Calls);
            Assert.Equal("published", parent.State.Business.InteractionStates["a"].DraftHandoff);
            Assert.Single(parent.ReadCommittedOperations(["gym/pending"]));
        }
    }

    [Fact]
    public async Task Published_registration_failure_can_be_inspected_without_creating_another_branch() {
        using var store = new TemporaryStore();
        CycleDecisionBoundary source = await NewBoundary(store.StorePath);
        RepositoryPublicationException error = Assert.Throws<RepositoryPublicationException>(() =>
            CycleDurableHistory.ForkCore(store.StorePath, source.BranchId, source.StateCommitId, "a", "published but not delivered", repository => {
                PropertyInfo observer = typeof(Repository).GetProperty("OperationStageObserver", BindingFlags.Instance | BindingFlags.NonPublic)!;
                Type stage = observer.PropertyType.GetGenericArguments()[0];
                ParameterExpression parameter = Expression.Parameter(stage);
                Action<object> fail = value => { if (value.ToString() == "AfterPublication") { throw new IOException("injected fork delivery failure"); } };
                observer.SetValue(repository, Expression.Lambda(observer.PropertyType,
                    Expression.Invoke(Expression.Constant(fail), Expression.Convert(parameter, typeof(object))), parameter).Compile());
            }));
        Assert.Equal(RepositoryPublicationOutcome.Published, error.Outcome);
        string createdId = CycleDurableHistory.FormatBranchId(error.TargetBranchId!.Value);
        IReadOnlyList<CycleBranchInfo> listed = CycleDurableHistory.ListBranches(store.StorePath);
        Assert.Equal(2, listed.Count);
        Assert.Equal(source.StateCommitId, Assert.Single(listed, item => item.BranchId == source.BranchId).HeadCommitId);
        Assert.Equal(source.StateCommitId, Assert.Single(listed, item => item.BranchId == createdId).HeadCommitId);
        using var child = CycleDurableHistory.Open(store.StorePath, createdId);
        Assert.Equal(source with { BranchId = createdId }, child.CaptureDecisionBoundary("a"));
    }

    [Fact]
    public async Task Registered_branch_remains_identifiable_when_a_different_revision_cannot_open_it() {
        using var store = new TemporaryStore();
        CycleDecisionBoundary source = await NewBoundary(store.StorePath);
        CycleBranchForkResult fork = CycleDurableHistory.Fork(store.StorePath, source.BranchId, source.StateCommitId, "a", "open separately");
        MethodInfo open = typeof(CycleDurableHistory).GetMethod("OpenCore", BindingFlags.Static | BindingFlags.NonPublic)!;
        TargetInvocationException failure = Assert.Throws<TargetInvocationException>(() => open.Invoke(null,
            [store.StorePath, CycleGameRevision.Current - 1, null, null, CycleDurableHistory.ParseBranchId(fork.Branch.BranchId), null]));
        Assert.IsType<InvalidDataException>(failure.InnerException);
        Assert.Equal(2, CycleDurableHistory.ListBranches(store.StorePath).Count);
        using var child = CycleDurableHistory.Open(store.StorePath, fork.Branch.BranchId);
        Assert.Equal(source.StateCommitId, child.CurrentBranch.HeadCommitId);
    }

    [Theory]
    [InlineData("budget")]
    [InlineData("repeated-cause")]
    public async Task Open_context_is_not_a_supported_boundary_when_Kernel_progress_would_reject_the_next_request(string reason) {
        using var store = new TemporaryStore();
        var initial = CycleGameFactory.CreateSurvival(Hour(8), enableWeather: false);
        var memory = CycleEngine.CreateKernel(new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor),
            characterController: new RecordingController());
        await Advance(memory);
        CyclePrediction request = Assert.Single(memory.World.Forecasts.Predictions, item => item.Kind == CyclePredictionKind.RequestDecision);
        KernelCursor blocked = reason == "budget"
            ? new(new WorldVersion(memory.Version.LineageId, CycleEngine.MaximumTransitionsPerModelTime), initial.Cursor.GenesisTime,
                new LogicalInstant(memory.CurrentModelTime, CycleEngine.MaximumTransitionsPerModelTime - 1), memory.Cursor.LastCauseKey)
            : new(memory.Version, initial.Cursor.GenesisTime, memory.Cursor.LastInstant, new CandidateKey(request.ToString()));
        string branch, state;
        using (var history = CycleDurableHistory.Create(store.StorePath, memory.World, blocked)) {
            branch = CycleDurableHistory.FormatBranchId(history.BranchId);
            state = CycleDurableHistory.FormatCommitId(history.CompletedStateCommitId);
            Assert.Throws<InvalidOperationException>(() => history.CaptureDecisionBoundary("a"));
            var controller = new RecordingController();
            var kernel = CycleEngine.CreateKernel(history, characterController: controller);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Advance(kernel));
            Assert.Equal(0, controller.Calls);
        }
        Assert.Throws<InvalidOperationException>(() => CycleDurableHistory.InspectDecisionBoundary(store.StorePath, branch, state, "a"));
        Assert.Throws<InvalidOperationException>(() => CycleDurableHistory.Fork(store.StorePath, branch, state, "a", "blocked"));
        Assert.Single(CycleDurableHistory.ListBranches(store.StorePath));
    }

    [Fact]
    public async Task Fork_rejects_unsupported_predecessor_Event_revision_without_registering() {
        using var store = new TemporaryStore();
        var initial = CycleGameFactory.CreateSurvival(Hour(8), enableWeather: false);
        var memory = CycleEngine.CreateKernel(new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor),
            characterController: new RecordingController());
        await Advance(memory);
        var models = new StateModelRegistry(); CycleDurableModels.Register(models);
        string branch, state;
        using (Repository repository = Repository.CreateNew(store.StorePath, models)) {
            BranchInfo created = repository.CreateRootBranchFromState("fixture", new CycleDurableRoot(initial.World.Business, initial.Cursor));
            branch = CycleDurableHistory.FormatBranchId(created.Id);
            using WorkingCopy work = repository.OpenWorkingCopy(created.Id);
            work.CommitEvent(new CycleDurableEvent(memory.LastCompletion!.Event, initial.World.Definition, CycleGameRevision.Current - 1));
            state = CycleDurableHistory.FormatCommitId(work.CommitState(new CycleDurableRoot(memory.World.Business, memory.Cursor)).Id);
        }
        Assert.Throws<InvalidDataException>(() => CycleDurableHistory.Fork(store.StorePath, branch, state, "a", "unsupported predecessor"));
        Assert.Single(CycleDurableHistory.ListBranches(store.StorePath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-hex!")]
    [InlineData("00000000")]
    public void Malformed_branch_tokens_are_rejected(string encoded) {
        Assert.Throws<FormatException>(() => CycleDurableHistory.ParseBranchId(encoded));
    }

    private static async Task<CycleDecisionBoundary> NewBoundary(string path) {
        var initial = CycleGameFactory.CreateSurvival(Hour(8), enableWeather: false);
        using var history = CycleDurableHistory.Create(path, initial.World, initial.Cursor);
        await Advance(CycleEngine.CreateKernel(history, characterController: new RecordingController()));
        return history.CaptureDecisionBoundary("a");
    }

    private static async Task Advance(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel) =>
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(48)));

    private static Dictionary<string, string> HashFiles(string path) => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).ToDictionary(file => Path.GetRelativePath(path, file), file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));

    private sealed class RecordingController(params (string Id, string Command)[] commands) : ICycleCharacterController {
        private readonly Queue<(string Id, string Command)> _commands = new(commands);
        public int Calls { get; private set; }
        public List<string> Actors { get; } = [];
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) {
            Calls++; Actors.Add(context.InteractionState.ActorId);
            if (!_commands.TryDequeue(out var command)) { throw new InvalidOperationException("Unexpected controller invocation."); }
            return ValueTask.FromResult(new CycleControllerOperation("gym-call/" + command.Id, command.Command, "gym/" + command.Id));
        }
    }

    private sealed class FailAfterTargetEvent(IOccurrenceHistory<CycleWorld, CycleFact> inner) : IOccurrenceHistory<CycleWorld, CycleFact> {
        public CycleWorld State => inner.State;
        public KernelCursor Cursor => inner.Cursor;
        public OccurrenceEvent<CycleFact>? PendingEvent => inner.PendingEvent;
        public void CommitEvent(OccurrenceEvent<CycleFact> occurrence) {
            inner.CommitEvent(occurrence);
            if (occurrence.Facts.Any(fact => fact.OperationRecordId == "gym/pending")) { throw new IOException("injected after Event"); }
        }
        public void CommitState(CycleWorld state, KernelCursor cursor) => inner.CommitState(state, cursor);
    }
}
