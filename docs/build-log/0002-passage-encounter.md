# Build Log 0002：Passage Contact 与单响应 Encounter 竖切

> 状态：**Ready for implementation**
> 规划日期：2026-08-22
> 实现基线：`2b6761c docs(build-log): close TravelTo implementation`
> 上位设计：[Design Note 008：Graph Spatial World](../开放世界棋盘游戏设计_008_Graph_Spatial_World.md)
> 前置竖切：[Build Log 0001：TravelTo](./0001-travel-to.md)
> 重开裁决：只重开 Design Note 008 的 Slice 2 MVP，并在同批交付可复用 Contact framework 与真实 FirstBoard / Player consumer；不把二者拆成孤立批次。
> 架构澄清：Contact Forecast / Plan / fact / reducer 是 `Spatial` 的第一等可复用框架能力；FirstBoard 只包装该 occurrence 并原子追加 Game encounter 语义。

## 1. 批次目标

下一批交付一条可玩的途中相遇闭环：

```text
两名 Actor 在同一 Passage 上形成严格内部交点
→ Spatial Forecast 一个局部 contact candidate
→ Spatial contact + Game encounter 同 batch 提交
→ 一个已注册 Player 在途中收到紧凑 observation
→ 选择 Continue 或 Reverse
→ Continue 保持原 movement 与 TravelGoal
→ Reverse 原子反向 movement，并结束该 Actor 的 TravelGoal
→ arrival / TravelGoal / 普通 DecisionPoint 从新的 committed world 继续
```

这批工作的产品价值不是“记录两条线相交”，而是让 Player 的委托导航在真正值得主意识介入的途中事件上暂停，并允许 Player 改变方向。它是 `TravelTo` 所建立的 Player Agency 边界的第一个关键事件 consumer。

本批仍服从这些总约束：

- 无旧数据、旧 API 或旧 persistence codec 兼容；当前格式直接演进。
- 简单性、灵活性优先，不为审计、规模或尚不存在的 encounter 类型预建平台。
- `src/Kernel/**` 与 `src/Host/**` 预期零修改；所有 contact、response、arrival 与 entry change 继续参加 Kernel 全局仲裁。
- Spatial 拥有客观 motion、Contact 的完整 occurrence API、contact truth、Reverse 与 pair-local consumption。
- FirstBoard 不实现或复制 Contact 预测；它只拥有“本次 Host 是否把这个 Contact 提升成 Encounter、谁作出回应以及 TravelGoal 如何结束”。
- FirstBoard.Demo 只展示 committed facts/state，不 Forecast、不 Plan，也不注册事件处理器。
- Player 只看到已提交 encounter 的局部材料；future contact、exact fraction、全图 occupancy 与 PRF rank 不得泄露。
- 不引入 fixed contact phase、暂停时间、外部 command gateway 或空 draft。

## 2. 为什么现在重开 Slice 2

Design Note 008 要求 Contact 必须与真实 Game/AI consumer 同时交付。此前不满足这一条件；`0001-travel-to` 完成后，当前代码已经具备：

- [`GraphSpatialState`](../../src/Spatial/State/GraphSpatialState.cs) 是唯一 objective location authority；Actor 在 Passage 中不再投影回起点。
- [`TravelGoalRule`](../../src/FirstBoard/FirstBoardSystems.cs) 能在每个 `AtPlace` prefix 自动规划下一腿，并在关键状态结束后恢复普通决策。
- [`DecisionPointRule`](../../src/FirstBoard/FirstBoardSystems.cs) 已有 frozen request、Player correlation、validator 与原子 Host draft 的完整形状。
- FirstBoard 默认地图已有 Alice、Bob 与可双向进入的 Tavern—Market Passage，可以构造稳定的迎面相遇 trace。
- 当前 persistence、replay、fork 与 Demo 已理解 composite Game + Spatial batches。

基线验收为 standard solution 254/254、local solution 272/272，Demo 0 warning / 0 error；下一批的回归结果不得低于这个已落地能力集合。

因此现在可以交付“发现接触 → 打包关键事件 → Player 回应 → 客观运动改变”的完整产品路径，而不是孤立的 pair math。

当前明确断链：

- [`TraversingLocation`](../../src/Spatial/State/SpatialLocation.cs) 只能表示 endpoint-to-endpoint segment，无法在途中整数时刻 Reverse。
- [`GraphSpatialState`](../../src/Spatial/State/GraphSpatialState.cs) 没有 current-segment contact consumption。
- [`SpatialOccurrenceRule`](../../src/Spatial/Simulation/SpatialOccurrenceRule.cs) 只 Forecast entry change 与 arrival；当前还没有可供所有 Host 复用的 Contact occurrence rule。
- FirstBoard 没有 pending encounter、途中 observation、response candidate 或 Continue/Reverse intent。
- 普通 `DecisionPointRule` 只服务 idle `AtPlace` Actor，不能直接复用为途中决策。
- `TravelGoal` 在 Reverse 后若仍保留，会在 Actor 返回上一 Place 后再次自动朝原目标出发，使 Reverse 失去实际含义。

### 2.1 当前已有的组合接缝

项目已经有正确但尚未被明确命名的 engine/framework → application 组合形状：

```text
Kernel
    IOccurrenceRule<TWorld, TCandidateData, TFact>

Spatial framework
    SpatialOccurrenceRule
        IOccurrenceRule<GraphSpatialState, SpatialOccurrenceData, GraphSpatialFact>

FirstBoard application
    SpatialHostOccurrenceRule
        project FirstBoardWorld → GraphSpatialState
        delegate inner Forecast / Plan
        lift GraphSpatialFact → FirstBoardFact
```

[`SpatialHostOccurrenceRule`](../../src/FirstBoard/FirstBoardSystems.cs) 已证明 application adapter 可以原样保留 inner CandidateKey / Due，并把 inner Spatial facts 提升到 composite Host fact union。`src/Host` 当前只提供运行循环，没有通用 rule decorator/composer；本批不为一个使用点提前建立高阶泛型包装平台。

Contact 沿用这个接缝，但多一步 Game enrichment：外层 FirstBoard rule 调用 inner Spatial Contact rule 得到同一个 objective candidate 与 Spatial draft，再追加 `EncounterOpened`。不得让 Spatial 反向调用 FirstBoard callback，也不引入 event bus；依赖方向继续是 `FirstBoard → Spatial → Kernel`。

若第二个独立游戏原型以后重复了完全相同的 project/lift/enrich 样板，再考虑把薄 adapter 提取到 `Host`。当前优先让 Spatial Contact rule 本身成为清晰、稳定、可独立运行的框架 API。

## 3. 冻结的 MVP 裁决

### 3.1 一个共享 pending encounter

`FirstBoardGameState` 在 MVP 中最多保存一个：

```text
PendingPassageEncounter?
    ContactKey
    Kind: HeadOnMeeting | Overtake
```

这是 FirstBoard 回应策略的有意识限额，不是 Spatial capacity，也不是 framework API 限额：

- `GraphSpatialState` 不保存 pending encounter；它可以同时保存多个仍属于 current segments 的 consumed contact keys。
- Spatial Contact rule 始终能够从任意 `GraphSpatialState` 枚举全部客观 contact candidates；一个 contact 不会消费同 tick 的其它 pair。
- 有 pending encounter 时，只是 FirstBoard 外层 adapter 暂不把新的 Spatial contact candidates 提升成 Game encounter openings。
- 当前 encounter resolve 后，其它已经到期且仍成立的 contact 会在同一 model time 重新 Forecast。
- 已消费 key 保证原 contact 不复发；未消费 peers 不会被吞掉。

不建立 encounter list、队列、优先级、phase 或 capacity service。真实玩法证明必须同时保留多个 pending encounter 时，再把单值扩成 canonical collection。

### 3.2 一个共享 encounter 只产生一次 Player 回应

对 pending encounter 的两个参与者：

- 只为既是 `BoardActor`、又在本次 Host 注册了 `IPlayerDriver` 的参与者 Forecast response candidate。
- 一个参与者有 driver 时，由该 Player 回应；两个都有 driver 时，两人的 response candidates 同时参加 Kernel PRF 仲裁。
- 第一个被选中的 Player 作出 Continue 或 Reverse 后，整个共享 encounter resolved，另一参与者不再获得第二轮回应。
- 没有 driver 的 Actor 可由测试或 Game rule 作为简单装置移动；本批不建立 NPC cognition/decision framework。
- FirstBoard encounter opening 至少要求一个可响应 Player；纯规则 Actor 之间的交点不会被 FirstBoard adapter 提升或消费，但这不限制 Spatial rule 对它们的客观 Forecast 能力。

这表达“谁先对相遇作出反应”，并保持一个 encounter 只有一次不可逆回应。双方协商、轮流反应、战斗回合和 delayed multi-actor response 继续延期。

`DecisionPointRule` 同步收紧为只 Forecast 已注册 driver 的 Actor。driver registry 因而成为本次 run 中“哪些 Actor 有 conscious Player”的最小 Host 配置；现有 Alice/Bob 都注册 driver 的运行方式保持原行为。

### 3.3 TravelGoal 与回应

| 回应 | Spatial movement | 当前 Actor 的 TravelGoal | Player decision 计数 |
|---|---|---|---|
| Continue | 不改变 | 保留 | `DecisionSequence + 1` |
| Reverse | 创建反向 anchored segment | 若存在则清除，结果记为“因途中相遇而中断” | `DecisionSequence + 1` |
| WorldChanged | 已被 arrival/remove/其它 motion change 改变 | 保留 | 不增加 |

Reverse 清除 TravelGoal 由 `PassageEncounterResolved` 这一 Game fact 直接表达，不再增加一类只为审计旅行原因而存在的 fact。返回上一 Place 后，Actor 获得普通 DecisionPoint，而不会被旧目标立即再次掉头。

pending encounter 会 suppress 其参与者的普通 DecisionPoint 与 TravelGoal continuation。若 arrival 先赢导致 encounter 失效，先提交非 Player 的 `WorldChanged` resolve；随后 TravelGoal 或普通 DecisionPoint 才从新 world 重新出现。这是状态依赖，不是同 tick phase。

## 4. Spatial：Anchored traversal

直接替换当前 `TraversingLocation` 的字段；不保留旧 constructor 或兼容类型：

```text
TraversingLocation
    PassageId
    AnchorOffset: Int64             // 0..Length
    AnchorTime: ModelTime
    TargetPlaceId                   // Passage 的 A 或 B endpoint
    SpeedSnapshot: Int64            // > 0
    ArrivalDue: ModelTime
```

endpoint start 是 anchored traversal 的特例：

```text
start A → B:
    AnchorOffset = 0
    AnchorTime = now
    TargetPlaceId = B

start B → A:
    AnchorOffset = Length
    AnchorTime = now
    TargetPlaceId = A
```

唯一位置法则：

```text
OffsetAt(segment, at)
    require AnchorTime <= at <= ArrivalDue
    if at == ArrivalDue: return TargetOffset

    elapsed = at - AnchorTime
    advanced = elapsed * SpeedSnapshot
    return target is B
        ? AnchorOffset + advanced
        : AnchorOffset - advanced
```

`ArrivalDue = AnchorTime + ceil(abs(TargetOffset - AnchorOffset) / SpeedSnapshot)`。`at < ArrivalDue` 时 offset 必须严格未越过目标；`at == ArrivalDue` 但 arrival 尚未提交时仍是 endpoint-boundary `Traversing`。

arrival CandidateKey 改为编码完整当前 anchor/target motion fields。State、Fact、Journal 不同时保存旧 endpoint segment 与 anchored segment 两套 identity。

### 4.1 Reverse

新增：

```text
TraversalReversedFact(
    EntityId,
    ExpectedMovementGeneration)

SpatialPlanner.TryReverseTraversal(
    state,
    entityId,
    at)
```

planner 与 reducer：

1. 要求 Entity 当前 `Traversing` 且 generation 精确匹配。
2. 要求 `AnchorTime < at < ArrivalDue`，并在整数 `at` 物化严格内部 `currentOffset`。
3. 新 target 是旧 target 的另一 endpoint。
4. Reverse 是新的方向承诺：旧 A→B segment 反向时检查 `EnterableFromB`；旧 B→A 则检查 `EnterableFromA`。
5. 以 `currentOffset / at / same speed / opposite target` 建立新 segment，`MovementGeneration + 1`。
6. 清理引用旧 movement generation 的 consumed contact keys。

零进度、endpoint boundary、反方向 entry 关闭、generation stale 或时间溢出稳定拒绝。Continue 不创建 segment，也不检查 entry access。

## 5. Spatial：Contact truth 与局部消费

### 5.1 最小类型

新增 public domain values：

```text
PassageContactKey
    PassageId
    canonical(EntityA, MovementGenerationA)
    canonical(EntityB, MovementGenerationB)

PassageContactKind
    HeadOnMeeting
    Overtake

PassageContactOccurredFact(
    ContactKey,
    Kind)
```

`PassageContactKey` 在构造时按 `EntityId` Ordinal canonicalize，拒绝同 Entity、自身重复或负 generation。它既是 Spatial current-segment consumption key，也是 FirstBoard pending encounter 的稳定 domain identity。

`GraphSpatialState` 增加 canonical `ConsumedContacts[]`。它只引用当前 active segments，不是历史账本：arrival、reverse、remove 或任何替换 motion law 的 reducer 都删除涉及旧 segment 的 keys。

### 5.2 Exact math 不越过领域边界

新增第一等、public、可独立测试和注册的 Spatial occurrence rule：

```text
SpatialContactOccurrenceRule
    : IOccurrenceRule<
        GraphSpatialState,
        PassageContactOccurrenceData,
        GraphSpatialFact>
```

它拥有完整的 Contact Forecast / selected Plan，并在内部使用 `BigInteger` 或等价 widened rational 运算。`PassageContactOccurrenceData` 和 public candidate 结果只含：

```text
CandidateKey
CandidateDue: ceil(exact intersection)
PassageContactKey
PassageContactKind
```

exact numerator/denominator 不进入 Candidate data、World、Fact、Journal、query、snapshot 或 Player observation。

每个 unordered current-segment pair 只在以下条件全部成立时产生 candidate：

- 两者在同一 Passage，且 key 尚未 consumed。
- 在共同 motion window 内存在唯一交点。
- exact contact 严格晚于共同 window 起点。
- exact contact 严格早于双方各自的 physical exit；endpoint contact 不报。
- 相同 worldline / CoTravel 不报；`tau == 0` overlap 不报。
- `ceil(contactTime) >= committed world.Now`；已经进入同一整数 tick 时仍保留 `Due == Now` 的未消费 contact。

同向追及为 `Overtake`，反向交会为 `HeadOnMeeting`。reference Forecast 直接按 Passage 分组并做 `O(Σn²)` pair scan；不建 index、pair cache 或容量平台。

Contact CandidateKey 使用 canonical structured bytes：

```text
["graph-spatial/contact",
 PassageId,
 EntityA, GenerationA,
 EntityB, GenerationB]
```

selected Plan 从 committed state 重算 pair math，验证 key、kind、due 与 current segments 后生成一个 `PassageContactOccurredFact`。Reducer 在 batch instant 再做同一真实性验证，然后只加入自己的 consumed key，不移动、不重锚、不改速。

`SpatialContactOccurrenceRule` 可以在 Spatial-only simulation 或只需要 objective Contact facts 的未来游戏中直接注册。它不依赖 FirstBoard、Player、Protocol、Host implementation 或任何 handler callback。

### 5.3 三种 ownership 必须分开

| Ownership | Owner | 精确含义 |
|---|---|---|
| 领域预测与事实真实性 | Spatial | 谁会相遇、何时成为 candidate、key/kind、selected Plan、fact fold 与 consumed progress |
| 当前 composite Kernel 的注册 | FirstBoard outer rule | 哪一个外层 rule 把该 CandidateKey 交给 Kernel，并返回完整 `FirstBoardFact` draft |
| Game 回应策略 | FirstBoard Game | Contact 是否形成 Encounter、pending 限额、Player affordance、TravelGoal 后果 |

“FirstBoard 是唯一 production owner”只能指第二行：在 **FirstBoard 这一个 composite Kernel** 中，只注册外层 adapter，不能同时直接注册 inner `SpatialContactOccurrenceRule`，否则同一 CandidateKey 会被两个 rules Forecast。它绝不表示 Contact 算法、candidate 或 Spatial fact 属于 FirstBoard。

未来应用可以选择：

- 直接注册 `SpatialContactOccurrenceRule`，只提交 objective Spatial fact；或
- 像 FirstBoard 一样用 application rule 包装它，在同一个 outer draft 中追加自己的 Game facts。

两条路径共享同一个 Spatial Forecast / Plan / reducer，不允许 application 重算交点、重新生成 CandidateKey 或维护第二份 consumed state。

本文所说的“事件处理器”必须按领域拆开理解：Spatial 层的处理器就是 inner rule 的 selected `PlanSelectedAsync` 加 `GraphSpatialReducer`，负责把客观 Contact 变成权威 Spatial fact/state；Game 层的处理器是 outer rule 的 enrichment，负责把同一 occurrence 解释为 Encounter。Spatial 不应持有一个回调到 FirstBoard 的 `Action<PassageContact...>`，否则会反转程序集依赖并把具体游戏政策塞进框架。

## 6. FirstBoard Game state 与 facts

在 `FirstBoardGameState` 增加：

```text
PendingPassageEncounter? PendingEncounter
```

只新增两类 Game payload：

```text
PassageEncounterOpenedEvent(
    PassageContactKey ContactKey,
    PassageContactKind Kind)

PassageEncounterResolvedEvent(
    PassageContactKey ContactKey,
    string? RespondingActorId,
    PassageEncounterResolution Resolution)

PassageEncounterResolution
    Continued
    Reversed
    WorldChanged
```

Reducer 语义：

- Opened 要求当前没有 pending encounter，contact key 已由同批较早的 Spatial fact consumed，且两个参与者都是 FirstBoard actors，其中至少一个存在于 Host 的可响应集合由 rule 保证。
- Continued / Reversed 要求 exact pending key、非空 responder、responder 属于 pair；清 pending，并只把 responder 的 `Generation`、`DecisionSequence` 各推进一次。
- Continued 保留 TravelGoal；Reversed 清 responder 的 TravelGoal，并记录 last outcome。
- WorldChanged 要求 exact pending key、`RespondingActorId == null`；只清 pending，不推进任何 Player decision sequence，也不清 TravelGoal。
- world validator 允许 pending key 因 arrival/reverse/remove 而暂时不再引用 current segments；这种合法 stale state 必须由 response rule Forecast cleanup，不能让 reducer 静默删除 Game state。

不保存 contact exact time、offset、future response、双方 route、Player prompt 或 Journal address。

## 7. 两条 FirstBoard composite rules

### 7.1 FirstBoardPassageEncounterRule（composite adapter）

职责：把可复用的 objective Spatial Contact occurrence 与 FirstBoard Game encounter 原子连接。它是 application adapter，不是 Contact detector。

内部持有：

```text
SpatialContactOccurrenceRule _inner
```

Forecast：

1. 若已有 pending encounter，返回空。
2. 调用 `_inner.Forecast(world.Spatial, rules)` 枚举所有 objective pair candidates。
3. 只保留双方都是 `BoardActor` 且至少一方有注册 driver 的 contacts。
4. 原样保留 inner CandidateKey、Due 与 `PassageContactOccurrenceData`，只把 data 提升进 FirstBoard candidate union。

selected Plan 先重验 FirstBoard consumer 条件，再调用 `_inner.PlanSelectedAsync(world.Spatial, innerWinner)` 完成全部 Spatial 重验与 planning；最后把 inner Spatial draft 与 Game fact 合成固定顺序：

```text
Spatial(PassageContactOccurred)
Game(PassageEncounterOpened)
```

两者一个 batch 全成全败。在 FirstBoard Kernel 中不得同时直接注册 `_inner`；其它游戏是否直接注册它由各自的 Host composition 决定。

### 7.2 FirstBoardPassageEncounterResponseRule

Forecast：

- 无 pending：空。
- 两个 contact segments 仍精确 current：为每个有注册 driver 的参与者产生一个 response candidate，Due=`world.Now`。
- 任一 segment/generation 已变化：只产生一个 automatic cleanup candidate，Due=`world.Now`。
- exact contact 仍有效但没有任何注册 responder：抛 Host configuration invariant，不生成 no-op，也不伪装成 WorldChanged。

response candidate key 使用 canonical structured bytes，并包含 encounter identity、Responder ActorId、Actor Generation 与下一 DecisionSequence；cleanup key 包含 encounter identity 与 `world-changed` tag。

selected Player candidate：

1. 重验 pending、两个 current segments、responder、candidate key/due。
2. 构造 frozen encounter request。
3. Continue 始终 advertised。
4. 仅当 `TryReverseTraversal` 在 frozen world 接受时 advertised Reverse。
5. 调用 selected actor 的 Player 一次，并通过通用 validator。
6. Continue 返回一个 Game resolved fact。
7. Reverse 返回固定顺序：

```text
Game(PassageEncounterResolved(Reversed))
Spatial(TraversalReversed)
```

若已 advertised Reverse 却在同一 frozen world 被 Spatial 拒绝，属于 Host invariant error；整个 batch 零提交。

selected cleanup candidate 不调用 Player，提交：

```text
Game(PassageEncounterResolved(WorldChanged))
```

rules 注册顺序不表达优先级。Contact、response、arrival、entry change、deadline、其它 Actor 的 DecisionPoint 仍由 Kernel 对当前全部 candidates 全局仲裁。

## 8. Player observation 与协议

新增 stable action kinds：

```text
action.continue-travel
action.reverse-travel
```

两种 intent 均不得携带 actor/object/exit/destination/duration/until；可保留可选 `FreeText`，但本批不把途中台词另存为 Game fact。

不扩张 `Observation` DTO。Encounter request 使用现有字段表达：

- `LocationId = PassageId`。
- `Exits = []`。
- `VisibleActorIds = [counterpart actor id]`，Ordinal 排序。
- `VisibleObjectIds = []`。
- `KnownFacts` 至少包含当前 travel 的 from/toward/ETA、contact kind、counterpart，以及存在时的 TravelGoal destination。
- `AvailableActions` 只有 Continue，以及条件成立时的 Reverse。

普通地点 request 与 encounter request 使用不同 builder，避免把 `AvailableActions` 中大量地点行为带入 Passage。途中不广告 Talk、Observe、Wait、Travel、TravelTo、物品操作或任意 destination。

Player.Llm parser 的 JSON 管线已经能传递无 target intent；生产只需让 prompt 清楚解释新 action，补 golden/parser tests，不再造 encounter 专用 parser 或 Player DTO。

`Player.Agency` 本批预期零修改：Contact 是当前 committed objective event 的局部感知，不是主观地图或 route knowledge 查询。

## 9. 同 tick 与关键竞争语义

必须保留以下因果分支，不能用 phase 固定结果：

### Contact 与 arrival 同 tick

- contact 先赢：打开 pending encounter；response 与 arrival 继续竞争。
- response Continue 先赢：movement 不变，arrival 后续正常完成。
- response Reverse 先赢：旧 arrival candidate 失效，新反向 arrival 出现。
- arrival 先赢：pending encounter 转为 WorldChanged cleanup，不调用 Player。

### Reverse 与 entry close 同 tick

- response 先赢：反向 segment 在当时合法创建，之后入口关闭不追溯取消。
- close 先赢：重新构造的 encounter request 不广告 Reverse，只能 Continue。

### 多个 contact 同 tick

- Kernel 先选择一个 contact opening；它只消费自己的 pair。
- pending encounter 暂时 suppress 其它 opening。
- resolve 后，仍成立的未消费 contact 在同一 model time 再次 Forecast。

这些分支都读取当前 committed prefix，不读取 PRF rank，也不假设 rule 注册顺序。

## 10. Replay、persistence 与版本

- Replay 只 fold当前格式 facts，不调用 Player、contact Forecast、pair math Forecast 或 response rule。
- Fork creation 同样不调用 Player；fork 从 pending encounter prefix 继续运行时才重新 Forecast response/cleanup。
- `PassageContactOccurredFact` 与 `TraversalReversedFact` 足以重建 objective Spatial state；Game encounter facts足以重建 pending/response state。
- persistence 测试 codec 直接从 `firstboard-host-fact-json/4` 升 `/5`，不保留 `/4` reader 或 migration。
- FirstBoard world snapshot、fact name/payload summary 与 Demo writer 对新 location/facts 做 exhaustive 更新。
- `ScenarioDefinition` schema、revision 与 ruleset 不因本次纯运行时能力自动升级；run manifest 的代码版本继续承担 build binding。

合法持久 prefix 至少包括：

1. contact 已 consumed 且 Game encounter pending；
2. encounter Continue 已 resolved、双方仍按原 segments 旅行；
3. encounter Reverse 已 resolved、responder 位于 anchored reverse segment；
4. arrival 已改变 segment、pending encounter 尚待 WorldChanged cleanup。

## 11. 施工顺序与预期文件

按依赖顺序施工，每一 wave 结束时保持对应 focused tests 通过：

1. 把 endpoint traversal 原位替换为 anchored traversal，更新 arrival/query/snapshot 与现有 Slice 1 回归。
2. 加 Reverse planner/fact/reducer、direction checks 与 current-segment cleanup。
3. 加 ContactKey/kind、ConsumedContacts、exact pair math 与第一等 `SpatialContactOccurrenceRule`。
4. 加 Protocol action kinds、validator 与 Player.Llm tests。
5. 加 FirstBoard pending encounter、两类 facts、opening/response rules 与 encounter request。
6. 接通 TravelGoal Continue/Reverse/WorldChanged 语义和原子失败路径。
7. 更新 persistence codec `/5`、replay/fork、Demo、Design Note 008 实施记录与全量回归。

预期生产修改：

- `src/Spatial/State/SpatialLocation.cs`
- `src/Spatial/State/GraphSpatialState.cs`
- `src/Spatial/State/GraphSpatialStateValidator.cs`
- 新建 `src/Spatial/Contacts/**`
- `src/Spatial/Facts/GraphSpatialFact.cs`
- `src/Spatial/Facts/GraphSpatialReducer.cs`
- `src/Spatial/Planning/SpatialPlanner.cs`
- `src/Spatial/Queries/**`
- `src/Spatial/Simulation/SpatialOccurrenceData.cs`
- `src/Spatial/Simulation/SpatialOccurrenceRule.cs`（只更新 anchored arrival identity，不 Forecast contact）
- 新建 `src/Spatial/Simulation/PassageContactOccurrenceData.cs`
- 新建 `src/Spatial/Simulation/SpatialContactOccurrenceRule.cs`
- `src/Protocol/ProtocolKinds.cs`
- `src/Decision.Validation/PlayerDecisionValidator.cs`
- `src/Player.Llm/PromptRenderer.cs`
- `src/FirstBoard/FirstBoardDomain.cs`
- `src/FirstBoard/FirstBoardSystems.cs`
- `src/FirstBoard/FirstBoardScenario.cs`
- `src/FirstBoard.Demo/DramaRecordWriter.cs`

预期测试修改：

- 更新现有 `tests/Spatial.Tests/**` 中 endpoint traversal fixtures。
- 新建 `tests/Spatial.Tests/Contacts/PassageContactTests.cs`。
- 扩展 Spatial planner/reducer/occurrence/acceptance tests，包括直接把 `SpatialContactOccurrenceRule` 注册进 Spatial-only Kernel 的闭环。
- 扩展 Protocol、Decision.Validation 与 Player.Llm tests。
- 新建 `tests/FirstBoard.Tests/PassageEncounterHostTests.cs`，验证 outer adapter 与 inner rule 的 Key/Due/Spatial fact 完全相同。
- 新建或扩展 FirstBoard atomicity/replay/fork tests。
- 更新 `tests/FirstBoard.Persistence.Tests/FirstBoardPersistenceTests.cs`。

不新建程序集，不修改 solution project graph，也不修改 `Host`。`Spatial` 继续只直接依赖 Kernel；`FirstBoard` 继续通过薄 composite adapter 使用 Spatial。

## 12. 验收矩阵

| ID | 必须证明的行为 |
|---|---|
| ANC-1 | endpoint start 在 anchored model 中保持原 length/speed/ceil/arrival 语义；旧 Slice 1 trace 不变。 |
| ANC-2 | integer-time Reverse 物化严格内部 offset、保持 speed、切换 target、generation +1，并产生新的精确 arrival。 |
| DIR-1 | A→B 途中 Reverse 检查 EnterableFromB；关闭时不广告/拒绝，开放时可反向；已创建 reverse segment 不受后续 close 追溯影响。 |
| CNT-1 | length10、A+4/B-3 在 exact `10/7` 交会，CandidateDue=T2；fraction 不进入 public state/fact/key。 |
| CNT-2 | HeadOn、Overtake 正确；CoTravel、`tau==0`、endpoint meeting 与没有共同 active window 不报。 |
| CNT-3 | A-B consumed 后不复发，不吞 A-C/C-D；arrival/reverse/remove 只清理涉及旧 segment 的 keys。 |
| CNT-4 | Contact candidate key canonical；Definition/entity 枚举顺序不改变 candidates；tampered key/due/kind/generation 被拒绝。 |
| CNT-5 | reference `O(Σn²)` Forecast 在 `Due==Now` 的 committed prefix 可用，无 whole-tick barrier。 |
| API-1 | Spatial-only Kernel 可直接注册 `SpatialContactOccurrenceRule`，独立完成 Forecast→Plan→fact→fold→replay；不引用 FirstBoard/Player/Protocol/Host implementation。 |
| API-2 | FirstBoard outer adapter 原样保留 inner Key/Due/data，selected Plan 复用 inner Spatial draft 并只追加 Game fact；FirstBoard 中没有 pair math、contact key builder 或第二份 consumed state。 |
| ENC-1 | selected contact 原子提交 Spatial Contact + Game Opened；任一 fold 失败时 World/Journal/Version 零半批。 |
| ENC-2 | 一个 driver participant 时只调用它；两个 driver participants 时不同 seed 可让任一方先回应，但每个 encounter 总共只调用一个 Player。 |
| ENC-3 | Continue 提交非空 resolved fact，DecisionSequence 只增一次，movement 与 TravelGoal 保留并可继续 arrival/自动导航。 |
| ENC-4 | Reverse 把 resolved + Spatial reverse 同 batch；DecisionSequence 只增一次，TravelGoal 清除，返回原 endpoint 后恢复普通 DecisionPoint。 |
| ENC-5 | 反方向 entry 关闭时 request 只有 Continue；伪造 Reverse 在 Player boundary 失败且零提交。 |
| ENC-6 | arrival 先改变任一 segment 后只提交 WorldChanged cleanup，不调用 Player、不清 TravelGoal；随后正常 controller 恢复。 |
| ENC-7 | pending encounter suppress 参与者的普通 DecisionPoint/TravelGoal，但不阻塞无关 Actor；resolve 后同 model time 重现合法 candidates。 |
| ENC-8 | 多个 same-tick contact 逐个打开/回应；第一个 pair 不消费其它 pair，也不引入 contact-first phase。 |
| ORD-1 | contact、response、arrival、entry close 同 tick 的不同 seed 产生各自合法分支；注册/枚举顺序不决定 winner。 |
| PLY-1 | Encounter request 只含 Passage、counterpart、travel/goal/contact facts 与 Continue/Reverse；不泄露 fraction、其它 occupancy、route 或 candidate/rank。 |
| PLY-2 | Continue/Reverse intent shape、LLM prompt/parser 与 malformed/stale correlation 全部覆盖。 |
| RPL-1 | pending、continued、reversed、world-changed 四类 prefix 的 full replay/fork 等价；创建过程零 Player 调用，续跑才恢复 response。 |
| RGR-1 | Travel、TravelTo、gate、parallel Passage、atomic batch、current persistence 与全部 Slice 1 tests 回归通过。 |

## 13. 明确不做与复杂性停线

本批不做：

- 双方轮流或同时响应、谈判回合、多人 encounter state；
- Talk/Attack/Flee/Trade 等 encounter-specific action；
- pause、stop、wait-on-passage、pace adjustment、MatchTraversalAtContact；
- 把 Actor 固定在 exact contact fraction，或把 fraction 暴露给 Player；
- 同行 formation、阻挡、碰撞、战斗距离或 passage capacity；
- NPC cognition、通用 behavior tree 或 scripted movement framework；
- contact index/cache、历史 contact ledger、receipt/hash/version 审计层；
- 主观地图、隐藏 Actor、感知概率或 MemoryBank contact authority；
- Area/ViewLink、旧格式 reader/migration。

实现中遇到以下情况，应先与用户讨论，不临时扩张：

- 单 pending encounter 真实导致合法故事事件丢失，而不是仅被顺序延后；
- 一个共享回应无法表达最小 playable trace，确实需要双方各自行动；
- Reverse 清 TravelGoal 不能表达必要的“暂停后恢复”产品语义；
- 1ms 量化让 contact 后 Reverse 出现不可接受的位置跳变；
- FirstBoard 需要新增 NPC 调度系统才能构造真实 consumer；
- FirstBoard 必须复制 pair math、CandidateKey 或 selected Spatial Plan 才能完成包装，说明 Spatial occurrence API 仍过窄；
- reducer 完整性必须依赖跨 fact 审计镜像或 Kernel 改造；
- reference pair scan 被真实基准证明不可用。

## 14. Definition of Done

至少执行：

```powershell
dotnet restore DramaBoard.Local.slnx --nologo
dotnet test tests/Spatial.Tests/Spatial.Tests.csproj --no-restore --nologo
dotnet test tests/Protocol.Tests/Protocol.Tests.csproj --no-restore --nologo
dotnet test tests/Decision.Validation.Tests/Decision.Validation.Tests.csproj --no-restore --nologo
dotnet test tests/Player.Llm.Tests/Player.Llm.Tests.csproj --no-restore --nologo
dotnet test tests/FirstBoard.Tests/FirstBoard.Tests.csproj --no-restore --nologo
dotnet test tests/FirstBoard.Persistence.Tests/FirstBoard.Persistence.Tests.csproj --no-restore --nologo
dotnet test DramaBoard.slnx --no-restore --nologo
dotnet test DramaBoard.Local.slnx --no-restore --nologo
dotnet build src/FirstBoard.Demo/FirstBoard.Demo.csproj --no-restore --nologo
git diff --check
git diff -- src/Kernel src/Host src/Player.Agency
git status --short
```

最后一个 Kernel/Host/Player.Agency diff 必须为空；`git status` 只能包含本批预期文件。实现完成后应：

- 把本文状态改为 Implemented and verified；
- 记录实际 commits、测试数量、复审结论与有意识偏差；
- 更新 Design Note 008 顶部状态、§3.1—3.5 framework/application API 边界、§7.2 验收矩阵和 Slice 2 实施记录；
- 若任何复杂性停线被触发，先记录用户裁决，再继续施工。
