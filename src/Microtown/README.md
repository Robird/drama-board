# Microtown

这是独立小镇游戏的领域实现，完整里程碑仍在施工。`TownGameFactory.Create()` 创建三居民、市场/工坊/住区与 48 游戏小时的有限实验。引用共享 Kernel、Spatial、Inventory、Goals，不引用 ResourceCycle。Human Console 入口见 [Microtown.Cli](../Microtown.Cli/README.md)。

当前支持 `buy <item> <count>`、`sell <item> <count>`、`produce <recipe> <batches>`、`eat`、`wait <hours>`、`go <passage>`、`say <resident> <text>`、`give <resident> <item|money> <count>`、`offer <resident> <give-asset> <give-count> <want-asset> <want-count> <hours>`、`accept/reject/withdraw <offer-id>`、`continue`、`cancel` 和共享 Goals 命令。外界交易需在市场，生产需在工坊，移动复用 Spatial。默认库存无玩法容量上限；每居民只能一个耗时活动。

- 开始生产将原料从背包移至在制品，到期才计配方消耗和产出。取消生产一次退回全部原料，不产出、不返还已经消耗的饱食；饥饿失败退还未完成原料到遗产，失败者仍计入人口及最差生活值。实验结束保留未完成在制品。
- 生产启动、购买及接收赠物均检查容量与整数表示上限，并分别保证未来完工、取消退料两种互斥结果仍可结算；不是把两份未来库存相加预留。世界校验再次核对该不变量。
- 初始现金总额 54，三个居民各一份饭、饱食 60/100；每小时饱食减 3，饭加 40。原料、配方、外部有限供需见 `TownContent` / `TownMarket.Fresh`。刷新只重置外部市场，未伪造居民进口。
- 同刻顺序为饥饿失败、报价到期、市场刷新、Spatial 到达/路上接触、活动完成、实验结束、角色决定；同类候选采用 Kernel 的确定性仲裁。路上接触消费 Spatial 的 typed fact，暂不触发交流，避免预测被跳过或过期。恰好饥饿归零时才产出的饭不能挽救居民；报价恰好到期时不能再接受。
- 即时成功操作轮转服务其他空闲或待响应居民；拒绝保留当前角色。每次新服务先发布 `DecisionOpened` 完成 S，下一 Step 才调用控制器。`CreateControlledKernel` 只交付冻结 `TownObservation`；返回原始输入和操作身份，再由游戏解析。本人最新操作回执与最新活动经历分别保存，不允许控制器暗中保存世界。
- 说话和赠与只允许同地、存活且不同的两名居民。话语上限 512 个 Unicode 标量；物品赠与每次 1..1000 份，货币赠与为正整数。赠与同时结算双方并记入各自交互记录，不计外界进出口。话语是角色的陈述，不自动成为游戏规则或已验证事实。
- 等待或生产期间收到社交通知会获得响应机会。即时操作保留活动及待响应状态，并轮转其他居民；`continue` 结束响应，保留原活动 ID、到期时刻和在制品；`cancel` 结束等待或生产。移动仍不支持途中社交决策或取消。
- `TownEconomicTotals` 是同根累计账权威，逐边界核对现金及逐物品的背包＋在制品余额。钱、饱食、件数分别呈现，不合成评分。

`TownDurableHistory` 提供独立 DG 根/事件，二者都标记 `microtown` / GameRevision 3；`TownBusinessState`、`TownEventKind`、`TownFact`、`TownExperience` 的 Schema 升至 2，`TownCharacter` 仍为 Schema 2。当前只接受本修订，没有旧开发存档迁移器：旧档可能在 DG 对象还原时先因缺少 Schema 升级路径而拒绝，不能继续游玩或作为当前程序的决策前态分叉。无需还原业务图的分支列表及 Gym 日志列表/导出仍可用于查看旧证据。

Open 还原完成 S 和 pending E，再重建派生缓存，不执行事件或写盘；Kernel 的 `RecoverPending` 应用已保存结果，不重新询问控制器。`CaptureDecisionBoundary(actorId)` 捕获精确完成 S；拒绝后同一 DecisionSequence 对应新的 S。`Fork` 只接受指定源分支历史中的完成决策 S，保留原游标、lineage 和累计经济账，子分支独立延续。

`TownObservation` 只有本人私有状态及交互记录、同地居民公开身份/活动，以及可知的地图和经济规则。实时市场余量只在市场可见；其他地点只显示公共价格和刷新时刻。`TownTextRenderer` 仅从冻结值产生 Markdown，列出动作形状、合法数量范围和阻因。赠与只展示本人的可提供数量；对方是否可接纳在提交时检查，不公开对方库存与容量。

`TownInteractionState` 保存本人收发记录；一次交互两边共用由发送者/操作序号生成的 ID，各自使用本地序号。最多保存 32 条待交付、最近一次 opening 的全部新项（最多 32 条）及更早的 16 条，暂存上限 80。`DecisionOpened` 推进 `DeliveredThrough`，以 `OpeningAfter` 标出本次新项；它只表示纳入材料，不证明控制器已经阅读或回应。队列满时整次发送/赠与拒绝，未读不丢弃；裁剪只发生于新 opening，重复渲染不消费。本人活动结果继续由 `LastExperience` 单独保存，不挤占消息队列。

`TownTradeState` 保存定向双边报价，每腿只含一种物品或钱。创建与接受要求双方存活且同地；有效期为 1..24 整小时，物品每腿 1..1000 份，钱为正整数。创建时要求提出者持有完整付款，但不锁定库存或钱；接受时重新检查双方支付与转移后状态，一次结算双方，失败无部分转移。同资产双腿也先要求各自足额付款，不能用净额冒充支付能力；内部交换不计进出口。接收者可拒绝，提出者可撤回，二者不要求仍同地。失败或实验结束会关闭相关有效报价。

报价记录最多 24 条、每提出者最多 8 条，含尚未回收的终态；仅新建受名额限制，既有报价结算和关闭不依赖普通消息队列的余量。本人参与的有效报价独立于聊天历史持续显示，终态原位保留至双方决策材料纳入后，才可在后续参与者 opening 回收；失败者免除未来交付要求，不伪造阅读证据。重复呈现不重复结算，UI 不公开另一方交付游标。报价通知可唤醒等待/生产角色；即时交易保留原活动，`continue` 不接受或拒绝报价，`cancel` 活动不撤回报价。`TownTradeRules.CheckVisible` 只检查本人和公开条件；UI 的接受按钮是合法尝试，不能由它反推对方资产或容量。

本人饱食预览（TOWN-09）根据冻结观察显示不进食的耗尽时刻、在途/当前活动和相邻路线结束时的预计饱食，并给出生产的多批时长公式及示例。预览假设没有进食或其他干预；零或负值表示会在完成前或同刻失败，不是保存负饱食。超过实验终点会提示风险，合法数量不代表推荐数量，也不泄露别人的生存状态。

LLM/Gym 宿主通过 [Microtown.Completion](../Microtown.Completion/README.md) 和 [Microtown.Cli](../Microtown.Cli/README.md) 接入；领域库不依赖模型客户端或宿主日志。`InspectDecisionBoundary` 只读验证历史精确 S；`ReadCommittedOperations` 查询当前分支已完成 E/S 的操作回执，排除 pending 和初始快照中复制的旧回执。Gym 日志只是证据，不能授权重做游戏动作。

边界：报价是显式双边交易，不是通用撮合市场、库存托管或远程交易。有界交互/报价记录加最近一条操作回执/活动结果不是完整 Context 历史；长期承诺应写入 Goals。`TownDecision.World` / `CreateKernel` 保留为开发者脚本接缝，包含其他角色私有状态，**不能直接传给模型**。即时成功轮转不保证任意控制器都能推进时间；Kernel 的同刻护栏不替代宿主操作/模型调用预算。

`TownForecasts` 在运行时按居民、市场、报价、终点和全局决策 owner 维护纯派生预测；`Complete` 检查依赖后只重算受影响者，冷开全量 `Rebuild`，`ForecastOracle` 保留原全量推导供对照。空间继续使用已有到达投影、接触增量缓存与 Rebuild/Complete。预测缓存不落盘，也不改变当前 GameRevision 3。`TownFact` 保存完整不可变业务替换值和强类型空间事实；E 与 S 是独立持久图，恢复不依赖跨图引用相等，也不重新规划。领域状态及回执均已有 DG Schema。

验证：`dotnet test tests/Microtown.Tests/Microtown.Tests.csproj -c Release -m:1`。测试通过真实 Kernel 路径覆盖三居民 48 小时经济循环、市场刷新、竞争、拒绝、决定前完成边界、不可变旧快照、失败及在制品核对；不是实际模型试玩证据。

社交、忙碌响应、持久恢复与本人饱食预览的证据边界见[社交切片验收](../../docs/experiments/microtown-social-acceptance.md)。

报价切片的构建、测试与实际试玩证据集中在[报价交易验收](../../docs/experiments/microtown-trade-acceptance.md)；实现说明本身不表示验收已通过，也不代表真实模型已完成整个经济日。

owner 预测维护的全量 oracle、真实磁盘恢复、提交隔离与 25h 产品 Console 证据见[预测验收](../../docs/experiments/microtown-prediction-acceptance.md)。
