# 新增游戏的最小接线路径

本文用当前山野镇（ResourceCycle 实现）与保留的 Microtown 调用点说明游戏接线责任。推荐产品入口统一为[山野镇](../../README.md)；Microtown 已停止独立产品演进，源码与测试仅保留作历史及共享模块参照。下文对比这些实现不意味着继续维护两条产品路线，也不把旧小镇的[验收](../experiments/microtown-acceptance.md)当作融合世界证据。

若只是扩充山野镇的地点、居民或经济参数，先使用[有效内容定义](../codex-goals/living-world/content.md)或在现有游戏中增添规则；只有确实需要不同游戏模型时才按本文接新根。本路径不提供通用世界根或自动生成游戏框架。

## 1. 先选择游戏拥有的状态与规则

新游戏拥有自己的 Business、角色、内容工厂、行动规则、事实与结束条件。需要的库存、目标、空间子图挂在同一个游戏根下；跨子图变化由游戏规则一次结算。不要把商店、任务、对话等所有潜在玩法提前装进统一根。

内容由普通 C# 组装，但运行时必须绑定本局采用的有效定义：

- 山野镇的 [CycleWorldDefinitions](../../src/ResourceCycle/CycleWorldDefinitions.cs) 提供 `LivingWorld` 与 `Named("hillside" / "gentle")`；[CycleGameFactory.Create](../../src/ResourceCycle/CycleGameFactory.cs) 接收 `CycleWorldDefinition` 并返回初始世界和 `KernelCursor`。`CreateLivingWorld` 是默认内容入口；`CreateSurvival`、`CreatePlayable`、`CreateBatch` 保留为历史测试夹具，不对应 CLI 产品模式。
- 历史参照 [TownGameFactory.Create](../../src/Microtown/TownGameFactory.cs) 返回 `TownWorld` 与 `KernelCursor`；[TownContent](../../src/Microtown/TownContent.cs) 保留原型配方、价格与地图。不要将这个独立根接入山野镇或作为其运行时内容后备。

工厂不读取旧档或自行提交。每次新游戏创建独立业务图；共享不可变定义可以复用，不能把某局可变库存或角色状态当作静态内容共享。

## 2. 复用现有模块，保留游戏组合

| 能力 | 共享代码 | 山野镇当前消费者 | Microtown 历史参照 | 游戏仍负责 |
| --- | --- | --- | --- | --- |
| 时间与发布 | [SimulationKernel](../../src/Kernel/Simulation/SimulationKernel.cs) | [CycleEngine](../../src/ResourceCycle/CycleEngine.cs) | [TownEngine](../../src/Microtown/TownEngine.cs) | 候选发生项、同刻规则、Plan/Fold/Complete/Validate |
| 空间 | [SpatialSimulation](../../src/Spatial/Simulation/SpatialSimulation.cs) | CycleEngine 的空间组合 | TownEngine 的空间组合 | 地图、可见性、移动命令、接触与到达消费 |
| 库存 | [ItemInventory](../../src/Inventory/ItemInventory.cs) | 角色背包、采集/生产/交易 | 角色库存、生产/贸易 | 物品定义、产出/消耗、双边转移与账本 |
| 目标 | [GoalRules](../../src/Goals/GoalRules.cs) | [CycleGoalInteractionRules](../../src/ResourceCycle/CycleGoalInteractionRules.cs) | [TownPlanning](../../src/Microtown/TownPlanning.cs) | 操作时机、角色所有权、事实发布与可见材料 |
| 模型调用 | [LlmDecisionLoop](../../src/CharacterControl.Completion/LlmDecisionLoop.cs) | [LlmCharacterController](../../src/ResourceCycle.Completion/LlmCharacterController.cs) | [TownLlmCharacterController](../../src/Microtown.Completion/TownLlmCharacterController.cs) | 强类型观察、游戏指引、原始操作转交 |
| API 连接 | [CompletionConnectionBinding](../../src/CharacterControl.Completion/CompletionConnectionBinding.cs) | 山野镇 Console（ResourceCycle.Cli） | [TownConsole](../../src/Microtown.Cli/TownConsole.cs) | 宿主配置、连接生命周期与恢复后惰性初始化 |
| Gym 证据 | [GymJournal](../../src/CharacterGym/GymJournal.cs)、[GymTraceExporter](../../src/CharacterGym/GymTraceExporter.cs) | [GymSession](../../src/ResourceCycle.Cli/Gym/GymSession.cs) | [TownGymSession](../../src/Microtown.Cli/Gym/TownGymSession.cs) | 游戏身份、精确 S 锚点、提交回执核对、fork 与访问范围 |

Inventory/Goals 不擅自提交整个世界，Completion/Gym 不解释游戏规则。新游戏不必引用 ResourceCycle；如果相似功能仍需大量游戏知识，就先写在新游戏中，出现明确共同消费者后再提取。

## 3. 接入时间、空间与一次发布

使用一个 Kernel 仲裁所有游戏发生项。典型流程是预测 → 选择 → Plan 记录事实 → Fold 业务状态 → Complete 准备派生状态 → Validate → 独立 E/S 发布。提交完成前不得把准备中的运行时视图安装为当前世界；pending 恢复消费已记录事实，不重新调用控制器或重新抽取结果。

持久 Business 与运行时缓存分开。新建/冷开显式 Rebuild；运行时 Complete 依据已提交候选事实准备下一份派生状态。不要另起一个定时器循环直接修改角色或库存。空间除了移动完成，也必须接入已有接触候选的消费路径。

历史小镇的 [TownForecasts / TownPredictionMaintenance](../../src/Microtown/TownForecasts.cs) 按角色、市场、报价等 owner 检查依赖并维护预测；全局决策可保守重算，纯时间推进不重建未改变的绝对饥饿截止。缓存绑定对应 Business，Fold 中间态必须经过 Complete 才能成为就绪世界。山野镇使用 [CyclePredictionMaintenance](../../src/ResourceCycle/CyclePredictionMaintenance.cs) 的绑定路由；两者共享发布合同，不强制相同的应用依赖模型。参见[派生预测设计](../design/runtime-derived-predictions.md)；全量重算保留为测试 oracle，不能把它偷偷放进每次 Validate 来掩盖增量维护缺口。

## 4. 先用脚本跑通，再提供冻结角色材料

先用 `InMemoryOccurrenceHistory` 和确定性控制器验证完整玩法，包含拒绝、并发活动、同刻竞争和结束。随后把控制器输入收窄为角色自己的不可变观察；不要把完整 World 交给正式 LLM 控制器。

历史小镇的 [TownObservation.Capture](../../src/Microtown/TownObservation.cs) 与 [TownTextRenderer](../../src/Microtown/TownTextRenderer.cs) 分别负责可见材料和文字；`TownEngine.CreateControlledKernel` 只交付冻结观察。[TownControllerOperation](../../src/Microtown/TownOperation.cs) 在解析前携带原始输入和来源身份。山野镇使用 `CycleDecisionContext` 与持久 Context，不能仅为共享调用循环而把两个观察模型强行合并。

查询、预览和渲染不修改交付游标。动作由游戏权威重新校验；若候选只能验证本人条件，必须说明提交时还需验证对方，不能通过渲染时的全状态查询泄露对方私有库存。

`CharacterController` 可以串行服务多个角色，但跨决策语义记忆放在各自游戏角色图中。共享模型循环只接收 `LlmDecisionInput(CharacterId, ContextSequence, Material, GameGuidance)`，保留调用预算及当前工具循环，不承担角色长期记忆。

## 5. 创建存档适配并验证真实恢复

新游戏需要自己的根、事件与游戏身份。参照 [CycleDurableHistory](../../src/ResourceCycle/Persistence/CycleDurableHistory.cs) 与历史 [TownDurableHistory](../../src/Microtown/Persistence/TownDurableHistory.cs)，明确 `GameId`、State/Event 各自修订以及拒绝不支持版本的行为。注册本游戏类型与实际引用模块，见 [TownDurableModels](../../src/Microtown/Persistence/TownDurableModels.cs) 和 [CycleDurableModels](../../src/ResourceCycle/Persistence/CycleDurableModels.cs)。不要依赖碰巧相同的修订数字区分游戏。

山野镇的有效定义随 S 保存，E 绑定规划前态内容身份；内容变体不是另一种 GameId。新增存档接线也要明确内容来源，不能在冷开或重放时回退到代码中的当前默认值。

加载先还原 S/E、检查边界、版本和内容身份、重建就绪视图；`Open` 不执行 pending 或偷偷写盘。正常 Kernel 恢复后才允许模型继续。UI、游标和角色状态都应读取同一就绪视图，不能混用旧磁盘根与已升级内存 wrapper。

Schema 由构建工具生成并 Verify，不手工维护生成文件。表示变化可明确放弃旧开发档；但当前版本必须通过真实磁盘 S 冷开、E-only 恢复、提交失败及精确 S fork。只测内存序列化不能证明这些路径。

## 6. 最后接宿主与 Gym

薄 Console 负责配置、控制器绑定、取消和预算，领域规则不读终端。参考山野镇的 [PlayConsole](../../src/ResourceCycle.Cli/PlayConsole.cs) 与 [LlmConsole](../../src/ResourceCycle.Cli/LlmConsole.cs)：先验证保存定义和显式内容选择，再恢复 pending、核对宿主回执，首次需要模型时才建立连接。历史 [TownConsole](../../src/Microtown.Cli/TownConsole.cs) 仅作对照，不再新增平行产品入口。Human 暂停/EOF 不伪造成角色行动；调用预算耗尽是实验暂停，不是游戏完成。

每次模型请求捕获真实决定前完成 S，不能只用决策轮号缓存锚点，因为拒绝和即时操作也会提交新 S。反馈先持久化再给工具回执。宿主日志的操作记录与领域 E/S 分开提交；冷开仅补齐可证明已完成的回执，对没有提交的输入标中断，不从日志重放。

fork 应验证源游戏/分支与精确边界，建立子分支后保留源请求前的证据前缀，排除父分支未来。每个角色导出只含自身私有轨迹及允许的公共元数据；日志不会自动成为角色记忆。

## 7. 一款新游戏的交付检查

- 一个脚本跑完事先声明的有限玩法循环，核对资源及资金变化；失败角色仍进入结果统计。
- 角色材料能独立解释当前处境、可尝试行动、已发生结果和重要未完成事项；其他角色私有数据未泄漏。
- 当前存档可冷开，pending 不重问、不重复效果；fork 不污染源分支与其他角色。
- 真正通过产品宿主运行 Human 与真实模型，保留输入、输出、预算和干预；测试通过不能替代试玩。
- 修改共享模块时运行山野镇与保留的 Microtown 消费者回归；更新启动说明、Schema 支持范围与验收证据。

这些是接线责任，不要求提前建设统一存档适配、万能 Context、组件注册中心或动态插件装载器。两个保留实现也不是所有规则都已通用化的保证。产品称为 Hillside 而类型仍为 `Cycle*` 不影响这些责任；无需为整理入口机械改动持久类型或 Schema。
