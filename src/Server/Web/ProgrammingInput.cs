using System.Text.Json;

namespace DramaBoard.Server.Web;

public sealed record ProgrammingInput(string ActivationId, IReadOnlyList<RawInstruction> Program, string Workspace) {
    public static bool TryRead(JsonElement body, out ProgrammingInput? input) {
        input = null;
        if (body.ValueKind != JsonValueKind.Object) { return false; }
        string? activationId = null;
        JsonElement programElement = default;
        bool programSeen = false;
        string? workspace = null;
        foreach (JsonProperty field in body.EnumerateObject()) {
            switch (field.Name) {
                case "activationId":
                    if (activationId is not null || field.Value.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(field.Value.GetString())) {
                        return false;
                    }
                    activationId = field.Value.GetString();
                    break;
                case "program":
                    if (programSeen || field.Value.ValueKind != JsonValueKind.Array) { return false; }
                    programSeen = true;
                    programElement = field.Value;
                    break;
                case "workspace":
                    if (workspace is not null || field.Value.ValueKind != JsonValueKind.String) { return false; }
                    workspace = field.Value.GetString();
                    break;
                default:
                    return false;
            }
        }
        if (activationId is null || !programSeen || workspace is null) { return false; }
        var program = new List<RawInstruction>();
        foreach (JsonElement entry in programElement.EnumerateArray()) {
            if (!TryReadInstruction(entry, out RawInstruction? instruction)) { return false; }
            program.Add(instruction!);
        }
        input = new(activationId, program.AsReadOnly(), workspace);
        return true;
    }

    private static bool TryReadInstruction(JsonElement element, out RawInstruction? instruction) {
        instruction = null;
        if (element.ValueKind != JsonValueKind.Object) { return false; }
        string? kind = null;
        string? passage = null;
        string? note = null;
        foreach (JsonProperty field in element.EnumerateObject()) {
            switch (field.Name) {
                case "kind":
                    if (kind is not null || field.Value.ValueKind != JsonValueKind.String) { return false; }
                    kind = field.Value.GetString();
                    break;
                case "passage":
                    if (passage is not null || field.Value.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(field.Value.GetString())) {
                        return false;
                    }
                    passage = field.Value.GetString();
                    break;
                case "note":
                    if (note is not null || field.Value.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(field.Value.GetString())) {
                        return false;
                    }
                    note = field.Value.GetString();
                    break;
                default:
                    return false;
            }
        }
        if (kind is null) { return false; }
        switch (kind) {
            case "move":
                if (passage is null || note is not null) { return false; }
                break;
            case "think":
                if (note is null || passage is not null) { return false; }
                break;
            case "stay":
                if (passage is not null || note is not null) { return false; }
                break;
            default:
                return false;
        }
        instruction = new(kind, passage, note);
        return true;
    }
}

public sealed record RawInstruction(string Kind, string? Passage, string? Note);
