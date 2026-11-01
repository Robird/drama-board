using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.ResourceCycle;

namespace DramaBoard.ResourceCycle.Cli.Tests;

public sealed class TradeViewTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Each_participant_sees_its_own_payment_direction_deadline_and_legal_response(bool decision) {
        CycleWorld world = await OfferedWorld();
        CycleTradeOffer offer = Assert.Single(world.Business.Economy!.Trades.Offers);
        CycleObservation proposer = CycleDecisionContext.Observe(world, "a");
        CycleObservation recipient = CycleDecisionContext.Observe(world, "b");
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
            Assert.Contains(CycleUpdateRenderer.Format(CycleVisibleValue.Numeric(offer.ExpiresAt.Ticks, "ticks")), rendered);
            Assert.Contains("报价不预留", rendered);
        }
        Assert.Null(recipient.CheckDisplayed(CycleCommand.Accept(offer.Id)));
        Assert.Null(recipient.CheckDisplayed(CycleCommand.Reject(offer.Id)));
        Assert.Null(proposer.CheckDisplayed(CycleCommand.Withdraw(offer.Id)));
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
        CycleWorld world = await OfferedWorld();
        CycleTradeOffer offer = Assert.Single(world.Business.Economy!.Trades.Offers);
        CycleObservation third = CycleDecisionContext.Observe(world, "c");
        Assert.Empty(third.Trades);
        string text = Layout(decision).Render(third);
        Assert.DoesNotContain(offer.Id, text);
        Assert.DoesNotContain("## 你的居民报价", text);
        var snapshot = CycleViewSnapshot.Create(third);
        Assert.DoesNotContain(snapshot.Values.Keys, key => key.Category == "trade");
        Assert.DoesNotContain(snapshot.Values.Keys, key => key.Category == "money" && key.ObjectId != "c");
    }

    [Theory]
    [InlineData(CycleTradeStatus.Accepted, "已成交，资产已转移")]
    [InlineData(CycleTradeStatus.Rejected, "已拒绝，未转移资产")]
    [InlineData(CycleTradeStatus.Withdrawn, "已撤回，未转移资产")]
    [InlineData(CycleTradeStatus.Expired, "已到期，未转移资产")]
    [InlineData(CycleTradeStatus.ParticipantFailed, "参与方已不可继续交易")]
    public async Task Both_layouts_distinguish_terminal_outcomes_and_do_not_offer_a_closed_trade_as_acceptable(CycleTradeStatus status, string expected) {
        CycleWorld world = await OfferedWorld();
        CycleObservation source = CycleDecisionContext.Observe(world, "b");
        CycleTradeOffer active = Assert.Single(source.Trades);
        ModelTime closedAt = status == CycleTradeStatus.Expired ? active.ExpiresAt : source.Time;
        CycleTradeOffer closed = active.Close(status, closedAt);
        CycleObservation frozen = WithTrade(source, closed, closedAt);
        foreach (IPlayLayout layout in new IPlayLayout[] { new ClassicPlayLayout(), new DecisionPlayLayout() }) {
            string text = layout.Render(frozen);
            Assert.Contains(expected, text);
            Assert.Contains("结束于", text);
            Assert.NotNull(frozen.CheckDisplayed(CycleCommand.Accept(closed.Id)));
            string commandLine = Assert.Single(layout.RenderActionHelp(frozen).Split('\n'),
                line => line.StartsWith($"- `accept {closed.Id}`", StringComparison.Ordinal));
            Assert.Contains("受阻", commandLine);
            Assert.DoesNotContain("可尝试接受", commandLine);
            Assert.DoesNotContain("## 结局", text);
            Assert.Equal(CycleExpeditionOutcome.Exploring, frozen.Outcome);
        }
    }

    [Fact]
    public async Task Remote_participant_failure_is_trade_history_and_does_not_render_the_viewers_death() {
        CycleWorld world = await OfferedWorld();
        CycleObservation source = CycleDecisionContext.Observe(world, "b");
        CycleTradeOffer closed = Assert.Single(source.Trades).Close(CycleTradeStatus.ParticipantFailed, source.Time);
        CycleObservation frozen = WithTrade(source, closed, source.Time, others: []);
        var change = new CycleObservedEvent(1, "b", CycleObservedEventKind.TradeParticipantFailed, source.Time, source.Time,
            tradeChanges: [closed]);
        string description = CycleUpdateRenderer.EventDescription(change);
        Assert.Contains(closed.Id, description);
        Assert.Contains("参与方已不可继续交易", description);
        Assert.DoesNotContain("生存状态耗尽", description);
        Assert.DoesNotContain("旅程结束", description);
        Assert.DoesNotContain("阿青", description);
        var update = CycleViewUpdate.Create(CycleViewSnapshot.Create(source), CycleViewSnapshot.Create(frozen), [change],
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
    private static CycleObservation WithTrade(CycleObservation source, CycleTradeOffer trade, ModelTime at,
        IReadOnlyList<CycleOtherCharacterObservation>? others = null) => new() {
        Time = at, IsDay = source.IsDay, NextLightChange = source.NextLightChange, Actor = source.Actor,
        Resources = source.Resources, HarvestDurations = source.HarvestDurations, ItemNames = source.ItemNames,
        OtherCharacters = others ?? source.OtherCharacters, Notices = source.Notices,
        Commands = source.Commands.Select(check => check.Command.TradeId == trade.Id
            ? check with { Rejection = "报价已经关闭。" } : check).ToArray(),
        IsExpedition = source.IsExpedition, PlaceId = source.PlaceId, PlaceName = source.PlaceName,
        PlaceDescription = source.PlaceDescription, Passages = source.Passages, Memories = source.Memories,
        Outcome = source.Outcome, Survival = source.Survival, Weather = source.Weather,
        Communication = source.Communication, Goals = source.Goals, Trades = [trade]
    };

    private static async Task<CycleWorld> OfferedWorld() {
        var initial = CycleGameFactory.CreateLivingWorld(characterCount: 3, enableWeather: false);
        var kernel = CycleEngine.CreateKernel(new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor),
            characterController: new Offer());
        for (int step = 0; step < 10; step++) {
            if (kernel.World.Business.Economy!.Trades.Offers.Count > 0) { return kernel.World; }
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
        }
        throw new InvalidOperationException("The fixture did not create an offer.");
    }
    private sealed class Offer : ICycleCharacterController {
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CycleControllerOperation(Guid.NewGuid().ToString("N"),
                "offer b item.food 1 for money 2 1", Guid.NewGuid().ToString("N")));
    }
}
