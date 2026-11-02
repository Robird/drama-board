# 经济融合编码起点

2026-11-01。C2／C4 的生产、市场与现金纵切已经验收；居民报价按下述合同实施。当前施工状态只维护在 [work-plan](work-plan.md)。

## 保持的合同

- 角色身体、背包、现金、活动、报价和经济账属于同一个业务根；一次交易或生产结算通过同一 Fact 与 E/S 发布，不能另起 TownWorld。
- 沿用 Cycle 的冻结 Context、个人 Goals、探索知识与跟随关系。Town 的身体模型、InteractionState、整根替换 Fact 不作为第二套权威迁入。
- pending 恢复只应用记录效果，不重问控制器、不重新 Plan；Fold 校验记录效果及其合法性，不能重新决定交付或结算结果。
- 零耗时经济交流不悄悄结束活动、改变 Due、脱队或确认原有警告；生产属于独占耗时活动，跟随者须先解除跟随。
- 报价不预留资产；接受时双方复查完整支付额、余额溢出和背包接纳，两个转移与报价关闭必须原子完成。同资产双向交换也不能用净额掩盖支付能力不足。

## 现金与物品守恒

参考 [TownEconomicTotals](https://github.com/Robird/drama-board/blob/b6fea5e94ca3ea718cee2546a2d785afa1bafa6d/src/Microtown/TownModels.cs)，迁移必须新增 `Harvested`，并将固定 meal 消费改为按实际物品记账：

```text
Σ所有角色现金 = InitialMoney + ExportIncome - ImportExpense

Σ所有角色背包(item) + 所有活动在制品(item)
= InitialItems + Imported + Harvested + Produced
  - Exported - Consumed - RecipeInputs
```

- 死亡角色的现金与背包仍计入遗产；不凭空删除资产。
- 资源点库存不计入持有资产账；再生不产生 Harvested，成功采集真正进入背包才记。
- 生产开工把输入从背包移入活动在制品，不立即记消耗；完成才记 RecipeInputs 与 Produced。
- 取消或死亡退回完整未消费输入；首版倾向整批完成，不做中间逐批产出。死亡退料应与终态同一 Fact 发布。
- 居民赠与、成交不改变进出口累计流。现金用非负整数，所有乘法、加法、数量和时间上界显式检查。

## 生产容量：两个互斥结局分别可达

沿用 [TownPlanning.CanSettleActivity](https://github.com/Robird/drama-board/blob/b6fea5e94ca3ea718cee2546a2d785afa1bafa6d/src/Microtown/TownPlanning.cs) 的合同：对拟变更后的背包，分别检查“装得下全部产物”和“装得下全部取消退料”。两者互斥，不能把所需容量相加，也不能只检查其中之一。

开工、购买、赠与接收、成交接收都需要此检查。它保证生产期间其他零耗时操作不会把角色锁在无法完成或取消的状态。活动保存配方身份、批次、输入物品与数量以及既有 Id／StartedAt／DurationTicks／Due；取消与死亡不得依赖重新查找已经消失的输入。

## 操作身份、过期与交付

- Cycle 的 operation sequence 在每个 Context 内编号；报价 ID 候选为 `trade:{actorId}:{contextSequence}:{operationSequence}`。不能照搬 Town 的 actor＋operation，否则跨 Context 会碰撞。
- 保留有限报价账与终态交付后回收；参考 [TownTradeState](https://github.com/Robird/drama-board/blob/b6fea5e94ca3ea718cee2546a2d785afa1bafa6d/src/Microtown/TownTradeState.cs)。只有实际发布的角色材料才能作为交付依据，死亡参与者可豁免未来交付，不能伪造已读。
- 推荐固定 `market` 和 `trades` owner：后者预测最早 active ExpiresAt，可在同一 Fact 关闭该时刻全部到期报价；空账时没有 prediction，但 binding 仍存在。
- 市场刷新、报价创建／关闭／回收必须更新对应 owner。不要只增加报价对象而依赖下次冷开重建才获得过期预测。
- 到期处理应先于同刻角色输入；`now >= ExpiresAt` 不允许接受。精确优先级与 ledger revision 形状在集成时裁决，并用全量 oracle 对照。
- 报价/成交材料只交付参与者；他人的现金、背包、远处位置不成为可见材料。参考 [TownTradeRules.CheckVisible/Check](https://github.com/Robird/drama-board/blob/b6fea5e94ca3ea718cee2546a2d785afa1bafa6d/src/Microtown/TownTradeRules.cs) 区分可见查询与完整提交检查。

## 已确认的接线陷阱

| 位置 | 集成必须核对的事项 |
| --- | --- |
| [CycleCharacter](../../../src/ResourceCycle/CycleCharacter.cs) | Change、WithSurvival、WithFollowing、End 等手动构造角色；新增 Money 必须全部保留。死亡清活动前先结算退料。 |
| [CycleModels](../../../src/ResourceCycle/CycleModels.cs)、[CycleFact](../../../src/ResourceCycle/CycleFact.cs) | 根 Replace／Validate、Fact 的所有 With 方法及 schema 修订必须包含新状态。 |
| [CycleFactApplication](../../../src/ResourceCycle/CycleFactApplication.cs) | raw input→kind、payload 限制、合法角色集合、原子资产变化和无关字段保留都需扩展；无关事实不得任意改钱或账。 |
| 同上 ValidateFollowingChanges | SurvivalFailed 目前限定死者＋释放跟随者；死亡关闭报价可能通知远处对方。应纳入准确交易通知集合，不能宽放任意角色。 |
| [CyclePredictionMaintenance](../../../src/ResourceCycle/CyclePredictionMaintenance.cs) | bindings、ConsumedOwner、Owners、Predict、ValidIdentity 与 oracle 同步；活动完成复用原 activity owner。 |
| [CycleInteractionRules](../../../src/ResourceCycle/CycleInteractionRules.cs) | Produce 需要关闭当前决策并形成 PendingAdvance；OpenContext 可能承担报价交付／回收。保持现有个人序号与恢复合同。 |
| [CycleObservedEvents](../../../src/ResourceCycle/CycleObservedEvents.cs) | 当前用 Enum.Parse 映射事实类型；新增 kind 必须有对应观察、接收集合及 typed payload。死亡通知不可冒充接收者本人死亡。 |
| [CycleObservation](../../../src/ResourceCycle/CycleObservation.cs) | 自己的钱、当地市场／配方、本人报价；同步材料类型、相等性、投递校验、OperationResults、Console 与 Completion。 |
| [CycleActions](../../../src/ResourceCycle/CycleActions.cs)、[CycleSurvival](../../../src/ResourceCycle/CycleSurvival.cs) | 当前 Use 将 item.food 以外的物品按水需求判断；新增口粮须同步供应效果、满值拒绝、名称和说明。 |

## 当前实现

`CycleBusinessState.Economy` 当前聚合 Market／Totals／Trades，角色新增 Money；LivingWorld 启用，基础采集夹具可不启用。未启用时拒绝经济命令且资金为零。这不是新增第二产品模式。Trades 保存有界报价与交付证据。

已接线 `buy <item> <count>`、`sell <item> <count>`、`produce <recipe> <batches>`、`give <actor> money <amount>`。前三种数量为 1–1000；金钱赠与使用独立 GiveMoney kind 与 long 金额，不改变采集 Count 的类型。`offer`、`accept`、`reject`、`withdraw` 已支持金钱与物品交换。

规则：买卖仅市场、生产仅工坊；报价与接受须同地，拒绝／撤回可在自己的地点决策窗口执行；途中保留现有限制。报价发送共用通信额度，接受及关闭已有报价不消耗发送额度，避免无额度可退出。忙碌／跟随只在已有决策窗口操作，不新增主动插话调度。

报价命令合同为 `offer <actor> <asset> <amount> for <asset> <amount> [hours]`，期限为 1–24 小时、默认 6 小时；`accept/reject/withdraw <tradeId>` 处理已有报价。资产为 `money` 或已知物品 ID；钱使用正 long，物品为 1–1000。报价账总量 24、每位发起者最多 8，含尚未完成终态交付的记录。状态为 Active/Accepted/Rejected/Withdrawn/Expired/ParticipantFailed；本世界没有固定实验 WorldEnded。

个人冻结观察提供 `Trades`，事件提供 `TradeChanges`；批量到期或死亡按收件人合并通知。远处对方死亡只带相关交易终止，不提供死亡地点、身体状态，也不伪装成观察者自己的死亡。交付证据采用实际发布的 Context Opening，先回收过去已完成双方终态交付的记录，再记本次交付；已打印/已读/同意不是同一个概念。

| 当前配方 | 输入→输出 | 耗时／用途 |
| --- | --- | --- |
| ration | 2 食物→2 口粮 | 工坊 1h／批，口粮恢复 55 食物，普通食物仍为 35。 |
| woodware | 2 干木→1 木器 | 工坊 2h／批，换取下次出行补给。 |

当前市场每日刷新有限额度：卖食物 3 钱×36 份、饮水 1 钱×60 份；收口粮 5 钱×18 份、木器 14 钱×12 份、原木 3 钱×12 份。每人初始 12 钱。加工收益对应真实耗时，库存不无限累积；这些数值须由实际循环和三日运行校验，不是稳定性结论。

当前实现见 [CycleEconomyRules](../../../src/ResourceCycle/CycleEconomyRules.cs)、[CycleEconomyValidation](../../../src/ResourceCycle/CycleEconomyValidation.cs) 和 [LivingWorldEconomyContent](../../../src/ResourceCycle/LivingWorldEconomyContent.cs)。生产订单记录原料、产出和批次快照。Fold 对精确资产变更、现金、累计账、订单和活动保留作独立校验；总账守恒本身不授权动作。地点记忆也必须来自本次真实位置与当地资源；消费必须产生所声明物品的准确身体增益。

## 实施顺序与文件分工

1. 主线程固定新增类型与命令签名；先生产＋市场，再居民交易，保持每片可运行。
2. 新经济状态／规则／内容文件与测试可独立一包；新交易状态／规则／生命周期测试可独立另一包。
3. 根、角色、Fact、Actions、Planning、Forecast、死亡退料由一个集成所有者串行修改；不要多代理同时争写这些闭集。
4. 接口稳定后交付冻结观察、材料校验、Console／Completion；共享命令与材料必须同源。
5. 验收普通命令两次出行返回与生产消费账、同刻市场竞争、生产中容量拒绝、死亡关闭报价、E-only 恢复与预测 oracle，再运行实际 Human Console 循环。测试不能只从内部 Plan 直接跳过操作与材料边界。
