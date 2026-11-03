using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Playground;

namespace DramaBoard.Playground.Cli.Tests;

public sealed class PathMaterialTests {
    [Theory]
    [InlineData("classic")]
    [InlineData("decision")]
    public async Task Real_queries_keep_a_self_contained_candidate_across_rejection_and_disk_reopen(string layoutId) {
        IPlayLayout layout = Layout(layoutId);
        string directory = Path.Combine(Path.GetTempPath(), "drama-path-material-" + Guid.NewGuid().ToString("N"));
        string expected;
        string candidateId;
        try {
            var initial = PlaygroundGameFactory.CreateSurvival(enableNeeds: false, enableWeather: false);
            using (var history = PlaygroundDurableHistory.Create(directory, initial.World, initial.Cursor)) {
                var kernel = PlaygroundEngine.CreateKernel(history,
                    characterController: new Commands("go e-c7", "go e-n4", "path stream", "map", "path missing", "wait 1"));
                await Until(kernel, world => HasItem(world, "path stream"));
                var accepted = kernel.World.Business.InteractionStates["a"];
                var candidate = accepted.CurrentContext!.LatestObservation.CandidatePath;
                Assert.NotNull(candidate);
                candidateId = candidate.Id;
                string candidateText = NavigationView.RenderCandidate(accepted.CurrentContext.LatestObservation);
                Assert.Contains(candidateId, candidateText);
                Assert.Contains("camp", candidateText);
                Assert.Contains("stream", candidateText);
                Assert.Contains("林间营地", candidateText);
                Assert.Contains("溪流湾", candidateText);
                Assert.Contains("e-c7", candidateText);
                Assert.Contains("60 分钟", candidateText);
                Assert.Contains("不保证安全", candidateText);
                Assert.DoesNotContain("trail-1", candidateText);
                Assert.DoesNotContain("navigate", layout.RenderActionHelp(accepted.CurrentContext.LatestObservation));

                await Until(kernel, world => HasItem(world, "map"));
                CharacterContextItem map = kernel.World.Business.InteractionStates["a"].CurrentContext!.Items.Single(item => item.RawInput == "map");
                Assert.Contains("camp", map.Result!);
                Assert.Contains("stream", map.Result!);
                Assert.Contains("e-n4", map.Result!);
                await Until(kernel, world => HasItem(world, "path missing"));
                var rejected = kernel.World.Business.InteractionStates["a"];
                Assert.Equal(candidateId, rejected.CurrentContext!.LatestObservation.CandidatePath!.Id);
                Assert.True(rejected.CurrentContext.Items.Single(item => item.RawInput == "path missing").IsRejected);
                Assert.Contains(candidateId, CharacterContextRenderer.RenderLatestState(rejected, layout));
                long sequence = rejected.CurrentContext.ContextSequence;
                await Until(kernel, world => world.Business.InteractionStates["a"].CurrentContext is {
                    Status: CharacterContextStatus.Open } current && current.ContextSequence > sequence);
                var next = kernel.World.Business.InteractionStates["a"];
                Assert.Empty(next.CurrentContext!.Items);
                expected = CharacterContextRenderer.RenderFull(next, layout);
                Assert.Contains(candidateId, expected); // A new opening is self-contained without the old query receipt.
                Assert.Contains("当前候选路径", expected);
                Assert.DoesNotContain("trail-1", expected);
            }
            using var reopened = PlaygroundDurableHistory.Open(directory);
            var saved = reopened.State.Business.InteractionStates["a"];
            Assert.Equal(candidateId, saved.CurrentContext!.LatestObservation.CandidatePath!.Id);
            Assert.Equal(expected, CharacterContextRenderer.RenderFull(saved, layout));
            Assert.Contains(candidateId, CharacterContextRenderer.RenderLatestState(saved, Layout(layoutId == "classic" ? "decision" : "classic")));
        } finally {
            if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
        }
    }

    [Theory]
    [InlineData("classic")]
    [InlineData("decision")]
    public async Task Remote_zero_segment_plan_and_candidate_clear_have_explicit_full_and_delta_meanings(string layoutId) {
        var initial = PlaygroundGameFactory.CreateSurvival(enableNeeds: false, enableWeather: false);
        var kernel = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor),
            characterController: new Commands("go e-c7", "go e-n4", "path stream from stream"));
        await Until(kernel, world => HasItem(world, "path stream from stream"));
        PlaygroundObservation planned = kernel.World.Business.InteractionStates["a"].CurrentContext!.LatestObservation;
        Assert.Equal("camp", planned.PlaceId);
        Assert.Empty(planned.CandidatePath!.Steps);
        IPlayLayout layout = Layout(layoutId);
        string material = NavigationView.RenderCandidate(planned);
        Assert.Contains("规划起终点相同，无需移动", material);
        Assert.Contains("行程合计：0 分钟", material);
        Assert.DoesNotContain("已在", material);
        Assert.DoesNotContain("已抵达", material);
        Assert.DoesNotContain("navigate", layout.RenderActionHelp(planned));

        // Isolate the view contract for a no-route result. Domain tests prove which queries clear the slot.
        PlaygroundObservation cleared = WithoutCandidate(planned);
        var delta = PlaygroundViewUpdate.Create(PlaygroundViewSnapshot.Create(planned), PlaygroundViewSnapshot.Create(cleared), [], PlayDeliveryPhase.ControllerInput);
        string rendered = layout.RenderUpdate(delta);
        Assert.Contains("当前无候选路径", rendered);
        Assert.Contains("先前被替换或清空的候选已失效", rendered);
        Assert.DoesNotContain(planned.CandidatePath.Id, rendered);
        Assert.Contains("当前无候选路径", layout.Render(cleared));
    }

    private static PlaygroundObservation WithoutCandidate(PlaygroundObservation source) => new() {
        Time = source.Time, IsDay = source.IsDay, NextLightChange = source.NextLightChange,
        Actor = source.Actor, Resources = source.Resources, HarvestDurations = source.HarvestDurations,
        ItemNames = source.ItemNames, OtherCharacters = source.OtherCharacters, Notices = source.Notices,
        Commands = source.Commands, IsExpedition = source.IsExpedition, PlaceId = source.PlaceId,
        PlaceName = source.PlaceName, PlaceDescription = source.PlaceDescription, KnownMap = source.KnownMap,
        Passages = source.Passages, WorldName = source.WorldName, CandidatePath = null
    };
    private static IPlayLayout Layout(string id) => id == "classic" ? new ClassicPlayLayout() : new DecisionPlayLayout();
    private static bool HasItem(PlaygroundWorld world, string raw) => world.Business.InteractionStates["a"].CurrentContext?.Items.Any(item => item.RawInput == raw) == true;
    private static async Task Until(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel, Func<PlaygroundWorld, bool> done) {
        for (int i = 0; i < 60; i++) {
            if (done(kernel.World)) { return; }
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
        }
        Assert.Fail("The scripted query did not reach its material boundary.");
    }
    private sealed class Commands(params string[] commands) : IPlaygroundCharacterController {
        private readonly Queue<string> _commands = new(commands);
        private int _sequence;
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) {
            string id = "path-material-" + ++_sequence;
            return ValueTask.FromResult(new PlaygroundControllerOperation(id, _commands.Dequeue(), id));
        }
    }
}
