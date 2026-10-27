# 时空引擎独立化 TODO / Task Brief

状态：步骤 1 已验收；步骤 2 待实施。2026-10-27。

## 目标与边界

将 DramaBoard 收缩为可独立使用、持续演进的时空库，让角色规划、人机交互与呈现层作为可恢复的技术储备冻结。按顺序完成两个独立验收的步骤：先收缩工程，再升级 DurableGraph。

- 活跃核心保留 `Kernel`、`Spatial`、`Host` 及其测试，暂留当前仓库与项目划分。
- 保留时间推进、同刻仲裁、地点与通路、运动段、相遇、掉头、到达和跨领域原子变化的现有语义。
- 不解决角色长期 Plan，不扩展 Player，不新建通用框架，不另拆仓库或发布公共包。
- MUD 接入实验不属于这份施工待办；完成两步后再依据结果评估，不自动续做。

## 步骤 1：收缩 DramaBoard

已完成：归档应用层并收缩为三核心；旧固定依赖下的构建、schema Verify 和无 Player 场景及完整核心测试通过。恢复入口见[归档索引](../archive/player-runtime.md)，准确命令、证据与范围见[验收记录](../build-log/0027-spacetime-extraction.md)。核心时空、仲裁、跨领域原子变化、E/S 与 pending 合同保持。

## 步骤 2：更新核心的 DurableGraph 依赖

上游入口：`E:\repos\Atelia-org\durable-graph`。实施时读取其当前项目状态与公开契约，选择并固定明确版本/提交，不依赖浮动的“最新”。

- [ ] 盘点三核心的实际 DG 使用点：模型声明、生成注册、schema history、包准备脚本与 CI。当前活动核心没有磁盘持久化 adapter，不把归档 adapter 的迁移带入本步。
- [ ] 选择上游可复现的包来源与固定版本，按上游当前方式准备真实包闭包；更新依赖配置、准备脚本、CI 与包来源说明，不仅替换版本字符串。
- [ ] 适配编译与生成器契约；核对生成注册和 schema history。对生成差异追查原因，不以批量覆盖历史文件或手改生成代码绕过问题。
- [ ] 保持核心行为和 `IOccurrenceHistory` 的 E/S/pending 合同；新版 DG 的 Repository/WorkingCopy 不接管模拟规则或自动重放。只有实际适配需要时才修改核心代码。
- [ ] 使用新依赖重新构建三核心与测试，执行相关模型/生成验证、既有行为测试及步骤 1 的小场景；对照旧依赖下的行为结果。
- [ ] 更新准确的依赖版本、复现步骤和实现边界，删除过时的活跃准备说明；归档实验继续保留原依赖语境。

### 验收点

- 本地与 CI 配置指向同一套明确、可复现的新依赖，活跃构建不再需要旧 DG 包闭包。
- 三核心编译、模型生成/schema 检查和相关行为验证实际通过；时空与调度语义没有因依赖升级悄然变化。
- 报告区分编译兼容、生成契约和运行行为，不将任一项通过等同于完整持久化验收。
- 本步不新增磁盘恢复产品能力，不承诺旧实验存档兼容；若适配迫使改变领域语义或存档兼容范围，先单列问题裁决。

## 两步之后再评估

潜在消费者是 `E:\repos\Atelia-org\durable-graph-mud`。是否接入、首个场景、位置权威、时间结算、回合级/occurrence 级提交与恢复边界，届时结合两边最新代码另定。此处不制定接入施工方案，也不修改 MUD。

## 源码与资料入口

- [当前项目状态](../../PROJECT-STATE.md)、[文档索引](../README.md)。
- [Kernel](../../src/Kernel/Simulation/SimulationKernel.cs)、[Spatial](../../src/Spatial/State/GraphSpatialState.cs)、[Host](../../src/Host/SimulationHost.cs)。
- [空间核心验收](../../tests/Spatial.Tests/Acceptance/SpatialKernelAcceptanceTests.cs)、[接触核心验收](../../tests/Spatial.Tests/Acceptance/SpatialContactKernelAcceptanceTests.cs)、[项目依赖约束](../../tests/Kernel.Tests/ProjectDependencyGuardTests.cs)。
- [Graph 设计](../design/graph-spatial-world.md)、[E/S 合同](../design/durablegraph-occurrence-persistence.md)、[当前包准备说明](../worksets/durablegraph-package-source.md)。
- [既有归档范例](../archive/firstboard-llm.md)、[Player 设计储备](../../archive/player-runtime/docs/design/player-runtime/README.md)。
