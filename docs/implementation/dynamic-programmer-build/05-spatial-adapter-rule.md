# R5 · FreePlaySpatialRule：到达适配

状态：未开始。依赖：R4。产物：`src/Server/FreePlay/Runtime/FreePlaySpatialRule.cs`、`tests/Server.Tests`。

## 目标

新建包装 Spatial 内部规则的适配层：Forecast 原样转发（不含旧 DecisionPoint），到达候选的 Plan 在同一 draft 中追加 `MoveArrived`，使"位置到达"与"结束当前 Move、确定下一阶段"一次提交。**本轮不注册进 live session**（live 仍用旧 scene 规则；本类与 R6 的规则在 R7 一并接管）。

## 前置阅读

- [Graph Spatial World](../../design/graph-spatial-world.md) §3.1（Host adapter / 外层 rule 投影 inner 候选并追加 Game facts 的既有模式）
- 现状：`src/Spatial/Simulation/SpatialOccurrenceRule.cs`（inner 规则与候选 data 联合）、`src/Spatial/Facts/GraphSpatialFact.cs`（`TraversalArrivedFact(EntityId, ExpectedMovementGeneration)`）

## 规格

```csharp
internal sealed class FreePlaySpatialRule : IOccurrenceRule<FreePlayWorld, FreePlayCandidate, FreePlayFact>
```

- 构造持有 inner `SpatialOccurrenceRule`（由 `GraphDefinition` 创建）。
- `Forecast`：`_spatial.Forecast(world.Spatial, rules)` 逐条包成 `FreePlayCandidate` 的 Spatial 臂（照抄现 scene 的包装行），不添加任何场景候选。
- `PlanSelectedAsync`（winner.Data.Spatial）：
  1. 调 inner `PlanSelectedAsync(world.Spatial, ...)` 得 `TransitionDraft<GraphSpatialFact>`；
  2. 若 winner data 是 `TraversalArrivalOccurrenceData`：要求 inner draft 恰为一条 `TraversalArrivedFact`；检查 `world.Process.Mode` 为 `RunningMoveProcess(g)` 且 `g == fact.ExpectedMovementGeneration`，否则抛 `InvalidOperationException`（规则故障、零提交）；draft = `[Spatial(TraversalArrived), Process(MoveArrived(g))]`；
  3. 其它（`PassageEntryChangeOccurrenceData`）原样映射为 `SpatialFact` 列表。

## 测试清单（Server.Tests，kernel 级，仅注册本规则）

构造世界的方式：从 `scene.Genesis()` 出发，用 `scene.Fold` 逐条折叠 facts（planner 产出 `TraversalStartedFact`，R3 的 `ProgramAcceptedFact`/`MoveStartedFact` 手工构造）。

- `ArrivalCommitsSpatialAndProcessInOneBatch`：折叠 `ProgramAccepted([Move(ac), Move(cd)])` + `TraversalStarted(ac)` + `MoveStarted(1)` 于 T0 得合法世界；`StepAsync` → Committed@2000，facts 为 `[TraversalArrived, MoveArrived(1)]`；终态 AtPlace C、`Ready`、Pending 含 `Move(cd)`；再 `StepAsync` → Exhausted。
- 空尾部版本：`ProgramAccepted([Move(ac)])` → 到达后 `Awaiting(MissingInstruction)`。
- `ArrivalWithoutRunningMoveFailsBeforeCommit`：构造 Traversing + `RunningThink` 的不一致世界（`PlayerProcessState` record 可直接构造）；Step → Plan 抛 `InvalidOperationException`，TransitionCount 仍 0（防御性检查生效）。R7 激活世界级跨域不变量时，本测试同步改写为断言构造期 `scene.Validate` 拒绝该世界（防御分支保留在 Plan 内，但不再可经 Kernel 构造触达）。

## 验收

```text
dotnet test DramaBoard.slnx
→ 全绿；live session 行为不变（本规则未被注册）。
```

## 禁止事项

- 不修改 inner `SpatialOccurrenceRule`、不改其候选 key/due/data。
- 不在 Forecast 里做任何过滤或排序（全量转发是 Kernel 合同）。
- 不处理"到达时 Process 不是 RunningMove"以外的防御分支——那是规则故障，抛错即止。
