/// <summary>Console-session preferences only; not character memory or durable game state.</summary>
internal sealed class PlayPresentation {
    private readonly IReadOnlyDictionary<string, IPlayLayout> _layouts;
    public IPlayLayout Current { get; private set; }

    public PlayPresentation(IPlayLayout initial, params IPlayLayout[] alternatives) {
        Current = initial;
        _layouts = alternatives.Prepend(initial).ToDictionary(layout => layout.Id, StringComparer.Ordinal);
    }

    public static PlayPresentation ForMode(bool survival) => survival
        ? new(new DecisionPlayLayout(), new ClassicPlayLayout())
        : new(new ClassicPlayLayout(), new DecisionPlayLayout());

    public bool TrySelect(string id) {
        if (!_layouts.TryGetValue(id, out IPlayLayout? selected)) { return false; }
        Current = selected;
        return true;
    }

    public string DescribeLayouts() => $"当前布局：{Current.Id}（{Current.Name}）。\n" +
        string.Join("\n", _layouts.Values.Select(layout => $"- `:layout {layout.Id}`：{layout.Name}")) +
        "\n切换立即重绘，仅影响本次控制台会话；不推进时间，不确认消息。";

    public const string MetaHelp = "元命令：`:layout` 列出可用布局，`:layout <名称>` 切换；`:look` 重绘；`:details` 查看完整观察；`:help` 查看规则与命令；`:exit` 退出。\n" +
        "元命令必须以冒号开头；游戏行动不加冒号。查看与切换不消耗时间、不确认消息。";
}
