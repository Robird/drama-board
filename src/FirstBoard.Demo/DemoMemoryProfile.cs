using DramaBoard.Player.Llm;

namespace DramaBoard.FirstBoard.Demo;

internal static class DemoMemoryProfile
{
    public static MemoryBank Create(ScenarioActorDefinition actor) =>
        new(
        [
            .. actor.Role.InitialMemoryShards.Select(shard => new MemoryShard(
                shard.Key,
                shard.Title,
                shard.MaintenanceInstructions,
                shard.InitialContent)),
        ]);

    public static IReadOnlyList<IMemoryShardMaintainer> Maintainers(
        MemoryBank memory,
        Func<string, ILlmChatBackend> backendForShard) =>
        memory.Shards
            .Select(shard => (IMemoryShardMaintainer)new LlmMemoryShardMaintainer(
                shard.Key,
                backendForShard(shard.Key)))
            .ToArray();

}
