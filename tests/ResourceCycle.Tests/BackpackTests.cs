namespace DramaBoard.ResourceCycle.Tests;

public sealed class BackpackTests {
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(int.MaxValue - 1)]
    public void Acceptance_and_add_preserve_the_original_snapshot(int quantity) {
        var initial = new Dictionary<string, int> { ["other"] = 3 };
        if (quantity > 0) { initial["herb"] = quantity; }
        var backpack = new CycleBackpack(initial);
        IReadOnlyDictionary<string, int> originalView = backpack.Items;

        Assert.True(backpack.CanAdd("herb"));
        CycleBackpack added = backpack.Add("herb");

        Assert.NotSame(backpack, added);
        Assert.Equal(quantity, backpack.Count("herb"));
        Assert.Equal(quantity, originalView.GetValueOrDefault("herb"));
        Assert.Equal(quantity + 1, added.Count("herb"));
        Assert.Equal(3, added.Count("other"));
        Assert.Equal(quantity < int.MaxValue - 1, added.CanAdd("herb"));
        Assert.True(backpack.CanAdd("herb"));
    }

    [Fact]
    public void Full_item_rejects_another_unit_without_blocking_other_items() {
        var backpack = new CycleBackpack(new Dictionary<string, int> { ["herb"] = int.MaxValue });

        Assert.False(backpack.CanAdd("herb"));
        Assert.Throws<OverflowException>(() => backpack.Add("herb"));
        Assert.Equal(int.MaxValue, backpack.Count("herb"));
        Assert.True(backpack.CanAdd("ore"));
        Assert.Equal(1, backpack.Add("ore").Count("ore"));
        Assert.Equal(0, backpack.Count("ore"));
    }

    [Fact]
    public void Construction_owns_its_item_snapshot_and_keeps_ordinal_identities() {
        var supplied = new Dictionary<string, int> { ["herb"] = 2 };
        var backpack = new CycleBackpack(supplied);
        supplied["herb"] = int.MaxValue;
        supplied["ore"] = 9;

        Assert.Equal(2, backpack.Count("herb"));
        Assert.True(backpack.CanAdd("herb"));
        Assert.Equal(0, backpack.Count("ore"));
        Assert.Equal(0, backpack.Count("HERB"));
        CycleBackpack added = backpack.Add("HERB");
        Assert.Equal(2, added.Count("herb"));
        Assert.Equal(1, added.Count("HERB"));
    }
}
