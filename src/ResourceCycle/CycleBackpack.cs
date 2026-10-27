using System.Collections.ObjectModel;
using Atelia.DurableGraph;

namespace DramaBoard.ResourceCycle;

/// <summary>An immutable item-count snapshot, with no gameplay capacity limit.</summary>
[DurableType("DramaBoard.ResourceCycle.Backpack", 2)]
public sealed partial class CycleBackpack : IDurableObject {
    [DurableField(1)] private Dictionary<string, int> _items;
    public IReadOnlyDictionary<string, int> Items => new ReadOnlyDictionary<string, int>(_items);
    public CycleBackpack(IReadOnlyDictionary<string, int>? items = null) {
        _items = items is null ? new(StringComparer.Ordinal) : new(items, StringComparer.Ordinal);
    }
    public int Count(string itemId) => _items.GetValueOrDefault(itemId);

    /// <summary>Whether this snapshot can represent one more unit of the given item.</summary>
    /// <remarks>This query reserves nothing. Callers must check again against the settlement snapshot.</remarks>
    internal bool CanAdd(string itemId) => Count(itemId) < int.MaxValue;

    /// <summary>Returns a new snapshot with one more unit; the original snapshot remains unchanged.</summary>
    /// <exception cref="OverflowException">The item quantity has reached its representation limit.</exception>
    internal CycleBackpack Add(string itemId) {
        var items = new Dictionary<string, int>(_items, StringComparer.Ordinal) { [itemId] = checked(Count(itemId) + 1) };
        return new(items);
    }
    internal void Validate() {
        if (_items.Any(item => string.IsNullOrWhiteSpace(item.Key) || item.Value <= 0)) {
            throw new InvalidOperationException("Backpack entries require an item identity and positive quantity.");
        }
    }
    public override string ToString() => $"[{string.Join(';', _items.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => $"{item.Key}:{item.Value}"))}]";
}
