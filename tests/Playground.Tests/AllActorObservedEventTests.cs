using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class AllActorObservedEventTests {
    [Fact]
    public async Task Plain_gathering_records_own_yield_and_finish_without_physiology_or_private_leaks() {
        ModelTime at = Hour(8);
        PlaygroundWorld initial = PlaygroundWorld.CreatePlayable(at, [
            new("a", "Alice", activity: Gathering(at, Hour(9)), nextActivityId: 2),
            new("b", "Bob", skillLevel: 3, activity: Gathering(at, Hour(10)), nextActivityId: 2)
        ], [Sun(at, stock: 1, regenerationTicks: 8 * PlaygroundTime.Hour)]);
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Commands());

        PlaygroundFact fact = await Advance(kernel);

        Assert.Equal(PlaygroundEventKind.GatherYielded, fact.Kind);
        Assert.Equal(new[] { PlaygroundObservedEventKind.GatherYielded, PlaygroundObservedEventKind.GatherFinished }, fact.ObservedEvents.Select(e => e.Kind));
        Assert.All(fact.ObservedEvents, e => Assert.Equal("a", e.ObserverId));
        PlaygroundObservedEvent yielded = fact.ObservedEvents[0];
        Assert.Equal(PlaygroundVisibleValue.Numeric(1, "份"), Effect(yielded, "inventory", "item.sunflower", "count").After);
        Assert.Equal(PlaygroundVisibleValue.Numeric(0, "份"), Effect(yielded, "resource", "sunflower", "stock").After);
        Assert.Equal(PlaygroundVisibleValue.Numeric(1, "份"), Effect(yielded, "activity", "a", "completed").After);
        Assert.Equal(PlaygroundVisibleValue.KnownAbsent, Effect(fact.ObservedEvents[1], "activity", "a", "kind").After);
        AssertNoBody(fact);
        Assert.Single(kernel.World.GetCharacter("b").Notices);
        Assert.Equal(0, kernel.World.GetCharacter("b").Backpack.Count("item.sunflower"));
        Assert.Throws<InvalidOperationException>(() => PlaygroundEngine.Fold(initial, kernel.LastCompletion!.Event.TargetInstant, fact.WithObservedEvents([])));
    }

    [Fact]
    public async Task Other_collectors_joining_remains_a_snapshot_change_without_inventing_private_events() {
        ModelTime at = Hour(8);
        PlaygroundWorld initial = PlaygroundWorld.CreatePlayable(at, [
            new("a", "Alice", activity: Gathering(at, Hour(10)), nextActivityId: 2),
            new("b", "Bob")
        ], [Sun(at)]);
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Commands(PlaygroundCommand.Gather("sunflower")));

        PlaygroundFact fact = await Advance(kernel);

        PlaygroundObservedEvent started = Assert.Single(fact.ObservedEvents);
        Assert.Equal("b", started.ObserverId);
        Assert.Equal(PlaygroundObservedEventKind.GatherStarted, started.Kind);
        Assert.All(started.Effects, effect => Assert.Equal("b", effect.Key.ObjectId));
        Assert.Single(kernel.World.GetCharacter("a").Notices);
        AssertNoBody(fact);
    }

    [Theory]
    [InlineData(PlaygroundCommandKind.Wait, PlaygroundObservedEventKind.WaitStarted)]
    [InlineData(PlaygroundCommandKind.Cancel, PlaygroundObservedEventKind.ActivityCancelled)]
    [InlineData(PlaygroundCommandKind.Continue, PlaygroundObservedEventKind.AttentionAcknowledged)]
    public async Task Plain_character_decisions_record_their_own_effects(PlaygroundCommandKind command, PlaygroundObservedEventKind kind) {
        ModelTime at = Hour(8);
        PlaygroundActivity? activity = command == PlaygroundCommandKind.Wait ? null : Gathering(at, Hour(10));
        PlaygroundNotice notice = new(PlaygroundNoticeKind.ResourceDepleted, "sunflower", null, "fixture", at, "Resource was depleted.");
        PlaygroundWorld initial = PlaygroundWorld.CreatePlayable(at, [new("a", "Alice", activity: activity, notices: [notice], nextActivityId: 2)], [Sun(at)]);
        var selected = command switch {
            PlaygroundCommandKind.Wait => PlaygroundCommand.Wait(1),
            PlaygroundCommandKind.Cancel => PlaygroundCommand.Cancel(),
            _ => PlaygroundCommand.Continue()
        };
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Commands(selected));

        PlaygroundFact fact = await Advance(kernel);

        PlaygroundObservedEvent action = Assert.Single(fact.ObservedEvents);
        Assert.Equal(kind, action.Kind);
        Assert.Equal(PlaygroundVisibleValue.Numeric(0, "条"), Effect(action, "notice", "a", "count").After);
        AssertNoBody(fact);
    }

    [Fact]
    public async Task Plain_wait_completion_has_an_event_and_activity_removal() {
        ModelTime at = Hour(8);
        PlaygroundWorld initial = PlaygroundWorld.CreatePlayable(at, [Waiting("a", at, Hour(9))], [Sun(at, stock: 5)]);
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Commands());
        PlaygroundFact fact = await Advance(kernel);
        PlaygroundObservedEvent ended = Assert.Single(fact.ObservedEvents);
        Assert.Equal(PlaygroundObservedEventKind.WaitCompleted, ended.Kind);
        Assert.Equal(PlaygroundVisibleValue.KnownAbsent, Effect(ended, "activity", "a", "kind").After);
        AssertNoBody(fact);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Plain_public_resource_changes_are_visible_to_each_actor(bool destruction) {
        ModelTime at = Hour(8);
        PlaygroundWorld initial = PlaygroundWorld.CreatePlayable(at, [Waiting("a", at, Hour(12)), Waiting("b", at, Hour(12))], [Sun(at)]);
        PlaygroundFact planned = destruction
            ? new(PlaygroundEventKind.ResourceDestroyed, Hour(9), resourceId: "sunflower",
                changedResources: [initial.GetResource("sunflower").Change(0, 0, Hour(9))])
            : new(PlaygroundEventKind.ResourceRegenerated, Hour(9), resourceId: "sunflower",
                changedResources: [initial.GetResource("sunflower").Change(4, 0, Hour(9))]);
        PlaygroundFact fact = PlaygroundObservedEvents.Attach(initial, planned);
        PlaygroundBusinessState after = initial.Business.Replace(fact.At, changedResources: fact.ChangedResources);
        PlaygroundObservedEventValidation.Validate(initial.Business, after, fact);

        Assert.Equal(new[] { "a", "b" }, fact.ObservedEvents.Select(e => e.ObserverId));
        Assert.Equal(new[] { 0, 1 }, fact.ObservedEvents.Select(e => e.EventOrdinal));
        Assert.All(fact.ObservedEvents, entry => {
            Assert.Equal(destruction ? PlaygroundObservedEventKind.ResourceDestroyed : PlaygroundObservedEventKind.ResourceRegenerated, entry.Kind);
            PlaygroundObservedEffect effect = Assert.Single(entry.Effects);
            Assert.Equal(new PlaygroundStateKey("resource", "sunflower", "stock"), effect.Key);
            Assert.Equal(PlaygroundVisibleValue.Numeric(destruction ? 0 : 4, "份"), effect.After);
        });
        Assert.Throws<InvalidOperationException>(() => PlaygroundObservedEventValidation.Validate(initial.Business, after,
            fact.WithObservedEvents([fact.ObservedEvents[0]])));
    }

    [Fact]
    public async Task Plain_daylight_changes_are_public_without_body_effects() {
        ModelTime at = Hour(17);
        PlaygroundWorld initial = PlaygroundWorld.CreatePlayable(at, [Waiting("a", at, Hour(20)), Waiting("b", at, Hour(20))], [Sun(at, stock: 5)]);
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Commands());
        PlaygroundFact fact = await Advance(kernel);
        Assert.Equal(PlaygroundEventKind.LightChanged, fact.Kind);
        Assert.Equal(new[] { "a", "b" }, fact.ObservedEvents.Select(e => e.ObserverId));
        Assert.All(fact.ObservedEvents, entry => Assert.Equal(new PlaygroundStateKey("world", "world", "light"), Assert.Single(entry.Effects).Key));
        AssertNoBody(fact);
    }

    [Fact]
    public async Task Expedition_without_needs_keeps_movement_and_arrival_experiences() {
        PlaygroundWorld initial = PlaygroundGameFactory.CreateSurvival(Hour(8), enableNeeds: false, enableWeather: false).World;
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Commands(PlaygroundCommand.Go("trail-1")));
        PlaygroundFact started;
        do { started = await Advance(kernel); } while (started.Kind == PlaygroundEventKind.DecisionContextOpened);
        Assert.Equal(PlaygroundObservedEventKind.MoveStarted, Assert.Single(started.ObservedEvents).Kind);
        AssertNoBody(started);
        PlaygroundFact arrived = await Advance(kernel);
        PlaygroundObservedEvent arrival = Assert.Single(arrived.ObservedEvents);
        Assert.Equal(PlaygroundObservedEventKind.MoveArrived, arrival.Kind);
        Assert.Equal("stream", arrival.SubjectId);
        Assert.Equal(PlaygroundVisibleValue.Literal("stream"), Effect(arrival, "location", "a", "place").After);
        AssertNoBody(arrived);
    }

    [Fact]
    public async Task Expedition_without_needs_still_hides_distant_resource_changes() {
        PlaygroundWorld initial = PlaygroundGameFactory.CreateSurvival(Hour(8), enableNeeds: false, enableWeather: false).World;
        var depleted = new PlaygroundResource("berries", "野浆果", "item.food", true, PlaygroundTime.Hour, 6, 5, Hour(8), harvestTicks: PlaygroundTime.Hour / 2);
        var actor = new PlaygroundCharacter("a", "Alice", activity: new(PlaygroundActivityKind.Wait, 1, Hour(8), 4 * PlaygroundTime.Hour, Hour(12)), nextActivityId: 2);
        initial = PlaygroundEngine.Rebuild(initial.Business.Replace(initial.Time, changedResources: [depleted], changedCharacters: [actor]), initial.Time);
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Commands());
        PlaygroundFact fact = await Advance(kernel);
        Assert.Equal(PlaygroundEventKind.ResourceRegenerated, fact.Kind);
        Assert.Empty(fact.ObservedEvents);
    }

    private static PlaygroundActivity Gathering(ModelTime at, ModelTime due) => new(PlaygroundActivityKind.Gather, 1, at, due.Ticks - at.Ticks, due, "sunflower", 1);
    private static PlaygroundCharacter Waiting(string id, ModelTime at, ModelTime due) => new(id, id,
        activity: new(PlaygroundActivityKind.Wait, 1, at, due.Ticks - at.Ticks, due), nextActivityId: 2);
    private static PlaygroundObservedEffect Effect(PlaygroundObservedEvent entry, string category, string id, string field) =>
        Assert.Single(entry.Effects, effect => effect.Key == new PlaygroundStateKey(category, id, field));
    private static void AssertNoBody(PlaygroundFact fact) {
        Assert.DoesNotContain(fact.ObservedEvents, entry => entry.Kind == PlaygroundObservedEventKind.BodyChanged);
        Assert.DoesNotContain(fact.ObservedEvents.SelectMany(entry => entry.Effects), effect => effect.Key.Category == "body");
    }
    private static async Task<PlaygroundFact> Advance(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel) {
        await StepWorldEvent(kernel);
        return Assert.Single(kernel.LastCompletion!.Event.Facts);
    }
}
