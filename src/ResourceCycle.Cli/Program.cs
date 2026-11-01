using System.Globalization;
using System.Text;
using DramaBoard.Kernel.Time;
using DramaBoard.ResourceCycle;

Console.OutputEncoding = new UTF8Encoding(false);
Console.InputEncoding = Encoding.UTF8;
bool hasStore = false;
try {
    var options = Options.Parse(args);
    hasStore = options.Store is not null;
    if (options.Help) {
        Console.WriteLine("""
            山野镇 / Hillside — 小镇与山林中的持续生活

            dotnet run --project src/ResourceCycle.Cli -- [options]
              --store PATH          创建或恢复存档；省略则仅内存运行
              --branch ID           打开已有分支；多分支时必须指定
              --content hillside|gentle 新局内容；省略新局为 hillside，续局使用存档定义
              --characters 1|2|3    新局人数，默认 3；恢复时省略则沿用存档
              --llm                 所有角色由模型控制；省略时共用 Human Console
              --human ID            配合 --llm，将指定角色交给 Human Console
              --model ID            模型 ID；默认 Codex Responses / gpt-6.1-sol
              --auth-file PATH      内置 Codex 认证文件；默认使用标准路径
              --catalog PATH        Completion V3 连接目录，配合 --connection ID
              --connection ID       精确选择目录连接，不使用回退连接
              --max-calls N         本次共享模型调用预算（默认 128）
              --max-failures N      连续协议错误上限（默认 3）
              --max-context-chars N Context 字符长度上限（默认 200000）
              --llm-log PATH        本次独立模型证据文件
              --until-hour N        本次绝对模拟小时上界；达到上界暂停，不结束世界
              --steps N             本次已提交转换上限（默认 10000）
              --gym                 配合 --llm --store 记录角色轨迹
              --feedback            同时启用 --gym 与模型反馈工具
              --branches            列出 --store 分支，不运行游戏
              --gym-feedbacks       列出反馈；可选 --branch / --actor
              --gym-export FILE     导出轨迹到新文件；需 --actor，可选 --branch
              --gym-fork ID         从反馈的决定前 S 注册分支
              --gym-fork-request ID 从请求的决定前 S 注册分支
              --fork-label LABEL    新分支名称；注册后另用 --branch 续跑
              --actor ID            管理查询角色，不改变控制绑定
              --help                显示帮助

            新局从 D0 08:00 开始。时间跳到事件，等待输入时世界暂停。
            模拟上界不保证有事件恰好落在该时刻；保留最后完成事件的时间。
            :quit、EOF 或 Ctrl+C 暂停。以同一 --store 续局，可重新选择控制绑定。
            """);
        return 0;
    }
    if (options.Management is { } management) {
        GymCommands.Run(options.Store!, options.Branch, management, Console.Out);
        return 0;
    }
    if (options.Branch is not null && !Directory.Exists(options.Store)) {
        throw new ArgumentException("指定分支需要已有存档。");
    }
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler handler = (_, signal) => { signal.Cancel = true; cancellation.Cancel(); };
    Console.CancelKeyPress += handler;
    try {
        return options.Llm is { } llm
            ? await LlmConsole.RunAsync(options.Store, llm, cancellation.Token, characterCount: options.CharacterCount)
            : await PlayConsole.RunAsync(options.Store, cancellation.Token, characterCount: options.CharacterCount,
                branch: options.Branch, until: options.Until, maxSteps: options.Steps, content: options.Content);
    } finally { Console.CancelKeyPress -= handler; }
} catch (OperationCanceledException) {
    Console.Error.WriteLine(hasStore ? "已暂停。已提交进度已保存，使用同一 --store 续局。" : "已暂停；内存进度未保存。");
    return 130;
} catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or InvalidDataException or UnauthorizedAccessException or OverflowException or FormatException) {
    Console.Error.WriteLine($"Hillside: {error.Message}");
    return 1;
}

internal sealed record Options(string? Store, ModelTime? Until, int Steps, bool Help, LlmHostOptions? Llm = null, int? CharacterCount = null,
    string? Branch = null, GymManagementOptions? Management = null, string? Content = null) {
    public static Options Parse(string[] args) {
        string? store = null, content = null;
        long? hour = null;
        int steps = 10_000;
        bool help = false, llm = false;
        string? human = null;
        int? characterCount = null;
        bool gym = false, feedback = false, branches = false, feedbacks = false;
        string? branch = null, actor = null, export = null, fork = null, forkRequest = null, forkLabel = null;
        string? model = null, authFile = null, catalog = null, connection = null, llmLog = null;
        int maxCalls = 128, maxFailures = 3, maxContextCharacters = 200_000;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < args.Length; index++) {
            string option = args[index];
            if (!seen.Add(option)) { throw new ArgumentException($"重复参数：{option}"); }
            string Value() => ++index < args.Length && !string.IsNullOrWhiteSpace(args[index])
                ? args[index] : throw new ArgumentException($"参数缺少值：{option}");
            switch (option) {
                case "--content":
                    content = Value();
                    if (content is not ("hillside" or "gentle")) { throw new ArgumentException("--content 必须为 hillside 或 gentle。"); }
                    break;
                case "--store": store = Path.GetFullPath(Value()); break;
                case "--until-hour":
                    if (!long.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out long parsedHour)) {
                        throw new ArgumentException("--until-hour 必须为非负整数。");
                    }
                    hour = parsedHour;
                    break;
                case "--steps":
                    if (!int.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out steps) || steps < 1) {
                        throw new ArgumentException("--steps 必须为正整数。");
                    }
                    break;
                case "--help": help = true; break;
                case "--characters":
                    characterCount = Positive(Value(), option);
                    if (characterCount > 3) { throw new ArgumentException("--characters 必须为 1、2 或 3。"); }
                    break;
                case "--llm": llm = true; break;
                case "--human": human = Value(); break;
                case "--branch": branch = Value(); break;
                case "--actor": actor = Value(); break;
                case "--gym": gym = true; break;
                case "--feedback": feedback = true; break;
                case "--branches": branches = true; break;
                case "--gym-feedbacks": feedbacks = true; break;
                case "--gym-export": export = Path.GetFullPath(Value()); break;
                case "--gym-fork": fork = Value(); break;
                case "--gym-fork-request": forkRequest = Value(); break;
                case "--fork-label": forkLabel = Value(); break;
                case "--model": model = Value(); break;
                case "--auth-file": authFile = Path.GetFullPath(Value()); break;
                case "--catalog": catalog = Path.GetFullPath(Value()); break;
                case "--connection": connection = Value(); break;
                case "--llm-log": llmLog = Path.GetFullPath(Value()); break;
                case "--max-calls": maxCalls = Positive(Value(), option); break;
                case "--max-failures": maxFailures = Positive(Value(), option); break;
                case "--max-context-chars": maxContextCharacters = Positive(Value(), option); break;
                default: throw new ArgumentException($"未知参数：{option}；使用 --help 查看用法。");
            }
        }
        GymManagementOptions? management = null;
        int managementModes = (branches ? 1 : 0) + (feedbacks ? 1 : 0) + (export is not null ? 1 : 0)
            + (fork is not null ? 1 : 0) + (forkRequest is not null ? 1 : 0);
        if (!help && managementModes > 0) {
            if (managementModes != 1 || store is null) { throw new ArgumentException("管理模式只能选择一种，并必须指定 --store。"); }
            string[] allowed = branches ? ["--store", "--branches"] : feedbacks
                ? ["--store", "--gym-feedbacks", "--branch", "--actor"] : export is not null
                ? ["--store", "--gym-export", "--branch", "--actor"]
                : ["--store", "--gym-fork", "--gym-fork-request", "--fork-label"];
            if (seen.Any(option => !allowed.Contains(option, StringComparer.Ordinal))) {
                throw new ArgumentException("管理模式不能混用游戏、模型或其他管理配置。");
            }
            if (export is not null && actor is null) { throw new ArgumentException("--gym-export 必须指定 --actor。"); }
            if ((fork is not null || forkRequest is not null) && forkLabel is null) { throw new ArgumentException("分叉必须指定 --fork-label。"); }
            management = new(branches ? GymManagementKind.Branches : feedbacks ? GymManagementKind.Feedbacks : export is not null
                ? GymManagementKind.Export : fork is not null ? GymManagementKind.ForkFeedback : GymManagementKind.ForkRequest,
                actor, export, fork ?? forkRequest, forkLabel);
        }
        if (!help && managementModes == 0 && (actor is not null || forkLabel is not null)) {
            throw new ArgumentException("--actor / --fork-label 仅用于对应的管理模式。");
        }
        if (!help && branch is not null && store is null) { throw new ArgumentException("--branch 必须配合已有 --store。"); }
        if (!help && (gym || feedback) && (!llm || store is null)) { throw new ArgumentException("--gym / --feedback 必须配合 --llm 和 --store。"); }
        if (!help && branch is not null) {
            try { branch = CycleDurableHistory.FormatBranchId(CycleDurableHistory.ParseBranchId(branch)); }
            catch (FormatException error) { throw new ArgumentException("--branch 必须为有效分支 ID。", error); }
        }
        if (!help && !llm && seen.Any(option => option is "--model" or "--auth-file" or "--catalog" or "--connection" or
            "--max-calls" or "--max-failures" or "--max-context-chars" or "--llm-log" or "--human")) {
            throw new ArgumentException("模型配置和预算参数必须配合 --llm。");
        }
        if (!help && ((catalog is null) != (connection is null))) { throw new ArgumentException("--catalog 与 --connection 必须一起指定。"); }
        if (!help && catalog is not null && authFile is not null) { throw new ArgumentException("--auth-file 仅用于内置 Codex 连接；目录连接使用 Completion 的认证配置。"); }
        ModelTime? until = hour is { } selectedHour ? new ModelTime(checked(selectedHour * 3_600_000)) : null;
        return new(store, until, steps, help,
            llm ? new(model, authFile, catalog, connection, maxCalls, maxFailures, maxContextCharacters, llmLog,
                gym || feedback, feedback, branch, human, until, steps, content) : null, characterCount, branch, management, content);
    }
    private static int Positive(string text, string option) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value > 0
            ? value : throw new ArgumentException($"{option} 必须为正整数。");
}
