using System.Security.Cryptography;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.ResourceCycle;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

/// <summary>Artificial revisions exercise the real disk adapter, not a second test-only history implementation.</summary>
public sealed class CycleRevisionTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Production_open_rejects_previous_development_revision_without_rewriting_disk(bool hasPending) {
        using var store = new TemporaryStore();
        CycleDurableRoot root = InitialRoot(CycleGameRevision.Current - 1);
        WriteBoundary(store.StorePath, root, hasPending ? new(RecordedYield(root), CycleGameRevision.Current - 1) : null);
        string[] before = DiskFingerprint(store.StorePath);

        Assert.Equal(16, CycleGameRevision.Current);
        Assert.Throws<InvalidDataException>(() => CycleDurableHistory.Open(store.StorePath));

        Assert.Equal(before, DiskFingerprint(store.StorePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Production_open_rejects_unknown_state_or_event_revision_without_publication(bool eventIsUnknown) {
        using var store = new TemporaryStore();
        CycleDurableRoot root = InitialRoot(eventIsUnknown ? CycleGameRevision.Current : 42);
        CycleDurableEvent? pending = eventIsUnknown ? new(RecordedYield(root), 42) : null;
        WriteBoundary(store.StorePath, root, pending);
        string[] before = DiskFingerprint(store.StorePath);

        Assert.Throws<InvalidDataException>(() => CycleDurableHistory.Open(store.StorePath));

        Assert.Equal(before, DiskFingerprint(store.StorePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Loading_upgrades_only_memory_and_repeats_stably_with_or_without_pending(bool hasPending) {
        using var store = new TemporaryStore();
        CycleDurableRoot original = InitialRoot();
        var pending = hasPending ? new CycleDurableEvent(RecordedYield(original), 1) : null;
        WriteBoundary(store.StorePath, original, pending);
        string[] before = DiskFingerprint(store.StorePath);
        var steps = new FixtureRevisions();
        string? readyText = null;

        for (int attempt = 0; attempt < 2; attempt++) {
            using var history = steps.Open(store.StorePath, 3);
            Assert.Equal(original.Cursor, history.Cursor);
            Assert.Equal(22, history.State.GetCharacter("actor.3").Backpack.Count("item.3"));
            Assert.Equal(3, history.State.GetResource("sunflower").Stock);
            Assert.Equal(hasPending, history.PendingEvent is not null);
            if (pending is not null) {
                Assert.Equal(pending.Occurrence.CauseKey, history.PendingEvent!.CauseKey);
                Assert.Equal(pending.Occurrence.TargetInstant, history.PendingEvent.TargetInstant);
                Assert.Equal(23, Assert.Single(Assert.Single(history.PendingEvent.Facts).ChangedCharacters).Backpack.Count("item.3"));
                Assert.NotSame(history.State.Characters[0], history.PendingEvent.Facts[0].ChangedCharacters[0]);
                CycleObservedEvent yielded = Assert.Single(history.PendingEvent.Facts[0].ObservedEvents,
                    entry => entry.Kind == CycleObservedEventKind.GatherYielded);
                Assert.Equal("actor.3", yielded.ObserverId);
                CycleObservedEffect inventory = Assert.Single(yielded.Effects, effect => effect.Key.Category == "inventory");
                Assert.Equal(new CycleStateKey("inventory", "item.3", "count"), inventory.Key);
                Assert.Equal(CycleVisibleValue.Numeric(22, "份"), inventory.Before);
                Assert.Equal(CycleVisibleValue.Numeric(23, "份"), inventory.After);
            }
            if (readyText is null) { readyText = history.State.DescribeState(); }
            else { Assert.Equal(readyText, history.State.DescribeState()); }
        }

        Assert.Equal(before, DiskFingerprint(store.StorePath));
        Assert.Equal(2, original.Business.GetCharacter("actor.1").Backpack.Count("item.1"));
        Assert.Equal(hasPending ? new[] { "S1", "E1", "S2", "E2", "S1", "E1", "S2", "E2" }
            : new[] { "S1", "S2", "S1", "S2" }, steps.Calls);
    }

    [Fact]
    public async Task Old_state_new_event_crash_aligns_state_before_multihop_event_conversion_and_recovers_once() {
        using var store = new TemporaryStore();
        CycleDurableRoot original = InitialRoot();
        WriteBoundary(store.StorePath, original);
        string stateAtRevision2;
        OccurrenceEvent<CycleFact> eventAtRevision2;
        var revision2 = new FixtureRevisions();
        using (var history = revision2.Open(store.StorePath, 2)) {
            stateAtRevision2 = history.State.DescribeState();
            var kernel = CycleEngine.CreateKernel(new CrashAfterPublication(history, afterState: false));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => kernel.StepAsync(Hour(8)).AsTask());
            Assert.IsType<InjectedCrash>(error.InnerException);
            Assert.True(kernel.IsFaulted);
            Assert.Equal(stateAtRevision2, history.State.DescribeState());
            eventAtRevision2 = Assert.IsType<OccurrenceEvent<CycleFact>>(history.PendingEvent);
            Assert.Equal(CycleEventKind.GatherYielded, Assert.Single(eventAtRevision2.Facts).Kind);
        }
        Assert.Equal(new[] { "S1" }, revision2.Calls);
        using (var repository = Repository.OpenReadOnlyExisting(store.StorePath, Models())) {
            CommitInfo head = repository.ReadCurrentCommit(Assert.Single(repository.ListBranches()).Id).Commit;
            Assert.Equal(CommitKind.Event, head.Kind);
            var rawState = Assert.IsType<CycleDurableRoot>(repository.LoadState(repository.GetPreviousState(head.Id)!.Value));
            var rawEvent = Assert.IsType<CycleDurableEvent>(repository.LoadEvent(head.Id));
            Assert.Equal(1, rawState.GameRevision);
            Assert.Equal(2, rawEvent.GameRevision);
            Assert.Equal(Describe(eventAtRevision2), Describe(rawEvent.Occurrence));
        }

        // The same source S must reproduce exactly the planning prestate, even now that E exists.
        using (var reopenedAt2 = new FixtureRevisions().Open(store.StorePath, 2)) {
            Assert.Equal(stateAtRevision2, reopenedAt2.State.DescribeState());
            Assert.Equal(Describe(eventAtRevision2), Describe(reopenedAt2.PendingEvent!));
        }
        string[] beforeUpgrade = DiskFingerprint(store.StorePath);
        var revision3 = new FixtureRevisions();
        using (var loaded = revision3.Open(store.StorePath, 3)) {
            Assert.Equal(new[] { "S1", "S2", "E2" }, revision3.Calls);
            Assert.Equal(original.Cursor, loaded.Cursor);
            Assert.Equal(22, loaded.State.GetCharacter("actor.3").Backpack.Count("item.3"));
            Assert.Equal(eventAtRevision2.CauseKey, loaded.PendingEvent!.CauseKey);
            Assert.Equal(eventAtRevision2.TargetInstant, loaded.PendingEvent.TargetInstant);
        }
        Assert.Equal(beforeUpgrade, DiskFingerprint(store.StorePath));

        KernelCursor completedCursor;
        string completedText;
        using (var loaded = new FixtureRevisions().Open(store.StorePath, 3)) {
            int folds = 0;
            var kernel = RecoveryKernel(loaded, () => folds++);
            Assert.True(kernel.RecoverPending());
            Assert.False(kernel.RecoverPending());
            Assert.Equal(1, folds);
            Assert.Equal(original.Cursor.Advance(eventAtRevision2.CauseKey, eventAtRevision2.TargetInstant), kernel.Cursor);
            Assert.Equal(23, kernel.World.GetCharacter("actor.3").Backpack.Count("item.3"));
            Assert.Equal(0, kernel.World.GetCharacter("actor.3").Backpack.Count("item.2"));
            Assert.Equal(2, kernel.World.GetResource("sunflower").Stock);
            Assert.Equal(99, kernel.World.GetResource("sunflower").RequiredLevel);
            Assert.Null(kernel.World.GetCharacter("actor.3").Activity);
            completedCursor = kernel.Cursor;
            completedText = kernel.World.DescribeState();
        }
        using var final = new FixtureRevisions().Open(store.StorePath, 3);
        Assert.Null(final.PendingEvent);
        Assert.Equal(completedCursor, final.Cursor);
        Assert.Equal(completedText, final.State.DescribeState());
        Assert.False(RecoveryKernel(final, () => Assert.Fail("Completed history must not Fold again.")).RecoverPending());
    }

    [Fact]
    public void Successful_state_publication_followed_by_failure_is_not_applied_again_after_reopen() {
        using var store = new TemporaryStore();
        CycleDurableRoot root = InitialRoot();
        WriteBoundary(store.StorePath, root, new(RecordedYield(root), 1));
        KernelCursor expectedCursor;
        using (var history = new FixtureRevisions().Open(store.StorePath, 3)) {
            expectedCursor = root.Cursor.Advance(history.PendingEvent!.CauseKey, history.PendingEvent.TargetInstant);
            var kernel = RecoveryKernel(new CrashAfterPublication(history, afterState: true), () => { });
            var error = Assert.Throws<InvalidOperationException>(() => kernel.RecoverPending());
            Assert.IsType<InjectedCrash>(error.InnerException);
            Assert.True(kernel.IsFaulted);
        }

        using var reopened = new FixtureRevisions().Open(store.StorePath, 3);
        Assert.Null(reopened.PendingEvent);
        Assert.Equal(expectedCursor, reopened.Cursor);
        Assert.Equal(23, reopened.State.GetCharacter("actor.3").Backpack.Count("item.3"));
        Assert.Equal(2, reopened.State.GetResource("sunflower").Stock);
        Assert.False(RecoveryKernel(reopened, () => Assert.Fail("Published S must not be executed twice.")).RecoverPending());
    }

    [Fact]
    public void Invalid_version_pair_and_missing_or_bad_upgrade_steps_leave_disk_unchanged() {
        using var inverted = new TemporaryStore();
        CycleDurableRoot root2 = new FixtureRevisions().StateStep(InitialRoot());
        WriteBoundary(inverted.StorePath, root2, new(RecordedYield(root2), 1));
        string[] before = DiskFingerprint(inverted.StorePath);
        Assert.Throws<InvalidDataException>(() => new FixtureRevisions().Open(inverted.StorePath, 3));
        Assert.Equal(before, DiskFingerprint(inverted.StorePath));

        using var unsupported = new TemporaryStore();
        WriteBoundary(unsupported.StorePath, InitialRoot());
        before = DiskFingerprint(unsupported.StorePath);
        Assert.Throws<InvalidDataException>(() => CycleDurableHistory.Open(unsupported.StorePath, 3,
            _ => throw new InvalidDataException("Fixture has no supported adjacent step."),
            (_, _, _) => throw new InvalidOperationException("No event exists.")));
        Assert.Throws<InvalidDataException>(() => CycleDurableHistory.Open(unsupported.StorePath, 3,
            state => state, (_, _, _) => throw new InvalidOperationException("No event exists.")));
        Assert.Equal(before, DiskFingerprint(unsupported.StorePath));
    }

    [Fact]
    public void Current_revision_skips_steps_but_still_validates_business_and_unversioned_events_are_rejected() {
        using var validStore = new TemporaryStore();
        CycleDurableRoot valid = InitialRoot(CycleGameRevision.Current);
        WriteBoundary(validStore.StorePath, valid, new(RecordedYield(valid), CycleGameRevision.Current));
        using (var history = CycleDurableHistory.Open(validStore.StorePath, CycleGameRevision.Current,
            _ => throw new InvalidOperationException("Current State must not be converted."),
            (_, _, _) => throw new InvalidOperationException("Current Event must not be converted."))) {
            Assert.Equal(valid.Cursor, history.Cursor);
            Assert.NotNull(history.PendingEvent);
        }

        using var expiredStore = new TemporaryStore();
        CycleWorld old = CycleWorld.Create(Hour(6), []);
        CycleBusinessState expired = old.Business.Replace(Hour(10));
        var cursor = new KernelCursor(new WorldVersion(17, 0), Hour(10), null, null);
        Assert.Throws<InvalidOperationException>(() => new CycleDurableRoot(expired, cursor));
        WriteBoundary(expiredStore.StorePath, CycleDurableRoot.CreateRevisionCandidate(expired, cursor, CycleGameRevision.Current));
        string[] before = DiskFingerprint(expiredStore.StorePath);
        Assert.Throws<InvalidOperationException>(() => CycleDurableHistory.Open(expiredStore.StorePath));
        Assert.Equal(before, DiskFingerprint(expiredStore.StorePath));

        using var unversionedStore = new TemporaryStore();
        using (Repository repository = Repository.CreateNew(unversionedStore.StorePath, Models())) {
            BranchId branch = repository.CreateRootBranchFromState("unversioned-event-fixture", valid).Id;
            using WorkingCopy work = repository.OpenWorkingCopy(branch);
            work.CommitEvent(RecordedYield(valid));
        }
        before = DiskFingerprint(unversionedStore.StorePath);
        Assert.Throws<InvalidDataException>(() => CycleDurableHistory.Open(unversionedStore.StorePath));
        Assert.Equal(before, DiskFingerprint(unversionedStore.StorePath));
    }

    [Fact]
    public void Restored_old_business_is_revised_before_current_business_validation() {
        using var store = new TemporaryStore();
        CycleWorld old = CycleWorld.Create(Hour(6), []);
        CycleBusinessState expired = old.Business.Replace(Hour(10));
        var cursor = new KernelCursor(new WorldVersion(17, 0), Hour(10), null, null);
        WriteBoundary(store.StorePath, CycleDurableRoot.CreateRevisionCandidate(expired, cursor, 1));
        string[] before = DiskFingerprint(store.StorePath);
        int calls = 0;
        using (var history = CycleDurableHistory.Open(store.StorePath, 2, source => {
            calls++;
            Assert.Equal(1, source.GameRevision);
            // Fixture policy restarts the old regeneration clocks; this is representation upgrade, not a tick.
            CycleBusinessState repaired = CycleWorld.Create(source.Business.Time, []).Business;
            return CycleDurableRoot.CreateRevisionCandidate(repaired, source.Cursor, 2);
        }, (_, _, _) => throw new InvalidOperationException("No pending Event exists."))) {
            Assert.Equal(cursor, history.Cursor);
            Assert.Equal(1, calls);
            history.State.Validate();
            Assert.Null(history.PendingEvent);
            Assert.Equal(Hour(10), history.State.GetResource("sunflower").ActiveSince);
        }
        Assert.Equal(before, DiskFingerprint(store.StorePath));
    }

    [Theory]
    [InlineData("state-revision")]
    [InlineData("state-cursor")]
    [InlineData("event-revision")]
    [InlineData("event-cause")]
    [InlineData("event-time")]
    [InlineData("event-discarded")]
    public void Upgrade_steps_cannot_rewrite_boundary_identity_or_skip_revision(string fault) {
        using var store = new TemporaryStore();
        CycleDurableRoot original = InitialRoot();
        WriteBoundary(store.StorePath, original, new(RecordedYield(original), 1));
        string[] before = DiskFingerprint(store.StorePath);
        var revisions = new FixtureRevisions();
        Assert.Throws<InvalidDataException>(() => CycleDurableHistory.Open(store.StorePath, 2, source => {
            CycleDurableRoot next = revisions.StateStep(source);
            if (fault == "state-revision") { return source; }
            if (fault == "state-cursor") {
                var changedCursor = new KernelCursor(new WorldVersion(999, 0), next.Cursor.CurrentModelTime, null, null);
                return CycleDurableRoot.CreateRevisionCandidate(next.Business, changedCursor, 2);
            }
            return next;
        }, (oldState, newState, pending) => {
            if (fault == "event-discarded") { return null!; }
            CycleDurableEvent next = revisions.EventStep(oldState, newState, pending);
            var occurrence = new OccurrenceEvent<CycleFact>(
                fault == "event-cause" ? new CandidateKey("different-event") : next.Occurrence.CauseKey,
                fault == "event-time" ? new(Hour(8), 0) : next.Occurrence.TargetInstant, next.Occurrence.Facts);
            return new(occurrence, fault == "event-revision" ? 1 : 2);
        }));
        Assert.Equal(before, DiskFingerprint(store.StorePath));
    }

    private static CycleDurableRoot InitialRoot(int revision = 1) {
        var character = new CycleCharacter("actor.1", "Fixture gatherer", backpack: new(new Dictionary<string, int> { ["item.1"] = 2 }),
            activity: new(CycleActivityKind.Gather, 1, Hour(6), CycleTime.Hour, Hour(7), "sunflower", 1), nextActivityId: 2);
        var resource = new CycleResource("sunflower", "Fixture plant", "item.1", true, 3 * CycleTime.Hour, 5, 3, Hour(6));
        CycleWorld world = CycleWorld.CreatePlayable(Hour(6), [character], [resource]);
        return CycleDurableRoot.CreateRevisionCandidate(world.Business, InitialCursor(world), revision);
    }

    private static OccurrenceEvent<CycleFact> RecordedYield(CycleDurableRoot root) {
        CycleCharacter actor = Assert.Single(root.Business.Characters);
        CycleResource resource = Assert.Single(root.Business.Resources);
        CycleCharacter changed = actor.Change(null, actor.Backpack.Add(resource.ItemId), result: "Recorded fixture yield");
        var fact = new CycleFact(CycleEventKind.GatherYielded, Hour(7), actor.Id, resource.Id,
            changedResources: [resource.Change(resource.Stock - 1, resource.AccumulatedTicks, resource.ActiveSince)],
            changedCharacters: [changed], result: "Recorded fixture yield", gatherStopReason: CycleGatherStopReason.TargetReached,
            observedEvents: [
                new(0, actor.Id, CycleObservedEventKind.GatherYielded, Hour(7), Hour(7), resource.Id, resource.Name,
                    effects: [
                        new(new("inventory", resource.ItemId, "count"), CycleVisibleValue.Numeric(actor.Backpack.Count(resource.ItemId), "份"),
                            CycleVisibleValue.Numeric(changed.Backpack.Count(resource.ItemId), "份")),
                        new(new("resource", resource.Id, "stock"), CycleVisibleValue.Numeric(resource.Stock, "份"), CycleVisibleValue.Numeric(resource.Stock - 1, "份")),
                        new(new("activity", actor.Id, "completed"), CycleVisibleValue.Numeric(0, "份"), CycleVisibleValue.Numeric(1, "份"))
                    ]),
                new(1, actor.Id, CycleObservedEventKind.GatherFinished, Hour(7), Hour(7), resource.Id, resource.Name, CycleGatherStopReason.TargetReached.ToString(),
                    [
                        new(new("activity", actor.Id, "kind"), CycleVisibleValue.Literal("Gather"), CycleVisibleValue.KnownAbsent),
                        new(new("activity", actor.Id, "due"), CycleVisibleValue.Numeric(Hour(7).Ticks, "ticks"), CycleVisibleValue.KnownAbsent),
                        new(new("activity", actor.Id, "completed"), CycleVisibleValue.Numeric(1, "份"), CycleVisibleValue.KnownAbsent),
                        new(new("activity", actor.Id, "target"), CycleVisibleValue.Numeric(1, "份"), CycleVisibleValue.KnownAbsent)
                    ])
            ]);
        fact = fact.WithInteractions([new(actor.Id, undeliveredExperiences: fact.ObservedEvents.Select((entry, index) =>
            new CharacterExperience(index + 1, entry)), nextExperienceSequence: fact.ObservedEvents.Count + 1)]);
        return new(new CandidateKey("fixture-recorded-yield"), new(Hour(7), 0), [fact]);
    }

    private static void WriteBoundary(string path, CycleDurableRoot root, CycleDurableEvent? pending = null) {
        using Repository repository = Repository.CreateNew(path, Models());
        BranchId branch = repository.CreateRootBranchFromState("revision-fixture", root).Id;
        if (pending is null) { return; }
        using WorkingCopy work = repository.OpenWorkingCopy(branch);
        work.CommitEvent(pending);
    }

    private static StateModelRegistry Models() {
        var models = new StateModelRegistry();
        CycleDurableModels.Register(models);
        return models;
    }

    private static string[] DiskFingerprint(string path) => Directory.GetFiles(path, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(file => $"{Path.GetRelativePath(path, file)}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))}").ToArray();

    private static SimulationKernel<CycleWorld, CyclePrediction, CycleFact> RecoveryKernel(
        IOccurrenceHistory<CycleWorld, CycleFact> history, Action onFold) => new(history, new SimulationRules(0, 1024),
            [new ForbiddenPlanning()], (world, instant, fact) => { onFold(); return CycleEngine.Fold(world, instant, fact); },
            (before, instant, facts, folded) => CycleEngine.Complete(before, instant, facts, folded, PredictionMode.Incremental),
            CycleEngine.Validate);

    /// <summary>Each artificial revision renames business identities and grants ten items; not production migration history.</summary>
    private sealed class FixtureRevisions {
        public List<string> Calls { get; } = [];
        public CycleDurableHistory Open(string path, int target) => CycleDurableHistory.Open(path, target, StateStep, EventStep);

        public CycleDurableRoot StateStep(CycleDurableRoot source) {
            Calls.Add($"S{source.GameRevision}");
            int next = source.GameRevision + 1;
            CycleCharacter actor = Assert.Single(source.Business.Characters);
            CycleResource resource = Assert.Single(source.Business.Resources);
            Assert.Equal($"actor.{source.GameRevision}", actor.Id);
            Assert.Equal($"item.{source.GameRevision}", resource.ItemId);
            var business = new CycleBusinessState(source.Business.Time, source.Business.Light,
                [MapResource(resource, next)], source.Business.Script, [MapCharacter(actor, next, 10)],
                source.Business.LastServedActorId is null ? null : $"actor.{next}");
            return CycleDurableRoot.CreateRevisionCandidate(business, source.Cursor, next);
        }

        public CycleDurableEvent EventStep(CycleDurableRoot before, CycleDurableRoot after, CycleDurableEvent pending) {
            Calls.Add($"E{pending.GameRevision}");
            Assert.Equal(before.GameRevision, pending.GameRevision);
            Assert.Equal(before.GameRevision + 1, after.GameRevision);
            CycleCharacter oldActor = Assert.Single(before.Business.Characters);
            CycleCharacter newActor = Assert.Single(after.Business.Characters);
            string oldItem = Assert.Single(before.Business.Resources).ItemId;
            string newItem = Assert.Single(after.Business.Resources).ItemId;
            int grant = newActor.Backpack.Count(newItem) - oldActor.Backpack.Count(oldItem);
            var facts = pending.Occurrence.Facts.Select(fact => {
                Assert.Equal(oldActor.Id, fact.ActorId);
                Assert.NotSame(oldActor, Assert.Single(fact.ChangedCharacters));
                Assert.Equal(oldItem, Assert.Single(Assert.Single(fact.ChangedCharacters).Backpack.Items).Key);
                CycleObservedEvent MapEvent(CycleObservedEvent entry) => new(entry.EventOrdinal,
                    entry.ObserverId == oldActor.Id ? newActor.Id : entry.ObserverId, entry.Kind, entry.From, entry.At,
                    entry.SubjectId, entry.SubjectName, entry.Detail, entry.Effects.Select(effect => {
                        string objectId = effect.Key.ObjectId == oldActor.Id ? newActor.Id
                            : effect.Key.ObjectId == oldItem ? newItem : effect.Key.ObjectId;
                        // Translate frozen endpoints with the representation's grant; no planner or perception reruns.
                        bool inventory = effect.Key.Category == "inventory" && effect.Key.ObjectId == oldItem;
                        CycleVisibleValue prior = inventory ? CycleVisibleValue.Numeric(effect.Before.Units + grant, effect.Before.Unit!, effect.Before.Scale) : effect.Before;
                        CycleVisibleValue next = inventory ? CycleVisibleValue.Numeric(effect.After.Units + grant, effect.After.Unit!, effect.After.Scale) : effect.After;
                        return new CycleObservedEffect(new(effect.Key.Category, objectId, effect.Key.Field), prior, next);
                    }));
                return new CycleFact(fact.Kind, fact.At, newActor.Id, fact.ResourceId, fact.Light,
                    fact.ChangedResources.Select(resource => MapResource(resource, after.GameRevision)), fact.Script,
                    fact.ChangedCharacters.Select(actor => MapCharacter(actor, after.GameRevision, grant)),
                    fact.LastServedActorId is null ? null : newActor.Id, fact.Result,
                    gatherStopReason: fact.GatherStopReason,
                    observedEvents: fact.ObservedEvents.Select(MapEvent),
                    interactionChanges: fact.InteractionChanges.Select(state => {
                        Assert.Null(state.CurrentContext);
                        Assert.Null(state.PendingAdvance);
                        return new CharacterInteractionState(newActor.Id, undeliveredExperiences: state.UndeliveredExperiences
                            .Select(experience => new CharacterExperience(experience.ExperienceSequence, MapEvent(experience.Event))),
                            nextExperienceSequence: state.NextExperienceSequence);
                    }));
            });
            return new(new(pending.Occurrence.CauseKey, pending.Occurrence.TargetInstant, facts), after.GameRevision);
        }

        private static CycleCharacter MapCharacter(CycleCharacter actor, int revision, int grant) => new(
            $"actor.{revision}", actor.Name, actor.SkillLevel,
            new(new Dictionary<string, int> { [$"item.{revision}"] = Assert.Single(actor.Backpack.Items).Value + grant }),
            actor.Activity, actor.Notices, actor.DecisionGeneration, actor.LastResult, actor.NextActivityId);

        private static CycleResource MapResource(CycleResource resource, int revision) => new(resource.Id, resource.Name,
            $"item.{revision}", resource.GrowsInDaylight, resource.RegenerationTicks, resource.Capacity, resource.Stock,
            resource.ActiveSince, resource.AccumulatedTicks, resource.HarvestTicks, resource.SkillId,
            revision == 3 ? 99 : resource.RequiredLevel, resource.Generation);
    }

    private sealed class ForbiddenPlanning : IOccurrenceRule<CycleWorld, CyclePrediction, CycleFact> {
        public IReadOnlyList<OccurrenceCandidate<CyclePrediction>> Forecast(CycleWorld world, SimulationRules rules) =>
            throw new InvalidOperationException("Recovery must not discover candidates.");
        public ValueTask<TransitionDraft<CycleFact>> PlanSelectedAsync(CycleWorld world,
            OccurrenceCandidate<CyclePrediction> winner, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Recovery must not replan or call a CharacterController.");
    }

    private sealed class InjectedCrash : IOException { }

    private sealed class CrashAfterPublication(IOccurrenceHistory<CycleWorld, CycleFact> inner, bool afterState)
        : IOccurrenceHistory<CycleWorld, CycleFact> {
        public CycleWorld State => inner.State;
        public KernelCursor Cursor => inner.Cursor;
        public OccurrenceEvent<CycleFact>? PendingEvent => inner.PendingEvent;
        public void CommitEvent(OccurrenceEvent<CycleFact> occurrence) {
            inner.CommitEvent(occurrence);
            if (!afterState) { throw new InjectedCrash(); }
        }
        public void CommitState(CycleWorld state, KernelCursor cursor) {
            inner.CommitState(state, cursor);
            throw new InjectedCrash();
        }
    }
}
