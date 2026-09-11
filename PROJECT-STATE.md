# DramaBoard 当前项目状态

> 跨会话入口；维护方式见 [AGENTS.md](AGENTS.md)。按本文件选择下一处阅读，不必顺读历史记录。
> 最近整理：2026-09-11；接入前准备批次从 `bd73e64` 开始，验证证据与范围见[批次记录](docs/worksets/pre-integration-cleanup.md)。兄弟库能力在接入前重新核对。

## 最终目标

探索 LLM-Native 的开放世界游戏：结合角色扮演、模拟养成、棋类与 RL Gym 的需求，用真实可玩场景逐步选择机制。
让领域世界能够持续演化、保存和恢复，同时保持领域代码易写、空间行为可解释、时间推进确定。
DramaBoard 作为 DurableGraph 的真实消费者，用游戏需求检验声明式增量持久化与 Schema 演化，两库相互提供反馈。

## 当前实现锚点

- **Kernel**：统一 Occurrence 仲裁；facts 经 scratch-fold 和验证后 `AppendBatch`，再安装世界。见 [SimulationKernel](src/Kernel/Simulation/SimulationKernel.cs)、[Kernel 设计](docs/design/simulation-kernel.md)与[已实施基线](docs/implementation/kernel-occurrence-baseline.md)。
- **Spatial**：Graph Slice 1/2 已实现，FirstBoard 已组合 Game + Spatial；未发现旧 Grid API 消费者。见 [GraphSpatialState](src/Spatial/State/GraphSpatialState.cs)、[FirstBoardWorld / FirstBoardReducer](src/FirstBoard/FirstBoardDomain.cs)、[Graph Spatial 设计](docs/design/graph-spatial-world.md)。Spatial 只依赖 Kernel；客观位置与运动归 Spatial，玩法回应和认知归消费者。
- **运行与存储**：[LiveSession](src/FirstBoard.Demo/Live/LiveSession.cs) 使用内存 Journal；[Journal.Atelia](src/Journal.Atelia/AteliaJournalSink.cs) 与 [FirstBoardPersistenceTests](tests/FirstBoard.Persistence.Tests/FirstBoardPersistenceTests.cs) 保有 EventJournal 路径。StateJournalNative 已退出默认源码与依赖，原实验见[归档索引](docs/archive/statejournal-native.md)。
- **DurableGraph**：已支持声明模型、增量 Commit、重开、引用身份恢复和显式升级；DramaBoard 尚未接入。当前单 head、单会话，无 branch/Reset；record class、接口集合等现有模型形状不能直接照搬。每次接入前核对兄弟仓库的 [README](../durable-graph/README.md) 和 [产品状态](../durable-graph/src/PROJECT-STATE.md)。

## 当前焦点与建议下一步

用户已授权的[接入前准备批次](docs/worksets/pre-integration-cleanup.md)已交付。两层设计已选择双根（可用固定 PairRoot）、与 Journal 对应的线性 Revision Parent，以及事件引用不随后续处理变化的领域快照。唯一[可编辑草稿](docs/research/event-journal-state-store-draft.md)维护使用规则与证据；当前完善可交给 DurableGraph 的方案，具体 API/实现分片未冻结，本轮仅修订文档。

1. 下一设计落点：按草稿的 PairRoot + World + Event 快照最小见证，具体化声明、Journal 接管发布/恢复的 API 与验证分片；不再建设兄弟 Revision 的合并身份机制。
2. DramaBoard 可独立从 PassageEncounterHostTests 提取前缀/纯 driver，补足 NextPersistentId 与 KnownFacts.Text 等完整边界对照；上游基础机制成立后接真实保存/重开。
3. 接入后验证一次模型升版，汇总声明、快照构造及实际写入成本；完整 Player closure 与倒带 UI 继续分别裁决。

## 长期 roadmap

| 用户目标 | 当前落点与后续方向 |
|---|---|
| 归档 StateJournalNative | 已完成；只保留[经验与恢复入口](docs/archive/statejournal-native.md)，旧实验不再日常维护。 |
| 用 DurableGraph 持久化 DramaBoard | 待首个切片；验证领域模型与提交边界后，逐步扩展到运行入口和完整续局状态。 |
| 向 DurableGraph 提供反馈 | 与接入同步；用最小复现区分领域建模问题、API 摩擦和功能缺口，再选择改动归属。 |
| 完成 Graph Spatial 方向 | 库及 FirstBoard 已实质切换；下一步由具体玩法选择缺失能力，按 008 的重开条件推进。 |
| 整理 docs 与文件名 | 活跃 Kernel/Spatial/Save/研究主题已分类改名，历史正文退出默认检索；剩余早期混合设计随真实任务按需提炼。 |

## 焦点问题与延期条件

- **分支与续局**：branch/ref 已进入用户两层候选；需验证 E/S 两种 head 的恢复与分支间对象隔离。完整 Player closure、倒带 UI 与首片实际范围继续分别裁决。
- **事件与处理结果**：统一 PairRoot.State/Event 槽随 S→E→S 交替更新，只有 Journal ref 发布前沿；事件语义由领域定义，异常/retry 历史继续延期。
- **快照与领域适配**：事件保留旧快照，替换式 reducer 方向可继续；需核对集合/元素别名及 DG 支持的声明形状，不强制稳定可变实体或全量深拷贝。Game + Spatial、逻辑时间与候选消费状态仍须一致恢复；根封套不自动串起历史。
- **Player 与外部调用的恢复边界？** 先明确客观世界切片，再逐项确定记忆、叙事记录和 Player 状态如何同世界对齐。DurableGraph 不提供 Task、LLM 调用或执行栈的透明恢复。
- **跨项目会话协作**：用户正在完善 Codex MCP；本批准备工作独立推进。接口就绪后试用固定项目会话的咨询、追问、结果读取、内部子代理与重启恢复；双方维护各自 PROJECT-STATE，交换具体需求、证据与结论。
- **Spatial 扩展**：调速/途中停留、Area、ViewLink、关系变化等按真实玩法触发；contact 索引按性能证据触发，详见 008。旧 Grid 留作历史证据，当前无恢复双实现的需求。
- **持久 Script VM**：保留[研究章程](docs/research/persistent-script-vm-selection.md)；需要持久执行状态时再讨论，与本次对象图接入分别裁决。

## 按需导航与验证

- Demo 使用入口：[README](README.md)。常规项目见 [DramaBoard.slnx](DramaBoard.slnx)；含 Atelia 持久化的完整集合见 [DramaBoard.Local.slnx](DramaBoard.Local.slnx)。当前 [CI](.github/workflows/ci.yml) 使用 Local solution 和固定 Atelia 提交。
- `AteliaRepositoryRoot` 默认在根 props 统一指向兄弟 `../atelia`，支持环境变量或 `-p:` 覆盖。完整基线用 CI 固定版本的独立 checkout，避免依赖兄弟库正在进行的未提交开发；复现命令见批次记录。
- 文档分类入口：[docs/README](docs/README.md)。旧实验取舍与恢复见[StateJournalNative 归档索引](docs/archive/statejournal-native.md)；其他旧材料见[历史索引](docs/archive/README.md)，均不作为当前待办或测试基线。
