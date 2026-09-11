# DramaBoard 当前项目状态

> 跨会话入口；维护方式见 [AGENTS.md](AGENTS.md)。按本文件选择下一处阅读，不必顺读历史记录。
> 最近整理：2026-09-12；当前实施 DurableGraph 真实接入。此前准备证据见[批次记录](docs/worksets/pre-integration-cleanup.md)，不作为本轮新测试结果。

## 最终目标

探索 LLM-Native 的开放世界游戏：结合角色扮演、模拟养成、棋类与 RL Gym 的需求，用真实可玩场景逐步选择机制。
让领域世界能够持续演化、保存和恢复，同时保持领域代码易写、空间行为可解释、时间推进确定。
DramaBoard 作为 DurableGraph 的真实消费者，用游戏需求检验声明式增量持久化与 Schema 演化，两库相互提供反馈。

## 当前实现锚点

- **Kernel**：统一 Occurrence 仲裁；facts 经 scratch-fold 和验证后 `AppendBatch`，再安装世界。见 [SimulationKernel](src/Kernel/Simulation/SimulationKernel.cs)、[Kernel 设计](docs/design/simulation-kernel.md)与[已实施基线](docs/implementation/kernel-occurrence-baseline.md)。
- **Spatial**：Graph Slice 1/2 已实现，FirstBoard 已组合 Game + Spatial；未发现旧 Grid API 消费者。见 [GraphSpatialState](src/Spatial/State/GraphSpatialState.cs)、[FirstBoardWorld / FirstBoardReducer](src/FirstBoard/FirstBoardDomain.cs)、[Graph Spatial 设计](docs/design/graph-spatial-world.md)。Spatial 只依赖 Kernel；客观位置与运动归 Spatial，玩法回应和认知归消费者。
- **运行与存储**：[LiveSession](src/FirstBoard.Demo/Live/LiveSession.cs) 使用内存 Journal；[Journal.Atelia](src/Journal.Atelia/AteliaJournalSink.cs) 与 [FirstBoardPersistenceTests](tests/FirstBoard.Persistence.Tests/FirstBoardPersistenceTests.cs) 保有 EventJournal 路径。StateJournalNative 已退出默认源码与依赖，原实验见[归档索引](docs/archive/statejournal-native.md)。
- **DurableGraph**：静态核对至 `f68388f`，EventHistory 的独立 E/S、PendingEvent、分支、同类型根替换已具备，上手/恢复及 ReadPair 反馈已落地；DramaBoard 尚未接入。单 writer/单活动 session；record class、接口集合仍需适配。入口见兄弟库 [README](../durable-graph/README.md)，反馈状态见[消费者反馈](docs/feedback/durablegraph/README.md)。

## 当前焦点与建议下一步

用户已授权按[真实接入计划](docs/worksets/durablegraph-first-integration.md)与[Occurrence 持久化方案](docs/design/durablegraph-occurrence-persistence.md)自主带队实施，直到难以解决的问题或上游能力缺口。必须保留三个核心：迭代预测最近单个事件的时间模式、Human/LLM Player 统一被调度唤醒决策、面向 LLM 理解与导航移动的 Graph 空间；其余按实现证据调整。

本批原地适配领域模型、局部替换 Kernel 发布/恢复接缝，保留调度与 Graph Spatial 算法；首片继续纯 fold。E 在 Plan 与 scratch 验证后记录完整变化，热路径复用 scratch，冷 E-head 只完成 pending，不重问 Player；S 包含完整世界与 Kernel 游标（含 LastCauseKey）。当前正在冻结接口、准备真实 oracle 与干净固定依赖，尚未完成代码接入。

实施顺序：真实 encounter 的完整 oracle/包消费/冷恢复 → 实际运行入口与明确的 Player 恢复边界 → 迁移行为测试并退出旧持久路径 → 真实升版与成本反馈。详细任务及验收只维护在计划中。旧 preflight 已转为后继导航，两份目标设计的 AppendBatch/replay 条款随代码切换定向修订。

## 长期 roadmap

| 用户目标 | 当前落点与后续方向 |
|---|---|
| 归档 StateJournalNative | 已完成；只保留[经验与恢复入口](docs/archive/statejournal-native.md)，旧实验不再日常维护。 |
| 用 DurableGraph 持久化 DramaBoard | 待首个切片；验证领域模型与提交边界后，逐步扩展到运行入口和完整续局状态。 |
| 向 DurableGraph 提供反馈 | 持续入口为[消费者反馈](docs/feedback/durablegraph/README.md)；DB-065/066 已落地并静态复核，真实游戏消费随接入验证。区分领域建模问题、API 摩擦和功能缺口。 |
| 完成 Graph Spatial 方向 | 库及 FirstBoard 已实质切换；下一步由具体玩法选择缺失能力，按 008 的重开条件推进。 |
| 整理 docs 与文件名 | 活跃 Kernel/Spatial/Save/研究主题已分类改名，历史正文退出默认检索；剩余早期混合设计随真实任务按需提炼。 |

## 焦点问题与延期条件

- **分支与续局**：上游 branch/ref、E/S head 恢复已有验证；DramaBoard 的可玩 fork 仍须选完整 S 并落实新 lineage 的持久化语义。完整 Player closure、倒带 UI 与首片实际范围继续分别裁决。
- **事件与处理结果**：Journal 保持 S→E→S 和唯一 ref；ReadEvent/ReadState 独立，Resume 可便利地读取准确配对但不合并实例。State 不必嵌最近 Event，异常/retry 历史继续延期。
- **快照与领域适配**：事件保留旧快照，闭包小是建模目标；文档/XML doc 明确可变别名和大图回指风险，关联当前实体走领域身份而非跨图 ReferenceEquals。各图内部仍保持真实共享/循环，完整 State 仍覆盖 Game+Spatial+Kernel。
- **API 反馈与历史读取**：[反馈目录](docs/feedback/durablegraph/README.md)集中维护处理状态。跨重开定位和局部浏览按真实需求触发，不阻塞首片；ReadPair 不是可编辑恢复入口，也不是接入前置条件。
- **Player 与外部调用的恢复边界？** 先明确客观世界切片，再逐项确定记忆、叙事记录和 Player 状态如何同世界对齐。DurableGraph 不提供 Task、LLM 调用或执行栈的透明恢复。
- **跨项目会话协作**：用户正在完善 Codex MCP；本批准备工作独立推进。接口就绪后试用固定项目会话的咨询、追问、结果读取、内部子代理与重启恢复；双方维护各自 PROJECT-STATE，交换具体需求、证据与结论。
- **Spatial 扩展**：调速/途中停留、Area、ViewLink、关系变化等按真实玩法触发；contact 索引按性能证据触发，详见 008。旧 Grid 留作历史证据，当前无恢复双实现的需求。
- **持久 Script VM**：保留[研究章程](docs/research/persistent-script-vm-selection.md)；需要持久执行状态时再讨论，与本次对象图接入分别裁决。

## 按需导航与验证

- Demo 使用入口：[README](README.md)。常规项目见 [DramaBoard.slnx](DramaBoard.slnx)；含 Atelia 持久化的完整集合见 [DramaBoard.Local.slnx](DramaBoard.Local.slnx)。当前 [CI](.github/workflows/ci.yml) 使用 Local solution 和固定 Atelia 提交。
- `AteliaRepositoryRoot` 默认在根 props 统一指向兄弟 `../atelia`，支持环境变量或 `-p:` 覆盖。完整基线用 CI 固定版本的独立 checkout，避免依赖兄弟库正在进行的未提交开发；复现命令见批次记录。
- 文档分类入口：[docs/README](docs/README.md)。旧实验取舍与恢复见[StateJournalNative 归档索引](docs/archive/statejournal-native.md)；其他旧材料见[历史索引](docs/archive/README.md)，均不作为当前待办或测试基线。
