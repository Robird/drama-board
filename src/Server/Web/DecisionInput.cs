using System.Text.Json;

namespace DramaBoard.Server.Web;

public sealed record DecisionInput(string DecisionId, string ActionKind, string ExitId) {
    public static bool TryRead(JsonElement body, out DecisionInput? input) {
        input = null;
        if (body.ValueKind != JsonValueKind.Object) { return false; }
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty field in body.EnumerateObject()) {
            if (field.Name is not ("decisionId" or "actionKind" or "exitId") ||
                field.Value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(field.Value.GetString()) ||
                !values.TryAdd(field.Name, field.Value.GetString()!)) {
                return false;
            }
        }
        if (values.Count != 3) { return false; }
        input = new(values["decisionId"], values["actionKind"], values["exitId"]);
        return true;
    }
}
