using System.Globalization;
using System.Text;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.ResourceCycle;

/// <summary>One asynchronous owner drives the Kernel; input and rendering never mutate the world.</summary>
internal static class PlayConsole {
    public static async Task<int> RunAsync(string? store, CancellationToken cancellationToken) {
        using CycleDurableHistory? durable = store is null ? null : Directory.Exists(store)
            ? CycleDurableHistory.Open(store) : CreateStore(store);
        IOccurrenceHistory<CycleWorld, CycleFact> history;
        if (durable is not null) {
            history = durable;
        } else {
            var initial = CycleGameFactory.CreatePlayable();
            history = new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor);
        }
        if (history.State.Characters.Count == 0) {
            throw new ArgumentException("这是批处理实验存档，不能用 --play 打开。请省略 --play 续跑，或选择新目录开始采集。");
        }

        var kernel = CycleEngine.CreateKernel(history, characterController: new ConsoleCharacterController());
        Console.WriteLine(store is null
            ? "多人采集 · 本次仅在内存中运行；退出后不保留进度。"
            : $"多人采集 · 存档：{store} · 每次成功转换自动保存。");
        Console.WriteLine("共用控制台轮流扮演角色；画面只显示当前角色的技能、活动细节和背包。等待输入期间世界暂停。");
        if (kernel.RecoverPending(cancellationToken)) {
            Console.WriteLine("已完成上次中断时保存的行动；接下来按已保存的活动与请求继续。");
            PrintPublicChanges(kernel.LastCompletion!);
        }

        try {
            while (true) {
                StepStatus status = await kernel.StepAsync(new ModelTime(long.MaxValue), cancellationToken);
                if (status != StepStatus.Committed) {
                    throw new InvalidOperationException($"采集世界未能产生下一步行动：{status}。");
                }
                PrintPublicChanges(kernel.LastCompletion!);
            }
        } catch (PlayEndedException) {
            Console.WriteLine(store is null ? "已退出。内存中的采集进度未保存。" : "已退出。已完成的行动均已保存，可使用同一 --play --store 路径继续。");
            return 0;
        }
    }

    public static void RequireBatchWorld(CycleWorld world) {
        if (world.Characters.Count != 0) {
            throw new ArgumentException("这是交互采集存档；请使用 --play --store 打开。批处理不会推进此存档。");
        }
    }

    private static CycleDurableHistory CreateStore(string path) {
        var initial = CycleGameFactory.CreatePlayable();
        return CycleDurableHistory.Create(path, initial.World, initial.Cursor);
    }

    // Actor-specific results belong to that actor's observation. Do not print their private
    // activity schedule, skill or inventory into another actor's intervening console view.
    private static void PrintPublicChanges(OccurrenceCompletion<CycleFact> completion) {
        foreach (CycleFact fact in completion.Event.Facts) {
            if (fact.Kind == CycleEventKind.LightChanged) {
                Console.WriteLine($"\n世界变化 · {FormatTime(fact.At)}：{(fact.Light!.IsDay ? "天亮了；太阳花满足再生光照条件，月光草暂停再生。" : "天黑了；月光草满足再生光照条件，太阳花暂停再生。")}");
            }
        }
    }

    private static string Render(CycleWorld world, string actorId) {
        CycleCharacter character = world.GetCharacter(actorId);
        var text = new StringBuilder();
        text.AppendLine($"# 林间苗圃 · {character.Name}（{character.Id}） · {FormatTime(world.Time)}");
        text.AppendLine();
        text.AppendLine($"当前控制角色：{character.Name}（`{character.Id}`）。等待输入期间世界暂停；其他人的活动不会因你查看信息而重新计时。");
        text.AppendLine($"{(world.Light.IsDay ? "白天" : "夜晚")}；下次{(world.Light.IsDay ? "天黑" : "天亮")}：{FormatTime(world.Light.NextChange)}。");
        text.AppendLine();
        text.AppendLine("## 本次介入");
        if (character.Notices.Count == 0) {
            text.AppendLine("- 当前空闲，请选择下一活动。");
        } else {
            foreach (CycleNotice notice in character.Notices) {
                text.AppendLine($"- {RenderNotice(world, notice)}");
            }
            text.AppendLine(character.Activity is { } ongoing
                ? $"- 你的活动没有中断；当前周期仍于 {FormatTime(ongoing.Due)} 结算。"
                : "- 你的活动已结束；以下感知和行动结果一起呈现，请选择下一活动。");
        }
        if (character.LastResult is { } result) { text.AppendLine($"- 上次结果（记录当时状态）：{result}"); }
        text.AppendLine();
        text.AppendLine("## 你的活动");
        text.AppendLine($"- 采药等级：{character.SkillLevel}；采集速度：基础的 {100m + 25m * character.SkillLevel:0}%。");
        if (character.Activity is not { } activity) {
            text.AppendLine("- 空闲。可以启动采集或等待；每次合法选择后交给下一个有请求的角色。");
        } else if (activity.Kind == CycleActivityKind.Wait) {
            text.AppendLine($"- 正在等待，完成时刻：{FormatTime(activity.Due)}。");
        } else {
            CycleResource resource = world.GetResource(activity.ResourceId!);
            text.AppendLine($"- 采集 {resource.Name}（`{resource.Id}`）；{(activity.SelectedAll ? "开始时选择 all，目标已固定" : "固定目标")}为 {activity.TargetCount} 份；已取得 {activity.CompletedCount}/{activity.TargetCount}。");
            text.AppendLine($"- 当前周期：{FormatTime(activity.StartedAt)} → {FormatTime(activity.Due)}；每份 {Minutes(activity.DurationTicks)} 分钟，剩余 {Minutes(activity.Due.Ticks - world.Time.Ticks)} 分钟。");
            text.AppendLine("- 成功后未达目标会安排下一周期，即使此刻库存为零。库存变零会先提醒；确认 continue 后才继续推进，已安排的周期保持不变。结算时无货则整批结束，最多损失一个无收获周期，不会自动等待再生。");
            text.AppendLine("- continue 只确认消息；cancel 保留已取得物品，放弃当前尚未完成的一份。");
        }
        text.AppendLine();
        text.AppendLine("## 资源点");
        text.AppendLine("资源不预留，先完成先得；同刻完成按固定规则仲裁。别人只公开正在采集哪个点，不公开技能、速度或下一份完成时刻。");
        text.AppendLine();
        text.AppendLine("| 资源点 | 余量 / 容量 | 再生 | 产出及你的采集周期 | 正在采集的其他人 |");
        text.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (CycleResource resource in world.Resources) {
            var others = world.Characters.Where(other => other.Id != actorId &&
                other.Activity is { Kind: CycleActivityKind.Gather } gathering && gathering.ResourceId == resource.Id).ToArray();
            string members = others.Length == 0 ? "无" : $"{string.Join("、", others.Select(other => $"{other.Name}（{other.Id}）"))}，共 {others.Length} 人";
            string phase = resource.Stock == resource.Capacity ? "已满，未囤积再生进度" :
                $"{(resource.ActiveSince is null ? "暂停" : "进行中")}，进度 {Minutes(resource.ProgressAt(world.Time))}/{Minutes(resource.RegenerationTicks)} 分钟";
            long duration = CycleActions.HarvestDuration(resource, character);
            text.AppendLine($"| {resource.Name} `{resource.Id}` | {resource.Stock} / {resource.Capacity} | {(resource.GrowsInDaylight ? "白天" : "夜晚")}每 {Hours(resource.RegenerationTicks)} 小时补 1；{phase} | 每份产出 {ItemName(world, resource.ItemId)} ×1；你需 {Minutes(duration)} 分钟；要求采药等级 {resource.RequiredLevel} | {members} |");
        }
        text.AppendLine();
        text.AppendLine("## 其他角色");
        foreach (CycleCharacter other in world.Characters.Where(other => other.Id != actorId)) {
            string status = other.Activity switch {
                { Kind: CycleActivityKind.Gather, ResourceId: { } resourceId } => $"正在采集 {world.GetResource(resourceId).Name}",
                { Kind: CycleActivityKind.Wait } => "正在等待",
                _ => "空闲",
            };
            text.AppendLine($"- {other.Name}（`{other.Id}`）：{status}。");
        }
        text.AppendLine();
        text.AppendLine("## 你的背包");
        if (character.Backpack.Items.Count == 0) {
            text.AppendLine("- 空。");
        } else {
            foreach (var item in character.Backpack.Items.OrderBy(pair => pair.Key, StringComparer.Ordinal)) {
                text.AppendLine($"- {ItemName(world, item.Key)}（`{item.Key}`）×{item.Value}。");
            }
        }
        text.AppendLine();
        text.Append(RenderCommands(world, actorId));
        return text.ToString();
    }

    private static string RenderNotice(CycleWorld world, CycleNotice notice) {
        CycleResource resource = world.GetResource(notice.ResourceId);
        CycleCharacter? actor = world.Characters.SingleOrDefault(candidate => candidate.Id == notice.ActorId);
        string who = actor is null ? "环境或未知原因" : $"{actor.Name}（{actor.Id}）";
        string repeated = notice.Count > 1 ? $"；待回应同类变化共 {notice.Count} 次，此处为最近一次" : "";
        if (notice.Kind == CycleNoticeKind.CollectorJoined) {
            bool stillGathering = actor?.Activity is { Kind: CycleActivityKind.Gather } gathering && gathering.ResourceId == notice.ResourceId;
            return $"{FormatTime(notice.At)}：{who}曾加入 {resource.Name} 的采集，{(stillGathering ? "目前仍在此点采集" : "目前已离开此点的采集")}{repeated}。";
        }
        string reason = notice.Reason.TrimEnd().TrimEnd('。', '.', '！', '!', '？', '?', '；', ';');
        return $"{FormatTime(notice.At)}：{resource.Name}曾被取空；相关行动者：{who}；原因：{reason}。当前余量 {resource.Stock}/{resource.Capacity}{repeated}。消息描述已发生的耗尽，不会取消你的在途周期。";
    }

    private static string RenderCommands(CycleWorld world, string actorId) {
        var text = new StringBuilder();
        text.AppendLine("## 可用命令");
        text.AppendLine();
        text.AppendLine("输入完整命令和标识，区分大小写。查询、非法输入和退出不确认消息、不消耗时间，也不交棒。");
        text.AppendLine("- `look`：重新显示完整画面。`help`：显示命令与当前受阻原因。");
        text.AppendLine("- `quit`：退出；EOF 同样退出。已提交的活动和未确认消息可随存档恢复。");
        AppendCommand(text, world, actorId, "continue", CycleCommand.Continue(), "确认感知并交还控制权，保留活动和进度");
        AppendCommand(text, world, actorId, "cancel", CycleCommand.Cancel(), "取消当前活动，保留已经获得的物品；随后按轮转选择新活动");
        text.AppendLine("- `gather <resource-id> <正整数|all>`：数量 1–2147483647；all 固定为接受命令时的库存，不随再生增加、不预留。新开时必须有货，不能同时安排另一耗时活动。");
        foreach (CycleResource resource in world.Resources) {
            AppendCommand(text, world, actorId, $"gather {resource.Id} 1", CycleCommand.Gather(resource.Id, 1), $"最多取得 1 份{resource.Name}");
            AppendCommand(text, world, actorId, $"gather {resource.Id} all", CycleCommand.GatherAll(resource.Id), $"当前 all = {resource.Stock} 份；也可改为任意受支持的正整数 n，不要求库存已有 n 份");
        }
        var waits = Enumerable.Range(1, 24).Select(hour => (Hour: hour, Reason: CycleActions.Check(world, actorId, CycleCommand.Wait(hour)))).ToArray();
        int[] allowed = waits.Where(entry => entry.Reason is null).Select(entry => entry.Hour).ToArray();
        text.AppendLine($"- `wait <hours>`：hours 为 1–24 的整数；{(allowed.Length == 24 ? "全部可执行" : $"当前可执行：{(allowed.Length == 0 ? "无" : string.Join(", ", allowed))}")}。等待完成后才请求行动。");
        foreach (var group in waits.Where(entry => entry.Reason is not null).GroupBy(entry => entry.Reason)) {
            string hours = group.Count() == 24 ? "1–24" : string.Join(", ", group.Select(entry => entry.Hour));
            text.AppendLine($"  - 小时数 {hours} 受阻：{group.Key}");
        }
        return text.ToString();
    }

    private static void AppendCommand(StringBuilder text, CycleWorld world, string actorId, string syntax, CycleCommand command, string description) {
        string? reason = CycleActions.Check(world, actorId, command);
        text.AppendLine($"- `{syntax}`：{description}；{(reason is null ? "可执行。" : $"受阻：{reason}")}");
    }

    private static string ItemName(CycleWorld world, string itemId) => world.Resources.FirstOrDefault(resource => resource.ItemId == itemId)?.Name ?? itemId;
    private static string Hours(long ticks) => ((decimal)ticks / CycleTime.Hour).ToString("0.##", CultureInfo.InvariantCulture);
    private static string Minutes(long ticks) => ((decimal)ticks / 60_000).ToString("0.##", CultureInfo.InvariantCulture);
    private static string FormatTime(ModelTime time) {
        string minutes = $"第 {time.Ticks / CycleTime.Day} 天 {time.Ticks % CycleTime.Day / CycleTime.Hour:00}:{time.Ticks % CycleTime.Hour / 60_000:00}";
        long remainder = time.Ticks % 60_000;
        return remainder == 0 ? minutes : $"{minutes}:{remainder / 1_000:00}.{remainder % 1_000:000}";
    }

    private sealed class PlayEndedException : Exception { }

    // Shared across characters; each invocation owns its input loop and renders the requested actor's view.
    private sealed class ConsoleCharacterController : ICycleCharacterController {
        public async ValueTask<CycleCommand> ChooseCommandAsync(CycleWorld world, string actorId, CancellationToken cancellationToken) {
            Console.WriteLine();
            Console.WriteLine(Render(world, actorId));
            while (true) {
                cancellationToken.ThrowIfCancellationRequested();
                Console.Write($"{actorId}> ");
                string? input = await Console.In.ReadLineAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (input is null) { throw new PlayEndedException(); }
                string[] parts = input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts is ["quit"]) { throw new PlayEndedException(); }
                if (parts is ["look"]) { Console.WriteLine(Render(world, actorId)); continue; }
                if (parts is ["help"]) { Console.WriteLine(RenderCommands(world, actorId)); continue; }
                CycleCommand command;
                if (parts is ["wait", var hours] && int.TryParse(hours, NumberStyles.None, CultureInfo.InvariantCulture, out int waitHours)) {
                    command = CycleCommand.Wait(waitHours);
                } else if (parts is ["gather", var allResource, "all"]) {
                    command = CycleCommand.GatherAll(allResource);
                } else if (parts is ["gather", var resourceId, var quantity] && int.TryParse(quantity, NumberStyles.None, CultureInfo.InvariantCulture, out int count)) {
                    command = CycleCommand.Gather(resourceId, count);
                } else if (parts is ["continue"]) {
                    command = CycleCommand.Continue();
                } else if (parts is ["cancel"]) {
                    command = CycleCommand.Cancel();
                } else {
                    Console.WriteLine("无法识别命令。请输入 look、help、quit、continue、cancel、wait <1–24 整数小时> 或 gather <sunflower|moongrass> <正整数|all>。时间未推进。");
                    continue;
                }
                string? reason = CycleActions.Check(world, actorId, command);
                if (reason is not null) {
                    Console.WriteLine($"无法行动：{reason} 时间未推进；未确认消息。");
                    continue;
                }
                return command;
            }
        }
    }
}
