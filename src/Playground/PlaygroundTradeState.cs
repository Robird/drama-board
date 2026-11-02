using Atelia.DurableGraph;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Playground;

[DurableType("DramaBoard.Playground.TradeStatus", 1)]
public enum PlaygroundTradeStatus { Active, Accepted, Rejected, Withdrawn, Expired, ParticipantFailed }

[DurableType("DramaBoard.Playground.TradeAsset", 1)]
public sealed partial record PlaygroundTradeAsset(
    [field: DurableField(1)] string AssetId,
    [field: DurableField(2)] long Amount) : IDurableObject {
    public bool IsMoney => AssetId == "money";

    public void Validate() {
        if (string.IsNullOrWhiteSpace(AssetId) || Amount <= 0 ||
            !IsMoney && Amount > 1000) {
            throw new InvalidOperationException("A trade asset requires positive money or 1–1000 item units.");
        }
    }
    public void Validate(PlaygroundWorldDefinition definition) {
        Validate();
        if (!IsMoney && !definition.Items.ContainsKey(AssetId)) {
            throw new InvalidOperationException("A trade asset must belong to the world's item definition.");
        }
    }
}

/// <summary>An explicit offer and each participant's last delivered version. Delivery is evidence of a decision opening, not of agreement.</summary>
[DurableType("DramaBoard.Playground.TradeOffer", 1)]
public sealed partial record PlaygroundTradeOffer(
    [field: DurableField(1)] string Id,
    [field: DurableField(2)] string ProposerId,
    [field: DurableField(3)] string RecipientId,
    [field: DurableField(4)] PlaygroundTradeAsset Give,
    [field: DurableField(5)] PlaygroundTradeAsset Want,
    [field: DurableField(6)] ModelTime CreatedAt,
    [field: DurableField(7)] ModelTime ExpiresAt,
    [field: DurableField(8)] PlaygroundTradeStatus Status = PlaygroundTradeStatus.Active,
    [field: DurableField(9)] ModelTime? ClosedAt = null,
    [field: DurableField(10)] int ProposerDeliveredVersion = 0,
    [field: DurableField(11)] int RecipientDeliveredVersion = 0,
    [field: DurableField(12)] long ProposerDeliveryContextSequence = 0,
    [field: DurableField(13)] long RecipientDeliveryContextSequence = 0) : IDurableObject {
    public int Version => Status == PlaygroundTradeStatus.Active ? 1 : 2;

    public bool Involves(string actorId) => actorId == ProposerId || actorId == RecipientId;

    public int DeliveredVersionFor(string actorId) => actorId == ProposerId ? ProposerDeliveredVersion
        : actorId == RecipientId ? RecipientDeliveredVersion : throw new ArgumentException("The resident is not a participant.", nameof(actorId));

    public long DeliveryContextSequenceFor(string actorId) => actorId == ProposerId ? ProposerDeliveryContextSequence
        : actorId == RecipientId ? RecipientDeliveryContextSequence : throw new ArgumentException("The resident is not a participant.", nameof(actorId));

    public PlaygroundTradeOffer MarkDelivered(string actorId, long contextSequence) {
        if (contextSequence <= 0) { throw new ArgumentOutOfRangeException(nameof(contextSequence)); }
        return actorId == ProposerId ? this with { ProposerDeliveredVersion = Version, ProposerDeliveryContextSequence = contextSequence }
            : actorId == RecipientId ? this with { RecipientDeliveredVersion = Version, RecipientDeliveryContextSequence = contextSequence }
            : throw new ArgumentException("The resident is not a participant.", nameof(actorId));
    }

    public PlaygroundTradeOffer Close(PlaygroundTradeStatus status, ModelTime at) {
        if (Status != PlaygroundTradeStatus.Active || status == PlaygroundTradeStatus.Active || !Enum.IsDefined(status)) {
            throw new InvalidOperationException("Only an active offer can transition to a terminal status.");
        }
        PlaygroundTradeOffer closed = this with { Status = status, ClosedAt = at };
        closed.Validate(at);
        return closed;
    }

    public void Validate(ModelTime now) {
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(ProposerId) || string.IsNullOrWhiteSpace(RecipientId) ||
            ProposerId == RecipientId || Give is null || Want is null || !Enum.IsDefined(Status) ||
            CreatedAt < ModelTime.Zero || CreatedAt > now || ExpiresAt <= CreatedAt ||
            Status == PlaygroundTradeStatus.Active && (ClosedAt is not null || ExpiresAt < now) ||
            Status != PlaygroundTradeStatus.Active && (ClosedAt is null || ClosedAt < CreatedAt || ClosedAt > now) ||
            Status == PlaygroundTradeStatus.Expired && ClosedAt < ExpiresAt ||
            (Status is PlaygroundTradeStatus.Accepted or PlaygroundTradeStatus.Rejected or PlaygroundTradeStatus.Withdrawn) && ClosedAt >= ExpiresAt ||
            ProposerDeliveredVersion < 0 || ProposerDeliveredVersion > Version ||
            RecipientDeliveredVersion < 0 || RecipientDeliveredVersion > Version ||
            ProposerDeliveryContextSequence < 0 || RecipientDeliveryContextSequence < 0 ||
            ProposerDeliveredVersion == 0 && ProposerDeliveryContextSequence != 0 ||
            RecipientDeliveredVersion == 0 && RecipientDeliveryContextSequence != 0 ||
            ProposerDeliveredVersion > 0 && ProposerDeliveryContextSequence == 0 ||
            RecipientDeliveredVersion > 0 && RecipientDeliveryContextSequence == 0) {
            throw new InvalidOperationException("Invalid trade offer, lifetime, or participant delivery boundary.");
        }
        Give.Validate();
        Want.Validate();
    }
    public void Validate(ModelTime now, PlaygroundWorldDefinition definition) {
        Validate(now);
        Give.Validate(definition);
        Want.Validate(definition);
    }
}

/// <summary>Immutable bounded offer ledger. Reclamation occurs only at a participant's later decision opening.</summary>
[DurableType("DramaBoard.Playground.TradeState", 1)]
public sealed partial class PlaygroundTradeState : IDurableObject {
    public const int MaximumOffers = 24, MaximumPerProposer = 8;
    public static PlaygroundTradeState Empty { get; } = new();
    [DurableField(1)] private readonly List<PlaygroundTradeOffer> _offers;
    public IReadOnlyList<PlaygroundTradeOffer> Offers => _offers.AsReadOnly();

    public PlaygroundTradeState(IEnumerable<PlaygroundTradeOffer>? offers = null) => _offers = offers?.ToList() ?? [];

    public PlaygroundTradeState Add(PlaygroundTradeOffer offer) {
        ArgumentNullException.ThrowIfNull(offer);
        if (_offers.Count >= MaximumOffers || _offers.Count(current => current.ProposerId == offer.ProposerId) >= MaximumPerProposer) {
            throw new InvalidOperationException("The retained offer ledger is full for this town or proposer.");
        }
        if (_offers.Any(current => current.Id == offer.Id)) { throw new InvalidOperationException("Trade offer identity already exists."); }
        return new(_offers.Append(offer));
    }

    public PlaygroundTradeState Replace(PlaygroundTradeOffer offer) {
        ArgumentNullException.ThrowIfNull(offer);
        if (!_offers.Any(current => current.Id == offer.Id)) { throw new InvalidOperationException("Cannot replace an unknown trade offer."); }
        return new(_offers.Select(current => current.Id == offer.Id ? offer : current));
    }

    /// <summary>First retires previously delivered terminal offers, then delivers retained entries to this opening.
    /// A failed participant is exempt from future delivery; that exemption never invents delivery evidence.</summary>
    public PlaygroundTradeState OpenContext(string actorId, long contextSequence, IReadOnlyCollection<string>? failedActorIds = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (contextSequence <= 0) { throw new ArgumentOutOfRangeException(nameof(contextSequence)); }
        bool Finished(PlaygroundTradeOffer offer) => offer.Status != PlaygroundTradeStatus.Active && offer.Involves(actorId) &&
            contextSequence > offer.DeliveryContextSequenceFor(actorId) &&
            (offer.ProposerDeliveredVersion == 2 || failedActorIds?.Contains(offer.ProposerId) == true) &&
            (offer.RecipientDeliveredVersion == 2 || failedActorIds?.Contains(offer.RecipientId) == true);
        return new(_offers.Where(offer => !Finished(offer))
            .Select(offer => offer.Involves(actorId) && offer.DeliveredVersionFor(actorId) < offer.Version
                ? offer.MarkDelivered(actorId, contextSequence) : offer));
    }

    public bool SameState(PlaygroundTradeState? other) => other is not null && Offers.SequenceEqual(other.Offers);

    public void Validate(PlaygroundBusinessState business) {
        ArgumentNullException.ThrowIfNull(business);
        if (_offers is null || _offers.Count > MaximumOffers || _offers.Any(offer => offer is null) ||
            _offers.GroupBy(offer => offer.ProposerId, StringComparer.Ordinal).Any(group => group.Count() > MaximumPerProposer)) {
            throw new InvalidOperationException("Invalid retained trade ledger capacity.");
        }
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (PlaygroundTradeOffer offer in _offers) {
            offer.Validate(business.Time, business.Definition);
            PlaygroundCharacter? proposer = business.Characters.FirstOrDefault(actor => actor.Id == offer.ProposerId);
            PlaygroundCharacter? recipient = business.Characters.FirstOrDefault(actor => actor.Id == offer.RecipientId);
            if (!identities.Add(offer.Id) || proposer is null || recipient is null ||
                offer.Status == PlaygroundTradeStatus.Active && (!proposer.IsActive || !recipient.IsActive) ||
                offer.ProposerDeliveryContextSequence > (business.InteractionStates[proposer.Id].CurrentContext?.ContextSequence ?? 0) ||
                offer.RecipientDeliveryContextSequence > (business.InteractionStates[recipient.Id].CurrentContext?.ContextSequence ?? 0)) {
                throw new InvalidOperationException("An offer needs unique identity and existing participants; active offers need a running world and living participants.");
            }
        }
    }
}
