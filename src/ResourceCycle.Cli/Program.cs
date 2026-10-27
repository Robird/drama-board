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
        if (options.Play) {
            return await PlayConsole.RunAsync(options.Store, cancellation.Token);
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
} catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or OverflowException) {
    Console.Error.WriteLine($"ResourceCycle: {error.Message}");
    return 1;
}

static CycleDurableHistory CreateStore(string path) {
    var initial = CycleGameFactory.CreateBatch();
    return CycleDurableHistory.Create(path, initial.World, initial.Cursor);
}

internal sealed record Options(string? Store, ModelTime Until, int Steps, bool FullRecompute,
    bool Trace, string? Snapshot, bool Help, bool Play) {
    public static Options Parse(string[] args) {
        string? store = null;
        string? snapshot = null;
        long hour = 88;
        int steps = 10_000;
        bool full = false, trace = false, help = false, play = false;
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
                default: throw new ArgumentException($"未知参数：{option}；使用 --help 查看用法。");
            }
        }
        if (!help && play && seen.Any(option => option is "--until-hour" or "--steps" or "--full-recompute" or "--trace" or "--snapshot")) {
            throw new ArgumentException("--play 仅能配合 --store；批处理参数不适用于交互采集。");
        }
        if (!help && hour < 16) { throw new ArgumentException("--until-hour 必须不早于初始时刻 16。"); }
        return new(store, new ModelTime(checked(hour * 3_600_000)), steps, full, trace, snapshot, help, play);
    }
}
