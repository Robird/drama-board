# DramaBoard 当前项目状态

> 跨会话入口；维护方式见 [AGENTS.md](AGENTS.md)。按本文件选择下一处阅读，不必顺读历史记录。
> 最近整理：2026-10-27；时空核心独立化与 DurableGraph 升级均已验收。施工范围与验收见[两步 TODO / Task Brief](docs/implementation/spacetime-engine-todo.md)。

## 最终目标

探索 LLM-Native 的开放世界游戏：用真实可玩场景逐步选择角色、模拟、棋类与 RL Gym 的机制。领域世界应可持续演化，同时保持时间推进确定、空间行为可解释、领域代码易写。DramaBoard 作为 DurableGraph 的真实消费者，持续反馈声明式增量持久化与 Schema 演化的需求。

## 当前实现锚点

- **Kernel**：统一 Occurrence 仲裁；scratch-fold 后发布独立 E/S，S 成功才安装世界；有限 [KernelCursor](src/Kernel/Simulation/KernelCursor.cs) 只支持完成已记录 pending。见 [SimulationKernel](src/Kernel/Simulation/SimulationKernel.cs) 与 [E/S 设计](docs/design/durablegraph-occurrence-persistence.md)。
- **Spatial**：Graph Slice 1/2 已实现；contact 使用 floor，arrival 使用 ceil，保留严格内部掉头与 current-segment 配对消费。见 [GraphSpatialState](src/Spatial/State/GraphSpatialState.cs)、[Graph 设计](docs/design/graph-spatial-world.md) 和 [contact 证据](docs/worksets/passage-contact-floor.md)。
- **Host**：通用 [SimulationHost](src/Host/SimulationHost.cs) 循环驱动 Kernel，保留在目标核心中。
- **构建依赖**：Kernel/Spatial 直接引用相邻 DurableGraph 当前源码，同时接入 Generator 与 schema Build 工具；Spatial.Tests 的 Persistence 也来自同一源码图。Storage 沿用 DG 的 revision 校验，验收显式运行 Schema Verify。源码根与使用边界见[源码依赖说明](docs/source-dependencies.md)。

## 当前焦点与下一步

用户已确认将时空引擎作为当前主线，角色可维护 Plan 与交互/呈现框架保留为技术储备。[两步 TODO / Task Brief](docs/implementation/spacetime-engine-todo.md)已完成，当前无剩余施工待办。后续按真实消费者需求选择增量；MUD 接合需另行讨论，不自动进入实施。

当前活跃范围已收缩为 Kernel、Spatial、Host；其余应用层采用固定 tag 与[仓内归档](docs/archive/player-runtime.md)保存，退出默认构建和搜索，独立场景与新旧依赖对照验收已通过。独立库先留在本仓库，不改变时空与调度语义。

Dynamic-Programmer R1–R8 的既有实现、验收、Player 运行时理念与长期 Plan 设计统一保留在[Player 归档](docs/archive/player-runtime.md)，不驱动当前施工。

FirstBoard 场景、Demo、LLM Player、实际持久化 adapter 和专属资料此前已移至[归档索引](docs/archive/firstboard-llm.md)，不参与默认编译或搜索。

## 延期与重开条件

- **MUD 接入**：完成两步并验收后，结合 `durable-graph-mud` 最新状态另行评估；当前不实施，也不预定其时空与持久化接合方案。
- **角色规划与交互**：长期 Plan、主动唤醒、LLM Programmer、探索披露、途中操作及呈现机制暂缓；获得新的设计证据或明确消费者需求时重开，冻结部分不要求随核心编译。
- **持久化消费者**：核心保留 E/S 接缝和内存合同；新场景需要落盘时，从其真实闭包重新设计 adapter、恢复与 Player 同步边界。归档 FirstBoard 的结果只是历史证据。
- **分支、续局与外部调用**：可玩 fork/倒带、完整 Player closure、LLM 调用恢复、异常/retry 历史按真实需求重开；DurableGraph 不透明恢复 Task、LLM 调用或执行栈。
- **Spatial 扩展**：调速/途中停留、Area、ViewLink、关系变化与 contact 索引按实际玩法和性能证据触发。
- **持久 Script VM**：研究材料已归档；只有需要持久执行状态时才重新选择方案。

## 按需导航与验证

- 两份等价入口 [DramaBoard.slnx](DramaBoard.slnx) 与 [DramaBoard.Local.slnx](DramaBoard.Local.slnx) 只含三核心及其测试。[README](README.md)与 [CI](.github/workflows/ci.yml)直接构建源码依赖，使用精确 SDK 10.0.201；第三方包来自 nuget.org，保留仓内包缓存。旧包准备脚本仅保留历史复现用途，当前不要求发包。
- 文档入口：[docs/README](docs/README.md)。FirstBoard/LLM 的恢复路径、清单与原始基线在 [归档索引](docs/archive/firstboard-llm.md)；其正文必须显式 `rg --no-ignore` 查阅，不能作为当前待办或测试基线。
