using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.Microtown.Tests;

public sealed class TownSpatialReviewTests {
    [Fact]
    public async Task Opposing_residents_consume_their_mid_passage_contact_before_arrival() {
        var run = await RunOpposingRoutes();
        PassageContactOccurredFact contact = Assert.Single(run.CompletedEvents
            .SelectMany(item => item.Facts).SelectMany(item => item.SpatialFacts)
            .OfType<PassageContactOccurredFact>());
        Assert.Equal(PassageContactKind.HeadOnMeeting, contact.Kind);
    }

    [Fact]
    public async Task Unrelated_failure_between_contact_and_arrival_does_not_leave_a_past_prediction() {
        var run = await RunOpposingRoutes(failThirdResident: true);
        Assert.True(run.State.Business.Ended);
        Assert.True(run.State.Business.Character("c").Failed);
        Assert.Contains(run.CompletedEvents.SelectMany(item => item.Facts).SelectMany(item => item.SpatialFacts),
            fact => fact is PassageContactOccurredFact);
    }

    private static async Task<InMemoryOccurrenceHistory<TownWorld, TownFact>> RunOpposingRoutes(bool failThirdResident = false) {
        var start = TownGameFactory.Create(2);
        TownWorld world = start.World;
        if (failThirdResident) {
            TownBusinessState initial = world.Business;
            // A valid near-hunger scenario: c expires at 1.4 h, after the 1.375 h contact
            // but before the 1.5 h arrivals. This must not invalidate an unrelated route.
            long food = 14 * TownContent.Hour / 10 * TownContent.FoodDecayPerHour;
            world = TownEngine.Rebuild(new(initial.Time, initial.EndTime,
                initial.Characters.Select(actor => actor.Id == "c"
                    ? actor with { FoodReserve = food, MinimumFoodReserve = food } : actor),
                initial.Market, initial.Economy, initial.Spatial));
        }
        var scripts = new Dictionary<string, Queue<string>> {
            ["a"] = new(["go market-workshop", "wait 1", "go market-workshop"]),
            ["b"] = new(["go market-home", "go home-workshop", "go market-workshop",
                "go market-home", "go market-home", "go market-workshop"]),
            ["c"] = new(["wait 2"])
        };
        var history = new InMemoryOccurrenceHistory<TownWorld, TownFact>(world, start.Cursor);
        var kernel = TownEngine.CreateKernel(history, (decision, _) => ValueTask.FromResult(
            TownCommand.Parse(scripts[decision.ActorId].TryDequeue(out string? command) ? command : "wait 2")));
        for (int i = 0; i < 100 && !history.State.Business.Ended; i++) {
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(world.Business.EndTime));
        }
        Assert.True(history.State.Business.Ended);
        return history;
    }
}
