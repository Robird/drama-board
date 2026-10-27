# DramaBoard 当前项目状态

> 跨会话入口；维护方式见 [AGENTS.md](AGENTS.md)。按本文件选择下一处阅读，不必顺读历史记录。
> 最近整理：2026-10-27；Kernel/Spatial 与 ResourceCycle 均按[缓存方案](docs/design/runtime-derived-predictions.md)分离持久业务状态与运行时派生预测。ResourceCycle 已具备三角色库存竞争及感知唤醒，规则留在应用层。

## 最终目标

探索 LLM-Native 的开放世界游戏：用真实可玩场景逐步选择角色、模拟、棋类与 RL Gym 的机制。领域世界应可持续演化，同时保持时间推进确定、空间行为可解释、领域代码易写。DramaBoard 作为 DurableGraph 的真实消费者，持续反馈声明式增量持久化与 Schema 演化的需求。

## 当前实现锚点

- **Kernel**：统一 Occurrence 仲裁；Plan → 业务 Fold → 纯 Complete → 带时间 Validate，发布独立 E/S，S 成功才安装；pending/Replay 同样准备派生缓存，不重做 Plan。见 [SimulationKernel](src/Kernel/Simulation/SimulationKernel.cs) 与 [E/S 设计](docs/design/durablegraph-occurrence-persistence.md)。
- **Spatial**：[SpatialSimulation](src/Spatial/Simulation/SpatialSimulation.cs) 按 dirty 通道增量维护运行时 Contacts，genesis/冷开显式 Rebuild；磁盘 S 只保存 Business/cursor，E 只含 GraphSpatialFact。保留 contact floor、arrival ceil、严格内部掉头与 current-segment 消费；见[缓存验收](docs/experiments/runtime-derived-predictions-acceptance.md)和 [Graph 设计](docs/design/graph-spatial-world.md)。
- **Host**：通用 [SimulationHost](src/Host/SimulationHost.cs) 循环驱动 Kernel，保留在目标核心中。
- **ResourceCycle 应用实例**：[CyclePlanning](src/ResourceCycle/CyclePlanning.cs) 原子记录库存、背包、活动及加入/耗尽感知；[CycleEngine](src/ResourceCycle/CycleEngine.cs) 编排独立事实应用与[预测维护](src/ResourceCycle/CyclePredictionMaintenance.cs)。[CycleDurableHistory](src/ResourceCycle/Persistence/CycleDurableHistory.cs) 单根保存业务/游标及游戏修订号，独立事件也带修订号；pending 恢复不重做控制器/Plan。[Console](src/ResourceCycle.Cli/Program.cs) 保留批处理及 `--play` 三角色有限采集。见[多人验收](docs/experiments/resource-cycle-multiplayer-acceptance.md)。
- **构建依赖**：Kernel/Spatial/ResourceCycle 直接引用相邻 DurableGraph 当前源码及 Generator/schema 工具；ResourceCycle 和 Spatial.Tests 的 Persistence 也来自同一源码图。Storage 沿用 DG 的 revision 校验，验收显式运行 Schema Verify。源码根与使用边界见[源码依赖说明](docs/source-dependencies.md)。

## 当前焦点与下一步

用户已确定围绕现有时空模型在本仓库建设下一代引擎与一组实例游戏；Mud 保留为可玩参照，选择性借鉴其动作、观察、事件与状态关联经验，不进行原地迁移或兼容适配。新业务逻辑先写应用层，经实例验证后再提取共性。

资源周期实例已有批处理预测对照与三角色、单地点、两资源点、独立背包/活动的 Console 闭环，均保持单根恢复；规则与当前证据见[多人采集设计](docs/design/resource-cycle-multiplayer-gathering.md)及[验收](docs/experiments/resource-cycle-multiplayer-acceptance.md)。没有空间接线、GUI 或 LLM。

用户已选定：在扩大世界前统一核心预测形状，空间由运动变化触发通道重新发现；运行中增量维护和冷开重建是独立选择。纯派生预测不应强制落盘；原持久方案仅作为[历史验收记录](docs/experiments/local-prediction-acceptance.md)保留。

Kernel/Spatial 的缓存与恢复闭环已落地：不可变运行时 wrapper 与持久 Business 分离，全量 oracle、实际 S/E-only 冷开及 S 成功后安装合同的证据见[缓存验收](docs/experiments/runtime-derived-predictions-acceptance.md)。没有旧存档兼容要求。

**当前焦点**：按已采纳的[分层与组合设计](docs/design/layering-and-composition.md)，先整理 ResourceCycle 内部职责，再依据收益提取领域模块；不强制等第二个完整游戏，也不预建通用组件框架。背包接纳、Console 纯视图/解析、事实应用与预测维护的逻辑边界已落地，证据见[模块化验收](docs/experiments/modularization-acceptance.md)；下一步用总件数容量探针验证库存变体，之后裁决程序集提取。具体合同、Schema/修订待决项及验收只在[渐进重构方案](docs/implementation/modularization-plan.md)维护，默认 demo 玩法不随探针改变。

重构保留现有采集合同：`all` 冻结接受时库存；加入/耗尽暂停时间请求响应，continue 保留活动，cancel 保留已得成果；同刻全部完成先于输入。跨模块变化保持单根结算，正常 Step 先准备并校验，再发布 E/S；继续以逐边界 Rebuild/oracle、同刻竞争及真实 S/E-only 冷开轨迹验收。

Mud 体验复用见[可行性调查](docs/research/mud-experience-portability.md)；用户选定就地增强 ResourceCycle，同一 Human Console 轮流控制多个角色。加入/耗尽感知扩展了原先“仅完成/打断才唤醒”的边界：Player 获得交互机会不必取消角色活动。近期不增加移动，复杂 Markdown 事件窗口和 AgentCore 子状态发布按后续切片裁决。

**控制器方向已定**：[共享无状态 CharacterController](docs/design/stateless-character-controller.md) 不私藏跨决策角色记忆；游戏按角色维护显式状态并构造上下文，本次局部会话与共享运行设施分开，文本/工具遵守角色可见性，pending E 不重做决定。当前接口为 `ICycleCharacterController.ChooseCommandAsync`；上下文 API、LLM 与工具持久化尚未施工。

**初始化与修订首片已落地**：[CycleGameFactory](src/ResourceCycle/CycleGameFactory.cs) 统一内存/磁盘新游戏边界；[加载编排](docs/design/game-initialization-and-revision.md) 分离还原、内存修订与运行就绪，S/E 各自带 `GameRevision`，加载不执行事件、不推进游标、不写盘。State 转换独立于 pending，混合版本先对齐再逐步转换，跨图用领域 ID；[验收](docs/experiments/game-revision-acceptance.md)覆盖真实磁盘与中断窗口。生产只接受修订 1，跨版本转换仅为夹具；首个真实规则修订出现后再定义转换政策和支持范围。事件持久替代、多模块版本和通用框架按真实需求重开。

当前构建包含三个核心、ResourceCycle 库/Console 及测试；其余旧应用层采用固定 tag 与[仓内归档](docs/archive/player-runtime.md)保存，不因新实例方向自动解冻。

Dynamic-Programmer R1–R8 的既有实现、验收、Player 运行时理念与长期 Plan 设计统一保留在[Player 归档](docs/archive/player-runtime.md)，不驱动当前施工。

FirstBoard 场景、Demo、LLM Player、实际持久化 adapter 和专属资料此前已移至[归档索引](docs/archive/firstboard-llm.md)，不参与默认编译或搜索。

## 延期与重开条件

- **Mud 成果复用**：按新实例的真实需要选择内容、独立库或设计经验，不搬迁整套领域/Session，不要求旧存档兼容。
- **Player 与玩法扩展**：更多地点、LLM Player 与长期 Plan 尚未重开，旧实现保持归档。当前仅采药技能和单项产出；概率产出、即时笔记/物品、主动后台插话、唯一目标失效和通用规则包按实际需求重开。真实重新加入仍可再次提醒；出现激活过多的试玩证据后再设计通知过滤。
- **持久化扩展**：ResourceCycle adapter 仅服务本实例；空间已有测试专用单根 E/S adapter 与冷开证据，尚未提供产品存档入口。其他游戏、分支或通用存档依据真实需求扩展。归档 FirstBoard 的结果不作为新实例的落盘证据。
- **分支、续局与外部调用**：可玩 fork/倒带、完整 Player closure、LLM 调用恢复、异常/retry 历史按真实需求重开；DurableGraph 不透明恢复 Task、LLM 调用或执行栈。
- **Spatial 扩展**：空间拓扑、运动数学和更多途中互动需独立需求再重开。现有 floor＋同 tick 仲裁仍作对照；更强索引按[综述](docs/research/prediction-discovery/README.md)的覆盖条件重开。缓存移除不会自动更新已保存的速度/周期参数，显式平衡调整见缓存方案第 6 节。
- **持久 Script VM**：研究材料已归档；只有需要持久执行状态时才重新选择方案。

## 按需导航与验证

- 唯一入口 [DramaBoard.slnx](DramaBoard.slnx) 包含核心、当前实例及测试，并显式列入相邻上游源码项目供 IDE/LSP 加载；重复 Local solution 已删除。[README](README.md)与 [CI](.github/workflows/ci.yml)均使用相邻仓库布局，使用精确 SDK 10.0.201；第三方包来自 nuget.org，保留仓内包缓存。旧包准备脚本仅保留历史复现用途，当前不要求发包。试玩：`dotnet run --project src/ResourceCycle.Cli -c Release -- --play`；批处理诊断使用 `--trace`。
- 文档入口：[docs/README](docs/README.md)；概念与当前代码命名对照见[术语表](docs/glossary.md)，区分角色、控制器、活动、决策请求与预测/事实。FirstBoard/LLM 的恢复路径、清单与原始基线在 [归档索引](docs/archive/firstboard-llm.md)；其正文必须显式 `rg --no-ignore` 查阅，不能作为当前待办或测试基线。
