using System.Globalization;
using DramaBoard.Goals;
namespace DramaBoard.ResourceCycle;

/// <summary>Pure action syntax; world validity remains the domain's responsibility.</summary>
public static class CycleCommandParser {
    /// <remarks>Case-sensitive, whitespace-separated syntax. Handoff preserves internal whitespace.</remarks>
    public static CycleCommand? Parse(string input) {
        ArgumentNullException.ThrowIfNull(input);
        string trimmed = input.Trim();
        if (GoalCommandParser.IsGoalCommand(trimmed)) {
            return GoalCommandParser.TryParse(input, out GoalCommand? goal, out _) && goal is not null
                ? CycleCommand.ManageGoal(goal) : null;
        }
        if (trimmed.StartsWith("handoff", StringComparison.Ordinal) && trimmed.Length > 7 && char.IsWhiteSpace(trimmed[7])) {
            string handoff = trimmed[8..].Trim();
            if (handoff.Length > 0) {
                return CycleCommand.Handoff(handoff == "--clear" ? null : handoff);
            }
        }
        if (trimmed.StartsWith("shout", StringComparison.Ordinal) && trimmed.Length > 5 && char.IsWhiteSpace(trimmed[5])) {
            return CycleCommand.Shout(trimmed[6..].Trim());
        }
        if (trimmed.StartsWith("say", StringComparison.Ordinal) && trimmed.Length > 3 && char.IsWhiteSpace(trimmed[3])) {
            string remaining = trimmed[4..].TrimStart();
            int separator = 0;
            while (separator < remaining.Length && !char.IsWhiteSpace(remaining[separator])) { separator++; }
            if (separator > 0 && separator < remaining.Length) {
                return CycleCommand.Say(remaining[..separator], remaining[separator..].Trim());
            }
        }
        string[] parts = input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 7 or 8 && parts[0] == "offer" && parts[4] == "for" &&
            long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out long giveAmount) &&
            long.TryParse(parts[6], NumberStyles.None, CultureInfo.InvariantCulture, out long wantAmount)) {
            int duration = 6;
            if (parts.Length == 8 && !int.TryParse(parts[7], NumberStyles.None, CultureInfo.InvariantCulture, out duration)) { return null; }
            return CycleCommand.Offer(parts[1], new(parts[2], giveAmount), new(parts[5], wantAmount), duration);
        }
        if (parts is ["give", var recipient, "money", var money] && long.TryParse(money, NumberStyles.None, CultureInfo.InvariantCulture, out long amount)) {
            return CycleCommand.GiveMoney(recipient, amount);
        }
        if (parts is [var economic, var target, var units] && int.TryParse(units, NumberStyles.None, CultureInfo.InvariantCulture, out int economicCount)) {
            if (economic == "buy") { return CycleCommand.Buy(target, economicCount); }
            if (economic == "sell") { return CycleCommand.Sell(target, economicCount); }
            if (economic == "produce") { return CycleCommand.Produce(target, economicCount); }
        }
        if (parts is ["wait", var hours] && int.TryParse(hours, NumberStyles.None, CultureInfo.InvariantCulture, out int waitHours)) {
            return CycleCommand.Wait(waitHours);
        }
        if (parts is ["gather", var allResource, "all"]) {
            return CycleCommand.GatherAll(allResource);
        }
        if (parts is ["gather", var resourceId, var quantity] && int.TryParse(quantity, NumberStyles.None, CultureInfo.InvariantCulture, out int count)) {
            return CycleCommand.Gather(resourceId, count);
        }
        if (parts is ["give", var targetActorId, var itemId, var itemQuantity] && int.TryParse(itemQuantity, NumberStyles.None, CultureInfo.InvariantCulture, out int itemCount)) {
            return CycleCommand.Give(targetActorId, itemId, itemCount);
        }
        return parts switch {
            ["accept", var accepted] => CycleCommand.Accept(accepted),
            ["reject", var rejected] => CycleCommand.Reject(rejected),
            ["withdraw", var withdrawn] => CycleCommand.Withdraw(withdrawn),
            ["follow", var leaderId] => CycleCommand.Follow(leaderId),
            ["unfollow"] => CycleCommand.Unfollow(),
            ["dismiss-followers"] => CycleCommand.DismissFollowers(),
            ["continue"] => CycleCommand.Continue(),
            ["cancel"] => CycleCommand.Cancel(),
            ["go", var passageId] => CycleCommand.Go(passageId),
            ["use", var usedItemId] => CycleCommand.Use(usedItemId),
            _ => null,
        };
    }
    /// <summary>Canonical syntax for scripted controllers and visible command catalogues.</summary>
    public static string Format(CycleCommand command) => command.Kind switch {
        CycleCommandKind.Offer => $"offer {command.TargetActorId} {command.GiveAsset!.AssetId} {command.GiveAsset.Amount.ToString(CultureInfo.InvariantCulture)} for {command.WantAsset!.AssetId} {command.WantAsset.Amount.ToString(CultureInfo.InvariantCulture)} {command.Hours.ToString(CultureInfo.InvariantCulture)}",
        CycleCommandKind.Accept => $"accept {command.TradeId}",
        CycleCommandKind.Reject => $"reject {command.TradeId}",
        CycleCommandKind.Withdraw => $"withdraw {command.TradeId}",
        CycleCommandKind.Buy => $"buy {command.ItemId} {command.Count.ToString(CultureInfo.InvariantCulture)}",
        CycleCommandKind.Sell => $"sell {command.ItemId} {command.Count.ToString(CultureInfo.InvariantCulture)}",
        CycleCommandKind.Produce => $"produce {command.RecipeId} {command.Count.ToString(CultureInfo.InvariantCulture)}",
        CycleCommandKind.GiveMoney => $"give {command.TargetActorId} money {command.MoneyAmount.ToString(CultureInfo.InvariantCulture)}",
        CycleCommandKind.Follow => $"follow {command.TargetActorId}",
        CycleCommandKind.Unfollow => "unfollow",
        CycleCommandKind.DismissFollowers => "dismiss-followers",
        CycleCommandKind.Goal => GoalCommandParser.Format(command.Goal!),
        CycleCommandKind.Shout => $"shout {command.SpeechText}",
        CycleCommandKind.Say => $"say {command.TargetActorId} {command.SpeechText}",
        CycleCommandKind.Give => $"give {command.TargetActorId} {command.ItemId} {command.Count.ToString(CultureInfo.InvariantCulture)}",
        CycleCommandKind.Wait => $"wait {command.Hours.ToString(CultureInfo.InvariantCulture)}",
        CycleCommandKind.Gather => $"gather {command.ResourceId} {(command.All ? "all" : command.Count.ToString(CultureInfo.InvariantCulture))}",
        CycleCommandKind.Go => $"go {command.PassageId}",
        CycleCommandKind.Use => $"use {command.ItemId}",
        CycleCommandKind.Handoff => command.HandoffText is null ? "handoff --clear" : $"handoff {command.HandoffText}",
        CycleCommandKind.Continue => "continue",
        CycleCommandKind.Cancel => "cancel",
        _ => throw new InvalidOperationException("Unknown character command.")
    };
}
