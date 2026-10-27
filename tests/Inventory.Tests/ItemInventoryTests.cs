namespace DramaBoard.Inventory.Tests;

public sealed class ItemInventoryTests {
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(int.MaxValue - 1)]
    public void Acceptance_and_add_preserve_the_original_snapshot(int quantity) {
        var initial = new Dictionary<string, int> { ["other"] = 3 };
        if (quantity > 0) { initial["herb"] = quantity; }
        var backpack = new ItemInventory(initial);
        IReadOnlyDictionary<string, int> originalView = backpack.Items;

        Assert.Equal(InventoryAdmission.Allowed, backpack.CheckAdd("herb"));
        ItemInventory added = backpack.Add("herb");

        Assert.NotSame(backpack, added);
        Assert.Equal(quantity, backpack.Count("herb"));
        Assert.Equal(quantity, originalView.GetValueOrDefault("herb"));
        Assert.Equal(quantity + 1, added.Count("herb"));
        Assert.Equal(3, added.Count("other"));
        Assert.Equal(quantity < int.MaxValue - 1 ? InventoryAdmission.Allowed : InventoryAdmission.ItemQuantityLimit, added.CheckAdd("herb"));
        Assert.Equal(InventoryAdmission.Allowed, backpack.CheckAdd("herb"));
    }

    [Fact]
    public void Full_item_rejects_another_unit_without_blocking_other_items() {
        var backpack = new ItemInventory(new Dictionary<string, int> { ["herb"] = int.MaxValue });

        Assert.Equal(InventoryAdmission.ItemQuantityLimit, backpack.CheckAdd("herb"));
        Assert.Throws<OverflowException>(() => backpack.Add("herb"));
        Assert.Equal(int.MaxValue, backpack.Count("herb"));
        Assert.Equal(InventoryAdmission.Allowed, backpack.CheckAdd("ore"));
        Assert.Equal(1, backpack.Add("ore").Count("ore"));
        Assert.Equal(0, backpack.Count("ore"));
    }

    [Fact]
    public void Construction_owns_its_item_snapshot_and_keeps_ordinal_identities() {
        var supplied = new Dictionary<string, int> { ["herb"] = 2 };
        var backpack = new ItemInventory(supplied);
        supplied["herb"] = int.MaxValue;
        supplied["ore"] = 9;

        Assert.Equal(2, backpack.Count("herb"));
        Assert.Equal(InventoryAdmission.Allowed, backpack.CheckAdd("herb"));
        Assert.Equal(0, backpack.Count("ore"));
        Assert.Equal(0, backpack.Count("HERB"));
        ItemInventory added = backpack.Add("HERB");
        Assert.Equal(2, added.Count("herb"));
        Assert.Equal(1, added.Count("HERB"));
    }

    [Fact]
    public void Different_items_share_capacity_and_add_cannot_bypass_rejection() {
        var backpack = new ItemInventory(new Dictionary<string, int> { ["ore"] = 2 }, unitCapacity: 3);
        IReadOnlyDictionary<string, int> originalView = backpack.Items;

        Assert.Equal(InventoryAdmission.Allowed, backpack.CheckAdd("herb"));
        ItemInventory full = backpack.Add("herb");

        Assert.Equal(3L, full.UnitCapacity);
        Assert.Equal(3L, full.TotalCount);
        Assert.Equal(InventoryAdmission.CapacityLimit, full.CheckAdd("herb"));
        Assert.Equal(InventoryAdmission.CapacityLimit, full.CheckAdd("new-item"));
        Assert.Throws<InvalidOperationException>(() => full.Add("ore"));
        Assert.Equal(2, full.Count("ore"));
        Assert.Equal(1, full.Count("herb"));
        Assert.Equal(2L, backpack.TotalCount);
        Assert.Equal(0, backpack.Count("herb"));
        Assert.False(originalView.ContainsKey("herb"));
        Assert.Equal(3L, backpack.UnitCapacity);
    }

    [Fact]
    public void Zero_capacity_is_valid_but_accepts_nothing() {
        var backpack = new ItemInventory(unitCapacity: 0);

        Assert.Equal(0L, backpack.TotalCount);
        Assert.Equal(InventoryAdmission.CapacityLimit, backpack.CheckAdd("herb"));
        Assert.Throws<InvalidOperationException>(() => backpack.Add("herb"));
        Assert.Empty(backpack.Items);
        backpack.Validate();
    }

    [Fact]
    public void Totals_use_long_and_item_limit_takes_precedence_over_total_capacity() {
        var items = new Dictionary<string, int> { ["ore"] = int.MaxValue, ["herb"] = int.MaxValue };
        var unlimited = new ItemInventory(items);
        var full = new ItemInventory(items, unitCapacity: 2L * int.MaxValue);

        Assert.Null(unlimited.UnitCapacity);
        Assert.Equal(2L * int.MaxValue, unlimited.TotalCount);
        Assert.Equal(InventoryAdmission.Allowed, unlimited.CheckAdd("new-item"));
        Assert.Equal(2L * int.MaxValue + 1, unlimited.Add("new-item").TotalCount);
        Assert.Equal(InventoryAdmission.ItemQuantityLimit, full.CheckAdd("ore"));
        Assert.Equal(InventoryAdmission.CapacityLimit, full.CheckAdd("new-item"));
        Assert.Throws<OverflowException>(() => full.Add("ore"));
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(0L)]
    [InlineData(1L)]
    public void Construction_rejects_invalid_capacity_or_contents_exceeding_it(long capacity) {
        Assert.Throws<InvalidOperationException>(() => new ItemInventory(
            new Dictionary<string, int> { ["ore"] = 2 }, capacity));
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData(" ", 1)]
    [InlineData("herb", 0)]
    [InlineData("herb", -1)]
    public void Construction_rejects_invalid_entries(string itemId, int count) {
        Assert.Throws<InvalidOperationException>(() => new ItemInventory(new Dictionary<string, int> { [itemId] = count }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Queries_and_add_reject_invalid_item_identities(string? itemId) {
        var backpack = new ItemInventory();

        Assert.ThrowsAny<ArgumentException>(() => backpack.Count(itemId!));
        Assert.ThrowsAny<ArgumentException>(() => backpack.CheckAdd(itemId!));
        Assert.ThrowsAny<ArgumentException>(() => backpack.Add(itemId!));
        Assert.Empty(backpack.Items);
    }
}
