# DramaBoard 当前项目状态

> 跨会话入口；维护方式见 [AGENTS.md](AGENTS.md)。按本文件选择下一处阅读，不必顺读历史记录。
> 最近整理：2026-10-27；局部预测重构的空间首阶段 S1/S2 已实施，下一步是协调 ResourceCycle 的 S3 集成；其多角色化由另一会话并行推进。见[实施方案](docs/design/local-prediction-refactor.md)与[验收记录](docs/experiments/local-prediction-acceptance.md)。

## 最终目标

探索 LLM-Native 的开放世界游戏：用真实可玩场景逐步选择角色、模拟、棋类与 RL Gym 的机制。领域世界应可持续演化，同时保持时间推进确定、空间行为可解释、领域代码易写。DramaBoard 作为 DurableGraph 的真实消费者，持续反馈声明式增量持久化与 Schema 演化的需求。

## 当前实现锚点

- **Kernel**：统一 Occurrence 仲裁；Plan 后显式 Complete，再完整 Fold/带时间 Validate，发布独立 E/S，S 成功才安装；pending 仅 Fold 保存结果。见 [SimulationKernel](src/Kernel/Simulation/SimulationKernel.cs) 与 [E/S 设计](docs/design/durablegraph-occurrence-persistence.md)。
- **Spatial**：[SpatialSimulation](src/Spatial/Simulation/SpatialSimulation.cs) 维护 dirty 通道，Business/Contacts 同根发布；候选只读投影，genesis 显式发现，恢复不重算。保留 contact floor、arrival ceil、严格内部掉头与 current-segment 消费；见[验收记录](docs/experiments/local-prediction-acceptance.md)和 [Graph 设计](docs/design/graph-spatial-world.md)。
- **Host**：通用 [SimulationHost](src/Host/SimulationHost.cs) 循环驱动 Kernel，保留在目标核心中。
- **ResourceCycle 应用实例**：[CycleEngine](src/ResourceCycle/CycleEngine.cs) 依赖路由/dirty 重算与全量对照；[CycleDurableHistory](src/ResourceCycle/Persistence/CycleDurableHistory.cs) 单根 E/S 与 pending 恢复；[Console](src/ResourceCycle.Cli/Program.cs) 提供批处理及 `--play` 单角色采集。角色/背包独立子图，输入源为运行时接口；见[可玩设计](docs/design/resource-cycle-play.md)。
- **构建依赖**：Kernel/Spatial/ResourceCycle 直接引用相邻 DurableGraph 当前源码及 Generator/schema 工具；ResourceCycle 和 Spatial.Tests 的 Persistence 也来自同一源码图。Storage 沿用 DG 的 revision 校验，验收显式运行 Schema Verify。源码根与使用边界见[源码依赖说明](docs/source-dependencies.md)。

## 当前焦点与下一步

用户已确定围绕现有时空模型在本仓库建设下一代引擎与一组实例游戏；Mud 保留为可玩参照，选择性借鉴其动作、观察、事件与状态关联经验，不进行原地迁移或兼容适配。新业务逻辑先写应用层，经实例验证后再提取共性。

资源周期实例已有批处理预测对照与单角色、单地点、两种植物、背包、等待/即时采集的 Console 闭环，均保持单根恢复；证据见[批处理验收](docs/experiments/resource-cycle-acceptance.md)与[采集验收](docs/experiments/resource-cycle-play-acceptance.md)。没有空间接线、GUI 或 LLM。

用户已选定：在扩大世界前统一核心预测形状，空间由运动变化触发通道重新发现，随后可缩为变化段配对。S1/S2 已落实独立 Contacts 子图、Kernel 唯一 Complete、带模型时间的完整校验与实际同根恢复；独立全量参考检查新关系漏项，arrival/schedule 复用既有 Due，没有通用过程注册表。下一步按[方案 S3](docs/design/local-prediction-refactor.md#7-实施切片与停止点)协调 ResourceCycle：把规则内维护迁入 Complete，保留 Due/Kind 优先级及角色唤醒，统一候选读取命名并验证恢复不维护；当前该工厂仅用显式 identity Complete 适配。现有 floor＋同 tick 仲裁是对照基线而非永久兼容约束；更强索引按[综述](docs/research/prediction-discovery/README.md)的覆盖条件重开。

Mud 体验复用见[可行性调查](docs/research/mud-experience-portability.md)；用户选定就地增强 ResourceCycle，避免另一套 Demo 的维护面。另一会话并行推进 2–3 角色、同一 Human Console 轮流控制；动作完成或被打断才唤醒交互。当前核心分支不改该应用片；近期不增加移动，复杂 Markdown 事件窗口和 AgentCore 子状态发布按后续切片裁决。

当前构建包含三个核心、ResourceCycle 库/Console 及测试；其余旧应用层采用固定 tag 与[仓内归档](docs/archive/player-runtime.md)保存，不因新实例方向自动解冻。

Dynamic-Programmer R1–R8 的既有实现、验收、Player 运行时理念与长期 Plan 设计统一保留在[Player 归档](docs/archive/player-runtime.md)，不驱动当前施工。

FirstBoard 场景、Demo、LLM Player、实际持久化 adapter 和专属资料此前已移至[归档索引](docs/archive/firstboard-llm.md)，不参与默认编译或搜索。

## 延期与重开条件

- **Mud 成果复用**：按新实例的真实需要选择内容、独立库或设计经验，不搬迁整套领域/Session，不要求旧存档兼容。
- **多角色与 Player**：无移动的 ResourceCycle 多角色片已由用户分到另一会话并行推进，同刻轮转/资源竞争规则在该片裁决；本分支不以核心重构阻断它。更多地点、LLM Player 与长期 Plan 尚未重开，旧实现保持归档。
- **持久化扩展**：ResourceCycle adapter 仅服务本实例；空间已有测试专用单根 E/S adapter 与冷开证据，尚未提供产品存档入口。其他游戏、分支或通用存档依据真实需求扩展。归档 FirstBoard 的结果不作为新实例的落盘证据。
- **分支、续局与外部调用**：可玩 fork/倒带、完整 Player closure、LLM 调用恢复、异常/retry 历史按真实需求重开；DurableGraph 不透明恢复 Task、LLM 调用或执行栈。
- **Spatial 扩展**：预测对象迁移已进入当前核心方向；空间拓扑、运动数学和更多途中互动不随之自动重设计，需独立需求再重开。
- **持久 Script VM**：研究材料已归档；只有需要持久执行状态时才重新选择方案。

## 按需导航与验证

- 两份等价入口 [DramaBoard.slnx](DramaBoard.slnx) 与 [DramaBoard.Local.slnx](DramaBoard.Local.slnx) 包含核心与当前实例。[README](README.md)与 [CI](.github/workflows/ci.yml)直接构建源码依赖，使用精确 SDK 10.0.201；第三方包来自 nuget.org，保留仓内包缓存。旧包准备脚本仅保留历史复现用途，当前不要求发包。试玩：`dotnet run --project src/ResourceCycle.Cli -c Release -- --play`；批处理诊断使用 `--trace`。
- 文档入口：[docs/README](docs/README.md)。FirstBoard/LLM 的恢复路径、清单与原始基线在 [归档索引](docs/archive/firstboard-llm.md)；其正文必须显式 `rg --no-ignore` 查阅，不能作为当前待办或测试基线。
