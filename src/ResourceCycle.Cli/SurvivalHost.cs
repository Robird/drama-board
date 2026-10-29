using DramaBoard.ResourceCycle;

/// <summary>Host-only scenario selection and controller bindings; never stored in the game graph.</summary>
internal static class SurvivalHost {
    public static void RequireMode(CycleWorld world, bool duo) {
        string[] expected = duo ? ["a", "b"] : ["a"];
        if (world.Expedition is null || !world.Characters.Select(character => character.Id)
            .Order(StringComparer.Ordinal).SequenceEqual(expected)) {
            throw new ArgumentException(duo ? "--duo 需要 a、b 双角色生存存档。" : "--survival 需要单角色 a 生存存档；双角色请使用 --duo。");
        }
    }

    public static string Summary(CycleWorld world) => "宿主全局总结（角色材料之外）：" + (world.ExpeditionSummary switch {
        CycleExpeditionSummary.AllEscaped => "全部获救",
        CycleExpeditionSummary.PartiallyEscaped => "部分获救",
        CycleExpeditionSummary.NoneEscaped => "无人获救",
        _ => "仍在探索"
    }) + "\n" + string.Join("\n", world.Characters.Select(character =>
        $"{character.Id} · {character.Name}：" + (character.Ending is { } ending
            ? $"{(ending.Outcome == CycleExpeditionOutcome.Escaped ? "已获救" : "已死亡")}（{ending.At}）"
            : "仍在探索")));
}

internal sealed class CharacterControllerRouter(IReadOnlyDictionary<string, ICycleCharacterController> bindings) : ICycleCharacterController {
    public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) =>
        bindings.TryGetValue(context.InteractionState.ActorId, out ICycleCharacterController? controller)
            ? controller.ChooseCommandAsync(context, cancellationToken)
            : throw new InvalidOperationException($"角色 {context.InteractionState.ActorId} 没有配置控制器。");
}
