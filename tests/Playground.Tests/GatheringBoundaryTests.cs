using DramaBoard.Kernel.Time;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class GatheringBoundaryTests {
    [Fact]
    public async Task Already_full_backpack_publishes_rejection_without_starting_gather() {
        var initial = PlaygroundWorld.CreatePlayable(characters: [new("a", "Alice", backpack: new(new Dictionary<string, int> { ["item.sunflower"] = int.MaxValue }))]);
        var history = Memory(initial);
        var command = PlaygroundCommand.Gather("sunflower");
        Assert.NotNull(PlaygroundActions.Check(initial, "a", command));
        var kernel = PlaygroundEngine.CreateKernel(history, characterController: Commands(command));
        await Step(kernel); // Commit the initial Context before testing command rejection.
        PlaygroundWorld opened = kernel.World;
        await Step(kernel);
        Assert.Equal(PlaygroundEventKind.OperationRejected, Assert.Single(history.CompletedEvents[^1].Facts).Kind);
        Assert.Null(history.PendingEvent);
        Assert.Equal(opened.Time, history.State.Time);
        Assert.Same(opened.GetCharacter("a").Backpack, history.State.GetCharacter("a").Backpack);
        Assert.Equal(opened.Resources, history.State.Resources);
        Assert.Null(history.State.GetCharacter("a").Activity);
        Assert.Equal(opened.GetCharacter("a").DecisionGeneration + 1, history.State.GetCharacter("a").DecisionGeneration);
    }
    [Fact]
    public async Task Valid_large_duration_does_not_overflow_intermediate_percentage_multiplication() {
        var start = Hour(6);
        long duration = long.MaxValue / 2;
        var kernel = PlaygroundEngine.CreateKernel(Memory(Solo(start, [Sun(start, harvestTicks: duration), Moon(start)])),
            characterController: Commands(PlaygroundCommand.Gather("sunflower")));
        await StepWorldEvent(kernel);
        Assert.Equal(duration, kernel.World.GetCharacter("a").Activity!.DurationTicks);
        Assert.Equal(new ModelTime(start.Ticks + duration), kernel.World.GetCharacter("a").Activity!.Due);
    }

    [Fact]
    public async Task Backpack_becoming_full_preserves_first_yield_and_does_not_deduct_second() {
        var start = Hour(6);
        var actor = new PlaygroundCharacter("a", "Alice", backpack: new(new Dictionary<string, int> { ["item.sunflower"] = int.MaxValue - 1 }));
        var initial = PlaygroundWorld.CreatePlayable(start, [actor]);
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Commands(PlaygroundCommand.Gather("sunflower", 2)));
        await StepWorldEvent(kernel);
        await Until(kernel, w => w.GetCharacter("a").Activity is null);
        Assert.Equal(initial.GetResource("sunflower").Stock - 1, kernel.World.GetResource("sunflower").Stock);
        Assert.Equal(int.MaxValue, kernel.World.GetCharacter("a").Backpack.Count("item.sunflower"));
        Assert.False(string.IsNullOrWhiteSpace(kernel.World.GetCharacter("a").LastResult));
    }

    [Fact]
    public void Notice_aggregation_preserves_kinds_and_actors_and_bounds_repeated_sources() {
        var actor = new PlaygroundCharacter("a", "Alice");
        actor = actor.Notify(new(PlaygroundNoticeKind.CollectorJoined, "sunflower", "b", "b:1", Hour(6), "joined"));
        PlaygroundCharacter once = actor;
        Assert.Same(once, actor.Notify(new(PlaygroundNoticeKind.CollectorJoined, "sunflower", "b", "b:1", Hour(6), "joined")));
        for (int i = 2; i <= 100; i++) {
            actor = actor.Notify(new(PlaygroundNoticeKind.CollectorJoined, "sunflower", "b", $"b:{i}", Hour(6), "joined"));
        }
        actor = actor.Notify(new(PlaygroundNoticeKind.CollectorJoined, "sunflower", "c", "c:1", Hour(6), "joined"));
        actor = actor.Notify(new(PlaygroundNoticeKind.ResourceDepleted, "sunflower", "b", "b:100:yield", Hour(7), "took final unit"));
        Assert.Equal(3, actor.Notices.Count);
        var joinedB = Assert.Single(actor.Notices, n => n.Kind == PlaygroundNoticeKind.CollectorJoined && n.ActorId == "b");
        Assert.Equal(100, joinedB.Count);
        Assert.Equal("b:100", joinedB.SourceId);
        Assert.Equal(1, Assert.Single(once.Notices).Count);
        Assert.Contains(actor.Notices, n => n.Kind == PlaygroundNoticeKind.ResourceDepleted && n.Reason == "took final unit");
    }

    [Fact]
    public async Task New_activity_consumes_old_perceptions_in_same_commit_without_resurrecting_request() {
        var start = Hour(6);
        PlaygroundNotice notice = new(PlaygroundNoticeKind.ResourceDepleted, "sunflower", null, "environment:1", start, "previous depletion");
        var initial = PlaygroundWorld.CreatePlayable(start, [new("a", "Alice", notices: [notice])]);
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Commands(PlaygroundCommand.Wait(1)));
        await StepWorldEvent(kernel);
        Assert.Empty(kernel.World.GetCharacter("a").Notices);
        Assert.NotNull(kernel.World.GetCharacter("a").Activity);
        Assert.DoesNotContain(kernel.World.Forecasts.Predictions, p => p.OwnerId == "actor:a:attention");
        Assert.Equal("a", kernel.World.Business.LastServedActorId);
        Assert.Single(initial.GetCharacter("a").Notices);
    }

    [Fact]
    public void Unknown_fact_is_rejected_by_completion_even_when_supplied_business_did_not_change() {
        var initial = PlaygroundWorld.Create();
        var unknown = new PlaygroundFact((PlaygroundEventKind)999, initial.Time);
        Assert.Throws<InvalidOperationException>(() => PlaygroundEngine.Complete(initial, new(initial.Time, 0), [unknown], initial));
        AssertPredictions(initial);
    }
}
