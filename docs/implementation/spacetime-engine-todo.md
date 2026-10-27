# 时空引擎独立化 TODO / Task Brief

状态：用户已确认方向，尚未实施；2026-10-27。下一轮从步骤 1 开始。

## 目标与边界

将 DramaBoard 收缩为可独立使用、持续演进的时空库，让角色规划、人机交互与呈现层作为可恢复的技术储备冻结。按顺序完成两个独立验收的步骤：先收缩工程，再升级 DurableGraph。

- 活跃核心保留 `Kernel`、`Spatial`、`Host` 及其测试，暂留当前仓库与项目划分。
- 保留时间推进、同刻仲裁、地点与通路、运动段、相遇、掉头、到达和跨领域原子变化的现有语义。
- 不解决角色长期 Plan，不扩展 Player，不新建通用框架，不另拆仓库或发布公共包。
- MUD 接入实验不属于这份施工待办；完成两步后再依据结果评估，不自动续做。

## 步骤 1：收缩 DramaBoard

本步保留现有 DurableGraph 固定依赖，先让工程边界与行为证据独立成立。

- [ ] 核对工作树、项目依赖及现有构建入口；保留未提交工作，记录拆分前准确基线。旧测试结果仅作历史参考，实际运行结果另记。
- [ ] 建立拆分前的本地固定 tag，保留完整路径、依赖与构建语境；若有未提交内容，先厘清其归属，不能用 HEAD 冒充完整工作树快照。不推送 tag 或分支。
- [ ] 按“固定 tag + 仓内归档”保存本次应用实验，沿用 `archive/firstboard-llm/` 的归档方式，为本批另建归档单元；记录原路径、归档范围及恢复方式。
- [ ] 归档 `Protocol`、`Player`、`Decision.Validation`、`Player.Agency`、`Server`、`WebUI`，连同专属测试、脚本和资料；保留角色运行时与 Plan 设计作为技术储备，不要求归档代码继续适配活跃核心。
- [ ] 区分展示缓存/播放与引擎历史：呈现投影、事件播放及动画随应用冻结；`IOccurrenceHistory`、E/S 提交、pending 恢复、重放与完成结果等核心合同保留。已有 FirstBoard 归档保持可恢复。
- [ ] 收缩两个 slnx、CI、默认构建/验证入口与搜索范围。移除活跃流程中的前端安装、Server 发布和 Chromium 要求；调整仍按旧项目路径读取的依赖约束测试。
- [ ] 补齐或复用一个无 Player、Protocol、Server 的小场景，直接组合 Kernel/Spatial/Host，验证多实体移动、途中相遇、严格内部掉头与到达。优先复用现有测试设施，不为演示引入新 UI 或长期服务。
- [ ] 更新 README、文档索引、归档索引与 PROJECT-STATE；将旧应用后续工作移出活跃路线，修正受影响链接及设计文档的适用范围说明。
- [ ] 在旧依赖下构建收缩后的 solution，运行 Kernel、Spatial、Host 相关测试与小场景，复核集成 diff 和依赖方向。

### 验收点

- 两个 solution 的活跃项目仅为三核心及其测试，默认验证不依赖冻结应用。
- 小场景实际运行，确定性时间顺序、相遇消费、掉头后的运动段与最终到达符合现有合同；保持 contact floor、arrival ceil 和同刻仲裁语义。
- 核心无需调用 Programmer；`PlanSelectedAsync` 仍表示单次事件的转移规划，不承担角色长期 Plan。
- 归档源文件与设计可以定位，拆分前完整上下文可以通过固定基线恢复；不以归档项目能在新主线上编译作为验收条件。
- 实际运行证据与未验证边界有明确记录；先完成本步验收，再进入步骤 2。

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
- [既有归档范例](../archive/firstboard-llm.md)、[Player 设计储备](../design/player-runtime/README.md)。
