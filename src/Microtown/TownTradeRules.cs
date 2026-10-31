using DramaBoard.Inventory;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Microtown;

/// <summary>Town-specific bilateral exchange. Offers reserve lifecycle records, never assets.</summary>
public static class TownTradeRules {
    public static bool IsTradeCommand(TownCommand command) => command.Kind is
        TownCommandKind.Offer or TownCommandKind.Accept or TownCommandKind.Reject or TownCommandKind.Withdraw;

    /// <summary>Checks personal assets, personal offer slots and publicly known terms/eligibility only.</summary>
    /// <remarks>Acceptance is provisional: the counterparty's private assets and inventory limits are checked on submission.</remarks>
    public static string? CheckVisible(TownBusinessState state, string actorId, TownCommand command) {
        TownCharacter actor = state.Character(actorId);
        if (state.Ended || actor.Failed) { return "This resident can no longer act."; }
        if (command.Kind == TownCommandKind.Offer) {
            TownCharacter? other = state.Characters.FirstOrDefault(resident => resident.Id == command.ResidentId);
            if (other is null || other.Id == actorId || other.Failed || state.Place(actorId) is not { } place || state.Place(other.Id) != place) {
                return "Create an offer for another living resident at the same place.";
            }
            if (!ValidAsset(command.GiveAsset) || !ValidAsset(command.WantAsset)) { return "Each leg requires a known item (1..1000) or money (positive integer)."; }
            if (command.DurationHours is < 1 or > 24 || state.Time.Ticks > long.MaxValue - command.DurationHours * TownContent.Hour) {
                return "Offer duration must be from 1 through 24 whole hours.";
            }
            if (state.Trades.Offers.Count(entry => entry.ProposerId == actorId) >= TownTradeState.MaximumPerProposer) {
                return "Your retained offer slots are full; existing offers can still be accepted or closed.";
            }
            return CanPay(actor, command.GiveAsset!) ? null : "You do not currently own the full asset you offer.";
        }
        if (command.Kind is not (TownCommandKind.Accept or TownCommandKind.Reject or TownCommandKind.Withdraw)) { return "Unknown trade operation."; }
        TownTradeOffer? offer = state.Trades.Offers.FirstOrDefault(entry => entry.Id == command.Target && entry.Involves(actorId));
        if (offer is null) { return "No retained offer with this ID involves you."; }
        if (offer.Status != TownTradeStatus.Active) { return "This offer is already closed."; }
        if (state.Time >= offer.ExpiresAt) { return "This offer has expired."; }
        if (command.Kind == TownCommandKind.Withdraw) { return offer.ProposerId == actorId ? null : "Only the proposer can withdraw this offer."; }
        if (offer.RecipientId != actorId) { return "Only the recipient can accept or reject this offer."; }
        if (command.Kind == TownCommandKind.Reject) { return null; }
        TownCharacter proposer = state.Character(offer.ProposerId);
        if (proposer.Failed || state.Place(actorId) is not { } currentPlace || state.Place(proposer.Id) != currentPlace) {
            return "Both living participants must be at the same place to accept.";
        }
        return TryExchange(actor, offer.Want, offer.Give, out _) ? null
            : "Your full payment or resulting inventory/money cannot settle this offer while preserving your activity.";
    }

    public static string? Check(TownBusinessState state, string actorId, TownCommand command) {
        string? error = CheckVisible(state, actorId, command);
        if (error is not null) { return error; }
        if (command.Kind == TownCommandKind.Offer) {
            return state.Trades.Offers.Count >= TownTradeState.MaximumOffers ? "The town cannot retain more offers right now." : null;
        }
        if (command.Kind != TownCommandKind.Accept) { return null; }
        TownTradeOffer offer = state.Trades.Offers.Single(entry => entry.Id == command.Target);
        return TryExchange(state.Character(offer.ProposerId), offer.Give, offer.Want, out _) ? null
            : "The counterparty cannot currently settle the agreed exchange.";
    }

    internal static (IReadOnlyList<TownCharacter> Characters, TownTradeState Trades, string Result) Apply(
        TownBusinessState state, string actorId, TownCommand command, long operationSequence) {
        TownTradeState trades = state.Trades;
        TownCharacter[] characters = state.Characters.ToArray();
        if (command.Kind == TownCommandKind.Offer) {
            var created = new TownTradeOffer($"trade:{actorId}:{operationSequence}", actorId, command.ResidentId!,
                command.GiveAsset!, command.WantAsset!, state.Time, new(checked(state.Time.Ticks + command.DurationHours * TownContent.Hour)));
            trades = trades.Add(created);
            Wake(characters, created.RecipientId);
            return (characters, trades, $"Created {created.Id}; expires at {created.ExpiresAt}. Assets are not reserved; acceptance checks both participants again.");
        }
        TownTradeOffer offer = trades.Offers.Single(entry => entry.Id == command.Target);
        TownTradeStatus status = command.Kind switch {
            TownCommandKind.Accept => TownTradeStatus.Accepted,
            TownCommandKind.Reject => TownTradeStatus.Rejected,
            TownCommandKind.Withdraw => TownTradeStatus.Withdrawn,
            _ => throw new InvalidOperationException("Not a trade operation.")
        };
        if (status == TownTradeStatus.Accepted) {
            if (!TryExchange(state.Character(offer.ProposerId), offer.Give, offer.Want, out TownCharacter proposer) ||
                !TryExchange(state.Character(offer.RecipientId), offer.Want, offer.Give, out TownCharacter recipient)) {
                throw new InvalidOperationException("A checked trade must settle against the same immutable boundary.");
            }
            characters[Array.FindIndex(characters, actor => actor.Id == proposer.Id)] = proposer;
            characters[Array.FindIndex(characters, actor => actor.Id == recipient.Id)] = recipient;
        }
        trades = trades.Replace(offer.Close(status, state.Time));
        Wake(characters, offer.ProposerId); Wake(characters, offer.RecipientId);
        return (characters, trades, $"Offer {offer.Id}: {status}." + (status == TownTradeStatus.Accepted ? " Both asset transfers completed atomically." : " No assets transferred."));
    }

    internal static TownBusinessState Expire(TownBusinessState state, string offerId) => CloseActive(state,
        offer => offer.Id == offerId && state.Time >= offer.ExpiresAt, TownTradeStatus.Expired, wake: true);

    internal static TownBusinessState CloseForFailure(TownBusinessState state, string actorId) => CloseActive(state,
        offer => offer.Involves(actorId), TownTradeStatus.ParticipantFailed, wake: true);

    internal static TownBusinessState CloseForEnd(TownBusinessState state) => CloseActive(state, _ => true, TownTradeStatus.WorldEnded, wake: false);

    private static TownBusinessState CloseActive(TownBusinessState state, Func<TownTradeOffer, bool> predicate, TownTradeStatus status, bool wake) {
        TownTradeOffer[] matches = state.Trades.Offers.Where(offer => offer.Status == TownTradeStatus.Active && predicate(offer)).ToArray();
        if (matches.Length == 0) { return state; }
        TownTradeState trades = state.Trades;
        TownCharacter[] characters = state.Characters.ToArray();
        foreach (TownTradeOffer offer in matches) {
            trades = trades.Replace(offer.Close(status, state.Time));
            if (wake) { Wake(characters, offer.ProposerId); Wake(characters, offer.RecipientId); }
        }
        return state.Change(characters: characters, trades: trades);
    }

    private static void Wake(TownCharacter[] characters, string actorId) {
        int index = Array.FindIndex(characters, actor => actor.Id == actorId);
        if (!characters[index].Failed) { characters[index] = characters[index] with { Interaction = characters[index].Interaction.RequestResponse() }; }
    }

    private static bool ValidAsset(TownTradeAsset? asset) => asset is not null && asset.Amount > 0 &&
        (asset.IsMoney || TownContent.Items.Contains(asset.AssetId) && asset.Amount <= 1000);

    private static bool CanPay(TownCharacter actor, TownTradeAsset asset) => asset.IsMoney
        ? actor.Money >= asset.Amount : actor.Inventory.Count(asset.AssetId) >= asset.Amount;

    private static bool TryExchange(TownCharacter actor, TownTradeAsset payment, TownTradeAsset receipt, out TownCharacter result) {
        result = actor;
        // Full gross payment is required even for two legs of the same asset. Netting cannot invent affordability.
        if (!CanPay(actor, payment)) { return false; }
        long money = actor.Money;
        ItemInventory inventory = actor.Inventory;
        if (payment.IsMoney) { money -= payment.Amount; }
        else { inventory = inventory.Remove(payment.AssetId, (int)payment.Amount); }
        if (receipt.IsMoney) {
            if (money > long.MaxValue - receipt.Amount) { return false; }
            money += receipt.Amount;
        } else {
            if (inventory.CheckAdd(receipt.AssetId, (int)receipt.Amount) != InventoryAdmission.Allowed) { return false; }
            inventory = inventory.Add(receipt.AssetId, (int)receipt.Amount);
        }
        if (!TownPlanning.CanSettleActivity(actor, inventory)) { return false; }
        result = actor with { Money = money, Inventory = inventory };
        return true;
    }
}
