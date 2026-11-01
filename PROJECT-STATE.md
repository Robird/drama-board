# DramaBoard 当前项目状态

> 跨会话入口；维护方式见 [AGENTS.md](AGENTS.md)。按本文件选择下一处阅读，不必顺读历史记录。
> 最近整理：2026-11-01；[小镇与山野的融合愿景](docs/codex-goals/living-world/README.md)的 Goal 已启动，山野镇首片已验收，接着迁入生产经济；大喊、跟随、微型小镇与 Character Gym 一期已完成，Gym 二期继续延期。

## 最终目标

探索 LLM-Native 的开放世界游戏：用真实可玩场景逐步选择角色、模拟、棋类与 RL Gym 的机制。领域世界应可持续演化，同时保持时间推进确定、空间行为可解释、领域代码易写。DramaBoard 作为 DurableGraph 的真实消费者，持续反馈声明式增量持久化与 Schema 演化的需求。

## 当前实现锚点

- **Kernel**：统一 Occurrence 仲裁；Plan → 业务 Fold → 纯 Complete → 带时间 Validate，发布独立 E/S，S 成功才安装；pending/Replay 同样准备派生缓存，不重做 Plan。见 [SimulationKernel](src/Kernel/Simulation/SimulationKernel.cs) 与 [E/S 设计](docs/design/durablegraph-occurrence-persistence.md)。
- **Spatial**：[SpatialSimulation](src/Spatial/Simulation/SpatialSimulation.cs) 按 dirty 通道增量维护运行时 Contacts，genesis/冷开显式 Rebuild；磁盘 S 只保存 Business/cursor，E 只含 GraphSpatialFact。保留 contact floor、arrival ceil、严格内部掉头与 current-segment 消费；见[缓存验收](docs/experiments/runtime-derived-predictions-acceptance.md)和 [Graph 设计](docs/design/graph-spatial-world.md)。
- **Host**：通用 [SimulationHost](src/Host/SimulationHost.cs) 循环驱动 Kernel，保留在目标核心中。
- **Inventory**：[ItemInventory](src/Inventory/ItemInventory.cs) 提供不可变物品数量与可选总件数上限，只依赖 DG；局部接纳/增加/校验由模块负责，跨模块采集效果及单根发布仍由游戏编排。
- **ResourceCycle 应用实例**：[CyclePlanning](src/ResourceCycle/CyclePlanning.cs) 原子记录库存、背包、活动及加入/耗尽感知；[CycleEngine](src/ResourceCycle/CycleEngine.cs) 编排独立事实应用与[预测维护](src/ResourceCycle/CyclePredictionMaintenance.cs)。[CycleDurableHistory](src/ResourceCycle/Persistence/CycleDurableHistory.cs) 单根保存业务/游标及游戏修订号，独立事件也带修订号；pending 恢复不重做控制器/Plan。[Console](src/ResourceCycle.Cli/Program.cs) 当前默认进入山野镇三居民生活世界；早期采集证据见[多人验收](docs/experiments/resource-cycle-multiplayer-acceptance.md)。
- **构建依赖**：Kernel/Spatial/Inventory/ResourceCycle 直接引用相邻 DurableGraph 当前源码及 Generator/schema 工具；ResourceCycle 和 Spatial.Tests 的 Persistence 也来自同一源码图。Storage 沿用 DG 的 revision 校验，验收显式运行 Schema Verify。源码根与使用边界见[源码依赖说明](docs/source-dependencies.md)。
- **JSON 文本**：文件、日志、Console JSON 与 Completion 请求使用 `UnsafeRelaxedJsonEscaping`；[角色 Markdown 数据字面量](src/ResourceCycle.Cli/CharacterContextRenderer.cs)保留安全转义，[空间候选键](src/Spatial/Simulation/SpatialOccurrenceRule.cs)及 Completion dispatch 指纹保留原编码以稳定身份与排序。日志校验按保存的原始 Payload 计算，旧行可与新编码追加行共存。

## 当前焦点与下一步

用户已确定围绕现有时空模型在本仓库建设下一代引擎与一组实例游戏；Mud 保留为可玩参照，选择性借鉴其动作、观察、事件与状态关联经验，不进行原地迁移或兼容适配。新业务逻辑先写应用层，经实例验证后再提取共性。

山野镇以 ResourceCycle 为唯一运行时：默认三居民，可用 `--characters 1|2|3` 选择新局人数；续局省略人数则读取存档。默认共用 Human Console；`--llm` 控制全体，`--human ID` 选择一个 Console 角色。`--steps` / `--until-hour` 仅暂停宿主，所有角色/空间/资源仍组成同一发布根；没有 GUI。旧 `--play` / `--survival` / `--duo` 及批处理入口已撤下，历史采集语义保留在领域测试。

用户已选定：在扩大世界前统一核心预测形状，空间由运动变化触发通道重新发现；运行中增量维护和冷开重建是独立选择。纯派生预测不应强制落盘；原持久方案仅作为[历史验收记录](docs/experiments/local-prediction-acceptance.md)保留。

Kernel/Spatial 的缓存与恢复闭环已落地：不可变运行时 wrapper 与持久 Business 分离，全量 oracle、实际 S/E-only 冷开及 S 成功后安装合同的证据见[缓存验收](docs/experiments/runtime-derived-predictions-acceptance.md)。没有旧存档兼容要求。

**当前焦点**：用户已选择将 Microtown 与 ResourceCycle 融合为“小镇与山野”的连续生活世界，支持 Human Console（真人或 Coding Agent 操作）与多个 LLM 角色，无强制主线。新[愿景与完成条件](docs/codex-goals/living-world/README.md)、[实现思路](docs/codex-goals/living-world/approach.md)和[Goal 启动文本](docs/codex-goals/living-world/goal.md)已建立，首片已通过[验收](docs/experiments/living-world-foundation-acceptance.md)；后续从[专题工作计划](docs/codex-goals/living-world/work-plan.md)接续，按完成条件自主选片、实现、验证与自由游玩，少量重大取舍再交用户。专题施工状态只在该计划维护。

[语义空间连接与大喊](docs/design/spatial-relations-and-shout.md)已贯通 64-bit 能力连接、一跳查询与多人原文交付，见[验收](docs/experiments/spatial-shout-acceptance.md)；事件分类、传播、筛选和订阅按新世界实际消费者继续深化，不预建空框架。

[角色跟随](docs/design/character-following.md)已贯通主动集合、普通 go 全员出发、到点解散工作与重新集合，见[实现与验收](docs/experiments/character-following-acceptance.md)。实际人类参与的合作试玩与集合体验反馈仍独立保留，代理操作 Human 入口不能替代。不自动等待未加入者，不添加 Party/Ready 屏障；途中退出、异步插话或通用队伍模块仍按实际需求裁决。

[微型小镇里程碑](docs/codex-goals/microtown/README.md)已完成，证据集中于[总验收](docs/experiments/microtown-acceptance.md)与[真实模型试玩](docs/experiments/microtown-playtest.md)。Microtown 暂留作经济迁移参照；新生活世界只使用 Cycle 发布根，生产与经济尚待迁入。两原型共用 Goals、CharacterControl.Completion、CharacterGym 与既有核心/库存；[新增游戏接线说明](docs/implementation/adding-a-game.md)列出实际调用点。小镇 GameRevision 3 的经济、交易、owner 预测和恢复/分叉是融合起点；融合方向以上述新专题为准，旧里程碑不再要求未来继续维持两个独立产品。其他体验建议与 Gym 二期的历史重开条件见[原工作计划](docs/codex-goals/microtown/work-plan.md)。

[角色实验环境一期](docs/design/character-gym/README.md)已通过[工程验收](docs/experiments/character-gym-acceptance.md)与[真实模型串联试玩](docs/experiments/character-gym-playtest.md)，操作见[使用说明](docs/design/character-gym/usage.md)；小镇复用并扩展其游戏适配，二期修复/对照评估需另行启动。

**独立保留的验收项**：[生存二期](docs/design/survival-demo-milestone.md) 2A–2C 已完成工程接线与模型预演，见[交流验收](docs/experiments/survival-communication-acceptance.md)。仍需实际人类控制 a、真实模型控制 b 的合作通关及体验反馈；代理操作 Human 入口不能替代。该验收不被新里程碑抹去，也不阻止目标管理设计；实现和证据不在此重复维护。

目标管理首版已完成[工程验收](docs/experiments/goal-management-acceptance.md)与[真实试玩](docs/experiments/goal-management-playtest.md)，复杂结构有明确引导的使用证据，不宣称模型自发选用。[设计](docs/design/goal-management/design.md)已吸收[跨领域研究](docs/research/goal-management/README.md)。[GoalRules](src/Goals/GoalRules.cs)实施状态/更正、显式 Focus、共享 DAG、容量清理与原子操作；[GoalView](src/ResourceCycle.Cli/GoalView.cs)复用冻结 Context 投影当前目标、短回执和最近有效查询，不另建详情状态。[角色决策规约](docs/design/goal-management/decision-policy.md)已按实际能力替换长期 handoff 指引；剩余体验问题统一见设计末节，不引入固定用户流程、多阶段 LLM 管线、自动规划或行为预测。

[属性速率解释](docs/design/attribute-rate-explanations.md)与[属性阈值预测](docs/design/attribute-threshold-forecasts.md)已贯通冻结观察、Context 与两种布局，见[验收](docs/experiments/attribute-threshold-forecasts-acceptance.md)；继续通过实际控制器体验打磨，Buff 生命周期、非加法解释与部分可见原因按真实玩法重开。

[角色上下文 Model](docs/design/character-context-model.md)已通过[验收](docs/experiments/character-context-model-acceptance.md)。角色信息真相为冻结 Opening＋保留的有序 Items，Console 游标仅优化绘制；纯输出独立于终端实录。2A 已提供宿主调用/连续协议失败预算、显式暂停及超大材料拒绝；现有同刻 1024 转换护栏不代替调用预算。其他材料容量与历史保留按真实运行证据另行裁决，不以呈现重构宣称这些已完成。

[增量 UI 切片 A](docs/design/incremental-ui-slice-a.md)的事件/效果与状态键继续复用；生存 Console 支持 `:mode full|delta`，默认 delta。当前 Context 已保存零耗时反馈与上次放行区间的经历，替代独立 Incoming 总计；尚无最近 n 个完整回合窗口。后续按[阶段目标](docs/design/incremental-ui-milestone.md)裁决 B 的容量和裁剪，倾向按决策回合分组。保持新回合自足，不重开动作后收尾或 `:end`；后续依真实模型体验比较决策质量与上下文成本；宿主审计不等于角色记忆，原生工具会话恢复仍延期。

生存一期的规则与恢复证据见[最终验收](docs/experiments/survival-demo-acceptance.md)。Console 使用 [IPlayLayout](src/ResourceCycle.Cli/IPlayLayout.cs) 切换决策/经典布局，支持 `:layout <名称> once`，元命令仅接受冒号前缀。[PlayPresentationSession](src/ResourceCycle.Cli/PlayPresentationSession.cs)以带角色/轮号的彩色上下文起止标记及带外管理块组织交互，Closed 后的结果在下一 Opening 交付。完整重放与最新状态预览分开，look/once 不推进消费游标；已打印的过期拒绝可留在滚动历史，纯输出只含保留材料。排版见[布局合同](docs/design/console-layouts.md)，不设位置快照测试；危险指标来自保存的观察，布局不重新预测；返回原因按本次警告事件列出触发属性，途中路线来自共享冻结观察，未知目的地不泄露。二期双角色个人结局与同地对话/赠与已实施；移动不可取消、干木燃料用途依体验重开。

[渐进重构方案](docs/implementation/modularization-plan.md) A–D 已通过[验收](docs/experiments/modularization-acceptance.md)；继续按[分层与组合设计](docs/design/layering-and-composition.md)在游戏层组合新玩法，依真实需求提取模块。默认采集 demo 仍使用无限背包；持久身份与旧开发存档支持范围集中在重构方案。

重构保留现有采集合同：`all` 冻结接受时库存；加入/耗尽暂停时间请求响应，continue 保留活动，cancel 保留已得成果；同刻全部完成先于输入。跨模块变化保持单根结算，正常 Step 先准备并校验，再发布 E/S；继续以逐边界 Rebuild/oracle、同刻竞争及真实 S/E-only 冷开轨迹验收。

Mud 体验复用见[可行性调查](docs/research/mud-experience-portability.md)；现有采集场景使用同一 Human Console 轮流控制多个角色。加入/耗尽感知扩展了原先“仅完成/打断才唤醒”的边界：Player 获得交互机会不必取消角色活动。空间移动已由生存一期重开；事件窗口随当前增量 UI 目标研究，AgentCore 子状态发布仍延期。

**控制器边界**：[共享无状态 CharacterController](docs/design/stateless-character-controller.md) 不私藏跨决策语义记忆；Business 根的 [CharacterInteractionState](src/ResourceCycle/CharacterContext.cs) 保存独立 Goals、Context、DraftHandoff、PendingAdvance 与待交付经历。`ChooseCommandAsync` 接收 [CycleDecisionContext](src/ResourceCycle/CycleObservation.cs)，返回保存原始输入/身份的 `CycleControllerOperation`；共享 parser 与角色校验决定成功或持久拒绝，pending E 不重做决定。[Human Console 边界](docs/design/console-command-boundary.md) 的 `HumanDefault` / `None` 目录同时控制元命令提示和执行；拒绝不轮转服务、不确认警告；操作窗口清理不返还同刻 1024 转换预算，冷开也不重置预算。LLM 适配器已接入；目录热切换和完整工具会话恢复未施工。

**操作身份**：[CycleOperationJournal](src/ResourceCycle.Cli/CycleOperationJournal.cs) 在解析前保存 Console 隐式 ToolCallId，覆盖查询/拒绝/元命令；结果同 ID 配对，成功/拒绝的游戏 E/S 保存来源与结论，元命令仍只记宿主日志。`operations.jsonl` 与 E/S 各自提交，冷开只补原成功/拒绝结果、不重做动作、不恢复已裁剪拒绝；LLM 保留 Completion 交付值，端点格式映射仍归兄弟项目 Completion。

**初始化与修订**：[CycleGameFactory](src/ResourceCycle/CycleGameFactory.cs) 统一内存/磁盘新游戏边界；[加载编排](docs/design/game-initialization-and-revision.md) 分离还原、内存修订与运行就绪，S/E 各自带 `GameRevision`，加载不执行事件、不推进游标、不写游戏 E/S。State 转换独立于 pending，跨图用领域 ID；[验收](docs/experiments/game-revision-acceptance.md)覆盖中断窗口。生产只接受[修订 19](src/ResourceCycle/Persistence/CycleGameRevision.cs)，大喊相关 Schema 边界见[大喊验收](docs/experiments/spatial-shout-acceptance.md)；旧开发存档不兼容，跨版本转换仅为夹具。CLI 在加载后恢复 pending 并修复宿主操作结果；多模块版本和通用框架按真实需求重开。

当前构建包含三个核心、Inventory、Goals、CharacterControl.Completion、CharacterGym、Microtown 领域/Completion 适配/Console、ResourceCycle 领域/Completion 适配/Console 及测试；其余旧应用层采用固定 tag 与[仓内归档](docs/archive/player-runtime.md)保存，不因新实例方向自动解冻。

Dynamic-Programmer R1–R8 的既有实现、验收、Player 运行时理念与长期 Plan 设计统一保留在[Player 归档](docs/archive/player-runtime.md)，不驱动当前施工。

FirstBoard 场景、Demo、LLM Player、实际持久化 adapter 和专属资料此前已移至[归档索引](docs/archive/firstboard-llm.md)，不参与默认编译或搜索。

## 延期与重开条件

- **Mud 成果复用**：按新实例的真实需要选择内容、独立库或设计经验，不搬迁整套领域/Session，不要求旧存档兼容。
- **Player 与玩法扩展**：长期约定由角色 Goals 承载，handoff 保持一次性交接。结构化经历召回、实体解析、完整笔记编辑器、自动规划与条件提醒按目标管理实测缺口重开；概率产出、主动后台插话、唯一目标失效和通用规则包按真实玩法需要重开。旧运行时保持归档；出现激活过多的证据后再设计通知过滤。
- **持久化扩展**：ResourceCycle adapter 仅服务本实例；空间已有测试专用单根 E/S adapter 与冷开证据，尚未提供产品存档入口。其他游戏或通用存档依据真实需求扩展。归档 FirstBoard 的结果不作为新实例的落盘证据。
- **分支、续局与外部调用**：2A 保留未发布 E 可重新调用、已发布 E 不重问或重复效果；Gym 支持串行选择完成 S 分叉，保留源 lineage/随机坐标、独立分支操作日志与截断的证据前缀；任意位置倒带、并发分支运行、完整 Player closure、原生模型会话恢复和全面异常/retry 恢复按真实需求重开。DurableGraph 不透明恢复 Task、LLM 调用或执行栈。
- **Spatial 扩展**：静态语义连接与一跳查询已实施；动态拓扑、距离传播、运动数学和更多途中互动继续延期。现有 floor＋同 tick 仲裁仍作对照；更强索引按[综述](docs/research/prediction-discovery/README.md)的覆盖条件重开。缓存移除不会自动更新已保存的速度/周期参数，显式平衡调整见缓存方案第 6 节。
- **持久 Script VM**：研究材料已归档；只有需要持久执行状态时才重新选择方案。

## 按需导航与验证

- 唯一入口 [DramaBoard.slnx](DramaBoard.slnx) 包含核心、当前实例及测试，并显式列入相邻 DG/Storage/Completion 源码项目供 IDE/LSP 加载；重复 Local solution 已删除。[README](README.md)与 [CI](.github/workflows/ci.yml)均使用相邻仓库布局，使用精确 SDK 10.0.201；第三方包来自 nuget.org，保留仓内包缓存。旧包准备脚本仅保留历史复现用途，当前不要求发包。试玩：`dotnet run --project src/ResourceCycle.Cli -c Release -- --store artifacts/my-hillside`；控制绑定和预算见根 README。
- 文档入口：[docs/README](docs/README.md)；概念与当前代码命名对照见[术语表](docs/glossary.md)，区分角色、控制器、活动、决策请求与预测/事实。FirstBoard/LLM 的恢复路径、清单与原始基线在 [归档索引](docs/archive/firstboard-llm.md)；其正文必须显式 `rg --no-ignore` 查阅，不能作为当前待办或测试基线。
