# R4 · FreePlayWorld 扩展与 FreePlayFact 联合

状态：未开始。依赖：R3。产物：`src/Server/FreePlay`、`tests/Server.Tests`。

## 目标

把场景的 fact 类型从裸 `GraphSpatialFact` 换成场景级联合 `FreePlayFact`，并把 `PlayerProcessState` 装入 `FreePlayWorld`。这是纯机械结构轮：**现有全部行为与测试保持不变**，Process 状态进入世界但不产生任何候选（旧决策路径继续驱动）。

## 前置阅读

- [Simulation Kernel](../../design/simulation-kernel.md) §3.2（一个 batch 表达一个 transition）、§7.1（composite HostWorld）
- 现状：`src/Server/FreePlay/FreePlayScene.cs`、`src/Server/FreePlay/FreePlaySession.cs`

## 类型与改动

```csharp
internal abstract record FreePlayFact;
internal sealed record SpatialFact(GraphSpatialFact Fact) : FreePlayFact;
internal sealed record ProcessFact(FreePlayProcessFact Fact) : FreePlayFact;

internal sealed record FreePlayWorld(GraphSpatialState Spatial, PlayerProcessState Process, ModelTime EffectiveTime);
```

逐项改动：

1. `FreePlayScene`：`IOccurrenceRule<FreePlayWorld, FreePlayCandidate, FreePlayFact>`；`Fold` 按 fact 联合分派（Spatial → 既有 `_reducer.Apply`；Process → `PlayerProcessReducer.Apply`），`EffectiveTime = instant.ModelTime` 不变；`Validate` = Spatial `ValidateComplete` + `PlayerProcessValidator.Validate`（**不含**跨域不变量，理由见 R3）；`Genesis` 增加 `PlayerProcessStateGenesis.Create()`；`PlanSelectedAsync` 的两处返回（inner spatial draft、决策 draft）把每条 `GraphSpatialFact` 包成 `SpatialFact`。
2. `FreePlaySession`：`InMemoryOccurrenceHistory<FreePlayWorld, FreePlayFact>` 与 `SimulationKernel<FreePlayWorld, FreePlayCandidate, FreePlayFact>` 的泛型替换；`PublishCommitted` 的轨迹投影与 `OccurrenceView.FactKinds` 先解联合再取内层 fact 的 `GetType().Name`（保持现有断言 `"Start"`/`"Arriv"` 子串不变绿）。
3. 旧决策流（DecisionPoint 候选、`TryPlan`、`_decide` 回调、Submit）**一字不改**；R4 之后世界里的 Process 恒为 genesis 值（Awaiting(MissingInstruction)），对旧流不可见。

## 测试清单

- 现有 `FreePlaySessionTests`、`HttpBoundaryTests`、`ApplicationBoundaryTests` 不修改且全绿（本轮的硬验收）。
- 新增少量 Server.Tests：联合分派——SpatialFact 只推进 Spatial、ProcessFact 只推进 Process、两者都推进 EffectiveTime；`PlayerProcessValidator` 拒绝非法 Process 状态（如 Ready + 空 Pending）经 `scene.Validate` 暴露。

## 验收

```text
dotnet test DramaBoard.slnx
→ 全绿；未新增任何候选种类；旧决策流行为不变。
```

## 禁止事项

- 不注册任何新规则、不改 Kernel 构造参数中的规则列表（仍只有 scene 一个）。
- 不加跨域不变量、不让旧决策流触碰 Process 状态。
- 不改 `FreePlayCandidate` 形状（process 臂在 R6 加）。
