using DramaBoard.Kernel.Simulation;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class CommunicationInputTests {
    [Fact]
    public async Task Speech_keeps_internal_spaces_tabs_newlines_and_unicode_through_parse_and_commit() {
        const string speech = "泉水  在东边\t先休息\n明早一起出发 🌄";
        string input = $"  say\tb   {speech}  ";
        PlaygroundCommand parsed = Assert.IsType<PlaygroundCommand>(PlaygroundCommandParser.Parse(input));

        Assert.Equal(PlaygroundCommand.Say("b", speech), parsed);
        Assert.Equal(parsed, PlaygroundCommandParser.Parse(PlaygroundCommandParser.Format(parsed)));
        var result = await Execute(input);

        Assert.Equal(PlaygroundEventKind.SpeechSent, result.Fact.Kind);
        Assert.Equal(input, result.Fact.RawInput);
        Assert.Equal(speech, result.Fact.SpeechText);
        Assert.Equal("b", result.Fact.TargetActorId);
        Assert.True(result.After.GetCharacter("b").Communication.AttentionPending);
    }

    [Theory]
    [InlineData(2000, true)]
    [InlineData(2001, false)]
    public async Task Speech_limit_counts_unicode_scalars_instead_of_utf16_code_units(int scalarCount, bool accepted) {
        string speech = string.Concat(Enumerable.Repeat("🌄", scalarCount));
        Assert.Equal(2 * scalarCount, speech.Length);
        var result = await Execute($"say b {speech}");

        Assert.Equal(accepted ? PlaygroundEventKind.SpeechSent : PlaygroundEventKind.OperationRejected, result.Fact.Kind);
        Assert.Equal(accepted ? 1 : 0, result.After.GetCharacter("a").Communication.SentCount);
        Assert.Equal(accepted, result.After.GetCharacter("b").Communication.AttentionPending);
        if (accepted) {
            Assert.Equal(speech, result.Fact.SpeechText);
        } else {
            Assert.Equal(PlaygroundOperationDisposition.Rejected, result.Fact.OperationDisposition);
            Assert.Same(result.Before.GetCharacter("b"), result.After.GetCharacter("b"));
            Assert.Empty(result.Fact.ObservedEvents);
        }
    }

    [Theory]
    [InlineData("2147483648")]
    [InlineData("999999999999999999999999999999999999")]
    [InlineData("1.5")]
    [InlineData("many")]
    [InlineData("-1")]
    [InlineData("0")]
    public async Task Invalid_gift_quantity_commits_a_rejection_without_transfer_or_budget_charge(string quantity) {
        string input = $"give b item.water {quantity}";
        var result = await Execute(input);

        Assert.Equal(PlaygroundEventKind.OperationRejected, result.Fact.Kind);
        Assert.Equal(PlaygroundOperationDisposition.Rejected, result.Fact.OperationDisposition);
        Assert.Equal(input, result.Fact.RawInput);
        Assert.False(string.IsNullOrWhiteSpace(result.Fact.Result));
        Assert.Empty(result.Fact.ObservedEvents);
        Assert.Equal(result.Before.Time, result.After.Time);
        Assert.All(result.After.Characters, actor => {
            Assert.Same(result.Before.GetCharacter(actor.Id).Backpack, actor.Backpack);
            Assert.Equal(0, actor.Communication.SentCount);
            Assert.False(actor.Communication.AttentionPending);
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void Gift_format_roundtrip_preserves_target_item_and_entire_integer_quantity(int quantity) {
        PlaygroundCommand command = PlaygroundCommand.Give("b", "item.water", quantity);

        Assert.Equal(command, PlaygroundCommandParser.Parse(PlaygroundCommandParser.Format(command)));
    }

    private static async Task<(PlaygroundWorld Before, PlaygroundWorld After, PlaygroundFact Fact)> Execute(string input) {
        PlaygroundWorld initial = PlaygroundGameFactory.CreateSurvival(Hour(8), enableWeather: false, characterCount: 2).World;
        var controller = new OneOperation(input);
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: controller);
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(initial.Time));
        Assert.Equal(PlaygroundEventKind.DecisionContextOpened, Assert.Single(kernel.LastCompletion!.Event.Facts).Kind);
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(initial.Time));
        Assert.Equal(1, controller.Calls);
        return (initial, kernel.World, Assert.Single(kernel.LastCompletion!.Event.Facts));
    }

    private sealed class OneOperation(string input) : IPlaygroundCharacterController {
        public int Calls { get; private set; }
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) {
            Assert.Equal("a", context.Observation.Actor.Id);
            Calls++;
            return ValueTask.FromResult(new PlaygroundControllerOperation("communication-input-call", input, "communication-input-operation"));
        }
    }
}
