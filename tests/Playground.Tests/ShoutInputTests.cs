using DramaBoard.Kernel.Simulation;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class ShoutInputTests {
    [Fact]
    public async Task Shout_preserves_whitespace_and_unicode_without_a_target_argument() {
        const string speech = "b  来集合\t等一等\nColdWind 🌄";
        string input = $"  shout\t {speech}  ";
        PlaygroundCommand command = Assert.IsType<PlaygroundCommand>(PlaygroundCommandParser.Parse(input));
        Assert.Equal(PlaygroundCommand.Shout(speech), command);
        Assert.Null(command.TargetActorId);
        Assert.Equal(command, PlaygroundCommandParser.Parse(PlaygroundCommandParser.Format(command)));
        var (world, fact) = await Execute(input);
        Assert.Equal(PlaygroundEventKind.SpeechShouted, fact.Kind);
        Assert.Equal(input, fact.RawInput);
        Assert.Equal(speech, fact.SpeechText);
        Assert.Null(fact.TargetActorId);
        Assert.Equal(1, world.GetCharacter("a").Communication.SentCount);
        Assert.All(world.Characters.Where(actor => actor.Id != "a"), actor => Assert.True(actor.Communication.AttentionPending));
    }

    [Theory]
    [InlineData(2000, true)]
    [InlineData(2001, false)]
    public async Task Shout_limit_counts_unicode_scalars(int count, bool accepted) {
        string speech = string.Concat(Enumerable.Repeat("🌄", count));
        var (world, fact) = await Execute($"shout {speech}");
        Assert.Equal(accepted ? PlaygroundEventKind.SpeechShouted : PlaygroundEventKind.OperationRejected, fact.Kind);
        Assert.Equal(accepted ? 1 : 0, world.GetCharacter("a").Communication.SentCount);
        Assert.All(world.Characters.Where(actor => actor.Id != "a"), actor => Assert.Equal(accepted, actor.Communication.AttentionPending));
    }

    [Theory]
    [InlineData("shout")]
    [InlineData("shout   ")]
    [InlineData("shout\t\n")]
    [InlineData("shoutx words")]
    public async Task Missing_text_or_unknown_command_rejects_without_delivery(string text) {
        var (world, fact) = await Execute(text);
        Assert.Equal(PlaygroundEventKind.OperationRejected, fact.Kind);
        Assert.Empty(fact.ObservedEvents);
        Assert.All(world.Characters, actor => {
            Assert.Equal(0, actor.Communication.SentCount);
            Assert.False(actor.Communication.AttentionPending);
        });
    }

    [Fact]
    public void Shout_rejects_extra_structured_parameters_and_non_spatial_scenarios() {
        var world = PlaygroundGameFactory.CreateSurvival(Hour(8), enableWeather: false).World;
        Assert.NotNull(PlaygroundActions.Check(Solo(), "a", PlaygroundCommand.Shout("hello")));
        Assert.NotNull(PlaygroundActions.Check(world, "a", PlaygroundCommand.Shout(" ")));
        Assert.NotNull(PlaygroundActions.Check(world, "a", new(PlaygroundCommandKind.Shout, TargetActorId: "b", SpeechText: "hello")));
        Assert.NotNull(PlaygroundActions.Check(world, "a", new(PlaygroundCommandKind.Shout, Count: 1, SpeechText: "hello")));
        Assert.NotNull(PlaygroundActions.Check(world, "a", new(PlaygroundCommandKind.Shout, ItemId: "item.water", SpeechText: "hello")));
    }

    private static async Task<(PlaygroundWorld World, PlaygroundFact Fact)> Execute(string input) {
        var initial = PlaygroundGameFactory.CreateSurvival(Hour(8), enableNeeds: false, enableWeather: false, characterCount: 3).World;
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: new OneInput(input));
        await StepWorldEvent(kernel);
        return (kernel.World, Assert.Single(kernel.LastCompletion!.Event.Facts));
    }
    private sealed class OneInput(string input) : IPlaygroundCharacterController {
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) {
            Assert.Equal("a", context.Observation.Actor.Id);
            return ValueTask.FromResult(new PlaygroundControllerOperation("shout-input-call", input, "shout-input-operation"));
        }
    }
}
