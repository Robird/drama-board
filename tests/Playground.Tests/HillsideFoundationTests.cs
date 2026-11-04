using DramaBoard.Kernel.Simulation;
using DramaBoard.Spatial;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class HillsideFoundationTests {
    [Fact]
    public void Default_residents_start_in_one_sheltered_world_with_finite_visible_supplies() {
        var initial = PlaygroundGameFactory.CreateLivingWorld();
        Assert.Equal(Hour(8), initial.World.Time);
        Assert.Equal("hillside-v1", initial.World.Expedition!.ScenarioId);
        Assert.Equal(3, initial.World.Characters.Count);
        Assert.All(initial.World.Characters, actor => {
            Assert.True(actor.IsActive);
            Assert.Equal("home", PlaygroundActions.CurrentPlace(initial.World, actor.Id));
            Assert.Equal(3, actor.Backpack.Count("item.food"));
            Assert.Equal(3, actor.Backpack.Count("item.water"));
            PlaygroundObservation observation = PlaygroundDecisionContext.Observe(initial.World, actor.Id);
            Assert.True(observation.Survival!.Sheltered);
            Assert.Equal(80m, observation.Survival.Food);
            Assert.DoesNotContain(observation.Memories, memory => memory.PlaceId == "stream");
        });
        initial.World.Validate();
    }

    [Fact]
    public async Task Two_town_mountain_round_trips_with_needs_weather_and_cold_open_preserve_one_resident() {
        using var store = new TemporaryStore();
        var initial = PlaygroundGameFactory.CreateLivingWorld(characterCount: 1);
        string firstState;
        string firstMaterial;
        long firstExperience;
        long firstTransition;
        using (var history = PlaygroundDurableHistory.Create(store.StorePath, initial.World, initial.Cursor)) {
            var controller = new JourneyController("first", RoundTrip());
            var kernel = PlaygroundEngine.CreateKernel(history, characterController: controller);
            await CompleteRoundTrip(kernel);
            Assert.True(kernel.World.GetCharacter("a").IsActive);
            Assert.NotNull(kernel.World.Expedition!.Environment);
            Assert.Contains(kernel.World.Expedition.Visits, visit => visit.PlaceId == "stream" && visit.ActorId == "a");
            Assert.Equal(PlaygroundExpeditionSummary.Exploring, kernel.World.ExpeditionSummary);
            Assert.Contains(history.ReadCommittedOperations(Enumerable.Range(1, controller.Calls).Select(i => $"first/fixture/{i}")),
                operation => operation.Result.Contains("采集", StringComparison.Ordinal));
            firstState = kernel.World.DescribeState();
            firstMaterial = MaterialSnapshot(kernel.World.Business.InteractionStates);
            firstExperience = kernel.World.Business.InteractionStates["a"].NextExperienceSequence;
            firstTransition = kernel.Cursor.Version.TransitionCount;
        }
        using (var history = PlaygroundDurableHistory.Open(store.StorePath)) {
            Assert.Null(history.PendingEvent);
            Assert.Equal(firstState, history.State.DescribeState());
            Assert.Equal(firstMaterial, MaterialSnapshot(history.State.Business.InteractionStates));
            // A new controller has no hidden continuation state: this is an ordinary second journey.
            var kernel = PlaygroundEngine.CreateKernel(history, characterController: new JourneyController("second", RoundTrip()));
            Assert.False(kernel.RecoverPending());
            await CompleteRoundTrip(kernel);
            Assert.True(kernel.World.GetCharacter("a").IsActive);
            Assert.Equal("home", PlaygroundActions.CurrentPlace(kernel.World, "a"));
            Assert.True(kernel.Cursor.Version.TransitionCount > firstTransition);
            Assert.True(kernel.World.Business.InteractionStates["a"].NextExperienceSequence > firstExperience);
            Assert.All(new[] { "home", "market", "road", "camp", "stream" }, id =>
                Assert.Contains(kernel.World.Expedition!.Visits, visit => visit.ActorId == "a" && visit.PlaceId == id));
        }
    }

    [Fact]
    public void Old_content_identity_is_rejected_before_running_the_new_map() {
        PlaygroundWorld world = PlaygroundGameFactory.CreateLivingWorld().World;
        PlaygroundExpedition current = world.Expedition!;
        var obsolete = new PlaygroundExpedition("lost-mountain-v1", current.Spatial, current.Visits, current.Environment);
        Assert.Throws<InvalidOperationException>(() => world.Business.Replace(world.Time, expedition: obsolete).Validate());
    }

    private static StubCharacterController RoundTrip() {
        string[] outbound = ["home", "market", "road", "camp", "stream"];
        var commands = new Queue<PlaygroundCommand>(outbound.Zip(outbound.Skip(1), (a, b) => PlaygroundCommand.Go(Passage(a, b)))
            .Concat([PlaygroundCommand.Gather("berries", 1), PlaygroundCommand.Gather("water", 1)])
            .Concat(outbound.Reverse().Zip(outbound.Reverse().Skip(1), (a, b) => PlaygroundCommand.Go(Passage(a, b)))));
        return new((observation, _) => {
            if (observation.Survival!.Food <= 65 && observation.Actor.Backpack.Count("item.food") > 0) {
                return PlaygroundCommand.Use("item.food");
            }
            if (observation.Survival.Water <= 55 && observation.Actor.Backpack.Count("item.water") > 0) {
                return PlaygroundCommand.Use("item.water");
            }
            if (observation.Actor.Activity is not null || observation.ArrivalDue is not null) {
                return PlaygroundCommand.Continue();
            }
            return commands.Count > 0 ? commands.Dequeue() : throw new InvalidOperationException("Unexpected decision after the completed journey.");
        });
    }

    private static async Task CompleteRoundTrip(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel) {
        bool leftHome = false;
        for (int step = 0; step < 300; step++) {
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(72)));
            Assert.True(kernel.World.GetCharacter("a").IsActive);
            AssertForecasts(PlaygroundEngine.Rebuild(kernel.World.Business, kernel.World.Time).Forecasts, kernel.World.Forecasts);
            string? place = PlaygroundActions.CurrentPlace(kernel.World, "a");
            leftHome |= place != "home";
            if (leftHome && place == "home") { return; }
        }
        Assert.Fail("The finite round trip did not finish.");
    }

    private sealed class JourneyController(string session, StubCharacterController inner) : IPlaygroundCharacterController {
        public int Calls => inner.Calls;
        public async ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) {
            PlaygroundControllerOperation operation = await inner.ChooseCommandAsync(context, cancellationToken);
            return operation with { ToolCallId = session + "/" + operation.ToolCallId, OperationRecordId = session + "/" + operation.OperationRecordId };
        }
    }

    private static string Passage(string from, string to) => PlaygroundExpeditionMap.GetEntrances(from).Single(passage => passage.To == to).EntranceId;
}
