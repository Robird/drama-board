using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DramaBoard.Protocol;

namespace DramaBoard.Player.Llm;

/// <summary>Reports either one parsed cognitive-loop output or a non-throwing failure.</summary>
public sealed record LlmOutputParseResult(
    bool IsSuccess,
    string? Error,
    string Monologue,
    Intent? Intent,
    string? Dialogue,
    string Memory);

/// <summary>Parses the four-section LLM response contract into an Intent and private text.</summary>
public static class LlmOutputParser
{
    private static readonly Regex SectionMarker = new(
        @"【\s*(独白|行动|台词|记忆)\s*】",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Parses a response without throwing for malformed model output.</summary>
    public static LlmOutputParseResult Parse(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return Failure("回复为空。");
        }

        IReadOnlyDictionary<string, string> sections = ExtractSections(response);
        if (!sections.TryGetValue("行动", out string? actionSection))
        {
            return Failure("回复缺少【行动】分节。");
        }

        string? dialogue = sections.TryGetValue("台词", out string? dialogueSection) &&
            !string.IsNullOrWhiteSpace(dialogueSection)
                ? dialogueSection.Trim()
                : null;
        if (!TryParseIntent(actionSection, dialogue, out Intent? intent, out string? error))
        {
            return Failure(error ?? "【行动】分节不包含合法的 Intent JSON。");
        }

        return new LlmOutputParseResult(
            IsSuccess: true,
            Error: null,
            Monologue: sections.GetValueOrDefault("独白", string.Empty).Trim(),
            Intent: intent,
            Dialogue: dialogue,
            Memory: sections.GetValueOrDefault("记忆", string.Empty).Trim());
    }

    private static IReadOnlyDictionary<string, string> ExtractSections(string response)
    {
        MatchCollection markers = SectionMarker.Matches(response);
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 0; index < markers.Count; index++)
        {
            Match marker = markers[index];
            int contentStart = marker.Index + marker.Length;
            int contentEnd = index + 1 < markers.Count
                ? markers[index + 1].Index
                : response.Length;
            sections[marker.Groups[1].Value] = response[contentStart..contentEnd].Trim();
        }

        return sections;
    }

    private static bool TryParseIntent(
        string actionSection,
        string? dialogue,
        out Intent? intent,
        out string? error)
    {
        intent = null;
        error = null;
        foreach (int objectStart in ObjectStarts(actionSection))
        {
            try
            {
                byte[] utf8 = Encoding.UTF8.GetBytes(actionSection[objectStart..]);
                var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });
                using JsonDocument document = JsonDocument.ParseValue(ref reader);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (TryCreateIntent(document.RootElement, dialogue, out intent))
                {
                    return true;
                }
            }
            catch (JsonException)
            {
                // Continue scanning because prose before the real object may contain a stray brace.
            }
            catch (ArgumentException exception)
            {
                error = exception.Message;
                return false;
            }
        }

        error ??= "【行动】分节不包含合法的 Intent JSON。";
        return false;
    }

    private static bool TryCreateIntent(
        JsonElement json,
        string? dialogue,
        out Intent? intent)
    {
        intent = null;
        if (!TryGetProperty(json, "action", out JsonElement actionElement) ||
            actionElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(actionElement.GetString()) ||
            !TryReadOptionalString(json, "targetActor", out string? targetActor) ||
            !TryReadOptionalString(json, "targetObject", out string? targetObject) ||
            !TryReadOptionalString(json, "exit", out string? exit) ||
            !TryReadOptionalString(json, "destination", out string? destination) ||
            !TryReadOptionalString(json, "freeText", out string? freeText) ||
            !TryReadOptionalInt64(json, "durationMs", out long? durationMs) ||
            !TryReadOptionalInt64(json, "untilModelTimeMs", out long? untilModelTimeMs))
        {
            return false;
        }

        intent = new Intent(
            new ActionKind(actionElement.GetString()!),
            TargetActorId: targetActor,
            TargetObjectId: targetObject,
            ExitId: exit,
            DestinationId: destination,
            FreeText: dialogue ?? freeText,
            DurationMs: durationMs,
            UntilModelTimeMs: untilModelTimeMs);
        return true;
    }

    private static IEnumerable<int> ObjectStarts(string text)
    {
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] == '{')
            {
                yield return index;
            }
        }
    }

    private static bool TryReadOptionalString(
        JsonElement json,
        string propertyName,
        out string? value)
    {
        value = null;
        if (!TryGetProperty(json, propertyName, out JsonElement element) ||
            element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return true;
    }

    private static bool TryReadOptionalInt64(
        JsonElement json,
        string propertyName,
        out long? value)
    {
        value = null;
        if (!TryGetProperty(json, propertyName, out JsonElement element) ||
            element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out long parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryGetProperty(
        JsonElement json,
        string propertyName,
        out JsonElement value)
    {
        foreach (JsonProperty property in json.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static LlmOutputParseResult Failure(string error) =>
        new(
            IsSuccess: false,
            Error: error,
            Monologue: string.Empty,
            Intent: null,
            Dialogue: null,
            Memory: string.Empty);
}
