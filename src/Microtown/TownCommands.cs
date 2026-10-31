using System.Globalization;
using DramaBoard.Goals;

namespace DramaBoard.Microtown;

public enum TownCommandKind { Invalid, Buy, Sell, Produce, Eat, Wait, Go, Goal }
public sealed record TownCommand(TownCommandKind Kind, string? Target = null, int Quantity = 1,
    GoalCommand? Goal = null, string? ParseError = null) {
    public static TownCommand Parse(string raw) {
        ArgumentNullException.ThrowIfNull(raw);
        if (GoalCommandParser.IsGoalCommand(raw)) {
            return GoalCommandParser.TryParse(raw, out GoalCommand? command, out string? error)
                ? new(TownCommandKind.Goal, Goal: command) : new(TownCommandKind.Invalid, ParseError: error);
        }
        string[] words = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) { return new(TownCommandKind.Invalid, ParseError: "Empty command."); }
        TownCommandKind kind = words[0] switch {
            "buy" => TownCommandKind.Buy, "sell" => TownCommandKind.Sell, "produce" => TownCommandKind.Produce,
            "eat" => TownCommandKind.Eat, "wait" => TownCommandKind.Wait, "go" => TownCommandKind.Go,
            _ => TownCommandKind.Invalid
        };
        if (kind == TownCommandKind.Eat && words.Length == 1) { return new(kind); }
        if (kind == TownCommandKind.Go && words.Length == 2) { return new(kind, words[1]); }
        if (kind == TownCommandKind.Wait && words.Length == 2 && Positive(words[1], out int hours)) { return new(kind, Quantity: hours); }
        if (kind is TownCommandKind.Buy or TownCommandKind.Sell or TownCommandKind.Produce && words.Length == 3 && Positive(words[2], out int count)) {
            return new(kind, words[1], count);
        }
        return new(TownCommandKind.Invalid, ParseError: "Use buy/sell <item> <count>, produce <recipe> <batches>, eat, wait <hours>, go <passage>, or a goal command.");
    }
    private static bool Positive(string value, out int parsed) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) && parsed > 0;
}

/// <summary>Script/test decision seam at a published boundary; it contains no hidden persistence callbacks.</summary>
/// <remarks>This includes developer world truth, not an actor-visible Context. A future LLM adapter must project and freeze
/// only actor-visible material rather than sending this complete world to the model.</remarks>
public sealed record TownDecision(TownWorld World, string ActorId) {
    public TownCharacter Character => World.Business.Character(ActorId);
}
