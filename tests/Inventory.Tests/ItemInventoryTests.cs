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
        Assert.ThrowsAny<ArgumentException>(() => backpack.Remove(itemId!));
        Assert.Empty(backpack.Items);
    }

    [Fact]
    public void Removing_one_unit_frees_capacity_without_changing_the_original_snapshot_or_view() {
        var inventory = new ItemInventory(new Dictionary<string, int> { ["food"] = 2, ["water"] = 1 }, unitCapacity: 3);
        IReadOnlyDictionary<string, int> originalView = inventory.Items;

        ItemInventory consumed = inventory.Remove("food");

        Assert.NotSame(inventory, consumed);
        Assert.Equal(1, consumed.Count("food"));
        Assert.Equal(1, consumed.Count("water"));
        Assert.Equal(2L, consumed.TotalCount);
        Assert.Equal(3L, consumed.UnitCapacity);
        Assert.Equal(InventoryAdmission.Allowed, consumed.CheckAdd("wood"));
        Assert.Equal(2, originalView["food"]);
        Assert.Equal(3L, inventory.TotalCount);
        Assert.Equal(InventoryAdmission.CapacityLimit, inventory.CheckAdd("wood"));
    }

    [Fact]
    public void Removing_the_whole_stack_deletes_only_the_exact_ordinal_identity() {
        var inventory = new ItemInventory(new Dictionary<string, int> { ["food"] = 2, ["FOOD"] = 3 });

        ItemInventory consumed = inventory.Remove("food", 2);

        Assert.False(consumed.Items.ContainsKey("food"));
        Assert.Equal(0, consumed.Count("food"));
        Assert.Equal(3, consumed.Count("FOOD"));
        Assert.Equal("FOOD", Assert.Single(consumed.Items).Key);
        Assert.Null(consumed.UnitCapacity);
        Assert.Equal(2, inventory.Count("food"));
        consumed.Validate();
    }

    [Theory]
    [InlineData("food", 3)]
    [InlineData("absent", 1)]
    [InlineData("FOOD", 1)]
    public void Insufficient_removal_is_rejected_without_partial_consumption(string itemId, int quantity) {
        var inventory = new ItemInventory(new Dictionary<string, int> { ["food"] = 2 }, unitCapacity: 4);
        IReadOnlyDictionary<string, int> originalView = inventory.Items;

        Assert.Throws<InvalidOperationException>(() => inventory.Remove(itemId, quantity));

        Assert.Equal(2, inventory.Count("food"));
        Assert.Equal(2, Assert.Single(originalView).Value);
        Assert.Equal(4L, inventory.UnitCapacity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Removal_requires_a_positive_quantity(int quantity) {
        var inventory = new ItemInventory(new Dictionary<string, int> { ["food"] = 2 });

        Assert.Throws<ArgumentOutOfRangeException>(() => inventory.Remove("food", quantity));
        Assert.Equal(2, inventory.Count("food"));
    }

    [Fact]
    public void Maximum_representable_stack_can_be_removed_in_one_operation() {
        var inventory = new ItemInventory(new Dictionary<string, int> { ["food"] = int.MaxValue });

        ItemInventory consumed = inventory.Remove("food", int.MaxValue);

        Assert.Empty(consumed.Items);
        Assert.Equal(0L, consumed.TotalCount);
        Assert.Equal(int.MaxValue, inventory.Count("food"));
    }
}
