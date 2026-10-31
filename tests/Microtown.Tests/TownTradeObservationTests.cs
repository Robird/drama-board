using System.Text.Json;
using DramaBoard.Inventory;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.Microtown.Tests;

public sealed class TownTradeObservationTests {
    [Fact]
    public void Own_offer_terms_are_complete_but_acceptance_does_not_probe_counterparty_assets() {
        TownTradeOffer offer = Offer("known-offer", "b", "a");
        TownWorld funded = World([offer]);
        TownWorld spent = World([offer], change: actor => actor.Id == "b" ? actor with {
            Inventory = new ItemInventory(), Money = 123456,
            FoodReserve = 19 * TownContent.Hour, MinimumFoodReserve = 19 * TownContent.Hour
        } : actor);
        TownObservation first = TownObservation.Capture(funded, "a");
        TownObservation second = TownObservation.Capture(spent, "a");
        Assert.Equal(TownTextRenderer.Render(first), TownTextRenderer.Render(second));
        TownVisibleTradeOffer visible = Assert.Single(second.TradeOffers);
        Assert.Equal("meal", visible.Give.AssetId);
        Assert.Equal(5, visible.Want.Amount);
        TownVisibleTradeAction accept = visible.Actions.Single(action => action.Command == "accept known-offer");
        Assert.True(accept.CanAttempt);
        Assert.True(accept.RevalidatesCounterparty);
        Assert.Null(TownPlanning.Check(funded.Business, "a", TownCommand.Parse(accept.Command)));
        Assert.NotNull(TownPlanning.Check(spent.Business, "a", TownCommand.Parse(accept.Command)));
        string material = TownTextRenderer.Render(second);
        Assert.Contains("You give 5 money; you receive 1 meal", material);
        Assert.Contains("not guaranteed executable", material);
        Assert.Contains("reserves no inventory or money", material);
    }

    [Fact]
    public void Uninvolved_offers_and_other_participants_delivery_markers_do_not_enter_projection() {
        TownTradeOffer own = Offer("known-offer", "b", "a");
        TownWorld first = World([own]);
        TownWorld second = World([
            own with { ProposerDeliveredVersion = 1, ProposerDeliveryDecisionSequence = 91 },
            Offer("secret-unrelated-offer", "b", "c")
        ], change: actor => actor.Id == "b" ? actor with { DecisionSequence = 91 } : actor);
        TownObservation snapshot = TownObservation.Capture(second, "a");
        Assert.Equal(TownTextRenderer.Render(TownObservation.Capture(first, "a")), TownTextRenderer.Render(snapshot));
        string serialized = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("secret-unrelated-offer", serialized);
        Assert.DoesNotContain("ProposerDeliveredVersion", serialized);
        Assert.DoesNotContain("DeliveryDecisionSequence", serialized);
        Assert.Single(snapshot.TradeOffers);
        Assert.Throws<NotSupportedException>(() => ((IList<TownVisibleTradeOffer>)snapshot.TradeOffers).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<TownVisibleTradeAction>)snapshot.TradeOffers[0].Actions).Clear());
    }

    [Fact]
    public void Terminal_terms_remain_visible_with_full_speech_inbox_and_rendering_does_not_consume_them() {
        TownInteractionState inbox = TownInteractionState.Empty;
        for (int i = 0; i < TownInteractionState.MaximumPending; i++) {
            inbox = inbox.Append("speech-" + i, "b", "a", ModelTime.Zero, TownInteractionKind.Speech, "ordinary message");
        }
        TownTradeOffer terminal = Offer("finished-offer", "b", "a").Close(TownTradeStatus.Accepted, ModelTime.Zero);
        TownWorld world = World([terminal], change: actor => actor.Id == "a" ? actor with { Interaction = inbox } : actor);
        TownObservation frozen = TownObservation.Capture(world, "a");
        string material = TownTextRenderer.Render(frozen);
        Assert.Contains("finished-offer: Accepted", material);
        Assert.Contains("Both transfers already happened", material);
        Assert.True(Assert.Single(frozen.TradeOffers).AwaitingDelivery);
        Assert.Empty(frozen.TradeOffers[0].Actions);
        Assert.Equal(material, TownTextRenderer.Render(frozen));
        Assert.Equal(0, world.Business.Trades.Offers[0].RecipientDeliveredVersion);

        TownTradeState delivered = world.Business.Trades.OpenDecision("a", 1);
        TownWorld later = World(delivered.Offers, change: actor => actor.Id == "a" ? actor with { Interaction = inbox } : actor);
        Assert.True(Assert.Single(TownObservation.Capture(later, "a").TradeOffers).NewInLatestDecision);
        Assert.Equal(material, TownTextRenderer.Render(frozen));
    }

    [Fact]
    public void Participant_controls_explain_location_expiry_and_busy_continue_without_inventing_agreement() {
        TownTradeOffer offer = Offer("local-offer", "b", "a");
        TownWorld remote = World([offer], places: new Dictionary<string, string> { ["a"] = "market", ["b"] = "home" });
        TownVisibleTradeOffer remoteOffer = Assert.Single(TownObservation.Capture(remote, "a").TradeOffers);
        Assert.NotNull(remoteOffer.Actions.Single(action => action.Command == "accept local-offer").BlockedReason);
        Assert.True(remoteOffer.Actions.Single(action => action.Command == "reject local-offer").CanAttempt);

        TownWorld expired = World([offer], time: offer.ExpiresAt);
        Assert.NotNull(Assert.Single(TownObservation.Capture(expired, "a").TradeOffers).Actions
            .Single(action => action.Command == "accept local-offer").BlockedReason);

        TownInteractionState response = TownInteractionState.Empty.Append("response", "b", "a", ModelTime.Zero,
            TownInteractionKind.Speech, "Please review our offer", needsResponse: true).OpenDecision();
        TownWorld busy = World([offer], change: actor => actor.Id == "a" ? actor with {
            Interaction = response, Activity = new(1, TownActivityKind.Wait, new(2 * TownContent.Hour)), NextActivityId = 2
        } : actor);
        TownObservation observation = TownObservation.Capture(busy, "a");
        Assert.True(observation.Actions.Single(action => action.Example == "continue").Available);
        Assert.True(Assert.Single(observation.TradeOffers).Actions.Single(action => action.Command == "accept local-offer").CanAttempt);
        string material = TownTextRenderer.Render(observation);
        Assert.Contains("continue releases the response without accepting", material);
        Assert.Contains("at the exact expiry time", material);
        Assert.Contains("preserve the current activity", material);
    }

    [Fact]
    public void Proposal_examples_are_parseable_and_proposer_gets_withdraw_not_accept() {
        TownWorld world = World([Offer("my-offer", "a", "b")]);
        TownObservation observation = TownObservation.Capture(world, "a");
        Assert.All(observation.TradeProposals, proposal => {
            TownCommand command = TownCommand.Parse(proposal.Command);
            Assert.NotEqual(TownCommandKind.Invalid, command.Kind);
            Assert.Equal(TownTradeRules.CheckVisible(world.Business, "a", command), proposal.BlockedReason);
            Assert.True(proposal.CreatesOffer);
            Assert.False(proposal.RevalidatesCounterparty);
        });
        Assert.Equal("withdraw my-offer", Assert.Single(Assert.Single(observation.TradeOffers).Actions).Command);
        Assert.Contains("offer <resident> <give-asset> <give-count> <want-asset> <want-count> <hours>", TownTextRenderer.Render(observation));
    }

    private static TownTradeOffer Offer(string id, string proposer, string recipient) => new(id, proposer, recipient,
        new("meal", 1), new("money", 5), ModelTime.Zero, new(4 * TownContent.Hour));

    private static TownWorld World(IEnumerable<TownTradeOffer> offers, Func<TownCharacter, TownCharacter>? change = null,
        IReadOnlyDictionary<string, string>? places = null, ModelTime? time = null) {
        TownBusinessState initial = TownGameFactory.Create().World.Business;
        TownCharacter[] actors = initial.Characters.Select(actor => change?.Invoke(actor) ?? actor)
            .Select(actor => actor.Id == "a" ? actor with { DecisionSequence = 1 } : actor).ToArray();
        var spatial = GraphSpatialState.Create(TownContent.Graph, actors.Select(actor => new EntityPlacement(new(actor.Id),
            new(places?.GetValueOrDefault(actor.Id) ?? "market"))));
        var items = TownContent.Items.ToDictionary(item => item, item => actors.Sum(actor => (long)actor.Inventory.Count(item) +
            (actor.Activity?.InputItem == item ? actor.Activity.InputCount : 0)), StringComparer.Ordinal);
        var business = new TownBusinessState(time ?? initial.Time, initial.EndTime, actors, initial.Market,
            new TownEconomicTotals(actors.Sum(actor => actor.Money), items), spatial, openDecisionActorId: "a", trades: new(offers));
        return TownEngine.Rebuild(business);
    }
}
