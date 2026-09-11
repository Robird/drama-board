# DurableGraph 真实接入近期计划

> 状态：**推荐路线，待用户采纳；本轮只完成调研与文档，没有实施迁移。**
> 2026-09-12 静态核对：DramaBoard `063e3e7`、DurableGraph `f68388f`。上游另有 DB-067 研究文档未提交，本计划不依赖它；没有重跑构建、测试或包实验。

## 1. 推荐路线

**原地适配真实领域模型，局部改写 Kernel 的提交/恢复边界，保留调度与 Graph Spatial 算法。**
把真实 PackageReference 验证嵌入这次产品纵切，不继续扩建一套独立实验世界，也不创建长期并存的新 Kernel/Spatial。

首片保留纯 fold 与不可变替换；直接保存完整的新 State 根，不沿用旧 GraphSession 固定根假设。E 记录已规划、经 scratch 校验的整次变化，冷开只完成 pending E；精确语义集中见[提交与恢复方案](../design/durablegraph-occurrence-persistence.md)。

对于 Coding Agent，本仓库的有利条件是现有算法、规则和测试的边界明确，可以按职责替换代码。另开目录不会减少领域声明适配、消费方迁移和验收工作。需要重写的函数可直接在原模块内重写；不要求逐行修补旧实现。

## 2. 要求与证据边界

| 来源 | 本计划据此保留或限制的内容 |
|---|---|
| 用户已定方向 | 强类型领域图、独立 E/S 历史、事件快照与独立浏览；正常恢复不从头执行历史业务 reducing。 |
| 用户本轮要求 | 比较实验、原地迁移和重写，形成近期计划；不是已批准实施任何候选路线。 |
| 当前源码与行为测试 | 全量 Forecast、确定性 winner、单次 Occurrence 跨域完整提交、1ms 时间、contact 局部进展、Player 信息隔离。 |
| 当前真实消费者 | FirstBoard rules/Host、FirstBoard.Demo 的 Authority/Presentation、现有保存/读取/fork 测试；不能只验证一个孤立 storage adapter。 |
| 尚待采纳的建议 | E 在 Plan 与 scratch 验证后；首片保留纯 fold；先客观世界冷恢复，再接实际入口与退出旧路径。 |
| 首片之外 | 完整 Player memory/LLM 交互与执行栈恢复、可玩 fork/rewind、长轨迹局部查询、模型升版验证分别安排，不暗中提升为首片门槛。 |

## 3. 路线比较与改动范围

| 路线 | 判断及具体代价 |
|---|---|
| 按旧 preflight 继续独立实验 | 保留场景/oracle，替换过时方案。GraphRepository 单 head、固定根、缺 branch/根替换的前提已失效；再造 Saved 模型只会增加未来搬运。 |
| 只加 Attribute、替换 Journal sink | 不足。Kernel 的 AppendBatch 不接收 scratch World，且构造/每步校验依赖全部 `Batches.Count` 和 tail；加载图后无法直接续局。 |
| 原地领域适配 + 局部接缝替换 | **推荐**。复用真实玩法、查询、算法与验证；修改模型、history 合同、恢复与完成通知。 |
| 全面重写 Kernel/Spatial/World | 目前没有重建领域本体的证据。Spatial 已是 Graph；全面重写会重做确定性排序、接触数学、入口方向、导航和消费方验证。 |
| 全部改为稳定可变实体 | 可行候选，但不是加 Attribute 的附带小改。改变失败后内存可观察值，涉及预检与 presentation world；按实际维护成本再选择。 |

| 范围 | 本轮调查结论与源码依据 |
|---|---|
| 保留算法 | [ForecastRound](../../src/Kernel/Simulation/ForecastRound.cs)、[OccurrenceScheduler](../../src/Kernel/Scheduling/OccurrenceScheduler.cs)、[LogicalInstantRules](../../src/Kernel/Simulation/LogicalInstantRules.cs)；Spatial 的 planner/contact math/query/navigator 保留。 |
| 替换提交接缝 | [SimulationKernel](../../src/Kernel/Simulation/SimulationKernel.cs) 的 journal 对齐、发布、故障后 Replay 要求；[LiveAuthorityLoop](../../src/FirstBoard.Demo/Live/LiveAuthorityLoop.cs) 的 Journal 尾部通知来源。 |
| 适配状态与事件 | [FirstBoardDomain](../../src/FirstBoard/FirstBoardDomain.cs) 的 World/Game/Actor/事件 union；[GraphSpatialState](../../src/Spatial/State/GraphSpatialState.cs)、[SpatialLocation](../../src/Spatial/State/SpatialLocation.cs)、[GraphSpatialFact](../../src/Spatial/Facts/GraphSpatialFact.cs)；其所达值类型、集合和枚举也要覆盖。 |
| 保留真实行为 | [PassageEncounterHostTests](../../tests/FirstBoard.Tests/PassageEncounterHostTests.cs)、[TravelGoalAtomicityAndReplayTests](../../tests/FirstBoard.Tests/TravelGoalAtomicityAndReplayTests.cs) 的规则/原子性断言；[FirstBoardPresentationLoop](../../src/FirstBoard.Demo/Live/FirstBoardPresentationLoop.cs) 仍真实消费纯 fold。 |
| 更新依赖规则 | [Spatial dependency guard](../../tests/Spatial.Tests/Architecture/DependencyGuardTests.cs) 当前禁止全部 PackageReference；定向允许 DG Runtime/生成工具，保留领域依赖方向。 |

DG `79be2c2` / `f68388f` 已落地上手、恢复、XML 与 ReadPair 反馈；当前没发现阻断首片的存储功能缺口。接口能力见[上游 README](../../../durable-graph/README.md)，细项状态见[反馈目录](../feedback/durablegraph/README.md)。实际模型编译和游戏冷恢复仍须在本仓库证明。

## 4. A：真实世界保存与冷恢复

这是第一交付，不以“包能编译”或“加载出几个对象”结束。使用正式领域类型、正式 Kernel 接缝与实际 DG 包；场景 runner 可以先是确定性测试入口，不宣称已经交付 LLM Demo 存档。

### A0 · 固定行为对照与依赖

- 从 PassageEncounterHostTests 提取 traveling prefix 与 request→response 纯 driver；对照保存在新进程可读取的规范测试输出中，不依赖同一进程保存的对象引用或 driver 队列游标。
- 补齐完整 committed-boundary oracle：WorldSnapshot 只作差异提示，另外比较 NextPersistentId、KnownFacts.Text、完整 Spatial、Kernel 游标与下一 request/候选行为。
- 前缀若直接由 reducer 构造，将它明示为这个测试 run 的 S0，按其世界时间初始化 genesis、count=0、last instant/key=null；不能把原用例的手工 fold 前缀伪称为已有历史。随后真实 Kernel 提交 contact/encounter/response。
- 固定本批 DG 与 Atelia 源码/包版本及来源。若本机打包，使用干净的固定 checkout 和新包版本，记录 feed/命令；CI 不依赖任意兄弟工作树。原有 Atelia CI pin 不自动等于 DG 本批依赖版本。
- 逐项映射 FirstBoard.Persistence.Tests 的旧 Kernel 调用与保存/fork 行为：受新接缝直接影响的测试随 A 迁移，纯旧 codec/sink 见证可暂独立保留。若 fork 等未决行为阻碍这次切换，在 A2 前裁决该项范围；不能把测试禁用或旧兼容构造器当作阶段衔接。

### A1 · 正式持久模型闭包

- 原地调整 Kernel 必需值类型、Spatial 动态状态/事实、FirstBoard 世界/事实为支持的 durable 声明；不把 Protocol 请求、候选私有数据、规则服务一概标记持久化。
- private 数组/受支持容器 + 只读查询，明确复制更新与语义相等；DG 恢复不调用构造器，补完整恢复校验。
- 各模型库保留自己的 `.dgschema`，经公开登记 facade 在宿主组合。每个声明模型的库走真实 Runtime PackageReference；不只用 ProjectReference 绕过包工具。
- 覆盖模型**全部可达的合法类型**，不因首场景只用 Continue 就漏掉其他 fact case 或 Spatial 派生类型。

### A2 · Kernel 与 EventHistory 接通

- 用[方案](../design/durablegraph-occurrence-persistence.md)的有限 State 游标替代整链 batch 计数依赖；保留 last cause 的无进展检测。
- 接入 E→S 发布与独立恢复入口；只在完成 S 后安装世界/通知，故障停止并重开。
- 为普通算法测试提供相同 E/S 合同的内存实现。旧持久 sink 不参与 DG 会话，不双写、不导入全历史来满足旧校验。
- 同步调整 FirstBoardScenario、LiveSession、LiveAuthorityLoop 及现有测试接线，先用新内存合同保持既有运行入口可用；不为推迟 B 而留下旧 Kernel 兼容构造器。B 再交付实际入口的 DG 存档创建/打开行为。
- 真实 FirstBoard adapter 保存完整状态和精确内容绑定；热路径复用 scratch，冷 E-head 只应用一次待完成事件。

### A3 · 首片验收

| 验收 | 最小见证 |
|---|---|
| 连续/冷重开等价 | Alice/Bob 同 Passage 的真实相遇；在 encounter 打开前、打开后、响应后保存/关闭，在新进程恢复；Continue 和 Reverse 的后续变化顺序、CandidateKey、LogicalInstant、完整世界及下一 request 等价。 |
| 因果游标 | 同 ModelTime 重开后 ordinal 正确接续；重复 cause 与预算仍拒绝；版本按完成 Occurrence 数量增长，不按 E/S frame 数增长。 |
| 空间与跨域原子性 | contact 不重复、pending 不丢；Reverse 的目标/anchor/generation 和 TravelGoal 正确；逐 fact 故障不交付 Game/Spatial prefix，沿用持票出发等既有反例。 |
| Pending E | E-head 冷开只完成该 E，Plan/Player/Forecast 调用数为零；成功后 pending 清空。S-head 加载的历史 fold 数为零。 |
| 发布异常 | 受控中断覆盖 E 前、E 后/S 前、S 已发布但调用未返回；丢弃尝试后按实际 head 恢复，不能重复消费事件。通过消费方故障 seam/复用上游见证验证，不修改文件伪造断电保证。 |
| 事件独立与快照 | 用仅含 E 及快照闭包的模型登记独立打开/读取事件；不登记 World/无关状态类型。读取当时字段正确，后续 State 变化不改旧 E；不依赖跨图 ReferenceEquals。 |
| 绑定与声明 | 错 Definition/不兼容 rules 失败；跨库生成、history Publish→Clean→Verify 和实际 PackageReference 恢复成功。 |

代码修改后执行相关 Kernel/Spatial/FirstBoard/Demo/持久化测试及完整 Local solution，Windows 下串行构建以避免输出锁。记录本轮实际命令、版本、结果与限制，不沿用旧测试数量作通过结论。

首片测量模型声明/复制/登记代码工作量、Base/Delta/Remove、实际文件字节、Commit 时间/分配；无预设“必须比旧存储更小”的门槛。问题附最小复现进入反馈目录。

## 5. B：接入实际运行入口

紧接 A，迁移 [LiveSession](../../src/FirstBoard.Demo/Live/LiveSession.cs) / FirstBoardScenario 的创建与打开接线，让既有运行入口可选择一个 DG 存档继续真实世界；不再新建无人使用的第二个游戏 runner。

- 完成通知来自已完成 S，presentation 从该边界开始；展示用 facts fold 可继续存在，不充当权威恢复。
- 保留现有新局、Human/AI、取消与呈现行为的验收。若恢复 driver 自带记忆/预算影响未来，必须显式决定保存或重新绑定规则，不能宣称加载世界已恢复全部 Player。
- **B 的交付范围需在 A 后裁决**：可先交付纯策略 driver 的可用存档，再扩到完整 LLM/Player closure；不让这一未决项阻止客观世界 A，也不把它隐藏在“Demo 已支持续局”中。

## 6. C：旧路径退出与后续反馈

旧 Journal.Atelia / 手写 FirstBoard 正文 codec 仅在尚有未迁移消费者时保留。相应行为迁入 DG 回归、实际调用方切换后，从 solution/project/CI 移除其旧接线及只约束旧 wire 的测试，Git 保存历史；不提供无需求的旧存档转换，也不留下永久双后端开关。

C 是退出条件，不是必须拖到最后的清理批次；在 A/B 中已经失去消费者的部分随当批删除。受到 A 的 Kernel 改动影响的保存行为测试不能等到 C 才处理。

已有 fork 测试是现实行为资产，但 DG branch 不自动等于可玩 fork：删除旧路径前明确新 lineage、完整 S 与 Player 边界的保留或延期决定，不能顺手丢弃。内存调度测试和可选 conformance/fold 对照可以继续保留。

随后用一个有业务意义的字段完成一次两代真实模型升级/续写，再按实际代码和测量判断局部 mutable、历史定位/局部浏览与 Spatial 新玩法的优先级。ReadPair 优化、完整 VM、MCP 就绪均不构成 A 的依赖。

## 7. 施工分工与停止扩张条件

| 有界工作包 | 所有权与依赖 |
|---|---|
| Oracle/测试材料 | 独占 FirstBoard 测试 helper 与冷进程 harness；A0 可先独立完成。 |
| Kernel | 独占 Kernel 源码/对应测试；主线程先固定 E/S 接缝、游标与完成结果合同。 |
| Spatial 模型 | 独占 Spatial 模型/对应测试/项目 history；Kernel 值类型合同固定后可与 FirstBoard 模型并行。 |
| FirstBoard 模型与 adapter | 独占 FirstBoard 模型/持久 adapter/history；依赖已固定的 Kernel/Spatial 形状。不要与另一代理同时编辑 FirstBoardDomain。 |
| Host/Demo 接线 | 独占 FirstBoardScenario、LiveSession、LiveAuthorityLoop 及相关调用方测试；A 跟随 Kernel 合同保持入口可运行，B 接上持久创建/打开。 |
| 集成与独立复核 | 主线程负责 props/solution/CI、依赖固定与串行验收；独立代理检查失败窗口和完整状态闭包。 |

每包产出可审查 diff 和行为证据，集成提交保持可构建，不把各代理编译成功当作最终验收。不为每个包建立临时产品程序集；必要时用短期分支/worktree 隔离未完成施工。

如发现必须复制整套领域模型、必须重新调用 Player 才能完成 E、必须放弃现有空间/因果法则才能保存，先提交最小复现并回到本方案裁决。一般声明或 API 摩擦在切片内解决，不自动升级为全库重写或上游平台需求。
