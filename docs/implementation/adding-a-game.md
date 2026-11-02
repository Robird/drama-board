# 新增游戏的最小接线路径

本文用当前山野镇（Playground 实现）说明游戏接线责任。产品入口见[山野镇](../../README.md)；Microtown 已从主线移除，历史对照见 [Git tag 归档](../archive/microtown.md)。

若只是扩充山野镇的地点、居民或经济参数，先使用[有效内容定义](../codex-goals/living-world/content.md)或在现有游戏中增添规则；只有确实需要不同游戏模型时才按本文接新根。本路径不提供通用世界根或自动生成游戏框架。

## 1. 先选择游戏拥有的状态与规则

新游戏拥有自己的 Business、角色、内容工厂、行动规则、事实与结束条件。需要的库存、目标、空间子图挂在同一个游戏根下；跨子图变化由游戏规则一次结算。不要把商店、任务、对话等所有潜在玩法提前装进统一根。

内容由普通 C# 组装，但运行时必须绑定本局采用的有效定义：

- 山野镇的 [PlaygroundWorldDefinitions](../../src/Playground/PlaygroundWorldDefinitions.cs) 提供 `LivingWorld` 与 `Named("hillside" / "gentle")`；[PlaygroundGameFactory.Create](../../src/Playground/PlaygroundGameFactory.cs) 接收 `PlaygroundWorldDefinition` 并返回初始世界和 `KernelCursor`。`CreateLivingWorld` 是默认内容入口；`CreateSurvival`、`CreatePlayable`、`CreateBatch` 保留为历史测试夹具，不对应 CLI 产品模式。

工厂不读取旧档或自行提交。每次新游戏创建独立业务图；共享不可变定义可以复用，不能把某局可变库存或角色状态当作静态内容共享。

## 2. 复用现有模块，保留游戏组合

| 能力 | 共享代码 | 山野镇当前消费者 | 游戏仍负责 |
| --- | --- | --- | --- |
| 时间与发布 | [SimulationKernel](../../src/Kernel/Simulation/SimulationKernel.cs) | [PlaygroundEngine](../../src/Playground/PlaygroundEngine.cs) | 候选发生项、同刻规则、Plan/Fold/Complete/Validate |
| 空间 | [SpatialSimulation](../../src/Spatial/Simulation/SpatialSimulation.cs) | PlaygroundEngine 的空间组合 | 地图、可见性、移动命令、接触与到达消费 |
| 库存 | [ItemInventory](../../src/Inventory/ItemInventory.cs) | 角色背包、采集/生产/交易 | 物品定义、产出/消耗、双边转移与账本 |
| 目标 | [GoalRules](../../src/Goals/GoalRules.cs) | [PlaygroundGoalInteractionRules](../../src/Playground/PlaygroundGoalInteractionRules.cs) | 操作时机、角色所有权、事实发布与可见材料 |
| 模型调用 | [LlmDecisionLoop](../../src/CharacterControl.Completion/LlmDecisionLoop.cs) | [LlmCharacterController](../../src/Playground.Completion/LlmCharacterController.cs) | 强类型观察、游戏指引、原始操作转交 |
| API 连接 | [CompletionConnectionBinding](../../src/CharacterControl.Completion/CompletionConnectionBinding.cs) | 山野镇 Console（Playground.Cli） | 宿主配置、连接生命周期与恢复后惰性初始化 |
| Gym 证据 | [GymJournal](../../src/CharacterGym/GymJournal.cs)、[GymTraceExporter](../../src/CharacterGym/GymTraceExporter.cs) | [GymSession](../../src/Playground.Cli/Gym/GymSession.cs) | 游戏身份、精确 S 锚点、提交回执核对、fork 与访问范围 |

Inventory/Goals 不擅自提交整个世界，Completion/Gym 不解释游戏规则。新游戏不必引用 Playground；如果相似功能仍需大量游戏知识，就先写在新游戏中，出现明确共同消费者后再提取。

## 3. 接入时间、空间与一次发布

使用一个 Kernel 仲裁所有游戏发生项。典型流程是预测 → 选择 → Plan 记录事实 → Fold 业务状态 → Complete 准备派生状态 → Validate → 独立 E/S 发布。提交完成前不得把准备中的运行时视图安装为当前世界；pending 恢复消费已记录事实，不重新调用控制器或重新抽取结果。

持久 Business 与运行时缓存分开。新建/冷开显式 Rebuild；运行时 Complete 依据已提交候选事实准备下一份派生状态。不要另起一个定时器循环直接修改角色或库存。空间除了移动完成，也必须接入已有接触候选的消费路径。

山野镇使用 [PlaygroundPredictionMaintenance](../../src/Playground/PlaygroundPredictionMaintenance.cs) 的绑定路由，缓存绑定对应 Business，Fold 中间态必须经过 Complete 才能成为就绪世界。参见[派生预测设计](../design/runtime-derived-predictions.md)；全量重算保留为测试 oracle，不进入每次 Validate。新游戏依据自己的依赖模型维护预测。

## 4. 先用脚本跑通，再提供冻结角色材料

先用 `InMemoryOccurrenceHistory` 和确定性控制器验证完整玩法，包含拒绝、并发活动、同刻竞争和结束。随后把控制器输入收窄为角色自己的不可变观察；不要把完整 World 交给正式 LLM 控制器。

山野镇使用 `PlaygroundDecisionContext` 与持久 Context，控制器只接收冻结观察；[PlaygroundControllerOperation](../../src/Playground/PlaygroundActions.cs) 在解析前携带原始输入和来源身份。共享调用循环不要求新游戏复用相同的观察模型。

查询、预览和渲染不修改交付游标。动作由游戏权威重新校验；若候选只能验证本人条件，必须说明提交时还需验证对方，不能通过渲染时的全状态查询泄露对方私有库存。

`CharacterController` 可以串行服务多个角色，但跨决策语义记忆放在各自游戏角色图中。共享模型循环只接收 `LlmDecisionInput(CharacterId, ContextSequence, Material, GameGuidance)`，保留调用预算及当前工具循环，不承担角色长期记忆。

## 5. 创建存档适配并验证真实恢复

新游戏需要自己的根、事件与游戏身份。参照 [PlaygroundDurableHistory](../../src/Playground/Persistence/PlaygroundDurableHistory.cs)，明确 `GameId`、State/Event 各自修订以及拒绝不支持版本的行为。注册本游戏类型与实际引用模块，见 [PlaygroundDurableModels](../../src/Playground/Persistence/PlaygroundDurableModels.cs)。不要依赖碰巧相同的修订数字区分游戏。

山野镇的有效定义随 S 保存，E 绑定规划前态内容身份；内容变体不是另一种 GameId。新增存档接线也要明确内容来源，不能在冷开或重放时回退到代码中的当前默认值。

加载先还原 S/E、检查边界、版本和内容身份、重建就绪视图；`Open` 不执行 pending 或偷偷写盘。正常 Kernel 恢复后才允许模型继续。UI、游标和角色状态都应读取同一就绪视图，不能混用旧磁盘根与已升级内存 wrapper。

Schema 由构建工具生成并 Verify，不手工维护生成文件。表示变化可明确放弃旧开发档；但当前版本必须通过真实磁盘 S 冷开、E-only 恢复、提交失败及精确 S fork。只测内存序列化不能证明这些路径。

## 6. 最后接宿主与 Gym

薄 Console 负责配置、控制器绑定、取消和预算，领域规则不读终端。参考山野镇的 [PlayConsole](../../src/Playground.Cli/PlayConsole.cs) 与 [LlmConsole](../../src/Playground.Cli/LlmConsole.cs)：先验证保存定义和显式内容选择，再恢复 pending、核对宿主回执，首次需要模型时才建立连接。Human 暂停/EOF 不伪造成角色行动；调用预算耗尽是实验暂停，不是游戏完成。

每次模型请求捕获真实决定前完成 S，不能只用决策轮号缓存锚点，因为拒绝和即时操作也会提交新 S。反馈先持久化再给工具回执。宿主日志的操作记录与领域 E/S 分开提交；冷开仅补齐可证明已完成的回执，对没有提交的输入标中断，不从日志重放。

fork 应验证源游戏/分支与精确边界，建立子分支后保留源请求前的证据前缀，排除父分支未来。每个角色导出只含自身私有轨迹及允许的公共元数据；日志不会自动成为角色记忆。

## 7. 一款新游戏的交付检查

- 一个脚本跑完事先声明的有限玩法循环，核对资源及资金变化；失败角色仍进入结果统计。
- 角色材料能独立解释当前处境、可尝试行动、已发生结果和重要未完成事项；其他角色私有数据未泄漏。
- 当前存档可冷开，pending 不重问、不重复效果；fork 不污染源分支与其他角色。
- 真正通过产品宿主运行 Human 与真实模型，保留输入、输出、预算和干预；测试通过不能替代试玩。
- 修改共享模块时运行共享模块及山野镇消费者回归；更新启动说明、Schema 支持范围与验收证据。

这些是接线责任，不要求提前建设统一存档适配、万能 Context、组件注册中心或动态插件装载器。历史上出现两个消费者并不保证所有规则都已通用化。产品称为 Hillside 而类型仍为 `Cycle*` 不影响这些责任；无需为整理入口机械改动持久类型或 Schema。
