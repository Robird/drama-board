# DramaBoard 当前项目状态

> 跨会话入口；维护方式见 [AGENTS.md](AGENTS.md)。按本文件选择下一处阅读，不必顺读历史记录。
> 最近整理：2026-10-27；Kernel/Spatial 与 ResourceCycle 单角色实例均按[缓存方案](docs/design/runtime-derived-predictions.md)分离持久业务状态与运行时派生预测，冷开重建缓存。多角色化独立推进，不是缓存迁移的前置条件。

## 最终目标

探索 LLM-Native 的开放世界游戏：用真实可玩场景逐步选择角色、模拟、棋类与 RL Gym 的机制。领域世界应可持续演化，同时保持时间推进确定、空间行为可解释、领域代码易写。DramaBoard 作为 DurableGraph 的真实消费者，持续反馈声明式增量持久化与 Schema 演化的需求。

## 当前实现锚点

- **Kernel**：统一 Occurrence 仲裁；Plan → 业务 Fold → 纯 Complete → 带时间 Validate，发布独立 E/S，S 成功才安装；pending/Replay 同样准备派生缓存，不重做 Plan。见 [SimulationKernel](src/Kernel/Simulation/SimulationKernel.cs) 与 [E/S 设计](docs/design/durablegraph-occurrence-persistence.md)。
- **Spatial**：[SpatialSimulation](src/Spatial/Simulation/SpatialSimulation.cs) 按 dirty 通道增量维护运行时 Contacts，genesis/冷开显式 Rebuild；磁盘 S 只保存 Business/cursor，E 只含 GraphSpatialFact。保留 contact floor、arrival ceil、严格内部掉头与 current-segment 消费；见[缓存验收](docs/experiments/runtime-derived-predictions-acceptance.md)和 [Graph 设计](docs/design/graph-spatial-world.md)。
- **Host**：通用 [SimulationHost](src/Host/SimulationHost.cs) 循环驱动 Kernel，保留在目标核心中。
- **ResourceCycle 应用实例**：[CycleEngine](src/ResourceCycle/CycleEngine.cs) 在 Complete 内路由/dirty 重算，Rebuild 从 Business 重建预测与固定订阅；[CycleDurableHistory](src/ResourceCycle/Persistence/CycleDurableHistory.cs) 单根只保存业务/游标，pending 恢复不重做 Player/Plan；见[缓存验收](docs/experiments/resource-cycle-runtime-cache-acceptance.md)。[Console](src/ResourceCycle.Cli/Program.cs) 保留批处理及 `--play` 单角色采集，角色/背包独立子图。
- **构建依赖**：Kernel/Spatial/ResourceCycle 直接引用相邻 DurableGraph 当前源码及 Generator/schema 工具；ResourceCycle 和 Spatial.Tests 的 Persistence 也来自同一源码图。Storage 沿用 DG 的 revision 校验，验收显式运行 Schema Verify。源码根与使用边界见[源码依赖说明](docs/source-dependencies.md)。

## 当前焦点与下一步

用户已确定围绕现有时空模型在本仓库建设下一代引擎与一组实例游戏；Mud 保留为可玩参照，选择性借鉴其动作、观察、事件与状态关联经验，不进行原地迁移或兼容适配。新业务逻辑先写应用层，经实例验证后再提取共性。

资源周期实例已有批处理预测对照与单角色、单地点、两种植物、背包、等待/即时采集的 Console 闭环，均保持单根恢复；证据见[批处理验收](docs/experiments/resource-cycle-acceptance.md)与[采集验收](docs/experiments/resource-cycle-play-acceptance.md)。没有空间接线、GUI 或 LLM。

用户已选定：在扩大世界前统一核心预测形状，空间由运动变化触发通道重新发现；运行中增量维护和冷开重建是独立选择。纯派生预测不应强制落盘；原持久方案仅作为[历史验收记录](docs/experiments/local-prediction-acceptance.md)保留。

Kernel/Spatial 的缓存与恢复闭环已落地：不可变运行时 wrapper 与持久 Business 分离，全量 oracle、实际 S/E-only 冷开及 S 成功后安装合同的证据见[缓存验收](docs/experiments/runtime-derived-predictions-acceptance.md)。没有旧存档兼容要求。

**下一步**：ResourceCycle 多角色片采用用户提出的库存/再生、独立耗时采集、有限目标及他人加入/库存耗尽感知方向；先审定[多人采集规则草案](docs/design/resource-cycle-multiplayer-gathering.md)，再分片实施。用户已明确：中途采空也暂停时间并唤醒，可继续原活动或取消换行动，不能无提示地继续劳动。团队建议三个角色、周期到期竞争、活动与注意力正交、`all` 冻结目标；尚未实现或试玩验收。继续以逐边界 Rebuild/oracle、同刻竞争及真实 S/E-only 冷开轨迹验收；原型稳定后再评估抽取基础规则包，不先扩建引擎框架。

Mud 体验复用见[可行性调查](docs/research/mud-experience-portability.md)；用户选定就地增强 ResourceCycle，同一 Human Console 轮流控制多个角色。加入/耗尽感知扩展了原先“仅完成/打断才唤醒”的边界：Player 获得交互机会不必取消角色活动。近期不增加移动，复杂 Markdown 事件窗口和 AgentCore 子状态发布按后续切片裁决。

当前构建包含三个核心、ResourceCycle 库/Console 及测试；其余旧应用层采用固定 tag 与[仓内归档](docs/archive/player-runtime.md)保存，不因新实例方向自动解冻。

Dynamic-Programmer R1–R8 的既有实现、验收、Player 运行时理念与长期 Plan 设计统一保留在[Player 归档](docs/archive/player-runtime.md)，不驱动当前施工。

FirstBoard 场景、Demo、LLM Player、实际持久化 adapter 和专属资料此前已移至[归档索引](docs/archive/firstboard-llm.md)，不参与默认编译或搜索。

## 延期与重开条件

- **Mud 成果复用**：按新实例的真实需要选择内容、独立库或设计经验，不搬迁整套领域/Session，不要求旧存档兼容。
- **多角色与 Player**：无移动的 ResourceCycle 多角色片按上述草案推进；更多地点、LLM Player 与长期 Plan 尚未重开，旧实现保持归档。概率产出、主动后台插话和通用规则包按草案的真实需求条件重开。
- **持久化扩展**：ResourceCycle adapter 仅服务本实例；空间已有测试专用单根 E/S adapter 与冷开证据，尚未提供产品存档入口。其他游戏、分支或通用存档依据真实需求扩展。归档 FirstBoard 的结果不作为新实例的落盘证据。
- **分支、续局与外部调用**：可玩 fork/倒带、完整 Player closure、LLM 调用恢复、异常/retry 历史按真实需求重开；DurableGraph 不透明恢复 Task、LLM 调用或执行栈。
- **Spatial 扩展**：空间拓扑、运动数学和更多途中互动需独立需求再重开。现有 floor＋同 tick 仲裁仍作对照；更强索引按[综述](docs/research/prediction-discovery/README.md)的覆盖条件重开。缓存移除不会自动更新已保存的速度/周期参数，显式平衡调整见缓存方案第 6 节。
- **持久 Script VM**：研究材料已归档；只有需要持久执行状态时才重新选择方案。

## 按需导航与验证

- 两份等价入口 [DramaBoard.slnx](DramaBoard.slnx) 与 [DramaBoard.Local.slnx](DramaBoard.Local.slnx) 包含核心与当前实例。[README](README.md)与 [CI](.github/workflows/ci.yml)直接构建源码依赖，使用精确 SDK 10.0.201；第三方包来自 nuget.org，保留仓内包缓存。旧包准备脚本仅保留历史复现用途，当前不要求发包。试玩：`dotnet run --project src/ResourceCycle.Cli -c Release -- --play`；批处理诊断使用 `--trace`。
- 文档入口：[docs/README](docs/README.md)。FirstBoard/LLM 的恢复路径、清单与原始基线在 [归档索引](docs/archive/firstboard-llm.md)；其正文必须显式 `rg --no-ignore` 查阅，不能作为当前待办或测试基线。
