# 新增游戏的最小接线路径

本文以山林求生与微型小镇的实际调用点说明如何新增游戏，不提供通用世界根或自动生成游戏框架。第三款游戏出现后继续检验这些边界。小镇里程碑尚在实施，其剩余验收见[工作计划](../codex-goals/microtown/work-plan.md)。

## 1. 先选择游戏拥有的状态与规则

新游戏拥有自己的 Business、角色、内容工厂、行动规则、事实与结束条件。需要的库存、目标、空间子图挂在同一个游戏根下；跨子图变化由游戏规则一次结算。不要把商店、任务、对话等所有潜在玩法提前装进统一根。

两款游戏的内容都在普通 C# 中组装：

- [TownGameFactory.Create](../../src/Microtown/TownGameFactory.cs) 返回初始 `TownWorld` 与 `KernelCursor`；[TownContent](../../src/Microtown/TownContent.cs) 定义小镇配方、价格与地图。
- [CycleGameFactory](../../src/ResourceCycle/CycleGameFactory.cs) 的 `CreateSurvival`、`CreatePlayable`、`CreateBatch` 组装各自实例，但仍归属 ResourceCycle 游戏模型。

工厂不读取旧档或自行提交。每次新游戏创建独立业务图；共享不可变定义可以复用，不能把某局可变库存或角色状态当作静态内容共享。

## 2. 复用现有模块，保留游戏组合

| 能力 | 共享代码 | 山林消费者 | 小镇消费者 | 游戏仍负责 |
| --- | --- | --- | --- | --- |
| 时间与发布 | [SimulationKernel](../../src/Kernel/Simulation/SimulationKernel.cs) | [CycleEngine](../../src/ResourceCycle/CycleEngine.cs) | [TownEngine](../../src/Microtown/TownEngine.cs) | 候选发生项、同刻规则、Plan/Fold/Complete/Validate |
| 空间 | [SpatialSimulation](../../src/Spatial/Simulation/SpatialSimulation.cs) | CycleEngine 的空间组合 | TownEngine 的空间组合 | 地图、可见性、移动命令、接触与到达消费 |
| 库存 | [ItemInventory](../../src/Inventory/ItemInventory.cs) | 角色背包、采集/赠与 | 角色库存、生产/贸易 | 物品定义、产出/消耗、双边转移与账本 |
| 目标 | [GoalRules](../../src/Goals/GoalRules.cs) | [CycleGoalInteractionRules](../../src/ResourceCycle/CycleGoalInteractionRules.cs) | [TownPlanning](../../src/Microtown/TownPlanning.cs) | 操作时机、角色所有权、事实发布与可见材料 |
| 模型调用 | [LlmDecisionLoop](../../src/CharacterControl.Completion/LlmDecisionLoop.cs) | [LlmCharacterController](../../src/ResourceCycle.Completion/LlmCharacterController.cs) | [TownLlmCharacterController](../../src/Microtown.Completion/TownLlmCharacterController.cs) | 强类型观察、游戏指引、原始操作转交 |
| API 连接 | [CompletionConnectionBinding](../../src/CharacterControl.Completion/CompletionConnectionBinding.cs) | ResourceCycle Console | [TownConsole](../../src/Microtown.Cli/TownConsole.cs) | 宿主配置、连接生命周期与恢复后惰性初始化 |
| Gym 证据 | [GymJournal](../../src/CharacterGym/GymJournal.cs)、[GymTraceExporter](../../src/CharacterGym/GymTraceExporter.cs) | [GymSession](../../src/ResourceCycle.Cli/Gym/GymSession.cs) | [TownGymSession](../../src/Microtown.Cli/Gym/TownGymSession.cs) | 游戏身份、精确 S 锚点、提交回执核对、fork 与访问范围 |

Inventory/Goals 不擅自提交整个世界，Completion/Gym 不解释游戏规则。新游戏不必引用 ResourceCycle；如果相似功能仍需大量游戏知识，就先写在新游戏中，出现明确共同消费者后再提取。

## 3. 接入时间、空间与一次发布

使用一个 Kernel 仲裁所有游戏发生项。典型流程是预测 → 选择 → Plan 记录事实 → Fold 业务状态 → Complete 准备派生状态 → Validate → 独立 E/S 发布。提交完成前不得把准备中的运行时视图安装为当前世界；pending 恢复消费已记录事实，不重新调用控制器或重新抽取结果。

持久 Business 与运行时缓存分开。新建/冷开显式 Rebuild；运行时 Complete 依据已提交候选事实准备下一份派生状态。不要另起一个定时器循环直接修改角色或库存。空间除了移动完成，也必须接入已有接触候选的消费路径。

小镇当前自身预测仍在逐步迁移 owner 维护；不能把目前全量 Forecast 当作新的缓存设计范式。成熟的增量/冷开边界参考 [CyclePredictionMaintenance](../../src/ResourceCycle/CyclePredictionMaintenance.cs) 与[派生预测设计](../design/runtime-derived-predictions.md)，全量重算可作为测试 oracle。

## 4. 先用脚本跑通，再提供冻结角色材料

先用 `InMemoryOccurrenceHistory` 和确定性控制器验证完整玩法，包含拒绝、并发活动、同刻竞争和结束。随后把控制器输入收窄为角色自己的不可变观察；不要把完整 World 交给正式 LLM 控制器。

小镇的 [TownObservation.Capture](../../src/Microtown/TownObservation.cs) 与 [TownTextRenderer](../../src/Microtown/TownTextRenderer.cs) 分别负责可见材料和文字；`TownEngine.CreateControlledKernel` 只交付冻结观察。[TownControllerOperation](../../src/Microtown/TownOperation.cs) 在解析前携带原始输入和来源身份。山林使用自己的 `CycleDecisionContext` 与持久 Context，不能仅为共享调用循环而把两个观察模型强行合并。

查询、预览和渲染不修改交付游标。动作由游戏权威重新校验；若候选只能验证本人条件，必须说明提交时还需验证对方，不能通过渲染时的全状态查询泄露对方私有库存。

`CharacterController` 可以串行服务多个角色，但跨决策语义记忆放在各自游戏角色图中。共享模型循环只接收 `LlmDecisionInput(CharacterId, ContextSequence, Material, GameGuidance)`，保留调用预算及当前工具循环，不承担角色长期记忆。

## 5. 创建存档适配并验证真实恢复

新游戏需要自己的根、事件与游戏身份。参照 [TownDurableHistory](../../src/Microtown/Persistence/TownDurableHistory.cs)，明确 `GameId`、State/Event 各自修订以及拒绝不支持版本的行为。注册本游戏类型与实际引用模块，见 [TownDurableModels](../../src/Microtown/Persistence/TownDurableModels.cs) 和 [CycleDurableModels](../../src/ResourceCycle/Persistence/CycleDurableModels.cs)。不要依赖碰巧相同的修订数字区分游戏。

加载先还原 S/E、检查边界和版本、重建就绪视图；`Open` 不执行 pending 或偷偷写盘。正常 Kernel 恢复后才允许模型继续。UI、游标和角色状态都应读取同一就绪视图，不能混用旧磁盘根与已升级内存 wrapper。

Schema 由构建工具生成并 Verify，不手工维护生成文件。表示变化可明确放弃旧开发档；但当前版本必须通过真实磁盘 S 冷开、E-only 恢复、提交失败及精确 S fork。只测内存序列化不能证明这些路径。

## 6. 最后接宿主与 Gym

薄 Console 负责配置、控制器绑定、取消和预算，领域规则不读终端。参考 [TownConsole](../../src/Microtown.Cli/TownConsole.cs)：先恢复 pending、核对宿主回执，首次需要模型时才建立连接。Human 暂停/EOF 不伪造成角色行动；调用预算耗尽是实验暂停，不是游戏完成。

每次模型请求捕获真实决定前完成 S，不能只用决策轮号缓存锚点，因为拒绝和即时操作也会提交新 S。反馈先持久化再给工具回执。宿主日志的操作记录与领域 E/S 分开提交；冷开仅补齐可证明已完成的回执，对没有提交的输入标中断，不从日志重放。

fork 应验证源游戏/分支与精确边界，建立子分支后保留源请求前的证据前缀，排除父分支未来。每个角色导出只含自身私有轨迹及允许的公共元数据；日志不会自动成为角色记忆。

## 7. 一款新游戏的交付检查

- 一个脚本跑完事先声明的有限玩法循环，核对资源及资金变化；失败角色仍进入结果统计。
- 角色材料能独立解释当前处境、可尝试行动、已发生结果和重要未完成事项；其他角色私有数据未泄漏。
- 当前存档可冷开，pending 不重问、不重复效果；fork 不污染源分支与其他角色。
- 真正通过产品宿主运行 Human 与真实模型，保留输入、输出、预算和干预；测试通过不能替代试玩。
- 修改共享模块时运行两个消费者的回归；更新启动说明、Schema 支持范围与验收证据。

这些是接线责任，不要求提前建设统一存档适配、万能 Context、组件注册中心或动态插件装载器。两个现有游戏也不是所有规则都已通用化的保证。
