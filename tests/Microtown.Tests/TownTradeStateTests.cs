using DramaBoard.Kernel.Time;

namespace DramaBoard.Microtown.Tests;

public sealed class TownTradeStateTests {
    [Fact]
    public void FullLedgerStillAllowsClosingAndNeverPrunesDuringAdd() {
        var offers = new[] { "a", "b", "c" }.SelectMany(actor => Enumerable.Range(1, TownTradeState.MaximumPerProposer)
            .Select(index => Offer($"{actor}-{index}", actor, actor == "a" ? "b" : "a"))).ToArray();
        var full = new TownTradeState(offers);
        full.Validate(TownGameFactory.Create().World.Business);
        Assert.Equal(TownTradeState.MaximumOffers, full.Offers.Count);
        Assert.Throws<InvalidOperationException>(() => full.Add(Offer("overflow")));
        TownTradeOffer closed = full.Offers[0].Close(TownTradeStatus.Withdrawn, ModelTime.Zero);
        TownTradeState replaced = full.Replace(closed);
        Assert.Equal(TownTradeStatus.Withdrawn, replaced.Offers[0].Status);
        Assert.Equal(TownTradeStatus.Active, full.Offers[0].Status);
        Assert.Equal(TownTradeState.MaximumOffers, replaced.Offers.Count);
        Assert.Throws<InvalidOperationException>(() => replaced.Add(Offer("still-full")));
        Assert.Throws<InvalidOperationException>(() => full.Replace(Offer("unknown")));
    }

    [Fact]
    public void TerminalOfferSurvivesBothFirstDeliveriesAndOnlyALaterParticipantOpeningReclaimsIt() {
        TownTradeState active = TownTradeState.Empty.Add(Offer("offer"));
        TownTradeState openedActive = active.OpenDecision("a", 1).OpenDecision("b", 1);
        Assert.Equal(1, openedActive.Offers[0].ProposerDeliveredVersion);
        Assert.Equal(1, openedActive.Offers[0].RecipientDeliveredVersion);
        TownTradeState closed = openedActive.Replace(openedActive.Offers[0].Close(TownTradeStatus.Accepted, ModelTime.Zero));
        TownTradeState first = closed.OpenDecision("a", 2);
        Assert.Single(first.Offers);
        Assert.Equal(2, first.Offers[0].ProposerDeliveredVersion);
        Assert.Equal(1, first.Offers[0].RecipientDeliveredVersion);
        TownTradeState both = first.OpenDecision("b", 2);
        Assert.Single(both.Offers);
        Assert.Equal(2, both.Offers[0].RecipientDeliveredVersion);
        Assert.Single(both.OpenDecision("a", 2).Offers);
        Assert.Single(both.OpenDecision("b", 2).Offers);
        Assert.Single(both.OpenDecision("c", 1).Offers);
        Assert.Empty(both.OpenDecision("a", 3).Offers);
        Assert.Equal(1, openedActive.Offers[0].ProposerDeliveredVersion);
        Assert.Equal(TownTradeStatus.Active, active.Offers[0].Status);
    }

    [Fact]
    public void LaterOpeningsDoNotRedeliverAnAlreadySeenVersion() {
        TownTradeState first = TownTradeState.Empty.Add(Offer("offer")).OpenDecision("a", 1);
        TownTradeState repeated = first.OpenDecision("a", 2);
        Assert.Equal(1, repeated.Offers[0].ProposerDeliveredVersion);
        Assert.Equal(1, repeated.Offers[0].ProposerDeliveryDecisionSequence);
        Assert.Equal(0, repeated.Offers[0].RecipientDeliveredVersion);

        TownTradeState closed = repeated.Replace(repeated.Offers[0].Close(TownTradeStatus.Rejected, ModelTime.Zero));
        TownTradeState terminalDelivery = closed.OpenDecision("a", 3);
        TownTradeState waitingForRecipient = terminalDelivery.OpenDecision("a", 4);
        Assert.Single(waitingForRecipient.Offers);
        Assert.Equal(2, waitingForRecipient.Offers[0].ProposerDeliveredVersion);
        Assert.Equal(3, waitingForRecipient.Offers[0].ProposerDeliveryDecisionSequence);
        Assert.Equal(0, waitingForRecipient.Offers[0].RecipientDeliveredVersion);
        Assert.Equal(1, first.Offers[0].ProposerDeliveryDecisionSequence);
    }

    [Fact]
    public void FailureExemptsDeadParticipantWithoutInventingDeliveryOrDroppingSurvivorsUnreadResult() {
        TownTradeOffer failed = Offer("failed").Close(TownTradeStatus.ParticipantFailed, ModelTime.Zero);
        Assert.Equal(0, failed.ProposerDeliveredVersion);
        Assert.Equal(0, failed.ProposerDeliveryDecisionSequence);
        var state = new TownTradeState([failed]);
        TownTradeState delivered = state.OpenDecision("b", 4, ["a"]);
        Assert.Single(delivered.Offers);
        Assert.Equal(0, delivered.Offers[0].ProposerDeliveredVersion);
        Assert.Equal(0, delivered.Offers[0].ProposerDeliveryDecisionSequence);
        Assert.Equal(2, delivered.Offers[0].RecipientDeliveredVersion);
        Assert.Equal(4, delivered.Offers[0].RecipientDeliveryDecisionSequence);
        Assert.Single(delivered.OpenDecision("b", 4, ["a"]).Offers);
        Assert.Empty(delivered.OpenDecision("b", 5, ["a"]).Offers);
        Assert.Single(delivered.OpenDecision("b", 5).Offers);
        Assert.Equal(0, state.Offers[0].RecipientDeliveredVersion);
    }

    [Fact]
    public void PerProposerCapacityCountsRetainedTerminalOffers() {
        var state = new TownTradeState(Enumerable.Range(1, TownTradeState.MaximumPerProposer)
            .Select(index => Offer($"a-{index}").Close(TownTradeStatus.Rejected, ModelTime.Zero)));
        Assert.Throws<InvalidOperationException>(() => state.Add(Offer("a-more")));
        TownTradeState other = state.Add(Offer("b-one", "b", "a"));
        Assert.Equal(9, other.Offers.Count);
        Assert.Equal(8, state.Offers.Count);
        Assert.Throws<InvalidOperationException>(() => other.Add(Offer("b-one", "c", "a")));
    }

    [Fact]
    public void ConstructorCopiesInputAndPublicOfferCollectionCannotBeMutated() {
        var input = new List<TownTradeOffer> { Offer("offer") };
        var state = new TownTradeState(input);
        input.Clear();
        Assert.Single(state.Offers);
        ICollection<TownTradeOffer> view = Assert.IsAssignableFrom<ICollection<TownTradeOffer>>(state.Offers);
        Assert.True(view.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => view.Clear());
        Assert.Empty(TownTradeState.Empty.Offers);
    }

    [Fact]
    public void DeliveryIsParticipantSpecificAndDoesNotChangeTheOtherParticipantsEvidence() {
        TownTradeOffer original = Offer("offer");
        TownTradeOffer delivered = original.MarkDelivered("a", 7);
        Assert.Equal(1, delivered.DeliveredVersionFor("a"));
        Assert.Equal(7, delivered.DeliveryDecisionSequenceFor("a"));
        Assert.Equal(0, delivered.DeliveredVersionFor("b"));
        Assert.Equal(0, original.DeliveredVersionFor("a"));
        Assert.False(delivered.Involves("c"));
        Assert.Throws<ArgumentException>(() => delivered.MarkDelivered("c", 8));
        Assert.Throws<ArgumentException>(() => delivered.DeliveredVersionFor("c"));
        Assert.Throws<ArgumentOutOfRangeException>(() => delivered.MarkDelivered("a", 0));
        TownTradeOffer closed = delivered.Close(TownTradeStatus.Rejected, ModelTime.Zero);
        Assert.Equal(1, closed.DeliveredVersionFor("a"));
        Assert.Equal(7, closed.DeliveryDecisionSequenceFor("a"));
        Assert.Throws<InvalidOperationException>(() => closed.Close(TownTradeStatus.Accepted, ModelTime.Zero));
    }

    [Fact]
    public void AssetAndOfferValidationRejectsInvalidQuantitiesLifetimesAndDeliveryVersions() {
        new TownTradeAsset("money", long.MaxValue).Validate();
        new TownTradeAsset("meal", 1000).Validate();
        Assert.Throws<InvalidOperationException>(() => new TownTradeAsset("meal", 1001).Validate());
        Assert.Throws<InvalidOperationException>(() => new TownTradeAsset("unknown", 1).Validate());
        Assert.Throws<InvalidOperationException>(() => new TownTradeAsset("money", 0).Validate());
        TownTradeOffer offer = Offer("offer");
        offer.Validate(offer.ExpiresAt);
        Assert.Throws<InvalidOperationException>(() => offer.Validate(new ModelTime(offer.ExpiresAt.Ticks + 1)));
        Assert.Throws<InvalidOperationException>(() => offer.Close(TownTradeStatus.Expired, ModelTime.Zero));
        offer.Close(TownTradeStatus.Expired, offer.ExpiresAt).Validate(offer.ExpiresAt);
        Assert.Throws<InvalidOperationException>(() => (offer with { ProposerDeliveredVersion = 2 }).Validate(ModelTime.Zero));
        Assert.Throws<InvalidOperationException>(() => (offer with { ProposerDeliveredVersion = 1 }).Validate(ModelTime.Zero));
        Assert.Throws<InvalidOperationException>(() => (offer with { RecipientDeliveredVersion = 1 }).Validate(ModelTime.Zero));
        Assert.Throws<InvalidOperationException>(() => (offer with { RecipientDeliveryDecisionSequence = 1 }).Validate(ModelTime.Zero));
        Assert.Throws<InvalidOperationException>(() => (offer with { RecipientId = "a" }).Validate(ModelTime.Zero));
    }

    [Theory]
    [InlineData(TownTradeStatus.Accepted)]
    [InlineData(TownTradeStatus.Rejected)]
    [InlineData(TownTradeStatus.Withdrawn)]
    public void ManualTerminalTransitionsMustPrecedeExpiry(TownTradeStatus status) {
        TownTradeOffer offer = Offer("offer");
        var beforeExpiry = new ModelTime(offer.ExpiresAt.Ticks - 1);
        var afterExpiry = new ModelTime(offer.ExpiresAt.Ticks + 1);
        offer.Close(status, beforeExpiry).Validate(afterExpiry);
        Assert.Throws<InvalidOperationException>(() => offer.Close(status, offer.ExpiresAt));
        Assert.Throws<InvalidOperationException>(() => offer.Close(status, afterExpiry));
        Assert.Throws<InvalidOperationException>(() => (offer with { Status = status, ClosedAt = offer.ExpiresAt }).Validate(afterExpiry));
        Assert.Throws<InvalidOperationException>(() => (offer with { Status = status, ClosedAt = afterExpiry }).Validate(afterExpiry));
    }

    [Fact]
    public void LedgerRejectsDeliveryFromBeyondEitherParticipantsDecisionSequence() {
        TownBusinessState original = TownGameFactory.Create().World.Business;
        TownTradeOffer delivered = Offer("offer").MarkDelivered("a", 7).MarkDelivered("b", 3);
        var state = new TownTradeState([delivered]);
        Assert.Throws<InvalidOperationException>(() => state.Validate(original));
        TownBusinessState WithDecisions(long a, long b) => new(original.Time, original.EndTime,
            original.Characters.Select(actor => actor with { DecisionSequence = actor.Id == "a" ? a : actor.Id == "b" ? b : 0 }),
            original.Market, original.Economy, original.Spatial);
        Assert.Throws<InvalidOperationException>(() => state.Validate(WithDecisions(7, 2)));
        Assert.Throws<InvalidOperationException>(() => state.Validate(WithDecisions(6, 3)));
        state.Validate(WithDecisions(7, 3));
    }

    [Fact]
    public void LedgerValidationRequiresKnownLivingParticipantsForActiveOffersOnly() {
        TownBusinessState original = TownGameFactory.Create().World.Business;
        new TownTradeState([Offer("active")]).Validate(original);
        Assert.Throws<InvalidOperationException>(() => new TownTradeState([Offer("missing", "a", "missing")]).Validate(original));
        var failed = new TownBusinessState(original.Time, original.EndTime,
            original.Characters.Select(actor => actor.Id == "a" ? actor with { Failed = true } : actor),
            original.Market, original.Economy, original.Spatial);
        Assert.Throws<InvalidOperationException>(() => new TownTradeState([Offer("active")]).Validate(failed));
        new TownTradeState([Offer("closed").Close(TownTradeStatus.ParticipantFailed, ModelTime.Zero)]).Validate(failed);
        var ended = new TownBusinessState(original.Time, original.EndTime, original.Characters,
            original.Market, original.Economy, original.Spatial, ended: true);
        Assert.Throws<InvalidOperationException>(() => new TownTradeState([Offer("active")]).Validate(ended));
        new TownTradeState([Offer("ended").Close(TownTradeStatus.WorldEnded, ModelTime.Zero)]).Validate(ended);
    }

    private static TownTradeOffer Offer(string id, string proposer = "a", string recipient = "b") =>
        new(id, proposer, recipient, new("meal", 1), new("money", 2), ModelTime.Zero, new ModelTime(TownContent.Hour));
}
