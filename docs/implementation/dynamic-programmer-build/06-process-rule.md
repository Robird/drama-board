# R6 · FreePlayProcessRule 与闭环验收

状态：未开始。依赖：R2、R5。产物：`src/Server/FreePlay/Runtime/FreePlayProcessRule.cs`、`tests/Server.Tests`。

## 目标

实现 Process 规则：按 Mode 预测 respond/start/think-complete 候选，在 Plan 中完成材料构造、Programmer 调用、权威校验、空程序规范化与指令启动。配合 R5 的适配规则，在 kernel 级跑通机制文档 §7.2 的行为矩阵。**本轮不注册进 live session**（R7 切换）。

## 前置阅读

- [机制模型](../../design/player-runtime/dynamic-programmer.md) §4–§6 与 §7.1（最小可观察闭环）、§7.2（行为验收表）
- [Graph Spatial World](../../design/graph-spatial-world.md) §4.2（pure planner 的拒绝语义）
- 本目录 [README](README.md) 的候选映射与提交语义表
- 现状：`src/Spatial/Planning/SpatialPlanner.cs`（TryStartTraversal 及拒绝原因）、`src/Player.Agency/Spatial/FullMapPlayerSpatialKnowledgeGetter.cs`、`src/Spatial/Queries/SpatialQueries.cs`

## 候选与数据类型

```csharp
internal enum FreePlayProcessCandidateKind { Respond, Start, ThinkComplete }
internal sealed record FreePlayProcessOccurrenceData(FreePlayProcessCandidateKind Kind) : FreePlayProcessCandidateData;
// FreePlayCandidate 增加第二臂：
internal sealed record FreePlayCandidate(
    OccurrenceCandidate<SpatialOccurrenceData>? Spatial,
    FreePlayProcessOccurrenceData? Process);
```

Forecast 按 Mode（due 与 key 见 README 裁决表；key 用与 Spatial 规则相同的 Utf8JsonWriter 数组字节风格）：

| Mode | 候选 | Due | Key |
|---|---|---|---|
| AwaitingProgrammer(r) | Respond | `world.EffectiveTime` | `["free-play/process/respond", rev]` |
| Ready | Start | `world.EffectiveTime` | `["free-play/process/start", rev]` |
| RunningThink(c) | ThinkComplete | `new ModelTime(c)` | `["free-play/process/think-complete", c]` |
| RunningMove / RunningStay | 不产生候选 | — | — |

## 规则规格

```csharp
internal sealed class FreePlayProcessRule : IOccurrenceRule<FreePlayWorld, FreePlayCandidate, FreePlayFact>
// ctor(string runId, IDynamicProgrammer programmer, SpatialPlanner planner,
//      SpatialQueries queries, GraphDefinition definition)
// 常量：MoveSpeedUnitsPerMs = 1；ThinkDurationMs = 1000
```

所有 Plan 分支先按 Spatial 规则的既有模式重验 winner（key/revision/Mode 与当前世界一致，不符抛 `InvalidOperationException`）。重验是"被选 occurrence 仍匹配当前状态"的合同断言，与 Kernel 的 `RequireProgress`/同刻预算互不替代，不得以"规则已查"为由移除后者。

**Respond**：

1. Mode 为 AwaitingProgrammer(reason)，ControlRevision 与 winner key 一致；
2. 构造 `ProgrammingRequest`（冻结材料）：
   - `ActivationId = $"{runId}:{ControlRevision}"`；
   - `Reason = reason`；`ReasonDetail`：仅 MoveBlocked 时从世界推导一句说明（头部 Move 的通路、当前位置是否端点、该方向是否可进入），其余为 null；
   - `PlaceId`：`queries.GetLocation(...)` 的 `AtPlaceView.PlaceId`（首批 respond 只在地点激活；`TraversingView` 分支不可达，抛 `InvalidOperationException` 防御）；
   - `KnownPassages`：`FullMapPlayerSpatialKnowledgeGetter<FreePlayWorld>.Instance` 的已知图全量通路，按 id 排序；每条含两端、`ExpectedDurationMs`（速度 1 时等于 Length，注释说明来源）、两端**静态** `InitialEntryAccess`（取自已知图定义，不读 `queries.GetPassageEntryAccess`——远方动态入口状态不属合法披露，运行时理念 §3；动态关闭由开始时 planner 拒绝与 `MoveBlocked` 原因表达）；
   - `Workspace`、`RemainingProgram = Pending.Items`；
3. `await programmer.ProgramAsync(request, ct)`；
4. `ProgrammingResponseValidator.Validate(response, request)` 失败 → 抛 `InvalidOperationException`（Step 失败零提交）；
5. 空程序规范化为 `[StayInstruction]`；
6. draft = `[Process(new ProgramAcceptedFact(program, response.Workspace))]`。

**Start**（Mode 为 Ready，按头部指令种类）：

- `MoveInstruction`：`PassageId = new(handle.Value)`；`planner.TryStartTraversal(world.Spatial, Actor, passageId, MoveSpeedUnitsPerMs, world.EffectiveTime)`：
  - Accepted → draft `[Spatial(startedFact), Process(new MoveStartedFact(actor.MovementGeneration + 1))]`（gen 取自 plan 前世界的 actor；与 Spatial reducer 的 +1 规则一致，R7 世界级不变量兜底）；
  - Rejected → draft `[Process(new MoveBlockedFact(passageId.Value))]`（受阻是正式世界内结果；不消费头部）；
- `ThinkInstruction` → `[Process(new ThinkStartedFact(note, ThinkDurationMs))]`；
- `StayInstruction` → `[Process(new StayStartedFact())]`。

**ThinkComplete**：Mode 为 RunningThink 且 due 一致 → `[Process(new ThinkCompletedFact())]`。

## 测试清单（Server.Tests，kernel 级：`[FreePlaySpatialRule, FreePlayProcessRule]` + `ScriptedDynamicProgrammer`）

建议先写一个共享 harness：构造 kernel、按脚本推进、暴露 world/history 断言助手。地图与时长：ab=1000、ac=2000、bd=3000、cd=1000，速度 1。

1. `TwoMovesReachDWithoutMidProgrammer`：脚本 `[Move(ac), Move(cd)]` → `[]`。期望 7 个 transition，ModelTime 序列 `[0,0,2000,2000,3000,3000,3000]`（接受、启动、到达 C、启动、到达 D、接受空程序、Stay 启动）；Programmer 恰被调用 2 次（初始缺指令、终点缺指令；**C 处不调用**）；终态 `RunningStay`、Exhausted。
2. `AcceptanceAndStartAreSeparateSameTickTransitions`：上例前两个 transition 同为 T=0、CausalOrdinal 0/1（接受与开始是两次提交）。
3. `ThinkConsumesTimeKeepsNoteAndMidEventStillFires`：genesis 折叠 `PassageEntryChangeScheduledFact`（如 T=2500 关闭 ab 某方向）注入独立世界事件；程序 `[Move(ac), Think(note)]` → `[]`。期望：Think 于 2000 开始、3000 完成；2500 的 entry change 在两者之间提交；ThinkStarted 后工作区含 `[2000] note`；完成后 Reason 为 ThinkCompleted（即使 Buffer 为空也不再报缺指令）；下次请求材料含该笔记，且其 KnownPassages 中 ab 仍为静态初始位——未泄露 2500 的动态关闭（运行时理念 §3）。
4. `ThinkCompletionRewritesNonEmptyTailAndWorkspace`：程序 `[Move(ac), Think(note), Move(cd)]`。Think 于 3000 完成时 Reason 为 ThinkCompleted **且 RemainingProgram 仍含 `Move(cd)`**（Buffer 非空也先交回 Programmer——机制 §6）；第二次响应 `[Move(ac)]` + 修订工作区 → Pending 与工作区同时改写、回 A 于 5000 ms、已完成的 A→C 与模型时间不被回改（§7.2"改写未来"行、§7.1 步 5）；变体：原样提交尾部 `[Move(cd)]` → 授权继续，D 于 4000 ms（§5.3 表"原样提交已有非空尾部"行）。
5. `BlockedMovePreservesHeadAndEntersMaintenance`：`[Move(ac), Move(bd)]`：到 C 后 start 被 Planner 拒绝 → `MoveBlocked("bd")` 提交，Pending 仍为 `[Move(bd)]`，Reason=MoveBlocked、ReasonDetail 非空；脚本原样重交 `[Move(bd)]` → 再次受阻提交（无 Kernel 故障、无 RequireProgress 触发）；改交 `[Move(cd)]` → 抵达 D。
6. `StayKeepsTailAndExhaustsNormally`：`[Stay, Move(cd)]`：Stay 启动后 Pending 仍含 `Move(cd)` 且不执行；`[Stay, Think(note)]` 变体：尾部 Think 可见且不执行、工作区无该笔记、无 think-complete 候选（§7.2"保留安排"行）；无候选时 Step 返回 Exhausted；注入的 2500 世界事件照常提交（Stay 不冻结世界）。
7. `LegalEmptyProgramNormalizesToStayAtAcceptance`：`[]` 响应 → 历史中的 `ProgramAcceptedFact.Program` 是 `[StayInstruction]`。
8. `InvalidResponseFailsStepWithZeroCommitAndIsReasked`：脚本先返回未知 handle、再返回合法程序；第一次 Step 抛错后 TransitionCount=0、world 不变、`kernel.IsFaulted == false`；第二次 Step 正常提交。
9. `RequestMaterialsAreFrozenAndComplete`：断言某次请求的 KnownPassages 为 4 条（含时长与两端静态 entry bit）、RemainingProgram 与世界一致、PlaceId 正确；重复消费同一请求对象不受后续修改影响。
10. `SameTickBudgetExhaustionIsDeterministicFault`：脚本程序员于同一 ModelTime 恒原样重交受阻程序（如在 C 反复重交 `[Move(bd)]`），断言耗尽 `MaxTransitionsPerModelTime=100` 时 Step 抛 `InvalidOperationException`、TransitionCount 停在该 tick 上限内、世界为最后合法提交、`kernel.IsFaulted == false`（LIV-1；fault 由会话层呈现）。

同刻语义注意：接受与开始之间可被同 tick 其它原因插入（如注入的 entry change 恰在 T=0）属预期，不强测；同 tick 受阻循环受 `MaxTransitionsPerModelTime=100` 预算约束，超出即**确定性 fault**（`LogicalInstantRules.Propose` 抛错 → 会话 faulted、保留最后世界、无孤儿 pending）。切换后人类可经数十次同刻原样重交受阻程序触达，属预期并由 0026 记录；不做提交前计数拒绝（那要求 HTTP 层读世界、给纯校验器加状态，且违反原样重交的无条件合法性）。

## 验收

```text
dotnet test DramaBoard.slnx
→ 全绿；live session（旧决策流）行为不变。
```

## 禁止事项

- 不调用 planner 以外的任何 Spatial 写路径；不接受程序时预检可执行性。
- 不在规则内缓存请求或程序员结果；每轮 Forecast 从世界重derive。
- 不为"Programmer 明确结束却无法产出程序"定义降级（机制文档 §5.3 明确首批不做）。
