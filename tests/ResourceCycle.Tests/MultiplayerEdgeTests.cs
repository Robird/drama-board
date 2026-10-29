using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.ResourceCycle;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class MultiplayerEdgeTests {
    [Fact]
    public async Task Same_due_competition_has_one_winner_unchanged_by_attention_or_an_unrelated_completion() {
        string? establishedWinner = null;
        foreach (bool attention in new[] { false, true }) {
            foreach (bool unrelated in new[] { false, true }) {
                var notices = attention
                    ? new[] { new CycleNotice(CycleNoticeKind.CollectorJoined, "sunflower", "b", "b:1", Hour(16), "Bob joined.") }
                    : [];
                List<CycleCharacter> actors = [
                    new("a", "Alice", activity: Gathering(1, Hour(16), Hour(17)), notices: notices, nextActivityId: 2),
                    new("b", "Bob", activity: Gathering(1, Hour(16), Hour(17)), nextActivityId: 2)
                ];
                if (unrelated) {
                    actors.Add(new("c", "Carol", activity: new(CycleActivityKind.Wait, 1, Hour(16),
                        CycleTime.Hour, Hour(17)), nextActivityId: 2));
                }
                CycleWorld initial = CycleWorld.CreatePlayable(Hour(16), actors,
                    [Sun(Hour(16), stock: 1), Moon(Hour(16))]);
                var source = new StubCharacterController((world, id) => {
                    Assert.Equal(Hour(16), world.Time);
                    Assert.Equal("a", id);
                    Assert.NotNull(world.Actor.Activity);
                    return CycleCommand.Continue();
                });
                using var directory = new TemporaryStore();
                using (CycleDurableHistory.Create(directory.StorePath, initial, InitialCursor(initial))) { }
                CycleWorld final = initial;
                for (int index = 0; index < 8; index++) {
                    // Every step is a real cold-open; no session-local service cursor or candidate cache survives.
                    using var history = CycleDurableHistory.Open(directory.StorePath);
                    var kernel = CycleEngine.CreateKernel(history, characterController: source);
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
                CycleCharacter loser = final.GetCharacter(winner == "a" ? "b" : "a");
                Assert.NotNull(CycleActions.Check(final, loser.Id, CycleCommand.Continue()));
                CycleNotice depletion = Assert.Single(loser.Notices);
                Assert.Equal(CycleNoticeKind.ResourceDepleted, depletion.Kind);
                Assert.Equal(winner, depletion.ActorId);
            }
        }
    }

    [Fact]
    public async Task Cancel_then_restart_rejects_the_old_candidate_and_cold_open_yields_only_once() {
        CycleActivity oldActivity = Gathering(1, Hour(16), Hour(17));
        var notice = new CycleNotice(CycleNoticeKind.CollectorJoined, "sunflower", "b", "b:1", Hour(16), "Bob previously joined.");
        CycleWorld initial = CycleWorld.CreatePlayable(Hour(16), [
            new("a", "Alice", activity: oldActivity, notices: [notice], nextActivityId: 2),
            new("b", "Bob", activity: new(CycleActivityKind.Wait, 1, Hour(16), 2 * CycleTime.Hour, Hour(18)), nextActivityId: 2)
        ], [Sun(Hour(16), stock: 2), Moon(Hour(16))]);
        CyclePrediction stale = Assert.Single(initial.Forecasts.Predictions, p => p.OwnerId == "actor:a:activity");
        using var directory = new TemporaryStore();
        using (var history = CycleDurableHistory.Create(directory.StorePath, initial, InitialCursor(initial))) {
            var kernel = CycleEngine.CreateKernel(history,
                characterController: Commands(CycleCommand.Cancel(), CycleCommand.Gather("sunflower")));
            await StepWorldEvent(kernel);
            Assert.Null(kernel.World.GetCharacter("a").Activity);
            Assert.DoesNotContain(kernel.World.Forecasts.Predictions, p => p.OwnerId == stale.OwnerId);
            await StepWorldEvent(kernel);
            CycleActivity restarted = kernel.World.GetCharacter("a").Activity!;
            Assert.NotEqual(oldActivity.Id, restarted.Id);
            Assert.Equal(oldActivity.Due, restarted.Due);
            CyclePrediction replacement = Assert.Single(kernel.World.Forecasts.Predictions, p => p.OwnerId == stale.OwnerId);
            Assert.NotEqual(stale.ToString(), replacement.ToString());
            CycleWorld corrupted = new(kernel.World.Business, kernel.World.Forecasts.WithPredictions(
                kernel.World.Forecasts.Predictions.Where(p => p.OwnerId != stale.OwnerId).Append(stale)));
            Assert.Throws<InvalidOperationException>(() => CycleEngine.Validate(corrupted, corrupted.Time));
        }

        using var reopened = CycleDurableHistory.Open(directory.StorePath);
        var resumed = CycleEngine.CreateKernel(reopened, characterController: Commands(CycleCommand.Wait(24)));
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
        ModelTime start = new(long.MaxValue - CycleTime.Hour - 60_000);
        ModelTime due = new(long.MaxValue - 60_000);
        var activity = new CycleActivity(CycleActivityKind.Gather, 1, start, CycleTime.Hour, due,
            "sunflower", targetCount: 2);
        CycleWorld initial = HorizonWorld(start, new("a", "Alice", activity: activity, nextActivityId: 2));
        using var directory = new TemporaryStore();
        string completedState;
        using (var history = CycleDurableHistory.Create(directory.StorePath, initial, InitialCursor(initial))) {
            var kernel = CycleEngine.CreateKernel(history, characterController: Commands());
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(due));
            Assert.Equal(due, kernel.World.Time);
            Assert.Equal(1, kernel.World.GetCharacter("a").Backpack.Count("item.sunflower"));
            Assert.Equal(1, kernel.World.GetResource("sunflower").Stock);
            Assert.Null(kernel.World.GetCharacter("a").Activity);
            Assert.Contains("时间", kernel.World.GetCharacter("a").LastResult);
            completedState = kernel.World.DescribeState();
        }
        using var reopened = CycleDurableHistory.Open(directory.StorePath);
        Assert.Equal(completedState, reopened.State.DescribeState());
        Assert.Null(reopened.PendingEvent);
    }

    [Fact]
    public void Initial_activity_due_overflow_is_a_displayable_rejection_without_mutation() {
        CycleWorld initial = HorizonWorld(new(long.MaxValue - 60_000), new("a", "Alice"));
        string before = initial.DescribeState();
        Assert.NotNull(CycleActions.Check(initial, "a", CycleCommand.Gather("sunflower", 2)));
        Assert.NotNull(CycleActions.Check(initial, "a", CycleCommand.Wait(1)));
        Assert.Equal(before, initial.DescribeState());
    }

    private static CycleActivity Gathering(long id, ModelTime start, ModelTime due) =>
        new(CycleActivityKind.Gather, id, start, due.Ticks - start.Ticks, due, "sunflower", targetCount: 1);

    private static CycleWorld HorizonWorld(ModelTime time, CycleCharacter actor) {
        // Finite horizon fixture: light changes after the tested deadline; the full daylight plant is dormant.
        // No regeneration forecast overflows and masks the independently tested activity overflow.
        var resource = new CycleResource("sunflower", "Sunflower", "item.sunflower", true,
            3 * CycleTime.Hour, 2, 2);
        var business = new CycleBusinessState(time, new(false, new(long.MaxValue)), [resource], new([]), [actor]);
        return CycleEngine.Rebuild(business, time);
    }
}
