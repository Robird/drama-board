using System.Security.Cryptography;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Playground;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

/// <summary>Artificial revisions exercise the real disk adapter, not a second test-only history implementation.</summary>
public sealed class PlaygroundRevisionTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Production_open_rejects_previous_development_revision_without_rewriting_disk(bool hasPending) {
        using var store = new TemporaryStore();
        PlaygroundDurableRoot root = InitialRoot(PlaygroundGameRevision.Current - 1);
        WriteBoundary(store.StorePath, root, hasPending ? new(RecordedYield(root), root.Business.Definition, PlaygroundGameRevision.Current - 1) : null);
        string[] before = DiskFingerprint(store.StorePath);

        Assert.Equal(25, PlaygroundGameRevision.Current);
        Assert.Throws<InvalidDataException>(() => PlaygroundDurableHistory.Open(store.StorePath));

        Assert.Equal(before, DiskFingerprint(store.StorePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Production_open_rejects_unknown_state_or_event_revision_without_publication(bool eventIsUnknown) {
        using var store = new TemporaryStore();
        PlaygroundDurableRoot root = InitialRoot(eventIsUnknown ? PlaygroundGameRevision.Current : 42);
        PlaygroundDurableEvent? pending = eventIsUnknown ? new(RecordedYield(root), root.Business.Definition, 42) : null;
        WriteBoundary(store.StorePath, root, pending);
        string[] before = DiskFingerprint(store.StorePath);

        Assert.Throws<InvalidDataException>(() => PlaygroundDurableHistory.Open(store.StorePath));

        Assert.Equal(before, DiskFingerprint(store.StorePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Loading_upgrades_only_memory_and_repeats_stably_with_or_without_pending(bool hasPending) {
        using var store = new TemporaryStore();
        PlaygroundDurableRoot original = InitialRoot();
        var pending = hasPending ? new PlaygroundDurableEvent(RecordedYield(original), original.Business.Definition, 1) : null;
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
                PlaygroundObservedEvent yielded = Assert.Single(history.PendingEvent.Facts[0].ObservedEvents,
                    entry => entry.Kind == PlaygroundObservedEventKind.GatherYielded);
                Assert.Equal("actor.3", yielded.ObserverId);
                PlaygroundObservedEffect inventory = Assert.Single(yielded.Effects, effect => effect.Key.Category == "inventory");
                Assert.Equal(new PlaygroundStateKey("inventory", "item.3", "count"), inventory.Key);
                Assert.Equal(PlaygroundVisibleValue.Numeric(22, "份"), inventory.Before);
                Assert.Equal(PlaygroundVisibleValue.Numeric(23, "份"), inventory.After);
            }
            if (readyText is null) { readyText = history.State.DescribeState(); }
            else { Assert.Equal(readyText, history.State.DescribeState()); }
        }

        Assert.Equal(before, DiskFingerprint(store.StorePath));
        Assert.Equal(2, original.Business.GetCharacter("actor.1").Backpack.Count("item.1"));
        Assert.Equal(hasPending ? new[] { "S1", "E1", "S2", "E2", "S1", "E1", "S2", "E2" }
            : new[] { "S1", "S2", "S1", "S2" }, steps.Calls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Mismatched_event_content_is_rejected_before_an_upgrade_can_rebind_its_identity(int eventRevision) {
        using var store = new TemporaryStore();
        PlaygroundDurableRoot original = InitialRoot();
        PlaygroundWorldDefinition wrong = PlaygroundGameFactory.CreateLivingWorld().World.Definition;
        WriteBoundary(store.StorePath, original, new(RecordedYield(original), wrong, eventRevision));
        string[] before = DiskFingerprint(store.StorePath);
        var revisions = new FixtureRevisions();
        Assert.Throws<InvalidDataException>(() => revisions.Open(store.StorePath, 3));
        Assert.Equal(eventRevision == 1 ? Array.Empty<string>() : ["S1"], revisions.Calls);
        Assert.Equal(before, DiskFingerprint(store.StorePath));
    }

    [Fact]
    public async Task Old_state_new_event_crash_aligns_state_before_multihop_event_conversion_and_recovers_once() {
        using var store = new TemporaryStore();
        PlaygroundDurableRoot original = InitialRoot();
        WriteBoundary(store.StorePath, original);
        string stateAtRevision2;
        OccurrenceEvent<PlaygroundFact> eventAtRevision2;
        var revision2 = new FixtureRevisions();
        using (var history = revision2.Open(store.StorePath, 2)) {
            stateAtRevision2 = history.State.DescribeState();
            var kernel = PlaygroundEngine.CreateKernel(new CrashAfterPublication(history, afterState: false));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => kernel.StepAsync(Hour(8)).AsTask());
            Assert.IsType<InjectedCrash>(error.InnerException);
            Assert.True(kernel.IsFaulted);
            Assert.Equal(stateAtRevision2, history.State.DescribeState());
            eventAtRevision2 = Assert.IsType<OccurrenceEvent<PlaygroundFact>>(history.PendingEvent);
            Assert.Equal(PlaygroundEventKind.GatherYielded, Assert.Single(eventAtRevision2.Facts).Kind);
        }
        Assert.Equal(new[] { "S1" }, revision2.Calls);
        using (var repository = Repository.OpenReadOnlyExisting(store.StorePath, Models())) {
            CommitInfo head = repository.ReadCurrentCommit(Assert.Single(repository.ListBranches()).Id).Commit;
            Assert.Equal(CommitKind.Event, head.Kind);
            var rawState = Assert.IsType<PlaygroundDurableRoot>(repository.LoadState(repository.GetPreviousState(head.Id)!.Value));
            var rawEvent = Assert.IsType<PlaygroundDurableEvent>(repository.LoadEvent(head.Id));
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
        PlaygroundDurableRoot root = InitialRoot();
        WriteBoundary(store.StorePath, root, new(RecordedYield(root), root.Business.Definition, 1));
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
        PlaygroundDurableRoot root2 = new FixtureRevisions().StateStep(InitialRoot());
        WriteBoundary(inverted.StorePath, root2, new(RecordedYield(root2), root2.Business.Definition, 1));
        string[] before = DiskFingerprint(inverted.StorePath);
        Assert.Throws<InvalidDataException>(() => new FixtureRevisions().Open(inverted.StorePath, 3));
        Assert.Equal(before, DiskFingerprint(inverted.StorePath));

        using var unsupported = new TemporaryStore();
        WriteBoundary(unsupported.StorePath, InitialRoot());
        before = DiskFingerprint(unsupported.StorePath);
        Assert.Throws<InvalidDataException>(() => PlaygroundDurableHistory.Open(unsupported.StorePath, 3,
            _ => throw new InvalidDataException("Fixture has no supported adjacent step."),
            (_, _, _) => throw new InvalidOperationException("No event exists.")));
        Assert.Throws<InvalidDataException>(() => PlaygroundDurableHistory.Open(unsupported.StorePath, 3,
            state => state, (_, _, _) => throw new InvalidOperationException("No event exists.")));
        Assert.Equal(before, DiskFingerprint(unsupported.StorePath));
    }

    [Fact]
    public void Current_revision_skips_steps_but_still_validates_business_and_unversioned_events_are_rejected() {
        using var validStore = new TemporaryStore();
        PlaygroundDurableRoot valid = InitialRoot(PlaygroundGameRevision.Current);
        WriteBoundary(validStore.StorePath, valid, new(RecordedYield(valid), valid.Business.Definition, PlaygroundGameRevision.Current));
        using (var history = PlaygroundDurableHistory.Open(validStore.StorePath, PlaygroundGameRevision.Current,
            _ => throw new InvalidOperationException("Current State must not be converted."),
            (_, _, _) => throw new InvalidOperationException("Current Event must not be converted."))) {
            Assert.Equal(valid.Cursor, history.Cursor);
            Assert.NotNull(history.PendingEvent);
        }

        using var expiredStore = new TemporaryStore();
        PlaygroundWorld old = PlaygroundWorld.Create(Hour(6), []);
        PlaygroundBusinessState expired = old.Business.Replace(Hour(10));
        var cursor = new KernelCursor(new WorldVersion(17, 0), Hour(10), null, null);
        Assert.Throws<InvalidOperationException>(() => new PlaygroundDurableRoot(expired, cursor));
        WriteBoundary(expiredStore.StorePath, PlaygroundDurableRoot.CreateRevisionCandidate(expired, cursor, PlaygroundGameRevision.Current));
        string[] before = DiskFingerprint(expiredStore.StorePath);
        Assert.Throws<InvalidOperationException>(() => PlaygroundDurableHistory.Open(expiredStore.StorePath));
        Assert.Equal(before, DiskFingerprint(expiredStore.StorePath));

        using var unversionedStore = new TemporaryStore();
        using (Repository repository = Repository.CreateNew(unversionedStore.StorePath, Models())) {
            BranchId branch = repository.CreateRootBranchFromState("unversioned-event-fixture", valid).Id;
            using WorkingCopy work = repository.OpenWorkingCopy(branch);
            work.CommitEvent(RecordedYield(valid));
        }
        before = DiskFingerprint(unversionedStore.StorePath);
        Assert.Throws<InvalidDataException>(() => PlaygroundDurableHistory.Open(unversionedStore.StorePath));
        Assert.Equal(before, DiskFingerprint(unversionedStore.StorePath));
    }

    [Fact]
    public void Restored_old_business_is_revised_before_current_business_validation() {
        using var store = new TemporaryStore();
        PlaygroundWorld old = PlaygroundWorld.Create(Hour(6), []);
        PlaygroundBusinessState expired = old.Business.Replace(Hour(10));
        var cursor = new KernelCursor(new WorldVersion(17, 0), Hour(10), null, null);
        WriteBoundary(store.StorePath, PlaygroundDurableRoot.CreateRevisionCandidate(expired, cursor, 1));
        string[] before = DiskFingerprint(store.StorePath);
        int calls = 0;
        using (var history = PlaygroundDurableHistory.Open(store.StorePath, 2, source => {
            calls++;
            Assert.Equal(1, source.GameRevision);
            // Fixture policy restarts the old regeneration clocks; this is representation upgrade, not a tick.
            PlaygroundBusinessState repaired = PlaygroundWorld.Create(source.Business.Time, []).Business;
            return PlaygroundDurableRoot.CreateRevisionCandidate(repaired, source.Cursor, 2);
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
        PlaygroundDurableRoot original = InitialRoot();
        WriteBoundary(store.StorePath, original, new(RecordedYield(original), original.Business.Definition, 1));
        string[] before = DiskFingerprint(store.StorePath);
        var revisions = new FixtureRevisions();
        Assert.Throws<InvalidDataException>(() => PlaygroundDurableHistory.Open(store.StorePath, 2, source => {
            PlaygroundDurableRoot next = revisions.StateStep(source);
            if (fault == "state-revision") { return source; }
            if (fault == "state-cursor") {
                var changedCursor = new KernelCursor(new WorldVersion(999, 0), next.Cursor.CurrentModelTime, null, null);
                return PlaygroundDurableRoot.CreateRevisionCandidate(next.Business, changedCursor, 2);
            }
            return next;
        }, (oldState, newState, pending) => {
            if (fault == "event-discarded") { return null!; }
            PlaygroundDurableEvent next = revisions.EventStep(oldState, newState, pending);
            var occurrence = new OccurrenceEvent<PlaygroundFact>(
                fault == "event-cause" ? new CandidateKey("different-event") : next.Occurrence.CauseKey,
                fault == "event-time" ? new(Hour(8), 0) : next.Occurrence.TargetInstant, next.Occurrence.Facts);
            return new(occurrence, newState.Business.Definition, fault == "event-revision" ? 1 : 2);
        }));
        Assert.Equal(before, DiskFingerprint(store.StorePath));
    }

    private static PlaygroundDurableRoot InitialRoot(int revision = 1) {
        var character = new PlaygroundCharacter("actor.1", "Fixture gatherer", backpack: new(new Dictionary<string, int> { ["item.1"] = 2 }),
            activity: new(PlaygroundActivityKind.Gather, 1, Hour(6), PlaygroundTime.Hour, Hour(7), "sunflower", 1), nextActivityId: 2);
        var resource = new PlaygroundResource("sunflower", "Fixture plant", "item.1", true, 3 * PlaygroundTime.Hour, 5, 3, Hour(6));
        PlaygroundWorld world = PlaygroundWorld.CreatePlayable(Hour(6), [character], [resource]);
        return PlaygroundDurableRoot.CreateRevisionCandidate(world.Business, InitialCursor(world), revision);
    }

    private static OccurrenceEvent<PlaygroundFact> RecordedYield(PlaygroundDurableRoot root) {
        PlaygroundCharacter actor = Assert.Single(root.Business.Characters);
        PlaygroundResource resource = Assert.Single(root.Business.Resources);
        PlaygroundCharacter changed = actor.Change(null, actor.Backpack.Add(resource.ItemId), result: "Recorded fixture yield");
        var fact = new PlaygroundFact(PlaygroundEventKind.GatherYielded, Hour(7), actor.Id, resource.Id,
            changedResources: [resource.Change(resource.Stock - 1, resource.AccumulatedTicks, resource.ActiveSince)],
            changedCharacters: [changed], result: "Recorded fixture yield", gatherStopReason: PlaygroundGatherStopReason.TargetReached,
            observedEvents: [
                new(0, actor.Id, PlaygroundObservedEventKind.GatherYielded, Hour(7), Hour(7), resource.Id, resource.Name,
                    effects: [
                        new(new("inventory", resource.ItemId, "count"), PlaygroundVisibleValue.Numeric(actor.Backpack.Count(resource.ItemId), "份"),
                            PlaygroundVisibleValue.Numeric(changed.Backpack.Count(resource.ItemId), "份")),
                        new(new("resource", resource.Id, "stock"), PlaygroundVisibleValue.Numeric(resource.Stock, "份"), PlaygroundVisibleValue.Numeric(resource.Stock - 1, "份")),
                        new(new("activity", actor.Id, "completed"), PlaygroundVisibleValue.Numeric(0, "份"), PlaygroundVisibleValue.Numeric(1, "份"))
                    ]),
                new(1, actor.Id, PlaygroundObservedEventKind.GatherFinished, Hour(7), Hour(7), resource.Id, resource.Name, PlaygroundGatherStopReason.TargetReached.ToString(),
                    [
                        new(new("activity", actor.Id, "kind"), PlaygroundVisibleValue.Literal("Gather"), PlaygroundVisibleValue.KnownAbsent),
                        new(new("activity", actor.Id, "due"), PlaygroundVisibleValue.Numeric(Hour(7).Ticks, "ticks"), PlaygroundVisibleValue.KnownAbsent),
                        new(new("activity", actor.Id, "completed"), PlaygroundVisibleValue.Numeric(1, "份"), PlaygroundVisibleValue.KnownAbsent),
                        new(new("activity", actor.Id, "target"), PlaygroundVisibleValue.Numeric(1, "份"), PlaygroundVisibleValue.KnownAbsent)
                    ])
            ]);
        fact = fact.WithInteractions([new(actor.Id, undeliveredExperiences: fact.ObservedEvents.Select((entry, index) =>
            new CharacterExperience(index + 1, entry)), nextExperienceSequence: fact.ObservedEvents.Count + 1)]);
        return new(new CandidateKey("fixture-recorded-yield"), new(Hour(7), 0), [fact]);
    }

    private static void WriteBoundary(string path, PlaygroundDurableRoot root, PlaygroundDurableEvent? pending = null) {
        using Repository repository = Repository.CreateNew(path, Models());
        BranchId branch = repository.CreateRootBranchFromState("revision-fixture", root).Id;
        if (pending is null) { return; }
        using WorkingCopy work = repository.OpenWorkingCopy(branch);
        work.CommitEvent(pending);
    }

    private static StateModelRegistry Models() {
        var models = new StateModelRegistry();
        PlaygroundDurableModels.Register(models);
        return models;
    }

    private static string[] DiskFingerprint(string path) => Directory.GetFiles(path, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(file => $"{Path.GetRelativePath(path, file)}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))}").ToArray();

    private static SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> RecoveryKernel(
        IOccurrenceHistory<PlaygroundWorld, PlaygroundFact> history, Action onFold) => new(history, new SimulationRules(0, 1024),
            [new ForbiddenPlanning()], (world, instant, fact) => { onFold(); return PlaygroundEngine.Fold(world, instant, fact); },
            (before, instant, facts, folded) => PlaygroundEngine.Complete(before, instant, facts, folded, PredictionMode.Incremental),
            PlaygroundEngine.Validate);

    /// <summary>Each artificial revision renames business identities and grants ten items; not production migration history.</summary>
    private sealed class FixtureRevisions {
        public List<string> Calls { get; } = [];
        public PlaygroundDurableHistory Open(string path, int target) => PlaygroundDurableHistory.Open(path, target, StateStep, EventStep);

        public PlaygroundDurableRoot StateStep(PlaygroundDurableRoot source) {
            Calls.Add($"S{source.GameRevision}");
            int next = source.GameRevision + 1;
            PlaygroundCharacter actor = Assert.Single(source.Business.Characters);
            PlaygroundResource resource = Assert.Single(source.Business.Resources);
            Assert.Equal($"actor.{source.GameRevision}", actor.Id);
            Assert.Equal($"item.{source.GameRevision}", resource.ItemId);
            var business = new PlaygroundBusinessState(source.Business.Time, source.Business.Light,
                [MapResource(resource, next)], source.Business.Script, [MapCharacter(actor, next, 10)],
                source.Business.LastServedActorId is null ? null : $"actor.{next}");
            return PlaygroundDurableRoot.CreateRevisionCandidate(business, source.Cursor, next);
        }

        public PlaygroundDurableEvent EventStep(PlaygroundDurableRoot before, PlaygroundDurableRoot after, PlaygroundDurableEvent pending) {
            Calls.Add($"E{pending.GameRevision}");
            Assert.Equal(before.GameRevision, pending.GameRevision);
            Assert.Equal(before.GameRevision + 1, after.GameRevision);
            PlaygroundCharacter oldActor = Assert.Single(before.Business.Characters);
            PlaygroundCharacter newActor = Assert.Single(after.Business.Characters);
            string oldItem = Assert.Single(before.Business.Resources).ItemId;
            string newItem = Assert.Single(after.Business.Resources).ItemId;
            int grant = newActor.Backpack.Count(newItem) - oldActor.Backpack.Count(oldItem);
            var facts = pending.Occurrence.Facts.Select(fact => {
                Assert.Equal(oldActor.Id, fact.ActorId);
                Assert.NotSame(oldActor, Assert.Single(fact.ChangedCharacters));
                Assert.Equal(oldItem, Assert.Single(Assert.Single(fact.ChangedCharacters).Backpack.Items).Key);
                PlaygroundObservedEvent MapEvent(PlaygroundObservedEvent entry) => new(entry.EventOrdinal,
                    entry.ObserverId == oldActor.Id ? newActor.Id : entry.ObserverId, entry.Kind, entry.From, entry.At,
                    entry.SubjectId, entry.SubjectName, entry.Detail, entry.Effects.Select(effect => {
                        string objectId = effect.Key.ObjectId == oldActor.Id ? newActor.Id
                            : effect.Key.ObjectId == oldItem ? newItem : effect.Key.ObjectId;
                        // Translate frozen endpoints with the representation's grant; no planner or perception reruns.
                        bool inventory = effect.Key.Category == "inventory" && effect.Key.ObjectId == oldItem;
                        PlaygroundVisibleValue prior = inventory ? PlaygroundVisibleValue.Numeric(effect.Before.Units + grant, effect.Before.Unit!, effect.Before.Scale) : effect.Before;
                        PlaygroundVisibleValue next = inventory ? PlaygroundVisibleValue.Numeric(effect.After.Units + grant, effect.After.Unit!, effect.After.Scale) : effect.After;
                        return new PlaygroundObservedEffect(new(effect.Key.Category, objectId, effect.Key.Field), prior, next);
                    }));
                return new PlaygroundFact(fact.Kind, fact.At, newActor.Id, fact.ResourceId, fact.Light,
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
            return new(new(pending.Occurrence.CauseKey, pending.Occurrence.TargetInstant, facts), after.Business.Definition, after.GameRevision);
        }

        private static PlaygroundCharacter MapCharacter(PlaygroundCharacter actor, int revision, int grant) => new(
            $"actor.{revision}", actor.Name, actor.SkillLevel,
            new(new Dictionary<string, int> { [$"item.{revision}"] = Assert.Single(actor.Backpack.Items).Value + grant }),
            actor.Activity, actor.Notices, actor.DecisionGeneration, actor.LastResult, actor.NextActivityId);

        private static PlaygroundResource MapResource(PlaygroundResource resource, int revision) => new(resource.Id, resource.Name,
            $"item.{revision}", resource.GrowsInDaylight, resource.RegenerationTicks, resource.Capacity, resource.Stock,
            resource.ActiveSince, resource.AccumulatedTicks, resource.HarvestTicks, resource.SkillId,
            revision == 3 ? 99 : resource.RequiredLevel, resource.Generation);
    }

    private sealed class ForbiddenPlanning : IOccurrenceRule<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> {
        public IReadOnlyList<OccurrenceCandidate<PlaygroundPrediction>> Forecast(PlaygroundWorld world, SimulationRules rules) =>
            throw new InvalidOperationException("Recovery must not discover candidates.");
        public ValueTask<TransitionDraft<PlaygroundFact>> PlanSelectedAsync(PlaygroundWorld world,
            OccurrenceCandidate<PlaygroundPrediction> winner, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Recovery must not replan or call a CharacterController.");
    }

    private sealed class InjectedCrash : IOException { }

    private sealed class CrashAfterPublication(IOccurrenceHistory<PlaygroundWorld, PlaygroundFact> inner, bool afterState)
        : IOccurrenceHistory<PlaygroundWorld, PlaygroundFact> {
        public PlaygroundWorld State => inner.State;
        public KernelCursor Cursor => inner.Cursor;
        public OccurrenceEvent<PlaygroundFact>? PendingEvent => inner.PendingEvent;
        public void CommitEvent(OccurrenceEvent<PlaygroundFact> occurrence) {
            inner.CommitEvent(occurrence);
            if (!afterState) { throw new InjectedCrash(); }
        }
        public void CommitState(PlaygroundWorld state, KernelCursor cursor) {
            inner.CommitState(state, cursor);
            throw new InjectedCrash();
        }
    }
}
