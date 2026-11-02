using DramaBoard.Playground;

/// <summary>Host-only scenario selection and controller bindings; never stored in the game graph.</summary>
internal static class WorldHost {
    public static PlaygroundWorldDefinition Definition(string? content, int? characterCount) =>
        PlaygroundWorldDefinitions.Named(content ?? "hillside", characterCount: characterCount ?? 3);

    public static void RequireContent(PlaygroundWorld world, string? content) {
        if (content is null) { return; }
        PlaygroundWorldDefinition saved = world.Definition;
        PlaygroundWorldDefinition expected = PlaygroundWorldDefinitions.Named(content, start: saved.StartTime,
            characterCount: saved.InitialCharacters.Count, enableNeeds: saved.NeedsEnabled,
            enableWeather: saved.Weather.Enabled, weatherSeed: saved.Weather.Seed);
        expected.Validate();
        if (saved.ContentId != expected.ContentId || saved.Fingerprint != expected.Fingerprint) {
            throw new ArgumentException($"指定 --content {content} 与存档内容身份不符；省略 --content 可使用存档保存的定义。");
        }
    }

    public static void RequireWorld(PlaygroundWorld world, int? characterCount) {
        int actual = world.Characters.Count;
        string[] expected = ["a", "b", "c"];
        if (world.Expedition is null || actual is < 1 or > 3 ||
            !world.Characters.Select(character => character.Id).Order(StringComparer.Ordinal).SequenceEqual(expected.Take(actual))) {
            throw new ArgumentException("山野镇需要含 a / a、b / a、b、c 的连续世界存档。");
        }
        if (characterCount is { } selected && selected != actual) {
            throw new ArgumentException($"指定 --characters {selected} 与存档人数 {actual} 不符；省略人数可直接沿用存档。");
        }
    }

    public static void RequireBudget(DramaBoard.Kernel.Time.ModelTime current, DramaBoard.Kernel.Time.ModelTime? until, int maxSteps) {
        if (maxSteps < 1) { throw new ArgumentOutOfRangeException(nameof(maxSteps)); }
        if (until is { } limit && limit < current) { throw new ArgumentException("--until-hour 不能早于存档当前模拟时刻。"); }
    }

    public static string Summary(PlaygroundWorld world) => "宿主全局总结（角色材料之外）：当前已无可行动居民，模拟停止。\n"
        + string.Join("\n", world.Characters.Select(character => $"{character.Id} · {character.Name}："
            + (character.Ending is { } ending ? $"已结束（{ending.At}）" : "仍在生活")));

}

internal sealed class CharacterControllerRouter(IReadOnlyDictionary<string, IPlaygroundCharacterController> bindings) : IPlaygroundCharacterController {
    public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) =>
        bindings.TryGetValue(context.InteractionState.ActorId, out IPlaygroundCharacterController? controller)
            ? controller.ChooseCommandAsync(context, cancellationToken)
            : throw new InvalidOperationException($"角色 {context.InteractionState.ActorId} 没有配置控制器。");
}
