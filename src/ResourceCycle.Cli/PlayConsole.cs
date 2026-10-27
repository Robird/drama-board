using System.Globalization;
using System.Text;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.ResourceCycle;

/// <summary>One asynchronous owner drives the Kernel; Console input never opens another writer.</summary>
internal static class PlayConsole {
    public static async Task<int> RunAsync(string? store, CancellationToken cancellationToken) {
        using CycleDurableHistory? durable = store is null ? null : Directory.Exists(store)
            ? CycleDurableHistory.Open(store) : CreateStore(store);
        IOccurrenceHistory<CycleWorld, CycleFact> history;
        if (durable is not null) {
            history = durable;
        } else {
            CycleWorld initial = CycleWorld.CreatePlayable();
            history = new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial, InitialCursor(initial));
        }
        if (history.State.Character is null) {
            throw new ArgumentException("这是批处理实验存档，不能用 --play 打开。请省略 --play 续跑，或选择新目录开始采集。");
        }

        var kernel = CycleEngine.CreateKernel(history, decisionSource: new ConsoleDecisionSource());
        Console.WriteLine(store is null
            ? "采集模式 · 本次仅在内存中运行；退出后不保留进度。"
            : $"采集模式 · 存档：{store} · 每次成功转换自动保存。");
        if (kernel.RecoverPending(cancellationToken)) {
            Console.WriteLine("已完成上次中断时保存的行动：");
            PrintCompletion(kernel.LastCompletion!);
        }
        if (kernel.World.Character!.WaitUntil is not null) {
            Console.WriteLine(Render(kernel.World));
            Console.WriteLine("正在继续已安排的等待；完成后再请求行动。");
        }

        try {
            while (true) {
                StepStatus status = await kernel.StepAsync(new ModelTime(long.MaxValue), cancellationToken);
                if (status != StepStatus.Committed) {
                    throw new InvalidOperationException($"采集世界未能产生下一步行动：{status}。");
                }
                PrintCompletion(kernel.LastCompletion!);
            }
        } catch (PlayEndedException) {
            Console.WriteLine(store is null ? "已退出。内存中的采集进度未保存。" : "已退出。已完成的行动均已保存，可使用同一 --play --store 路径继续。");
            return 0;
        }
    }

    public static void RequireBatchWorld(CycleWorld world) {
        if (world.Character is not null) {
            throw new ArgumentException("这是交互采集存档；请使用 --play --store 打开。批处理不会推进此存档。");
        }
    }

    private static KernelCursor InitialCursor(CycleWorld world) => new(new WorldVersion(1, 0), world.Time, null, null);

    private static CycleDurableHistory CreateStore(string path) {
        CycleWorld initial = CycleWorld.CreatePlayable();
        return CycleDurableHistory.Create(path, initial, InitialCursor(initial));
    }

    private static void PrintCompletion(OccurrenceCompletion<CycleFact> completion) {
        Console.WriteLine();
        Console.WriteLine("## 刚刚发生");
        foreach (CycleFact fact in completion.Event.Facts) {
            string text = fact.Kind switch {
                CycleEventKind.PlantMatured => $"{PlantName(fact.TargetId)}成熟了，可以采集。",
                CycleEventKind.LightChanged => fact.Light!.IsDay
                    ? "天亮了。未成熟的太阳花继续生长，未成熟的月光草暂停生长并保留进度。"
                    : "天黑了。未成熟的月光草继续生长，未成熟的太阳花暂停生长并保留进度。",
                CycleEventKind.PlantDestroyed => $"{PlantName(fact.TargetId)}被销毁，生长进度归零。",
                CycleEventKind.WaitStarted or CycleEventKind.WaitCompleted or CycleEventKind.PlantGathered
                    => fact.Character!.LastResult ?? throw new InvalidOperationException("角色行动缺少结果文本。"),
                _ => throw new InvalidOperationException($"尚未支持的采集事件：{fact.Kind}。"),
            };
            Console.WriteLine($"- {FormatTime(fact.At)}：{text}");
        }
    }

    private static string Render(CycleWorld world) {
        var character = world.Character ?? throw new InvalidOperationException("采集画面需要角色。");
        var text = new StringBuilder();
        text.AppendLine("# 林间苗圃");
        text.AppendLine();
        text.AppendLine("这里有一片太阳花和一片月光草。成熟后可采集，每次获得一份，并重新开始生长。");
        text.AppendLine();
        text.AppendLine("## 时间与角色");
        text.AppendLine($"- 当前时间：{FormatTime(world.Time)}，{(world.Light.IsDay ? "白天" : "夜晚")}。");
        text.AppendLine($"- 下次{(world.Light.IsDay ? "天黑" : "天亮")}：{FormatTime(world.Light.NextChange)}。");
        text.AppendLine($"- 当前角色：园丁（`{character.Id}`）。");
        text.AppendLine(character.WaitUntil is { } until
            ? $"- 行动：等待至{FormatTime(until)}；期间会显示自然变化。"
            : "- 行动：轮到你决定。等待输入期间，世界时间暂停。");
        if (character.LastResult is { } result) { text.AppendLine($"- 上次行动：{result}"); }
        text.AppendLine();
        text.AppendLine("## 植物");
        foreach (CyclePlant plant in world.Plants) {
            string phase = plant.GrowsInDaylight ? "白天" : "夜晚";
            string status = plant.IsMature ? "已成熟" : plant.ActiveSince is null ? "暂停生长，进度保留" : "正在生长";
            string progress = Hours(plant.ProgressAt(world.Time));
            text.AppendLine($"- {PlantName(plant.Id)}（`{plant.Id}`）：{status}；有效生长进度 {progress}/{Hours(plant.RequiredTicks)} 小时，仅在{phase}生长。");
        }
        text.AppendLine();
        text.AppendLine("## 背包");
        if (character.Backpack.Items.Count == 0) {
            text.AppendLine("- 空。");
        } else {
            foreach (var item in character.Backpack.Items.OrderBy(pair => pair.Key, StringComparer.Ordinal)) {
                text.AppendLine($"- {PlantName(item.Key)}（`{item.Key}`）×{item.Value}。");
            }
        }
        text.AppendLine();
        text.Append(RenderCommands(world));
        return text.ToString();
    }

    private static string RenderCommands(CycleWorld world) {
        string actorId = world.Character!.Id;
        var text = new StringBuilder();
        text.AppendLine("## 可用命令");
        text.AppendLine();
        text.AppendLine("请输入完整命令和标识，区分大小写，不接受编号或缩写。");
        text.AppendLine("- `look`：重新显示完整画面；不推进时间。");
        text.AppendLine("- `help`：显示命令和当前受阻原因；不推进时间。");
        text.AppendLine("- `quit`：退出；不额外推进时间。输入结束（EOF）也会退出。");
        var availableWaits = new List<int>();
        var blockedWaits = new List<(int Hour, string Reason)>();
        for (int hour = 1; hour <= 24; hour++) {
            string? reason = CycleActions.Check(world, actorId, CycleCommand.Wait(hour));
            if (reason is null) { availableWaits.Add(hour); }
            else { blockedWaits.Add((hour, reason)); }
        }
        if (availableWaits.Count == 24) {
            text.AppendLine("- `wait <hours>`：可执行；hours 为 1–24 的整数，例如 `wait 6`。等待指定小时数后再次行动；期间自然事件照常发生。");
        } else {
            text.AppendLine($"- `wait <hours>`：hours 为 1–24 的整数；当前可执行小时数：{(availableWaits.Count == 0 ? "无" : string.Join(", ", availableWaits))}。");
            foreach (var group in blockedWaits.GroupBy(entry => entry.Reason)) {
                text.AppendLine($"  - 受阻小时数 {string.Join(", ", group.Select(entry => entry.Hour))}：{group.Key}");
            }
        }
        foreach (CyclePlant plant in world.Plants) {
            string? reason = CycleActions.Check(world, actorId, CycleCommand.Gather(plant.Id));
            text.AppendLine($"- `gather {plant.Id}`：采集{PlantName(plant.Id)}；{(reason is null ? "可执行。" : $"受阻：{reason}")}");
        }
        return text.ToString();
    }

    private static string PlantName(string id) => id switch {
        "sunflower" => "太阳花",
        "moongrass" => "月光草",
        _ => id,
    };

    private static string Hours(long ticks) => ((decimal)ticks / CycleTime.Hour).ToString("0.##", CultureInfo.InvariantCulture);
    private static string FormatTime(ModelTime time) => $"第 {time.Ticks / CycleTime.Day} 天 {time.Ticks % CycleTime.Day / CycleTime.Hour:00}:{time.Ticks % CycleTime.Hour / 60_000:00}";

    private sealed class PlayEndedException : Exception { }

    private sealed class ConsoleDecisionSource : ICycleDecisionSource {
        public async ValueTask<CycleCommand> ChooseAsync(CycleWorld world, string actorId, CancellationToken cancellationToken) {
            Console.WriteLine();
            Console.WriteLine(Render(world));
            while (true) {
                cancellationToken.ThrowIfCancellationRequested();
                Console.Write("> ");
                string? input = await Console.In.ReadLineAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (input is null) { throw new PlayEndedException(); }
                string[] parts = input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts is ["quit"]) { throw new PlayEndedException(); }
                if (parts is ["look"]) { Console.WriteLine(Render(world)); continue; }
                if (parts is ["help"]) { Console.WriteLine(RenderCommands(world)); continue; }
                CycleCommand command;
                if (parts is ["wait", var hours] && int.TryParse(hours, NumberStyles.None, CultureInfo.InvariantCulture, out int count)) {
                    command = CycleCommand.Wait(count);
                } else if (parts is ["gather", var plantId]) {
                    command = CycleCommand.Gather(plantId);
                } else {
                    Console.WriteLine("无法识别命令。请输入 look、help、quit、wait <1–24 整数小时> 或 gather sunflower|moongrass。时间未推进。");
                    continue;
                }
                string? reason = CycleActions.Check(world, actorId, command);
                if (reason is not null) {
                    Console.WriteLine($"无法行动：{reason} 时间未推进。");
                    continue;
                }
                return command;
            }
        }
    }
}
