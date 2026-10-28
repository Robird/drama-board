using System.Globalization;
using DramaBoard.ResourceCycle;

/// <summary>Pure action syntax; world validity remains the domain's responsibility.</summary>
internal static class CycleCommandParser {
    /// <remarks>Case-sensitive, whitespace-separated syntax. Handoff preserves internal whitespace.</remarks>
    public static CycleCommand? Parse(string input) {
        ArgumentNullException.ThrowIfNull(input);
        string trimmed = input.Trim();
        if (trimmed.StartsWith("handoff", StringComparison.Ordinal) && trimmed.Length > 7 && char.IsWhiteSpace(trimmed[7])) {
            string handoff = trimmed[8..].Trim();
            if (handoff.Length > 0) {
                return CycleCommand.Handoff(handoff == "--clear" ? null : handoff);
            }
        }
        string[] parts = input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts is ["wait", var hours] && int.TryParse(hours, NumberStyles.None, CultureInfo.InvariantCulture, out int waitHours)) {
            return CycleCommand.Wait(waitHours);
        }
        if (parts is ["gather", var allResource, "all"]) {
            return CycleCommand.GatherAll(allResource);
        }
        if (parts is ["gather", var resourceId, var quantity] && int.TryParse(quantity, NumberStyles.None, CultureInfo.InvariantCulture, out int count)) {
            return CycleCommand.Gather(resourceId, count);
        }
        return parts switch {
            ["continue"] => CycleCommand.Continue(),
            ["cancel"] => CycleCommand.Cancel(),
            ["go", var passageId] => CycleCommand.Go(passageId),
            ["use", var itemId] => CycleCommand.Use(itemId),
            _ => null,
        };
    }
}
