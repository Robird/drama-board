using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class SurvivalRegenerationTests {
    [Fact]
    public async Task Morning_explorer_can_harvest_eat_and_wait_for_natural_regeneration_across_weather_and_cold_open() {
        // A directed mechanism probe using the production scenario, not a blind playthrough.
        var initial = CycleGameFactory.CreateSurvival(Hour(8));
        string passage = Assert.Single(CycleExpeditionMap.GetPassages("camp"), p => p.To == "stream").Id;
        var controller = new CheckedScriptController(
            CycleCommand.Go(passage), CycleCommand.Continue(), CycleCommand.Gather("berries", 2),
            CycleCommand.Use("item.food"), CycleCommand.Use("item.water"), CycleCommand.Wait(3));
        using var store = new TemporaryStore();
        CycleWorld checkpoint;
        KernelCursor checkpointCursor;
        List<CycleFact> facts = [];
        List<(ModelTime At, int Stock, int Food)> yields = [];
        using (var history = CycleDurableHistory.Create(store.StorePath, initial.World, initial.Cursor)) {
            var kernel = CycleEngine.CreateKernel(history, characterController: controller);
            bool regenerated = false;
            for (int steps = 0; steps < 40; steps++) {
                Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(13)));
                kernel.World.Validate();
                CycleFact fact = Assert.Single(kernel.LastCompletion!.Event.Facts);
                facts.Add(fact);
                CycleResource berries = kernel.World.GetResource("berries");
                if (fact.Kind == CycleEventKind.GatherYielded) {
                    yields.Add((fact.At, berries.Stock, kernel.World.GetCharacter("a").Backpack.Count("item.food")));
                }
                if (fact.Kind == CycleEventKind.ResourceRegenerated) {
                    Assert.Equal("berries", fact.ResourceId);
                    regenerated = true;
                    break;
                }
                if (berries.Stock < berries.Capacity) {
                    // Second harvest, supply decisions and weather must preserve the first depletion's timer.
                    Assert.Equal(Minute(570), berries.ActiveSince);
                    Assert.Equal(0, berries.AccumulatedTicks);
                    Assert.Equal(Minute(750), Assert.Single(kernel.World.Forecasts.Predictions,
                        p => p.OwnerId == "resource:berries:regen").Due);
                }
            }
            Assert.True(regenerated, "The production scenario did not regenerate within the bounded script.");
            Assert.Equal(new[] { (Minute(570), 5, 1), (Hour(10), 4, 2) }, yields);
            Assert.Equal(Hour(9), Assert.Single(facts, f => f.Kind == CycleEventKind.SurvivalWarning).At);
            Assert.Equal(Hour(9), Assert.Single(facts, f => f.Kind == CycleEventKind.AttentionAcknowledged).At);
            Assert.Equal(new[] { Hour(10), Hour(12) }, facts
                .Where(f => f.Kind == CycleEventKind.WeatherResolved).Select(f => f.At));
            Assert.All(facts.Where(f => f.Kind == CycleEventKind.ItemUsed), f => Assert.Equal(Hour(10), f.At));
            Assert.Equal(2, facts.Count(f => f.Kind == CycleEventKind.ItemUsed));
            Assert.Equal(6, controller.Calls);
            Assert.Equal(0, controller.Remaining);

            checkpoint = kernel.World;
            checkpointCursor = kernel.Cursor;
            Assert.Equal(Minute(750), checkpoint.Time);
            Assert.Equal("stream", CycleActions.CurrentPlace(checkpoint, "a"));
            Assert.Equal(CycleExpeditionOutcome.Exploring, checkpoint.Expedition!.Outcome);
            Assert.Equal(2, checkpoint.Expedition.Environment!.Sequence);
            Assert.Equal(CycleEnvironmentRules.DefaultSeed, checkpoint.Expedition.Environment.Seed);
            Assert.Equal(5, checkpoint.GetResource("berries").Stock);
            Assert.Equal(Minute(930), Assert.Single(checkpoint.Forecasts.Predictions,
                p => p.OwnerId == "resource:berries:regen").Due);
            CycleCharacter actor = checkpoint.GetCharacter("a");
            // The resource regained one unit; owned food is still two harvested minus one eaten.
            Assert.Equal(1, actor.Backpack.Count("item.food"));
            Assert.Equal(0, actor.Backpack.Count("item.water"));
            Assert.Equal(Hour(13), actor.Activity!.Due);
            Assert.Equal(CycleActivityKind.Wait, actor.Activity.Kind);
            Assert.Equal(0, actor.Survival!.PendingMask);
            CycleSurvival body = actor.Survival.Project(checkpoint.Time);
            Assert.Equal(85 * CycleSurvival.Scale / 2, body.FoodUnits);
            Assert.Equal(54 * CycleSurvival.Scale, body.WaterUnits);
            Assert.Null(history.PendingEvent);
        }

        // Rebuild from an actual S-only disk boundary, then compare the next natural occurrence.
        using var reopened = CycleDurableHistory.Open(store.StorePath);
        Assert.Null(reopened.PendingEvent);
        Assert.Equal(checkpoint.DescribeState(), reopened.State.DescribeState());
        Assert.Equal(checkpoint.Expedition!.Spatial, reopened.State.Expedition!.Spatial);
        Assert.Equal(checkpointCursor, reopened.Cursor);
        var uninterrupted = CycleEngine.CreateKernel(
            new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(checkpoint, checkpointCursor), characterController: controller);
        var resumed = CycleEngine.CreateKernel(reopened, characterController: controller);
        Assert.Equal(StepStatus.Committed, await uninterrupted.StepAsync(Hour(13)));
        Assert.Equal(StepStatus.Committed, await resumed.StepAsync(Hour(13)));
        Assert.Equal(CycleEventKind.WaitCompleted, Assert.Single(resumed.LastCompletion!.Event.Facts).Kind);
        Assert.Equal(Describe(uninterrupted.LastCompletion!.Event), Describe(resumed.LastCompletion.Event));
        Assert.Equal(uninterrupted.World.DescribeState(), resumed.World.DescribeState());
        Assert.Equal(uninterrupted.World.Expedition!.Spatial, resumed.World.Expedition!.Spatial);
        Assert.Equal(uninterrupted.Cursor, resumed.Cursor);
        Assert.Equal(6, controller.Calls);
        Assert.Equal(5, resumed.World.GetResource("berries").Stock);
        Assert.Equal(1, resumed.World.GetCharacter("a").Backpack.Count("item.food"));
    }

    private sealed class CheckedScriptController(params CycleCommand[] commands) : ICycleCharacterController {
        private readonly Queue<CycleCommand> _remaining = new(commands);
        public int Calls { get; private set; }
        public int Remaining => _remaining.Count;

        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) {
            Assert.NotEmpty(_remaining);
            CycleCommand command = _remaining.Dequeue();
            Assert.Null(context.Check(command));
            Calls++;
            return ValueTask.FromResult(Operation(command, Calls));
        }
    }
}
