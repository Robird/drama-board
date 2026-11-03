using DramaBoard.Inventory;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.Playground;

/// <summary>Verifies recorded trade effects without planning or replaying controller decisions.</summary>
internal static class PlaygroundTradeValidation {
    internal static bool IsTradeFact(PlaygroundEventKind kind) => kind is PlaygroundEventKind.TradeOffered or PlaygroundEventKind.TradeAccepted or
        PlaygroundEventKind.TradeRejected or PlaygroundEventKind.TradeWithdrawn or PlaygroundEventKind.TradesExpired;

    internal static PlaygroundTradeState? Validate(PlaygroundBusinessState before, PlaygroundFact fact) {
        if (before.Economy is not { } economy) {
            Require(!IsTradeFact(fact.Kind) && fact.TradeChanges.Count == 0, "Trade effects require an economy.");
            return null;
        }
        PlaygroundTradeState expected = economy.Trades;
        PlaygroundTradeOffer[] changes = [];
        if (IsTradeFact(fact.Kind) && fact.Kind != PlaygroundEventKind.TradesExpired) {
            Require(fact.ActorId is not null && fact.At == before.Time && PlaygroundActions.NeedsDecision(before, fact.ActorId) &&
                PlaygroundActions.Location(before, fact.ActorId) is AtPlaceLocation, "A trade command requires a local decision window.");
            string actorId = fact.ActorId!;
            PlaygroundCommand command = PlaygroundCommandParser.Parse(fact.RawInput ?? "") ?? throw new InvalidOperationException("Missing trade command.");
            ValidateShape(command);
            CharacterContext context = before.InteractionStates[actorId].CurrentContext
                ?? throw new InvalidOperationException("A trade command requires its recorded open context.");
            Require(context.Status == CharacterContextStatus.Open && context.LastOperationSequence < long.MaxValue, "Invalid trade operation boundary.");
            PlaygroundTradeOffer changed;
            if (fact.Kind == PlaygroundEventKind.TradeOffered) {
                Require(command.Kind == PlaygroundCommandKind.Offer && command.GiveAsset is not null && command.WantAsset is not null &&
                    command.Hours is >= 1 and <= 24 && PlaygroundCommunicationRules.CanTarget(before, actorId, command.TargetActorId), "Invalid offer participants or terms.");
                command.GiveAsset!.Validate(before.Definition); command.WantAsset!.Validate(before.Definition);
                Require(CanPay(before.GetCharacter(actorId), command.GiveAsset!) &&
                    PlaygroundCommunicationRules.Remaining(before.GetCharacter(actorId), fact.At) > 0, "Offer must be affordable and within the sending budget.");
                changed = new($"trade:{actorId}:{context.ContextSequence}:{checked(context.LastOperationSequence + 1)}", actorId, command.TargetActorId!,
                    command.GiveAsset!, command.WantAsset!, fact.At, new(checked(fact.At.Ticks + command.Hours * PlaygroundTime.Hour)));
                expected = expected.Add(changed);
            } else {
                PlaygroundTradeOffer offer = expected.Offers.SingleOrDefault(value => value.Id == command.TradeId && value.Involves(actorId))
                    ?? throw new InvalidOperationException("Trade closure requires a participating retained offer.");
                Require(offer.Status == PlaygroundTradeStatus.Active && fact.At < offer.ExpiresAt, "Only a live offer can be closed by command.");
                PlaygroundTradeStatus status = fact.Kind switch {
                    PlaygroundEventKind.TradeAccepted => PlaygroundTradeStatus.Accepted,
                    PlaygroundEventKind.TradeRejected => PlaygroundTradeStatus.Rejected,
                    PlaygroundEventKind.TradeWithdrawn => PlaygroundTradeStatus.Withdrawn,
                    _ => throw new InvalidOperationException("Unknown trade command fact.")
                };
                Require((command.Kind, status) is (PlaygroundCommandKind.Accept, PlaygroundTradeStatus.Accepted) or
                    (PlaygroundCommandKind.Reject, PlaygroundTradeStatus.Rejected) or (PlaygroundCommandKind.Withdraw, PlaygroundTradeStatus.Withdrawn), "Trade command and status disagree.");
                Require(status == PlaygroundTradeStatus.Withdrawn ? offer.ProposerId == actorId : offer.RecipientId == actorId, "Wrong participant closes the offer.");
                if (status == PlaygroundTradeStatus.Accepted) {
                    Require(PlaygroundCommunicationRules.CanTarget(before, actorId, offer.ProposerId), "Acceptance requires living colocated participants.");
                    ValidateSettlement(before.GetCharacter(offer.ProposerId), offer.Give, offer.Want);
                    ValidateSettlement(before.GetCharacter(offer.RecipientId), offer.Want, offer.Give);
                }
                changed = offer.Close(status, fact.At); expected = expected.Replace(changed);
            }
            changes = [changed];
            PlaygroundCharacter[] participants = before.Characters.Where(actor => changed.Involves(actor.Id)).ToArray();
            Require(participants.All(actor => PlaygroundCommunicationRules.CanReceiveShout(before, actor)), "Trade result identities are exhausted.");
            Require(participants.Select(actor => actor.Id).SequenceEqual(fact.ChangedCharacters.Select(actor => actor.Id)), "Trade changes exactly its participants in world order.");
            foreach (PlaygroundCharacter prior in participants) {
                PlaygroundCommunicationState communication = prior.Id == actorId
                    ? command.Kind == PlaygroundCommandKind.Offer ? prior.Communication.Sent(fact.At) : prior.Communication
                    : prior.Communication.Receive();
                ValidatePreserved(prior, fact.ChangedCharacters.Single(actor => actor.Id == prior.Id), fact.At,
                    communication, prior.FollowingLeaderId, prior.Id == actorId ? fact.Result : prior.LastResult);
            }
        } else if (fact.Kind == PlaygroundEventKind.TradesExpired) {
            PlaygroundTradeOffer[] active = expected.Offers.Where(offer => offer.Status == PlaygroundTradeStatus.Active).ToArray();
            Require(active.Length > 0 && fact.At == active.Min(offer => offer.ExpiresAt) && fact.ActorId is null, "Expiration must close the earliest active boundary.");
            changes = active.Where(offer => offer.ExpiresAt == fact.At).Select(offer => offer.Close(PlaygroundTradeStatus.Expired, fact.At)).ToArray();
            foreach (PlaygroundTradeOffer closed in changes) { expected = expected.Replace(closed); }
            var recipients = changes.SelectMany(offer => new[] { offer.ProposerId, offer.RecipientId }).ToHashSet(StringComparer.Ordinal);
            PlaygroundCharacter[] participants = before.Characters.Where(actor => actor.IsActive && recipients.Contains(actor.Id)).ToArray();
            Require(participants.Select(actor => actor.Id).SequenceEqual(fact.ChangedCharacters.Select(actor => actor.Id)), "Expiration must notify each participant once.");
            foreach (PlaygroundCharacter prior in participants) {
                ValidatePreserved(prior, fact.ChangedCharacters.Single(actor => actor.Id == prior.Id), fact.At,
                    prior.Communication.Receive(), prior.FollowingLeaderId, prior.LastResult);
            }
        } else if (fact.Kind is PlaygroundEventKind.SurvivalFailed or PlaygroundEventKind.WeatherResolved) {
            var ended = fact.ChangedCharacters.Where(actor => !actor.IsActive && before.GetCharacter(actor.Id).IsActive)
                .Select(actor => actor.Id).ToHashSet(StringComparer.Ordinal);
            changes = expected.Offers.Where(offer => offer.Status == PlaygroundTradeStatus.Active &&
                (ended.Contains(offer.ProposerId) || ended.Contains(offer.RecipientId)))
                .Select(offer => offer.Close(PlaygroundTradeStatus.ParticipantFailed, fact.At)).ToArray();
            foreach (PlaygroundTradeOffer closed in changes) { expected = expected.Replace(closed); }
            var recipients = changes.SelectMany(offer => new[] { offer.ProposerId, offer.RecipientId })
                .Where(id => !ended.Contains(id)).ToHashSet(StringComparer.Ordinal);
            PlaygroundCharacter[] participants = before.Characters.Where(actor => actor.IsActive && (fact.Kind == PlaygroundEventKind.WeatherResolved ||
                ended.Contains(actor.Id) || recipients.Contains(actor.Id) || actor.FollowingLeaderId is { } leader && ended.Contains(leader))).ToArray();
            Require(participants.Select(actor => actor.Id).SequenceEqual(fact.ChangedCharacters.Select(actor => actor.Id)), "Ending effects have an exact subject, follower, and trade recipient set.");
            foreach (PlaygroundCharacter prior in participants) {
                PlaygroundCharacter after = fact.ChangedCharacters.Single(actor => actor.Id == prior.Id);
                PlaygroundCommunicationState communication = ended.Contains(prior.Id) ? prior.Communication.Acknowledge()
                    : recipients.Contains(prior.Id) ? prior.Communication.Receive() : prior.Communication;
                Require(after.Communication == communication && after.DecisionGeneration == checked(prior.DecisionGeneration + 1), "Ending notification must preserve budget and change generation once.");
                if (fact.Kind == PlaygroundEventKind.SurvivalFailed && !ended.Contains(prior.Id)) {
                    string? leader = prior.FollowingLeaderId is { } id && ended.Contains(id) ? null : prior.FollowingLeaderId;
                    ValidatePreserved(prior, after, fact.At, communication, leader, prior.LastResult, deathBoundary: before);
                }
            }
        } else if (fact.Kind == PlaygroundEventKind.DecisionContextOpened) {
            string actorId = fact.ActorId ?? throw new InvalidOperationException("Opening needs an actor.");
            CharacterInteractionState state = fact.InteractionChanges.SingleOrDefault(value => value.ActorId == actorId)
                ?? throw new InvalidOperationException("Delivery requires the committed interaction opening.");
            CharacterContext opened = state.CurrentContext ?? throw new InvalidOperationException("Delivery requires a context.");
            Require(opened.Status == CharacterContextStatus.Open && opened.ContextSequence == checked((before.InteractionStates[actorId].CurrentContext?.ContextSequence ?? 0) + 1) &&
                opened.Opening.Observation.Actor.Id == actorId && opened.Opening.Observation.Time == fact.At, "Delivery must match the actual new personal opening.");
            expected = expected.OpenContext(actorId, opened.ContextSequence, before.Characters.Where(actor => !actor.IsActive).Select(actor => actor.Id).ToArray());
            Require(opened.Opening.Observation.Trades.SequenceEqual(expected.Offers.Where(offer => offer.Involves(actorId)).Select(PlaygroundEconomyObservation.VisibleTrade)),
                "An opening must actually contain exactly the offers marked as delivered.");
        }
        Require(changes.SequenceEqual(fact.TradeChanges), "Recorded trade changes do not match the event's exact transitions.");
        Require((fact.Economy?.Trades ?? economy.Trades).SameState(expected), "Recorded trade ledger differs from the verified transitions.");
        return expected;
    }

    private static void ValidateShape(PlaygroundCommand command) {
        Require(command.ResourceId is null && command.Count == 0 && !command.All && command.EntranceId is null && command.ItemId is null &&
            command.HandoffText is null && command.SpeechText is null && command.Goal is null && command.RecipeId is null && command.MoneyAmount == 0,
            "Trade command contains unrelated arguments.");
        Require(command.Kind == PlaygroundCommandKind.Offer ? command.TradeId is null : command.Hours == 0 && command.TargetActorId is null &&
            command.GiveAsset is null && command.WantAsset is null && !string.IsNullOrWhiteSpace(command.TradeId), "Trade argument shape mismatch.");
    }
    private static bool CanPay(PlaygroundCharacter actor, PlaygroundTradeAsset asset) => asset.IsMoney ? actor.Money >= asset.Amount : actor.Backpack.Count(asset.AssetId) >= asset.Amount;
    private static void ValidateSettlement(PlaygroundCharacter actor, PlaygroundTradeAsset payment, PlaygroundTradeAsset receipt) {
        Require(CanPay(actor, payment), "Settlement requires the gross payment before any receipt.");
        ItemInventory backpack = actor.Backpack;
        long money = actor.Money;
        if (payment.IsMoney) { money = checked(money - payment.Amount); } else { backpack = backpack.Remove(payment.AssetId, checked((int)payment.Amount)); }
        if (receipt.IsMoney) { _ = checked(money + receipt.Amount); } else { backpack = backpack.Add(receipt.AssetId, checked((int)receipt.Amount)); }
        Require(PlaygroundEconomyRules.CanSettleActivity(actor, backpack), "Settlement must preserve completion and cancellation capacity.");
    }
    private static void ValidatePreserved(PlaygroundCharacter prior, PlaygroundCharacter after, ModelTime at, PlaygroundCommunicationState communication,
        string? leaderId, string? result, PlaygroundBusinessState? deathBoundary = null) {
        PlaygroundSurvival? body = prior.Survival?.Project(at);
        if (deathBoundary is not null && body is not null) {
            body = body.WithRates(at, body.FoodRate, body.WaterRate,
                PlaygroundSurvivalRules.WarmthRate(deathBoundary.Definition, PlaygroundSurvivalRules.IsSheltered(deathBoundary.Definition, deathBoundary.Expedition!.Spatial, prior.Id), deathBoundary.Light.IsDay));
        }
        Require(prior.Name == after.Name && prior.SkillLevel == after.SkillLevel && prior.Ending == after.Ending &&
            prior.NextActivityId == after.NextActivityId && after.DecisionGeneration == checked(prior.DecisionGeneration + 1) &&
            PlaygroundObservationEquality.SameActivity(prior.Activity, after.Activity) && SameBody(body, after.Survival) &&
            prior.Notices.Select(NoticeFields).SequenceEqual(after.Notices.Select(NoticeFields)) && prior.Backpack.UnitCapacity == after.Backpack.UnitCapacity &&
            after.Communication == communication && after.FollowingLeaderId == leaderId && after.LastResult == result,
            "Trade effects must preserve all unrelated participant fields.");
    }
    private static bool SameBody(PlaygroundSurvival? a, PlaygroundSurvival? b) => ReferenceEquals(a, b) || a is not null && b is not null &&
        (a.BaselineAt, a.FoodUnits, a.WaterUnits, a.WarmthUnits, a.WarnedMask, a.PendingMask, a.PendingSince) ==
        (b.BaselineAt, b.FoodUnits, b.WaterUnits, b.WarmthUnits, b.WarnedMask, b.PendingMask, b.PendingSince) &&
        a.FoodRate.SameAs(b.FoodRate) && a.WaterRate.SameAs(b.WaterRate) && a.WarmthRate.SameAs(b.WarmthRate);
    private static (PlaygroundNoticeKind, string, string?, string, ModelTime, string, int) NoticeFields(PlaygroundNotice n) =>
        (n.Kind, n.ResourceId, n.ActorId, n.SourceId, n.At, n.Reason, n.Count);
    private static void Require(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
}
