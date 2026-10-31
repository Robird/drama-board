using System.Globalization;
using DramaBoard.Goals;

namespace DramaBoard.Microtown;

public enum TownCommandKind { Invalid, Buy, Sell, Produce, Eat, Wait, Go, Goal, Say, Give, Continue, Cancel, Offer, Accept, Reject, Withdraw }
public sealed record TownCommand(TownCommandKind Kind, string? Target = null, int Quantity = 1,
    GoalCommand? Goal = null, string? ParseError = null, string? ResidentId = null, string? Text = null, long Amount = 0,
    TownTradeAsset? GiveAsset = null, TownTradeAsset? WantAsset = null, int DurationHours = 0) {
    public static TownCommand Parse(string raw) {
        ArgumentNullException.ThrowIfNull(raw);
        if (GoalCommandParser.IsGoalCommand(raw)) {
            return GoalCommandParser.TryParse(raw, out GoalCommand? command, out string? error)
                ? new(TownCommandKind.Goal, Goal: command) : new(TownCommandKind.Invalid, ParseError: error);
        }
        string[] words = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) { return new(TownCommandKind.Invalid, ParseError: "Empty command."); }
        if (words[0] == "offer" && words.Length == 7 &&
            long.TryParse(words[3], NumberStyles.None, CultureInfo.InvariantCulture, out long give) && give > 0 &&
            long.TryParse(words[5], NumberStyles.None, CultureInfo.InvariantCulture, out long want) && want > 0 &&
            Positive(words[6], out int duration)) {
            return new(TownCommandKind.Offer, ResidentId: words[1], GiveAsset: new(words[2], give), WantAsset: new(words[4], want), DurationHours: duration);
        }
        if (words[0] == "say") {
            string[] speech = raw.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
            return speech.Length == 3 ? new(TownCommandKind.Say, ResidentId: speech[1], Text: speech[2])
                : new(TownCommandKind.Invalid, ParseError: "Use say <resident> <text>.");
        }
        if (words[0] == "give" && words.Length == 4 && long.TryParse(words[3], NumberStyles.None, CultureInfo.InvariantCulture, out long amount) && amount > 0) {
            return new(TownCommandKind.Give, words[2], ResidentId: words[1], Amount: amount);
        }
        TownCommandKind kind = words[0] switch {
            "buy" => TownCommandKind.Buy, "sell" => TownCommandKind.Sell, "produce" => TownCommandKind.Produce,
            "eat" => TownCommandKind.Eat, "wait" => TownCommandKind.Wait, "go" => TownCommandKind.Go,
            "continue" => TownCommandKind.Continue, "cancel" => TownCommandKind.Cancel,
            "accept" => TownCommandKind.Accept, "reject" => TownCommandKind.Reject, "withdraw" => TownCommandKind.Withdraw,
            _ => TownCommandKind.Invalid
        };
        if (kind == TownCommandKind.Eat && words.Length == 1) { return new(kind); }
        if (kind is TownCommandKind.Continue or TownCommandKind.Cancel && words.Length == 1) { return new(kind); }
        if (kind is TownCommandKind.Accept or TownCommandKind.Reject or TownCommandKind.Withdraw && words.Length == 2) { return new(kind, words[1]); }
        if (kind == TownCommandKind.Go && words.Length == 2) { return new(kind, words[1]); }
        if (kind == TownCommandKind.Wait && words.Length == 2 && Positive(words[1], out int hours)) { return new(kind, Quantity: hours); }
        if (kind is TownCommandKind.Buy or TownCommandKind.Sell or TownCommandKind.Produce && words.Length == 3 && Positive(words[2], out int count)) {
            return new(kind, words[1], count);
        }
        return new(TownCommandKind.Invalid, ParseError: "Use buy/sell <item> <count>, produce <recipe> <batches>, eat, wait <hours>, go <passage>, say <resident> <text>, give <resident> <item|money> <count>, offer <resident> <give-asset> <give-count> <want-asset> <want-count> <hours>, accept/reject/withdraw <offer-id>, continue, cancel, or a goal command.");
    }
    private static bool Positive(string value, out int parsed) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) && parsed > 0;
}

/// <summary>Script/test decision seam at a published boundary; it contains no hidden persistence callbacks.</summary>
/// <remarks>This includes developer world truth, not an actor-visible Context. A future LLM adapter must project and freeze
/// only actor-visible material rather than sending this complete world to the model.</remarks>
public sealed record TownDecision(TownWorld World, string ActorId) {
    public TownCharacter Character => World.Business.Character(ActorId);
}
