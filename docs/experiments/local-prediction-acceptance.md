# 局部预测重构：空间首阶段验收

> 历史记录：本文对应 `21379a1` 的持久预测实现。当前已改为[运行时预测缓存](../design/runtime-derived-predictions.md)，见[新验收记录](runtime-derived-predictions-acceptance.md)；下文“恢复不运行 Complete”和“E 保存预测替换”仅为旧基线证据，源码链接指向继续演化后的文件。

本记录对应[实施方案](../design/local-prediction-refactor.md)的 S1/S2。空间候选读取已改为持久接触输出的投影；业务变化通过 Kernel 的唯一 Complete 接缝重算受影响通道，并把结果与业务事实一起发布。ResourceCycle 本轮仅适配共享签名；其预测维护迁入 Complete 和 Forecast 命名收敛仍是 S3。

## 实现与使用入口

- [SpatialSimulation](../../src/Spatial/Simulation/SpatialSimulation.cs)：`CreateGenesis` 初始化预测；`CreateKernel` 安装全部空间规则和同一个 Complete；自定义发生源经 `additionalRules` 加入，返回 `SpatialBusinessFact` 包装的业务决定。
- [SpatialSimulationState](../../src/Spatial/State/SpatialSimulationState.cs)：Business 与 Contacts 为同根下独立不可变子图。Arrival/schedule 继续使用业务记录中的 Due，不复制权威数据。
- `SpatialSimulationFact`（仅存在于 `21379a1`）：Complete 在完整业务批次末尾至多追加一次通道输出替换；空输出清空指定通道，其他通道保持原输出。没有持久 dirty 队列或通用过程注册表。
- [SimulationKernel](../../src/Kernel/Simulation/SimulationKernel.cs)：显式 Complete 位于 Plan 与完整 Fold/Validate 之间；校验接收完成边界 ModelTime。恢复只 Fold 已记录 facts。

新游戏先调用 `CreateGenesis(business, genesisTime)`，再把结果与初始 cursor 放入 history。重开已有 S 时直接加载保存的组合根，经工厂创建 Kernel；有 pending E 则调用 `RecoverPending`，不得重新调用 CreateGenesis。实际游戏存档产品不在本片范围，测试使用[专属 DG adapter](../../tests/Spatial.Tests/TestSupport/SpatialDurableHistory.cs)验证单根发布。

## 行为证据

| 边界 | 可执行证据 |
| --- | --- |
| B 掉头撤销 AB、发现此前不存在的 BC；无关通道不重算，旧快照保持 | [LocalPredictionAcceptanceTests](../../tests/Spatial.Tests/Acceptance/LocalPredictionAcceptanceTests.cs) |
| 空通道、同批多人入场、消费与移除、多个 generation、非邻接同 tick 赢家 | 同上；固定种子两通道脚本逐完成边界核对全量参考 |
| 独立完整性检查 | [ContactOracle](../../tests/Spatial.Tests/TestSupport/ContactOracle.cs) 从全局实体对枚举，不读取 dirty 或存储预测来决定范围；独立编码候选键，共用已单测的精确求交数学 |
| 跨规则运动改变也维护、每个赢家只完成一次、回放不再次完成 | [SpacetimeHostScenarioTests](../../tests/Spatial.Tests/Acceptance/SpacetimeHostScenarioTests.cs)、[CompletionBoundaryTests](../../tests/Kernel.Tests/Simulation/CompletionBoundaryTests.cs) |
| Complete 抛错/null 在 E 前拒绝；重复维护 draft 拒绝 | CompletionBoundaryTests、LocalPredictionAcceptanceTests |
| 同根世界/预测/cursor 的逐 S 冷开，以及 E-only/S 中断恢复 | [SpatialPredictionPersistenceTests](../../tests/Spatial.Tests/Persistence/SpatialPredictionPersistenceTests.cs)：fresh Repository/注册，比较事件键、时刻、完整事实及最终状态 |
| 非空新预测随 E 保存，恢复不重新发现或调用 Player | 同上：新人入场产生非空替换，恢复时 Complete、候选读取、Plan 使用抛错探针，然后继续与不中断轨迹对照 |
| 错误 generation、过期 Due；无后续 Step 也拒绝 | [SpatialPredictionBoundaryTests](../../tests/Spatial.Tests/Persistence/SpatialPredictionBoundaryTests.cs)：contact/generation 覆盖 S 打开、pending、空/非空 replay；genesis 另覆盖 contact/arrival/schedule 三类 Due；Kernel 测试覆盖 conformance/fork 时间接线 |

保留原 contact floor、负时间、arrival ceil、同 tick ordinal、严格内部掉头和入口日程测试。低层数学/单事实测试使用仅测试内的组合根适配器；真实 Kernel/Host 与持久化验收均走新的完成路径。

## 本轮验证

2026-10-27，Windows / .NET SDK 10.0.201，主线程在集成后的工作树执行：

```powershell
dotnet build DramaBoard.slnx -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
dotnet test DramaBoard.slnx -c Release --no-build --no-restore -m:1
```

构建零警告、零错误；247 项测试通过（Kernel 125、Spatial 85、ResourceCycle 35、Host 2），无跳过。Spatial 新增六个模型 schema，测试发布根新增一个 schema；Verify 同时覆盖 Kernel、Spatial、ResourceCycle 及测试根。Kernel、Spatial 生产机制及 S2 冷开证据另经独立只读审阅，结论由主线程对照源码和实际运行验收。

## 证据边界

结构校验能拒绝失效的已存条目，不能证明没有遗漏新关系；测试明确展示空 Contacts 可以结构合法、却被独立 oracle 判为不完整。生产没有以每次全量求交来掩盖维护遗漏。

当前减少的是预测重算范围，不声称候选扫描、状态复制或验证已达亚线性，也没有测得整体 CPU 加速。内部通道重算探针只证明不进入无关通道的求交循环。更细粒度的变化段配对、索引与堆仍依照方案的实测重开条件处理。

持久化故障测试在发布调用边界注入异常，并实际关闭、重开 Repository；不等同于操作系统断电或底层存储故障测试。无旧存档迁移；本轮未改上游或兄弟仓库源码。
