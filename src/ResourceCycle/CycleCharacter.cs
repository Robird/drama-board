using System.Collections.ObjectModel;
using Atelia.DurableGraph;
using DramaBoard.Kernel.Time;

namespace DramaBoard.ResourceCycle;

/// <summary>An independent inventory subgraph; only accepted actions replace it.</summary>
[DurableType("DramaBoard.ResourceCycle.Backpack", 1)]
public sealed partial class CycleBackpack : IDurableObject {
    [DurableField(1)] private Dictionary<string, int> _items = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, int> Items => new ReadOnlyDictionary<string, int>(_items);
    internal CycleBackpack() { }
    private CycleBackpack(Dictionary<string, int> items) { _items = items; }
    public int Count(string itemId) => _items.GetValueOrDefault(itemId);
    internal CycleBackpack Add(string itemId) {
        var items = new Dictionary<string, int>(_items, StringComparer.Ordinal) {
            [itemId] = checked(Count(itemId) + 1)
        };
        return new(items);
    }
    internal void Validate() {
        if (_items.Any(item => item.Key is not ("sunflower" or "moongrass") || item.Value <= 0)) {
            throw new InvalidOperationException("Backpack entries must contain positive known resource quantities.");
        }
    }
    public override string ToString() => $"[{string.Join(';', _items.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => $"{item.Key}:{item.Value}"))}]";
}

/// <summary>Role state only. The provider of its next decision is a runtime dependency.</summary>
[DurableType("DramaBoard.ResourceCycle.Character", 1)]
public sealed partial class CycleCharacter : IDurableObject {
    [DurableField(1)] private string _id;
    [DurableField(2)] private CycleBackpack _backpack;
    [DurableField(3)] private ModelTime? _waitUntil;
    [DurableField(4)] private long _decisionGeneration;
    [DurableField(5)] private string? _lastResult;
    public string Id => _id;
    public CycleBackpack Backpack => _backpack;
    public ModelTime? WaitUntil => _waitUntil;
    public long DecisionGeneration => _decisionGeneration;
    public string? LastResult => _lastResult;
    internal CycleCharacter(string id, CycleBackpack backpack, ModelTime? waitUntil = null,
        long decisionGeneration = 0, string? lastResult = null) {
        _id = id; _backpack = backpack; _waitUntil = waitUntil;
        _decisionGeneration = decisionGeneration; _lastResult = lastResult;
    }
    internal void Validate(ModelTime time) {
        if (string.IsNullOrWhiteSpace(Id) || Backpack is null || DecisionGeneration < 0 || WaitUntil < time) {
            throw new InvalidOperationException("Invalid character action state.");
        }
        Backpack.Validate();
    }
    public override string ToString() => $"{Id};backpack={Backpack};waitUntil={WaitUntil?.Ticks.ToString() ?? "-"};decision={DecisionGeneration};result={LastResult ?? "-"}";
}
