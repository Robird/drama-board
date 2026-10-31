using DramaBoard.Inventory;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;

namespace DramaBoard.Microtown.Tests;

public sealed class TownTradeAcceptanceTests {
    [Fact]
    public async Task Offer_reserves_nothing_and_acceptance_atomically_exchanges_both_sides_once() {
        var play = new Play();
        TownBusinessState original = play.State;
        Assert.True((await play.Act("a", "offer b meal 1 money 5 4")).Accepted);
        TownTradeOffer offer = Assert.Single(play.State.Trades.Offers);
        Assert.Equal(TownTradeStatus.Active, offer.Status);
        Assert.Equal(1, play.State.Character("a").Inventory.Count("meal"));
        Assert.Equal(18, play.State.Character("a").Money);
        Assert.Equal(18, play.State.Character("b").Money);
        Assert.True((await play.Act("b", $"accept {offer.Id}")).Accepted);
        Assert.Equal(TownTradeStatus.Accepted, Assert.Single(play.State.Trades.Offers).Status);
        Assert.Equal(23, play.State.Character("a").Money);
        Assert.Equal(13, play.State.Character("b").Money);
        Assert.Equal(0, play.State.Character("a").Inventory.Count("meal"));
        Assert.Equal(2, play.State.Character("b").Inventory.Count("meal"));
        Assert.Equal(54, play.State.Characters.Sum(actor => actor.Money));
        Assert.Equal(3, play.State.Characters.Sum(actor => actor.Inventory.Count("meal")));
        Assert.Equal(0, play.State.Economy.ImportExpense);
        Assert.Equal(0, play.State.Economy.ExportIncome);
        Assert.Empty(original.Trades.Offers);
        Assert.All(original.Characters, actor => { Assert.Equal(18, actor.Money); Assert.Equal(1, actor.Inventory.Count("meal")); });
        Assert.True((await play.Act("c", "wait 4")).Accepted);
        Assert.True((await play.Act("a", "wait 4")).Accepted);
        Assert.False((await play.Act("b", $"accept {offer.Id}")).Accepted);
        Assert.Equal(23, play.State.Character("a").Money);
        Assert.Equal(13, play.State.Character("b").Money);
        Assert.Equal(2, play.State.Character("b").Inventory.Count("meal"));
    }

    [Fact]
    public async Task Proposer_can_spend_offered_goods_and_failed_acceptance_transfers_nothing_and_keeps_offer_active() {
        var play = new Play();
        Assert.True((await play.Act("a", "offer b meal 1 money 5 4")).Accepted);
        string id = Assert.Single(play.State.Trades.Offers).Id;
        Assert.True((await play.Act("b", "eat")).Accepted);
        Assert.True((await play.Act("c", "wait 4")).Accepted);
        Assert.True((await play.Act("a", "give c meal 1")).Accepted);
        TownBusinessState before = play.State;
        Assert.False((await play.Act("b", $"accept {id}")).Accepted);
        Assert.Equal(TownTradeStatus.Active, Assert.Single(play.State.Trades.Offers).Status);
        Assert.Equal("b", play.State.OpenDecisionActorId);
        AssertBalances(before, play.State);
    }

    [Fact]
    public async Task Acceptance_checks_recipient_full_payment_without_using_the_expected_incoming_money() {
        var play = new Play();
        Assert.True((await play.Act("a", "offer b money 18 money 19 4")).Accepted);
        string id = Assert.Single(play.State.Trades.Offers).Id;
        TownBusinessState before = play.State;
        Assert.False((await play.Act("b", $"accept {id}")).Accepted);
        Assert.Equal(TownTradeStatus.Active, Assert.Single(play.State.Trades.Offers).Status);
        AssertBalances(before, play.State);
    }

    [Fact]
    public async Task Two_full_backpacks_can_swap_when_both_outgoing_assets_are_removed_before_admission() {
        var initial = TownGameFactory.Create(4);
        TownCharacter[] actors = initial.World.Business.Characters.Select(actor => actor.Id switch {
            "a" => actor with { Inventory = new(new Dictionary<string, int> { ["meal"] = 1 }, unitCapacity: 1) },
            "b" => actor with { Inventory = new(new Dictionary<string, int> { ["wood"] = 1 }, unitCapacity: 1) },
            _ => actor
        }).ToArray();
        var play = new Play(Seed(initial.World.Business, actors), initial.Cursor);
        Assert.True((await play.Act("a", "offer b meal 1 wood 1 4")).Accepted);
        string id = Assert.Single(play.State.Trades.Offers).Id;
        Assert.True((await play.Act("b", $"accept {id}")).Accepted);
        Assert.Equal(1, play.State.Character("a").Inventory.Count("wood"));
        Assert.Equal(0, play.State.Character("a").Inventory.Count("meal"));
        Assert.Equal(1, play.State.Character("b").Inventory.Count("meal"));
        Assert.Equal(0, play.State.Character("b").Inventory.Count("wood"));
        Assert.Equal(1, play.State.Character("a").Inventory.TotalCount);
        Assert.Equal(1, play.State.Character("b").Inventory.TotalCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reject_or_withdraw_is_a_single_terminal_transition_without_asset_changes(bool withdraw) {
        var play = new Play();
        Assert.True((await play.Act("a", "offer b meal 1 money 5 4")).Accepted);
        string id = Assert.Single(play.State.Trades.Offers).Id;
        if (withdraw) {
            Assert.True((await play.Act("b", "wait 4")).Accepted);
            Assert.True((await play.Act("c", "wait 4")).Accepted);
        }
        TownBusinessState before = play.State;
        string actor = withdraw ? "a" : "b", command = (withdraw ? "withdraw " : "reject ") + id;
        Assert.True((await play.Act(actor, command)).Accepted);
        TownTradeOffer terminal = Assert.Single(play.State.Trades.Offers);
        Assert.Equal(withdraw ? TownTradeStatus.Withdrawn : TownTradeStatus.Rejected, terminal.Status);
        Assert.NotNull(terminal.ClosedAt);
        Assert.Equal(2, terminal.Version);
        AssertBalances(before, play.State);
        // A terminal command cannot settle again, irrespective of eventual bounded-history reclamation.
        Assert.NotNull(TownPlanning.Check(play.State, actor, TownCommand.Parse(command)));
        Assert.NotNull(TownPlanning.Check(play.State, "b", TownCommand.Parse($"accept {id}")));
        Assert.Equal(terminal, Assert.Single(play.State.Trades.Offers));
    }

    [Fact]
    public async Task Only_the_named_party_can_accept_reject_or_withdraw_the_offer() {
        var play = new Play();
        Assert.True((await play.Act("a", "offer b meal 1 money 5 4")).Accepted);
        string id = Assert.Single(play.State.Trades.Offers).Id;
        Assert.False((await play.Act("b", $"withdraw {id}")).Accepted);
        Assert.True((await play.Act("b", "wait 4")).Accepted);
        Assert.False((await play.Act("c", $"accept {id}")).Accepted);
        Assert.False((await play.Act("c", $"reject {id}")).Accepted);
        Assert.True((await play.Act("c", "wait 4")).Accepted);
        Assert.False((await play.Act("a", $"accept {id}")).Accepted);
        Assert.Equal(TownTradeStatus.Active, Assert.Single(play.State.Trades.Offers).Status);
        Assert.All(play.State.Characters, actor => Assert.Equal(18, actor.Money));
    }

    [Fact]
    public async Task Expiration_precedes_the_same_time_decision_wakes_both_busy_parties_and_preserves_due_times() {
        var play = new Play();
        Assert.True((await play.Act("a", "offer b meal 1 money 5 1")).Accepted);
        string id = Assert.Single(play.State.Trades.Offers).Id;
        Assert.True((await play.Act("b", "wait 2")).Accepted);
        Assert.True((await play.Act("c", "wait 2")).Accepted);
        Assert.True((await play.Act("a", "wait 2")).Accepted);
        TownActivity a = play.State.Character("a").Activity!, b = play.State.Character("b").Activity!;
        await play.Open("b");
        TownTradeOffer expired = Assert.Single(play.State.Trades.Offers);
        Assert.Equal(TownTradeStatus.Expired, expired.Status);
        Assert.Equal(TownContent.Hour, play.State.Time.Ticks);
        Assert.Equal(expired.ExpiresAt, expired.ClosedAt);
        Assert.Equal(2, expired.RecipientDeliveredVersion);
        Assert.Equal(a, play.State.Character("a").Activity);
        Assert.Equal(b, play.State.Character("b").Activity);
        Assert.False((await play.Act("b", $"accept {id}")).Accepted);
        Assert.True((await play.Act("b", "continue")).Accepted);
        await play.Open("a");
        Assert.Equal(2, Assert.Single(play.State.Trades.Offers).ProposerDeliveredVersion);
        Assert.True((await play.Act("a", "continue")).Accepted);
        Assert.Equal(a, play.State.Character("a").Activity);
        Assert.Equal(b, play.State.Character("b").Activity);
        Assert.Equal(TownContent.Hour, play.State.Time.Ticks);
        Assert.All(play.State.Characters, actor => Assert.Equal(18, actor.Money));
    }

    [Fact]
    public async Task Continuing_after_new_offer_does_not_repeat_its_delivery_or_polling_wakeup() {
        var play = new Play();
        Assert.True((await play.Act("a", "wait 1")).Accepted);
        Assert.True((await play.Act("b", "wait 2")).Accepted);
        TownActivity waiting = play.State.Character("b").Activity!;
        Assert.True((await play.Act("c", "offer b meal 1 money 2 4")).Accepted);
        await play.Open("b");
        Assert.True(Assert.Single(TownObservation.Capture(play.History.State, "b").TradeOffers).NewInLatestDecision);
        long deliveredAt = Assert.Single(play.State.Trades.Offers).RecipientDeliveryDecisionSequence;
        Assert.True((await play.Act("b", "continue")).Accepted);
        Assert.False(play.State.Character("b").Interaction.NeedsResponse);
        Assert.Equal(waiting, play.State.Character("b").Activity);
        Assert.True((await play.Act("c", "wait 4")).Accepted);
        await play.Open("a");
        Assert.Equal(TownContent.Hour, play.State.Time.Ticks); // No immediate repeated b notification.
        Assert.True((await play.Act("a", "wait 4")).Accepted);
        await play.Open("b");
        Assert.Equal(2 * TownContent.Hour, play.State.Time.Ticks);
        Assert.Null(play.State.Character("b").Activity);
        Assert.Equal(TownTradeStatus.Active, Assert.Single(play.State.Trades.Offers).Status);
        Assert.Equal(deliveredAt, Assert.Single(play.State.Trades.Offers).RecipientDeliveryDecisionSequence);
        Assert.False(Assert.Single(TownObservation.Capture(play.History.State, "b").TradeOffers).NewInLatestDecision);
        Assert.False(play.State.Character("b").Interaction.NeedsResponse);
    }

    [Fact]
    public async Task Real_hunger_failure_closes_offer_and_preserves_survivors_undelivered_terminal_record() {
        var initial = TownGameFactory.Create(4);
        TownCharacter[] actors = initial.World.Business.Characters.Select(actor => actor.Id == "a"
            ? actor with { FoodReserve = 3 * TownContent.Hour, MinimumFoodReserve = 3 * TownContent.Hour } : actor).ToArray();
        var play = new Play(Seed(initial.World.Business, actors), initial.Cursor);
        Assert.True((await play.Act("a", "offer b meal 1 money 5 4")).Accepted);
        Assert.True((await play.Act("b", "wait 2")).Accepted);
        Assert.True((await play.Act("c", "wait 2")).Accepted);
        Assert.True((await play.Act("a", "wait 2")).Accepted);
        TownBusinessState before = play.State;
        await play.Until(state => state.Character("a").Failed);
        Assert.Equal(TownContent.Hour, play.State.Time.Ticks);
        TownTradeOffer terminal = Assert.Single(play.State.Trades.Offers);
        Assert.Equal(TownTradeStatus.ParticipantFailed, terminal.Status);
        Assert.True(terminal.RecipientDeliveredVersion < terminal.Version);
        Assert.True(Assert.Single(TownObservation.Capture(play.History.State, "b").TradeOffers).AwaitingDelivery);
        Assert.True(play.State.Character("b").Interaction.NeedsResponse);
        Assert.False(play.State.Character("b").Failed);
        AssertBalances(before, play.State);
        await play.Open("b");
        TownTradeOffer delivered = Assert.Single(play.State.Trades.Offers);
        Assert.Equal(2, delivered.RecipientDeliveredVersion);
        Assert.True(delivered.ProposerDeliveredVersion < 2); // Failure exemption is not invented delivery.
        Assert.Equal(TownTradeStatus.ParticipantFailed,
            Assert.Single(TownObservation.Capture(play.History.State, "b").TradeOffers).Status);
        Assert.Equal(4, play.Calls);
    }

    [Fact]
    public async Task Experiment_end_closes_all_active_offers_without_controller_calls_or_asset_transfer() {
        var initial = TownGameFactory.Create(1);
        var play = new Play(initial.World, initial.Cursor);
        Assert.True((await play.Act("a", "offer b meal 1 money 5 4")).Accepted);
        Assert.True((await play.Act("b", "offer c money 2 meal 1 4")).Accepted);
        Assert.True((await play.Act("c", "wait 4")).Accepted);
        Assert.True((await play.Act("a", "wait 4")).Accepted);
        Assert.True((await play.Act("b", "wait 4")).Accepted);
        TownBusinessState before = play.State;
        await play.Until(state => state.Ended);
        Assert.Equal(TownContent.Hour, play.State.Time.Ticks);
        Assert.Equal(2, play.State.Trades.Offers.Count);
        Assert.All(play.State.Trades.Offers, offer => {
            Assert.Equal(TownTradeStatus.WorldEnded, offer.Status);
            Assert.Equal(play.State.Time, offer.ClosedAt);
        });
        AssertBalances(before, play.State);
        Assert.All(play.State.Characters, actor => {
            Assert.False(actor.Interaction.NeedsResponse);
            Assert.Equal(before.Character(actor.Id).Activity, actor.Activity);
        });
        Assert.Equal(5, play.Calls);
        Assert.Null(play.State.OpenDecisionActorId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejection_and_withdrawal_do_not_require_reuniting_after_departure(bool withdraw) {
        var play = new Play();
        Assert.True((await play.Act("a", "offer b meal 1 money 5 4")).Accepted);
        string id = Assert.Single(play.State.Trades.Offers).Id;
        Assert.True((await play.Act("b", "go market-workshop")).Accepted);
        Assert.True((await play.Act("c", "wait 4")).Accepted);
        if (!withdraw) { Assert.True((await play.Act("a", "wait 4")).Accepted); }
        TownBusinessState before = play.State;
        Assert.True((await play.Act(withdraw ? "a" : "b", (withdraw ? "withdraw " : "reject ") + id)).Accepted);
        Assert.NotEqual(play.State.Place("a"), play.State.Place("b"));
        Assert.Equal(withdraw ? TownTradeStatus.Withdrawn : TownTradeStatus.Rejected, Assert.Single(play.State.Trades.Offers).Status);
        AssertBalances(before, play.State);
    }

    [Fact]
    public async Task Terminal_trade_delivery_bypasses_a_full_user_inbox_without_losing_unread_speech() {
        var original = new Play();
        Assert.True((await original.Act("a", "offer b meal 1 money 5 4")).Accepted);
        string id = Assert.Single(original.State.Trades.Offers).Id;
        TownInteractionState full = TownInteractionState.Empty;
        for (int index = 0; index < TownInteractionState.MaximumPending; index++) {
            full = full.Append($"fixture-speech-{index}", "c", "a", original.State.Time,
                TownInteractionKind.Speech, $"unread-{index}", needsResponse: true);
        }
        var actors = original.State.Characters.Select(actor => actor.Id == "a" ? actor with { Interaction = full } : actor).ToArray();
        var play = new Play(Copy(original.State, actors), original.History.Cursor);
        Assert.True((await play.Act("b", $"accept {id}")).Accepted);
        Assert.Equal(TownTradeStatus.Accepted, Assert.Single(play.State.Trades.Offers).Status);
        Assert.Equal(TownInteractionState.MaximumPending, play.State.Character("a").Interaction.PendingCount);
        Assert.Equal(23, play.State.Character("a").Money);
        Assert.True((await play.Act("c", "wait 4")).Accepted);
        await play.Open("a");
        TownTradeOffer delivered = Assert.Single(play.State.Trades.Offers);
        Assert.Equal(2, delivered.ProposerDeliveredVersion);
        Assert.Equal(TownInteractionState.MaximumPending, play.State.Character("a").Interaction.Entries.Count);
        Assert.Equal(0, play.State.Character("a").Interaction.PendingCount);
        Assert.Contains("Accepted", TownTextRenderer.Render(TownObservation.Capture(play.History.State, "a")), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Acceptance_preserves_future_completion_and_refund_space_for_either_party(bool producerIsProposer, bool protectRefund) {
        var initial = TownGameFactory.Create(4);
        string item = protectRefund ? "grain" : "meal";
        TownCharacter[] actors = initial.World.Business.Characters.Select(actor => actor.Id switch {
            "a" => actor with { Inventory = new(new Dictionary<string, int> {
                ["grain"] = protectRefund ? int.MaxValue : 2, ["meal"] = protectRefund ? 1 : int.MaxValue - 2
            }) },
            "b" => actor with { Inventory = new(new Dictionary<string, int> { ["grain"] = 1, ["meal"] = 1 }) },
            _ => actor
        }).ToArray();
        var play = new Play(Seed(initial.World.Business, actors), initial.Cursor);
        Assert.True((await play.Act("a", "go market-workshop")).Accepted);
        Assert.True((await play.Act("b", "go market-workshop")).Accepted);
        Assert.True((await play.Act("c", "wait 4")).Accepted);
        Assert.True((await play.Act("a", "produce meal 1")).Accepted);
        TownActivity activity = play.State.Character("a").Activity!;
        if (producerIsProposer) {
            Assert.True((await play.Act("b", "say a trade while cooking")).Accepted);
            Assert.True((await play.Act("a", $"offer b money 1 {item} 1 4")).Accepted);
        } else {
            Assert.True((await play.Act("b", $"offer a {item} 1 money 1 4")).Accepted);
        }
        string id = Assert.Single(play.State.Trades.Offers).Id;
        TownBusinessState before = play.State;
        Assert.False((await play.Act(producerIsProposer ? "b" : "a", $"accept {id}")).Accepted);
        AssertBalances(before, play.State);
        Assert.Equal(activity, play.State.Character("a").Activity);
        Assert.Equal(TownTradeStatus.Active, Assert.Single(play.State.Trades.Offers).Status);
    }

    private static void AssertBalances(TownBusinessState before, TownBusinessState after) {
        foreach (TownCharacter actor in before.Characters) {
            Assert.Equal(actor.Money, after.Character(actor.Id).Money);
            Assert.Equal(actor.Inventory.Items.OrderBy(pair => pair.Key), after.Character(actor.Id).Inventory.Items.OrderBy(pair => pair.Key));
        }
        after.Economy.Verify(after);
    }
    private static TownWorld Copy(TownBusinessState before, TownCharacter[] actors, TownEconomicTotals? economy = null) =>
        TownEngine.Rebuild(new(before.Time, before.EndTime, actors, before.Market, economy ?? before.Economy, before.Spatial,
            before.LastServedActorId, before.OpenDecisionActorId, before.Generation, before.Ended, before.Trades));
    private static TownWorld Seed(TownBusinessState before, TownCharacter[] actors) => Copy(before, actors,
        new(actors.Sum(actor => actor.Money), TownContent.Items.ToDictionary(item => item, item => actors.Sum(actor => (long)actor.Inventory.Count(item)))));

    private sealed class Play {
        public InMemoryOccurrenceHistory<TownWorld, TownFact> History { get; }
        public TownBusinessState State => History.State.Business;
        private readonly SimulationKernel<TownWorld, TownPrediction, TownFact> _kernel;
        private string? _expectedActor, _command;
        private int _calls;
        public int Calls => _calls;
        public Play() : this(TownGameFactory.Create(4)) { }
        private Play((TownWorld World, KernelCursor Cursor) initial) : this(initial.World, initial.Cursor) { }
        public Play(TownWorld world, KernelCursor cursor) {
            History = new(world, cursor);
            _kernel = TownEngine.CreateControlledKernel(History, (observation, _) => {
                Assert.Equal(_expectedActor, observation.ActorId);
                Assert.NotNull(_command);
                _calls++;
                return ValueTask.FromResult(new TownControllerOperation(_command!, $"trade-tool-{_calls}", $"trade-op-{_calls}"));
            });
        }
        public async Task<TownFact> Act(string actor, string command) {
            _expectedActor = actor; _command = command;
            int before = _calls;
            for (int step = 0; step < 100; step++) {
                Assert.Equal(StepStatus.Committed, await _kernel.StepAsync(State.EndTime));
                State.Economy.Verify(State);
                if (_calls != before) { _command = null; return Assert.Single(_kernel.LastCompletion!.Event.Facts); }
            }
            throw new InvalidOperationException("Expected controller was not reached within 100 occurrences.");
        }
        public async Task Open(string actor) {
            _expectedActor = null; _command = null;
            for (int step = 0; step < 100; step++) {
                if (State.OpenDecisionActorId is { } open) { Assert.Equal(actor, open); return; }
                Assert.Equal(StepStatus.Committed, await _kernel.StepAsync(State.EndTime));
                State.Economy.Verify(State);
            }
            Assert.Fail("Expected published opening was not reached within 100 occurrences.");
        }
        public async Task Until(Func<TownBusinessState, bool> condition) {
            _expectedActor = null; _command = null;
            for (int step = 0; step < 100; step++) {
                if (condition(State)) { return; }
                Assert.Equal(StepStatus.Committed, await _kernel.StepAsync(State.EndTime));
                State.Economy.Verify(State);
            }
            Assert.Fail("Expected natural occurrence was not reached within 100 steps.");
        }
    }
}
