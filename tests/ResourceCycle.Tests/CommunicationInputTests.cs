using DramaBoard.Kernel.Simulation;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class CommunicationInputTests {
    [Fact]
    public async Task Speech_keeps_internal_spaces_tabs_newlines_and_unicode_through_parse_and_commit() {
        const string speech = "泉水  在东边\t先休息\n明早一起出发 🌄";
        string input = $"  say\tb   {speech}  ";
        CycleCommand parsed = Assert.IsType<CycleCommand>(CycleCommandParser.Parse(input));

        Assert.Equal(CycleCommand.Say("b", speech), parsed);
        Assert.Equal(parsed, CycleCommandParser.Parse(CycleCommandParser.Format(parsed)));
        var result = await Execute(input);

        Assert.Equal(CycleEventKind.SpeechSent, result.Fact.Kind);
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

        Assert.Equal(accepted ? CycleEventKind.SpeechSent : CycleEventKind.OperationRejected, result.Fact.Kind);
        Assert.Equal(accepted ? 1 : 0, result.After.GetCharacter("a").Communication.SentCount);
        Assert.Equal(accepted, result.After.GetCharacter("b").Communication.AttentionPending);
        if (accepted) {
            Assert.Equal(speech, result.Fact.SpeechText);
        } else {
            Assert.Equal(CycleOperationDisposition.Rejected, result.Fact.OperationDisposition);
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

        Assert.Equal(CycleEventKind.OperationRejected, result.Fact.Kind);
        Assert.Equal(CycleOperationDisposition.Rejected, result.Fact.OperationDisposition);
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
        CycleCommand command = CycleCommand.Give("b", "item.water", quantity);

        Assert.Equal(command, CycleCommandParser.Parse(CycleCommandParser.Format(command)));
    }

    private static async Task<(CycleWorld Before, CycleWorld After, CycleFact Fact)> Execute(string input) {
        CycleWorld initial = CycleGameFactory.CreateSurvival(Hour(8), enableWeather: false, characterCount: 2).World;
        var controller = new OneOperation(input);
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: controller);
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(initial.Time));
        Assert.Equal(CycleEventKind.DecisionContextOpened, Assert.Single(kernel.LastCompletion!.Event.Facts).Kind);
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(initial.Time));
        Assert.Equal(1, controller.Calls);
        return (initial, kernel.World, Assert.Single(kernel.LastCompletion!.Event.Facts));
    }

    private sealed class OneOperation(string input) : ICycleCharacterController {
        public int Calls { get; private set; }
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) {
            Assert.Equal("a", context.Observation.Actor.Id);
            Calls++;
            return ValueTask.FromResult(new CycleControllerOperation("communication-input-call", input, "communication-input-operation"));
        }
    }
}
