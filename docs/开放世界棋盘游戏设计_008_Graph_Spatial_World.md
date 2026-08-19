# Design Note 008：Graph Spatial World
## ——以 Place、Passage、客观可见性与模型时间构成可实施的空间框架

**状态：功能架构已冻结；用于指导后续详细设计与实施**

**日期：2026-08-20**

**讨论基线：`spatial-grid-v1-baseline`（`main @ ed6b6d3`）**

**认知层拆分：** Player HUD、战争迷雾、主观地图、Claims、LLM DecisionView 与 Human realization 已移至 [Design Note 009](./开放世界棋盘游戏设计_009_Player空间HUD与战争迷雾_备忘.md)。它们不属于本子系统。

---

# 1. 定位与阶段性决定

Graph Spatial World 是 DramaBoard 中唯一的客观空间 authority。

它只负责五件事：

1. Graph Definition 在磁盘上的读取、验证、规范化与写回；
2. Actor 的客观位置；
3. Actor 在 Place 与 Passage 之间的移动；
4. 客观空间可见机会与共处关系；
5. Objective Graph 上的确定性导航。

它与 Kernel 集成，保存模型时间中的空间事实，但不管理 Player 的记忆、信念、地图笔记或行动策略。

一句话边界是：

> **Spatial World 决定世界在哪里、怎样相连、Actor 正在哪里以及客观上可能看见什么；调用方决定 Actor 为什么行动、实际注意到什么以及如何理解。**

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
- Passage 中 Continue 或 TurnBack，但不能静止；
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
- 同 Passage 相遇、追逐、超车、碰撞；
- Passage capacity、reservation 或 Actor blocking；
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
- ArrivalDue；
- total route cost；
- TraversalId / MutationId / MomentOrdinal；
- Generation；
- StateRevision。

Spatial 不持久化逐 tick progress，也不产生 progress event。

## 3.4 Passage 拆分的取整

把一个 Passage 在语义地点拆成两个 Passage 时：

- 两段 Length 之和必须等于原 Length；
- 每段 travel time 独立计算；
- 新增一个真实 Place 最多引入一次 ModelTick 的 ceil 差异；
- V1 不为消除该差异保存跨 Place 的分数余量；
- authoring validation 应报告拆分前后的默认速度 travel-time 差异。

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
- 非法 ViewLink。

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
    LastTurnBackAtByEntity[]
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

## 5.5 LastTurnBack guard

`LastTurnBackAtByEntity` 是 sparse、可 Replay 的权威状态。

同一 Entity / ModelTime 最多接受一次 TurnBack，无论结果是反向运动还是零进度返回。Entity removal 同时清理 guard。

## 5.6 Revision

- 每个 state-changing primary event 使 StateRevision checked `+1`；
- derived event 不改 State；
- no-op command 不改 Revision；
- `MomentResolved` 消费 MomentOrdinal 并改变 StateRevision；
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

`SpatialReadContext.Create` 除 structural complete validation 外，还要求每个 active traversal 都满足 `SegmentStartedAt <= Now < ArrivalDue`，并且不存在 `Due <= Now` 的 scheduled mutation。也就是说它只代表 **settled-at-Now** snapshot。

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
GetSamePlaceEntityCandidates(observerEntityId)
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
| SamePlaceEntityCandidates | §10.1 的 immutable records | TargetEntityId；observer unknown → EntityNotFound；observer Traversing → Found(empty) |
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

Game / test adapter 在提交 Begin 或 TurnBack 前，可以裁决：

- Inventory、Faction、ticket；
- movement mode；
- Actor-specific speed；
- semantic intent。

它只把已经裁决的正 SpeedSnapshot 与明确 command 交给 Spatial。

Spatial 仍重新验证：

- ExpectedStateRevision；
- Entity 当前 location；
- endpoint；
- BeginTraversal 时 Passage effective enabled；TurnBack 不检查 enabled；
- traversal identity / generation；
- time boundary；
- speed、Due 与 overflow。

Projector 与 Replay 不回调外部 validator。

## 7.3 Batch 结构

- CommandId 必须正且整批唯一；duplicate 是整批结构错误；
- Handler 只接受 settled-at-command-time pre-state；若存在 scheduled mutation 或 arrival `Due <= now`，整批以 `InternalWorkMustSettle` 拒绝且零事件，从而让 gateway bypass 也不能改变 World；
- 规划前按 CommandId Ordinal 规范排序；
- results 按 CommandId 返回；
- 输入排列不改变 events、results 或 allocator；
- 同 Entity 多个 lifecycle command 整组 conflict；
- 同 Passage 多个 immediate desired：相同 alias，不同 conflict；
- scheduled mutation 依 §5.4 alias / conflict；
- topology commands 先形成 working topology，movement command 随后验证；
- 一个 batch 内所有 `ExpectedStateRevision` 都与唯一 input / pre-state revision 比较，不能与逐事件增长的 scratch revision 比较；
- rejected command 不产 event、不改 Revision、不消费 allocator；
- 所有 allocator、Revision、Generation 与 arithmetic capacity 在提交前整体预检。

Command transition 的 Journal 顺序固定为 command family phase，再按 producing CommandId 排序；alias 与 rejection 不产生 event。不能用调用方输入排列决定 event order。

为使 Handler 成为唯一结果的 total planning function，V1 再冻结以下细则：

1. family phase 固定为 `immediate topology → scheduled topology → entity existence → movement`；family 内再按 producing CommandId。Entity existence 是 `PlaceEntity / RemoveEntity`，movement 是 `BeginTraversal / TurnBack`。同一 Entity 在这四种 lifecycle commands 中出现两个或更多命令时，该组全部 `CommandConflict`，V1 不提供 Place+Begin 组合捷径。
2. 同一 Passage 的 immediate commands 先按 target 分组。desired 不同则整组 conflict；desired 相同时最小 CommandId 是 canonical leader。若 desired 已是 current effective value，leader 为 `AcceptedNoChange`；否则 leader 为 `Accepted`。其余命令只有在 leader 成功或 no-change 时才是 `AcceptedAlias(AliasOfCommandId=leader)`。
3. scheduled command 的 canonical key 是 `(PassageId, Due)`。同 key desired 不同则整组 `MutationConflict`。若 pre-state 已有同 key、同 desired schedule，最小 CommandId 以 `AcceptedAlias(MutationId=existing, AliasOfCommandId=null)` 指向它，其余同批命令 alias 到最小 CommandId；若没有 existing schedule，最小 CommandId 是唯一 allocator consumer，其余 alias 到它。future desired 即使等于 current effective value也不是 no-op。
4. canonical leader 最终被玩法校验拒绝时，同组 aliases 继承同一 rejection code，而不是留下 `AcceptedAlias`。
5. 每个 allocator domain 先找出其它校验均会成功的 canonical consumers，再整体检查容量。容量不足时该 domain 的全部 consumers 及 aliases 都以 `AllocatorExhausted` 拒绝；不按低 CommandId 部分接收。其它不消费该 allocator 的独立 command 仍可成功。StateRevision 总容量不足是 terminal invariant failure，整次调用在返回任何 events/results 前抛出，绝不部分提交。
6. 普通 rejection precedence 固定为：group conflict → unknown/static reference → expected pre-state revision → location/traversal/time boundary → enabled/speed/arithmetic → allocator capacity。一个 command 只返回最先命中的 code。Batch 结构错误与 unsettled pre-state 分别在分组和玩法校验之前整批处理。

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
AlreadyTurnedAtThisTime
DueBoundaryMustSettle
InternalWorkMustSettle
DueNotFuture
CommandConflict
MutationConflict
InvalidSpeed
ArithmeticOverflow
AllocatorExhausted
```

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
- `LastTurnBackAt != now`；
- 新 speed 正；
- generation 与 Due 有容量。

在 now 物化 Offset：

- 若仍精确位于初始 endpoint，产生 ReturnedToOrigin；
- 若当前 segment 从内部 Offset 开始且 now 等于 SegmentStartedAt，拒绝 `NoElapsedMovement`；
- 否则保留 PassageId / TraversalId，把 target 改为另一 endpoint，Generation checked `+1`。

两种成功 outcome 都写入 LastTurnBackAt。

## 8.4 Arrival

Arrival 只由 SpatialMoment 在 `now == ArrivalDue` 产生：

- 删除 TraversalState；
- Entity 变为 AtPlace(target)；
- 普通 Passage disable 不阻止 arrival；
- arrival 后的下一 traversal 必须是独立 external command；
- reducer 不自动寻路或续程。

## 8.5 RemoveEntity

Remove 必须原子清理：

- Entity；
- active Traversal；
- LastTurnBackAt guard。

它不删除 Definition、Passage mutation 或其它 Entity。

## 8.6 Passage 中不静止

Spatial 不表达 Passage 中长期停留。

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
- 同 Passage 不构成 CoPresence；
- CoPresence event 是 derived no-op，不在 State 保存第二份 cache。

Area membership 只做查询，V1 不提交 AreaEntered / AreaLeft。

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
| `graph-spatial.traversal-returned-to-origin` | 1 | `EntityId, TraversalId, ExpectedGeneration` |
| `graph-spatial.traversal-arrived` | 1 | `EntityId, TraversalId, ExpectedGeneration` |
| `graph-spatial.moment-resolved` | 1 | `MomentOrdinal, ResolvedWorkCount` |

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
| EntityRemoved | 精确匹配 location；原子删除 Entity、Traversal 与 turn guard |
| PassageEnabledChanged | 精确匹配 expected sparse override；写入或删除 result override |
| PassageMutationScheduled | 精确消费 NextMutationOrdinal 并新增 schedule |
| PassageMutationApplied | 精确匹配并删除 schedule，同时原子投影 result sparse override |
| TraversalStarted | 精确消费 NextTraversalOrdinal；创建 Generation=1 traversal 并改 Entity location |
| TraversalTurnedBack | 保留 Passage/Traversal；投影新 segment、Generation+1 与 LastTurnBackAt |
| TraversalReturnedToOrigin | 删除 Traversal、Entity 回 endpoint，并写 LastTurnBackAt |
| TraversalArrived | 删除 Traversal、Entity 到 target Place |
| MomentResolved | 精确消费 NextMomentOrdinal |

每个成功 primary event 使 StateRevision checked `+1`。同一 transition 中 later primary 的 expected state 读取前一事件已经投影的 working state，但 command payload 的 ExpectedStateRevision 始终与 batch pre-state 比较。

## 11.2 Derived events

| EventKind.Id | Version | Payload |
|---|---:|---|
| `graph-spatial.co-presence-ended` | 1 | `EntityA, EntityB, PlaceId` |
| `graph-spatial.co-presence-started` | 1 | `EntityA, EntityB, PlaceId` |

Moment transition 的 terminal audit：

```text
MomentResolved
    MomentOrdinal
    ResolvedWorkCount
```

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
→ diff pre/final CoPresence
→ append canonical derived events
→ formal fold all events
→ assert formal state == scratch state
```

Moment transition在 terminal 前同样执行完整防线。

Public primary event constructors应限制在 Spatial assembly 内，避免任意调用方伪造 world history。

## 11.6 Replay

- 从 `CreateEmpty(definition)` 开始；
- placements、schedules 和 mutations 都通过 committed events 建立；
- definition stamp 必须精确匹配；
- event kind / payload codec 使用本节精确版本；unknown kind/version 稳定拒绝；
- replay 不运行 command handler、Navigator、MockPlayer 或 Game rule；
- candidate 不持久化；
- Fork 从 committed state + cursor 重新 Forecast；
- Fork 只发生在 batch boundary。

Replay harness 还要先按 `EventCause.BatchOrdinal` 审计 batch envelope：同 batch 的 Cause 与 ModelTime 完全相同，LogicalTimestamp.Microstep 在 Journal 中连续；Resolve batch 的 SourceId / CandidateId / Due 必须与当时持久化的 cause 一致；external batch 不得伪造 candidate metadata。逐 event Projector 只验证局部 precondition。Resolve batch 的 phase/order、唯一 terminal 与 ResolvedWorkCount 可以由 pre-state 与 committed events 审计；external batch 只能审计 envelope、event-local precondition 以及可由 committed events 推导的 family order。Command aliases、rejections、producing CommandId 与完整 results 不存在于 Kernel Journal，必须由 live `SpatialTransition` / command-handler test 对 command trace 验证，pure Replay 不能声称重构它们。

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
    active traversal ArrivalDue)
```

约束：

- 无 work 返回 none；
- Due 不得小于 cursor.Now；
- active traversal overdue 是状态错误；
- Forecast 不改变 State；
- candidate identity 不依赖 collection order。

`SpatialSubsystem` 构造时取得一个 Host manifest 中稳定且为正的 `SourceId`；所有 Spatial candidate 使用该值。`NextMomentOrdinal` 从 1 开始，candidate 的 `EventCandidateId = EventCandidateId(NextMomentOrdinal)`，payload.MomentOrdinal 必须相同。Resolve journal cause 必须精确记录这组 SourceId / CandidateId / Due；Fork 从 State 与 cursor 恢复后会 Forecast 出同一 identity。SourceId 是运行清单中的调度 identity，不进入 Graph content hash。

## 12.2 Resolve stale audit

Resolve 生成任何事件前重新验证：

- DefinitionStamp；
- StateRevision；
- MomentOrdinal；
- Due；
- 该 Due 确为当前最早。

stale candidate 稳定拒绝，不能用空 batch 冒充成功。

## 12.3 同刻 phases

在 ModelTime T：

```text
Phase 1
    按 MutationId 应用所有 Due == T 的 scheduled mutations

Phase 2
    冻结并同时投影所有 ArrivalDue == T 的 traversal arrivals
    event order 使用 (EntityId, TraversalId)

Phase 3
    比较 complete pre / final state
    先产生全部 CoPresenceEnded，再产生全部 CoPresenceStarted
    各 family 使用 (EntityA, EntityB, PlaceId) canonical order

Phase 4
    MomentResolved，严格最后
```

mutation first 只影响 T 之后新的 BeginTraversal。它不取消已经在 Passage 上并于 T 到达的 Actor。

`ResolvedWorkCount` 等于 Resolve 开始时 Due == T 的 mutation 数加 traversal 数。

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

SpatialProjector
SpatialReducer
SpatialTransition

ObjectiveSpatialQueries
ObjectiveNavigator

SpatialSubsystem : ISimSystem<SpatialGraphState, SpatialMomentCandidate, SpatialEvent>

SpatialCommandGateway
```

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
- LastTurnBack guard 可 Replay；
- State 不保存 adjacency、route、CoPresence 或 visibility cache 第二真理。

## 14.3 Commands

- duplicate CommandId 整批拒绝；
- conflict 结果不依赖输入顺序；
- rejected command 零事件、零状态改变、零 allocator 消费；
- Begin 只从 endpoint AtPlace 开始；
- TurnBack 保留 PassageId / TraversalId；
- 同 Entity / ModelTime 最多一次 TurnBack；
- Passage disable 不追溯 active traversal；
- Continue / Stop / Wait 不属于 World command。

## 14.4 Events与Replay

- Projector 是唯一写状态路径；
- event ModelTime 是运动 timestamp authority；
- reducer 不寻路、不调用 Game、不做 perception；
- derived events exact no-op；
- scratch state 与 formal fold 完全相等；
- Replay、split run 与 Fork deterministic。

## 14.5 Kernel

- 0 或 1 earliest candidate；
- overdue 是错误；
- stale 在事件前拒绝；
- mutation → arrival → relation → terminal；
- terminal event 唯一且最后；
- external command 在同刻 internal work 之后。

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
| IO-3 | Validation | unknown property、duplicate JSON property、numeric overflow、duplicate ID、unknown ref、Area cycle、多 root、坏 endpoint、Length≤0、坏 ViewLink、未知 schema/rules 全部在创建 State 前拒绝。 |
| CMD-1 | Command batch | 同一 commands 的任意排列产生逐字节相同 events/results/final state；duplicate CommandId 整批结构拒绝；同 Entity lifecycle conflict；同 Passage immediate same desired 的 leader/no-change/alias、different desired conflict；existing schedule alias 与 new schedule leader 映射精确；同 `(PassageId,Due)` 的不同 desired（包括与 existing schedule 相反）整组 MutationConflict；future desired 即使等于当前 effective value 仍分配并持久化；leader rejection 向 alias 传播；同批 disable+Begin 按 topology-first 使 Begin 拒绝；两个合法 Begin 而只剩一个 Traversal ordinal 时全部 allocator consumer 拒绝，不能让低 ID 抢占；rejection precedence 精确；所有 rejected/conflict 零 event、零 Revision、零 allocator。 |
| POS-1 | Location | Place 多 Actor 合法；Begin 原子形成唯一 Traversal；Remove 在途 Entity 原子清理 traversal 与 guard。 |
| QRY-1 | Settled read | structural state 在 ArrivalDue==Now 尚未 settle 时不能创建 SpatialReadContext；public query / Navigator 稳定拒绝；Resolve 后 AtPlace snapshot 可读。missing entity 与合法 empty 分离，结果 immutable 且 canonical。 |
| MOV-1 | Lazy movement | length10/speed1/T0 → Due10；T0..9 Offset 精确；T10 只允许 Arrival；Journal 无 progress event。 |
| MOV-2 | Ceil | length10/speed3 → Due4；T3 Offset9；T4 精确 endpoint。 |
| MOV-3 | TurnBack | T4 掉头：Offset4，同 Passage/Traversal，Generation+1，回 origin Due8，旧 Due10 candidate stale。 |
| MOV-4 | Liveness | 零进度 ReturnedToOrigin；同 Entity/T 的第二次 TurnBack 在事件、Revision、allocator 前拒绝。 |
| NAV-1 | Navigation | physical Passage 派生双向 arc；同价 tie 固定；disable 改走 detour；全断开 NoRoute。overflow 三向量：无关 overflow dead-end + disconnected goal → NoRoute；representable goal 与 overflow branch 并存 → RouteFound；唯一 goal route 总成本不可表示 → CostOverflow。 |
| VIS-1 | Visibility | lagoon→island 单向 candidate；reverse 无；ViewLink 不产生路径；Traversing observer 返回空。 |
| REL-1 | CoPresence | same Place pair canonical；Traversing 不共处；多人同刻 arrival 只按 final state 产 delta。 |
| EVT-1 | Projector | 每类 v1 payload 的 pre/post/revision 精确；`EventKind(listedId,1)` 两部分都匹配；MutationApplied 原子消费 schedule+override；derived exact no-op；scratch fold 与 formal reducer state 相等；unknown kind/version 拒绝；replay batch envelope 的 cause/modelTime/microstep/order/terminal 审计精确。 |
| MUT-1 | Topology | ordinary close 只阻止之后进入；active traversal 可继续/掉头/到达；reopen 恢复。另设同一 Passage scheduled disable 与 active arrival 都 Due=T：disable 先应用、arrival 仍完成、final AtPlace 且 Passage disabled、workCount=2，T 的后续 Begin 被拒绝。 |
| SIM-1 | Moment | Forecast 唯一 earliest；SourceId 来自稳定 manifest、CandidateId 等于 NextMomentOrdinal，Fork 后 identity 相同；mutation / arrival / relation total order 稳定；同刻关系只按 pre/final 产生；work count 精确；恰一个最后 MomentResolved；idle 无 candidate。 |
| CON-1 | Confluence | ArrivalDue 与外部 pulse 同 T：arrival 先完成，随后 TurnBack NotTraversing；注册顺序不改变结果。 |
| ARCH-1 | Boundary | Runtime production reference 只有 Kernel；无 Player/HUD/Knowledge 类型；直接绕过 SpatialCommandGateway 在 due boundary 提交 external Spatial input 的 integration harness 必须失败。 |
| RPL-1 | Replay | full run / split run / pure replay 的 State、Journal、next candidate 相等；Replay 不运行 Navigator 或 Mock。 |
| FORK-1 | Fork | T4 前 fork；Continue 分支保留旧 Due，TurnBack 分支 Due8；源 state 不变，两支各自 replay 相等。 |

P0 全部通过才算 Objective Graph Spatial vertical slice 成立。

## 15.3 Seeded RandomWalker

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

## 15.4 大鱼吃小鱼薄组合测试

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

## 15.5 建议测试目录

```text
tests/GraphSpatial.Tests/
    Definition/
    GraphIo/
    State/
    Commands/
    Events/
    Movement/
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
1. Strong IDs + distance/time arithmetic
2. Definition + validator + canonical Graph codec/hash
3. State + complete-state validator
4. Event kinds/payloads + single Projector
5. Commands/results/conflicts + SpatialTransition
6. Queries ─┬─ Objective Navigator
             └─ Objective visibility opportunities
7. Forecast/Resolve + SpatialMoment + coordinator integration
8. Event codec + Replay/Fork/split-run
9. Deterministic MockPlayer + RandomWalker + BigFish slice
```

第 6 波可以并行；第 7 波必须等待 Projector 与 Transition 稳定。MockPlayer 是最后的纵向验收，不能反向把 Player/HUD 类型带进 Spatial。

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
| StrictSupercover LOS | same-place / ViewLink opportunities |

本文不裁决就地重构还是并行 `GraphSpatial` project。执行前应根据现有 Grid Spatial 的复用成本另写 implementation plan；无论选择哪条路径，都不能让 Grid 与 Graph 同时成为 authority。

---

# 17. 结论

Graph Spatial World V1 的甜点位置是：

1. Area / Place / Passage 提供稳定语义拓扑；
2. Actor 在 Place 或正沿 Passage 运动；
3. Passage 可掉头但不可静止；
4. Length 与 ModelTime 分离，并以 lazy Offset 保持 DEVS-like Simulation；
5. Objective navigation、same-place 与 ViewLink 只回答客观空间关系；
6. Kernel 保存 mutation、arrival、关系变化与 Replay；
7. Graph Content 可以被严格读取、验证、规范写回与 hash；
8. MockPlayer 足以验证 World，无需先实现 Player cognition。

一句话总结：

> **先把客观 Graph 世界做成一个小而严密、可以长期跑动和重放的空间机器；Player 如何看地图、留下战争迷雾残影和形成信念，留给独立的 Player-facing 层。**
