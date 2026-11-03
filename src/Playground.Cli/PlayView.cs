using System.Globalization;
using System.Text;
using DramaBoard.Kernel.Time;
using DramaBoard.Playground;

/// <summary>Pure Markdown projection for one character; does not acknowledge notices or mutate state.</summary>
/// <remarks>Only the game-produced observation is available here; rendering cannot query hidden world state.</remarks>
internal static class PlayView {
    public static string Render(PlaygroundObservation observation, CharacterInteractionState? interactionState = null) =>
        Render(PlaygroundViewSnapshot.Create(observation, interactionState));

    public static string Render(PlaygroundViewSnapshot snapshot) {
        PlaygroundObservation observation = snapshot.Observation;
        PlaygroundActorObservation character = observation.Actor;
        bool ended = observation.Outcome != PlaygroundExpeditionOutcome.Exploring;
        var text = new StringBuilder();
        text.AppendLine($"# {snapshot.Background.Title} · {character.Name}（{character.Id}） · {FormatTime(observation.Time)}");
        text.AppendLine();
        text.Append(RenderKnowledge(observation, snapshot.Background));
        if (snapshot.IncludeHistory) { text.Append(ContextHistoryPlayView.RenderHistory(observation, snapshot.Context)); }
        text.AppendLine("## 历史通知");
        foreach (PlaygroundNoticeObservation notice in observation.Notices) {
            text.AppendLine($"- {RenderNotice(notice, ended)}");
        }
        if (observation.Survival is { PendingWarnings.Count: > 0 } needs) {
            text.AppendLine(ended ? "- 生存警告（结束时尚未确认的历史记录）：" : "- 生存警告（历史记录，待确认）：");
            foreach (string warning in needs.PendingWarnings) { text.AppendLine($"  - {warning}"); }
            text.AppendLine("- 这些警告记录已经发生的危险；即使已经补给也会保留，当前余量以“身体状态”为准。" +
                (observation.Outcome == PlaygroundExpeditionOutcome.Exploring
                    ? "use 只补充食物或饮水；" + NavigationView.ContinueMeaning(observation) : "旅程已经结束，无需再确认。"));
        }
        if (!observation.IsExpedition && character.LastResult is { } result) {
            text.AppendLine($"- 上次结果（记录当时状态）：{result}");
        }
        text.AppendLine();
        if (observation.Weather is { } pastWeather) { RenderWeatherHistory(text, pastWeather); }
        text.AppendLine(ended ? "## 结束时的状态" : "## 当前状态与环境");
        text.AppendLine(ended ? "此角色的旅程已经结束；以下为结束时的冻结记录，其他角色仍可继续探索。"
            : $"当前角色：{character.Name}（`{character.Id}`）。");
        text.AppendLine(ended ? $"结束时为{(observation.IsDay ? "白天" : "夜晚")}。" :
            $"{(observation.IsDay ? "白天" : "夜晚")}；下次{(observation.IsDay ? "天黑" : "天亮")}：{FormatTime(observation.NextLightChange)}。");
        text.AppendLine();
        if (observation.Survival is { } survival) { RenderSurvival(text, survival, ended); }
        if (!ended) { RenderWarmthAdvice(text, observation); }
        if (!ended && observation.Weather is { } weather) { RenderCurrentWeather(text, weather); }
        if (observation.IsExpedition) {
            text.AppendLine(ended ? "## 结束时的位置" : "## 当前位置");
            text.AppendLine(observation.ArrivalDue is { } arrival
                ? ended ? $"结束时仍在途中；{JourneyView.Route(observation.Journey!)}；原计划于 {FormatTime(arrival)} 到达，本段行程未完成，不再推进。"
                    : $"正在途中；{JourneyView.Route(observation.Journey!)}；预计 {FormatTime(arrival)} 到达（剩余 {Minutes(arrival.Ticks - observation.Time.Ticks)} 分钟）。本段移动不可取消；活动导航可 cancel 剩余路线，当前路段和期限保持不变。"
                : $"{observation.PlaceName}：{observation.PlaceDescription}");
            text.AppendLine();
        }
        if (character.Notices.Count > 0 && character.Activity is { } ongoing && !ended) {
            text.AppendLine($"- 你的活动没有中断；当前周期仍于 {FormatTime(ongoing.Due)} 结算。");
        } else if (character.Activity is { } active && !ended) {
            text.AppendLine($"- 原活动进行中，当前周期仍于 {FormatTime(active.Due)} 结算。");
        }
        text.Append(ContextHistoryPlayView.RenderCurrent(observation, snapshot.Context, snapshot.DraftHandoff));
        if (snapshot.IncludeGoals) { text.Append(GoalView.RenderDefault(observation.Goals)); }
        text.AppendLine(ended ? "## 结束时的活动记录" : "## 你的活动");
        text.AppendLine($"- 采药等级：{character.SkillLevel}；采集速度：基础的 {100m + 25m * character.SkillLevel:0}%。");
        if (EconomyView.RenderProduction(text, observation, ended)) {
            // Production details above use the frozen reserved order.
        } else if (ended) {
            if (observation.ArrivalDue is { } unfinishedArrival) {
                text.AppendLine($"- 移动未完成；原计划到达时刻：{FormatTime(unfinishedArrival)}。此记录已冻结。");
            } else if (character.Activity is { } unfinished) {
                string description = unfinished.Kind == PlaygroundActivityKind.Wait ? "等待未完成" :
                    $"采集未完成：{unfinished.ResourceId}，已取得 {unfinished.CompletedCount}/{unfinished.TargetCount} 份";
                text.AppendLine($"- {description}；原周期截止时刻：{FormatTime(unfinished.Due)}。此记录已冻结，不再结算。");
            } else {
                text.AppendLine("- 此角色已停止安排活动；原推进及中断情况见终局记录。");
            }
        } else if (observation.ArrivalDue is { } due) {
            text.AppendLine($"- 正在移动，预计 {FormatTime(due)} 到达；当前不可采集或等待；本段不能取消，导航 cancel 只清剩余路线。");
        } else if (observation.FollowingLeaderId is not null) {
            text.AppendLine("- 正在跟随，等候领队安排行程；处理提醒后 continue 可继续同行。");
        } else if (observation.Navigation is not null) {
            text.AppendLine("- 导航在此等待确认；continue 尝试下一段，cancel 清除剩余路线。");
        } else if (character.Activity is not { } activity) {
            text.AppendLine(observation.Outcome != PlaygroundExpeditionOutcome.Exploring ? "- 无待执行的活动。" :
                observation.IsExpedition ? "- 空闲。可以沿可见通路移动、采集当前位置的资源，或等待。" :
                    "- 空闲。可以启动采集或等待；每次合法选择后交给下一个有请求的角色。");
        } else if (activity.Kind == PlaygroundActivityKind.Wait) {
            text.AppendLine($"- 正在等待，完成时刻：{FormatTime(activity.Due)}。");
        } else {
            PlaygroundResource resource = observation.Resources.Single(resource => resource.Id == activity.ResourceId);
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
        foreach (PlaygroundResource resource in observation.Resources) {
            var others = observation.OtherCharacters.Where(other => other.GatheringResourceId == resource.Id).ToArray();
            string members = others.Length == 0 ? "无" : $"{string.Join("、", others.Select(other => $"{other.Name}（{other.Id}）"))}，共 {others.Length} 人";
            string phase = resource.Stock == resource.Capacity ? "已满，未囤积再生进度" :
                $"{(resource.ActiveSince is null ? "暂停" : "进行中")}，进度 {Minutes(resource.ProgressAt(observation.Time))}/{Minutes(resource.RegenerationTicks)} 分钟";
            long duration = observation.HarvestDurations[resource.Id];
            text.AppendLine($"| {resource.Name} `{resource.Id}` | {resource.Stock} / {resource.Capacity} | {(resource.GrowsInDaylight ? "白天" : "夜晚")}每 {Hours(resource.RegenerationTicks)} 小时补 1；{phase} | 每份产出 {ItemName(observation, resource.ItemId)} ×1；你需 {Minutes(duration)} 分钟；要求采药等级 {resource.RequiredLevel} |" +
                (observation.IsExpedition ? "" : $" {members} |"));
        }
        text.AppendLine();
        FollowingView.RenderStatus(text, observation);
        RenderOtherCharacters(text, observation);
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
        EconomyView.RenderStatus(text, observation);
        TradeView.RenderStatus(text, observation);
        if (observation.Outcome != PlaygroundExpeditionOutcome.Exploring) {
            text.AppendLine("## 结局");
            text.AppendLine(observation.Outcome == PlaygroundExpeditionOutcome.Escaped ? "获救。" : "这位居民的生命已结束。其他居民仍可继续生活。");
            text.AppendLine(observation.EndingReason);
            text.AppendLine("此角色的旅程已经结束，不再安排或结算活动。");
        } else {
            text.Append(RenderCommands(observation, snapshot.IncludeGoals));
        }
        return text.ToString();
    }

    internal static string RenderKnowledge(PlaygroundObservation observation, PlaygroundBackground? savedBackground = null) {
        var text = new StringBuilder();
        PlaygroundBackground background = savedBackground ?? PlaygroundBackground.For(observation);
        text.AppendLine(background.Introduction);
        text.AppendLine();
        if (observation.Outcome == PlaygroundExpeditionOutcome.Exploring) {
            text.AppendLine("## 规则与已知常识");
            foreach (string rule in background.Rules) { text.AppendLine(rule); }
            text.AppendLine();
        }
        text.Append(NavigationView.RenderMap(observation));
        text.Append(NavigationView.RenderCandidate(observation));
        text.Append(NavigationView.RenderActive(observation));
        if (observation.Memories.Count > 0) {
            text.AppendLine("## 曾到访地点（历史观察）");
            text.AppendLine("以下是经历留下的记录，保留观察时间；远处余量可能已改变，不表示当前事实。");
            foreach (PlaygroundPlaceMemory memory in observation.Memories) {
                string stock = memory.Resources.Count == 0 ? "当时未见可采资源" :
                    string.Join("；", memory.Resources.Select(resource => $"{resource.Name} {resource.Stock}/{resource.Capacity}"));
                text.AppendLine($"- {memory.Name}（{FormatTime(memory.At)}）：{stock}。");
            }
            text.AppendLine();
        }
        return text.ToString();
    }

    internal static void RenderWarmthAdvice(StringBuilder text, PlaygroundObservation observation) {
        if (observation.Survival is not { WarmthWarningPending: true }) { return; }
        text.AppendLine("- 温暖警告：请依据当前温暖趋势、阈值预测与眼前补给效果安排下一步。" + (observation.ArrivalDue is not null
            ? "抵达前不能安排庇护停留；请留意剩余行程。"
            : "停留不保证回暖，是否恢复取决于当前条件与变化速率。"));
    }

    internal static string RenderNotice(PlaygroundNoticeObservation perceived, bool ended) {
        PlaygroundNotice notice = perceived.Notice;
        string repeated = notice.Count > 1 ? $"；待回应同类变化共 {notice.Count} 次，此处为最近一次" : "";
        if (notice.Kind == PlaygroundNoticeKind.CollectorJoined) {
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

    internal static void RenderOtherCharacters(StringBuilder text, PlaygroundObservation observation) {
        if (observation.IsExpedition && observation.OtherCharacters.Count == 0) { return; }
        text.AppendLine("## 其他角色");
        foreach (PlaygroundOtherCharacterObservation other in observation.OtherCharacters) {
            string status = other.GatheringResourceId is { } resourceId
                ? $"正在采集 {observation.Resources.FirstOrDefault(resource => resource.Id == resourceId)?.Name ?? resourceId}"
                : other.ProducingRecipeId is { } recipe ? $"正在生产 `{recipe}`" : other.Waiting ? "正在等待" : other.FollowingLeaderId is { } leader ? $"正在跟随 `{leader}`" : "空闲";
            text.AppendLine($"- {other.Name}（`{other.Id}`）：{status}。");
        }
        text.AppendLine();
    }

    public static string RenderCommands(PlaygroundObservation observation, bool includeGoals = true) {
        if (observation.Outcome != PlaygroundExpeditionOutcome.Exploring) { return "此角色的旅程已经结束，没有可用行动。\n"; }
        var text = new StringBuilder();
        text.AppendLine("## 可用命令");
        text.AppendLine();
        text.AppendLine("行动命令与标识区分大小写，需提供完整命令和标识。");
        if (includeGoals) { text.Append(GoalView.RenderHelp(observation)); }
        if (observation.Survival is not null) { text.AppendLine("属性预测见身体状态；改变预测条件的行动另有提示。"); }
        if (observation.Commands.Any(entry => entry.Command.Kind == PlaygroundCommandKind.Handoff)) {
            text.AppendLine($"- `handoff <文字>`：覆盖本轮交接草稿（最多 {CharacterInteractionState.MaximumHandoffScalars} 个 Unicode 字符）；`handoff --clear` 清空。零耗时，不确认警告；下一次放行模拟时交给新回合，不自动续传旧交接。");
        }
        CommunicationView.Render(text, observation);
        FollowingView.RenderCommands(text, observation);
        EconomyView.RenderCommands(text, observation);
        TradeView.RenderCommands(text, observation);
        text.Append(NavigationView.RenderHelp(observation));
        text.Append(NavigationView.RenderNavigateCommands(observation));
        if (observation.IsExpedition) {
            text.AppendLine("- `go <entrance-id>`：沿当前可见入口移动。耗时如下；描述是眼前线索，不保证目的地或安全。已经开始的路段不能取消，活动导航的 cancel 只清除剩余路线。");
            foreach (PlaygroundPassageObservation passage in observation.Passages) {
                AppendCommand(text, observation, $"go {passage.Id}", PlaygroundCommand.Go(passage.Id),
                    $"{passage.Description}；耗时 {Minutes(passage.DurationTicks)} 分钟" +
                    (passage.KnownDestinationName is { } known ? $"；已确认通向：{known}" : "；这条入口去向未确认"));
            }
        }
        AppendCommand(text, observation, "continue", PlaygroundCommand.Continue(), NavigationView.ContinueMeaning(observation));
        if (observation.ArrivalDue is null || observation.Navigation is not null) {
            AppendCommand(text, observation, "cancel", PlaygroundCommand.Cancel(), NavigationView.CancelMeaning(observation));
        }
        if (observation.Survival is not null) {
            text.AppendLine("- `use <item-id>`：消耗背包内 1 份物品补给，不消耗时间，不改变活动或行程期限，也不确认历史警告。");
            foreach (PlaygroundCommandCheck supply in observation.Commands.Where(entry => entry.Command.Kind == PlaygroundCommandKind.Use)) {
                AppendCommand(text, observation, $"use {supply.Command.ItemId}", supply.Command,
                    observation.ItemUseDescriptions[supply.Command.ItemId!]);
            }
        }
        text.AppendLine("- `gather <resource-id> <正整数|all>`：数量 1–2147483647；all 固定为接受命令时的库存，不随再生增加、不预留。新开时必须有货，不能同时安排另一耗时活动。");
        foreach (PlaygroundResource resource in observation.Resources) {
            AppendCommand(text, observation, $"gather {resource.Id} 1", PlaygroundCommand.Gather(resource.Id, 1), $"最多取得 1 份{resource.Name}");
            AppendCommand(text, observation, $"gather {resource.Id} all", PlaygroundCommand.GatherAll(resource.Id), $"当前 all = {resource.Stock} 份；也可改为任意受支持的正整数 n，不要求库存已有 n 份");
        }
        var waits = Enumerable.Range(1, 24).Select(hour => (Hour: hour, Reason: observation.CheckDisplayed(PlaygroundCommand.Wait(hour)))).ToArray();
        int[] allowed = waits.Where(entry => entry.Reason is null).Select(entry => entry.Hour).ToArray();
        text.AppendLine($"- `wait <hours>`：hours 为 1–24 的整数；{(allowed.Length == 24 ? "全部可执行" : $"当前可执行：{(allowed.Length == 0 ? "无" : string.Join(", ", allowed))}")}。" +
            (observation.Survival is null ? "等待完成后才请求行动。" : "等待完成或发生危险警告时请求行动。"));
        foreach (var group in waits.Where(entry => entry.Reason is not null).GroupBy(entry => entry.Reason)) {
            string hours = group.Count() == 24 ? "1–24" : string.Join(", ", group.Select(entry => entry.Hour));
            text.AppendLine($"  - 小时数 {hours} 受阻：{group.Key}");
        }
        foreach (var group in allowed.Select(hour => (Hour: hour, Warning: observation.WarningDisplayed(PlaygroundCommand.Wait(hour))))
            .Where(entry => entry.Warning is not null).GroupBy(entry => entry.Warning)) {
            text.AppendLine($"  - 小时数 {string.Join(", ", group.Select(entry => entry.Hour))} 行动提示（不禁止）：{group.Key}");
        }
        return text.ToString();
    }

    private static void AppendCommand(StringBuilder text, PlaygroundObservation observation, string syntax, PlaygroundCommand command, string description) {
        string? reason = observation.CheckDisplayed(command);
        description = description.TrimEnd().TrimEnd('。', '；');
        text.AppendLine($"- `{syntax}`：{description}；{(reason is null ? "可执行。" : $"受阻：{reason}")}");
        if (observation.WarningDisplayed(command) is { } warning) {
            text.AppendLine($"  - 行动提示（不禁止）：{warning}");
        }
    }

    private static void RenderSurvival(StringBuilder text, PlaygroundSurvivalObservation survival, bool ended) {
        text.AppendLine("## 身体状态");
        if (ended) {
            text.AppendLine(survival.Sheltered ? "结束时处于庇护中。" : "结束时暴露在野外。");
            text.AppendLine();
            text.AppendLine("| 指标 | 结束时余量 | 范围 | 结束时速率（冻结，不再推进） |");
            text.AppendLine("| --- | --- | --- | --- |");
            RenderAttributeRows(text, survival, forecastEnabled: false);
            text.AppendLine();
            return;
        }
        text.AppendLine(survival.Sheltered
            ? "当前有庇护保护；" + (survival.WarmthPerHour > 0 ? "当前温暖正在恢复。" : "温暖变化以当前速率为准。")
            : "当前暴露在野外，温暖变化以当前速率为准。");
        text.AppendLine(PlaygroundAttributeView.ForecastAssumptions);
        text.AppendLine();
        text.AppendLine("| 指标 | 当前余量 | 范围 | 当前趋势（点 / 小时）与构成 | 阈值与耗尽预测 |");
        text.AppendLine("| --- | --- | --- | --- | --- |");
        RenderAttributeRows(text, survival);
        text.AppendLine();
    }

    private static void RenderAttributeRows(StringBuilder text, PlaygroundSurvivalObservation survival, bool forecastEnabled = true) {
        foreach (var attribute in PlaygroundAttributeView.FromSurvival(survival, forecastEnabled)) {
            text.AppendLine($"| {attribute.Label} | {Number(attribute.Value)} | {attribute.Range} | {attribute.RateDescription}" +
                (attribute.Limit.Length == 0 ? "" : $"；{attribute.Limit}") + " |" +
                (forecastEnabled ? $" {attribute.ForecastDescription} |" : ""));
        }
    }

    internal static void RenderCurrentWeather(StringBuilder text, PlaygroundWeatherObservation weather) {
        text.AppendLine("## 天气与暴露");
        text.AppendLine(weather.NextWindow is { } next
            ? $"下次窗口：{FormatTime(next)}。结果尚未发生，可能平静，也可能遭遇寒风。"
            : "没有后续天气窗口。");
        text.AppendLine(weather.Sheltered ? "当前有庇护保护；离开后不再受保护。" : "当前没有庇护保护。");
        text.AppendLine();
    }

    internal static void RenderWeatherHistory(StringBuilder text, PlaygroundWeatherObservation weather) {
        text.AppendLine("## 最近实际天气（历史记录）");
        if (weather.LastAt is { } at && weather.LastKind is { } kind) {
            string name = kind == PlaygroundWeatherKind.ColdWind ? "寒风" : "平静";
            string impact = kind == PlaygroundWeatherKind.Calm ? "没有天气造成的温暖损失" :
                weather.LastWasSheltered ? "当时受到庇护保护，温暖损失 0 点" :
                    $"当时暴露在外，温暖实际减少 {Number(weather.LastActualWarmthLoss)} 点";
            text.AppendLine($"- 最近实际天气：{FormatTime(at)}，{name}；{impact}。");
            text.AppendLine("  这是当时已经结算的影响，不代表现在仍受影响，也不预示下一次结果。");
        } else {
            text.AppendLine("- 尚无已发生的天气记录。");
        }
        text.AppendLine();
    }

    private static string Number(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string ItemName(PlaygroundObservation observation, string itemId) => observation.ItemNames.GetValueOrDefault(itemId, itemId);
    private static string Hours(long ticks) => ((decimal)ticks / PlaygroundTime.Hour).ToString("0.##", CultureInfo.InvariantCulture);
    private static string Minutes(long ticks) => ((decimal)ticks / 60_000).ToString("0.##", CultureInfo.InvariantCulture);
    private static string FormatTime(ModelTime time) {
        string minutes = $"第 {time.Ticks / PlaygroundTime.Day} 天 {time.Ticks % PlaygroundTime.Day / PlaygroundTime.Hour:00}:{time.Ticks % PlaygroundTime.Hour / 60_000:00}";
        long remainder = time.Ticks % 60_000;
        return remainder == 0 ? minutes : $"{minutes}:{remainder / 1_000:00}.{remainder % 1_000:000}";
    }

}
