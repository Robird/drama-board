# Design Note 008：Graph Spatial World
## ——以 Place、Passage、途中关系、客观可见性与模型时间构成可实施的空间框架

**状态：功能架构已冻结；已纳入 Passage 共行、相遇与超过；用于指导后续详细设计与实施**

**日期：2026-08-20**

**讨论基线：`spatial-grid-v1-baseline`（`main @ ed6b6d3`）**

**认知层拆分：** Player HUD、战争迷雾、主观地图、Claims、LLM DecisionView 与 Human realization 已移至 [Design Note 009](./开放世界棋盘游戏设计_009_Player空间HUD与战争迷雾_备忘.md)。它们不属于本子系统。

---

# 1. 定位与阶段性决定

Graph Spatial World 是 DramaBoard 中唯一的客观空间 authority。

它只负责六件事：

1. Graph Definition 在磁盘上的读取、验证、规范化与写回；
2. Actor 的客观位置；
3. Actor 在 Place 与 Passage 之间的移动；
4. 客观空间可见机会与共处关系；
5. Passage 内持续共行关系与可预测的相遇 / 超过；
6. Objective Graph 上的确定性导航。

它与 Kernel 集成，保存模型时间中的空间事实，但不管理 Player 的记忆、信念、地图笔记或行动策略。

一句话边界是：

> **Spatial World 决定世界在哪里、怎样相连、Actor 正在哪里、客观上与谁共行以及轨迹何时相交；调用方决定 Actor 为什么行动、实际注意到什么以及如何理解。**

## 1.1 权威拥有

Spatial World 权威拥有：

- immutable `SpatialGraphDefinition`；
- Area containment；
- Place、Passage 与 ViewLink identity；
- PassageLength 与 PassageOffset 的解释；
- EntityLocation；
- active Traversal；
- Passage 是否允许新的进入；
- scheduled Passage mutation；
- CoPresence 客观关系；
- CoTravel 客观关系；
- Passage interaction 的精确预测、结算与 provenance；
- Kernel Forecast、Resolve、Journal projection 与 Replay。

## 1.2 明确不拥有

Spatial World 不拥有：

- PlayerId；
- Known / Confirmed Graph；
- Claims、Rumor、Evidence 或 Fog-of-War；
- ExitStub、player-scoped opaque handle；
- Observation history；
- DecisionView、Prompt、HUD；
- Travel goal、Quest、Inventory、Faction、ticket；
- Actor 为什么拥有某种速度；
- Human GridMap 或 renderer state。

生产代码中不得出现对这些概念的依赖。

## 1.3 依赖方向

运行时 Graph Spatial project 单项依赖 Kernel：

```text
Kernel
  ↑
Graph Spatial Runtime
  ↑
Game validation / MockPlayer / Host composition
```

Graph 文件 codec 是 Spatial World framework 的内容适配器，但不是 Simulation authority：

```text
Graph JSON
  → parse / validate / canonicalize
  → immutable SpatialGraphDefinition
  → Graph Spatial Runtime
```

Runtime 不直接读取文件；文件系统、JSON 和编辑工具不得进入 reducer 或 Forecast。

---

# 2. V1 功能与复杂度边界

## 2.1 V1 保留

- Area、Place、双端 Passage、directed ViewLink；
- `AtPlace | TraversingPassage`；
- 正 Length、已裁决 SpeedSnapshot 与 lazy Offset；
- Passage 中 Continue、TurnBack、离散 AdjustTraversalPace 或基于 contact 的 MatchTraversalAtContact，但不能静止；
- Passage 内同位、同向、同速的持续 CoTravel；
- Passage 内非阻挡的迎面相遇与同向超过；
- widened exact-rational contact math，但 Kernel 仍只在整数 ModelTime 结算；
- degree-2 Place；
- 平行 Passage；
- 普通 Actor 不阻挡；
- dynamic enabled 与 future scheduled mutation；
- Objective Dijkstra；
- same-Place 与 ViewLink visibility opportunities；
- deterministic command batch；
- event-sourced state、Replay、Fork、split run。

## 2.2 V1 明确不做

- Grid、连续 2D / 3D 坐标、NavMesh；
- Passage 内 Stop、Wait、Resume、Site 或 Milestone；
- 任意 PassageOffset 作为移动目标；
- Passage capacity、reservation、Actor blocking、碰撞响应或车道；
- 追逐意图、自动拦截、自动停车或 combat positioning；
- 在分数 contact time 插入 Player decision；
- actor-specific condition DSL；
- Inventory / Faction / Quest 条件；
- 动态 Length、连续变速、加速度；
- Passage 内部 barrier 或局部坍塌；
- runtime 创建或删除 Place / Passage；
- 几何 LOS、阴影、隐身、识别与注意力；
- AreaEntered / AreaLeft 事件；AreaPath 只做查询；
- View opportunity 历史；
- Player knowledge、战争迷雾、LLM 与 Human UI；
- procedural generation 与 Graph→Grid compiler；
- 通用 rule DSL 或 property graph engine。

## 2.3 Passage 粒度规则

Place 是能够停留、互动、等待、调查、成为目标或连接其它 Passage 的原子 locality。

Passage 是只有两个 endpoint 的有限一维旅行空间。

Passage 同时是一种 **非阻挡的途中互动舞台**：

- 两条 active traversal 的世界线可以在 Passage 内产生客观相遇或超过；
- 同位、同向、同速的 Actor 可以形成持续 CoTravel；
- Spatial 只证明接触机会与共同行进，不推断注意、认出、对话、结伴意愿、追捕意图或阵营；
- contact 不自动使 Actor 停下。需要多回合战斗、调查、等待或稳定 interaction site 的位置仍必须成为 Place。

如果一个途中位置需要：

- 稳定 identity；
- 精确触发客观事件；
- 调查、战斗或等待；
- 会面；
- 分岔；
- 成为移动目标；

它必须成为 Place，即使它只有两个相连 Passage。

其它系统不能把 Site 换名藏成 `OffsetReached` callback。只要触发条件依赖稳定 PassageOffset，该位置就必须成为 Place。

---

# 3. 数值模型

V1 使用全局统一的抽象空间微单位。单位比例及算术规则属于 `RulesVersion`。

```text
PassageLength
    Int64 Units              // > 0

PassageOffset
    Int64 Units              // 0..PassageLength

SpeedSnapshot
    Int64 DistanceUnitsPerModelTick // > 0
```

ModelTick 只是 `ModelTime / ModelDuration` 的最小算术单位，不表示 Simulation 按 tick 推进。

## 3.1 旅行时间

对正距离与正速度：

```text
TravelTime(distance, speed)
    quotient  = distance / speed
    remainder = distance % speed
    ticks     = quotient + (remainder == 0 ? 0 : 1)
    return ModelDuration(ticks)
```

因此正剩余距离总是产生至少一个 ModelTick。

```text
TargetOffset =
    TargetEndpoint == EndpointA ? 0 : PassageLength

RemainingDistance =
    TargetEndpoint == EndpointA
        ? SegmentOriginOffset
        : PassageLength - SegmentOriginOffset

ArrivalDue = checked(
    SegmentStartedAt
    + TravelTime(RemainingDistance, SpeedSnapshot))
```

## 3.2 Lazy Offset

对合法查询域：

```text
SegmentStartedAt <= now < ArrivalDue
```

位置按需计算：

```text
elapsedTicks = (now - SegmentStartedAt).Ticks
advanced     = elapsedTicks * SpeedSnapshot

EffectiveOffset =
    TargetEndpoint == EndpointB
        ? SegmentOriginOffset + advanced
        : SegmentOriginOffset - advanced
```

中间乘法与加减使用 widened integer；验证结果后再回落 Int64。由 ceil TravelTime 可知，在 `now < ArrivalDue` 时 advanced 严格小于 remaining。

Arrival resolver 在 `now == ArrivalDue` 直接使用 TargetOffset，不调用普通 EffectiveOffset。

以下均为错误：

- `now < SegmentStartedAt`；
- active traversal 且 `now == ArrivalDue`，但 arrival 尚未 settle；
- active traversal 且 `now > ArrivalDue`；
- 使用 `double`、`decimal` 或未版本化舍入决定 Journal。

## 3.3 原子溢出

以下溢出必须在生成事件、消费 allocator 或改变 Revision 前拒绝：

- distance / speed arithmetic；
- relative velocity、exact-rational contact、fraction reduction 与 cross multiplication；
- ArrivalDue；
- total route cost；
- Passage interaction / CoTravel / CoPresence event-count 与 Journal microstep capacity；
- TraversalId / MutationId / MomentOrdinal；
- Generation；
- StateRevision。

Spatial 不持久化逐 tick progress，也不产生 progress event。

V1 同时冻结以下 `RulesVersion` 工程上限：

```text
MaxSpatialEntities                 = 256
MaxGraphPassages                   = 4_096
MaxCommandsPerSpatialBatch         = 256
MaxScheduledPassageMutations       = 65_536
MaxSpatialEventsPerTransition      = 131_072
```

这些是有界软件表示，不是“道路只能站多少人”的 fiction capacity：256 个 Actor 仍可全部位于同一 Passage 或 Place。Definition compiler、`PlaceEntity`、schedule admission 与 batch parser 必须在创建超限 state 前分别以 `DefinitionLimitExceeded / WorldEntityLimitReached / MutationStoreLimitReached / BatchLimitExceeded` 拒绝；前后两个是 compile / batch-level structural diagnostic，中间两个才是逐 command rejection。

上限保证任一合法 state 的单次 SpatialMoment 都可表示：同一 Due 最多 `4_096` 个 mutation、`C(256,2)=32_640` 个 interaction、256 个 arrival、每 pair 最多一个 CoPresence delta 与一个 CoTravel delta，再加 terminal，共 `102_273 < 131_072` 个 Spatial event。Command transition 的 256 个 primary 加两类 pair delta 也不超过该 event 上限。实现可以流式规划，但不能降低这些 V1 语义上限。

Host composition 还必须在同一 ModelTime 为所有已到期 internal systems 预留 microstep headroom；Spatial Gateway 不得接收会侵占已知 internal reserve 的 external batch。若损坏的 snapshot、绕过 gateway 的 system 或 Kernel 的其它事件已经使一个合法 SpatialMoment 没有 `MaxSpatialEventsPerTransition` headroom，Resolve 在写入任何事件前抛出确定性的 terminal `KernelCapacityInvariantViolated` 并终止 session；它绝不把容量问题伪装成可重试 rejection，也不返回原 candidate 形成永久 resolve 循环。

## 3.4 Passage 拆分的取整

把一个 Passage 在语义地点拆成两个 Passage 时：

- 两段 Length 之和必须等于原 Length；
- 每段 travel time 独立计算；
- 新增一个真实 Place 最多引入一次 ModelTick 的 ceil 差异；
- V1 不为消除该差异保存跨 Place 的分数余量；
- authoring validation 应报告拆分前后的默认速度 travel-time 差异。

## 3.5 Passage interaction 的精确运动学

V1 不把 Kernel 时间改成分数，也不产生逐 tick progress event。但如果只比较整数 tick 上的 Offset，两个高速 Actor 可能在相邻 tick 之间交换顺序而被错误地视为“没有相遇”。因此 Passage interaction 使用一套受 `RulesVersion` 约束的 **exact-rational 辅助运动学**。

对 frozen traversal 定义 signed velocity：

```text
SignedVelocity =
    TargetEndpoint == EndpointB
        ? +SpeedSnapshot
        : -SpeedSnapshot
```

pair planner 不能简单地“从查询时的 Now 向未来求交”。Resolve 发生在 `SettlementDue = ceil(ContactTime)`，此时分数 contact 通常已经位于该整数 tick 之前。它必须从两条当前 segment 的共同有效区间重建 worldline：

```text
t0 = max(A.SegmentStartedAt, B.SegmentStartedAt)
xA = ExactOffsetOnSegment(A, t0)
xB = ExactOffsetOnSegment(B, t0)
```

其中 `ExactOffsetOnSegment` 直接使用 segment origin / start / signed velocity，并允许以 exact rational instant 做历史重算；它不是只能在 settled `SpatialReadContext.Now` 调用的整数 `EffectiveOffset` query。只有双方 segment 在 `t0` 都已开始且尚未到达各自 exact physical exit，才继续求交。

在共同整数参考时刻 `t0`，两 Actor 的 Offset 为 `xA`、`xB`，signed velocity 为 `vA`、`vB`。若 `vA != vB`：

```text
tau           = (xB - xA) / (vA - vB)
ContactTime   = t0 + tau
ContactOffset = xA + vA * tau
SettlementDue = ceil(ContactTime)
```

所有比较和约分都使用 widened integer / exact rational，禁止 `double`、`decimal` 或容差。事件 codec 使用 canonical reduced fraction：

```text
RationalModelInstant
    WholeTicks: Int64
    FractionNumerator: UInt64
    FractionDenominator: UInt64   // > 0, gcd == 1, numerator < denominator

RationalPassageOffset
    WholeUnits: Int64
    FractionNumerator: UInt64
    FractionDenominator: UInt64
```

canonical signed rational 使用 Euclidean / floor form，不能沿用 C# 整数除法的 toward-zero remainder：

- `WholeTicks = floor(value)`，`0 <= FractionNumerator < FractionDenominator`；
- numerator 为 0 时 denominator 必须规范为 1；否则 numerator / denominator 必须互质；
- `ceil(value) = WholeTicks`（numerator 为 0），否则是 checked `WholeTicks + 1`；
- `RationalPassageOffset` 同样使用 floor form，且合法 contact 最终仍须严格落在 `(0, PassageLength)`；
- `long.MinValue` 附近的分解、取负、加一与 cross multiplication 必须先在 widened signed representation 完成。

因此 `-1/2` 的唯一编码是 `WholeTicks=-1, Numerator=1, Denominator=2`，其 ceil 为 0；`-1` 的唯一编码是 `WholeTicks=-1, Numerator=0, Denominator=1`。Graph Spatial 允许 Kernel 已支持的负 ModelTime，不另设非负假设。

intermediate multiplication / cross multiplication 使用 checked `UInt128`、`Int128` 或 `BigInteger`；必须在生成任何事件、消费 ordinal 或改变 Revision 前验证最终 canonical fraction 可表示。

一个合法 Passage interaction 必须：

- 两个不同 Entity 的 active traversal 位于同一 Passage；
- `tau > 0`，即不把共同 segment overlap 起点的 `f = 0` 伪报成超过；
- ContactOffset 严格位于 `(0, PassageLength)`；endpoint contact 交给 Arrival / Place CoPresence；
- ContactTime 严格早于双方按 `remaining / speed` 得到的 exact physical exit；
- SettlementDue 尚未被 `PassageInteractionsSettledThrough` 消费。

分类冻结为：

```text
HeadOnMeeting
    TargetEndpoint 相反，二者正相向接近

Overtake
    TargetEndpoint 相同，后方较快者追上并交换轴上顺序
```

同方向、同速、不同 Offset 是 `ConstantGap`，永不相交。同 Passage、同方向、同 Offset、同 SpeedSnapshot 且具有相同 ArrivalDue 是持续 `CoTravel`，不产生重复 contact event。同一 endpoint 同刻出发但速度不同，只是共同出发后分离，也不伪报 Overtake。

reference planner 接受明确的 evaluation window，而不是暗用调用时钟：Forecast / Resolve 枚举 `SettlementDue > PassageInteractionsSettledThrough` 的未消费 contact；public `PredictPassageInteraction` 只返回 `ContactTime > SpatialReadContext.Now` 的未来 contact；Projector / Replay 则在 event 指定 segment identities 上历史重算该唯一 contact。三者必须复用同一个 pair math，只改变过滤窗口。

`PassageInteractionOccurred@T` 的权威含义是：精确轨迹交点位于该事件携带的 rational instant，Kernel 在其上界整数 tick `T = ceil(ContactTime)` 提交结果。Game / Player 不能在同一 tick 内两个 rational contact 之间插入命令；若需要在精确分数时刻停下、阻止超过或进入多回合战斗，必须升级 Kernel 时间或引入新的 Passage stop/site 语义，不属于本 V1。

---

# 4. Graph Definition 与磁盘 I/O

## 4.1 稳定标识符

V1 内容 ID 使用强类型、Ordinal 比较的 kebab-case UTF-8 字符串：

```text
[a-z][a-z0-9]*(?:-[a-z0-9]+)*
```

长度限制与底层存储由实现冻结，但必须：

- 拒绝空白、控制字符和 ill-formed UTF-16；
- 每种 ID 类型内唯一；
- equality、hash、serialization 与 total order 使用同一 Ordinal 语义；
- display name 与 localization 不进入 ID。

运行时 identity：

```text
EntityId / TraversalId / MutationId / SpatialCommandId
    positive Int64 ordinal
```

## 4.2 Definition stamp

```text
SpatialDefinitionStamp
    DefinitionId
    DefinitionRevision
    RulesVersion
    ContentHash
```

`DefinitionRevision` 与 dynamic `StateRevision` 必须是不同类型。

`ContentHash` 使用 canonical UTF-8 上的 SHA-256。Hash 输入排除：

- hash 字段本身；
- 派生 adjacency index；
- parser cache；
- debug metadata；
- 文件中的集合原始顺序。

这里的 canonical UTF-8 不是“任意语义等价 JSON”。它精确定义为 `WriteCanonicalSource` 的 compact 输出：UTF-8 without BOM、无末尾换行和无 insignificant whitespace；property 顺序固定为 §4.7 展示的 schema 顺序；四类数组分别按 typed ID Ordinal 排序；整数只用无前导零的十进制；`true` / `false` / `null` 使用 JSON 小写字面量。V1 ID 仅含 ASCII，因此不存在 Unicode normalization 或可选 escape。`WriteCanonicalDebugView` 不参与 hash。

## 4.3 Area

```text
AreaDefinition
    AreaId
    ParentAreaId?
```

约束：

- 恰好一个 root；
- 无环；
- 每个非 root Area 恰好一个 parent；
- Area containment 不参与导航；
- Area 不是 Entity location。

## 4.4 Place

```text
PlaceDefinition
    PlaceId
    DirectAreaId
```

Place 是：

- 可停留位置；
- interaction locality；
- traversal endpoint；
- Objective navigation vertex。

每个 Place 必须引用存在的 Area。

## 4.5 Passage

```text
PassageDefinition
    PassageId
    EndpointAPlaceId
    EndpointBPlaceId
    Length: PassageLength
    InitiallyEnabled
```

约束：

- endpoints 存在且不同；
- Length 正；
- EndpointA / EndpointB 的顺序是权威 Offset 轴，canonicalization 不得交换；
- enabled 时允许从任一 endpoint 进入；
- 相同 endpoints 之间允许平行 Passage；
- 一个 physical Passage 在导航中派生两个 arc，但仍只有一个 PassageId；
- Passage 交叉不代表相连，只有共享 Place 才相连。

## 4.6 ViewLink

```text
ViewLinkDefinition
    ViewLinkId
    FromPlaceId
    TargetPlaceId
```

ViewLink 是 directed objective visibility opportunity，不是 movement edge。

V1：

- 两端 Place 必须存在；
- 拒绝 self-link；
- 拒绝重复 `(FromPlaceId, TargetPlaceId)`；
- 不从 Passage adjacency 自动生成；
- 不披露 target Place 内的 Entity；
- 不表达 LOS blocker、识别或注意力。

## 4.7 JSON authoring schema

V1 source shape：

```json
{
  "schema": "dramaboard.graph-spatial/1",
  "id": "coast-graph",
  "revision": 1,
  "rulesVersion": 1,
  "areas": [
    { "id": "world", "parent": null },
    { "id": "coast", "parent": "world" },
    { "id": "grotto-area", "parent": "coast" }
  ],
  "places": [
    { "id": "lagoon", "area": "coast" },
    { "id": "fork", "area": "coast" },
    { "id": "cliff", "area": "coast" },
    { "id": "ford", "area": "coast" },
    { "id": "grotto", "area": "grotto-area" },
    { "id": "island", "area": "coast" }
  ],
  "passages": [
    { "id": "lagoon-fork", "endpointA": "lagoon", "endpointB": "fork", "length": 10, "initiallyEnabled": true },
    { "id": "fork-cliff", "endpointA": "fork", "endpointB": "cliff", "length": 4, "initiallyEnabled": true },
    { "id": "cliff-grotto", "endpointA": "cliff", "endpointB": "grotto", "length": 6, "initiallyEnabled": true },
    { "id": "fork-ford", "endpointA": "fork", "endpointB": "ford", "length": 3, "initiallyEnabled": true },
    { "id": "ford-grotto", "endpointA": "ford", "endpointB": "grotto", "length": 7, "initiallyEnabled": true }
  ],
  "viewLinks": [
    { "id": "lagoon-sees-island", "from": "lagoon", "target": "island" }
  ]
}
```

Parser 必须拒绝：

- 未知 schema / RulesVersion；
- unknown property；
- duplicate JSON property；
- 数字越界；
- duplicate ID；
- unknown reference；
- Area 多 root、cycle 或断裂；
- Place 无 Area；
- Passage 同 endpoint 或非正 Length；
- 非法 ViewLink；
- Passage 数超过 `MaxGraphPassages`。

## 4.8 编译与写回

```text
read UTF-8
→ parse strict source DTO
→ validate identifiers and ranges
→ resolve references
→ validate containment and graph
→ canonical sort by typed ID
→ build immutable definition and derived indexes
→ hash canonical UTF-8
```

Framework 提供：

```text
CompileGraph(sourceBytes)
WriteCanonicalSource(definition)
WriteCanonicalDebugView(definition)
```

输入数组顺序不影响 compiled definition、hash、navigation 或 visibility result。

Graph 文件只保存 immutable Definition。Dynamic State 由 setup events、Journal 与可选 Host snapshot 保存，不另造 Spatial JSON state authority。

---

# 5. Dynamic State

```text
SpatialGraphState
    DefinitionStamp
    StateRevision
    Entities[]
    Traversals[]
    PassageEnabledOverrides[]
    ScheduledPassageMutations[]
    LastTraversalRebasedAtByEntity[]
    PassageInteractionsSettledThrough?
    NextTraversalOrdinal
    NextMutationOrdinal
    NextMomentOrdinal
```

## 5.1 Entity

```text
EntityState
    EntityId
    Location

EntityLocation
    AtPlace(PlaceId)
    TraversingPassage(TraversalId)
```

一个 Entity 在 complete state 中恰好拥有一种 location。

普通 Entity 不阻挡 Place、Passage 或其它 Entity。多个 Entity 可以位于同一 Place，也可以同时遍历同一 Passage。

## 5.2 Traversal

```text
TraversalState
    TraversalId
    EntityId
    PassageId
    SegmentOriginOffset
    TargetEndpoint
    SegmentStartedAt
    SpeedSnapshot
    ArrivalDue
    Generation
```

约束：

- 每个 `TraversingPassage` 精确引用一个属于该 Entity 的 Traversal；
- 每个 Traversal 恰好被一个 Entity 引用；
- origin offset 在 `[0, Length]`；
- target 是 Passage endpoint；
- remaining distance 正；
- speed 正；
- due 精确符合统一 TravelTime；
- Generation 初始为 1；
- active traversal 始终运动且具有有限 Due。

## 5.3 Passage override

```text
PassageEnabledOverride
    PassageId
    Enabled
```

Override 采用 sparse canonical form：若 desired 与 Definition 初值相同，则删除 override。

## 5.4 Scheduled mutation

```text
ScheduledPassageMutation
    MutationId
    PassageId
    Due
    Enabled
```

同一 `(PassageId, Due)` 最多一个结果：

- 相同 desired 是 alias；
- 不同 desired 是 conflict；
- Due 必须严格晚于 command time；
- future desired 即使等于当前 effective value 也必须持久化，因为 Due 前状态可能变化。

## 5.5 LastTraversalRebased guard

`LastTraversalRebasedAtByEntity` 是 sparse、可 Replay 的权威状态。

同一 Entity / ModelTime 最多接受一次会重建 active segment 的命令：`TurnBack`、`AdjustTraversalPace` 或 `MatchTraversalAtContact`。无论结果是反向运动、调速、匹配共行还是零进度返回，成功后都写 guard；Entity removal 同时清理 guard。

## 5.6 Passage interaction settlement watermark

```text
PassageInteractionsSettledThrough: ModelTime?
```

它是可 Replay 的全局消费水位：

- `null` 表示尚未由 SpatialMoment 扫描任何 Passage interaction settlement tick；
- `T` 表示所有 `SettlementDue <= T` 的 Passage interactions 都已经被完整扫描并提交；
- 每个 `MomentResolved@T` 原子把水位单调推进到 `T`；
- Forecast 只考虑尚未被该水位消费的 contact；
- watermark 不是 Simulation cursor 的替代品，但 snapshot / Replay / Fork 必须保存它。

`CreateEmpty(definition)` 初始化为 `null`。Command handler 可以接受 watermark 早于 command time 的 state，但必须先证明不存在尚未消费且 `SettlementDue <= command time` 的 contact；否则整批 `InternalWorkMustSettle`。一旦某次 SpatialMoment 在 T 运行，无论该 moment 实际 contact count 是否为零，terminal 都把水位推进到 T。

这个字段不能省略。Passage interaction event 本身不改变 traversal；若没有可回放消费证据，Resolve 后在同一 cursor time 重新 Forecast 会再次发现同一交点并形成无限 resolve。

不保存 pairwise contact ledger、Passage occupancy cache 或 relative-order cache。它们都能从 Traversal 与 watermark 推导，保存会形成第二份 authority。

## 5.7 Revision

- 每个 state-changing primary event 使 StateRevision checked `+1`；
- derived event 不改 State；
- no-op command 不改 Revision；
- `MomentResolved` 消费 MomentOrdinal、推进 interaction watermark 并改变 StateRevision；
- Revision overflow 在 transition 前整体拒绝。

---

# 6. Effective Topology 与查询

Structural complete state 只表示引用与局部不变量闭合；它不自动证明当前 cursor time 已经 settle。Public query 统一接受：

```text
SpatialReadContext
    Definition
    State
    Now
```

`SpatialReadContext.Create` 除 structural complete validation 外，还要求每个 active traversal 都满足 `SegmentStartedAt <= Now < ArrivalDue`，不存在 `Due <= Now` 的 scheduled mutation，并且不存在尚未被 `PassageInteractionsSettledThrough` 消费而 `SettlementDue <= Now` 的 Passage interaction。也就是说它只代表 **settled-at-Now** snapshot。

所有 public query：

- 只接受 `SpatialReadContext`，不能分别接受 Definition、State 与另一个 time；
- 验证 DefinitionStamp；
- 不接受 reducer prefix；
- 返回 defensive immutable、canonical order；
- missing entity 与合法 empty result 使用不同结果；
- 不出现 PlayerId、Known、HUD 或 Prompt DTO。

Forecast / Resolve 使用自己的 internal context，可以在 `Due == Now` 时 settle work；普通 query 不能读取这个 temporal prefix。Navigator 也只接受 settled read context，即使某次路线查询表面上只读取 topology。

## 6.1 最小查询面

```text
GetEffectiveEntityLocation(entityId)
GetEffectivePassageEnabled(passageId)
GetIncidentPassages(placeId)
GetPlaceAreaPath(placeId)
    GetEntityAreaPath(entityId)
    GetCoPresentEntities(entityId)
    GetCoTravelingEntities(entityId)
    GetSamePlaceEntityCandidates(observerEntityId)
    GetSamePassageEntityCandidates(observerEntityId)
    PredictPassageInteraction(entityAId, entityBId)
    GetViewLinkedPlaceCandidates(observerEntityId)
```

其中所有时间计算都只能使用 `SpatialReadContext.Now`。查询结果统一使用 `Found(value) | EntityNotFound | PlaceNotFound | PassageNotFound` 这类显式 union；合法的空 immutable collection 仍是 `Found(empty)`，不能与 unknown reference 合并。Entity collection 按 EntityId；Passage collection 按 PassageId；AreaPath 按 root→direct Area 排序。

| Query | 成功值与过滤 | canonical order / 非成功结果 |
|---|---|---|
| EffectiveEntityLocation | §6.4 的 `AtPlace | OnPassage`；OnPassage offset 只用 context.Now | unknown Entity → EntityNotFound |
| EffectivePassageEnabled | 当前 bool，不返回 raw override | unknown Passage → PassageNotFound |
| IncidentPassages | 所有与 Place 相接的 descriptor，并显式带 `EffectiveEnabled`；不隐式过滤 disabled | PassageId；unknown Place → PlaceNotFound |
| PlaceAreaPath | root 到 direct Area 的非空序列 | root→direct；unknown Place → PlaceNotFound |
| EntityAreaPath | AtPlace 时返回 PlaceAreaPath | unknown → EntityNotFound；Traversing → NoAreaWhileTraversing，不是 Found(empty) |
| CoPresentEntities | 仅完整 snapshot 中同 Place 的其它 Entity | EntityId；unknown → EntityNotFound；合法无人 → Found(empty) |
| CoTravelingEntities | §10.5 的持续客观共行 pair | EntityId；unknown → EntityNotFound；AtPlace 或合法无人 → Found(empty) |
| SamePlaceEntityCandidates | §10.1 的 immutable records | TargetEntityId；observer unknown → EntityNotFound；observer Traversing → Found(empty) |
| SamePassageEntityCandidates | §10.6 的 immutable relative-motion records；只含同一 physical Passage | TargetEntityId；observer unknown → EntityNotFound；observer AtPlace → Found(empty) |
| PredictPassageInteraction | §3.5 的纯 exact-rational pair planner；返回 kind / ContactInstant / ContactOffset / SettlementDue | unknown Entity、not Traversing、different Passage 与合法 NoFutureInteraction 使用不同 union case |
| ViewLinkedPlaceCandidates | §10.2 的 immutable records | `(ViewLinkId, TargetPlaceId)`；observer unknown → EntityNotFound；observer Traversing → Found(empty) |

## 6.2 Effective enabled

```text
EffectiveEnabled(passage)
    = override if present
      else definition.InitiallyEnabled
```

这只决定能否开始新的 traversal。普通 disable 不追溯已经开始的 traversal；既有 Actor 仍可继续、TurnBack 或 Arrive。

## 6.3 AreaPath

Entity `AtPlace` 时可以从 Place 的 direct Area 推导 Objective AreaPath。

Entity TraversingPassage 时 V1 没有 Area membership。Passage 不保存 InteriorArea。

## 6.4 Effective location

Gameplay query 只能使用当前 Simulation cursor time。

```text
EffectiveEntityLocation
    AtPlace(PlaceId)
    OnPassage(PassageId, PassageOffset, TargetEndpoint)
```

Offset 只在 §3 的合法查询域物化。

---

# 7. Commands、Batch 与 Results

## 7.1 Command schema

```text
PlaceEntity
    CommandId, EntityId, PlaceId

RemoveEntity
    CommandId, EntityId

BeginTraversal
    CommandId, EntityId, PassageId
    ExpectedFromPlaceId
    SpeedSnapshot
    ExpectedStateRevision

TurnBack
    CommandId, EntityId
    ExpectedTraversalId
    ExpectedGeneration
    SpeedSnapshot
    ExpectedStateRevision

AdjustTraversalPace
    CommandId, EntityId
    ExpectedTraversalId
    ExpectedGeneration
    SpeedSnapshot
    ExpectedStateRevision

MatchTraversalAtContact
    CommandId, EntityId
    ContactMomentOrdinal
    ContactInteractionOrdinal
    ExpectedContactKind
    ExpectedContactInstant
    ExpectedContactOffset
    MatchEntityId
    ExpectedTraversalId, ExpectedGeneration
    ExpectedMatchTraversalId, ExpectedMatchGeneration
    ExpectedStateRevision

SetPassageEnabled
    CommandId, PassageId, Enabled

SchedulePassageEnabled
    CommandId, PassageId, Due, Enabled
```

V1 没有：

```text
Continue
Stop
Wait
Resume
Relocate
CancelMutation
```

Continue 是 Spatial no-op；调用方不应提交命令。

## 7.2 外部 validator seam

Game / test adapter 在提交 Begin、TurnBack、AdjustTraversalPace 或 MatchTraversalAtContact 前，可以裁决：

- Inventory、Faction、ticket；
- movement mode；
- Actor-specific speed；
- semantic intent。

Begin / TurnBack / Adjust 只把已经裁决的正 SpeedSnapshot 交给 Spatial；Match 则提交明确的 contact receipt 与 target motion identity，并由 Game 先裁决 Acting Entity 是否能够在该 tick 完成这种匹配。两类都不能把 semantic intent 塞进 Spatial。

Spatial 仍重新验证：

- ExpectedStateRevision；
- Entity 当前 location；
- endpoint；
- BeginTraversal 时 Passage effective enabled；TurnBack 不检查 enabled；
- traversal identity / generation；
- Match 引用的 contact 是否属于刚刚在 current ModelTime committed 的 SpatialMoment，以及双方 motion 是否仍与该 contact 一致；
- time boundary；
- speed、Due 与 overflow。

Projector 与 Replay 不回调外部 validator。

## 7.3 Batch 结构

- CommandId 必须正且整批唯一；duplicate 是整批结构错误；
- Handler 只接受 settled-at-command-time pre-state；若存在 scheduled mutation、unconsumed Passage interaction 或 arrival `Due <= now`，整批以 `InternalWorkMustSettle` 拒绝且零事件，从而让 gateway bypass 也不能改变 World；
- 规划前按 CommandId Ordinal 规范排序；
- results 按 CommandId 返回；
- 输入排列不改变 events、results 或 allocator；
- 同 Entity 多个 lifecycle / segment-rebase command 整组 conflict；
- 同 Passage 多个 immediate desired：相同 alias，不同 conflict；
- scheduled mutation 依 §5.4 alias / conflict；
- topology commands 先形成 working topology，movement command 随后验证；
- 一个 batch 内所有 `ExpectedStateRevision` 都与唯一 input / pre-state revision 比较，不能与逐事件增长的 scratch revision 比较；
- rejected command 不产 event、不改 Revision、不消费 allocator；
- 所有 allocator、Revision、Generation 与 arithmetic capacity 在提交前整体预检。

Command transition 的 Journal 顺序固定为 command family phase，再按 producing CommandId 排序；alias 与 rejection 不产生 event。不能用调用方输入排列决定 event order。

为使 Handler 成为唯一结果的 total planning function，V1 再冻结以下细则：

1. family phase 固定为 `immediate topology → scheduled topology → entity existence → movement`；family 内再按 producing CommandId。Entity existence 是 `PlaceEntity / RemoveEntity`，movement 是 `BeginTraversal / TurnBack / AdjustTraversalPace / MatchTraversalAtContact`。同一 Entity 在这些 lifecycle / rebase commands 中出现两个或更多命令时，该组全部 `CommandConflict`，V1 不提供 Place+Begin 组合捷径。
2. 同一 Passage 的 immediate commands 先按 target 分组。desired 不同则整组 conflict；desired 相同时最小 CommandId 是 canonical leader。若 desired 已是 current effective value，leader 为 `AcceptedNoChange`；否则 leader 为 `Accepted`。其余命令只有在 leader 成功或 no-change 时才是 `AcceptedAlias(AliasOfCommandId=leader)`。
3. scheduled command 的 canonical key 是 `(PassageId, Due)`。同 key desired 不同则整组 `MutationConflict`。若 pre-state 已有同 key、同 desired schedule，最小 CommandId 以 `AcceptedAlias(MutationId=existing, AliasOfCommandId=null)` 指向它，其余同批命令 alias 到最小 CommandId；若没有 existing schedule，最小 CommandId 是唯一 allocator consumer，其余 alias 到它。future desired 即使等于 current effective value也不是 no-op。
4. canonical leader 最终被玩法校验拒绝时，同组 aliases 继承同一 rejection code，而不是留下 `AcceptedAlias`。
5. 每个 allocator domain 先找出其它校验均会成功的 canonical consumers，再整体检查容量。容量不足时该 domain 的全部 consumers 及 aliases 都以 `AllocatorExhausted` 拒绝；不按低 CommandId 部分接收。其它不消费该 allocator 的独立 command 仍可成功。StateRevision 总容量不足是 terminal invariant failure，整次调用在返回任何 events/results 前抛出，绝不部分提交。
6. `MaxCommandsPerSpatialBatch` 超限是分组前的 batch structural error。Entity / scheduled-mutation RulesVersion 上限按完整 planned final state 预检；若某类新 consumers 会使上限超出，该类全部 otherwise-valid consumers 以 `WorldEntityLimitReached / MutationStoreLimitReached` 拒绝，不能让较小 CommandId 抢剩余名额；独立 Remove 和其它不消费该容量的 command 仍可成功。
7. 普通 rejection precedence 固定为：group conflict → unknown/static reference → expected pre-state revision → location/traversal/time boundary → enabled/speed/arithmetic → rules/allocator capacity。一个 command 只返回最先命中的 code。Batch 结构错误与 unsettled pre-state 分别在分组和玩法校验之前整批处理。
8. `MatchTraversalAtContact` 的 authority 是刚刚 committed 的 contact，而不是任意“传送到另一个 Actor”。Host gateway 先从已提交 Journal 把 `(MomentOrdinal, InteractionOrdinal)` 解析成 `CommittedPassageInteractionReceipt`，再通过独立的 trusted `SpatialCommandAdmissionContext`、按 CommandId 交给 Handler；命令 payload 中的 `Expected*` 只能做 optimistic match，不能自己充当收据。Spatial Handler 不读取 Journal，但要求 context receipt 存在，并从 current traversal state、`PassageInteractionsSettledThrough == command time`、receipt 的 exact rational contact 与双方 identities 重新证明该 pair 确实在 current tick 发生同一个 contact。contact 不是 current ModelTime、context 缺失或不匹配、任一 generation 已改变、任一方已 Arrival / Remove 时稳定拒绝。测试 adapter 也只能从 fixture Journal 构造该 context，production 不公开“任意 receipt”构造捷径。
9. 多个 follower 可以在同一 batch 匹配一个本批不改变 motion 的 leader。leader 自己也有 movement / rebase command、match dependency 成环、或同一 follower 指向多个 target 时，所有相关 Match 整组 `CommandConflict`；不能让 CommandId order 决定谁先成为 leader。

这些规则刻意选择“全组拒绝”而不是“让最小 ID 抢到最后一个 ordinal”，避免 allocator 临界状态把业务结果变成一种隐蔽的竞争机制。

## 7.4 Result

```text
SpatialCommandResult
    CommandId
    Disposition
        Accepted
        AcceptedNoChange
        AcceptedAlias
        Rejected
    RejectionCode?
    TraversalId?
    MutationId?
    AliasOfCommandId?
```

最小稳定 rejection family：

```text
UnknownEntity
UnknownPlace
UnknownPassage
EntityAlreadyExists
LocationMismatch
PassageDisabled
NotTraversing
StaleTraversal
StaleStateRevision
NoElapsedMovement
AlreadyRebasedAtThisTime
NoRecentPassageContact
PassageContactMismatch
DueBoundaryMustSettle
InternalWorkMustSettle
DueNotFuture
CommandConflict
MutationConflict
InvalidSpeed
ArithmeticOverflow
AllocatorExhausted
WorldEntityLimitReached
MutationStoreLimitReached
```

`DefinitionLimitExceeded / BatchLimitExceeded` 是 compiler / batch structural diagnostic，不伪装成某一 CommandId 的普通 result。

异常只用于结构/API 违约或 terminal invariant failure；普通玩法失败进入 result。

---

# 8. Movement 生命周期

## 8.1 状态机

```text
AtPlace
    └─ BeginTraversal ─→ TraversingPassage
                              ├─ TurnBack ─→ TraversingPassage(new generation)
                              ├─ Return ───→ AtPlace(origin)
                              └─ Arrive ───→ AtPlace(target)
```

## 8.2 BeginTraversal

前置：

- Entity AtPlace；
- current Place 等于 ExpectedFromPlace；
- Place 是 Passage endpoint；
- Passage effective enabled；
- speed 正；
- remaining distance 与 Due 可表示；
- allocator 有容量。

结果：

- 分配 TraversalId；
- target 是另一 endpoint；
- origin offset 是 0 或 Length；
- SegmentStartedAt 是 command ModelTime；
- Generation = 1；
- Entity 原子变为 TraversingPassage。

## 8.3 TurnBack

前置：

- Entity 正在 Traversing；
- traversal id / generation / state revision 匹配；
- `now < ArrivalDue`；
- `LastTraversalRebasedAtByEntity[EntityId] != now`；
- 新 speed 正；
- generation 与 Due 有容量。

在 now 物化 Offset：

- 若仍精确位于初始 endpoint，产生 ReturnedToOrigin；
- 若当前 segment 从内部 Offset 开始且 now 等于 SegmentStartedAt，拒绝 `NoElapsedMovement`；
- 否则保留 PassageId / TraversalId，把 target 改为另一 endpoint，Generation checked `+1`。

两种成功 outcome 都写入 `LastTraversalRebasedAtByEntity[EntityId]`。未来 contact candidate 因 Generation / StateRevision 改变而 stale。

## 8.4 AdjustTraversalPace

`AdjustTraversalPace` 是离散的 piecewise-constant rebase，不是连续加速度：

- Entity 必须正 Traversing，traversal id / generation / state revision 匹配；
- `now < ArrivalDue` 且本 Entity 本时刻尚未 rebase；
- 在 now 物化当前 Offset；
- PassageId 与 TargetEndpoint 不变；
- SegmentOriginOffset 改为当前 Offset，SegmentStartedAt 改为 now；
- 写入新正 SpeedSnapshot，Generation checked `+1`，重新计算 ArrivalDue；
- 新 speed 与当前 speed 相同是 `AcceptedNoChange`，不增加 Generation、不写 guard；
- 成功改变 motion 时写 `LastTraversalRebasedAtByEntity[EntityId]`。

这使 Actor 能主动加速、减速、结束既有 CoTravel，或在当前精确同位时形成新的 CoTravel。Game 仍负责裁决体力、载具、地形与其它速度来源。

## 8.5 MatchTraversalAtContact

`MatchTraversalAtContact` 是 contact settlement 后、同一整数 ModelTime 内唯一允许的接触响应捷径。它表示 Acting Entity 在该 board-game tick 内完成转向 / 调速并从当前边界开始匹配目标 motion；它不表示目标同意结伴。

前置：

- `PassageInteractionsSettledThrough == now`；
- `ContactMomentOrdinal == NextMomentOrdinal - 1`，且该 Moment 的 event ModelTime 等于 now；
- trusted admission context 含 Host 从 committed Journal 解析出的 `(ContactMomentOrdinal, ContactInteractionOrdinal)` receipt；命令 expected fields、双方 current traversal / generation 与历史 segment worldline 都可以重算并精确匹配 receipt 的 kind / rational instant / rational offset；
- 双方仍 Traversing，且 Acting Entity 本时刻尚未 rebase；
- target 在同批中没有 movement / rebase command；
- Acting Entity 与 target 不同；
- target 的 effective Offset、TargetEndpoint、SpeedSnapshot 与 ArrivalDue 在 now 合法可读。

结果：

- Acting Entity 保留 PassageId / TraversalId；
- SegmentOriginOffset 对齐到 target 在 now 的 EffectiveOffset；
- TargetEndpoint、SpeedSnapshot 与 ArrivalDue 匹配 target；
- SegmentStartedAt = now，Generation checked `+1`；
- 写 `LastTraversalRebasedAtByEntity[EntityId]`；
- complete pre / final relation diff 决定是否产生 CoTravelStarted。

这个显式对齐是可审计的 contact response，不是普通 teleport API。它不能引用旧 contact、不能对齐远处 Actor，也不能撤销同 tick 已经 committed 的其它 contact。若 contact 与 Arrival 同 T，Arrival phase 已使参与者不再 Traversing，因此 Match 稳定拒绝。

## 8.6 Arrival

Arrival 只由 SpatialMoment 在 `now == ArrivalDue` 产生：

- 删除 TraversalState；
- Entity 变为 AtPlace(target)；
- 普通 Passage disable 不阻止 arrival；
- arrival 后的下一 traversal 必须是独立 external command；
- reducer 不自动寻路或续程。

## 8.7 RemoveEntity

Remove 必须原子清理：

- Entity；
- active Traversal；
- `LastTraversalRebasedAtByEntity[EntityId]` guard。

它不删除 Definition、Passage mutation 或其它 Entity。

## 8.8 Passage 中不静止

Spatial 不表达 Passage 中长期停留。Passage contact 与 CoTravel 提供途中互动的客观信息基础，但不会自动停车、开始对话或进入战斗状态。

需要等待、调查、战斗、恢复或精确披露的位置必须是 Place。强制 interruption 若未来加入，必须原子地：

- 保留一种 active motion；或
- 把 Entity 放到既有 Place；或
- 移除 Entity。

不能只删除 Traversal 后把 Entity 留在无状态 Passage。

---

# 9. Objective Navigation

```text
FindRoute(
    readContext,
    startPlaceId,
    goalPlaceId,
    speedSnapshot)
```

Result：

```text
RouteFound
    TotalDuration
    Legs[]
        PassageId
        FromPlaceId
        ToPlaceId

AlreadyAtGoal
NoRoute
CostOverflow
UnknownStartPlace
UnknownGoalPlace
InvalidSpeed
```

规则：

- 只使用 effective-enabled Passage；
- 每个 physical Passage 派生 A→B 与 B→A arc；
- route leg 必须携带 From / To，不能只返回 PassageId；
- V1 使用一个已裁决的正 SpeedSnapshot；
- edge cost 使用 §3 的 TravelTime；
- widened total cost；
- NoRoute 与 CostOverflow 不混淆；
- `start == goal` 返回 AlreadyAtGoal；
- equal-cost route 使用完整 route key 的 Ordinal 字典序；
- 结果不依赖 collection insertion order；
- Navigator 是纯查询，不写 State、不保存 path cache；
- Reducer、Replay 与 SpatialMoment 不调用 Navigator。

完整 route key 是 legs 序列中 `(PassageId, FromPlaceId, ToPlaceId)` 三元组的 Ordinal 字典序；不是只比较 first edge，也不使用 collection insertion order。`FindRoute` 只能读取 `readContext.State`、`readContext.Definition` 与 `readContext.Now`，没有绕过 settled-time audit 的 overload。Unknown start / goal 和非正 speed 分别返回上述显式 result，不抛成“无路线”。

V1 Navigator 接受 Place 起点。Actor 在 Passage 中只能继续当前 target 或 TurnBack；不把它伪装成任一 endpoint。

---

# 10. 客观可见机会与 Spatial Relations

Spatial 只回答客观空间关系，不回答 Actor 是否注意、识别、记住或相信。

## 10.1 Same-place candidate

```text
SamePlaceEntityCandidate
    ObserverEntityId
    TargetEntityId
    PlaceId
```

规则：

- observer 必须已放置且 AtPlace；
- target 必须 AtPlace 且 Place 相同；
- 排除自身；
- Traversing Entity 不属于 endpoint；
- canonical EntityId order；
- 普通 Entity 不阻挡可见机会。

## 10.2 ViewLink candidate

```text
ViewLinkedPlaceCandidate
    ObserverEntityId
    ViewLinkId
    TargetPlaceId
```

规则：

- observer AtPlace(source)；
- directed；
- target 只是 Place opportunity；
- 不自动枚举 target Place 中的 Entity；
- Passage enabled 与 ViewLink 无关；
- ViewLink 不产生 navigation arc。

## 10.3 Query，不是历史

Visibility opportunities：

- 是当前 Definition + State 的纯查询；
- 不持久化；
- 不产 Observation；
- 不保存 seen-before 残影；
- 不产生 Player-scoped delta；
- 不进入 Spatial Replay 语义。

Game / Perception 可在 committed cause 后查询并产生自己的事件，但这属于 Spatial 之外。

## 10.4 CoPresence

CoPresence 是 Game interaction 常用的客观关系，因此 V1 提交：

```text
CoPresenceStarted(EntityA, EntityB, PlaceId)
CoPresenceEnded(EntityA, EntityB, PlaceId)
```

规则：

- pair 使用 `(min EntityId, max EntityId)`；
- 只比较 complete pre / final state；
- 同刻多人 arrival / swap 不暴露逐 Actor prefix；
- 同 Passage不构成 Place CoPresence；途中关系由 CoTravel 与 Passage interaction 明确表达；
- CoPresence event 是 derived no-op，不在 State 保存第二份 cache。

Area membership 只做查询，V1 不提交 AreaEntered / AreaLeft。

## 10.5 CoTravel：持续的客观共行关系

`CoTravel` 与 CoPresence 平级，是一等客观 Spatial relation，但不是社会上的“结伴”。在 settled read context 的 Now，A 与 B 同时满足以下条件时成立：

```text
CoTravel(A, B)
    both TraversingPassage
    same PassageId
    same EffectiveOffset(Now)
    same TargetEndpoint
    same SpeedSnapshot
    same ArrivalDue > Now
```

这些条件表示二者拥有相同的未来 worldline；只要没有新 command，它们会持续同位前进并同时到达。Spatial 因而可以为对话、相互观察和共同遭遇提供持续 interaction locality，但绝不推断：

- 谁邀请谁、是否同意；
- friend、ally、escort、prisoner、pursuer 或 party；
- 是否注意、识别、交谈、分享秘密或共同战斗。

这些属于 Game / Perception：

```text
Spatial: CoTravel(A, B)
Game:    TravelParty / Escort / Following / Alliance / Captivity
```

查询：

```text
CoTravelingEntityCandidate
    ObserverEntityId
    TargetEntityId
    PassageId
    TargetEndpoint
    NaturalEndDue
```

`NaturalEndDue` 只表示无新 command 时共同到达的时间，不是未来行为保证。

关系规则：

- 同 Place 同刻、同速 Begin 相同 Passage，可以形成 CoTravel；
- 同方向同速但有间隔只是 ConstantGap，不是 CoTravel；
- 一方 TurnBack、AdjustTraversalPace、MatchTraversalAtContact 到别处、Remove 或 Arrival 时关系结束；
- 两人同 batch 同步 rebase 且 complete final motion 仍一致时，关系连续，不制造虚假 End / Start；
- 普通 Passage disable 不追溯 active traversal，因此不拆散 CoTravel；
- 一同到达时，canonical relation 顺序是 `CoTravelEnded → CoPresenceStarted`；
- Game-owned TravelParty 可以跨 Place 持续，但下一 Passage 的客观 CoTravel 必须由新的 motion 重新成立。

CoTravel 不保存 pair cache。`CoTravelStarted / Ended` 与当前查询都由 Traversal 在 complete pre / final state 上推导，和 CoPresence 一样避免第二真理。

relation evaluator 必须区分普通 settled query 与 Moment 的 incoming relation，不能在 arrival due boundary 偷调普通 `EffectiveOffset(T)`：

```text
CoTravelAtSettledTime(state, T)
    // public query / command pre-final；要求 ArrivalDue > T

CoTravelBeforeSettlement(state, T)
    // 仅供 SpatialMoment 的 complete entry state
    // 允许 ArrivalDue == T
    same PassageId / TargetEndpoint / SpeedSnapshot / ArrivalDue
    same affine worldline key over a non-empty common physical interval:
        SignedVelocity
        Intercept = SegmentOriginOffset - SignedVelocity * SegmentStartedAt.Ticks
                    // widened exact signed arithmetic
```

`CoTravelBeforeSettlement` 表示“进入 T 这次 settlement 时仍携带的共行关系”，并证明二者在各自 exact physical exit 前拥有同一条轨迹；它既不外露为 T 时刻的 location query，也不声称 Actor 在分数 exit 后仍物理停留于 Passage。Moment relation diff 使用这个 incoming evaluator 对比 settled final evaluator。于是共同 arrival 在 pre pair 中存在、final CoTravel 中消失，并按 `CoTravelEnded → CoPresenceStarted` 提交；未到达且运动未变的 pair 则前后都存在。

## 10.6 Same-Passage relative motion 与瞬时 contact

```text
SamePassageEntityCandidate
    ObserverEntityId
    TargetEntityId
    PassageId
    ObserverOffset
    TargetOffset
    DirectionRelation: SameDirection | Opposing
    AxialRelation: TowardA | Equal | TowardB
    SeparationUnits
    MotionRelation: CoTravel | Closing | Separating | ConstantGap
```

它只表示客观一维运动关系。`Closing` 可以支持 Game-owned pursuit / escape policy，但 Spatial 不把“正在追捕”这种意图写入 Law。

```text
PredictPassageInteraction(entityAId, entityBId)
    Predicted
        Kind
        ContactInstant
        ContactOffset
        SettlementDue
    NoFutureInteraction
    EntityNotFound
    NotTraversing
    DifferentPassage
```

这是 Objective query，不是 Player-safe Forecast；Player/HUD 是否获知该预测仍受 009 与 Perception 边界约束。SpatialSubsystem 与 public query 必须调用同一个 `PassageInteractionPlanner`，不能分别实现两套 contact math。

`PassageInteractionOccurred` 是两条 worldline 在 Passage 严格内部相交的瞬时客观事实：

- `HeadOnMeeting`：方向相反并相向交叉；
- `Overtake`：同方向较快的后方 Actor 追上并超过前方 Actor；
- ordinary Actor 非阻挡，若没有后续 command，二者继续原 motion；
- contact 自身不形成 CoTravel；只有 complete final state 形成相同 future worldline 才产生 CoTravelStarted；
- Passage interaction 是 Game / Perception 产生喊话、辨认、短暂攻击或其它 Observation 的 cause，不代表参与者实际注意到对方；
- 需要多回合停留、精确站位或长期战斗的地点仍必须升级为 Place。

Game 可以在 interaction committed 后选择 NoOp、TurnBack、AdjustTraversalPace 或合法的 MatchTraversalAtContact。不同选择能够从同一 Fork 形成“擦身而过 / 追上后超过 / 相遇后转身同行”等不同轨迹，Spatial 不替 Player 选择。

---

# 11. Events、Projector 与 Transition

## 11.1 Primary events

EventKind ID 与 payload schema 一起版本化：

| EventKind.Id | Version | Payload |
|---|---:|---|
| `graph-spatial.entity-placed` | 1 | `EntityId, PlaceId` |
| `graph-spatial.entity-removed` | 1 | `EntityId, ExpectedLocationSnapshot` |
| `graph-spatial.passage-enabled-changed` | 1 | `PassageId, ExpectedOverride?, ResultOverride?` |
| `graph-spatial.passage-mutation-scheduled` | 1 | `MutationId, PassageId, Due, Enabled` |
| `graph-spatial.passage-mutation-applied` | 1 | `MutationId, PassageId, Due, Enabled, ExpectedOverride?, ResultOverride?` |
| `graph-spatial.traversal-started` | 1 | `EntityId, TraversalId, PassageId, SpeedSnapshot` |
| `graph-spatial.traversal-turned-back` | 1 | `EntityId, TraversalId, ExpectedGeneration, SpeedSnapshot` |
| `graph-spatial.traversal-pace-adjusted` | 1 | `EntityId, TraversalId, ExpectedGeneration, SpeedSnapshot` |
| `graph-spatial.traversal-matched-at-contact` | 1 | `EntityId, TraversalId, ExpectedGeneration, MatchEntityId, ExpectedMatchTraversalId, ExpectedMatchGeneration, ContactMomentOrdinal, ContactInteractionOrdinal, ExpectedContactKind/Instant/Offset` |
| `graph-spatial.traversal-returned-to-origin` | 1 | `EntityId, TraversalId, ExpectedGeneration` |
| `graph-spatial.traversal-arrived` | 1 | `EntityId, TraversalId, ExpectedGeneration` |
| `graph-spatial.moment-resolved` | 1 | `MomentOrdinal, ResolvedMutationCount, ResolvedPassageInteractionCount, ResolvedTraversalCount` |

表中每一项都注册为精确的 `EventKind(Id, Version)`；codec 不做“只看 Id”或自动升降级。

```text
ExpectedLocationSnapshot
    AtPlace(PlaceId)
    Traversing(TraversalId, Generation)
```

Primary payload 只携带外部裁决、identity 与必要的 expected→result audit。可从 pre-state、Definition 与 event ModelTime 唯一推导的运动字段，不形成第二份 authority。

Projector effect 冻结为：

| Event | State effect |
|---|---|
| EntityPlaced | 新建 AtPlace Entity |
| EntityRemoved | 精确匹配 location；原子删除 Entity、Traversal 与 rebase guard |
| PassageEnabledChanged | 精确匹配 expected sparse override；写入或删除 result override |
| PassageMutationScheduled | 精确消费 NextMutationOrdinal 并新增 schedule |
| PassageMutationApplied | 精确匹配并删除 schedule，同时原子投影 result sparse override |
| TraversalStarted | 精确消费 NextTraversalOrdinal；创建 Generation=1 traversal 并改 Entity location |
| TraversalTurnedBack | 保留 Passage/Traversal；投影新 segment、Generation+1 与 `LastTraversalRebasedAtByEntity[EntityId]` |
| TraversalPaceAdjusted | 保留 Passage/方向/Traversal；在 event time rebase Offset，写新 speed、Generation+1 与 `LastTraversalRebasedAtByEntity[EntityId]` |
| TraversalMatchedAtContact | 以 receipt payload + current motion 重算刚 committed contact；保留 Acting TraversalId，对齐 target 当前 motion，Generation+1 与 `LastTraversalRebasedAtByEntity[EntityId]` |
| TraversalReturnedToOrigin | 删除 Traversal、Entity 回 endpoint，并写 `LastTraversalRebasedAtByEntity[EntityId]` |
| TraversalArrived | 删除 Traversal、Entity 到 target Place |
| MomentResolved | 精确消费 NextMomentOrdinal；把 PassageInteractionsSettledThrough 推进到 event time |

每个成功 primary event 使 StateRevision checked `+1`。同一 transition 中 later primary 的 expected state 读取前一事件已经投影的 working state，但 command payload 的 ExpectedStateRevision 始终与 batch pre-state 比较。

## 11.2 Derived events

| EventKind.Id | Version | Payload |
|---|---:|---|
| `graph-spatial.passage-interaction-occurred` | 1 | `MomentOrdinal, InteractionOrdinal, PassageId, Kind, EntityA/B + TraversalId/Generation, OvertakerEntityId?, ContactInstant, ContactOffset` |
| `graph-spatial.co-presence-ended` | 1 | `EntityA, EntityB, PlaceId` |
| `graph-spatial.co-presence-started` | 1 | `EntityA, EntityB, PlaceId` |
| `graph-spatial.co-travel-ended` | 1 | `EntityA, EntityB, PassageId, TargetEndpoint` |
| `graph-spatial.co-travel-started` | 1 | `EntityA, EntityB, PassageId, TargetEndpoint, NaturalEndDue` |

所有 Command / Moment transition 的 relation family order 统一为：全部 `CoTravelEnded → CoPresenceEnded → CoPresenceStarted → CoTravelStarted`；family 内按 canonical pair 与 own PlaceId / PassageId 排序。这样共同出发、共同到达和同步 rebase 都只暴露 complete-state 关系变化，不暴露 scratch prefix。

Moment transition 的 terminal audit：

```text
MomentResolved
    MomentOrdinal
    ResolvedMutationCount
    ResolvedPassageInteractionCount
    ResolvedTraversalCount
```

`PassageInteractionOccurred` 是 exact no-op，但不是含糊的 narrative annotation：Projector 必须从 event-local pre-state 中两条 current segment 的 origin / start 重建其重叠 worldline，而不是从 event ModelTime 向未来求交；随后验证 event ModelTime 等于 `ceil(ContactInstant)`、contact 严格在 Passage 内、kind / overtaker / identities / generations 全部匹配。`InteractionOrdinal` 在 Moment 内从 1 连续递增，不另设 allocator；稳定引用是 `(MomentOrdinal, InteractionOrdinal)`。

`SpatialTransition` / Replay batch audit 在 terminal 前一次性重算该 Moment 的完整 expected interaction list，验证无遗漏、无重复、phase、canonical order 与分项 count。不能让每个 event 各自 O(n²) 重算完整集合。

## 11.3 唯一 Projector

正式 reducer 与 scratch transition 必须调用同一个 `SpatialProjector`。

Projector 使用：

- exact EventKind；
- payload；
- event ModelTime；
- pre-state；
- immutable Definition。

它负责验证并投影，不读取：

- Kernel microstep 来决定领域结果；
- Game state；
- Navigator；
- Perception；
- Player/HUD。

## 11.4 Projector 时间审计

- Started 的 SegmentStartedAt = event time；
- TurnBack Offset 由旧 segment 在 event time 精确推导；
- TurnBack / Return time < old ArrivalDue；
- Arrival time = ArrivalDue；
- PassageMutationApplied 的 event time = schedule Due，并精确匹配 MutationId / PassageId / desired result；
- PassageInteractionOccurred 的 event time = ceil(exact ContactInstant)，rational contact / kind / pair identity 可从 frozen traversal 唯一重算；
- PaceAdjusted / MatchedAtContact 在 event time rebase segment，绝不原地改 speed 后重写旧运动历史；
- ReturnedToOrigin 只能落到真实 endpoint；
- Due 使用冻结 TravelTime；
- stale identity / generation 拒绝；
- 失败不产生部分状态。

## 11.5 Transition

Command transition：

```text
pre complete state
→ plan primary events
→ scratch-fold each primary through SpatialProjector
→ validate complete final state
→ diff pre/final CoPresence and CoTravel
→ append canonical derived events
→ formal fold all events
→ assert formal state == scratch state
```

Moment transition 因 interaction event 是 no-op 且必须排在 Arrival 前，使用以下明确形状：

这里的 pre 是 structurally complete 的 Resolve-entry state，不是 settled-at-T read context；它可以合法含有 `ArrivalDue == T`，且只能由 resolver 专用 evaluator 读取。

```text
pre complete state
→ evaluate incoming CoTravel with CoTravelBeforeSettlement(pre, T)
→ scratch-fold due mutation primary events
→ derive and append the complete frozen PassageInteraction list (no-op)
→ scratch-fold frozen arrival primary events
→ validate complete final state
→ diff incoming CoTravel / pre CoPresence against settled final relations
→ append canonical relation events
→ append and project the unique MomentResolved terminal
→ formal fold all events
→ assert formal state == scratch state
```

mutation 不改变 active traversal，因此 interaction 数学以 Resolve entry 的 frozen traversal set 为 authority；不能让 earlier pair event 或逐 Actor arrival prefix 改变同 tick 其余 contact。Moment transition 在 terminal 前同样执行完整防线。

Public primary event constructors应限制在 Spatial assembly 内，避免任意调用方伪造 world history。

## 11.6 Replay

- 从 `CreateEmpty(definition)` 开始；
- placements、schedules 和 mutations 都通过 committed events 建立；
- definition stamp 必须精确匹配；
- event kind / payload codec 使用本节精确版本；unknown kind/version 稳定拒绝；
- replay 不运行 command handler、Navigator、MockPlayer 或 Game rule；
- candidate 不持久化；
- Replay 遇到 TraversalMatchedAtContact 时，除 Projector 的 local kinematic audit 外，还必须在当前 committed history prefix（可以是 Fork 继承的 ancestor prefix）的较早 batch 中找到同 ModelTime、同 `(MomentOrdinal, InteractionOrdinal)` 且 payload 精确匹配的 PassageInteractionOccurred receipt；不能靠当前数学“本来可能发生”伪造一张 contact receipt；
- Fork 从 committed state + cursor 重新 Forecast；
- Fork 只发生在 batch boundary。

这里的 “state + cursor” 是 Spatial 纯计算输入，不等于 Host checkpoint 的完整持久化契约。Host checkpoint / split-run / Fork 必须同时保存或可寻址地绑定 **committed Journal prefix**；receipt resolver 以该 prefix 为信任根。Fork 的新 Lineage 继承只读 ancestor prefix，并把新事件追加到自己的 suffix，所以 contact 后、Match 前创建 Fork 或崩溃恢复仍能解析祖先 `PassageInteractionOccurred`。prefix 缺失或 hash 不匹配是 checkpoint corruption，不能降级成“连续运行可以 Match、恢复后却只能 NoRecentPassageContact”。

Replay harness 还要先按 `EventCause.BatchOrdinal` 审计 batch envelope：同 batch 的 Cause 与 ModelTime 完全相同，LogicalTimestamp.Microstep 在 Journal 中连续；Resolve batch 的 SourceId / CandidateId / Due 必须与当时持久化的 cause 一致；external batch 不得伪造 candidate metadata。逐 event Projector 只验证局部 precondition。Resolve batch 的 mutation / interaction / arrival phase、exact-rational interaction order、唯一 terminal 与三项 resolved count 可以由 pre-state 与 committed events 审计；external batch 只能审计 envelope、event-local precondition 以及可由 committed events 推导的 family order。Command aliases、rejections、producing CommandId 与完整 results 不存在于 Kernel Journal，必须由 live `SpatialTransition` / command-handler test 对 command trace 验证，pure Replay 不能声称重构它们。

---

# 12. Kernel Forecast 与 SpatialMoment

## 12.1 Candidate

```text
SpatialMomentCandidate
    DefinitionStamp
    StateRevision
    MomentOrdinal
    Due
```

`ForecastNext` 返回 0 或 1 个 candidate：

```text
Due = min(
    scheduled mutation Due,
    unconsumed Passage interaction SettlementDue,
    active traversal ArrivalDue)
```

约束：

- 无 work 返回 none；
- Due 不得小于 cursor.Now；
- active traversal overdue 是状态错误；
- interaction planner 只考虑 `SettlementDue > PassageInteractionsSettledThrough` 的 exact contact；
- Forecast 不改变 State；
- candidate identity 不依赖 collection order。

V1 reference planner 按 PassageId 分组 active traversals，并对每组 unordered pair 求 exact contact；复杂度是 `O(Σ n_p²)`。可以增加 keyed by StateRevision 的非权威 kinetic index，但结果必须与 pairwise reference planner 逐字节相同，Replay / Fork 不读取 cache。

`SpatialSubsystem` 构造时取得一个 Host manifest 中稳定且为正的 `SourceId`；所有 Spatial candidate 使用该值。`NextMomentOrdinal` 从 1 开始，candidate 的 `EventCandidateId = EventCandidateId(NextMomentOrdinal)`，payload.MomentOrdinal 必须相同。Resolve journal cause 必须精确记录这组 SourceId / CandidateId / Due；Fork 从 State 与 cursor 恢复后会 Forecast 出同一 identity。SourceId 是运行清单中的调度 identity，不进入 Graph content hash。

## 12.2 Resolve stale audit

Resolve 生成任何事件前重新验证：

- DefinitionStamp；
- StateRevision；
- MomentOrdinal；
- Due；
- 该 Due 确为当前最早；
- 由当前 Traversal 与 interaction watermark 重算的 canonical contact set。

stale candidate 稳定拒绝，不能用空 batch 冒充成功。

## 12.3 同刻 phases

在 ModelTime T：

```text
Phase 1
    按 MutationId 应用所有 Due == T 的 scheduled mutations

Phase 2
    从 complete pre-state 冻结所有 SettlementDue == T 的 Passage interactions
    按 exact ContactInstant → PassageId → kind → canonical Entity pair 排序

Phase 3
    冻结并同时投影所有 ArrivalDue == T 的 traversal arrivals
    event order 使用 (EntityId, TraversalId)

Phase 4
    比较 complete pre / final state
    依次产生全部 CoTravelEnded、CoPresenceEnded、CoPresenceStarted、CoTravelStarted
    各 family 使用 (EntityA, EntityB, own PlaceId/PassageId) canonical order

Phase 5
    MomentResolved，严格最后
    写入 mutation / interaction / traversal 三项 resolved count
    PassageInteractionsSettledThrough = T
```

mutation first 只影响 T 之后新的 BeginTraversal。它不取消已经在 Passage 上并于 T 到达的 Actor。

Passage interaction 发生于 active motion 严格内部并先于同 tick Arrival 投影。contact 恰在 endpoint 时不产生 Passage interaction，由 Arrival 后的 Place CoPresence 表达。Game / Player 不能在 Phase 2 的两个 rational contact 之间插入 external command。

三个 resolved count 分别等于 Resolve 开始时 Due == T 的 mutation 数、Passage interaction pair 数与 traversal 数。relation event 不计入这些 work count。

每个非空 Resolve 恰有一个 MomentResolved；无 work 的 candidate 不合法。

## 12.4 Confluent external input

Spatial 的同刻语义是 internal first：

```text
settle Spatial internal work due at T
→ admit external Spatial command at T
```

当前 Kernel `SimulationLoop` 会在 Forecast 前应用 external input，因此 internal-first 不能靠普通 `Run(externalInputs)` 自动得到。Host 必须通过唯一 `SpatialCommandGateway` admission seam 提交 Spatial commands：

```text
SpatialCommandGateway.Admit(cursor, commands)
    1. 检查 Spatial Forecast 是否存在 Due <= cursor.Now 的 work
    2. 若存在，拒绝接收 commands，并返回 NeedsInternalSettlement
    3. Host 使用无 external input 的 internal-settlement run 继续执行
    4. 若其它 system 在同刻请求 Decision，只缓存 request，不向 controller 暴露
    5. 重复，直到 Spatial 不再有 Due <= cursor.Now
    6. 重新验证 command ExpectedStateRevision
    7. 才把 commands 交给 Kernel external batch
```

Host / composition coordinator 不得绕过 gateway 直接提交 Spatial external input。单纯给 Spatial 较小 SourceId 也不够，因为 external input 在 candidate ordering 之前应用。

因此：

- `ArrivalDue == T` 时 Actor 先成为 AtPlace；
- `PassageInteraction SettlementDue == T` 时相遇 / 超过先提交，随后才允许 TurnBack、AdjustTraversalPace、MatchTraversalAtContact 或 Remove；
- 同刻 TurnBack 随后因 NotTraversing 拒绝；
- 不能依赖偶然注册顺序或 SourceId；
- DecisionRequest 可以暂存，但不能在 Spatial due work settle 前交给 Mock/Human/LLM；
- 真实 Kernel integration test 必须覆盖 arrival 与外部 pulse 同刻。

---

# 13. Public API 与组件边界

建议 Runtime 组件：

```text
Definitions/
State/
Commands/
Events/
Queries/
Navigation/
PassageInteractions/
Relations/
Simulation/
```

核心类型：

```text
SpatialGraphDefinition
SpatialGraphState
SpatialDefinitionStamp
SpatialReadContext

SpatialCommandHandler
SpatialCommandBatchResult
SpatialCommandAdmissionContext
CommittedPassageInteractionReceipt

SpatialProjector
SpatialReducer
SpatialTransition

ObjectiveSpatialQueries
ObjectiveNavigator
PassageInteractionPlanner
PassageRelationQueries

SpatialSubsystem : ISimSystem<SpatialGraphState, SpatialMomentCandidate, SpatialEvent>

SpatialCommandGateway
```

`SpatialCommandAdmissionContext` 是 Host 在 batch admission 时生成的瞬时、非持久化可信输入：普通 command 没有附加项；每个 Match command 必须按 CommandId 对应一张从 committed Journal 解析出的 `CommittedPassageInteractionReceipt`。它不进入 World State 或 command codec，也不能由 Player / Game payload 自报。Projector / Replay 仍依赖已经 committed 的 contact event 做历史审计，因此不会把这个瞬时 context 变成第二份 authority。

`CommittedPassageInteractionReceipt` 还携带 Journal event address / hash 与其 committed history-prefix identity。连续运行、snapshot restore 与 descendant Fork 都通过同一个 Host receipt resolver 读取该 prefix；新 lineage 可以引用其只读 ancestor prefix 中的 contact，但不能引用 sibling / future / 未提交 event。Host snapshot 因而是 `World + Cursor + committed Journal prefix handle/hash`，而不只是两个内存值。

## 13.1 Host composite lift

上面的 `SpatialSubsystem` 是可独立测试的纯 Spatial 形状。当前 Kernel 要求同一 SimulationLoop 中的 systems 共享 `TWorld / TCandidatePayload / TEventPayload`，所以 Game、pulse 与 Spatial 共用一条时间线时，由 Host / Game project 提供机械 lift：

```text
HostWorld
    Spatial: SpatialGraphState
    Game: GameState

HostCandidate
    Spatial(SpatialMomentCandidate)
    Game(...)

HostEvent
    Spatial(SpatialEvent)
    Game(...)

SpatialSystemLift
    : ISimSystem<HostWorld, HostCandidate, HostEvent>
```

Lift 只做四件事：从 HostWorld 取得 Spatial state；委托纯 Spatial Forecast / Resolve；原样保留 SourceId、CandidateId 与 Due；把 payload 包入 Host union。Host root reducer 按 exact union case / EventKind 把 Spatial event 交给 `SpatialReducer`，把 Game event 交给 Game reducer。`SpatialCommandGateway` 同样在一个 Host cursor + HostWorld.Spatial revision snapshot 上 admission，并把 accepted Spatial events 包成 HostEvent 后提交。

Lift、Host union 与 composite reducer 依赖 Spatial；Spatial Runtime 不引用它们。Random pulse、`FishEaten` 与其它 Game event 因而可以进入同一 Kernel Journal，但不能回调或改写 Spatial reducer。Pure replay 只分派 committed union events，不重跑 Game rule。

## 13.2 内容适配器

内容适配器建议独立：

```text
Spatial.GraphContent
    GraphSourceDto
    GraphCompiler
    CanonicalGraphWriter
```

架构守卫：

- Runtime production project只依赖 Kernel；
- Runtime 不做文件 I/O；
- GraphContent 不依赖 Game 或 Player；
- Game adapter依赖 Spatial，而 Spatial 不反向依赖 Game；
- MockPlayer 只存在于 tests / sample；
- HUD/Fog 不进入 Runtime 类型系统。

---

# 14. 权威不变量

## 14.1 Definition

- stamp 完整且受 RulesVersion gate；
- Area 是单 root tree；
- Place 引用存在 Area；
- Passage endpoints 存在且不同；
- Length 正且 EndpointA/B order 保留；
- ViewLink directed、非 self、引用合法；
- canonical hash 不依赖输入集合顺序；
- Definition immutable。

## 14.2 State

- Entity 恰好 AtPlace 或 TraversingPassage；
- Traversal 与 Entity 一一对应；
- Traversal math 完整合法；
- sparse override canonical；
- scheduled key 唯一；
- allocators 指向未使用正 ordinal；
- Definition / State / batch / schedule 数量不超过 `RulesVersion` 工程上限；
- LastTraversalRebased guard 与 PassageInteractionsSettledThrough 可 Replay；
- interaction watermark 单调，已消费 contact 不会在同 cursor time 重发；
- State 不保存 adjacency、route、CoPresence、CoTravel、Passage occupancy/order 或 visibility cache 第二真理。

## 14.3 Commands

- duplicate CommandId 整批拒绝；
- conflict 结果不依赖输入顺序；
- rejected command 零事件、零状态改变、零 allocator 消费；
- Begin 只从 endpoint AtPlace 开始；
- TurnBack 保留 PassageId / TraversalId；
- TurnBack / AdjustTraversalPace / MatchTraversalAtContact 都从 command time 的当前 Offset rebase，绝不重写旧 segment；
- 同 Entity / ModelTime 最多一次成功 traversal rebase；
- Match 只能引用 current ModelTime 刚 committed 且可重算的 contact；
- RulesVersion 容量按完整 planned final state、对所有同类 consumers 原子预检；
- Passage disable 不追溯 active traversal；
- Continue / Stop / Wait 不属于 World command。

## 14.4 Events与Replay

- Projector 是唯一写状态路径；
- ordinary event ModelTime 是运动 timestamp authority；Passage interaction 的 exact rational instant 只用于同 tick contact audit / order，Kernel settlement time 仍是其 ceil tick；
- reducer 不寻路、不调用 Game、不做 perception；
- derived events exact no-op；
- CoTravel / CoPresence 都从 complete pre / final state 推导；
- Moment 的 incoming CoTravel 使用 segment affine key，不在 `ArrivalDue == T` 调 ordinary EffectiveOffset；
- scratch state 与 formal fold 完全相等；
- Replay、split run 与 Fork deterministic，Host checkpoint 始终绑定可验证的 committed Journal prefix。

## 14.5 Kernel

- 0 或 1 earliest candidate；
- overdue 是错误；
- stale 在事件前拒绝；
- mutation → Passage interaction → arrival → relation → terminal；
- terminal event 唯一且最后；
- external command 在同刻 internal work 之后；
- Host 为 due internal work 预留 microstep；reserve 违约是原子 terminal failure，不是可重试空 Resolve。

---

# 15. 可证伪验收

## 15.1 共享 CoastGraph

使用 §4.7 的 Definition，固定默认 `SpeedSnapshot = 1 distance unit / ModelTick`。

Graph 特性：

- 3 Area；
- 6 Place；
- 5 Passage；
- 1 directed ViewLink；
- `fork → grotto` 有两条 total cost 都为 10 的路线；
- `island` 从 lagoon 客观可见但不可达。

同价 tie 固定选择 route key 较小的 `fork-cliff → cliff-grotto`。关闭 `fork-cliff` 后选择 `fork-ford → ford-grotto`。

## 15.2 P0 验收矩阵

| ID | 范围 | 硬断言 |
|---|---|---|
| IO-1 | Graph I/O | JSON 精确编译为 3 Area / 6 Place / 5 Passage / 1 ViewLink；canonical writer 的 exact bytes、无 BOM/换行、property/array order 与 SHA-256 用手写 golden 锁定；round-trip 语义与 hash 相等；A→B 的 origin=0、B→A 的 origin=Length。 |
| IO-2 | Determinism | 任意重排输入数组，definition、hash、navigation、visibility result 完全相等；canonicalization 与 round-trip 永不交换 EndpointA / B。 |
| IO-3 | Validation | unknown property、duplicate JSON property、numeric overflow、duplicate ID、unknown ref、Area cycle、多 root、坏 endpoint、Length≤0、坏 ViewLink、Passage 超 RulesVersion 上限、未知 schema/rules 全部在创建 State 前拒绝。 |
| CMD-1 | Command batch | 同一 commands 的任意排列产生逐字节相同 events/results/final state；duplicate CommandId / 超 256 commands 整批结构拒绝；同 Entity lifecycle conflict；同 Passage immediate same desired 的 leader/no-change/alias、different desired conflict；existing schedule alias 与 new schedule leader 映射精确；同 `(PassageId,Due)` 的不同 desired（包括与 existing schedule 相反）整组 MutationConflict；future desired 即使等于当前 effective value 仍分配并持久化；leader rejection 向 alias 传播；同批 disable+Begin 按 topology-first 使 Begin 拒绝；两个合法 Begin 而只剩一个 Traversal ordinal 时全部 allocator consumer 拒绝；entity / mutation RulesVersion 只余部分容量时也全组拒绝，不能让低 ID 抢占；rejection precedence 精确；所有 rejected/conflict 零 event、零 Revision、零 allocator。 |
| POS-1 | Location | Place 多 Actor 合法；Begin 原子形成唯一 Traversal；Remove 在途 Entity 原子清理 traversal 与 guard。 |
| QRY-1 | Settled read | structural state 在 ArrivalDue==Now 或 unconsumed PassageInteractionDue==Now 尚未 settle 时不能创建 SpatialReadContext；public query / Navigator 稳定拒绝；Resolve 后 snapshot 可读。missing entity 与合法 empty 分离，结果 immutable 且 canonical。 |
| MOV-1 | Lazy movement | length10/speed1/T0 → Due10；T0..9 Offset 精确；T10 只允许 Arrival；Journal 无 progress event。 |
| MOV-2 | Ceil | length10/speed3 → Due4；T3 Offset9；T4 精确 endpoint。 |
| MOV-3 | TurnBack | T4 掉头：Offset4，同 Passage/Traversal，Generation+1，回 origin Due8，旧 Due10 candidate stale。 |
| MOV-4 | Liveness | 零进度 ReturnedToOrigin；同 Entity/T 的第二次 TurnBack / AdjustTraversalPace / MatchTraversalAtContact rebase 在事件、Revision、allocator 前拒绝。 |
| INT-1 | Exact contact math | head-on fractional golden：length10，A 从 0 以 +4、B 从 10 以 -3，ContactTime=`10/7`、Offset=`40/7`、T2 settlement；integer contact、轴镜像、A/B identity swap、最后 ceil tick 与 UInt64/UInt128 overflow vectors 全部不用浮点且结果 canonical；`-1/2 → floor whole -1 + 1/2 → ceil 0`、`-1` 与 long.MinValue 邻域锁定 signed rational bytes。 |
| INT-2 | Meeting / overtake | 整数 tick 从未同 Offset但世界线交叉仍产 HeadOnMeeting；同向快者从后追上产 Overtake 且 semantic roles 不随 EntityId 改变；同速分离为 ConstantGap；同起点不同速的 `f=0` 不伪报；endpoint contact 不产 interaction。 |
| INT-3 | Contact liveness | interaction resolve 后 watermark 推进，同 cursor re-Forecast 不重发；future contact 前 TurnBack / AdjustTraversalPace / Remove 使旧 candidate stale；disabled Passage 上 active actors 仍可 meeting/overtake；full/split/Fork 在 contact 前后相等。 |
| NAV-1 | Navigation | physical Passage 派生双向 arc；同价 tie 固定；disable 改走 detour；全断开 NoRoute。overflow 三向量：无关 overflow dead-end + disconnected goal → NoRoute；representable goal 与 overflow branch 并存 → RouteFound；唯一 goal route 总成本不可表示 → CostOverflow。 |
| VIS-1 | Visibility | lagoon→island 单向 candidate；reverse 无；ViewLink 不产生路径；Traversing observer 返回空。 |
| REL-1 | CoPresence | same Place pair canonical；Traversing 不共处；多人同刻 arrival 只按 final state 产 delta。 |
| REL-2 | CoTravel | 两个陌生 Actor 同 Place 同刻同速 Begin：只产生客观 CoTravelStarted，不产生 Alliance / Conversation；单方调速或掉头结束；双方同批同步 rebase 且 final worldline 相同则关系连续；共同 Arrival 时用 incoming evaluator（不得读 EffectiveOffset(T)）证明 pre pair，CoTravelEnded 先于 CoPresenceStarted；三人 pair set canonical 且 State 无 pair cache。 |
| REL-3 | Contact response | fractional HeadOn / Overtake committed 后，同 T MatchTraversalAtContact 可以显式对齐并形成 CoTravel；NoOp 继续原 motion；缺少 trusted admission receipt、伪造 expected ordinal/payload、旧 contact、stale generation、contact+arrival 同 T、target 同批改变、A↔B match cycle 全部稳定拒绝。 |
| EVT-1 | Projector | 每类 v1 payload 的 pre/post/revision 精确；`EventKind(listedId,1)` 两部分都匹配；MutationApplied 原子消费 schedule+override；PassageInteraction 从 segment overlap 历史重建 exact rational / kind / pair，不能从 settlement Now 向未来求交；derived exact no-op；scratch fold 与 formal reducer state 相等；unknown kind/version 拒绝；replay batch envelope 的 cause/modelTime/microstep/order/terminal 审计精确。 |
| MUT-1 | Topology | ordinary close 只阻止之后进入；active traversal 可继续/掉头/interaction/到达；reopen 恢复。另设同一 Passage scheduled disable、contact 与 active arrival 都 Due=T：disable → contact → arrival，final AtPlace 且 Passage disabled，三项 resolved count 精确，T 的后续 Begin 被拒绝。 |
| SIM-1 | Moment | Forecast 唯一 earliest；SourceId 来自稳定 manifest、CandidateId 等于 NextMomentOrdinal，Fork 后 identity 相同；mutation / exact-rational interaction / arrival / relation total order 稳定；同刻关系只按 pre/final 产生；三项 work count 精确；watermark 单调；恰一个最后 MomentResolved；idle 无 candidate。 |
| CON-1 | Confluence | InteractionDue / ArrivalDue 与外部 pulse 同 T：interaction 与 arrival 先完成，随后 stale TurnBack / Match 稳定拒绝；注册顺序不改变结果。 |
| ARCH-1 | Boundary | Runtime production reference 只有 Kernel；无 Player/HUD/Knowledge 类型；直接绕过 SpatialCommandGateway 在 due boundary 提交 external Spatial input 的 integration harness 必须失败。 |
| RPL-1 | Replay | full run / split run / pure replay 的 State、Journal、interaction watermark、next candidate 相等；contact 后、Match 前 snapshot/restore 仍从 prefix resolver 得到同 receipt；Replay 不运行 Navigator、interaction consumer 或 Mock。 |
| FORK-1 | Fork | contact 前 fork：Continue 保留 meeting/overtake，TurnBack / AdjustTraversalPace 分支改变或取消 contact；contact 后同 T fork 不重发；contact 后、Match 前的 descendant 可从 ancestor prefix 合法 Match，但 sibling/future receipt 拒绝；源 state 不变，各分支 replay 相等。 |
| PERF-1 | Passage interaction budget | reference pair planner 与 small-integer exact-rational oracle property-equivalent；64 / 256 active traversals on one Passage 的 Forecast、event bytes、Journal batch 与 Replay 达到 implementation plan 冻结预算；256 Actor worst-case Moment 为 102,273 events 并低于 131,072 上限；规则上限在 admission 前拒绝，microstep headroom 由 Host 预留，破坏 reserve 时 terminal fail 而非重复 Resolve。 |

P0 全部通过才算 Objective Graph Spatial vertical slice 成立。

## 15.3 Passage interaction / CoTravel 手算 golden

### 迎面相遇与同刻选择

```text
Passage length = 10
T0  A: offset 0  → B, speed 4, ArrivalDue T3
T0  B: offset 10 → A, speed 3, ArrivalDue T4

Exact contact:
    ContactTime   = 10 / (4 + 3) = 10/7
    ContactOffset = 4 * 10/7     = 40/7
    SettlementDue = T2
```

T2 的 SpatialMoment 先提交 `PassageInteractionOccurred(HeadOnMeeting)`，不自动停下、不自动对话。随后 Game / Player 可以：

- NoOp：A、B 继续擦身而过；
- B TurnBack：从 B 在 T2 的普通边界 Offset 反向；
- B `MatchTraversalAtContact(A)`：用刚 committed contact 作为 authority，B 在该 board-game tick 内完成转向 / 调速，T2 边界对齐 A 的 current motion，形成 `CoTravelStarted(A,B)`。

第三种结果是显式、可 Replay 的 contact response，不撤销 interaction event，也不允许引用旧 contact。注意、认出和“为何转身”仍由 Perception / Player 决定。

### 同向追上与超过

```text
Passage length = 10
T0  slow 从 A 出发，speed 1
T1  fast 从 A 出发，speed 3

At T1:
    slow offset = 1
    fast offset = 0

Exact contact:
    ContactTime   = T1 + 1/(3-1) = T1.5
    ContactOffset = 1.5
    SettlementDue = T2
```

T2 提交 `PassageInteractionOccurred(Overtake, Overtaker=fast, Overtaken=slow)`。若 fast NoOp，二者继续分离且不形成 CoTravel；若 fast 合法 Match slow，则形成持续 CoTravel。两条分支都必须能从 contact 前 checkpoint Fork 并稳定 Replay。

### 自然共行

```text
T0  A、B 同处 lagoon
T0  分别 Begin lagoon-fork，SpeedSnapshot 都为 1
    CoPresenceEnded(A,B,lagoon)
    CoTravelStarted(A,B,lagoon-fork)

T4  A AdjustTraversalPace(speed=2)
    CoTravelEnded(A,B,lagoon-fork)
```

没有任何 Spatial event 声称 A 与 B 是朋友。若二者一路保持相同 motion 并同时到达，则同一 Moment 的关系顺序是 `CoTravelEnded → CoPresenceStarted`。

## 15.4 Seeded RandomWalker

RandomWalker 只验证 World 能长期运行，不替代 P0 精确测试。

测试 fixture 固定：

```text
PRNG                 xorshift32
Seed                 0x00C0A57
Initial placement    wanderer@lagoon
Default speed        1
Wander pulse         T = 3, 6, 9, ...
fork-cliff close     T = 25
fork-cliff reopen    T = 60
Non-Idle decisions   100
```

`xorshift32` 精确使用 unsigned 32-bit wrap：`x ^= x << 13; x ^= x >> 17; x ^= x << 5`。选择 index 为 `nextUInt32 % canonicalOptionCount`。

close / reopen 都在 T0 作为 scheduled mutations 预排。T25 与 T60 先由 Spatial internal settlement 应用 mutation，再处理同刻 wander pulse；因此 T60 pulse 必须读取 reopened topology。

```text
AtPlace:
    从按 PassageId 排序的 enabled incident Passages 中
    用 seeded PRNG 选一个并请求 BeginTraversal
    若 incident list 为空则稳定 Idle，不提交 Spatial command

Traversing:
    在 test driver 产生的正时间 wander pulse 上
    从固定顺序 [Continue, TurnBack] 中
    使用 nextUInt32 % 2 选择
```

规则：

- PRNG algorithm / seed / initial placement / pulse times / mutation times 只属于冻结的 test fixture，不进入 Spatial authority；
- RandomWalker 不直接修改 State；
- 它不调用 Projector；
- 它只通过 Game test adapter 获得已裁决 SpeedSnapshot；
- Continue 不产 Spatial command；
- `Non-Idle decision` 是 Game test adapter 接受的一次 Begin / Continue / TurnBack；Continue 计入 decision 数但不计入 Spatial command 数，Idle 两者都不计；
- live test driver 单独保存所有 Non-Idle decision trace 与实际 Spatial command trace，Kernel Journal 只保存这些命令产生的 committed events；
- Replay 不重新调用 RandomWalker；
- 前 10 个 decision/event 使用人工核算、hard-coded golden trace，明确 PassageId、Continue/TurnBack、Due 与 Generation，不能由 Walker 或 Navigator 反算期望值；
- 再运行至少 100 个 accepted decisions，并注入 fixed-time close/reopen；
- 全程无 overdue、孤儿 traversal、零时循环或 collection-order nondeterminism。

## 15.5 大鱼吃小鱼薄组合测试

Game test adapter 在 Spatial 外保存：

```text
FishSize[EntityId]
```

场景：

- big-fish 从 lagoon 按 scripted trace 移动：`lagoon-fork → fork-ford → ford-grotto`；
- small-fish 停留 grotto；
- SpeedSnapshot=1；big-fish 分别在 T10、T13、T20 抵达 fork、ford、grotto；
- Spatial 先提交完整 Arrival 与 CoPresenceStarted；
- Game rule 比较 Size，提交 `FishEaten`；
- Game 再请求 Spatial RemoveEntity(small-fish)。

断言：

- Size 不进入 Spatial Definition / State / Event；
- Spatial 不知道“吃”；
- arrival final state 先于 Game rule；
- Remove 后 CoPresenceEnded 按 final state 产生；
- `FishEaten` 先作为 Game-owned committed event 落入一个完整 batch；随后 Game 在同一 ModelTime 通过 gateway 提交独立 Remove command batch；
- replay 只投影已经提交的 Spatial 与 Game events，不重新决定谁吃谁。

这个测试证明游戏规则可以组合在位置和共处事实之上，而无需污染 Spatial。

## 15.6 建议测试目录

```text
tests/GraphSpatial.Tests/
    Definition/
    GraphIo/
    State/
    Commands/
    Events/
    Movement/
    PassageInteractions/
    Relations/
    Queries/
    Navigation/
    Simulation/
    Replay/
    Acceptance/
```

---

# 16. 实施顺序

推荐依赖波次：

```text
1. Strong IDs + distance/time + canonical rational arithmetic
2. Definition + validator + canonical Graph codec/hash
3. State + interaction watermark + complete-state validator
4. Event kinds/payloads + single Projector
5. Commands/results/conflicts + segment rebase + SpatialTransition
6. Queries ─┬─ Objective Navigator
             ├─ Objective visibility opportunities
             └─ CoTravel / same-Passage relations
7. Passage interaction reference planner + Forecast/Resolve + SpatialMoment
8. Event codec + Replay/Fork/split-run
9. Coordinator integration + deterministic MockPlayer + Passage golden + RandomWalker + BigFish slice
```

第 6 波可以并行；第 7 波必须等待 rational arithmetic、Projector 与 Transition 稳定。Passage interaction 先实现 `O(Σ n_p²)` reference planner，再根据 benchmark 决定是否加入非权威 kinetic index；优化版本必须与 reference planner 逐字节等价。MockPlayer 是最后的纵向验收，不能反向把 Player/HUD 类型带进 Spatial。

## 16.1 与现有 `src/Spatial` 的关系

可复用：

- Kernel seam；
- DefinitionStamp / RulesVersion / ContentHash 经验；
- immutable State；
- ID 与 total order；
- command batch / result；
- Projector / Reducer / Transition；
- allocator / generation；
- scheduled mutation；
- single earliest SpatialMoment；
- scratch = formal replay 防线；
- Replay / Fork / split-run 测试；
- non-blocking Actor；
- pre / final relation diff。

需要替换：

| Grid Spatial | Graph Spatial World |
|---|---|
| GridMapDefinition | SpatialGraphDefinition |
| CellRef | AtPlace / TraversingPassage |
| Orthogonal edge | explicit two-ended Passage |
| Portal special case | ordinary Passage |
| Cell MoveCost | PassageLength + SpeedSnapshot |
| Cell / Portal override | Passage enabled override |
| Anchor | PlaceId |
| Zone cells | AreaPath query / game tags |
| EntityStepped | Traversal lifecycle events |
| SameCell | SamePlace CoPresence |
| 无直接等价 | Passage CoTravel + exact-rational meeting / overtake |
| StrictSupercover LOS | same-place / ViewLink opportunities |

本文不裁决就地重构还是并行 `GraphSpatial` project。执行前应根据现有 Grid Spatial 的复用成本另写 implementation plan；无论选择哪条路径，都不能让 Grid 与 Graph 同时成为 authority。

---

# 17. 结论

Graph Spatial World V1 的甜点位置是：

1. Area / Place / Passage 提供稳定语义拓扑；
2. Actor 在 Place 或正沿 Passage 运动；
3. Passage 可掉头、调速和基于刚 committed contact 匹配共行，但不可长期静止；
4. CoTravel 提供持续途中 interaction locality，相遇 / 超过提供瞬时 objective opportunity；
5. Length 与 ModelTime 分离，并以 lazy Offset + exact-rational contact math 保持 DEVS-like Simulation；
6. Objective navigation、same-place、same-Passage 与 ViewLink 只回答客观空间关系；
7. Kernel 保存 mutation、interaction、arrival、关系变化与 Replay；
8. Graph Content 可以被严格读取、验证、规范写回与 hash；
9. MockPlayer 足以验证 World，无需先实现 Player cognition。

一句话总结：

> **先把客观 Graph 世界做成一个小而严密、可以相遇、超过、共行并长期重放的空间机器；Spatial 证明轨迹与机会，Player 决定是否注意、回应、追逐、结伴或并肩作战。**
