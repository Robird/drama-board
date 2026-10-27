# 时空引擎独立化 TODO / Task Brief

状态：两步均已完成并验收，当前没有剩余施工待办。2026-10-27。

## 目标与边界

将 DramaBoard 收缩为可独立使用、持续演进的时空库，让角色规划、人机交互与呈现层作为可恢复的技术储备冻结。按顺序完成两个独立验收的步骤：先收缩工程，再升级 DurableGraph。

- 活跃核心保留 `Kernel`、`Spatial`、`Host` 及其测试，暂留当前仓库与项目划分。
- 保留时间推进、同刻仲裁、地点与通路、运动段、相遇、掉头、到达和跨领域原子变化的现有语义。
- 不解决角色长期 Plan，不扩展 Player，不新建通用框架，不另拆仓库或发布公共包。
- MUD 接入实验不属于这份施工待办；完成两步后再依据结果评估，不自动续做。

## 步骤 1：收缩 DramaBoard

已完成：归档应用层并收缩为三核心；旧固定依赖下的构建、schema Verify 和无 Player 场景及完整核心测试通过。恢复入口见[归档索引](../archive/player-runtime.md)，准确命令、证据与范围见[验收记录](../build-log/0027-spacetime-extraction.md)。核心时空、仲裁、跨领域原子变化、E/S 与 pending 合同保持。

## 步骤 2：更新核心的 DurableGraph 依赖

已完成：固定 DG `c549124` 与 Storage `70b406c` 的真实十一包闭包，更新准备脚本、隔离 NuGet 配置、SDK 与 CI。三核心源码与 31 份 schema 不变；新版构建、生成/schema Verify、真实模型保存冷读、全部行为测试通过。准确版本与复现方式见[包来源](../worksets/durablegraph-package-source.md)，实际执行与范围见[验收记录](../build-log/0027-spacetime-extraction.md#step-2--current-durablegraph-packages-and-generated-models)。

两步均保留了核心时空和调度语义；没有新增生产磁盘 adapter，也不承诺旧实验存档兼容。归档实验继续使用原基线及旧依赖语境。

## 两步之后再评估

潜在消费者是 `E:\repos\Atelia-org\durable-graph-mud`。是否接入、首个场景、位置权威、时间结算、回合级/occurrence 级提交与恢复边界，届时结合两边最新代码另定。此处不制定接入施工方案，也不修改 MUD。

## 源码与资料入口

- [当前项目状态](../../PROJECT-STATE.md)、[文档索引](../README.md)。
- [Kernel](../../src/Kernel/Simulation/SimulationKernel.cs)、[Spatial](../../src/Spatial/State/GraphSpatialState.cs)、[Host](../../src/Host/SimulationHost.cs)。
- [空间核心验收](../../tests/Spatial.Tests/Acceptance/SpatialKernelAcceptanceTests.cs)、[接触核心验收](../../tests/Spatial.Tests/Acceptance/SpatialContactKernelAcceptanceTests.cs)、[项目依赖约束](../../tests/Kernel.Tests/ProjectDependencyGuardTests.cs)。
- [Graph 设计](../design/graph-spatial-world.md)、[E/S 合同](../design/durablegraph-occurrence-persistence.md)、[当前包准备说明](../worksets/durablegraph-package-source.md)。
- [既有归档范例](../archive/firstboard-llm.md)、[Player 设计储备](../../archive/player-runtime/docs/design/player-runtime/README.md)。
