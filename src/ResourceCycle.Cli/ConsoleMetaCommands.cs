internal enum ConsoleMetaCommandKind { Look, Help, Details, Layout, Mode, Exit }

internal sealed record ConsoleMetaCommand(ConsoleMetaCommandKind Kind, string? Argument = null);

/// <summary>Immutable Human Console capabilities. Parsing and help share the same finite definitions.</summary>
internal sealed class ConsoleMetaCommands {
    private enum ArgumentShape { None, OptionalLayout, OptionalMode }
    private sealed record Definition(ConsoleMetaCommandKind Kind, string Name, string? Alias,
        ArgumentShape Argument, string Description) {
        public string Syntax => $":{Name}";
    }

    private readonly Definition[] _definitions;

    public static ConsoleMetaCommands HumanDefault { get; } = new([
        new(ConsoleMetaCommandKind.Layout, "layout", null, ArgumentShape.OptionalLayout, "无参数列出可用布局，带名称切换"),
        new(ConsoleMetaCommandKind.Mode, "mode", null, ArgumentShape.OptionalMode, "无参数列出输出模式，带名称切换"),
        new(ConsoleMetaCommandKind.Look, "look", null, ArgumentShape.None, "重绘当前观察"),
        new(ConsoleMetaCommandKind.Details, "details", null, ArgumentShape.None, "查看完整观察"),
        new(ConsoleMetaCommandKind.Help, "help", null, ArgumentShape.None, "查看规则与命令"),
        new(ConsoleMetaCommandKind.Exit, "exit", "quit", ArgumentShape.None, "退出 Console 会话"),
    ]);
    public static ConsoleMetaCommands None { get; } = new([]);

    private ConsoleMetaCommands(Definition[] definitions) => _definitions = definitions;

    public ConsoleMetaCommand? Parse(string input) {
        ArgumentNullException.ThrowIfNull(input);
        string[] parts = input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2) { return null; }
        foreach (Definition definition in _definitions) {
            if (parts[0] != definition.Syntax && (definition.Alias is null || parts[0] != $":{definition.Alias}")) { continue; }
            if (parts.Length == 2 && definition.Argument == ArgumentShape.None) { return null; }
            return new(definition.Kind, parts.Length == 2 ? parts[1] : null);
        }
        return null;
    }

    public string RenderHint(PlayPresentation presentation) => _definitions.Length == 0 ? ""
        : "元命令：" + string.Join("；", _definitions.Select(definition => Describe(definition, presentation))) + "。\n" +
            "元命令必须以冒号开头；游戏行动不加冒号。查看与切换不消耗时间、不确认消息。";

    public string RenderHelp(PlayPresentation presentation) => _definitions.Length == 0 ? ""
        : RenderHint(presentation) + "\n" + DescribeLayouts(presentation) + "\n" + DescribeModes(presentation) +
            "\n输入流结束（EOF）也会结束 Console 会话。";

    public string RejectionHint => _definitions.FirstOrDefault(definition => definition.Kind == ConsoleMetaCommandKind.Help) is { } help
        ? $"输入 `{help.Syntax}` 查看规则与命令。" : "";

    public string DescribeLayouts(PlayPresentation presentation) {
        Definition? definition = _definitions.FirstOrDefault(item => item.Kind == ConsoleMetaCommandKind.Layout);
        if (definition is null) { return ""; }
        return $"当前布局：{presentation.Current.Id}（{presentation.Current.Name}）。\n" +
            string.Join("\n", presentation.Layouts.Select(layout => $"- `{definition.Syntax} {layout.Id}`：{layout.Name}")) +
            "\n切换立即重绘，仅影响本次控制台会话；不推进时间，不确认消息。";
    }

    public string DescribeModes(PlayPresentation presentation) {
        Definition? definition = _definitions.FirstOrDefault(item => item.Kind == ConsoleMetaCommandKind.Mode);
        if (definition is null) { return ""; }
        return $"当前输出模式：{presentation.Mode.ToString().ToLowerInvariant()}。\n" +
            string.Join("\n", presentation.SupportedModes.Select(mode => $"- `{definition.Syntax} {mode.Key}`：" +
                (mode.Value == PlayOutputMode.Full ? "每次提交显示事件、效果与完整当前状态。" : "每次提交显示事件、效果与受影响的当前状态。"))) +
            "\n切换不推进时间，不确认消息；立即全量重同步。";
    }

    private static string Describe(Definition definition, PlayPresentation presentation) {
        string argument = definition.Argument switch {
            ArgumentShape.OptionalLayout => " [名称]",
            ArgumentShape.OptionalMode => $" [{string.Join('|', presentation.SupportedModes.Keys)}]",
            _ => "",
        };
        string alias = definition.Alias is null ? "" : $"（别名 `:{definition.Alias}`）";
        return $"`{definition.Syntax}{argument}`{alias}：{definition.Description}";
    }
}
