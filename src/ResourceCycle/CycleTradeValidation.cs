using DramaBoard.Inventory;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.ResourceCycle;

/// <summary>Verifies recorded trade effects without planning or replaying controller decisions.</summary>
internal static class CycleTradeValidation {
    internal static bool IsTradeFact(CycleEventKind kind) => kind is CycleEventKind.TradeOffered or CycleEventKind.TradeAccepted or
        CycleEventKind.TradeRejected or CycleEventKind.TradeWithdrawn or CycleEventKind.TradesExpired;

    internal static CycleTradeState? Validate(CycleBusinessState before, CycleFact fact) {
        if (before.Economy is not { } economy) {
            Require(!IsTradeFact(fact.Kind) && fact.TradeChanges.Count == 0, "Trade effects require an economy.");
            return null;
        }
        CycleTradeState expected = economy.Trades;
        CycleTradeOffer[] changes = [];
        if (IsTradeFact(fact.Kind) && fact.Kind != CycleEventKind.TradesExpired) {
            Require(fact.ActorId is not null && fact.At == before.Time && CycleActions.NeedsDecision(before, fact.ActorId) &&
                CycleActions.Location(before, fact.ActorId) is AtPlaceLocation, "A trade command requires a local decision window.");
            string actorId = fact.ActorId!;
            CycleCommand command = CycleCommandParser.Parse(fact.RawInput ?? "") ?? throw new InvalidOperationException("Missing trade command.");
            ValidateShape(command);
            CharacterContext context = before.InteractionStates[actorId].CurrentContext
                ?? throw new InvalidOperationException("A trade command requires its recorded open context.");
            Require(context.Status == CharacterContextStatus.Open && context.LastOperationSequence < long.MaxValue, "Invalid trade operation boundary.");
            CycleTradeOffer changed;
            if (fact.Kind == CycleEventKind.TradeOffered) {
                Require(command.Kind == CycleCommandKind.Offer && command.GiveAsset is not null && command.WantAsset is not null &&
                    command.Hours is >= 1 and <= 24 && CycleCommunicationRules.CanTarget(before, actorId, command.TargetActorId), "Invalid offer participants or terms.");
                command.GiveAsset!.Validate(before.Definition); command.WantAsset!.Validate(before.Definition);
                Require(CanPay(before.GetCharacter(actorId), command.GiveAsset!) &&
                    CycleCommunicationRules.Remaining(before.GetCharacter(actorId), fact.At) > 0, "Offer must be affordable and within the sending budget.");
                changed = new($"trade:{actorId}:{context.ContextSequence}:{checked(context.LastOperationSequence + 1)}", actorId, command.TargetActorId!,
                    command.GiveAsset!, command.WantAsset!, fact.At, new(checked(fact.At.Ticks + command.Hours * CycleTime.Hour)));
                expected = expected.Add(changed);
            } else {
                CycleTradeOffer offer = expected.Offers.SingleOrDefault(value => value.Id == command.TradeId && value.Involves(actorId))
                    ?? throw new InvalidOperationException("Trade closure requires a participating retained offer.");
                Require(offer.Status == CycleTradeStatus.Active && fact.At < offer.ExpiresAt, "Only a live offer can be closed by command.");
                CycleTradeStatus status = fact.Kind switch {
                    CycleEventKind.TradeAccepted => CycleTradeStatus.Accepted,
                    CycleEventKind.TradeRejected => CycleTradeStatus.Rejected,
                    CycleEventKind.TradeWithdrawn => CycleTradeStatus.Withdrawn,
                    _ => throw new InvalidOperationException("Unknown trade command fact.")
                };
                Require((command.Kind, status) is (CycleCommandKind.Accept, CycleTradeStatus.Accepted) or
                    (CycleCommandKind.Reject, CycleTradeStatus.Rejected) or (CycleCommandKind.Withdraw, CycleTradeStatus.Withdrawn), "Trade command and status disagree.");
                Require(status == CycleTradeStatus.Withdrawn ? offer.ProposerId == actorId : offer.RecipientId == actorId, "Wrong participant closes the offer.");
                if (status == CycleTradeStatus.Accepted) {
                    Require(CycleCommunicationRules.CanTarget(before, actorId, offer.ProposerId), "Acceptance requires living colocated participants.");
                    ValidateSettlement(before.GetCharacter(offer.ProposerId), offer.Give, offer.Want);
                    ValidateSettlement(before.GetCharacter(offer.RecipientId), offer.Want, offer.Give);
                }
                changed = offer.Close(status, fact.At); expected = expected.Replace(changed);
            }
            changes = [changed];
            CycleCharacter[] participants = before.Characters.Where(actor => changed.Involves(actor.Id)).ToArray();
            Require(participants.All(actor => CycleCommunicationRules.CanReceiveShout(before, actor)), "Trade result identities are exhausted.");
            Require(participants.Select(actor => actor.Id).SequenceEqual(fact.ChangedCharacters.Select(actor => actor.Id)), "Trade changes exactly its participants in world order.");
            foreach (CycleCharacter prior in participants) {
                CycleCommunicationState communication = prior.Id == actorId
                    ? command.Kind == CycleCommandKind.Offer ? prior.Communication.Sent(fact.At) : prior.Communication
                    : prior.Communication.Receive();
                ValidatePreserved(prior, fact.ChangedCharacters.Single(actor => actor.Id == prior.Id), fact.At,
                    communication, prior.FollowingLeaderId, prior.Id == actorId ? fact.Result : prior.LastResult);
            }
        } else if (fact.Kind == CycleEventKind.TradesExpired) {
            CycleTradeOffer[] active = expected.Offers.Where(offer => offer.Status == CycleTradeStatus.Active).ToArray();
            Require(active.Length > 0 && fact.At == active.Min(offer => offer.ExpiresAt) && fact.ActorId is null, "Expiration must close the earliest active boundary.");
            changes = active.Where(offer => offer.ExpiresAt == fact.At).Select(offer => offer.Close(CycleTradeStatus.Expired, fact.At)).ToArray();
            foreach (CycleTradeOffer closed in changes) { expected = expected.Replace(closed); }
            var recipients = changes.SelectMany(offer => new[] { offer.ProposerId, offer.RecipientId }).ToHashSet(StringComparer.Ordinal);
            CycleCharacter[] participants = before.Characters.Where(actor => actor.IsActive && recipients.Contains(actor.Id)).ToArray();
            Require(participants.Select(actor => actor.Id).SequenceEqual(fact.ChangedCharacters.Select(actor => actor.Id)), "Expiration must notify each participant once.");
            foreach (CycleCharacter prior in participants) {
                ValidatePreserved(prior, fact.ChangedCharacters.Single(actor => actor.Id == prior.Id), fact.At,
                    prior.Communication.Receive(), prior.FollowingLeaderId, prior.LastResult);
            }
        } else if (fact.Kind is CycleEventKind.SurvivalFailed or CycleEventKind.WeatherResolved) {
            var ended = fact.ChangedCharacters.Where(actor => !actor.IsActive && before.GetCharacter(actor.Id).IsActive)
                .Select(actor => actor.Id).ToHashSet(StringComparer.Ordinal);
            changes = expected.Offers.Where(offer => offer.Status == CycleTradeStatus.Active &&
                (ended.Contains(offer.ProposerId) || ended.Contains(offer.RecipientId)))
                .Select(offer => offer.Close(CycleTradeStatus.ParticipantFailed, fact.At)).ToArray();
            foreach (CycleTradeOffer closed in changes) { expected = expected.Replace(closed); }
            var recipients = changes.SelectMany(offer => new[] { offer.ProposerId, offer.RecipientId })
                .Where(id => !ended.Contains(id)).ToHashSet(StringComparer.Ordinal);
            CycleCharacter[] participants = before.Characters.Where(actor => actor.IsActive && (fact.Kind == CycleEventKind.WeatherResolved ||
                ended.Contains(actor.Id) || recipients.Contains(actor.Id) || actor.FollowingLeaderId is { } leader && ended.Contains(leader))).ToArray();
            Require(participants.Select(actor => actor.Id).SequenceEqual(fact.ChangedCharacters.Select(actor => actor.Id)), "Ending effects have an exact subject, follower, and trade recipient set.");
            foreach (CycleCharacter prior in participants) {
                CycleCharacter after = fact.ChangedCharacters.Single(actor => actor.Id == prior.Id);
                CycleCommunicationState communication = ended.Contains(prior.Id) ? prior.Communication.Acknowledge()
                    : recipients.Contains(prior.Id) ? prior.Communication.Receive() : prior.Communication;
                Require(after.Communication == communication && after.DecisionGeneration == checked(prior.DecisionGeneration + 1), "Ending notification must preserve budget and change generation once.");
                if (fact.Kind == CycleEventKind.SurvivalFailed && !ended.Contains(prior.Id)) {
                    string? leader = prior.FollowingLeaderId is { } id && ended.Contains(id) ? null : prior.FollowingLeaderId;
                    ValidatePreserved(prior, after, fact.At, communication, leader, prior.LastResult, deathBoundary: before);
                }
            }
        } else if (fact.Kind == CycleEventKind.DecisionContextOpened) {
            string actorId = fact.ActorId ?? throw new InvalidOperationException("Opening needs an actor.");
            CharacterInteractionState state = fact.InteractionChanges.SingleOrDefault(value => value.ActorId == actorId)
                ?? throw new InvalidOperationException("Delivery requires the committed interaction opening.");
            CharacterContext opened = state.CurrentContext ?? throw new InvalidOperationException("Delivery requires a context.");
            Require(opened.Status == CharacterContextStatus.Open && opened.ContextSequence == checked((before.InteractionStates[actorId].CurrentContext?.ContextSequence ?? 0) + 1) &&
                opened.Opening.Observation.Actor.Id == actorId && opened.Opening.Observation.Time == fact.At, "Delivery must match the actual new personal opening.");
            expected = expected.OpenContext(actorId, opened.ContextSequence, before.Characters.Where(actor => !actor.IsActive).Select(actor => actor.Id).ToArray());
            Require(opened.Opening.Observation.Trades.SequenceEqual(expected.Offers.Where(offer => offer.Involves(actorId)).Select(CycleEconomyObservation.VisibleTrade)),
                "An opening must actually contain exactly the offers marked as delivered.");
        }
        Require(changes.SequenceEqual(fact.TradeChanges), "Recorded trade changes do not match the event's exact transitions.");
        Require((fact.Economy?.Trades ?? economy.Trades).SameState(expected), "Recorded trade ledger differs from the verified transitions.");
        return expected;
    }

    private static void ValidateShape(CycleCommand command) {
        Require(command.ResourceId is null && command.Count == 0 && !command.All && command.PassageId is null && command.ItemId is null &&
            command.HandoffText is null && command.SpeechText is null && command.Goal is null && command.RecipeId is null && command.MoneyAmount == 0,
            "Trade command contains unrelated arguments.");
        Require(command.Kind == CycleCommandKind.Offer ? command.TradeId is null : command.Hours == 0 && command.TargetActorId is null &&
            command.GiveAsset is null && command.WantAsset is null && !string.IsNullOrWhiteSpace(command.TradeId), "Trade argument shape mismatch.");
    }
    private static bool CanPay(CycleCharacter actor, CycleTradeAsset asset) => asset.IsMoney ? actor.Money >= asset.Amount : actor.Backpack.Count(asset.AssetId) >= asset.Amount;
    private static void ValidateSettlement(CycleCharacter actor, CycleTradeAsset payment, CycleTradeAsset receipt) {
        Require(CanPay(actor, payment), "Settlement requires the gross payment before any receipt.");
        ItemInventory backpack = actor.Backpack;
        long money = actor.Money;
        if (payment.IsMoney) { money = checked(money - payment.Amount); } else { backpack = backpack.Remove(payment.AssetId, checked((int)payment.Amount)); }
        if (receipt.IsMoney) { _ = checked(money + receipt.Amount); } else { backpack = backpack.Add(receipt.AssetId, checked((int)receipt.Amount)); }
        Require(CycleEconomyRules.CanSettleActivity(actor, backpack), "Settlement must preserve completion and cancellation capacity.");
    }
    private static void ValidatePreserved(CycleCharacter prior, CycleCharacter after, ModelTime at, CycleCommunicationState communication,
        string? leaderId, string? result, CycleBusinessState? deathBoundary = null) {
        CycleSurvival? body = prior.Survival?.Project(at);
        if (deathBoundary is not null && body is not null) {
            body = body.WithRates(at, body.FoodRate, body.WaterRate,
                CycleSurvivalRules.WarmthRate(deathBoundary.Definition, CycleSurvivalRules.IsSheltered(deathBoundary.Definition, deathBoundary.Expedition!.Spatial, prior.Id), deathBoundary.Light.IsDay));
        }
        Require(prior.Name == after.Name && prior.SkillLevel == after.SkillLevel && prior.Ending == after.Ending &&
            prior.NextActivityId == after.NextActivityId && after.DecisionGeneration == checked(prior.DecisionGeneration + 1) &&
            CycleObservationEquality.SameActivity(prior.Activity, after.Activity) && SameBody(body, after.Survival) &&
            prior.Notices.Select(NoticeFields).SequenceEqual(after.Notices.Select(NoticeFields)) && prior.Backpack.UnitCapacity == after.Backpack.UnitCapacity &&
            after.Communication == communication && after.FollowingLeaderId == leaderId && after.LastResult == result,
            "Trade effects must preserve all unrelated participant fields.");
    }
    private static bool SameBody(CycleSurvival? a, CycleSurvival? b) => ReferenceEquals(a, b) || a is not null && b is not null &&
        (a.BaselineAt, a.FoodUnits, a.WaterUnits, a.WarmthUnits, a.WarnedMask, a.PendingMask, a.PendingSince) ==
        (b.BaselineAt, b.FoodUnits, b.WaterUnits, b.WarmthUnits, b.WarnedMask, b.PendingMask, b.PendingSince) &&
        a.FoodRate.SameAs(b.FoodRate) && a.WaterRate.SameAs(b.WaterRate) && a.WarmthRate.SameAs(b.WarmthRate);
    private static (CycleNoticeKind, string, string?, string, ModelTime, string, int) NoticeFields(CycleNotice n) =>
        (n.Kind, n.ResourceId, n.ActorId, n.SourceId, n.At, n.Reason, n.Count);
    private static void Require(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
}
