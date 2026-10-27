using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.ResourceCycle;

/// <summary>One asynchronous owner drives the Kernel; input and rendering never mutate the world.</summary>
internal static class PlayConsole {
    public static async Task<int> RunAsync(string? store, CancellationToken cancellationToken, bool survival = false) {
        using CycleDurableHistory? durable = store is null ? null : Directory.Exists(store)
            ? CycleDurableHistory.Open(store) : CreateStore(store, survival);
        IOccurrenceHistory<CycleWorld, CycleFact> history;
        if (durable is not null) {
            history = durable;
        } else {
            var initial = survival ? CycleGameFactory.CreateSurvival() : CycleGameFactory.CreatePlayable();
            history = new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor);
        }
        if (history.State.Characters.Count == 0) {
            throw new ArgumentException("这是批处理实验存档，不能用 --play 打开。请省略 --play 续跑，或选择新目录开始采集。");
        }
        if ((history.State.Expedition is not null) != survival) {
            throw new ArgumentException(survival ? "这是原采集场景存档，请使用 --play。" : "这是生存场景存档，请使用 --survival。");
        }

        var kernel = CycleEngine.CreateKernel(history, characterController: new ConsoleCharacterController());
        string title = survival ? "山林求生" : "多人采集";
        Console.WriteLine(store is null
            ? $"{title} · 本次仅在内存中运行；退出后不保留进度。"
            : $"{title} · 存档：{store} · 每次成功转换自动保存。");
        Console.WriteLine(survival
            ? "你在山林中迷途。观察路标、探索通路，寻找人类社会。眼前事实与旧观察分开显示；等待输入期间世界暂停。"
            : "共用控制台轮流扮演角色；画面只显示当前角色的技能、活动细节和背包。等待输入期间世界暂停。");
        if (kernel.RecoverPending(cancellationToken)) {
            Console.WriteLine("已完成上次中断时保存的行动；接下来按已保存的活动与请求继续。");
            PrintPublicChanges(kernel.LastCompletion!, survival);
        }

        try {
            while (true) {
                if (kernel.World.Expedition is { Outcome: not CycleExpeditionOutcome.Exploring }) {
                    Console.WriteLine(PlayView.Render(CycleDecisionContext.Create(kernel.World, kernel.World.Characters[0].Id).Observation));
                    return 0;
                }
                StepStatus status = await kernel.StepAsync(new ModelTime(long.MaxValue), cancellationToken);
                if (status != StepStatus.Committed) {
                    throw new InvalidOperationException($"采集世界未能产生下一步行动：{status}。");
                }
                PrintPublicChanges(kernel.LastCompletion!, survival);
            }
        } catch (PlayEndedException) {
            Console.WriteLine(store is null ? $"已退出。内存中的{(survival ? "生存" : "采集")}进度未保存。" :
                $"已退出。已完成的行动均已保存，可使用同一 {(survival ? "--survival" : "--play")} --store 路径继续。");
            return 0;
        }
    }

    public static void RequireBatchWorld(CycleWorld world) {
        if (world.Characters.Count != 0) {
            throw new ArgumentException(world.Expedition is not null
                ? "这是生存场景存档；请使用 --survival --store 打开。批处理不会推进此存档。"
                : "这是交互采集存档；请使用 --play --store 打开。批处理不会推进此存档。");
        }
    }

    private static CycleDurableHistory CreateStore(string path, bool survival) {
        var initial = survival ? CycleGameFactory.CreateSurvival() : CycleGameFactory.CreatePlayable();
        return CycleDurableHistory.Create(path, initial.World, initial.Cursor);
    }

    private static void PrintPublicChanges(OccurrenceCompletion<CycleFact> completion, bool survival) {
        foreach (CycleFact fact in completion.Event.Facts) {
            if (PlayView.RenderPublicChange(fact, survival) is { } text) { Console.WriteLine(text); }
        }
    }

    private sealed class PlayEndedException : Exception { }

    // Shared across characters; each invocation owns its input loop and renders the requested actor's view.
    private sealed class ConsoleCharacterController : ICycleCharacterController {
        public async ValueTask<CycleCommand> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) {
            CycleObservation observation = context.Observation;
            string actorId = observation.Actor.Id;
            Console.WriteLine();
            Console.WriteLine(PlayView.Render(observation));
            while (true) {
                cancellationToken.ThrowIfCancellationRequested();
                Console.Write($"{actorId}> ");
                string? input = await Console.In.ReadLineAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (input is null) { throw new PlayEndedException(); }
                PlayInput parsed = PlayInput.Parse(input);
                if (parsed.Kind == PlayInputKind.Quit) { throw new PlayEndedException(); }
                if (parsed.Kind == PlayInputKind.Look) { Console.WriteLine(PlayView.Render(observation)); continue; }
                if (parsed.Kind == PlayInputKind.Help) { Console.WriteLine(PlayView.RenderCommands(observation)); continue; }
                if (parsed.Command is not { } command) {
                    Console.WriteLine("无法识别命令。请输入 look、help、quit、continue、cancel、wait <1–24 整数小时>、gather <resource-id> <正整数|all> 或 go <passage-id>。时间未推进。");
                    continue;
                }
                string? reason = context.Check(command);
                if (reason is not null) {
                    Console.WriteLine($"无法行动：{reason} 时间未推进；未确认消息。");
                    continue;
                }
                return command;
            }
        }
    }
}
