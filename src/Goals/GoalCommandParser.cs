using System.Text.Json;
using Atelia.DurableGraph;

namespace DramaBoard.Goals;

/// <summary>A closed operation kind plus its strictly checked JSON arguments. Rules revalidate all public inputs.</summary>
[DurableType("DramaBoard.Goals.GoalCommand", 1)]
public sealed partial record GoalCommand(
    [field: DurableField(1)] GoalOperationKind Kind,
    [field: DurableField(2)] string ArgumentsJson) : IDurableObject;

public static class GoalCommandParser {
    private static readonly string[] Names = ["create-goal", "update-goal", "link-goal", "update-goal-link", "unlink-goal", "focus-goal",
        "pause-goal", "resume-goal", "finish-goal", "reopen-goal", "forget-goal", "show-goal"];
    public static string Format(GoalCommand command) => Enum.IsDefined(command.Kind)
        ? Names[(int)command.Kind] + " " + command.ArgumentsJson : throw new ArgumentException("Unknown goal operation.", nameof(command));
    public static bool IsGoalCommand(string raw) => TryGetKind(raw, out _);
    public static bool TryGetKind(string raw, out GoalOperationKind kind) {
        kind = default;
        if (raw is null) { return false; }
        ReadOnlySpan<char> text = raw.AsSpan().TrimStart();
        int length = 0;
        while (length < text.Length && !char.IsWhiteSpace(text[length])) { length++; }
        for (int i = 0; i < Names.Length; i++) {
            if (text[..length].SequenceEqual(Names[i])) { kind = (GoalOperationKind)i; return true; }
        }
        return false;
    }

    public static bool TryParse(string raw, out GoalCommand? command, out string? error) {
        command = null;
        if (!TryGetKind(raw, out GoalOperationKind kind)) { error = "未知目标命令。"; return false; }
        string text = raw.TrimStart();
        var candidate = new GoalCommand(kind, text[Names[(int)kind].Length..].Trim());
        if (raw.Length > GoalLimits.MaximumCommandCodeUnits) { error = $"目标命令最多 {GoalLimits.MaximumCommandCodeUnits} 个 UTF-16 code units。"; return false; }
        if (!TryRead(candidate, out _, out error)) { return false; }
        command = candidate;
        return true;
    }

    internal static bool TryRead(GoalCommand command, out Dictionary<string, JsonElement> fields, out string? error) {
        fields = new(StringComparer.Ordinal);
        error = null;
        if (command is null || !Enum.IsDefined(command.Kind) || command.ArgumentsJson is null) { error = "无效目标操作。"; return false; }
        if (Names[(int)command.Kind].Length + 1 + command.ArgumentsJson.Length > GoalLimits.MaximumCommandCodeUnits) {
            error = $"目标命令最多 {GoalLimits.MaximumCommandCodeUnits} 个 UTF-16 code units。"; return false;
        }
        if (!ValidUtf16(command.ArgumentsJson)) { error = "目标命令含非法 Unicode。"; return false; }
        string allowed = command.Kind switch {
            GoalOperationKind.Create => "title criteria progress nextStep revisit parent linkReason focus",
            GoalOperationKind.Update => "id title criteria progress nextStep revisit pauseReason endNote reason",
            GoalOperationKind.Link => "parent child reason",
            GoalOperationKind.UpdateLink => "id reason",
            GoalOperationKind.Focus => "id via",
            GoalOperationKind.Pause => "id reason revisit",
            GoalOperationKind.Finish => "id outcome note",
            GoalOperationKind.Reopen => "id reason",
            _ => "id"
        };
        var keys = allowed.Split(' ').ToHashSet(StringComparer.Ordinal);
        try {
            using JsonDocument document = JsonDocument.Parse(command.ArgumentsJson, new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) { error = "参数必须是一个 JSON 对象。"; return false; }
            foreach (JsonProperty property in document.RootElement.EnumerateObject()) {
                if (!keys.Contains(property.Name) || !fields.TryAdd(property.Name, property.Value.Clone())) { error = "参数含未知或重复字段。"; return false; }
            }
            foreach ((string name, JsonElement value) in fields) {
                if (name == "focus") {
                    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) { error = "focus 必须是布尔值。"; return false; }
                    continue;
                }
                if (name == "via") {
                    if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 1 or > GoalLimits.MaximumGoals) { error = "via 必须是 1–32 个 Goal ID 的数组。"; return false; }
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (JsonElement item in value.EnumerateArray()) {
                        if (item.ValueKind != JsonValueKind.String || !ValidEscapes(item.GetRawText()) ||
                            GoalLimits.Identity(item.GetString()!, 'G') == 0 || !seen.Add(item.GetString()!)) { error = "via 必须包含不同的有效 Goal ID。"; return false; }
                    }
                    continue;
                }
                bool nullable = name is "progress" or "nextStep" or "revisit" || name == "id" && command.Kind == GoalOperationKind.Focus;
                if (nullable && value.ValueKind == JsonValueKind.Null) { continue; }
                if (value.ValueKind != JsonValueKind.String || !ValidEscapes(value.GetRawText())) { error = $"{name} 必须是合法 Unicode 文本。"; return false; }
                string? content = value.GetString();
                if (name is "id" or "parent" or "child") {
                    char prefix = name == "id" && command.Kind is GoalOperationKind.UpdateLink or GoalOperationKind.Unlink ? 'L' : 'G';
                    if (GoalLimits.Identity(content!, prefix) == 0) { error = $"{name} 必须是有效的 {prefix} ID。"; return false; }
                } else if (name == "outcome") {
                    if (content is not ("completed" or "abandoned")) { error = "outcome 只能是 completed 或 abandoned。"; return false; }
                } else {
                    int maximum = name switch {
                        "title" => GoalLimits.TitleScalars,
                        "criteria" => GoalLimits.CriteriaScalars,
                        "progress" => GoalLimits.ProgressScalars,
                        "nextStep" => GoalLimits.NextStepScalars,
                        "revisit" => GoalLimits.RevisitScalars,
                        "pauseReason" or "endNote" or "note" => GoalLimits.StateNoteScalars,
                        "reason" when command.Kind == GoalOperationKind.Pause => GoalLimits.StateNoteScalars,
                        _ => GoalLimits.ReasonScalars
                    };
                    if (!GoalLimits.ValidText(GoalLimits.Normalize(content), maximum, !nullable)) { error = $"{name} 必须符合非空规则且不超过 {maximum} 个 Unicode scalar。"; return false; }
                }
            }
            string required = command.Kind switch {
                GoalOperationKind.Create => "title criteria",
                GoalOperationKind.Link => "parent child reason",
                GoalOperationKind.UpdateLink or GoalOperationKind.Pause or GoalOperationKind.Reopen => "id reason",
                GoalOperationKind.Finish => "id outcome note",
                GoalOperationKind.Show => "",
                _ => "id"
            };
            foreach (string name in required.Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
                if (!fields.ContainsKey(name)) { error = $"缺少必填字段 {name}。"; return false; }
            }
            if (command.Kind == GoalOperationKind.Create && fields.ContainsKey("parent") != fields.ContainsKey("linkReason")) { error = "parent 和 linkReason 必须成对提供。"; return false; }
            if (command.Kind == GoalOperationKind.Update && fields.Keys.All(key => key is "id" or "reason")) { error = "update-goal 至少需要一个可编辑字段。"; return false; }
            if (command.Kind == GoalOperationKind.Focus && fields["id"].ValueKind == JsonValueKind.Null && fields.ContainsKey("via")) { error = "清空焦点时不能指定 via。"; return false; }
            return true;
        } catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException) {
            error = "参数不是合法的严格 JSON 对象（最多 4 层）。"; return false;
        }
    }

    private static bool ValidUtf16(string text) {
        for (int i = 0; i < text.Length; i++) {
            if (char.IsHighSurrogate(text[i])) { if (++i >= text.Length || !char.IsLowSurrogate(text[i])) { return false; } }
            else if (char.IsLowSurrogate(text[i])) { return false; }
        }
        return true;
    }

    // System.Text.Json may substitute a replacement character for an unpaired escaped surrogate.
    private static bool ValidEscapes(string token) {
        for (int i = 1; i < token.Length - 1; i++) {
            if (token[i] != '\\') { continue; }
            i++;
            if (token[i] != 'u') { continue; }
            int code = Convert.ToInt32(token.Substring(i + 1, 4), 16); i += 4;
            if (code is >= 0xDC00 and <= 0xDFFF) { return false; }
            if (code is >= 0xD800 and <= 0xDBFF) {
                if (i + 6 >= token.Length || token[i + 1] != '\\' || token[i + 2] != 'u') { return false; }
                int low = Convert.ToInt32(token.Substring(i + 3, 4), 16);
                if (low is < 0xDC00 or > 0xDFFF) { return false; }
                i += 6;
            }
        }
        return true;
    }
}
