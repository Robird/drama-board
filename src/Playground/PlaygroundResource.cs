using Atelia.DurableGraph;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Playground;

/// <summary>A shared stock with independent, phase-gated regeneration. The first prototype has one deterministic output item.</summary>
[DurableType("DramaBoard.Playground.Resource", 1)]
public sealed partial class PlaygroundResource : IDurableObject {
    [DurableField(1)] private string _id;
    [DurableField(2)] private string _name;
    [DurableField(3)] private string _itemId;
    [DurableField(4)] private bool _growsInDaylight;
    [DurableField(5)] private long _regenerationTicks;
    [DurableField(6)] private int _capacity;
    [DurableField(7)] private int _stock;
    [DurableField(8)] private ModelTime? _activeSince;
    [DurableField(9)] private long _accumulatedTicks;
    [DurableField(10)] private long _harvestTicks;
    [DurableField(11)] private string _skillId;
    [DurableField(12)] private int _requiredLevel;
    [DurableField(13)] private long _generation;
    public string Id => _id;
    public string Name => _name;
    public string ItemId => _itemId;
    public bool GrowsInDaylight => _growsInDaylight;
    public long RegenerationTicks => _regenerationTicks;
    public int Capacity => _capacity;
    public int Stock => _stock;
    public ModelTime? ActiveSince => _activeSince;
    public long AccumulatedTicks => _accumulatedTicks;
    public long HarvestTicks => _harvestTicks;
    public string SkillId => _skillId;
    public int RequiredLevel => _requiredLevel;
    public long Generation => _generation;
    public PlaygroundResource(string id, string name, string itemId, bool growsInDaylight, long regenerationTicks,
        int capacity, int stock, ModelTime? activeSince = null, long accumulatedTicks = 0,
        long harvestTicks = PlaygroundTime.Hour, string skillId = "herbalism", int requiredLevel = 0, long generation = 0) {
        _id = id; _name = name; _itemId = itemId; _growsInDaylight = growsInDaylight; _regenerationTicks = regenerationTicks;
        _capacity = capacity; _stock = stock; _activeSince = activeSince; _accumulatedTicks = accumulatedTicks;
        _harvestTicks = harvestTicks; _skillId = skillId; _requiredLevel = requiredLevel; _generation = generation;
    }
    public long ProgressAt(ModelTime time) => Stock == Capacity ? 0 : checked(AccumulatedTicks + (ActiveSince is { } since ? time.Ticks - since.Ticks : 0));
    internal PlaygroundResource Change(int stock, long progress, ModelTime? activeSince) =>
        new(Id, Name, ItemId, GrowsInDaylight, RegenerationTicks, Capacity, stock, activeSince, progress,
            HarvestTicks, SkillId, RequiredLevel, checked(Generation + 1));
    internal void Validate(ModelTime time, bool isDay) {
        if (string.IsNullOrWhiteSpace(Id) || Id.Contains(':') || string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(ItemId) ||
            SkillId != "herbalism" || RequiredLevel < 0 || RegenerationTicks <= 0 || HarvestTicks <= 0 || Capacity <= 0 ||
            Stock < 0 || Stock > Capacity || Generation < 0 || AccumulatedTicks < 0 || AccumulatedTicks > RegenerationTicks ||
            ActiveSince is { } since && (since.Ticks < 0 || since > time)) {
            throw new InvalidOperationException("Invalid resource state or definition.");
        }
        if (Stock == Capacity ? ActiveSince is not null || AccumulatedTicks != 0 :
            (ActiveSince is not null) != (GrowsInDaylight == isDay) || ProgressAt(time) > RegenerationTicks) {
            throw new InvalidOperationException("Resource regeneration does not match its stock and light phase.");
        }
    }
    public override string ToString() => $"{Id}:{Name}:{ItemId}:{GrowsInDaylight}:{RegenerationTicks}:{Capacity}:{Stock}:{ActiveSince?.Ticks.ToString() ?? "-"}:{AccumulatedTicks}:{HarvestTicks}:{SkillId}:{RequiredLevel}:{Generation}";
}
