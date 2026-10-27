# 运行时预测缓存：实施与验收

2026-10-27。对应[运行时派生预测方案](../design/runtime-derived-predictions.md)，替代 `21379a1` 的[持久 Contacts 基线](local-prediction-acceptance.md)。本片完成 Kernel/Spatial 与其他消费者的机械接线，没有迁移 ResourceCycle 的实际预测维护。

## 实现边界与使用方式

- [SpatialSimulationState](../../src/Spatial/State/SpatialSimulationState.cs)、`SpatialContactPredictions`、`ContactPrediction` 是普通不可变 CLR 对象；删除其 durable 定义及业务包装/预测替换 facts。`PassageContactKey` 仍服务于持久业务消费记录。
- [SpatialSimulation](../../src/Spatial/Simulation/SpatialSimulation.cs) 的 `Rebuild(business, boundaryTime)` 用于 genesis 和冷开。正常推进在业务 Fold 后调用 `Complete(before, instant, facts, folded)`，按逐 fact 旧/新通道收集 dirty，基于最终业务状态重算；保持传入 `folded.Business` 的引用，旧快照不变。候选读取不求交。
- [Kernel](../../src/Kernel/Simulation/SimulationKernel.cs)、Replay、Conformance、Fork 统一 Fold → Complete → 带时间 Validate。E 保存原业务 facts；正常推进只在 S 成功后安装运行时世界。pending 恢复可以重做纯派生计算，不重新选择 pending winner 或调用 Plan/Player。
- [测试专用持久 adapter](../../tests/Spatial.Tests/TestSupport/SpatialDurableHistory.cs) 的磁盘根为 Business/cursor；打开时按 cursor 重建一次、稳定持有 `_state`，S 成功后安装传入的同一 wrapper。实际产品存档入口仍未建设。

应用接线先 `Rebuild`，再将就绪 wrapper 与 cursor 交给 history 和 `CreateKernel`。冷开加载 Business/cursor 后再次 `Rebuild`；如果存在 pending E，先建立旧 S 的就绪 wrapper，再 `RecoverPending`。不能把未完成的 Fold scratch 暴露给候选读取。

## 行为证据

| 合同 | 本轮证据 |
| --- | --- |
| 新组合发现、空通道新人、无关通道不求交、消费/代数回收 | [LocalPredictionAcceptanceTests](../../tests/Spatial.Tests/Acceptance/LocalPredictionAcceptanceTests.cs) 保留局部发现用例；固定种子两通道六实体的 36 tick 脚本逐提交比较全量 Rebuild |
| 独立完整性检查 | [ContactOracle](../../tests/Spatial.Tests/TestSupport/ContactOracle.cs) 独立从全局实体对发现并编码候选键；只共享精确求交数学，不读取 dirty/cache 决定范围 |
| 同 tick 消费不复活、未消费 due-now 保留 | 局部验收及 [SpatialPredictionPersistenceTests](../../tests/Spatial.Tests/Persistence/SpatialPredictionPersistenceTests.cs)，包括真实 S 冷开和随后 E-only 恢复 |
| 五条执行入口都在 Fold 后准备缓存 | [CompletionBoundaryTests](../../tests/Kernel.Tests/Simulation/CompletionBoundaryTests.cs) 的独立 Business/Cache 模型验证 normal、pending、Replay、Conformance、Fork；E 中只有原业务 facts |
| 准备/校验失败和 E/S 边界异常不提前安装缓存 | CompletionBoundaryTests 与局部验收；真实 adapter 的非法 S cursor 导致 fault，重开后 pending 可恢复；未修改既有取消、重入、同 tick 合同 |
| 实际 S 冷开续跑事件轨迹一致 | SpatialPredictionPersistenceTests：每个完成边界关闭 Repository，fresh open 后对照候选、cause/instant/facts、世界及 cursor |
| E-only 恢复重新生成接触，不规划 | 同上：各发布边界和新人入场样本，保存 E 仅为业务 fact；`ForbiddenPlanning` 在 Forecast/Plan 抛错，Complete 允许且本批一次，之后继续对照不中断轨迹 |
| 缓存不在磁盘对象图或模型目录中 | [SpatialPredictionStorageShapeTests](../../tests/Spatial.Tests/Persistence/SpatialPredictionStorageShapeTests.cs) 绕过 adapter，直接 raw `LoadState/LoadEvent`，检查 durable 字段/可达类型与生成注册 catalog；根只有 Business/cursor |
| 陈旧或缺失内存缓存不成为存档权威 | StorageShapeTests 与 [BoundaryTests](../../tests/Spatial.Tests/Persistence/SpatialPredictionBoundaryTests.cs) 将错误代数/过期/缺项缓存附在有效 Business 上，真实创建与冷开后恢复完整正确输出，旧 wrapper 保持原状 |
| 无后续 Step 也拒绝不合法边界 | BoundaryTests 覆盖空 Replay/Fork、pending/replay 的错误业务 contact generation 与过期时间；StorageShapeTests 的实际 Business/cursor 存档在 Open 重建时拒绝过期接触；genesis 另验 arrival/schedule Due |

## 本轮执行

主线程在 Windows / .NET SDK 10.0.201 执行集成 Release 构建，生成新的测试根 schema 后运行：

```powershell
dotnet test DramaBoard.slnx -c Release --no-build --no-restore -m:1
dotnet build DramaBoard.slnx -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
```

259 项测试通过，无失败/跳过（Kernel 131、Spatial 91、ResourceCycle 35、Host 2）；最终构建零警告、零错误。Schema Verify 覆盖 Kernel 7、Spatial 24、ResourceCycle 14 及测试发布根 1 个当前 schema。空间旧 6 个派生模型 schema 删除，测试根按无兼容负担重建 V1，没有双格式读取路径。

Kernel、Spatial 及持久化专项分别经过独立只读审查，主线程核对源码与运行结果。日志留在忽略的 `artifacts/runtime-cache-*.log`；文档链接与集成 diff 另行检查。没有修改上游或兄弟仓库源码。

## 证据范围与后续

结构 Validate 不能证明预测无遗漏，仍由独立 oracle 及增量/全量对照检查；没有生产全量重算兜底。当前只是减少求交范围，没有宣称候选扫描、复制、验证或整体 CPU 已达某种复杂度/加速比例。

故障测试模拟实际 E 提交后抛错、S 调用前失败及非法 cursor 拒绝，并关闭重开 Repository；没有模拟断电或磁盘写入中途故障。陈旧缓存实验只证明当前代码从业务重新发现，没有模拟两个算法版本，更不承诺任意旧 pending 与新规则兼容。

`SpeedSnapshot`、`ArrivalDue`、`RequiredTicks` 的业务语义保持。默认平衡值改变不会自动修改已保存的在途/生长过程。后续已先在单角色 ResourceCycle 完成 Forecasts/固定路由运行时迁移，见[应用缓存验收](resource-cycle-runtime-cache-acceptance.md)；多角色化为独立切片。
