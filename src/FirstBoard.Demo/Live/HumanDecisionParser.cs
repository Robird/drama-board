using System.Globalization;
using DramaBoard.Protocol;

namespace DramaBoard.FirstBoard.Demo.Live;

internal readonly record struct HumanDecisionParseResult(
    Intent? Intent,
    string? Error)
{
    public bool IsSuccess => Intent is not null;
}

/// <summary>Parses the intentionally small Console command language into Protocol intents.</summary>
internal static class HumanDecisionParser
{
    public static HumanDecisionParseResult Parse(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return Failure("Enter a command.");
        }

        (string verb, string arguments) = SplitFirst(command.Trim());
        return verb.ToLowerInvariant() switch
        {
            "travel" => ParseTargetWithOptionalText(
                arguments,
                "travel <exit-id> [reason]",
                (target, text) => new Intent(ActionKinds.Travel, ExitId: target, FreeText: text)),
            "travel-to" => ParseTargetWithOptionalText(
                arguments,
                "travel-to <destination-id> [reason]",
                (target, text) => new Intent(
                    ActionKinds.TravelTo,
                    DestinationId: target,
                    FreeText: text)),
            "continue" => ParseOptionalText(
                arguments,
                text => new Intent(ActionKinds.ContinueTravel, FreeText: text)),
            "reverse" => ParseOptionalText(
                arguments,
                text => new Intent(ActionKinds.ReverseTravel, FreeText: text)),
            "wait" => ParseWait(arguments),
            "talk" => ParseTalk(arguments),
            "observe" => ParseOptionalTarget(
                arguments,
                "observe [object-id]",
                target => new Intent(ActionKinds.Observe, TargetObjectId: target)),
            "take" => ParseSingleTarget(
                arguments,
                "take <object-id>",
                target => new Intent(ActionKinds.Take, TargetObjectId: target)),
            "put" => ParseSingleTarget(
                arguments,
                "put <object-id>",
                target => new Intent(ActionKinds.Put, TargetObjectId: target)),
            "give" => ParseTwoTargets(
                arguments,
                "give <actor-id> <object-id>",
                (actor, item) => new Intent(
                    ActionKinds.Give,
                    TargetActorId: actor,
                    TargetObjectId: item)),
            "show" => ParseTwoTargets(
                arguments,
                "show <actor-id> <object-id>",
                (actor, item) => new Intent(
                    ActionKinds.Show,
                    TargetActorId: actor,
                    TargetObjectId: item)),
            "use" => ParseSingleTarget(
                arguments,
                "use <object-id>",
                target => new Intent(ActionKinds.Use, TargetObjectId: target)),
            _ => Failure($"Unknown command '{verb}'."),
        };
    }

    private static HumanDecisionParseResult ParseWait(string arguments)
    {
        string[] values = Tokens(arguments);
        if (values.Length == 0)
        {
            return Success(new Intent(ActionKinds.Wait));
        }

        if (values.Length == 1)
        {
            return TryParseInt64(values[0], out long duration)
                ? TryCreate(() => new Intent(ActionKinds.Wait, DurationMs: duration))
                : Failure("wait <duration-ms> requires an integer duration.");
        }

        if (values.Length == 2 &&
            string.Equals(values[0], "until", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseInt64(values[1], out long until)
                ? TryCreate(() => new Intent(ActionKinds.Wait, UntilModelTimeMs: until))
                : Failure("wait until <model-time-ms> requires an integer model time.");
        }

        return Failure("Usage: wait [duration-ms] or wait until <model-time-ms>.");
    }

    private static HumanDecisionParseResult ParseTalk(string arguments)
    {
        (string target, string text) = SplitFirst(arguments);
        if (target.Length == 0 || text.Length == 0)
        {
            return Failure("Usage: talk <actor-id> <text>.");
        }

        return TryCreate(() => new Intent(
            ActionKinds.Talk,
            TargetActorId: target,
            FreeText: text));
    }

    private static HumanDecisionParseResult ParseTargetWithOptionalText(
        string arguments,
        string usage,
        Func<string, string?, Intent> create)
    {
        (string target, string text) = SplitFirst(arguments);
        if (target.Length == 0)
        {
            return Failure($"Usage: {usage}.");
        }

        return TryCreate(() => create(target, Optional(text)));
    }

    private static HumanDecisionParseResult ParseOptionalText(
        string arguments,
        Func<string?, Intent> create) =>
        TryCreate(() => create(Optional(arguments.Trim())));

    private static HumanDecisionParseResult ParseOptionalTarget(
        string arguments,
        string usage,
        Func<string?, Intent> create)
    {
        string[] values = Tokens(arguments);
        return values.Length switch
        {
            0 => TryCreate(() => create(null)),
            1 => TryCreate(() => create(values[0])),
            _ => Failure($"Usage: {usage}."),
        };
    }

    private static HumanDecisionParseResult ParseSingleTarget(
        string arguments,
        string usage,
        Func<string, Intent> create)
    {
        string[] values = Tokens(arguments);
        return values.Length == 1
            ? TryCreate(() => create(values[0]))
            : Failure($"Usage: {usage}.");
    }

    private static HumanDecisionParseResult ParseTwoTargets(
        string arguments,
        string usage,
        Func<string, string, Intent> create)
    {
        string[] values = Tokens(arguments);
        return values.Length == 2
            ? TryCreate(() => create(values[0], values[1]))
            : Failure($"Usage: {usage}.");
    }

    private static HumanDecisionParseResult TryCreate(Func<Intent> create)
    {
        try
        {
            return Success(create());
        }
        catch (ArgumentException error)
        {
            return Failure(error.Message);
        }
    }

    private static bool TryParseInt64(string value, out long result) =>
        long.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out result);

    private static (string Head, string Tail) SplitFirst(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return (string.Empty, string.Empty);
        }

        int separator = -1;
        for (int index = 0; index < trimmed.Length; index++)
        {
            if (char.IsWhiteSpace(trimmed[index]))
            {
                separator = index;
                break;
            }
        }

        return separator < 0
            ? (trimmed, string.Empty)
            : (trimmed[..separator], trimmed[(separator + 1)..].Trim());
    }

    private static string[] Tokens(string value) =>
        value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static string? Optional(string value) =>
        value.Length == 0 ? null : value;

    private static HumanDecisionParseResult Success(Intent intent) => new(intent, null);

    private static HumanDecisionParseResult Failure(string error) => new(null, error);
}
