using Atelia.DurableGraph;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Microtown;

[DurableType("DramaBoard.Microtown.TradeStatus", 1)]
public enum TownTradeStatus { Active, Accepted, Rejected, Withdrawn, Expired, ParticipantFailed, WorldEnded }

[DurableType("DramaBoard.Microtown.TradeAsset", 1)]
public sealed partial record TownTradeAsset(
    [field: DurableField(1)] string AssetId,
    [field: DurableField(2)] long Amount) : IDurableObject {
    public bool IsMoney => AssetId == "money";

    public void Validate() {
        if (string.IsNullOrWhiteSpace(AssetId) || Amount <= 0 ||
            !IsMoney && (!TownContent.Items.Contains(AssetId) || Amount > 1000)) {
            throw new InvalidOperationException("A trade asset requires positive money or 1–1000 units of a known item.");
        }
    }
}

/// <summary>An explicit offer and each participant's last delivered version. Delivery is evidence of a decision opening, not of agreement.</summary>
[DurableType("DramaBoard.Microtown.TradeOffer", 1)]
public sealed partial record TownTradeOffer(
    [field: DurableField(1)] string Id,
    [field: DurableField(2)] string ProposerId,
    [field: DurableField(3)] string RecipientId,
    [field: DurableField(4)] TownTradeAsset Give,
    [field: DurableField(5)] TownTradeAsset Want,
    [field: DurableField(6)] ModelTime CreatedAt,
    [field: DurableField(7)] ModelTime ExpiresAt,
    [field: DurableField(8)] TownTradeStatus Status = TownTradeStatus.Active,
    [field: DurableField(9)] ModelTime? ClosedAt = null,
    [field: DurableField(10)] int ProposerDeliveredVersion = 0,
    [field: DurableField(11)] int RecipientDeliveredVersion = 0,
    [field: DurableField(12)] long ProposerDeliveryDecisionSequence = 0,
    [field: DurableField(13)] long RecipientDeliveryDecisionSequence = 0) : IDurableObject {
    public int Version => Status == TownTradeStatus.Active ? 1 : 2;

    public bool Involves(string actorId) => actorId == ProposerId || actorId == RecipientId;

    public int DeliveredVersionFor(string actorId) => actorId == ProposerId ? ProposerDeliveredVersion
        : actorId == RecipientId ? RecipientDeliveredVersion : throw new ArgumentException("The resident is not a participant.", nameof(actorId));

    public long DeliveryDecisionSequenceFor(string actorId) => actorId == ProposerId ? ProposerDeliveryDecisionSequence
        : actorId == RecipientId ? RecipientDeliveryDecisionSequence : throw new ArgumentException("The resident is not a participant.", nameof(actorId));

    public TownTradeOffer MarkDelivered(string actorId, long decisionSequence) {
        if (decisionSequence <= 0) { throw new ArgumentOutOfRangeException(nameof(decisionSequence)); }
        return actorId == ProposerId ? this with { ProposerDeliveredVersion = Version, ProposerDeliveryDecisionSequence = decisionSequence }
            : actorId == RecipientId ? this with { RecipientDeliveredVersion = Version, RecipientDeliveryDecisionSequence = decisionSequence }
            : throw new ArgumentException("The resident is not a participant.", nameof(actorId));
    }

    public TownTradeOffer Close(TownTradeStatus status, ModelTime at) {
        if (Status != TownTradeStatus.Active || status == TownTradeStatus.Active || !Enum.IsDefined(status)) {
            throw new InvalidOperationException("Only an active offer can transition to a terminal status.");
        }
        TownTradeOffer closed = this with { Status = status, ClosedAt = at };
        closed.Validate(at);
        return closed;
    }

    public void Validate(ModelTime now) {
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(ProposerId) || string.IsNullOrWhiteSpace(RecipientId) ||
            ProposerId == RecipientId || Give is null || Want is null || !Enum.IsDefined(Status) ||
            CreatedAt < ModelTime.Zero || CreatedAt > now || ExpiresAt <= CreatedAt ||
            Status == TownTradeStatus.Active && (ClosedAt is not null || ExpiresAt < now) ||
            Status != TownTradeStatus.Active && (ClosedAt is null || ClosedAt < CreatedAt || ClosedAt > now) ||
            Status == TownTradeStatus.Expired && ClosedAt < ExpiresAt ||
            (Status is TownTradeStatus.Accepted or TownTradeStatus.Rejected or TownTradeStatus.Withdrawn) && ClosedAt >= ExpiresAt ||
            ProposerDeliveredVersion < 0 || ProposerDeliveredVersion > Version ||
            RecipientDeliveredVersion < 0 || RecipientDeliveredVersion > Version ||
            ProposerDeliveryDecisionSequence < 0 || RecipientDeliveryDecisionSequence < 0 ||
            ProposerDeliveredVersion == 0 && ProposerDeliveryDecisionSequence != 0 ||
            RecipientDeliveredVersion == 0 && RecipientDeliveryDecisionSequence != 0 ||
            ProposerDeliveredVersion > 0 && ProposerDeliveryDecisionSequence == 0 ||
            RecipientDeliveredVersion > 0 && RecipientDeliveryDecisionSequence == 0) {
            throw new InvalidOperationException("Invalid trade offer, lifetime, or participant delivery boundary.");
        }
        Give.Validate();
        Want.Validate();
    }
}

/// <summary>Immutable bounded offer ledger. Reclamation occurs only at a participant's later decision opening.</summary>
[DurableType("DramaBoard.Microtown.TradeState", 1)]
public sealed partial class TownTradeState : IDurableObject {
    public const int MaximumOffers = 24, MaximumPerProposer = 8;
    public static TownTradeState Empty { get; } = new();
    [DurableField(1)] private readonly List<TownTradeOffer> _offers;
    public IReadOnlyList<TownTradeOffer> Offers => _offers.AsReadOnly();

    public TownTradeState(IEnumerable<TownTradeOffer>? offers = null) => _offers = offers?.ToList() ?? [];

    public TownTradeState Add(TownTradeOffer offer) {
        ArgumentNullException.ThrowIfNull(offer);
        if (_offers.Count >= MaximumOffers || _offers.Count(current => current.ProposerId == offer.ProposerId) >= MaximumPerProposer) {
            throw new InvalidOperationException("The retained offer ledger is full for this town or proposer.");
        }
        if (_offers.Any(current => current.Id == offer.Id)) { throw new InvalidOperationException("Trade offer identity already exists."); }
        return new(_offers.Append(offer));
    }

    public TownTradeState Replace(TownTradeOffer offer) {
        ArgumentNullException.ThrowIfNull(offer);
        if (!_offers.Any(current => current.Id == offer.Id)) { throw new InvalidOperationException("Cannot replace an unknown trade offer."); }
        return new(_offers.Select(current => current.Id == offer.Id ? offer : current));
    }

    /// <summary>First retires previously delivered terminal offers, then delivers retained entries to this opening.
    /// A failed participant is exempt from future delivery; that exemption never invents delivery evidence.</summary>
    public TownTradeState OpenDecision(string actorId, long decisionSequence, IReadOnlyCollection<string>? failedActorIds = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (decisionSequence <= 0) { throw new ArgumentOutOfRangeException(nameof(decisionSequence)); }
        bool Finished(TownTradeOffer offer) => offer.Status != TownTradeStatus.Active && offer.Involves(actorId) &&
            decisionSequence > offer.DeliveryDecisionSequenceFor(actorId) &&
            (offer.ProposerDeliveredVersion == 2 || failedActorIds?.Contains(offer.ProposerId) == true) &&
            (offer.RecipientDeliveredVersion == 2 || failedActorIds?.Contains(offer.RecipientId) == true);
        return new(_offers.Where(offer => !Finished(offer))
            .Select(offer => offer.Involves(actorId) && offer.DeliveredVersionFor(actorId) < offer.Version
                ? offer.MarkDelivered(actorId, decisionSequence) : offer));
    }

    public void Validate(TownBusinessState business) {
        ArgumentNullException.ThrowIfNull(business);
        if (_offers is null || _offers.Count > MaximumOffers || _offers.Any(offer => offer is null) ||
            _offers.GroupBy(offer => offer.ProposerId, StringComparer.Ordinal).Any(group => group.Count() > MaximumPerProposer)) {
            throw new InvalidOperationException("Invalid retained trade ledger capacity.");
        }
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (TownTradeOffer offer in _offers) {
            offer.Validate(business.Time);
            TownCharacter? proposer = business.Characters.FirstOrDefault(actor => actor.Id == offer.ProposerId);
            TownCharacter? recipient = business.Characters.FirstOrDefault(actor => actor.Id == offer.RecipientId);
            if (!identities.Add(offer.Id) || proposer is null || recipient is null ||
                offer.Status == TownTradeStatus.Active && (business.Ended || proposer.Failed || recipient.Failed) ||
                offer.ProposerDeliveryDecisionSequence > proposer.DecisionSequence ||
                offer.RecipientDeliveryDecisionSequence > recipient.DecisionSequence) {
                throw new InvalidOperationException("An offer needs unique identity and existing participants; active offers need a running world and living participants.");
            }
        }
    }
}
