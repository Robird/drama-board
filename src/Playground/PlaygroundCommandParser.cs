using System.Globalization;
using DramaBoard.Goals;
namespace DramaBoard.Playground;

/// <summary>Pure action syntax; world validity remains the domain's responsibility.</summary>
public static class PlaygroundCommandParser {
    /// <remarks>Case-sensitive, whitespace-separated syntax. Handoff preserves internal whitespace.</remarks>
    public static PlaygroundCommand? Parse(string input) {
        ArgumentNullException.ThrowIfNull(input);
        string trimmed = input.Trim();
        if (GoalCommandParser.IsGoalCommand(trimmed)) {
            return GoalCommandParser.TryParse(input, out GoalCommand? goal, out _) && goal is not null
                ? PlaygroundCommand.ManageGoal(goal) : null;
        }
        if (trimmed.StartsWith("handoff", StringComparison.Ordinal) && trimmed.Length > 7 && char.IsWhiteSpace(trimmed[7])) {
            string handoff = trimmed[8..].Trim();
            if (handoff.Length > 0) {
                return PlaygroundCommand.Handoff(handoff == "--clear" ? null : handoff);
            }
        }
        if (trimmed.StartsWith("shout", StringComparison.Ordinal) && trimmed.Length > 5 && char.IsWhiteSpace(trimmed[5])) {
            return PlaygroundCommand.Shout(trimmed[6..].Trim());
        }
        if (trimmed.StartsWith("say", StringComparison.Ordinal) && trimmed.Length > 3 && char.IsWhiteSpace(trimmed[3])) {
            string remaining = trimmed[4..].TrimStart();
            int separator = 0;
            while (separator < remaining.Length && !char.IsWhiteSpace(remaining[separator])) { separator++; }
            if (separator > 0 && separator < remaining.Length) {
                return PlaygroundCommand.Say(remaining[..separator], remaining[separator..].Trim());
            }
        }
        string[] parts = input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 7 or 8 && parts[0] == "offer" && parts[4] == "for" &&
            long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out long giveAmount) &&
            long.TryParse(parts[6], NumberStyles.None, CultureInfo.InvariantCulture, out long wantAmount)) {
            int duration = 6;
            if (parts.Length == 8 && !int.TryParse(parts[7], NumberStyles.None, CultureInfo.InvariantCulture, out duration)) { return null; }
            return PlaygroundCommand.Offer(parts[1], new(parts[2], giveAmount), new(parts[5], wantAmount), duration);
        }
        if (parts is ["give", var recipient, "money", var money] && long.TryParse(money, NumberStyles.None, CultureInfo.InvariantCulture, out long amount)) {
            return PlaygroundCommand.GiveMoney(recipient, amount);
        }
        if (parts is [var economic, var target, var units] && int.TryParse(units, NumberStyles.None, CultureInfo.InvariantCulture, out int economicCount)) {
            if (economic == "buy") { return PlaygroundCommand.Buy(target, economicCount); }
            if (economic == "sell") { return PlaygroundCommand.Sell(target, economicCount); }
            if (economic == "produce") { return PlaygroundCommand.Produce(target, economicCount); }
        }
        if (parts is ["wait", var hours] && int.TryParse(hours, NumberStyles.None, CultureInfo.InvariantCulture, out int waitHours)) {
            return PlaygroundCommand.Wait(waitHours);
        }
        if (parts is ["gather", var allResource, "all"]) {
            return PlaygroundCommand.GatherAll(allResource);
        }
        if (parts is ["gather", var resourceId, var quantity] && int.TryParse(quantity, NumberStyles.None, CultureInfo.InvariantCulture, out int count)) {
            return PlaygroundCommand.Gather(resourceId, count);
        }
        if (parts is ["give", var targetActorId, var itemId, var itemQuantity] && int.TryParse(itemQuantity, NumberStyles.None, CultureInfo.InvariantCulture, out int itemCount)) {
            return PlaygroundCommand.Give(targetActorId, itemId, itemCount);
        }
        return parts switch {
            ["map"] => PlaygroundCommand.Map(),
            ["path", var destination] => PlaygroundCommand.Path(destination),
            ["path", var destination, "from", var origin] => PlaygroundCommand.Path(destination, origin),
            ["accept", var accepted] => PlaygroundCommand.Accept(accepted),
            ["reject", var rejected] => PlaygroundCommand.Reject(rejected),
            ["withdraw", var withdrawn] => PlaygroundCommand.Withdraw(withdrawn),
            ["follow", var leaderId] => PlaygroundCommand.Follow(leaderId),
            ["unfollow"] => PlaygroundCommand.Unfollow(),
            ["dismiss-followers"] => PlaygroundCommand.DismissFollowers(),
            ["continue"] => PlaygroundCommand.Continue(),
            ["cancel"] => PlaygroundCommand.Cancel(),
            ["go", var entranceId] => PlaygroundCommand.Go(entranceId),
            ["use", var usedItemId] => PlaygroundCommand.Use(usedItemId),
            _ => null,
        };
    }
    /// <summary>Canonical syntax for scripted controllers and visible command catalogues.</summary>
    public static string Format(PlaygroundCommand command) => command.Kind switch {
        PlaygroundCommandKind.Map => "map",
        PlaygroundCommandKind.Path => $"path {command.TargetPlaceId}" + (command.OriginPlaceId is null ? "" : $" from {command.OriginPlaceId}"),
        PlaygroundCommandKind.Offer => $"offer {command.TargetActorId} {command.GiveAsset!.AssetId} {command.GiveAsset.Amount.ToString(CultureInfo.InvariantCulture)} for {command.WantAsset!.AssetId} {command.WantAsset.Amount.ToString(CultureInfo.InvariantCulture)} {command.Hours.ToString(CultureInfo.InvariantCulture)}",
        PlaygroundCommandKind.Accept => $"accept {command.TradeId}",
        PlaygroundCommandKind.Reject => $"reject {command.TradeId}",
        PlaygroundCommandKind.Withdraw => $"withdraw {command.TradeId}",
        PlaygroundCommandKind.Buy => $"buy {command.ItemId} {command.Count.ToString(CultureInfo.InvariantCulture)}",
        PlaygroundCommandKind.Sell => $"sell {command.ItemId} {command.Count.ToString(CultureInfo.InvariantCulture)}",
        PlaygroundCommandKind.Produce => $"produce {command.RecipeId} {command.Count.ToString(CultureInfo.InvariantCulture)}",
        PlaygroundCommandKind.GiveMoney => $"give {command.TargetActorId} money {command.MoneyAmount.ToString(CultureInfo.InvariantCulture)}",
        PlaygroundCommandKind.Follow => $"follow {command.TargetActorId}",
        PlaygroundCommandKind.Unfollow => "unfollow",
        PlaygroundCommandKind.DismissFollowers => "dismiss-followers",
        PlaygroundCommandKind.Goal => GoalCommandParser.Format(command.Goal!),
        PlaygroundCommandKind.Shout => $"shout {command.SpeechText}",
        PlaygroundCommandKind.Say => $"say {command.TargetActorId} {command.SpeechText}",
        PlaygroundCommandKind.Give => $"give {command.TargetActorId} {command.ItemId} {command.Count.ToString(CultureInfo.InvariantCulture)}",
        PlaygroundCommandKind.Wait => $"wait {command.Hours.ToString(CultureInfo.InvariantCulture)}",
        PlaygroundCommandKind.Gather => $"gather {command.ResourceId} {(command.All ? "all" : command.Count.ToString(CultureInfo.InvariantCulture))}",
        PlaygroundCommandKind.Go => $"go {command.EntranceId}",
        PlaygroundCommandKind.Use => $"use {command.ItemId}",
        PlaygroundCommandKind.Handoff => command.HandoffText is null ? "handoff --clear" : $"handoff {command.HandoffText}",
        PlaygroundCommandKind.Continue => "continue",
        PlaygroundCommandKind.Cancel => "cancel",
        _ => throw new InvalidOperationException("Unknown character command.")
    };
}
