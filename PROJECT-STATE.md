# DramaBoard 当前项目状态

> 跨会话入口；维护方式见 [AGENTS.md](AGENTS.md)。按本文件选择下一处阅读，不必顺读历史记录。
> 最近整理：2026-09-16；用户确定先归档旧实验并独立验证核心，再设计无剧情 free-play。当前已撰写[归档设计与实施交接](docs/build-log/0024-archive-firstboard-and-verify-core.md)，归档与本轮核心验收尚未执行。

## 最终目标

探索 LLM-Native 的开放世界游戏：结合角色扮演、模拟养成、棋类与 RL Gym 的需求，用真实可玩场景逐步选择机制。
让领域世界能够持续演化、保存和恢复，同时保持领域代码易写、空间行为可解释、时间推进确定。
DramaBoard 作为 DurableGraph 的真实消费者，用游戏需求检验声明式增量持久化与 Schema 演化，两库相互提供反馈。

## 当前实现锚点

- **Kernel**：统一 Occurrence 仲裁；scratch-fold 后发布独立 E/S，S 成功才安装世界；有限 [KernelCursor](src/Kernel/Simulation/KernelCursor.cs) 支持仅完成 pending 的恢复。见 [SimulationKernel](src/Kernel/Simulation/SimulationKernel.cs)与[提交方案](docs/design/durablegraph-occurrence-persistence.md)。旧 occurrence baseline 的时间/仲裁部分保留，Journal 接缝已被替换。
- **Spatial**：Graph Slice 1/2 已实现；contact 按 floor 提前开放交互，arrival 保持 ceil，保留严格内部掉头及 current-segment 配对消费。FirstBoard 已组合 Game + Spatial；见[接触时间边界与证据](docs/worksets/passage-contact-floor.md)、[GraphSpatialState](src/Spatial/State/GraphSpatialState.cs)、[FirstBoardWorld / FirstBoardReducer](src/FirstBoard/FirstBoardDomain.cs)。领域依赖仍只有 Kernel，另引用 DG Runtime；客观运动归 Spatial，回应与认知归消费者。
- **运行与存储**：[FirstBoardOccurrenceHistory](src/FirstBoard/Persistence/FirstBoardOccurrenceHistory.cs) 保存完整世界、游标与精确内容/规则绑定；[LiveSession](src/FirstBoard.Demo/Live/LiveSession.cs)已接创建/续局入口，并明确重新建立 Player 记忆/预算。[真实存储](tests/FirstBoard.Persistence.Tests/FirstBoardPersistenceTests.cs)与[冷进程](tests/FirstBoard.Persistence.Tests/ColdProcessTests.cs)已有验收。旧 Journal.Atelia 已退役，历史可从 Git `3b77450` 恢复。
- **DurableGraph**：固定包已更新至 `4cea773`（DB-071），适配 Persistence / Storage / Runtime 命名空间；来源见[包准备说明](docs/worksets/durablegraph-package-source.md)，试用与兼容证据见[反馈 004](docs/feedback/durablegraph/004-db071-namespace-adaptation.md)。三库模型仍通过 `IDurableObject` 声明 durable；单 writer/单活动 session。

## 当前焦点与下一步

用户已确定路线：**归档减负 → 核心独立验证 → 无剧情 free-play → Human 入口 → 逐项增加互动机制**。保留迭代预测最近单个事件的时间模式、Human/LLM Player 统一被调度唤醒决策、面向 LLM 理解与导航移动的 Graph 空间；暂时剥离 FirstBoard、FirstBoard.Demo、Player.Llm 及其专属材料。

当前交接：[Build Log 0024](docs/build-log/0024-archive-firstboard-and-verify-core.md)覆盖归档清单、核心保留边界、G0—G3 与 A1—A7 验收。用户由其他 Coding Agent 会话调度实施；本设计会话只撰写文档。源码与 solution 仍保持归档前状态，不能提前声称已经只剩核心。

本批验收后再设计首个移动闭环：Human 发出移动决策，系统维护时间与位置，界面呈现这些信息、Player 已知地图和自身历史轨迹。探索披露、轨迹模型、UI 技术与跨进程保存范围尚未裁决，不进入归档实施。业务模型升级、Player 记忆对齐与性能改写改由后续真实玩法重新触发。

## 长期 roadmap

| 用户目标 | 当前落点与后续方向 |
|---|---|
| 归档 StateJournalNative | 已完成；只保留[经验与恢复入口](docs/archive/statejournal-native.md)，旧实验不再日常维护。 |
| 建立干净的 free-play 起点 | 先执行[旧实验归档与核心验证](docs/build-log/0024-archive-firstboard-and-verify-core.md)，验收后再做无剧情的移动、地图与轨迹闭环；LLM Player 开发暂缓。 |
| 用 DurableGraph 持久化 DramaBoard | 旧 FirstBoard 已有完整世界与 pending 冷恢复证据；归档后保留核心 E/S 合同，新消费者落盘按实际需要另行设计。 |
| 向 DurableGraph 提供反馈 | 持续入口为[消费者反馈](docs/feedback/durablegraph/README.md)；DB-065/066 已落地并静态复核，真实游戏消费随接入验证。区分领域建模问题、API 摩擦和功能缺口。 |
| 完成 Graph Spatial 方向 | 库及 FirstBoard 已实质切换；下一步由具体玩法选择缺失能力，按 008 的重开条件推进。 |
| 整理 docs 与文件名 | 活跃 Kernel/Spatial/Save/研究主题已分类改名，历史正文退出默认检索；剩余早期混合设计随真实任务按需提炼。 |

## 焦点问题与延期条件

- **分支与续局**：本批可玩持久 fork、倒带 UI 和完整 Player closure 延期；原 encounter 分支调度资产迁入[内存边界测试](tests/FirstBoard.Tests/OccurrenceBranchSemanticsTests.cs)。真实需求触发时再落实新 lineage 与 Player 同步语义。
- **事件与处理结果**：Journal 保持 S→E→S 和唯一 ref；ReadEvent/ReadState 独立，Resume 可便利地读取准确配对但不合并实例。State 不必嵌最近 Event，异常/retry 历史继续延期。
- **规则版本**：FirstBoard 当前 `/3` 使用 floor contact；旧 `/2` S/E-head 明确拒绝续跑，不自动转换。领域 Schema 未变化；新旧业务规则不可仅因对象图可读就混用。
- **快照与领域适配**：事件保留旧快照，闭包小是建模目标；文档/XML doc 明确可变别名和大图回指风险，关联当前实体走领域身份而非跨图 ReferenceEquals。各图内部仍保持真实共享/循环，完整 State 仍覆盖 Game+Spatial+Kernel。
- **API 反馈与历史读取**：[反馈目录](docs/feedback/durablegraph/README.md)集中维护处理状态。跨重开定位和局部浏览按真实需求触发，不阻塞首片；ReadPair 不是可编辑恢复入口，也不是接入前置条件。
- **Player 与外部调用的恢复边界**：现有世界续局明确重建 Player 记忆/预算；完整记忆、叙事记录与世界对齐待真实交互需求。DurableGraph 不提供 Task、LLM 调用或执行栈的透明恢复。
- **跨项目会话协作**：用户正在完善 Codex MCP；本批准备工作独立推进。接口就绪后试用固定项目会话的咨询、追问、结果读取、内部子代理与重启恢复；双方维护各自 PROJECT-STATE，交换具体需求、证据与结论。
- **Spatial 扩展**：调速/途中停留、Area、ViewLink、关系变化等按真实玩法触发；contact 索引按性能证据触发，详见 008。旧 Grid 留作历史证据，当前无恢复双实现的需求。
- **持久 Script VM**：保留[研究章程](docs/research/persistent-script-vm-selection.md)；需要持久执行状态时再讨论，与本次对象图接入分别裁决。

## 按需导航与验证

- Demo 使用入口：[README](README.md)。[DramaBoard.slnx](DramaBoard.slnx)与保留的[Local 入口](DramaBoard.Local.slnx)均包含持久化测试；源码不再 ProjectReference 兄弟 Atelia。先运行[固定包准备脚本](scripts/Prepare-DurableGraph.ps1)，再构建/测试；[CI](.github/workflows/ci.yml)同样准备固定包并验证已提交 schema history。
- 文档分类入口：[docs/README](docs/README.md)。旧实验取舍与恢复见[StateJournalNative 归档索引](docs/archive/statejournal-native.md)；其他旧材料见[历史索引](docs/archive/README.md)，均不作为当前待办或测试基线。
