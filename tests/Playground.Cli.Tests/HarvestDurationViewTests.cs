using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Playground;

namespace DramaBoard.Playground.Cli.Tests;

public sealed class HarvestDurationViewTests {
    [Theory]
    [InlineData(0, 3_600_000, 3_600_000, "60")]
    [InlineData(1, 3_600_000, 2_880_000, "48")]
    [InlineData(2, 3_600_000, 2_400_000, "40")]
    [InlineData(3, 3_600_001, 2_057_144, "34.29")]
    public async Task Actual_gather_and_cold_context_use_the_observed_cycle_in_both_layouts(
        int skill, long baseTicks, long expectedTicks, string expectedMinutes) {
        using var files = new Files();
        var initial = PlaygroundGameFactory.Create(Definition(skill, baseTicks));
        var controller = new GatherController();
        var expected = new Dictionary<string, string>();
        using (var history = PlaygroundDurableHistory.Create(files.Path, initial.World, initial.Cursor)) {
            var kernel = PlaygroundEngine.CreateKernel(history, characterController: controller);
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(24 * PlaygroundTime.Hour)));
            var observed = kernel.World.Business.InteractionStates["a"].CurrentContext!.Opening.Observation;
            Assert.Equal(expectedTicks, observed.HarvestDurations["well"]);
            Assert.Equal(skill, observed.Actor.SkillLevel);

            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(24 * PlaygroundTime.Hour)));
            var activity = kernel.World.GetCharacter("a").Activity!;
            Assert.Equal(PlaygroundActivityKind.Gather, activity.Kind);
            Assert.Equal("well", activity.ResourceId);
            Assert.Equal(expectedTicks, activity.DurationTicks);
            Assert.Equal(observed.Time.Ticks + expectedTicks, activity.Due.Ticks);
            Assert.Equal(expectedTicks, observed.HarvestDurations["well"]);

            var state = kernel.World.Business.InteractionStates["a"];
            foreach (IPlayLayout layout in Layouts()) {
                expected[layout.Id] = CharacterContextRenderer.RenderFull(state, layout);
                AssertCost(expected[layout.Id], expectedMinutes);
            }
        }

        using var reopened = PlaygroundDurableHistory.Open(files.Path);
        var restored = reopened.State.Business.InteractionStates["a"];
        Assert.Equal(expectedTicks, restored.CurrentContext!.Opening.Observation.HarvestDurations["well"]);
        Assert.Equal(expectedTicks, reopened.State.GetCharacter("a").Activity!.DurationTicks);
        foreach (IPlayLayout layout in Layouts()) {
            Assert.Equal(expected[layout.Id], CharacterContextRenderer.RenderFull(restored, layout));
            AssertCost(CharacterContextRenderer.RenderLatestState(restored, layout), expectedMinutes);
        }
        Assert.Equal(1, controller.Calls);
    }

    [Theory]
    [InlineData("classic")]
    [InlineData("decision")]
    public void Saved_cycle_changes_with_the_same_skill_flow_through_full_delta_and_latest_views(string layoutId) {
        var initial = PlaygroundGameFactory.Create(Definition(1, PlaygroundTime.Hour));
        // Presentation fixtures can represent costs captured under different rules without changing skill.
        // They are not installed into a live Business state or submitted to its domain validator.
        var before = WithDuration(PlaygroundDecisionContext.Observe(initial.World, "a"), 47 * 60_000);
        var after = WithDuration(before, 11 * 60_000);
        var opening = ViewContextFixture.State(before).CurrentContext!.Opening;
        var state = new CharacterInteractionState("a", new CharacterContext(1, CharacterContextStatus.Open,
            opening, [new CharacterContextItem(1, observation: after)], nextItemSequence: 2));
        IPlayLayout layout = Layouts().Single(layout => layout.Id == layoutId);

        string full = CharacterContextRenderer.RenderFull(state, layout);
        AssertCost(full, "47");
        Assert.Contains("11分钟", full);
        string latest = CharacterContextRenderer.RenderLatestState(state, layout);
        AssertCost(latest, "11");
        Assert.DoesNotContain("47 分钟", latest);

        var update = PlaygroundViewUpdate.Create(PlaygroundViewSnapshot.Create(before), PlaygroundViewSnapshot.Create(after), [],
            PlayDeliveryPhase.ControllerInput);
        Assert.Empty(update.Events);
        Assert.Equal(11 * 60_000, update.CurrentValues[new("resource", "well", "harvest")].Units);
        Assert.Contains("11分钟", layout.RenderUpdate(update));
        Assert.Equal(47 * 60_000, before.HarvestDurations["well"]);
        Assert.Equal(before.Actor.SkillLevel, after.Actor.SkillLevel);
    }

    private static void AssertCost(string text, string minutes) {
        Assert.Contains(text.Split('\n'), line => line.Contains("`well`", StringComparison.Ordinal) &&
            line.Contains(minutes + " 分钟", StringComparison.Ordinal));
        // No global multiplier is frozen here; unrelated percentages elsewhere in a view are allowed.
        foreach (string line in text.Split('\n').Where(line => line.Contains("采药等级", StringComparison.Ordinal) ||
            line.Contains("采集速度", StringComparison.Ordinal) || line.Contains("基础速度", StringComparison.Ordinal))) {
            Assert.DoesNotMatch(@"\d+(?:[.,]\d+)?%", line);
        }
    }

    private static IPlayLayout[] Layouts() => [new ClassicPlayLayout(), new DecisionPlayLayout()];

    private static PlaygroundWorldDefinition Definition(int skill, long baseTicks) {
        var basis = PlaygroundWorldDefinitions.LivingWorld(characterCount: 1, enableNeeds: false, enableWeather: false);
        return basis.Change(initialCharacters: [basis.InitialCharacters[0].Change(skillLevel: skill, placeId: "market")],
            resources: basis.Resources.Select(resource => resource.Id == "well" ? resource with { HarvestTicks = baseTicks } : resource));
    }

    private static PlaygroundObservation WithDuration(PlaygroundObservation source, long ticks) => new() {
        WorldName = source.WorldName, Time = source.Time, IsDay = source.IsDay, NextLightChange = source.NextLightChange,
        Actor = source.Actor, Resources = source.Resources,
        HarvestDurations = new Dictionary<string, long>(source.HarvestDurations, StringComparer.Ordinal) { ["well"] = ticks },
        ItemNames = source.ItemNames, ItemUseDescriptions = source.ItemUseDescriptions,
        OtherCharacters = source.OtherCharacters, Notices = source.Notices, Commands = source.Commands,
        IsExpedition = source.IsExpedition, PlaceId = source.PlaceId, PlaceName = source.PlaceName,
        PlaceDescription = source.PlaceDescription, Passages = source.Passages
    };

    private sealed class GatherController : IPlaygroundCharacterController {
        public int Calls { get; private set; }
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) {
            Calls++;
            return ValueTask.FromResult(new PlaygroundControllerOperation("harvest-view", "gather well 1", "harvest-view"));
        }
    }

    private sealed class Files : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "harvest-view-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Path)) { Directory.Delete(Path, recursive: true); } }
    }
}
