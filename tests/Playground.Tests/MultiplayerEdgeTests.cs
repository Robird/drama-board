using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Playground;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class MultiplayerEdgeTests {
    [Fact]
    public async Task Same_due_competition_has_one_winner_unchanged_by_attention_or_an_unrelated_completion() {
        string? establishedWinner = null;
        foreach (bool attention in new[] { false, true }) {
            foreach (bool unrelated in new[] { false, true }) {
                var notices = attention
                    ? new[] { new PlaygroundNotice(PlaygroundNoticeKind.CollectorJoined, "sunflower", "b", "b:1", Hour(16), "Bob joined.") }
                    : [];
                List<PlaygroundCharacter> actors = [
                    new("a", "Alice", activity: Gathering(1, Hour(16), Hour(17)), notices: notices, nextActivityId: 2),
                    new("b", "Bob", activity: Gathering(1, Hour(16), Hour(17)), nextActivityId: 2)
                ];
                if (unrelated) {
                    actors.Add(new("c", "Carol", activity: new(PlaygroundActivityKind.Wait, 1, Hour(16),
                        PlaygroundTime.Hour, Hour(17)), nextActivityId: 2));
                }
                PlaygroundWorld initial = PlaygroundWorld.CreatePlayable(Hour(16), actors,
                    [Sun(Hour(16), stock: 1), Moon(Hour(16))]);
                var source = new StubCharacterController((world, id) => {
                    Assert.Equal(Hour(16), world.Time);
                    Assert.Equal("a", id);
                    Assert.NotNull(world.Actor.Activity);
                    return PlaygroundCommand.Continue();
                });
                using var directory = new TemporaryStore();
                using (PlaygroundDurableHistory.Create(directory.StorePath, initial, InitialCursor(initial))) { }
                PlaygroundWorld final = initial;
                for (int index = 0; index < 8; index++) {
                    // Every step is a real cold-open; no session-local service cursor or candidate cache survives.
                    using var history = PlaygroundDurableHistory.Open(directory.StorePath);
                    var kernel = PlaygroundEngine.CreateKernel(history, characterController: source);
                    await StepWorldEvent(kernel);
                    final = kernel.World;
                    if (final.GetCharacter("a").Activity is null && final.GetCharacter("b").Activity is null) { break; }
                }

                Assert.Equal(Hour(17), final.Time);
                Assert.Null(final.GetCharacter("a").Activity);
                Assert.Null(final.GetCharacter("b").Activity);
                Assert.Equal(0, final.GetResource("sunflower").Stock);
                Assert.Equal(1, final.Characters.Sum(actor => actor.Backpack.Count("item.sunflower")));
                string winner = Assert.Single(final.Characters, actor => actor.Backpack.Count("item.sunflower") == 1).Id;
                establishedWinner ??= winner;
                Assert.Equal(establishedWinner, winner);
                Assert.Equal(attention ? 1 : 0, source.Calls);
                PlaygroundCharacter loser = final.GetCharacter(winner == "a" ? "b" : "a");
                Assert.NotNull(PlaygroundActions.Check(final, loser.Id, PlaygroundCommand.Continue()));
                PlaygroundNotice depletion = Assert.Single(loser.Notices);
                Assert.Equal(PlaygroundNoticeKind.ResourceDepleted, depletion.Kind);
                Assert.Equal(winner, depletion.ActorId);
            }
        }
    }

    [Fact]
    public async Task Cancel_then_restart_rejects_the_old_candidate_and_cold_open_yields_only_once() {
        PlaygroundActivity oldActivity = Gathering(1, Hour(16), Hour(17));
        var notice = new PlaygroundNotice(PlaygroundNoticeKind.CollectorJoined, "sunflower", "b", "b:1", Hour(16), "Bob previously joined.");
        PlaygroundWorld initial = PlaygroundWorld.CreatePlayable(Hour(16), [
            new("a", "Alice", activity: oldActivity, notices: [notice], nextActivityId: 2),
            new("b", "Bob", activity: new(PlaygroundActivityKind.Wait, 1, Hour(16), 2 * PlaygroundTime.Hour, Hour(18)), nextActivityId: 2)
        ], [Sun(Hour(16), stock: 2), Moon(Hour(16))]);
        PlaygroundPrediction stale = Assert.Single(initial.Forecasts.Predictions, p => p.OwnerId == "actor:a:activity");
        using var directory = new TemporaryStore();
        using (var history = PlaygroundDurableHistory.Create(directory.StorePath, initial, InitialCursor(initial))) {
            var kernel = PlaygroundEngine.CreateKernel(history,
                characterController: Commands(PlaygroundCommand.Cancel(), PlaygroundCommand.Gather("sunflower")));
            await StepWorldEvent(kernel);
            Assert.Null(kernel.World.GetCharacter("a").Activity);
            Assert.DoesNotContain(kernel.World.Forecasts.Predictions, p => p.OwnerId == stale.OwnerId);
            await StepWorldEvent(kernel);
            PlaygroundActivity restarted = kernel.World.GetCharacter("a").Activity!;
            Assert.NotEqual(oldActivity.Id, restarted.Id);
            Assert.Equal(oldActivity.Due, restarted.Due);
            PlaygroundPrediction replacement = Assert.Single(kernel.World.Forecasts.Predictions, p => p.OwnerId == stale.OwnerId);
            Assert.NotEqual(stale.ToString(), replacement.ToString());
            PlaygroundWorld corrupted = new(kernel.World.Business, kernel.World.Forecasts.WithPredictions(
                kernel.World.Forecasts.Predictions.Where(p => p.OwnerId != stale.OwnerId).Append(stale)));
            Assert.Throws<InvalidOperationException>(() => PlaygroundEngine.Validate(corrupted, corrupted.Time));
        }

        using var reopened = PlaygroundDurableHistory.Open(directory.StorePath);
        var resumed = PlaygroundEngine.CreateKernel(reopened, characterController: Commands(PlaygroundCommand.Wait(24)));
        await StepWorldEvent(resumed);
        Assert.Equal(Hour(17), resumed.World.Time);
        Assert.Equal(1, resumed.World.GetCharacter("a").Backpack.Count("item.sunflower"));
        Assert.Equal(1, resumed.World.GetResource("sunflower").Stock);
        Assert.Null(resumed.World.GetCharacter("a").Activity);
        await StepWorldEvent(resumed);
        Assert.Equal(StepStatus.BoundaryReached, await resumed.StepAsync(Minute(17 * 60 + 30)));
        Assert.Equal(1, resumed.World.GetCharacter("a").Backpack.Count("item.sunflower"));
    }

    [Fact]
    public async Task Next_cycle_time_overflow_keeps_the_successful_yield_and_finishes_the_batch() {
        ModelTime start = new(long.MaxValue - PlaygroundTime.Hour - 60_000);
        ModelTime due = new(long.MaxValue - 60_000);
        var activity = new PlaygroundActivity(PlaygroundActivityKind.Gather, 1, start, PlaygroundTime.Hour, due,
            "sunflower", targetCount: 2);
        PlaygroundWorld initial = HorizonWorld(start, new("a", "Alice", activity: activity, nextActivityId: 2));
        using var directory = new TemporaryStore();
        string completedState;
        using (var history = PlaygroundDurableHistory.Create(directory.StorePath, initial, InitialCursor(initial))) {
            var kernel = PlaygroundEngine.CreateKernel(history, characterController: Commands());
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(due));
            Assert.Equal(due, kernel.World.Time);
            Assert.Equal(1, kernel.World.GetCharacter("a").Backpack.Count("item.sunflower"));
            Assert.Equal(1, kernel.World.GetResource("sunflower").Stock);
            Assert.Null(kernel.World.GetCharacter("a").Activity);
            Assert.Contains("时间", kernel.World.GetCharacter("a").LastResult);
            completedState = kernel.World.DescribeState();
        }
        using var reopened = PlaygroundDurableHistory.Open(directory.StorePath);
        Assert.Equal(completedState, reopened.State.DescribeState());
        Assert.Null(reopened.PendingEvent);
    }

    [Fact]
    public void Initial_activity_due_overflow_is_a_displayable_rejection_without_mutation() {
        PlaygroundWorld initial = HorizonWorld(new(long.MaxValue - 60_000), new("a", "Alice"));
        string before = initial.DescribeState();
        Assert.NotNull(PlaygroundActions.Check(initial, "a", PlaygroundCommand.Gather("sunflower", 2)));
        Assert.NotNull(PlaygroundActions.Check(initial, "a", PlaygroundCommand.Wait(1)));
        Assert.Equal(before, initial.DescribeState());
    }

    private static PlaygroundActivity Gathering(long id, ModelTime start, ModelTime due) =>
        new(PlaygroundActivityKind.Gather, id, start, due.Ticks - start.Ticks, due, "sunflower", targetCount: 1);

    private static PlaygroundWorld HorizonWorld(ModelTime time, PlaygroundCharacter actor) {
        // Finite horizon fixture: light changes after the tested deadline; the full daylight plant is dormant.
        // No regeneration forecast overflows and masks the independently tested activity overflow.
        var resource = new PlaygroundResource("sunflower", "Sunflower", "item.sunflower", true,
            3 * PlaygroundTime.Hour, 2, 2);
        var business = new PlaygroundBusinessState(time, new(false, new(long.MaxValue)), [resource], new([]), [actor]);
        return PlaygroundEngine.Rebuild(business, time);
    }
}
