namespace DramaBoard.Protocol;

internal static class FrozenList {
    public static IReadOnlyList<T> Snapshot<T>(IEnumerable<T> values) {
        ArgumentNullException.ThrowIfNull(values);
        return Array.AsReadOnly(values.ToArray());
    }

    public static IReadOnlyList<T>? OptionalSnapshot<T>(IEnumerable<T>? values) =>
        values is null ? null : Snapshot(values);
}
