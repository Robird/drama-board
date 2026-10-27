using System.Globalization;
using System.Text;
using DramaBoard.Kernel.Time;
using DramaBoard.ResourceCycle;

/// <summary>Pure Markdown projection for one character; does not acknowledge notices or mutate state.</summary>
/// <remarks>Only the game-produced observation is available here; rendering cannot query hidden world state.</remarks>
internal static class PlayView {
    /// <summary>Only public world changes belong between actor-specific observations.</summary>
    public static string? RenderPublicChange(CycleFact fact, bool expedition = false) => fact.Kind == CycleEventKind.LightChanged
        ? expedition ? $"\n世界变化 · {FormatTime(fact.At)}：{(fact.Light!.IsDay ? "天亮了。" : "天黑了。")}" : $"\n世界变化 · {FormatTime(fact.At)}：{(fact.Light!.IsDay ? "天亮了；太阳花满足再生光照条件，月光草暂停再生。" : "天黑了；月光草满足再生光照条件，太阳花暂停再生。")}" : null;

    public static string Render(CycleObservation observation) {
        CycleActorObservation character = observation.Actor;
        bool ended = observation.Outcome != CycleExpeditionOutcome.Exploring;
        var text = new StringBuilder();
        text.AppendLine($"# {(observation.IsExpedition ? "山林求生" : "林间苗圃")} · {character.Name}（{character.Id}） · {FormatTime(observation.Time)}");
        text.AppendLine();
        text.AppendLine(ended ? "旅程已经结束；以下为结束时的冻结记录，世界不再推进。" : observation.IsExpedition
            ? $"当前控制角色：{character.Name}（`{character.Id}`）。等待输入期间世界暂停；查看和帮助不消耗时间。"
            : $"当前控制角色：{character.Name}（`{character.Id}`）。等待输入期间世界暂停；其他人的活动不会因你查看信息而重新计时。");
        text.AppendLine(ended ? $"结束时为{(observation.IsDay ? "白天" : "夜晚")}。" :
            $"{(observation.IsDay ? "白天" : "夜晚")}；下次{(observation.IsDay ? "天黑" : "天亮")}：{FormatTime(observation.NextLightChange)}。");
        text.AppendLine();
        if (observation.Survival is { } survival) { RenderSurvival(text, observation.Time, survival, ended); }
        if (observation.IsExpedition) {
            text.AppendLine(ended ? "## 结束时的位置" : "## 当前位置");
            text.AppendLine(observation.ArrivalDue is { } arrival
                ? ended ? $"结束时仍在途中；原计划于 {FormatTime(arrival)} 到达，本段行程未完成，不再推进。"
                    : $"正在途中；预计 {FormatTime(arrival)} 到达。移动途中不可 cancel；查看或继续不会重置行程。"
                : $"{observation.PlaceName}：{observation.PlaceDescription}");
            text.AppendLine();
            if (observation.Memories.Count > 0) {
                text.AppendLine("## 曾到访地点");
                text.AppendLine("以下均为离开前的历史观察，远处余量可能已改变，不表示当前事实。");
                foreach (CyclePlaceMemory memory in observation.Memories) {
                    string stock = memory.Resources.Count == 0 ? "当时未见可采资源" : string.Join("；", memory.Resources.Select(resource => $"{resource.Name} {resource.Stock}/{resource.Capacity}"));
                    text.AppendLine($"- {memory.Name}（{FormatTime(memory.At)}）：{stock}。");
                }
                text.AppendLine();
            }
        }
        text.AppendLine("## 本次介入");
        if (character.Notices.Count == 0) {
            text.AppendLine(observation.Outcome != CycleExpeditionOutcome.Exploring ? "- 本次旅程已结束。" :
                observation.ArrivalDue is not null ? "- 行程进行中。" :
                character.Activity is { } active ? $"- 原活动进行中，当前周期仍于 {FormatTime(active.Due)} 结算。" :
                "- 当前空闲，请选择下一活动。");
        } else {
            foreach (CycleNoticeObservation notice in observation.Notices) {
                text.AppendLine($"- {RenderNotice(notice, ended)}");
            }
            text.AppendLine(observation.Outcome != CycleExpeditionOutcome.Exploring ? "- 本次旅程已结束；以下是结束前的历史感知。" : character.Activity is { } ongoing
                ? $"- 你的活动没有中断；当前周期仍于 {FormatTime(ongoing.Due)} 结算。"
                : "- 你的活动已结束；以下感知和行动结果一起呈现，请选择下一活动。");
        }
        if (observation.Survival is { PendingWarnings.Count: > 0 } needs) {
            text.AppendLine(ended ? "- 生存警告（结束时尚未确认的历史记录）：" : "- 生存警告（历史记录，待确认）：");
            foreach (string warning in needs.PendingWarnings) { text.AppendLine($"  - {warning}"); }
            text.AppendLine("- 这些警告记录已经发生的危险；即使已经补给也会保留，当前余量以“身体状态”为准。" +
                (observation.Outcome == CycleExpeditionOutcome.Exploring
                    ? "可以先 use 补给，再 continue 确认；原活动和行程期限保持不变。" : "旅程已经结束，无需再确认。"));
        }
        if (character.LastResult is { } result) { text.AppendLine($"- 上次结果（记录当时状态）：{result}"); }
        text.AppendLine();
        text.AppendLine(ended ? "## 结束时的活动记录" : "## 你的活动");
        text.AppendLine($"- 采药等级：{character.SkillLevel}；采集速度：基础的 {100m + 25m * character.SkillLevel:0}%。");
        if (ended) {
            if (observation.ArrivalDue is { } unfinishedArrival) {
                text.AppendLine($"- 移动未完成；原计划到达时刻：{FormatTime(unfinishedArrival)}。此记录已冻结。");
            } else if (character.Activity is { } unfinished) {
                string description = unfinished.Kind == CycleActivityKind.Wait ? "等待未完成" :
                    $"采集未完成：{unfinished.ResourceId}，已取得 {unfinished.CompletedCount}/{unfinished.TargetCount} 份";
                text.AppendLine($"- {description}；原周期截止时刻：{FormatTime(unfinished.Due)}。此记录已冻结，不再结算。");
            } else {
                text.AppendLine("- 结束时没有未完成的活动。");
            }
        } else if (observation.ArrivalDue is { } due) {
            text.AppendLine($"- 正在移动，预计 {FormatTime(due)} 到达；当前不可采集、等待或取消移动。");
        } else if (character.Activity is not { } activity) {
            text.AppendLine(observation.Outcome != CycleExpeditionOutcome.Exploring ? "- 无待执行的活动。" :
                observation.IsExpedition ? "- 空闲。可以沿可见通路移动、采集当前位置的资源，或等待。" :
                    "- 空闲。可以启动采集或等待；每次合法选择后交给下一个有请求的角色。");
        } else if (activity.Kind == CycleActivityKind.Wait) {
            text.AppendLine($"- 正在等待，完成时刻：{FormatTime(activity.Due)}。");
        } else {
            CycleResource resource = observation.Resources.Single(resource => resource.Id == activity.ResourceId);
            text.AppendLine($"- 采集 {resource.Name}（`{resource.Id}`）；{(activity.SelectedAll ? "开始时选择 all，目标已固定" : "固定目标")}为 {activity.TargetCount} 份；已取得 {activity.CompletedCount}/{activity.TargetCount}。");
            text.AppendLine($"- 当前周期：{FormatTime(activity.StartedAt)} → {FormatTime(activity.Due)}；每份 {Minutes(activity.DurationTicks)} 分钟，剩余 {Minutes(activity.Due.Ticks - observation.Time.Ticks)} 分钟。");
            text.AppendLine("- 成功后未达目标会安排下一周期，即使此刻库存为零。库存变零会先提醒；确认 continue 后才继续推进，已安排的周期保持不变。结算时无货则整批结束，最多损失一个无收获周期，不会自动等待再生。");
            text.AppendLine("- continue 只确认消息；cancel 保留已取得物品，放弃当前尚未完成的一份。");
        }
        text.AppendLine();
        text.AppendLine("## 资源点");
        if (observation.IsExpedition && observation.Resources.Count == 0) {
            text.AppendLine("当前没有可见的可采资源点。");
        } else {
            text.AppendLine(observation.IsExpedition
                ? "这里只显示当前位置可见的资源；采集按每份周期结算，不预留库存。"
                : "资源不预留，先完成先得；同刻完成按固定规则仲裁。别人只公开正在采集哪个点，不公开技能、速度或下一份完成时刻。");
            text.AppendLine();
            text.AppendLine(observation.IsExpedition
                ? "| 资源点 | 余量 / 容量 | 再生 | 产出及你的采集周期 |"
                : "| 资源点 | 余量 / 容量 | 再生 | 产出及你的采集周期 | 正在采集的其他人 |");
            text.AppendLine(observation.IsExpedition ? "| --- | --- | --- | --- |" : "| --- | --- | --- | --- | --- |");
        }
        foreach (CycleResource resource in observation.Resources) {
            var others = observation.OtherCharacters.Where(other => other.GatheringResourceId == resource.Id).ToArray();
            string members = others.Length == 0 ? "无" : $"{string.Join("、", others.Select(other => $"{other.Name}（{other.Id}）"))}，共 {others.Length} 人";
            string phase = resource.Stock == resource.Capacity ? "已满，未囤积再生进度" :
                $"{(resource.ActiveSince is null ? "暂停" : "进行中")}，进度 {Minutes(resource.ProgressAt(observation.Time))}/{Minutes(resource.RegenerationTicks)} 分钟";
            long duration = observation.HarvestDurations[resource.Id];
            text.AppendLine($"| {resource.Name} `{resource.Id}` | {resource.Stock} / {resource.Capacity} | {(resource.GrowsInDaylight ? "白天" : "夜晚")}每 {Hours(resource.RegenerationTicks)} 小时补 1；{phase} | 每份产出 {ItemName(observation, resource.ItemId)} ×1；你需 {Minutes(duration)} 分钟；要求采药等级 {resource.RequiredLevel} |" +
                (observation.IsExpedition ? "" : $" {members} |"));
        }
        text.AppendLine();
        if (!observation.IsExpedition || observation.OtherCharacters.Count > 0) {
            text.AppendLine("## 其他角色");
            foreach (CycleOtherCharacterObservation other in observation.OtherCharacters) {
                string status = other.GatheringResourceId is { } resourceId
                    ? $"正在采集 {observation.Resources.Single(resource => resource.Id == resourceId).Name}"
                    : other.Waiting ? "正在等待" : "空闲";
                text.AppendLine($"- {other.Name}（`{other.Id}`）：{status}。");
            }
            text.AppendLine();
        }
        text.AppendLine("## 你的背包");
        if (character.Backpack.UnitCapacity is { } capacity) {
            text.AppendLine($"- 容量：{character.Backpack.TotalCount} / {capacity} 件。");
        }
        if (character.Backpack.Items.Count == 0) {
            text.AppendLine("- 空。");
        } else {
            foreach (var item in character.Backpack.Items.OrderBy(pair => pair.Key, StringComparer.Ordinal)) {
                text.AppendLine($"- {ItemName(observation, item.Key)}（`{item.Key}`）×{item.Value}。");
            }
        }
        text.AppendLine();
        if (observation.Outcome != CycleExpeditionOutcome.Exploring) {
            text.AppendLine("## 结局");
            text.AppendLine(observation.Outcome == CycleExpeditionOutcome.Escaped ? "获救。" : "旅程结束。只是这一次未能走出山林。");
            text.AppendLine(observation.EndingReason);
            text.AppendLine("本局已经结束，不再推进普通活动。");
        } else {
            text.Append(RenderCommands(observation));
        }
        return text.ToString();
    }

    private static string RenderNotice(CycleNoticeObservation perceived, bool ended) {
        CycleNotice notice = perceived.Notice;
        string repeated = notice.Count > 1 ? $"；待回应同类变化共 {notice.Count} 次，此处为最近一次" : "";
        if (notice.Kind == CycleNoticeKind.CollectorJoined) {
            string current = perceived.StillGathering switch {
                true => "目前仍在此点采集", false => "目前已离开此点的采集", _ => "当前情况不在视野内"
            };
            return $"{FormatTime(notice.At)}：{perceived.ActorLabel}曾加入 {perceived.ResourceName} 的采集，{current}{repeated}。";
        }
        string reason = notice.Reason.TrimEnd().TrimEnd('。', '.', '！', '!', '？', '?', '；', ';');
        string stock = perceived.CurrentStock is { } currentStock ? $"当前余量 {currentStock}/{perceived.Capacity}" : "当前余量不在视野内";
        return $"{FormatTime(notice.At)}：{perceived.ResourceName}曾被取空；相关行动者：{perceived.ActorLabel}；原因：{reason}。{stock}{repeated}。" +
            (ended ? "这是结束前的耗尽记录。" : "消息描述已发生的耗尽，不会取消你的在途周期。");
    }

    public static string RenderCommands(CycleObservation observation) {
        var text = new StringBuilder();
        text.AppendLine("## 可用命令");
        text.AppendLine();
        text.AppendLine(observation.IsExpedition
            ? "输入完整命令和标识，区分大小写。查询、非法输入和退出不确认消息、不消耗时间。"
            : "输入完整命令和标识，区分大小写。查询、非法输入和退出不确认消息、不消耗时间，也不交棒。");
        text.AppendLine("- `look`：重新显示完整画面。`help`：显示命令与当前受阻原因。");
        text.AppendLine("- `quit`：退出；EOF 同样退出。已提交的活动和未确认消息可随存档恢复。");
        if (observation.IsExpedition) {
            text.AppendLine("- `go <passage-id>`：沿当前可见通路移动。耗时如下；描述是眼前线索，不保证目的地或安全。途中不支持 cancel。");
            foreach (CyclePassageObservation passage in observation.Passages) {
                AppendCommand(text, observation, $"go {passage.Id}", CycleCommand.Go(passage.Id),
                    $"{passage.Description}；耗时 {Minutes(passage.DurationTicks)} 分钟" +
                    (passage.KnownDestinationName is { } known ? $"；曾到访的目的地：{known}" : "；目的地尚未探索"));
            }
        }
        AppendCommand(text, observation, "continue", CycleCommand.Continue(), "确认感知并交还控制权，保留活动和进度");
        if (observation.ArrivalDue is null) {
            AppendCommand(text, observation, "cancel", CycleCommand.Cancel(), observation.IsExpedition
                ? "取消采集或等待，保留已经获得的物品；随后选择新活动，移动不能取消"
                : "取消当前活动，保留已经获得的物品；随后按轮转选择新活动");
        }
        if (observation.Survival is not null) {
            text.AppendLine("- `use <item-id>`：消耗背包内 1 份物品补给，不消耗时间，不改变活动或行程期限，也不确认历史警告。");
            foreach (CycleCommandCheck supply in observation.Commands.Where(entry => entry.Command.Kind == CycleCommandKind.Use)) {
                AppendCommand(text, observation, $"use {supply.Command.ItemId}", supply.Command,
                    CycleSurvivalRules.UseDescription(supply.Command.ItemId!)!);
            }
        }
        text.AppendLine("- `gather <resource-id> <正整数|all>`：数量 1–2147483647；all 固定为接受命令时的库存，不随再生增加、不预留。新开时必须有货，不能同时安排另一耗时活动。");
        foreach (CycleResource resource in observation.Resources) {
            AppendCommand(text, observation, $"gather {resource.Id} 1", CycleCommand.Gather(resource.Id, 1), $"最多取得 1 份{resource.Name}");
            AppendCommand(text, observation, $"gather {resource.Id} all", CycleCommand.GatherAll(resource.Id), $"当前 all = {resource.Stock} 份；也可改为任意受支持的正整数 n，不要求库存已有 n 份");
        }
        var waits = Enumerable.Range(1, 24).Select(hour => (Hour: hour, Reason: observation.CheckDisplayed(CycleCommand.Wait(hour)))).ToArray();
        int[] allowed = waits.Where(entry => entry.Reason is null).Select(entry => entry.Hour).ToArray();
        text.AppendLine($"- `wait <hours>`：hours 为 1–24 的整数；{(allowed.Length == 24 ? "全部可执行" : $"当前可执行：{(allowed.Length == 0 ? "无" : string.Join(", ", allowed))}")}。" +
            (observation.Survival is null ? "等待完成后才请求行动。" : "等待完成或发生危险警告时请求行动。"));
        foreach (var group in waits.Where(entry => entry.Reason is not null).GroupBy(entry => entry.Reason)) {
            string hours = group.Count() == 24 ? "1–24" : string.Join(", ", group.Select(entry => entry.Hour));
            text.AppendLine($"  - 小时数 {hours} 受阻：{group.Key}");
        }
        foreach (var group in allowed.Select(hour => (Hour: hour, Warning: observation.WarningDisplayed(CycleCommand.Wait(hour))))
            .Where(entry => entry.Warning is not null).GroupBy(entry => entry.Warning)) {
            text.AppendLine($"  - 小时数 {string.Join(", ", group.Select(entry => entry.Hour))} 风险提示（不禁止）：{group.Key}");
        }
        return text.ToString();
    }

    private static void AppendCommand(StringBuilder text, CycleObservation observation, string syntax, CycleCommand command, string description) {
        string? reason = observation.CheckDisplayed(command);
        description = description.TrimEnd().TrimEnd('。', '；');
        text.AppendLine($"- `{syntax}`：{description}；{(reason is null ? "可执行。" : $"受阻：{reason}")}");
        if (observation.WarningDisplayed(command) is { } warning) {
            text.AppendLine($"  - 风险提示（不禁止）：{warning}");
        }
    }

    private static void RenderSurvival(StringBuilder text, ModelTime time, CycleSurvivalObservation survival, bool ended) {
        text.AppendLine("## 身体状态");
        if (ended) {
            text.AppendLine(survival.Sheltered ? "结束时处于庇护中。" : "结束时暴露在野外。");
            text.AppendLine();
            text.AppendLine("| 指标 | 结束时余量 |");
            text.AppendLine("| --- | --- |");
            text.AppendLine($"| 食物 | {Number(survival.Food)} |");
            text.AppendLine($"| 饮水 | {Number(survival.Water)} |");
            text.AppendLine($"| 温暖 | {Number(survival.Warmth)} |");
            text.AppendLine();
            return;
        }
        text.AppendLine(survival.Sheltered ? "当前有庇护，可避风回暖；食物和饮水仍会消耗。" : "当前暴露在野外，温暖会随昼夜变化。");
        text.AppendLine("余量范围 0–100；25 触发低值提醒，任一项耗尽至 0 则本局失败。");
        text.AppendLine();
        text.AppendLine("| 指标 | 当前余量 | 当前趋势（点 / 小时） |");
        text.AppendLine("| --- | --- | --- |");
        text.AppendLine($"| 食物 | {Number(survival.Food)} | {Trend(survival.FoodPerHour)} |");
        text.AppendLine($"| 饮水 | {Number(survival.Water)} | {Trend(survival.WaterPerHour)} |");
        text.AppendLine($"| 温暖 | {Number(survival.Warmth)} | {Trend(survival.WarmthPerHour)} |");
        text.AppendLine(survival.FatalAt is { } fatal
            ? $"- 维持当前趋势且不补给，预计最早致命时刻：{FormatTime(fatal)}（约剩 {Minutes(Math.Max(0, fatal.Ticks - time.Ticks))} 分钟）。环境改变后需重新评估。"
            : "- 当前趋势没有可预计的致命时刻；环境改变后需重新评估。");
        text.AppendLine();
    }

    private static string Number(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Trend(int value) => value > 0 ? $"+{value}" : value.ToString(CultureInfo.InvariantCulture);

    private static string ItemName(CycleObservation observation, string itemId) => observation.ItemNames.GetValueOrDefault(itemId, itemId);
    private static string Hours(long ticks) => ((decimal)ticks / CycleTime.Hour).ToString("0.##", CultureInfo.InvariantCulture);
    private static string Minutes(long ticks) => ((decimal)ticks / 60_000).ToString("0.##", CultureInfo.InvariantCulture);
    private static string FormatTime(ModelTime time) {
        string minutes = $"第 {time.Ticks / CycleTime.Day} 天 {time.Ticks % CycleTime.Day / CycleTime.Hour:00}:{time.Ticks % CycleTime.Hour / 60_000:00}";
        long remainder = time.Ticks % 60_000;
        return remainder == 0 ? minutes : $"{minutes}:{remainder / 1_000:00}.{remainder % 1_000:000}";
    }

}
