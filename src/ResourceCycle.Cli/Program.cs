using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.ResourceCycle;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;
bool hasStore = false;
try {
    var options = Options.Parse(args);
    hasStore = options.Store is not null;
    if (options.Help) {
        Console.WriteLine("""
            ResourceCycle — 昼夜、资源再生与多人采集实验

            dotnet run --project src/ResourceCycle.Cli -- [options]
              --store PATH          创建或恢复单根存档；省略则仅内存运行
              --play                三角色轮流采集；可配合 --store 续玩，不接受批处理选项
              --survival            单角色山林探索；可配合 --store 续玩，不接受批处理选项
              --duo                 双角色山林探索；共用 Console，配合 --llm 时 a 由人类、b 由模型控制
              --llm                 --survival 控制 a；--duo 控制 b；默认 Codex Responses / gpt-6.1-sol
              --model ID            模型 ID；自定义目录默认沿用所选连接的模型
              --auth-file PATH      内置 Codex 连接的认证文件；默认使用 Codex 的标准路径
              --catalog PATH        Completion V3 连接目录；必须配合 --connection ID 精确选择
              --connection ID       自定义目录中的连接 ID，不使用回退连接
              --max-calls N         本次宿主运行最多模型调用次数（默认 128）
              --max-failures N      连续完整响应协议错误上限（默认 3）
              --max-context-chars N 完整 Context 的 UTF-16 长度上限（默认 200000）
              --llm-log PATH        本次独立 JSONL 证据文件；默认在存档或 artifacts 下新建
              --until-hour N        运行到绝对逻辑小时上界（默认 88，即 D3 16:00）
              --steps N             本次最多推进 N 次转换（默认 10000）
              --full-recompute      每次重算全部预测者，用于对照
              --trace               每次转换后显示完整诊断
              --snapshot PATH       写入最终状态诊断（不含运行期计数）
              --help                显示帮助

            默认从 D0 16:00 开始。时间跳到事件，不按墙钟等待。
            上界之间若没有事件，保持最后完成事件的逻辑时刻。
            Ctrl+C 停止；已有 pending 事件将在下次打开时完成。
            """);
        return 0;
    }

    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelHandler = (_, eventArgs) => {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };
    Console.CancelKeyPress += cancelHandler;
    try {
        if (options.Play || options.Survival || options.Duo) {
            if (options.Llm is { } llm) { return await LlmConsole.RunAsync(options.Store, llm, cancellation.Token, duo: options.Duo); }
            return await PlayConsole.RunAsync(options.Store, cancellation.Token, options.Survival || options.Duo, duo: options.Duo);
        }
        using CycleDurableHistory? durable = options.Store is { } store
            ? Directory.Exists(store)
                ? CycleDurableHistory.Open(store)
                : CreateStore(store)
            : null;
        IOccurrenceHistory<CycleWorld, CycleFact> history;
        if (durable is not null) {
            history = durable;
        } else {
            var initial = CycleGameFactory.CreateBatch();
            history = new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor);
        }

        PlayConsole.RequireBatchWorld(history.State);
        var metrics = new CycleMetrics();
        var kernel = CycleEngine.CreateKernel(history,
            options.FullRecompute ? PredictionMode.Full : PredictionMode.Incremental, metrics);
        if (kernel.RecoverPending(cancellation.Token)) {
            Console.WriteLine($"已完成 pending 事件，恢复至 {kernel.CurrentModelTime}。");
        }
        if (options.Until < kernel.CurrentModelTime) {
            throw new ArgumentException("--until-hour 不能早于存档当前逻辑时刻。");
        }
        Console.WriteLine(options.FullRecompute ? "模式：全量重算对照" : "模式：依赖驱动增量重算");
        Console.WriteLine(CycleDiagnostics.Render(kernel.World));

        int completed = 0;
        StepStatus status = StepStatus.Committed;
        while (completed < options.Steps) {
            status = await kernel.StepAsync(options.Until, cancellation.Token);
            if (status != StepStatus.Committed) { break; }
            completed++;
            var occurrence = kernel.LastCompletion!;
            foreach (CycleFact fact in occurrence.Event.Facts) {
                Console.WriteLine($"{fact.At} | {fact.Kind} | {fact.ResourceId ?? fact.ActorId ?? "world"}");
            }
            if (options.Trace) { Console.WriteLine(CycleDiagnostics.Render(kernel.World, metrics)); }
        }

        string snapshot = CycleDiagnostics.Render(kernel.World);
        Console.WriteLine();
        Console.WriteLine(CycleDiagnostics.Render(kernel.World, metrics));
        Console.WriteLine($"本次转换：{completed}；停止原因：{(completed == options.Steps ? "步数上限" : status.ToString())}；累计转换：{kernel.Version.TransitionCount}");
        Console.WriteLine($"状态摘要 SHA256：{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot)))}");
        if (options.Snapshot is { } snapshotPath) {
            string fullPath = Path.GetFullPath(snapshotPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, snapshot, new UTF8Encoding(false), cancellation.Token);
        }
    } finally {
        Console.CancelKeyPress -= cancelHandler;
    }
    return 0;
} catch (OperationCanceledException) {
    Console.Error.WriteLine(hasStore
        ? "已停止。已完成的进度已保存，使用同一 --store 路径可继续。"
        : "已停止。内存中的进度未保存。");
    return 130;
} catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or InvalidDataException or UnauthorizedAccessException or OverflowException) {
    Console.Error.WriteLine($"ResourceCycle: {error.Message}");
    return 1;
}

static CycleDurableHistory CreateStore(string path) {
    var initial = CycleGameFactory.CreateBatch();
    return CycleDurableHistory.Create(path, initial.World, initial.Cursor);
}

internal sealed record Options(string? Store, ModelTime Until, int Steps, bool FullRecompute,
    bool Trace, string? Snapshot, bool Help, bool Play, bool Survival, LlmHostOptions? Llm = null, bool Duo = false) {
    public static Options Parse(string[] args) {
        string? store = null;
        string? snapshot = null;
        long hour = 88;
        int steps = 10_000;
        bool full = false, trace = false, help = false, play = false, survival = false;
        bool llm = false, duo = false;
        string? model = null, authFile = null, catalog = null, connection = null, llmLog = null;
        int maxCalls = 128, maxFailures = 3, maxContextCharacters = 200_000;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < args.Length; index++) {
            string option = args[index];
            if (!seen.Add(option)) { throw new ArgumentException($"重复参数：{option}"); }
            string Value() => ++index < args.Length && !string.IsNullOrWhiteSpace(args[index])
                ? args[index] : throw new ArgumentException($"参数缺少值：{option}");
            switch (option) {
                case "--store": store = Path.GetFullPath(Value()); break;
                case "--snapshot": snapshot = Path.GetFullPath(Value()); break;
                case "--until-hour":
                    if (!long.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out hour)) {
                        throw new ArgumentException("--until-hour 必须为非负整数。");
                    }
                    break;
                case "--steps":
                    if (!int.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out steps) || steps < 1) {
                        throw new ArgumentException("--steps 必须为正整数。");
                    }
                    break;
                case "--full-recompute": full = true; break;
                case "--trace": trace = true; break;
                case "--help": help = true; break;
                case "--play": play = true; break;
                case "--survival": survival = true; break;
                case "--duo": duo = true; break;
                case "--llm": llm = true; break;
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
        if (!help && (play && (survival || duo) || survival && duo)) { throw new ArgumentException("--play、--survival、--duo 请选择一种。"); }
        if (!help && llm && !survival && !duo) { throw new ArgumentException("--llm 必须配合 --survival 或 --duo。"); }
        if (!help && !llm && seen.Any(option => option is "--model" or "--auth-file" or "--catalog" or "--connection" or
            "--max-calls" or "--max-failures" or "--max-context-chars" or "--llm-log")) {
            throw new ArgumentException("模型配置和预算参数必须配合 --llm。");
        }
        if (!help && ((catalog is null) != (connection is null))) { throw new ArgumentException("--catalog 与 --connection 必须一起指定。"); }
        if (!help && catalog is not null && authFile is not null) { throw new ArgumentException("--auth-file 仅用于内置 Codex 连接；目录连接使用 Completion 的认证配置。"); }
        if (!help && (play || survival || duo) && seen.Any(option => option is "--until-hour" or "--steps" or "--full-recompute" or "--trace" or "--snapshot")) {
            throw new ArgumentException("批处理参数不适用于 --play / --survival / --duo 交互游戏。");
        }
        if (!help && hour < 16) { throw new ArgumentException("--until-hour 必须不早于初始时刻 16。"); }
        return new(store, new ModelTime(checked(hour * 3_600_000)), steps, full, trace, snapshot, help, play, survival,
            llm ? new(model, authFile, catalog, connection, maxCalls, maxFailures, maxContextCharacters, llmLog) : null, duo);
    }
    private static int Positive(string text, string option) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value > 0
            ? value : throw new ArgumentException($"{option} 必须为正整数。");
}
