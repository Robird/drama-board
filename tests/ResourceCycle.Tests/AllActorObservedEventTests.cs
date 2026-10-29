using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class AllActorObservedEventTests {
    [Fact]
    public async Task Plain_gathering_records_own_yield_and_finish_without_physiology_or_private_leaks() {
        ModelTime at = Hour(8);
        CycleWorld initial = CycleWorld.CreatePlayable(at, [
            new("a", "Alice", activity: Gathering(at, Hour(9)), nextActivityId: 2),
            new("b", "Bob", skillLevel: 3, activity: Gathering(at, Hour(10)), nextActivityId: 2)
        ], [Sun(at, stock: 1, regenerationTicks: 8 * CycleTime.Hour)]);
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: Commands());

        CycleFact fact = await Advance(kernel);

        Assert.Equal(CycleEventKind.GatherYielded, fact.Kind);
        Assert.Equal(new[] { CycleObservedEventKind.GatherYielded, CycleObservedEventKind.GatherFinished }, fact.ObservedEvents.Select(e => e.Kind));
        Assert.All(fact.ObservedEvents, e => Assert.Equal("a", e.ObserverId));
        CycleObservedEvent yielded = fact.ObservedEvents[0];
        Assert.Equal(CycleVisibleValue.Numeric(1, "份"), Effect(yielded, "inventory", "item.sunflower", "count").After);
        Assert.Equal(CycleVisibleValue.Numeric(0, "份"), Effect(yielded, "resource", "sunflower", "stock").After);
        Assert.Equal(CycleVisibleValue.Numeric(1, "份"), Effect(yielded, "activity", "a", "completed").After);
        Assert.Equal(CycleVisibleValue.KnownAbsent, Effect(fact.ObservedEvents[1], "activity", "a", "kind").After);
        AssertNoBody(fact);
        Assert.Single(kernel.World.GetCharacter("b").Notices);
        Assert.Equal(0, kernel.World.GetCharacter("b").Backpack.Count("item.sunflower"));
        Assert.Throws<InvalidOperationException>(() => CycleEngine.Fold(initial, kernel.LastCompletion!.Event.TargetInstant, fact.WithObservedEvents([])));
    }

    [Fact]
    public async Task Other_collectors_joining_remains_a_snapshot_change_without_inventing_private_events() {
        ModelTime at = Hour(8);
        CycleWorld initial = CycleWorld.CreatePlayable(at, [
            new("a", "Alice", activity: Gathering(at, Hour(10)), nextActivityId: 2),
            new("b", "Bob")
        ], [Sun(at)]);
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: Commands(CycleCommand.Gather("sunflower")));

        CycleFact fact = await Advance(kernel);

        CycleObservedEvent started = Assert.Single(fact.ObservedEvents);
        Assert.Equal("b", started.ObserverId);
        Assert.Equal(CycleObservedEventKind.GatherStarted, started.Kind);
        Assert.All(started.Effects, effect => Assert.Equal("b", effect.Key.ObjectId));
        Assert.Single(kernel.World.GetCharacter("a").Notices);
        AssertNoBody(fact);
    }

    [Theory]
    [InlineData(CycleCommandKind.Wait, CycleObservedEventKind.WaitStarted)]
    [InlineData(CycleCommandKind.Cancel, CycleObservedEventKind.ActivityCancelled)]
    [InlineData(CycleCommandKind.Continue, CycleObservedEventKind.AttentionAcknowledged)]
    public async Task Plain_character_decisions_record_their_own_effects(CycleCommandKind command, CycleObservedEventKind kind) {
        ModelTime at = Hour(8);
        CycleActivity? activity = command == CycleCommandKind.Wait ? null : Gathering(at, Hour(10));
        CycleNotice notice = new(CycleNoticeKind.ResourceDepleted, "sunflower", null, "fixture", at, "Resource was depleted.");
        CycleWorld initial = CycleWorld.CreatePlayable(at, [new("a", "Alice", activity: activity, notices: [notice], nextActivityId: 2)], [Sun(at)]);
        var selected = command switch {
            CycleCommandKind.Wait => CycleCommand.Wait(1),
            CycleCommandKind.Cancel => CycleCommand.Cancel(),
            _ => CycleCommand.Continue()
        };
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: Commands(selected));

        CycleFact fact = await Advance(kernel);

        CycleObservedEvent action = Assert.Single(fact.ObservedEvents);
        Assert.Equal(kind, action.Kind);
        Assert.Equal(CycleVisibleValue.Numeric(0, "条"), Effect(action, "notice", "a", "count").After);
        AssertNoBody(fact);
    }

    [Fact]
    public async Task Plain_wait_completion_has_an_event_and_activity_removal() {
        ModelTime at = Hour(8);
        CycleWorld initial = CycleWorld.CreatePlayable(at, [Waiting("a", at, Hour(9))], [Sun(at, stock: 5)]);
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: Commands());
        CycleFact fact = await Advance(kernel);
        CycleObservedEvent ended = Assert.Single(fact.ObservedEvents);
        Assert.Equal(CycleObservedEventKind.WaitCompleted, ended.Kind);
        Assert.Equal(CycleVisibleValue.KnownAbsent, Effect(ended, "activity", "a", "kind").After);
        AssertNoBody(fact);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Plain_public_resource_changes_are_visible_to_each_actor(bool destruction) {
        ModelTime at = Hour(8);
        CycleWorld initial = CycleWorld.CreatePlayable(at, [Waiting("a", at, Hour(12)), Waiting("b", at, Hour(12))], [Sun(at)]);
        CycleFact planned = destruction
            ? new(CycleEventKind.ResourceDestroyed, Hour(9), resourceId: "sunflower",
                changedResources: [initial.GetResource("sunflower").Change(0, 0, Hour(9))])
            : new(CycleEventKind.ResourceRegenerated, Hour(9), resourceId: "sunflower",
                changedResources: [initial.GetResource("sunflower").Change(4, 0, Hour(9))]);
        CycleFact fact = CycleObservedEvents.Attach(initial, planned);
        CycleBusinessState after = initial.Business.Replace(fact.At, changedResources: fact.ChangedResources);
        CycleObservedEventValidation.Validate(initial.Business, after, fact);

        Assert.Equal(new[] { "a", "b" }, fact.ObservedEvents.Select(e => e.ObserverId));
        Assert.Equal(new[] { 0, 1 }, fact.ObservedEvents.Select(e => e.EventOrdinal));
        Assert.All(fact.ObservedEvents, entry => {
            Assert.Equal(destruction ? CycleObservedEventKind.ResourceDestroyed : CycleObservedEventKind.ResourceRegenerated, entry.Kind);
            CycleObservedEffect effect = Assert.Single(entry.Effects);
            Assert.Equal(new CycleStateKey("resource", "sunflower", "stock"), effect.Key);
            Assert.Equal(CycleVisibleValue.Numeric(destruction ? 0 : 4, "份"), effect.After);
        });
        Assert.Throws<InvalidOperationException>(() => CycleObservedEventValidation.Validate(initial.Business, after,
            fact.WithObservedEvents([fact.ObservedEvents[0]])));
    }

    [Fact]
    public async Task Plain_daylight_changes_are_public_without_body_effects() {
        ModelTime at = Hour(17);
        CycleWorld initial = CycleWorld.CreatePlayable(at, [Waiting("a", at, Hour(20)), Waiting("b", at, Hour(20))], [Sun(at, stock: 5)]);
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: Commands());
        CycleFact fact = await Advance(kernel);
        Assert.Equal(CycleEventKind.LightChanged, fact.Kind);
        Assert.Equal(new[] { "a", "b" }, fact.ObservedEvents.Select(e => e.ObserverId));
        Assert.All(fact.ObservedEvents, entry => Assert.Equal(new CycleStateKey("world", "world", "light"), Assert.Single(entry.Effects).Key));
        AssertNoBody(fact);
    }

    [Fact]
    public async Task Expedition_without_needs_keeps_movement_and_arrival_experiences() {
        CycleWorld initial = CycleGameFactory.CreateSurvival(Hour(8), enableNeeds: false, enableWeather: false).World;
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: Commands(CycleCommand.Go("trail-1")));
        CycleFact started;
        do { started = await Advance(kernel); } while (started.Kind == CycleEventKind.DecisionContextOpened);
        Assert.Equal(CycleObservedEventKind.MoveStarted, Assert.Single(started.ObservedEvents).Kind);
        AssertNoBody(started);
        CycleFact arrived = await Advance(kernel);
        CycleObservedEvent arrival = Assert.Single(arrived.ObservedEvents);
        Assert.Equal(CycleObservedEventKind.MoveArrived, arrival.Kind);
        Assert.Equal("stream", arrival.SubjectId);
        Assert.Equal(CycleVisibleValue.Literal("stream"), Effect(arrival, "location", "a", "place").After);
        AssertNoBody(arrived);
    }

    [Fact]
    public async Task Expedition_without_needs_still_hides_distant_resource_changes() {
        CycleWorld initial = CycleGameFactory.CreateSurvival(Hour(8), enableNeeds: false, enableWeather: false).World;
        var depleted = new CycleResource("berries", "野浆果", "item.food", true, CycleTime.Hour, 6, 5, Hour(8), harvestTicks: CycleTime.Hour / 2);
        var actor = new CycleCharacter("a", "Alice", activity: new(CycleActivityKind.Wait, 1, Hour(8), 4 * CycleTime.Hour, Hour(12)), nextActivityId: 2);
        initial = CycleEngine.Rebuild(initial.Business.Replace(initial.Time, changedResources: [depleted], changedCharacters: [actor]), initial.Time);
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: Commands());
        CycleFact fact = await Advance(kernel);
        Assert.Equal(CycleEventKind.ResourceRegenerated, fact.Kind);
        Assert.Empty(fact.ObservedEvents);
    }

    private static CycleActivity Gathering(ModelTime at, ModelTime due) => new(CycleActivityKind.Gather, 1, at, due.Ticks - at.Ticks, due, "sunflower", 1);
    private static CycleCharacter Waiting(string id, ModelTime at, ModelTime due) => new(id, id,
        activity: new(CycleActivityKind.Wait, 1, at, due.Ticks - at.Ticks, due), nextActivityId: 2);
    private static CycleObservedEffect Effect(CycleObservedEvent entry, string category, string id, string field) =>
        Assert.Single(entry.Effects, effect => effect.Key == new CycleStateKey(category, id, field));
    private static void AssertNoBody(CycleFact fact) {
        Assert.DoesNotContain(fact.ObservedEvents, entry => entry.Kind == CycleObservedEventKind.BodyChanged);
        Assert.DoesNotContain(fact.ObservedEvents.SelectMany(entry => entry.Effects), effect => effect.Key.Category == "body");
    }
    private static async Task<CycleFact> Advance(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel) {
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(240)));
        return Assert.Single(kernel.LastCompletion!.Event.Facts);
    }
}
