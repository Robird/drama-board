using System.Collections.ObjectModel;
using Atelia.DurableGraph;

namespace DramaBoard.Inventory;

/// <summary>The result of attempting to accept one unit into an inventory snapshot.</summary>
public enum InventoryAdmission { Allowed, ItemQuantityLimit, CapacityLimit }

/// <summary>An immutable item-count snapshot with an optional total-unit capacity.</summary>
/// <remarks>The persistent identity predates this module; moving the CLR type does not change stored graphs.</remarks>
[DurableType("DramaBoard.ResourceCycle.Backpack", 3)]
public sealed partial class ItemInventory : IDurableObject {
    [DurableField(1)] private Dictionary<string, int> _items;
    [DurableField(2)] private long? _unitCapacity;
    public IReadOnlyDictionary<string, int> Items => new ReadOnlyDictionary<string, int>(_items);
    /// <summary>Maximum total quantity, or null for no gameplay capacity limit. Zero is a valid empty capacity.</summary>
    public long? UnitCapacity => _unitCapacity;
    public long TotalCount => _items.Values.Sum(quantity => (long)quantity);
    public ItemInventory(IReadOnlyDictionary<string, int>? items = null, long? unitCapacity = null) {
        _items = items is null ? new(StringComparer.Ordinal) : new(items, StringComparer.Ordinal);
        _unitCapacity = unitCapacity;
        Validate();
    }
    public int Count(string itemId) {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        return _items.GetValueOrDefault(itemId);
    }

    /// <summary>Checks whether this snapshot can accept one more unit of the given item.</summary>
    /// <remarks>This query reserves nothing. Check again against the settlement snapshot.
    /// The per-item representation limit takes precedence when both limits are reached.</remarks>
    public InventoryAdmission CheckAdd(string itemId) {
        if (Count(itemId) == int.MaxValue) { return InventoryAdmission.ItemQuantityLimit; }
        return UnitCapacity is { } capacity && TotalCount >= capacity
            ? InventoryAdmission.CapacityLimit : InventoryAdmission.Allowed;
    }

    /// <summary>Returns a new snapshot with one more unit; the original snapshot remains unchanged.</summary>
    /// <exception cref="OverflowException">The item quantity has reached its representation limit.</exception>
    /// <exception cref="InvalidOperationException">The total-unit capacity is exhausted.</exception>
    public ItemInventory Add(string itemId) {
        switch (CheckAdd(itemId)) {
            case InventoryAdmission.ItemQuantityLimit:
                throw new OverflowException("The item quantity has reached its representation limit.");
            case InventoryAdmission.CapacityLimit:
                throw new InvalidOperationException("The backpack's total-unit capacity is exhausted.");
        }
        var items = new Dictionary<string, int>(_items, StringComparer.Ordinal) { [itemId] = checked(Count(itemId) + 1) };
        return new(items, UnitCapacity);
    }
    /// <summary>Checks local invariants, including after hydration that bypasses the public constructor.</summary>
    /// <remarks>The composing game validates its complete graph before handing it to normal execution.</remarks>
    public void Validate() {
        if (_items.Any(item => string.IsNullOrWhiteSpace(item.Key) || item.Value <= 0)) {
            throw new InvalidOperationException("Backpack entries require an item identity and positive quantity.");
        }
        if (UnitCapacity < 0 || UnitCapacity is { } capacity && TotalCount > capacity) {
            throw new InvalidOperationException("Backpack capacity must be nonnegative and accommodate its contents.");
        }
    }
    public override string ToString() => $"[{string.Join(';', _items.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => $"{item.Key}:{item.Value}"))}]" +
        (UnitCapacity is { } capacity ? $";units={TotalCount}/{capacity}" : "");
}
