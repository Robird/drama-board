namespace DramaBoard.Player.Llm.Tests;

public sealed class MemoryBankTests
{
    [Fact]
    public void Replace_ReturnsNewSnapshotAndPreservesShardOrderAndMetadata()
    {
        var original = new MemoryBank(
        [
            new MemoryShard("working", "当前处境", "快速更新。", "旧处境"),
            new MemoryShard("commitments", "承诺", "默认保留。", "旧承诺"),
        ]);

        MemoryBank updated = original.Replace("commitments", "新承诺");

        Assert.Equal("旧承诺", original["commitments"].Content);
        Assert.Equal("新承诺", updated["commitments"].Content);
        Assert.Equal(["working", "commitments"], updated.Shards.Select(shard => shard.Key));
        Assert.Equal("默认保留。", updated["commitments"].MaintenanceInstructions);
        Assert.True(updated.Render().IndexOf("当前处境", StringComparison.Ordinal) <
            updated.Render().IndexOf("承诺", StringComparison.Ordinal));
    }

    [Fact]
    public void Constructor_RejectsDuplicateShardKeys()
    {
        Assert.Throws<ArgumentException>(() => new MemoryBank(
        [
            new MemoryShard("beliefs", "判断", "维护判断。", "A"),
            new MemoryShard("beliefs", "另一判断", "维护判断。", "B"),
        ]));
    }
}
