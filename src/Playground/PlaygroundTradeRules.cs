using DramaBoard.Inventory;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Playground;

/// <summary>Bilateral offers retain terms, never reserve assets, and settle within the world's fact.</summary>
public static class PlaygroundTradeRules {
    public static bool IsTradeCommand(PlaygroundCommand command) => command.Kind is
        PlaygroundCommandKind.Offer or PlaygroundCommandKind.Accept or PlaygroundCommandKind.Reject or PlaygroundCommandKind.Withdraw;

    public static string? CheckVisible(PlaygroundWorld world, string actorId, PlaygroundCommand command) {
        if (world.Business.Economy is not { } economy) { return "此世界尚未启用交易。"; }
        PlaygroundCharacter actor = world.GetCharacter(actorId);
        if (!actor.IsActive) { return "角色已无法行动。"; }
        if (command.ResourceId is not null || command.Count != 0 || command.All || command.EntranceId is not null ||
            command.ItemId is not null || command.HandoffText is not null || command.SpeechText is not null ||
            command.Goal is not null || command.RecipeId is not null || command.MoneyAmount != 0) { return "交易只接受声明的参数。"; }
        if (command.Kind == PlaygroundCommandKind.Offer) {
            if (command.TradeId is not null || !ValidAsset(world.Definition, command.GiveAsset) || !ValidAsset(world.Definition, command.WantAsset)) {
                return "报价需要双方资产；物品数量为 1 至 1000，钱为正整数。";
            }
            if (!PlaygroundCommunicationRules.CanTarget(world, actorId, command.TargetActorId)) { return "报价需要另一名同地活跃角色。"; }
            if (command.Hours is < 1 or > 24 || world.Time.Ticks > long.MaxValue - command.Hours * PlaygroundTime.Hour) { return "报价有效期为 1 至 24 小时。"; }
            if (PlaygroundCommunicationRules.Remaining(actor, world.Time) <= 0) { return "本模型时刻的 4 次交流额度已用完。"; }
            if (economy.Trades.Offers.Count(offer => offer.ProposerId == actorId) >= PlaygroundTradeState.MaximumPerProposer) { return "你的保留报价数量已达上限；仍可处理已有报价。"; }
            return CanPay(actor, command.GiveAsset!) ? null : "你目前没有完整的报价支付资产。";
        }
        if (command.Kind is not (PlaygroundCommandKind.Accept or PlaygroundCommandKind.Reject or PlaygroundCommandKind.Withdraw)) { return "未知交易操作。"; }
        if (command.Hours != 0 || command.TargetActorId is not null || command.GiveAsset is not null || command.WantAsset is not null || string.IsNullOrWhiteSpace(command.TradeId)) {
            return "处理报价只接受报价编号。";
        }
        PlaygroundTradeOffer? entry = economy.Trades.Offers.FirstOrDefault(offer => offer.Id == command.TradeId && offer.Involves(actorId));
        if (entry is null) { return "没有与你相关的这个保留报价。"; }
        if (entry.Status != PlaygroundTradeStatus.Active) { return "报价已经关闭。"; }
        if (world.Time >= entry.ExpiresAt) { return "报价已经到期。"; }
        if (command.Kind == PlaygroundCommandKind.Withdraw) { return entry.ProposerId == actorId ? null : "只有报价人可撤回。"; }
        if (entry.RecipientId != actorId) { return "只有接收人可接受或拒绝。"; }
        if (command.Kind == PlaygroundCommandKind.Reject) { return null; }
        if (!PlaygroundCommunicationRules.CanTarget(world, actorId, entry.ProposerId)) { return "成交需要双方活跃且同地。"; }
        return TryExchange(actor, entry.Want, entry.Give, out _, out _) ? null : "你的支付资产或结算后容量不足以完成交易。";
    }

    public static string? Check(PlaygroundWorld world, string actorId, PlaygroundCommand command) {
        string? error = CheckVisible(world, actorId, command);
        if (error is not null) { return error; }
        PlaygroundTradeState trades = world.Business.Economy!.Trades;
        string otherId;
        if (command.Kind == PlaygroundCommandKind.Offer) {
            if (trades.Offers.Count >= PlaygroundTradeState.MaximumOffers) { return "当前无法保留更多报价。"; }
            CharacterContext? context = world.Business.InteractionStates[actorId].CurrentContext;
            if (context is null || context.Status != CharacterContextStatus.Open || context.LastOperationSequence == long.MaxValue) { return "当前无法记录新报价。"; }
            otherId = command.TargetActorId!;
        } else {
            PlaygroundTradeOffer offer = trades.Offers.Single(entry => entry.Id == command.TradeId);
            otherId = offer.ProposerId == actorId ? offer.RecipientId : offer.ProposerId;
            if (command.Kind == PlaygroundCommandKind.Accept && !TryExchange(world.GetCharacter(offer.ProposerId), offer.Give, offer.Want, out _, out _)) {
                return "对方当前无法完成约定的交易。";
            }
        }
        return !PlaygroundCommunicationRules.CanReceiveShout(world.Business, world.GetCharacter(otherId)) ||
            !PlaygroundCommunicationRules.CanReceiveShout(world.Business, world.GetCharacter(actorId)) ? "当前无法交付交易结果。" : null;
    }

    internal static PlaygroundFact Plan(PlaygroundWorld world, string actorId, PlaygroundCommand command) {
        string? failure = Check(world, actorId, command);
        if (failure is not null) { throw new InvalidOperationException(failure); }
        PlaygroundEconomyState economy = world.Business.Economy!;
        PlaygroundTradeState trades = economy.Trades;
        PlaygroundTradeOffer changed;
        PlaygroundEventKind kind;
        string result;
        if (command.Kind == PlaygroundCommandKind.Offer) {
            CharacterContext context = world.Business.InteractionStates[actorId].CurrentContext!;
            changed = new($"trade:{actorId}:{context.ContextSequence}:{checked(context.LastOperationSequence + 1)}", actorId, command.TargetActorId!,
                command.GiveAsset!, command.WantAsset!, world.Time, new(checked(world.Time.Ticks + command.Hours * PlaygroundTime.Hour)));
            trades = trades.Add(changed);
            kind = PlaygroundEventKind.TradeOffered;
            result = $"已创建报价 {changed.Id}，到期时刻 {changed.ExpiresAt}；资产不预留，接受时双方重新核对。";
        } else {
            PlaygroundTradeOffer before = trades.Offers.Single(offer => offer.Id == command.TradeId);
            (PlaygroundTradeStatus status, PlaygroundEventKind eventKind) = command.Kind switch {
                PlaygroundCommandKind.Accept => (PlaygroundTradeStatus.Accepted, PlaygroundEventKind.TradeAccepted),
                PlaygroundCommandKind.Reject => (PlaygroundTradeStatus.Rejected, PlaygroundEventKind.TradeRejected),
                PlaygroundCommandKind.Withdraw => (PlaygroundTradeStatus.Withdrawn, PlaygroundEventKind.TradeWithdrawn),
                _ => throw new InvalidOperationException("Not a trade action.")
            };
            changed = before.Close(status, world.Time); trades = trades.Replace(changed); kind = eventKind;
            result = $"报价 {changed.Id}：{status}。" + (status == PlaygroundTradeStatus.Accepted ? "双方资产已一次结算。" : "未转移资产。");
        }
        var characters = new List<PlaygroundCharacter>();
        foreach (PlaygroundCharacter actor in world.Characters.Where(actor => changed.Involves(actor.Id))) {
            ItemInventory backpack = actor.Backpack;
            long money = actor.Money;
            if (changed.Status == PlaygroundTradeStatus.Accepted && !TryExchange(actor,
                actor.Id == changed.ProposerId ? changed.Give : changed.Want,
                actor.Id == changed.ProposerId ? changed.Want : changed.Give, out backpack, out money)) {
                throw new InvalidOperationException("A checked exchange must settle against the same boundary.");
            }
            PlaygroundCommunicationState communication = actor.Id == actorId
                ? command.Kind == PlaygroundCommandKind.Offer ? actor.Communication.Sent(world.Time) : actor.Communication
                : actor.Communication.Receive();
            characters.Add(actor.Change(actor.Activity, backpack, result: actor.Id == actorId ? result : null, communication: communication, money: money));
        }
        return new PlaygroundFact(kind, world.Time, actorId, changedCharacters: characters, lastServedActorId: actorId,
            result: result, economy: economy.Change(trades: trades)).WithTradeChanges([changed]);
    }

    internal static PlaygroundFact Expire(PlaygroundWorld world) {
        PlaygroundEconomyState economy = world.Business.Economy ?? throw new InvalidOperationException("Trades require economy.");
        ModelTime at = economy.Trades.Offers.Where(offer => offer.Status == PlaygroundTradeStatus.Active).Min(offer => offer.ExpiresAt);
        PlaygroundTradeOffer[] closed = economy.Trades.Offers.Where(offer => offer.Status == PlaygroundTradeStatus.Active && offer.ExpiresAt == at)
            .Select(offer => offer.Close(PlaygroundTradeStatus.Expired, at)).ToArray();
        PlaygroundTradeState trades = economy.Trades;
        foreach (PlaygroundTradeOffer offer in closed) { trades = trades.Replace(offer); }
        var ids = closed.SelectMany(offer => new[] { offer.ProposerId, offer.RecipientId }).ToHashSet(StringComparer.Ordinal);
        return new PlaygroundFact(PlaygroundEventKind.TradesExpired, at,
            changedCharacters: world.Characters.Where(actor => actor.IsActive && ids.Contains(actor.Id))
                .Select(actor => actor.Change(actor.Activity, communication: actor.Communication.Receive())),
            result: "到期报价已关闭；没有转移资产。", economy: economy.Change(trades: trades)).WithTradeChanges(closed);
    }

    internal static PlaygroundFact Enrich(PlaygroundWorld world, PlaygroundFact fact) {
        if (fact.Kind is not (PlaygroundEventKind.SurvivalFailed or PlaygroundEventKind.WeatherResolved) || world.Business.Economy is not { } economy) { return fact; }
        var ended = fact.ChangedCharacters.Where(actor => !actor.IsActive && world.GetCharacter(actor.Id).IsActive)
            .Select(actor => actor.Id).ToHashSet(StringComparer.Ordinal);
        PlaygroundTradeOffer[] closed = economy.Trades.Offers.Where(offer => offer.Status == PlaygroundTradeStatus.Active &&
                (ended.Contains(offer.ProposerId) || ended.Contains(offer.RecipientId)))
            .Select(offer => offer.Close(PlaygroundTradeStatus.ParticipantFailed, fact.At)).ToArray();
        if (closed.Length == 0) { return fact; }
        PlaygroundTradeState trades = economy.Trades;
        foreach (PlaygroundTradeOffer offer in closed) { trades = trades.Replace(offer); }
        var ids = closed.SelectMany(offer => new[] { offer.ProposerId, offer.RecipientId }).ToHashSet(StringComparer.Ordinal);
        var changes = fact.ChangedCharacters.ToDictionary(actor => actor.Id, StringComparer.Ordinal);
        foreach (PlaygroundCharacter before in world.Characters.Where(actor => actor.IsActive && !ended.Contains(actor.Id) && ids.Contains(actor.Id))) {
            if (changes.TryGetValue(before.Id, out PlaygroundCharacter? current)) {
                changes[before.Id] = new(current.Id, current.Name, current.SkillLevel, current.Backpack, current.Activity, current.Notices,
                    current.DecisionGeneration, current.LastResult, current.NextActivityId, current.Survival, current.Ending,
                    current.Communication.Receive(), current.FollowingLeaderId, current.Money);
            } else { changes[before.Id] = before.Change(before.Activity, communication: before.Communication.Receive()); }
        }
        return fact.WithCharacters(world.Characters.Where(actor => changes.ContainsKey(actor.Id)).Select(actor => changes[actor.Id]))
            .WithEconomy((fact.Economy ?? economy).Change(trades: trades)).WithTradeChanges(closed);
    }

    private static bool ValidAsset(PlaygroundWorldDefinition definition, PlaygroundTradeAsset? asset) => asset is not null && !string.IsNullOrWhiteSpace(asset.AssetId) && asset.Amount > 0 &&
        (asset.IsMoney || definition.Items.ContainsKey(asset.AssetId) && asset.Amount <= 1000);
    private static bool CanPay(PlaygroundCharacter actor, PlaygroundTradeAsset asset) => asset.IsMoney
        ? actor.Money >= asset.Amount : actor.Backpack.Count(asset.AssetId) >= asset.Amount;
    private static bool TryExchange(PlaygroundCharacter actor, PlaygroundTradeAsset payment, PlaygroundTradeAsset receipt, out ItemInventory backpack, out long money) {
        backpack = actor.Backpack; money = actor.Money;
        if (!CanPay(actor, payment)) { return false; }
        if (payment.IsMoney) { money -= payment.Amount; }
        else { backpack = backpack.Remove(payment.AssetId, (int)payment.Amount); }
        if (receipt.IsMoney) {
            if (money > long.MaxValue - receipt.Amount) { return false; }
            money += receipt.Amount;
        } else {
            if (backpack.CheckAdd(receipt.AssetId, (int)receipt.Amount) != InventoryAdmission.Allowed) { return false; }
            backpack = backpack.Add(receipt.AssetId, (int)receipt.Amount);
        }
        return PlaygroundEconomyRules.CanSettleActivity(actor, backpack);
    }
}
