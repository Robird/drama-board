# DramaBoard 术语表

本文统一当前核心与应用实例的概念、中文称呼和代码映射。规则细节仍由相应设计说明，实际能力以源码和测试为准；归档文档保留其历史语义。

使用约定：新文档首次出现概念时使用“中文名（EnglishName）”；引用具体 API 时保留准确标识符。概念不要求各有一个同名类。本表中的“概念名”不表示已经存在公共接口，也不构成未来框架的施工计划。修改概念含义或引入易混淆命名时，同步更新本表和受影响的当前文档；不批量改写历史记录。

## 角色、控制与交互

| 推荐术语 | 含义与边界 | 当前代码映射 / 状态 |
| --- | --- | --- |
| 角色（Character） | 世界内承受规则效果的对象，拥有技能、背包和活动。不能用 Player 指代角色状态。 | [CycleCharacter](../src/ResourceCycle/CycleCharacter.cs) |
| 角色控制器（CharacterController），亦称 Player | 为被服务的角色读取观察、选择并提交命令的决策提供者。可由人类、LLM 或脚本驱动；一个控制器可以服务多个角色，不预设一对一。此处 Controller 指决策职责，不指运动控制组件。 | 当前应用接口为 [ICycleCharacterController](../src/ResourceCycle/CycleActions.cs)，尚未提取通用引擎接口。 |
| 人类控制器（HumanPlayer） | 通过交互界面向人类取得角色决策。人类使用者、控制器和被控角色是三个不同概念。 | [PlayConsole](../src/ResourceCycle.Cli/PlayConsole.cs) 中的 `ConsoleCharacterController`；采集场景同一 Console 服务三个角色，探索场景服务一个角色。 |
| LLM 控制器（LlmPlayer） | 通过 LLM 调用或局部 tool-loop 产生角色决策。 | 概念预留；当前可玩实例未接入。 |
| 行动主体（Actor） | 某条命令、事实或规则中的“谁在行动”这一关系角色，不另立一种 Character 类别。 | ResourceCycle 的 `actorId` / `ActorId` 指向 `CycleCharacter.Id`；不因此要求所有 Spatial 实体都是角色。 |
| 观察（Observation） | 提供给某个控制器、围绕当前角色组织的信息；不等同于完整世界对象。 | [CycleObservation / CycleDecisionContext](../src/ResourceCycle/CycleObservation.cs) 由游戏构造角色观察与纯命令校验；PlayView 只接收观察来渲染 Markdown。 |
| 命令 / 行动意图（Command） | 请求做什么及其参数；可能被校验拒绝，尚不是已发生事实。日常可称“行动”，讨论协议时优先用 Command。 | [CycleCommand、CycleActions.Check](../src/ResourceCycle/CycleActions.cs) |
| 耗时活动（Activity） | 角色已启动、跨模型时间持续的过程；移动与等待/采集互斥。 | 等待/采集由 `CycleActivity` 表示；移动由 Spatial 的 `TraversingLocation` 表示，不复制到另一份 Activity 计时器。 |
| 感知通知（Notice） | 角色收到、等待控制器响应的业务信息，例如他人加入或资源耗尽；不是世界所有事件的副本。 | `CycleNotice`、`CycleCharacter.Notices`；会持久化。 |
| 决策请求 / 待决策（Decision request） | 当前应给予角色控制器交互机会。可因空闲或有待响应感知产生。 | `NeedsDecision`、`CyclePredictionKind.RequestDecision`；没有独立持久请求对象。 |
| 唤醒（Wake-up） | 服务决策请求：展示角色观察并等待合法操作。不是操作系统线程唤醒，也不意味着取消 Activity。 | `ICycleCharacterController.ChooseCommandAsync`；等待输入时暂停模型时间。 |
| 交棒 / 服务轮转（Decision rotation） | 多个角色待决策时，轮流选择下一个被服务者。保证服务机会，不保证资源公平分配。 | `CycleBusinessState.LastServedActorId`；查询和非法输入不交棒。 |

**“多人”需要说明计数对象。** 当前 ResourceCycle 的采集场景是“单个人类、共用 Console、多个 Character”，生存探索一期则为单角色；不能由三个角色推导出三个独立 Player 实例。未来多个 LLM 控制器与多个角色的绑定关系另行设计。需要精确表达时，用“多角色”或“多控制器”，不单写“多 Player”。

**唤醒不等于打断。** `continue` 确认感知、保留活动及原定到期时刻；`cancel` 取消当前活动、保留已获成果。收到感知和活动继续存在可以同时成立。

**控制器不私藏角色的跨决策记忆。** 已采纳[共享无状态控制器决策](design/stateless-character-controller.md)：游戏维护角色状态并构造动态上下文，控制器可共享运行设施，本次工具交互使用独立局部会话。三层状态归属、可见性及恢复边界以该文档为准；角色观察和纯命令校验已接入，LLM 工具会话尚未实施。

## 时间、预测与调度

| 推荐术语 | 含义与边界 | 当前代码映射 |
| --- | --- | --- |
| 模型时间（Model time） | 游戏世界时间轴；与人类思考、网络调用所用的墙钟时间分开。 | [ModelTime](../src/Kernel/Time/ModelTime.cs)，一个 tick 为 1 毫秒；不是 .NET `TimeSpan` 的 tick 单位。 |
| 模型时长（Model duration） | 两个模型时刻之间的时长。 | [ModelDuration](../src/Kernel/Time/ModelDuration.cs) |
| 逻辑时点（Logical instant） | 模型时间加同一时刻内的提交因果序号；用于区分模型时间相同的多次转换。 | [LogicalInstant](../src/Kernel/Time/LogicalInstant.cs)：`ModelTime + CausalOrdinal` |
| 到期时刻（Due） | 过程或候选预计应结算的模型时刻。它不保证结算成功；活动可能取消，采集可能无货。 | [CandidateDue](../src/Kernel/Scheduling/CandidateDue.cs)、`CycleActivity.Due`、`TraversingLocation.ArrivalDue`；是否持久化取决于所属概念。 |
| 预测（Prediction） | 基于当前业务状态推导的未来发生可能；可失效、重算、冷开重建。当前纯派生预测是运行时缓存。 | [CyclePrediction / CycleForecasts](../src/ResourceCycle/CycleForecasts.cs)、[ContactPrediction / SpatialContactPredictions](../src/Spatial/State/SpatialSimulationState.cs) |
| 候选（Occurrence candidate） | 提交给 Kernel 仲裁的一次可能发生项，携带稳定身份、到期时刻和规则私有数据。是预测对调度器的投影，不是事实。 | [OccurrenceCandidate](../src/Kernel/Scheduling/OccurrenceCandidate.cs)、`CandidateKey` |
| 候选枚举（Forecast） | 规则读取当前世界、产出候选的操作。不要把方法名 Forecast 理解成每次必须全量重算预测。 | [IOccurrenceRule.Forecast](../src/Kernel/Simulation/IOccurrenceRule.cs) |
| 发生项（Occurrence） | Kernel 一次选择并处理的单位；可能是自然过程到期，也可能是请求角色决策。一次发生项可产生多个业务事实。 | 候选经 Plan 形成转换；记录形式为 `OccurrenceEvent<TFact>`。明确“候选”或“已记录”阶段，避免统称已发生事件。 |
| 调度与仲裁（Scheduling / Arbitration） | 从规则提交的候选中选择下一项；同 Due 按世界 seed、稳定 key 确定性破平局。 | [OccurrenceScheduler](../src/Kernel/Scheduling/OccurrenceScheduler.cs)；ResourceCycle 的自然事件、活动完成、输入优先级由应用筛选实现。 |
| 预测所属单元（Prediction owner） | 负责某类预测的应用单元；更新时可以暂时没有候选。不是物品所有权。 | `CyclePrediction.OwnerId`、`CycleBinding.OwnerId`，例如 `actor:a:activity` 与 `actor:a:attention`。没有通用 Owner 接口。 |
| 依赖绑定（Binding） | 把事实种类和目标映射到可能受影响的预测单元，先粗筛再检查是否需要重算。 | `CycleBinding`；当前运行时数据，不是持久化 delegate 或通用事件总线。 |
| 待重算集合（Dirty set） | 本次转换中可能失效的单元/空间范围的去重集合；在最终业务态上更新。 | ResourceCycle 的 owner 集合、Spatial 的 passage 集合；实现层概念。 |
| 全量重建（Rebuild） | 从业务真相和确定性规则重新构造完整派生缓存，用于初始状态、冷开及对照验证。 | `CycleEngine.Rebuild`、`SpatialSimulation.Rebuild` |

“倒计时条（Countdown）”只作为过程的展示比喻：记录进度锚点或已决定的到期时刻，并维护预测，不逐 tick 递减计数。目前没有统一 `LocalTimeline` / `Countdown` 类型。

“回合（Turn）”不是 Kernel 的基础时间单位。讨论时明确是一次控制器决策、一次采集周期，还是一次发生项提交。世界并不等待所有角色各行动一次才统一推进。

## 事实、转换与恢复

| 推荐术语 | 含义与边界 | 当前代码映射 |
| --- | --- | --- |
| 转换规划（Plan） | 为选中的候选确定完整、有序的业务事实；尚未发布世界状态。可包含询问控制器，恢复时不能重新询问。此 Plan 不是控制器的长期目标树。 | `IOccurrenceRule.PlanSelectedAsync`、[TransitionDraft](../src/Kernel/Simulation/TransitionDraft.cs)、`CyclePlanning` |
| 业务事实（Fact） | 已确定的具体效果数据，可依次应用到业务状态；规划草稿中的 Fact 在 E 发布前还不是已记录历史。 | [CycleFact](../src/ResourceCycle/CycleFact.cs)、[GraphSpatialFact](../src/Spatial/Facts/GraphSpatialFact.cs) |
| 事实折叠（Fold） | 按顺序应用业务事实，得到新的业务态；不重新决定事实。 | `CycleEngine.Fold`、`GraphSpatialReducer` |
| 转换补全（Complete） | Fold 后纯粹维护派生缓存，返回准备好的运行时世界；不增加业务事实、不调用控制器。不是“角色活动完成”的同义词。 | Kernel 的 complete 回调、`CycleEngine.Complete`、`SpatialSimulation.Complete` |
| 状态转换（Transition） | 从一个已完成边界推进到下一个边界，包含该发生项的有序事实、业务态和缓存准备、校验及发布。多个子对象的变化可属于同一次转换。 | [SimulationKernel](../src/Kernel/Simulation/SimulationKernel.cs) |
| 业务状态（Business state） | 必须保存的世界真相和已作出的决定；包括活动参数、感知、消耗记录等，不包含纯派生预测。 | `CycleBusinessState`、`GraphSpatialState` |
| 运行时世界（Runtime world） | Kernel 操作的完整就绪视图，组合业务状态与派生缓存。不能把整个 wrapper 都当作存档根。 | `CycleWorld`、`SpatialSimulationState` |
| 事件记录（Event，E） | 一次发生项的原因 key、目标逻辑时点和有序业务事实的记录。不是一条 UI 消息。 | [OccurrenceEvent](../src/Kernel/Journal/OccurrenceEvent.cs) |
| 状态发布（State publication，S） | 发布该边界的业务状态与 Kernel 游标。E/S 是两个发布边界，不能称为一次不可分割的磁盘事务。 | [CycleDurableRoot](../src/ResourceCycle/Persistence/CycleDurableRoot.cs)、`CycleDurableHistory` |
| Kernel 游标（Kernel cursor） | 标明已完成的世界版本、逻辑时点与原因，和业务状态一起保存。不同于 Console 的角色服务游标。 | [KernelCursor](../src/Kernel/Simulation/KernelCursor.cs) |
| 待完成事件（Pending E） | E 已发布，而对应 S 尚未完成发布的记录。兼容语义下按记录 Fold、Complete、Validate，再发布 S；跨游戏修订由应用协调转换 State/E，不重新仲裁、Plan 或询问控制器。 | [E/S 合同](design/durablegraph-occurrence-persistence.md)、[游戏修订决策](design/game-initialization-and-revision.md)；加载仍保留 pending，正常恢复负责完成。 |
| 单根发布（Single-root publication） | 在同一个 S 根下保存业务对象图及游标，使背包、资源、角色活动等共同组成一致边界。 | 当前 ResourceCycle 的 durable root；不表示 .NET Task、调用栈或外部 LLM 调用也自动持久化。 |

单写“事件”时容易混淆：说明是在讲候选发生项、业务事实、E 记录、依赖绑定所匹配的事实种类，还是给角色的感知通知。它们可以相关，但不是同一个对象。

## 初始化与版本修订

| 推荐术语 | 含义与边界 | 当前代码映射 / 状态 |
| --- | --- | --- |
| 新游戏工厂（New-game factory） | 组装当前规则下的初始领域对象图及起始游标；可以组合程序化生成器，不负责升级旧世界。 | `CycleGameFactory.CreateBatch` / `CreatePlayable`；发布由 `CycleDurableHistory.Create` 负责。 |
| 游戏修订号（GameRevision） | State/Event 各自表示的游戏语义版本，使用同一编号体系；独立于 Schema 版本和模拟转换次数。 | `CycleDurableRoot.GameRevision` / `CycleDurableEvent.GameRevision`，生产当前值为 `CycleGameRevision.Current`。 |
| 游戏状态修订（Game revision） | 加载时协调升级 State 与可选 pending E 的内存表示；不执行事件、不推进游标、不写盘，pending 工作留给正常流程。 | `CycleGameRevision.UpgradeLoadedBoundary`；[实施合同](design/game-initialization-and-revision.md)，生产只接受当前版本，跨版本政策由夹具验证。 |
| 模拟世界版本（WorldVersion） | 模拟谱系及已完成转换次数，标识模拟位置；不表示游戏内容或规则修订。 | [WorldVersion](../src/Kernel/Simulation/WorldVersion.cs) |


## 空间

| 推荐术语 | 含义与边界 | 当前代码映射 |
| --- | --- | --- |
| 空间实体（Spatial entity） | 拥有客观位置的实体；空间核心不要求其具备角色、背包或控制器。 | [SpatialEntity](../src/Spatial/State/GraphSpatialState.cs)、`EntityId` |
| 地点（Place） | 可区分的稳定语义地点，构成空间图的节点。不是 UI 面板或叙事场景对象。 | `PlaceId`、[GraphDefinition](../src/Spatial/Definitions/GraphDefinition.cs) |
| 通道（Passage） | 两个地点间有长度、有身份的连接，可分别控制从两端进入的权限。 | `PassageDefinition`、`PassageId`、`PassageEntryAccess` |
| 客观位置（Spatial location） | 实体当前唯一的空间位置：在地点，或处于通道运动段。不同于角色对地图的知识。 | [SpatialLocation](../src/Spatial/State/SpatialLocation.cs)：`AtPlaceLocation` / `TraversingLocation` |
| 运动段（Movement segment） | 用通道、锚点位置/时间、目标端点、速度快照和到达时刻描述的当前运动；不逐帧更新坐标。 | `TraversingLocation`、`SpatialEntity.MovementGeneration` |
| 路线（Route） | 从起点到目标的一组通道行程，不意味着已启动或已完成移动。 | [RouteLeg、RouteFound](../src/Spatial/Navigation/RouteModels.cs)、`SpatialNavigator` |
| 相遇 / 追及（Passage contact） | 当前两条通道运动段的交会，包含迎面相遇与同向追及。不是自动战斗、碰撞伤害或角色感知。 | [PassageContactKind、PassageContactKey](../src/Spatial/Contacts/PassageContact.cs)；预测为 `ContactPrediction`。 |
| 已消费交会（Consumed contact） | 指定当前运动段组合的交会已经处理，用来避免重复触发；这是发生过的历史，不能仅靠几何重建。 | `GraphSpatialState.ConsumedContacts`，与运行时 Contacts 预测缓存区分。 |
| 到达（Arrival） | 结束通道运动段、进入目标地点的空间转换。与通道内交会不同。 | `TraversingLocation.ArrivalDue`、空间事实与规则；到达向上取整，交会采用现有 floor 语义，详见[交会时刻说明](worksets/passage-contact-floor.md)。 |

ResourceCycle 的原 `--play` 采集场景保持单地点；`--survival` 探索场景使用 Spatial 的地点/通路/移动，并校验采集目标实际位于角色地点。移动中没有第二份游戏位置或到达期限。

探索子图 `CycleExpedition` 保存场景身份、Spatial 业务状态、结局和按角色/地点的最近观察 `CycleVisit`。历史观察附时间，不等于远处资源的实时真相；观察记忆属于游戏状态，运行时 `CycleObservation` 是其投影。

## 采集实例与层次边界

| 推荐术语 | 含义与边界 | 当前代码映射 / 状态 |
| --- | --- | --- |
| 资源点（Resource node） | 储存可采份额、按条件再生并定义产出的世界对象；不等同于产出物品。 | [CycleResource](../src/ResourceCycle/CycleResource.cs)：`Id` 与 `ItemId` 分开。 |
| 库存 / 余量（Stock）与容量（Capacity） | 资源点当前可被采走的份数与最大份数。 | `CycleResource.Stock` / `Capacity` |
| 再生（Regeneration） | 累积有效生长时间后补充资源点库存；与角色采集过程独立。 | `RegenerationTicks`、`ActiveSince`、`AccumulatedTicks` |
| 采集批次（Gathering batch） | 一次接受的有限成功目标，内部可以连续执行多个周期；`all` 固定为接受时库存快照，不是预留。 | Gather 种类的 `CycleActivity`：`TargetCount`、`CompletedCount`、`SelectedAll` |
| 采集周期（Gathering cycle） | 尝试取得一份资源的耗时过程；到期成功才扣一份库存并给一件物品，失败可结束批次。 | `CycleActivity.DurationTicks`、`Due`、`CycleIndex`；成功事件 `GatherYielded`。 |
| 物品库存（Item inventory）与背包（Backpack） | 物品 ID 到数量的不可变快照；背包是角色对它的一种用途，独立于资源点余量。 | `DramaBoard.Inventory.ItemInventory`；角色属性仍为 `CycleCharacter.Backpack`。 |
| 总件数容量（Unit capacity） | 所有物品数量之和的可选上限，不是物品种类数或资源点容量；null 表示无玩法限制，0 表示零容量。 | `ItemInventory.UnitCapacity` 持久化；`TotalCount` 派生；默认 demo 不设置上限。 |
| 库存接纳（Inventory admission） | 当前快照能否接纳单份物品的局部查询；不预留，成功/失败如何改变活动由游戏决定。 | `ItemInventory.CheckAdd` / `InventoryAdmission`；单项表示上限优先于总容量上限。 |
| 内核（Kernel） | 通用发生项调度、转换与发布协议。 | `src/Kernel` |
| 空间层（Spatial） | 客观图空间、运动及交会规则。 | `src/Spatial` |
| 宿主（Host） | 驱动 Kernel 运行的程序层职责，不等同于角色控制器。 | [SimulationHost](../src/Host/SimulationHost.cs)、应用 Console 入口 |
| 规则包（Rule suite） | 可组合的领域规则集合，如采集、战斗等；提取需具体用例证据，不强制等待多个完整游戏。不是 Kernel 的 `SimulationRules` 配置对象。 | 当前规则在 ResourceCycle 应用层，尚无通用 RuleSuite 框架；方向见[分层设计](design/layering-and-composition.md)。 |

采集规则详见[多人有限批次采集](design/resource-cycle-multiplayer-gathering.md)。新增术语优先解释真实代码/业务边界，不为凑齐名词而建立抽象类型。

## 模块与游戏组合

| 推荐术语 | 含义与边界 | 当前代码映射 / 状态 |
| --- | --- | --- |
| 领域模块（Domain module） | 具有明确状态、局部规则及协作合同的一项领域能力；不要求独立程序集、统一组件基类或独立提交。 | Spatial、Inventory 已独立；ResourceCycle 的资源、活动和跨模块采集编排仍归游戏。 |
| 组件（Component） | 模块内可组合的对象或能力的宽泛称呼；使用时说明具体职责，不默认指 ECS 或某个 `IComponent`。 | 尚无通用组件协议；设计讨论优先使用具体领域名称。 |
| 游戏组合（Game composition） | 选择并连接模块，协调跨模块行为、世界根、初始化及整体修订的职责。 | 当前分布在 `CycleGameFactory`、`CyclePlanning`、`CycleEngine` 和游戏持久化入口，不对应一个新接口。 |
| 扩展内容 / 模组（Mod） | 内容、规则或完整游戏的一种来源，按其职责接入，并非固定的最上层。 | 方向为源码引用、共同编译与显式接线；尚无 mod 加载器或稳定扩展 API。 |

职责与依赖决策以[分层与组合设计](design/layering-and-composition.md)为准；文件/程序集调整次序见[重构方案](implementation/modularization-plan.md)。
