using DramaBoard.ResourceCycle;

/// <summary>Host-only scenario selection and controller bindings; never stored in the game graph.</summary>
internal static class SurvivalHost {
    public static void RequireMode(CycleWorld world, int? characterCount) {
        int actual = world.Characters.Count;
        string[] expected = ["a", "b", "c"];
        if (world.Expedition is null || actual is < 1 or > 3 ||
            !world.Characters.Select(character => character.Id).Order(StringComparer.Ordinal).SequenceEqual(expected.Take(actual))) {
            throw new ArgumentException("--survival 需要含 a / a、b / a、b、c 的生存存档。");
        }
        if (characterCount is { } selected && selected != actual) {
            throw new ArgumentException($"指定 --characters {selected} 与存档人数 {actual} 不符；省略人数可直接沿用存档。");
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
