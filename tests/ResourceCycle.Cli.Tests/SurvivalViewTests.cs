using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.ResourceCycle;

namespace DramaBoard.ResourceCycle.Cli.Tests;

public sealed class SurvivalViewTests {
    [Fact]
    public void Initial_view_describes_visible_trails_without_revealing_destinations_or_remote_resources() {
        CycleWorld world = CycleGameFactory.CreateSurvival(enableNeeds: false).World;
        CycleDecisionContext context = ViewContextFixture.Create(world, "a");
        string text = PlayView.Render(context.Observation, context.InteractionState);
        Assert.Contains("# 山野镇", text);
        Assert.Contains("林间营地", text);
        Assert.Contains("go trail-1", text);
        Assert.Contains("go trail-2", text);
        Assert.Contains("耗时 60 分钟", text);
        Assert.Contains("耗时 120 分钟", text);
        foreach (string hidden in new[] { "山脚镇口", "巡山信号站", "溪流湾", "废弃林屋", "berries", "ridge-herbs", "trail-6" }) {
            Assert.DoesNotContain(hidden, text);
            Assert.DoesNotContain(hidden, PlayView.RenderCommands(context.Observation));
        }
        Assert.Empty(context.Observation.Resources);
        Assert.All(context.Observation.Passages, passage => Assert.Null(passage.KnownDestinationName));
        Assert.Equal(context.Check(CycleCommand.Gather("missing")), context.Check(CycleCommand.Gather("berries")));
        Assert.Equal(context.Check(CycleCommand.Go("missing")), context.Check(CycleCommand.Go("trail-6")));
    }

    [Fact]
    public async Task Leaving_a_place_shows_timestamped_history_not_live_distant_stock() {
        var initial = CycleGameFactory.CreateSurvival(enableNeeds: false);
        var history = new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor);
        var kernel = CycleEngine.CreateKernel(history, characterController: new Commands(CycleCommand.Go("trail-1"), CycleCommand.Go("trail-1")));
        await Step(kernel); // leave camp
        await Until(kernel, world => CycleActions.CurrentPlace(world, "a") == "stream");
        CycleDecisionContext atStream = CycleDecisionContext.Create(kernel.World, "a");
        Assert.Contains(atStream.Observation.Resources, resource => resource.Id == "berries");
        Assert.Contains("gather berries 1", PlayView.Render(atStream.Observation, atStream.InteractionState));
        await Step(kernel); // leave stream
        await Until(kernel, world => CycleActions.CurrentPlace(world, "a") == "camp");
        CycleDecisionContext atCamp = CycleDecisionContext.Create(kernel.World, "a");
        string text = PlayView.Render(atCamp.Observation, atCamp.InteractionState);
        Assert.Contains("## 曾到访地点", text);
        Assert.Contains("历史观察", text);
        Assert.Contains("溪流湾（第", text);
        Assert.Contains("曾到访的目的地：溪流湾", text);
        Assert.DoesNotContain("gather berries", text);
        Assert.Empty(atCamp.Observation.Resources);
        Assert.Equal("当前不可见或不可用的资源点。", atCamp.Check(CycleCommand.Gather("berries")));
        Assert.Single(atCamp.Observation.Memories);
    }

    [Fact]
    public async Task Shared_item_identity_does_not_reveal_an_unvisited_producer() {
        var initial = CycleGameFactory.CreateSurvival(enableNeeds: false);
        var kernel = CycleEngine.CreateKernel(new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor),
            characterController: new Commands(CycleCommand.Go("trail-2")));
        await Until(kernel, world => CycleActions.CurrentPlace(world, "a") == "ridge");
        CycleObservation observation = CycleDecisionContext.Create(kernel.World, "a").Observation;
        Assert.Equal("食物", observation.ItemNames["item.food"]);
        string text = PlayView.Render(observation);
        Assert.Contains("山脊野菜", text);
        Assert.DoesNotContain("野浆果", text);
        Assert.DoesNotContain("溪流湾", text);
    }

    [Fact]
    public async Task Arrival_at_town_keeps_the_resident_active_with_available_actions() {
        var initial = CycleGameFactory.CreateSurvival(enableNeeds: false);
        var kernel = CycleEngine.CreateKernel(new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor),
            characterController: new Commands(CycleCommand.Go("trail-1"), CycleCommand.Go("trail-3"), CycleCommand.Go("trail-4")));
        await Until(kernel, world => CycleActions.CurrentPlace(world, "a") == "road");
        CycleDecisionContext context = CycleDecisionContext.Create(kernel.World, "a");
        string text = PlayView.Render(context.Observation, context.InteractionState);
        Assert.Contains("山脚镇口", text);
        Assert.DoesNotContain("## 结局", text);
        Assert.Contains("## 可用命令", text);
        Assert.True(kernel.World.GetCharacter("a").IsActive);
        Assert.Null(context.Check(CycleCommand.Wait(1)));
    }

    [Theory]
    [InlineData("--survival")]
    [InlineData("--play")]
    [InlineData("--duo")]
    public void Removed_product_modes_are_rejected(string oldMode) =>
        Assert.Throws<ArgumentException>(() => Options.Parse([oldMode]));

    [Fact]
    public void Default_product_entry_is_the_continuous_living_world() {
        Options options = Options.Parse([]);
        Assert.Null(options.Llm);
        Assert.Null(options.CharacterCount);
        Assert.Null(options.Until);
    }

    private static async Task Step(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel) =>
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(96 * CycleTime.Hour)));
    private static async Task Until(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel, Func<CycleWorld, bool> predicate) {
        for (int i = 0; i < 80; i++) {
            if (predicate(kernel.World) && kernel.World.Business.InteractionStates["a"].CurrentContext is {
                Status: CharacterContextStatus.Open or CharacterContextStatus.Terminal }) { return; }
            await Step(kernel);
        }
        Assert.Fail("Expected location or ending was not reached.");
    }
    private sealed class Commands(params CycleCommand[] commands) : ICycleCharacterController {
        private readonly Queue<CycleCommand> _commands = new(commands);
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CycleControllerOperation("call_" + Guid.NewGuid().ToString("N"),
                CycleCommandParser.Format(_commands.Count > 0 ? _commands.Dequeue() : throw new InvalidOperationException("Unexpected decision.")), Guid.NewGuid().ToString("N")));
    }
}
