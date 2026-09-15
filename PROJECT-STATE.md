# DramaBoard 当前项目状态

> 跨会话入口；维护方式见 [AGENTS.md](AGENTS.md)。按本文件选择下一处阅读，不必顺读历史记录。
> 最近整理：2026-09-16；FirstBoard/LLM 冷归档与核心独立验证已完成。归档范围和 A1—A7 证据见 [Build Log 0024](docs/build-log/0024-archive-firstboard-and-verify-core.md)。

## 最终目标

探索 LLM-Native 的开放世界游戏：用真实可玩场景逐步选择角色、模拟、棋类与 RL Gym 的机制。领域世界应可持续演化，同时保持时间推进确定、空间行为可解释、领域代码易写。DramaBoard 作为 DurableGraph 的真实消费者，持续反馈声明式增量持久化与 Schema 演化的需求。

## 当前实现锚点

- **Kernel**：统一 Occurrence 仲裁；scratch-fold 后发布独立 E/S，S 成功才安装世界；有限 [KernelCursor](src/Kernel/Simulation/KernelCursor.cs) 只支持完成已记录 pending。见 [SimulationKernel](src/Kernel/Simulation/SimulationKernel.cs) 与 [E/S 设计](docs/design/durablegraph-occurrence-persistence.md)。
- **Spatial**：Graph Slice 1/2 已实现；contact 使用 floor，arrival 使用 ceil，保留严格内部掉头与 current-segment 配对消费。见 [GraphSpatialState](src/Spatial/State/GraphSpatialState.cs)、[Graph 设计](docs/design/graph-spatial-world.md) 和 [contact 证据](docs/worksets/passage-contact-floor.md)。
- **Player 与协议**：冻结的 Observation、DecisionRequest、Intent 和稳定身份由 [Protocol](src/Protocol) 持有；[Player](src/Player) 提供 Null/Scripted/Random driver，Decision.Validation 负责请求关联和动作校验，Player.Agency 提供已知图快照接缝。
- **构建依赖**：Kernel/Spatial 使用固定 DurableGraph 包；来源和复现规则见 [包准备说明](docs/worksets/durablegraph-package-source.md)。已提交的 Kernel/Spatial schema history 不因本批改变。

## 当前焦点与下一步

活跃核心固定为 Kernel、Spatial、Protocol、Player、Decision.Validation、Host、Player.Agency 及各自测试。FirstBoard 场景、Demo、LLM Player、实际持久化 adapter 和专属资料均已移至 [归档索引](docs/archive/firstboard-llm.md)，不参与默认编译或搜索。

下一步才设计无剧情 free-play 的第一个移动闭环：Human 发出移动决策，系统呈现时间、位置、已知地图和自身轨迹。探索披露、轨迹模型、UI 技术与跨进程保存范围仍未裁决。

## 延期与重开条件

- **自由玩法模型**：探索披露、轨迹模型、UI 技术与跨进程保存范围由首个移动闭环设计裁决，本批不预设。
- **持久化消费者**：核心保留 E/S 接缝和内存合同；新场景需要落盘时，从其真实闭包重新设计 adapter、恢复与 Player 同步边界。归档 FirstBoard 的结果只是历史证据。
- **分支、续局与外部调用**：可玩 fork/倒带、完整 Player closure、LLM 调用恢复、异常/retry 历史按真实需求重开；DurableGraph 不透明恢复 Task、LLM 调用或执行栈。
- **Spatial 扩展**：调速/途中停留、Area、ViewLink、关系变化与 contact 索引按实际玩法和性能证据触发。
- **持久 Script VM**：研究材料已归档；只有需要持久执行状态时才重新选择方案。

## 按需导航与验证

- 两份等价入口：[DramaBoard.slnx](DramaBoard.slnx) 与 [DramaBoard.Local.slnx](DramaBoard.Local.slnx)。先运行 [Prepare-DurableGraph.ps1](scripts/Prepare-DurableGraph.ps1)，再按 Build Log 0024 进行串行构建和测试；[CI](.github/workflows/ci.yml)同样准备固定包并验证 schema history。
- 文档入口：[docs/README](docs/README.md)。FirstBoard/LLM 的恢复路径、清单与原始基线在 [归档索引](docs/archive/firstboard-llm.md)；其正文必须显式 `rg --no-ignore` 查阅，不能作为当前待办或测试基线。
