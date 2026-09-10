# DramaBoard 当前项目状态

> 跨会话入口；维护方式见 [AGENTS.md](AGENTS.md)。按本文件选择下一处阅读，不必顺读历史记录。
> 最近核对：2026-09-10；DramaBoard 源码基线 `f7391dc`，DurableGraph `2fbcca4`。本次依据源码与文档，未重跑构建或测试。

## 最终目标

探索 LLM-Native 的开放世界游戏：结合角色扮演、模拟养成、棋类与 RL Gym 的需求，用真实可玩场景逐步选择机制。
让领域世界能够持续演化、保存和恢复，同时保持领域代码易写、空间行为可解释、时间推进确定。
DramaBoard 作为 DurableGraph 的真实消费者，用游戏需求检验声明式增量持久化与 Schema 演化，两库相互提供反馈。

## 当前实现锚点

- **Kernel**：统一 Occurrence 仲裁；facts 经 scratch-fold 和验证后 `AppendBatch`，再安装世界。见 [SimulationKernel](src/Kernel/Simulation/SimulationKernel.cs)；设计意图见 [003](docs/开放世界棋盘游戏设计_003_Forecast_Elapse_Decide_SimulationKernel.md)，已实施基线见 [计划 006](docs/研发计划_006_统一原子Occurrence与LogicalInstant_Kernel重构计划.md)。
- **Spatial**：Graph Slice 1/2 已实现，FirstBoard 已组合 Game + Spatial；未发现旧 Grid API 消费者。见 [GraphSpatialState](src/Spatial/State/GraphSpatialState.cs)、[FirstBoardWorld / FirstBoardReducer](src/FirstBoard/FirstBoardDomain.cs)、[008](docs/开放世界棋盘游戏设计_008_Graph_Spatial_World.md)。Spatial 只依赖 Kernel；客观位置与运动归 Spatial，玩法回应和认知归消费者。
- **运行与存储**：[LiveSession](src/FirstBoard.Demo/Live/LiveSession.cs) 使用内存 Journal；[Journal.Atelia](src/Journal.Atelia/AteliaJournalSink.cs) 与 [FirstBoardPersistenceTests](tests/FirstBoard.Persistence.Tests/FirstBoardPersistenceTests.cs) 保有 EventJournal 路径。[StateJournalNative](tests/FirstBoard.Persistence.Tests/StateJournalNative) 仍是待归档的测试实验，未接管生产运行入口。
- **DurableGraph**：已支持声明模型、增量 Commit、重开、引用身份恢复和显式升级；DramaBoard 尚未接入。当前单 head、单会话，无 branch/Reset；record class、接口集合等现有模型形状不能直接照搬。每次接入前核对兄弟仓库的 [README](../durable-graph/README.md) 和 [产品状态](../durable-graph/src/PROJECT-STATE.md)。

## 当前焦点与建议下一步

用户已明确下表的五项目标，并授权自主维护本文件。当前处于路线讨论，首个持久化切片的模型、提交边界和验收范围尚未裁决。

建议先做小范围归档与入口整理，再以一个真实玩法切片试用 DurableGraph；按反馈扩大接入。具体推进顺序仍可修订：

1. 确定旧实验的归档单位与证据保留方式：StateJournalNative + build-log 0021/0022；保留独立的 EventJournal 回归。验收应同时覆盖默认编译、常规检索退出和历史可找回。
2. 收敛首个 DurableGraph 切片：候选为两个角色沿 Passage 移动、发生一次接触或入口变化，覆盖 Game + Spatial 的一次原子变化。先用确定性玩家；这只是候选场景。
3. 为选定切片写清问题、模型边界和最小验收，再实施：保存、退出、重开、继续行为一致，并验证一次模型升版；记录接入需要的手工代码与功能缺口。

## 长期 roadmap

| 用户目标 | 当前落点与后续方向 |
|---|---|
| 归档 StateJournalNative | 待执行；提取仍有效的恢复/同提交/失败处理经验，保存历史入口，减少维护和搜索噪声。 |
| 用 DurableGraph 持久化 DramaBoard | 待首个切片；验证领域模型与提交边界后，逐步扩展到运行入口和完整续局状态。 |
| 向 DurableGraph 提供反馈 | 与接入同步；用最小复现区分领域建模问题、API 摩擦和功能缺口，再选择改动归属。 |
| 完成 Graph Spatial 方向 | 库及 FirstBoard 已实质切换；下一步由具体玩法选择缺失能力，按 008 的重开条件推进。 |
| 整理 docs 与文件名 | 当前入口已统一；后续分类设计、研究和历史材料，采用稳定主题命名，迁移时修复引用。 |

## 焦点问题与延期条件

- **首个可用存档是否必须倒带/分叉？** 旧实验重视这两项；本次用户尚未确认首轮优先级。答案决定是否需要先扩展 DurableGraph 的历史访问能力。
- **谁持有权威状态、谁提交？** 现有不可变 record + reducer/Journal 与 DurableGraph 的持久对象身份需要明确衔接。不要把“替换 adapter”或“重写为可变领域图”预先记成裁决；Game + Spatial、逻辑时间与候选消费状态必须一致恢复。
- **Player 与外部调用的恢复边界？** 先明确客观世界切片，再逐项确定记忆、叙事记录和 Player 状态如何同世界对齐。DurableGraph 不提供 Task、LLM 调用或执行栈的透明恢复。
- **Spatial 扩展**：调速/途中停留、Area、ViewLink、关系变化等按真实玩法触发；contact 索引按性能证据触发，详见 008。旧 Grid 留作历史证据，当前无恢复双实现的需求。
- **持久 Script VM**：保留 [0023 研究章程](docs/build-log/0023-persistent-script-vm-selection.md)；需要持久执行状态时再讨论，与本次对象图接入分别裁决。

## 按需导航与验证

- Demo 使用入口：[README](README.md)。常规项目见 [DramaBoard.slnx](DramaBoard.slnx)；含 Atelia 持久化的完整集合见 [DramaBoard.Local.slnx](DramaBoard.Local.slnx)。当前 [CI](.github/workflows/ci.yml) 使用 Local solution 和固定 Atelia 提交。
- 本机默认 `AteliaRepositoryRoot` 仍按旧目录布局计算，落到不存在的 `Atelia-org/Atelia-org/atelia`；后续相关验证需显式指定正确路径或修正默认值。完整基线验证还须核对 CI 固定的依赖版本。
- 旧实验取舍与证据：[0021](docs/build-log/0021-statejournal-native-vertical-probe.md)、[0022](docs/build-log/0022-statejournal-native-objective-probe-results.md)。按问题阅读，文中的“当前裁决”属于当时实验阶段。
- [旧工作包表](docs/研发计划_002_工作包分解.md) 与 [旧交接快照](agent-checkpoint/memory-notebook.md) 已退出当前状态维护；其待办、测试数量和自主循环提示保留为历史资料。
