using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Playground;

namespace DramaBoard.Playground.Cli.Tests;

public sealed class TradeViewTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Each_participant_sees_its_own_payment_direction_deadline_and_legal_response(bool decision) {
        PlaygroundWorld world = await OfferedWorld();
        PlaygroundTradeOffer offer = Assert.Single(world.Business.Economy!.Trades.Offers);
        PlaygroundObservation proposer = PlaygroundDecisionContext.Observe(world, "a");
        PlaygroundObservation recipient = PlaygroundDecisionContext.Observe(world, "b");
        IPlayLayout layout = Layout(decision);
        string proposerText = layout.Render(proposer);
        string recipientText = layout.Render(recipient);
        string outgoing = Assert.Single(proposerText.Split('\n'), line => line.Contains("对你而言", StringComparison.Ordinal));
        string incoming = Assert.Single(recipientText.Split('\n'), line => line.Contains("对你而言", StringComparison.Ordinal));
        Assert.Contains("支付", outgoing);
        Assert.Contains("item.food", outgoing);
        Assert.Contains("×1", outgoing);
        Assert.Contains("收到 2 钱", outgoing);
        Assert.Contains("支付 2 钱", incoming);
        Assert.Contains("收到", incoming);
        Assert.Contains("item.food", incoming);
        foreach (string rendered in new[] { proposerText, recipientText }) {
            Assert.Contains(offer.Id, rendered);
            Assert.Contains("有效报价，尚未成交", rendered);
            Assert.Contains(PlaygroundUpdateRenderer.Format(PlaygroundVisibleValue.Numeric(offer.ExpiresAt.Ticks, "ticks")), rendered);
            Assert.Contains("报价不预留", rendered);
        }
        Assert.Null(recipient.CheckDisplayed(PlaygroundCommand.Accept(offer.Id)));
        Assert.Null(recipient.CheckDisplayed(PlaygroundCommand.Reject(offer.Id)));
        Assert.Null(proposer.CheckDisplayed(PlaygroundCommand.Withdraw(offer.Id)));
        string help = layout.RenderActionHelp(recipient);
        Assert.Contains($"accept {offer.Id}", help);
        Assert.Contains($"reject {offer.Id}", help);
        Assert.Contains("可尝试接受", help);
        Assert.Contains("不重置原活动期限", help);
        Assert.Contains("只有发起 offer 消耗", help);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Third_resident_has_no_personal_trade_record_or_counterparty_assets(bool decision) {
        PlaygroundWorld world = await OfferedWorld();
        PlaygroundTradeOffer offer = Assert.Single(world.Business.Economy!.Trades.Offers);
        PlaygroundObservation third = PlaygroundDecisionContext.Observe(world, "c");
        Assert.Empty(third.Trades);
        string text = Layout(decision).Render(third);
        Assert.DoesNotContain(offer.Id, text);
        Assert.DoesNotContain("## 你的居民报价", text);
        var snapshot = PlaygroundViewSnapshot.Create(third);
        Assert.DoesNotContain(snapshot.Values.Keys, key => key.Category == "trade");
        Assert.DoesNotContain(snapshot.Values.Keys, key => key.Category == "money" && key.ObjectId != "c");
    }

    [Theory]
    [InlineData(PlaygroundTradeStatus.Accepted, "已成交，资产已转移")]
    [InlineData(PlaygroundTradeStatus.Rejected, "已拒绝，未转移资产")]
    [InlineData(PlaygroundTradeStatus.Withdrawn, "已撤回，未转移资产")]
    [InlineData(PlaygroundTradeStatus.Expired, "已到期，未转移资产")]
    [InlineData(PlaygroundTradeStatus.ParticipantFailed, "参与方已不可继续交易")]
    public async Task Both_layouts_distinguish_terminal_outcomes_and_do_not_offer_a_closed_trade_as_acceptable(PlaygroundTradeStatus status, string expected) {
        PlaygroundWorld world = await OfferedWorld();
        PlaygroundObservation source = PlaygroundDecisionContext.Observe(world, "b");
        PlaygroundTradeOffer active = Assert.Single(source.Trades);
        ModelTime closedAt = status == PlaygroundTradeStatus.Expired ? active.ExpiresAt : source.Time;
        PlaygroundTradeOffer closed = active.Close(status, closedAt);
        PlaygroundObservation frozen = WithTrade(source, closed, closedAt);
        foreach (IPlayLayout layout in new IPlayLayout[] { new ClassicPlayLayout(), new DecisionPlayLayout() }) {
            string text = layout.Render(frozen);
            Assert.Contains(expected, text);
            Assert.Contains("结束于", text);
            Assert.NotNull(frozen.CheckDisplayed(PlaygroundCommand.Accept(closed.Id)));
            string commandLine = Assert.Single(layout.RenderActionHelp(frozen).Split('\n'),
                line => line.StartsWith($"- `accept {closed.Id}`", StringComparison.Ordinal));
            Assert.Contains("受阻", commandLine);
            Assert.DoesNotContain("可尝试接受", commandLine);
            Assert.DoesNotContain("## 结局", text);
            Assert.Equal(PlaygroundExpeditionOutcome.Exploring, frozen.Outcome);
        }
    }

    [Fact]
    public async Task Remote_participant_failure_is_trade_history_and_does_not_render_the_viewers_death() {
        PlaygroundWorld world = await OfferedWorld();
        PlaygroundObservation source = PlaygroundDecisionContext.Observe(world, "b");
        PlaygroundTradeOffer closed = Assert.Single(source.Trades).Close(PlaygroundTradeStatus.ParticipantFailed, source.Time);
        PlaygroundObservation frozen = WithTrade(source, closed, source.Time, others: []);
        var change = new PlaygroundObservedEvent(1, "b", PlaygroundObservedEventKind.TradeParticipantFailed, source.Time, source.Time,
            tradeChanges: [closed]);
        string description = PlaygroundUpdateRenderer.EventDescription(change);
        Assert.Contains(closed.Id, description);
        Assert.Contains("参与方已不可继续交易", description);
        Assert.DoesNotContain("生存状态耗尽", description);
        Assert.DoesNotContain("旅程结束", description);
        Assert.DoesNotContain("阿青", description);
        var update = PlaygroundViewUpdate.Create(PlaygroundViewSnapshot.Create(source), PlaygroundViewSnapshot.Create(frozen), [change],
            PlayDeliveryPhase.ControllerInput);
        foreach (IPlayLayout layout in new IPlayLayout[] { new ClassicPlayLayout(), new DecisionPlayLayout() }) {
            string rendered = layout.RenderUpdate(update);
            Assert.Contains(closed.Id, rendered);
            Assert.DoesNotContain("## 结局", rendered);
        }
        Assert.Contains("参与方已不可继续交易", update.CurrentValues[new("trade", closed.Id, "status")].Text);
    }

    private static IPlayLayout Layout(bool decision) => decision ? new DecisionPlayLayout() : new ClassicPlayLayout();
    // These presentation-only terminal variants exercise recorded data; domain transition legality has independent tests.
    private static PlaygroundObservation WithTrade(PlaygroundObservation source, PlaygroundTradeOffer trade, ModelTime at,
        IReadOnlyList<PlaygroundOtherCharacterObservation>? others = null) => new() {
        Time = at, IsDay = source.IsDay, NextLightChange = source.NextLightChange, Actor = source.Actor,
        Resources = source.Resources, HarvestDurations = source.HarvestDurations, WorldName = source.WorldName, ItemUseDescriptions = source.ItemUseDescriptions, ItemNames = source.ItemNames,
        OtherCharacters = others ?? source.OtherCharacters, Notices = source.Notices,
        Commands = source.Commands.Select(check => check.Command.TradeId == trade.Id
            ? check with { Rejection = "报价已经关闭。" } : check).ToArray(),
        IsExpedition = source.IsExpedition, PlaceId = source.PlaceId, PlaceName = source.PlaceName,
        PlaceDescription = source.PlaceDescription, Passages = source.Passages, Memories = source.Memories,
        Outcome = source.Outcome, Survival = source.Survival, Weather = source.Weather,
        Communication = source.Communication, Goals = source.Goals, Trades = [trade]
    };

    private static async Task<PlaygroundWorld> OfferedWorld() {
        var initial = PlaygroundGameFactory.CreateLivingWorld(characterCount: 3, enableWeather: false);
        var kernel = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor),
            characterController: new Offer());
        for (int step = 0; step < 10; step++) {
            if (kernel.World.Business.Economy!.Trades.Offers.Count > 0) { return kernel.World; }
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
        }
        throw new InvalidOperationException("The fixture did not create an offer.");
    }
    private sealed class Offer : IPlaygroundCharacterController {
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PlaygroundControllerOperation(Guid.NewGuid().ToString("N"),
                "offer b item.food 1 for money 2 1", Guid.NewGuid().ToString("N")));
    }
}
