using System.Text;
using Atelia.DurableGraph;
using DramaBoard.Inventory;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.ResourceCycle;

/// <summary>Finite sending budget and merged attention, without duplicating the character's experienced messages.</summary>
[DurableType("DramaBoard.ResourceCycle.CommunicationState", 1)]
public sealed partial record CycleCommunicationState(
    [field: DurableField(1)] ModelTime BudgetAt,
    [field: DurableField(2)] int SentCount,
    [field: DurableField(3)] bool AttentionPending) : IDurableObject {
    internal CycleCommunicationState Acknowledge() => AttentionPending ? this with { AttentionPending = false } : this;
    internal CycleCommunicationState Receive() => AttentionPending ? this : this with { AttentionPending = true };
    internal CycleCommunicationState Sent(ModelTime at) => new(at, checked((at == BudgetAt ? SentCount : 0) + 1), AttentionPending);
    internal void Validate(ModelTime at) {
        if (BudgetAt.Ticks < 0 || BudgetAt > at || SentCount is < 0 or > CycleCommunicationRules.SendLimit) {
            throw new InvalidOperationException("Invalid communication budget anchor or count.");
        }
    }
}

/// <summary>Application-local directed communication and one-hop speech policy. Querying an effective budget never mutates it.</summary>
public static class CycleCommunicationRules {
    private static readonly SpatialQueries Spatial = new(CycleExpeditionMap.Graph);
    internal const string ShoutResult = "已大喊。";
    public const int SendLimit = 4;
    public const int MaximumSpeechScalars = 2000;
    public static int Remaining(CycleCharacter actor, ModelTime at) =>
        SendLimit - (actor.Communication.BudgetAt == at ? actor.Communication.SentCount : 0);
    public static bool ValidSpeech(string? text) => !string.IsNullOrWhiteSpace(text) && text.EnumerateRunes().Count() <= MaximumSpeechScalars;
    public static bool CanTarget(CycleWorld world, string senderId, string? targetId) => CanTarget(world.Business, senderId, targetId);
    internal static bool CanTarget(CycleBusinessState business, string senderId, string? targetId) {
        if (targetId is null || targetId == senderId || business.Expedition is null ||
            !business.Characters.Any(c => c.Id == senderId && c.IsActive) || !business.Characters.Any(c => c.Id == targetId && c.IsActive)) { return false; }
        return business.Expedition.Spatial.Entities.SingleOrDefault(e => e.Id.Value == senderId)?.Location is AtPlaceLocation sender &&
            business.Expedition.Spatial.Entities.SingleOrDefault(e => e.Id.Value == targetId)?.Location is AtPlaceLocation target && sender.PlaceId == target.PlaceId;
    }
    /// <summary>Objective candidate selection only; it neither grants knowledge nor modifies recipients.</summary>
    internal static IReadOnlyList<CycleCharacter> GetShoutRecipients(CycleBusinessState business, string senderId) {
        if (business.Expedition is null || !business.GetCharacter(senderId).IsActive ||
            business.Expedition.Spatial.Entities.SingleOrDefault(e => e.Id.Value == senderId)?.Location is not AtPlaceLocation origin) {
            throw new InvalidOperationException("Shouting requires an active sender at an actual place.");
        }
        var characters = business.Characters.ToDictionary(actor => actor.Id, StringComparer.Ordinal);
        return Array.AsReadOnly(Spatial.GetEntitiesInOneHop(business.Expedition.Spatial, origin.PlaceId, PlaceRelationCapabilities.Sound)
            .Where(id => id.Value != senderId && characters.TryGetValue(id.Value, out CycleCharacter? actor) && actor.IsActive)
            .Select(id => characters[id.Value]).ToArray());
    }
    internal static bool CanReceiveShout(CycleBusinessState business, CycleCharacter actor) {
        CharacterInteractionState state = business.InteractionStates[actor.Id];
        return actor.DecisionGeneration < long.MaxValue - 2 && state.NextExperienceSequence < long.MaxValue &&
            state.CurrentContext is not { Status: CharacterContextStatus.Open, NextItemSequence: long.MaxValue };
    }
    internal static string? Check(CycleWorld world, CycleCharacter sender, CycleCommand command) {
        if (command.Kind == CycleCommandKind.Shout) {
            if (world.Expedition is null || CycleActions.Location(world, sender.Id) is not AtPlaceLocation) { return "大喊需要身处一个地点。"; }
            if (!ValidSpeech(command.SpeechText)) { return $"话语需要非空文本，最多 {MaximumSpeechScalars} 个 Unicode 字符。"; }
            if (Remaining(sender, world.Time) <= 0) { return "本模型时刻的 4 次交流额度已用完；请确认感知并继续或安排耗时行动。"; }
            return world.Business.InteractionStates[sender.Id].NextExperienceSequence == long.MaxValue ||
                GetShoutRecipients(world.Business, sender.Id).Any(actor => !CanReceiveShout(world.Business, actor))
                ? "当前无法完成此次大喊。" : null;
        }
        if (!CanTarget(world, sender.Id, command.TargetActorId)) { return "当前不可见或不可交互的角色。"; }
        if (Remaining(sender, world.Time) == 0) { return "本模型时刻的 4 次交流额度已用完；请确认感知并继续或安排耗时行动。"; }
        if (world.GetCharacter(command.TargetActorId!).DecisionGeneration >= long.MaxValue - 2) { return "对方当前无法接收交流。"; }
        if (command.Kind == CycleCommandKind.Say) {
            return ValidSpeech(command.SpeechText) ? null : $"话语需要非空文本，最多 {MaximumSpeechScalars} 个 Unicode 字符。";
        }
        if (sender.Backpack.Count(command.ItemId!) < command.Count) { return "背包中的物品数量不足。"; }
        return world.GetCharacter(command.TargetActorId!).Backpack.CheckAdd(command.ItemId!, command.Count) == InventoryAdmission.Allowed
            ? null : "对方当前无法接收这份物品。";
    }
    internal static CycleFact Plan(CycleWorld world, string senderId, CycleCommand command) {
        if (command.Kind == CycleCommandKind.Shout) {
            var recipients = GetShoutRecipients(world.Business, senderId).Select(actor => actor.Id).ToHashSet(StringComparer.Ordinal);
            CycleCharacter[] participants = world.Characters.Where(actor => actor.Id == senderId || recipients.Contains(actor.Id))
                .Select(actor => actor.Id == senderId
                    ? actor.Change(actor.Activity, result: ShoutResult, communication: actor.Communication.Sent(world.Time))
                    : actor.Change(actor.Activity, communication: actor.Communication.Receive())).ToArray();
            return new(CycleEventKind.SpeechShouted, world.Time, senderId, changedCharacters: participants,
                lastServedActorId: senderId, result: ShoutResult, speechText: command.SpeechText);
        }
        CycleCharacter sender = world.GetCharacter(senderId), target = world.GetCharacter(command.TargetActorId!);
        bool speech = command.Kind == CycleCommandKind.Say;
        string result = speech ? $"已向 {target.Name} 说话。" : $"已将 {command.ItemId} ×{command.Count} 赠与 {target.Name}。";
        CycleCharacter sent = sender.Change(sender.Activity,
            backpack: speech ? sender.Backpack : sender.Backpack.Remove(command.ItemId!, command.Count), result: result,
            communication: sender.Communication.Sent(world.Time));
        CycleCharacter received = target.Change(target.Activity,
            backpack: speech ? target.Backpack : target.Backpack.Add(command.ItemId!, command.Count),
            communication: target.Communication.Receive());
        return new(speech ? CycleEventKind.SpeechSent : CycleEventKind.ItemGiven, world.Time, senderId,
            changedCharacters: world.Characters.Where(c => c.Id == senderId || c.Id == target.Id).Select(c => c.Id == senderId ? sent : received),
            lastServedActorId: senderId, result: result, targetActorId: target.Id,
            speechText: speech ? command.SpeechText : null, itemId: speech ? null : command.ItemId, itemCount: speech ? 0 : command.Count);
    }
}
