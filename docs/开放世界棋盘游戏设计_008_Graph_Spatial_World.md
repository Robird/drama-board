# Design Note 008：Graph Spatial World
## ——建立在统一原子 Occurrence Kernel 上、面向 AI Player 的故事世界空间框架

**状态：修订草案；Kernel 对齐已冻结，空间竖切仍待实施**

**本次修订：2026-08-21**

**Kernel 权威基线：** [研发计划 006](./研发计划_006_统一原子Occurrence与LogicalInstant_Kernel重构计划.md)、[Design Note 003](./开放世界棋盘游戏设计_003_Forecast_Elapse_Decide_SimulationKernel.md) 与当前 `src/Kernel`。

**认知层边界：** Player HUD、战争迷雾、主观地图、Claims 与 LLM DecisionView 继续由 [Design Note 009](./开放世界棋盘游戏设计_009_Player空间HUD与战争迷雾_备忘.md) 研究，不进入本子系统。

---

# 0. 本次修订的决定

本文不再把 Spatial 设计成一个拥有自己 winner、同刻 phase、command gateway 与 audited replay 的“小 Kernel”。

当前 Kernel 的唯一时间路径已经是：

```text
committed HostWorld
→ 所有 IOccurrenceRule 全量 Forecast
→ Kernel 选择一个全局 winner
→ owning rule 生成一个完整 TransitionDraft
→ 一个 AppendBatch 原子提交
→ 全量 re-Forecast
```

因此 Graph Spatial 必须服从以下决定：

1. 每个 scheduled passage entry change、每个 arrival、每个 passage contact 都是独立 candidate；
2. Spatial 不先选自己的 earliest work，也不批量“结算这一 tick”；
3. 同一 `ModelTime` 的 Spatial、Game 与 Player candidates 一律由 Kernel 的 PRF 全局仲裁；
4. 一个 Spatial winner 只消费自己的局部成立条件；
5. facts 共享 batch 的一个 `LogicalInstant`，只按数组位置 fold；
6. Player action 在 owning rule 的 `PlanSelectedAsync` 内完成观察、决策、校验与领域规划，不走 external input；
7. 跨 Game + Spatial 的原子行动使用 composite `HostWorld`、一个 draft 与一次 `AppendBatch`；
8. Replay 只重建当前格式的完整 batches，不承担跨 build 法证审计；
9. 当前没有旧 Graph 数据，Definition、World 与 Journal 格式改变后直接重建；
10. 原型优先简单、灵活和可玩的竖切，不为不存在的审计、网络并发或极限容量消费者预建平台。
11. Passage 不再保存整条通路的 `Enabled`；EndpointA / EndpointB 各自保存“可否由本端进入 Passage”的方向许可；
12. 离开 Passage 抵达目标 Place 始终成功，已提交的 movement segment 不受后续入口关闭追溯影响；需要“走到门前才发现不能进城”时，把门前建成 Place。

旧草案中的下列机制被明确删除：

| 旧机制 | 新裁决 |
|---|---|
| `SpatialMoment`、`MomentResolved`、mutation/contact/arrival fixed phases | 删除；每个局部原因独立参加 Kernel 仲裁 |
| `PassageInteractionsSettledThrough` | 删除；contact 采用 pair + current-segment local progress |
| `SourceId / CandidateId / MomentOrdinal / InteractionOrdinal` | 合并为 Kernel `CandidateKey` 与领域 segment identity |
| public rational time/offset、exact contact 同 tick 排序 | 删除；精确数学只在 planner 内计算整数 `CandidateDue` |
| `SpatialCommandGateway`、internal-first、trusted receipt | 删除；Player 位于 owning rule 内 |
| command batch alias/conflict/capacity 平台 | 删除；保留单行动的纯 Spatial planner |
| `StateRevision / ExpectedStateRevision` | 删除；单未完成 Step 使用 frozen `HostWorld`，提交版本由 `WorldVersion` 表达 |
| derived CoPresence/CoTravel audit facts | 简化为当前关系查询；真实 Game 消费者自行拥有历史 |
| passage-wide `InitiallyEnabled / EffectiveEnabled` | 简化为 EndpointA / EndpointB 两个 entry bit；arrival 永远允许离开 Passage |
| canonical JSON writer/hash/version matrix | 延期到真实内容管线需要时 |
| 102,273-event Moment、microstep reserve、固定容量矩阵 | 删除；使用小 transition、checked arithmetic 与现有 Kernel/Journal 边界 |

---

# 1. 产品目标与 authority 边界

Graph Spatial World 是故事世界的客观空间 authority。它回答：

- 哪些语义地点存在；
- 地点之间有哪些可区分的通路；
- Actor 现在位于地点，还是正在某条 Passage 上旅行；
- 旅行何时抵达；
- 哪些 Actor 客观同地、同路或轨迹相交；
- 当前客观图上有哪些可走出口与路线。

一句话边界：

> **Spatial 决定“在哪里、怎样相连、怎样运动以及客观上发生了什么空间交会”；Game / Perception 决定 Actor 为何行动、实际察觉什么以及如何回应。**

## 1.1 面向 AI Player 的优化目标

AI Player 不应管理 tile、角度或逐步路径。它应能用紧凑、稳定的语义表达：

```text
去旧港
走山路绕过关卡
继续赶路
途中遇见 Alice 后回头
```

底层空间框架必须替它完成：

- 稳定 Place / Passage identity；
- 出口与 ETA；
- 确定性路线计算；
- lazy travel progress；
- arrival 与途中 contact Forecast；
- 对动作的客观合法性校验；
- 与 Game 条件的一次原子提交。

AI-facing view 只包含 Perception 合法披露的局部材料。完整 Graph、隐藏 Actor、Kernel contender、PRF rank 与尚未发生的 future contact 都不是 Player 信息。

## 1.2 Spatial 拥有

- immutable `GraphDefinition`；
- stable `PlaceId / PassageId`；
- Passage 的 Length、endpoints 与两个方向当前是否可进入；
- Entity 的客观 location 与 current movement generation；
- active traversal 的 piecewise-constant motion law；
- scheduled passage changes；
- current-segment pair 的 contact consumption；
- objective queries、relations 与 navigation；
- Spatial facts、fold 与 invariant validation；
- mutation、arrival、contact 的纯 Forecast / Plan 逻辑。

## 1.3 Spatial 不拥有

- PlayerId、Memory、Belief、Known/Confirmed Graph、Fog-of-War；
- Prompt、HUD、DecisionRequest 或 LLM DTO；
- Quest、Inventory、Faction、ticket、relationship interpretation；
- Actor 为什么获得某个 `SpeedSnapshot`；
- Player 的长期 `TravelGoal` 或“为什么去那里”；
- renderer、Grid、NavMesh、动画或 Presentation Time；
- Kernel winner selection、WorldVersion、Journal publication；
- contact 后必须怎样回应的 Game policy。

高层 `TravelTo(goal)` 由 Game-owned Activity / controller 保存。它可以调用 Spatial Navigator 选择下一 Passage，但不能把 Player goal 变成 Spatial authority。

## 1.4 与当前 `src/Spatial` 的关系

当前 Grid Spatial 是已实现的消费者证据，尤其证明了：

- directed Portal 不生成隐式反向 edge；
- independent mutation / arrival candidates；
- lazy current-leg travel；
- pure command planning；
- Game + Spatial facts 同 batch；
- reducer/replay 的基本形状。

它不是 Graph schema 的兼容义务。实施 Graph Spatial 时可以替换现有 Grid 类型；运行时不得让 Grid 与 Graph 同时成为 objective location authority。

Grid Portal 当前会在 leg Due 重查 passability，并在失败时把 Entity 留在 source Cell；这是“旅程期间 Entity 仍投影在源 Cell”的旧 Grid 语义。Graph 已把 Entity 客观放在 Passage 中，且本设计选择“离开 Passage 必然成功”，因此不继承该到达时重查行为。

---

# 2. 最小客观空间模型

## 2.1 Stable identifiers

V1 使用强类型、非空、Ordinal 比较的字符串 ID：

```text
PlaceId
PassageId
EntityId        // 可由 Host 的稳定 Entity identity 适配
```

ID 与 display name、localized text 分离。Definition 内每种 ID 唯一；所有查询与 route tie-break 使用同一 Ordinal 语义。

不为 traversal、contact、schedule、command 或 transition 再分配全局 ordinal。它们的当前身份由稳定内容 ID、EntityId、movement generation 与局部值组成。

## 2.2 Graph Definition

```text
GraphDefinition
    Places[]
        PlaceId

    Passages[]
        PassageId
        EndpointA: PlaceId
        EndpointB: PlaceId
        Length: Int64                 // > 0, abstract distance units
        InitialEntryAccess
            EnterableFromA: bool      // 允许创建 A → B segment
            EnterableFromB: bool      // 允许创建 B → A segment
```

约束：

- Passage 两个 endpoint 必须存在且不同；
- EndpointA / EndpointB 顺序定义权威 offset 轴；A 为 0，B 为 Length；
- 相同 endpoints 可以有多条 Passage，例如 ferry 与 bridge；
- Passage crossing 不代表相连，只有共享 Place 才相连；
- `EnterableFromA` 与 `EnterableFromB` 独立，且是方向许可的唯一 authority；
- V1 不运行时创建或删除 Place / Passage。

精确方向法则：

```text
CanCreateSegment(A → B) = EffectiveEntryAccess.EnterableFromA
CanCreateSegment(B → A) = EffectiveEntryAccess.EnterableFromB
```

它适用于每一个新 movement segment，而不只适用于 Actor 从 Place 首次启程。A→B 途中选择 Reverse 会结束旧 segment 并创建 B→A segment，因此也必须检查 `EnterableFromB`。这使两个 bit 足以表达：

| Initial entry access | 客观含义 |
|---|---|
| `A=true, B=true` | 双向 Passage |
| `A=true, B=false` | 只允许 A→B；可表达悬崖、顺流或单向 portal |
| `A=false, B=true` | 只允许 B→A |
| `A=false, B=false` | 两个方向都不能创建新 segment |

`InitiallyEnabled`、额外的 `AllowedAtoB / AllowedBtoA` 与 whole-passage master switch 均不再存在。上述四种只是 authoring preset，不是附加状态。

Area hierarchy 与 directed ViewLink 不进入第一竖切。重启条件见 §9：真实 AI view 需要稳定区域层级或远距 Place visibility 时，再以薄 content relation 加入，不提前建设 property graph。

## 2.3 Place 与 Passage 的粒度

Place 是可以稳定引用、停留并产生多回合互动的 locality。以下位置必须建成 Place：

- 可以等待、调查、休息或战斗；
- 可以会面、分岔或成为 travel goal；
- 需要稳定名字或规则触发；
- Player 可能在此重新考虑长期行动。

Passage 是两个 Place 之间的有限一维旅行空间。它允许：

- Actor 正在途中；
- 同路与共行查询；
- 非阻挡的迎面相遇与超过；
- 在整数模型时间边界反向运动。

Passage 不允许长期静止。若故事需要“在桥中央等候/调查/持续战斗”，该位置应升级为 Place，而不是添加任意 Offset site callback。

Endpoint entry access 也遵守这一粒度法则。离开 Passage、跨入目标 Place 始终成功；若故事要求“旅行一段时间后在城门前被拦下、排队、交涉或寻找密道”，应建模为：

```text
野外 Place
→ 接近道路 Passage
→ 城门外 Place
→ 城门 Passage
→ 城内 Place
```

关闭“城门外→城内”方向后，Actor 仍能完成接近道路并停在有故事身份的城门外 Place，而不需要 `BlockedAtEndpoint`、Passage 内等待或自动退出 candidate。

## 2.4 唯一距离与时间法则

```text
PassageLength
    Int64 Units                     // > 0

PassageOffset
    Int64 Units                     // 0..Length at integer ModelTime

SpeedSnapshot
    Int64 UnitsPerMillisecond       // > 0
```

`SpeedSnapshot` 是 Game 已裁决的客观结果。Spatial 不知道它来自步行、载具、地形、伤势还是魔法。

所有 traversal、route ETA 与 contact 使用同一法则：

```text
TravelDuration(distance, speed)
    q = distance / speed
    r = distance % speed
    return ModelDuration(q + (r == 0 ? 0 : 1))
```

中间算术使用 widened checked representation。正距离始终至少需要 1ms。

不再保存第二套 `BaseDuration` 或从量化 Due 反推速度。这样 arrival、turn-back、route cost 与 relative motion 共享一个数值 authority。

## 2.5 Entity 与 traversal

一个 Entity 直接内嵌自己的 location，不建立独立 Traversal table。第一竖切只保存 endpoint-to-endpoint traversal：

```text
SpatialEntity
    EntityId
    MovementGeneration              // >= 0; motion law 每次替换时 +1
    Location
        AtPlace(PlaceId)
        Traversing(
            PassageId,
            FromPlaceId,
            ToPlaceId,
            StartedAt,
            SpeedSnapshot,
            ArrivalDue)
```

`MovementGeneration` 同时区分同一 Entity 的 successive segments，并参与 arrival/contact CandidateKey。它替代旧草案中的 `TraversalId + Generation + StateRevision`。

active traversal 必须满足：

- From / To 是该 Passage 两个不同的 endpoint；
- StartedAt 早于 ArrivalDue；
- speed 正；
- ArrivalDue 精确等于 `StartedAt + TravelDuration(Passage.Length, speed)`。

创建 `TraversalStarted` 时，planner 与 reducer 还必须验证 From 所定义方向的 effective entry access。该检查只证明 segment 在创建时合法，不成为 state 上持续成立的不变量：access 后续可能关闭，而 active traversal 仍继续。

整数时刻位置：

```text
OffsetAt(segment, at)
    require StartedAt <= at <= ArrivalDue

    if at == ArrivalDue:
        return ToOffset

    elapsed = at - StartedAt
    advanced = elapsed.Ticks * SpeedSnapshot
    return FromPlaceId == EndpointA
        ? advanced
        : PassageLength - advanced
```

在 `at < ArrivalDue` 时，ceil 法则保证结果不会越过 endpoint。Journal 不产生 progress fact。

重要的因果边界：

> `at == ArrivalDue` 但 arrival occurrence 尚未提交时，Entity 仍是 `Traversing`，query 只把 offset 显示在 endpoint boundary；只有 `TraversalArrived` fact 才使它成为 `AtPlace`。

这使合法的同 `ModelTime` committed prefix 始终可查询，不需要“Spatial 已把这一 tick 全部 settle”的 barrier。

Arrival 不重新检查 entry access。Actor 已经合法进入 Passage，目标端也没有独立的 exit gate；因此 selected arrival 始终把它变为 `AtPlace(ToPlaceId)`。运行时关闭只阻止此后创建同方向的新 segment，不能把在途 Actor 卡在端点、弹回起点或删除。

第二条 contact 竖切只有在真实 AI encounter 需要 Reverse 时，才把 traversal 泛化为：

```text
AnchoredTraversal(
    PassageId,
    AnchorOffset,
    AnchorTime,
    TargetEndpoint,
    SpeedSnapshot,
    ArrivalDue)
```

届时 start 只是 endpoint anchor 的特例；Reverse 在整数 committed time 物化当前 offset 后替换 anchor、target 与 movement generation，并检查反方向当前 entry access。当前没有旧 Graph 数据，因此第一竖切不预留字段，也不承担 schema migration。

## 2.6 Dynamic state

第一竖切：

```text
GraphSpatialState
    Entities[]
    PassageEntryAccessOverrides[]   // sparse；每个 Passage 保存完整两位结果
        PassageId, EnterableFromA, EnterableFromB
    ScheduledPassageEntryChanges[]
        PassageId, Due, Patch

PassageEntryPatch                  // 至少一个字段非 null
    EnterableFromA: bool?
    EnterableFromB: bool?
```

effective access 是 `override ?? Definition.InitialEntryAccess`。若完整结果等于 Definition 初值，reducer 删除 override；runtime state 不保存每 bit 一份 authority。

schedule 保存 patch 而不是 scheduling-time 的完整快照：到 Due 时只覆盖明确指定的方向，不回滚期间对另一方向的独立修改。同一 `(PassageId, Due)` 最多一项，调用方必须在规划时把同一原因的两个方向合成一个 patch；这使“在 T 把整条 Passage 双向关闭”成为一个 candidate 和一个原子 fact，而不是两个可被其它 winner 穿插的局部原因。

第二条 contact 竖切加入：

```text
ConsumedContacts[]
    PassageId
    EntityA, MovementGenerationA
    EntityB, MovementGenerationB
```

`ConsumedContacts` 只保存 current active segment pairs，不是历史账本：

- pair 以 canonical EntityId 顺序保存；
- 任一相关 segment 被 arrival、Slice 2 reverse、remove 或新的 movement law 替换时，旧 key 被清理；
- 同一对 constant-linear segments 最多只有一个严格内部交点；
- CoTravel 的相同 worldline 不产生 contact。

第一竖切不得提前加入 contact 字段或占位类型；它与真实 contact + AI encounter consumer 在第二竖切一起交付。

---

# 3. 与统一 Occurrence Kernel 的唯一集成

## 3.1 Host adapter

生产组合使用当前 Kernel 形状：

```text
HostWorld
    Game
    Spatial: GraphSpatialState

HostCandidate
    SpatialMutation(...)
    SpatialArrival(...)
    SpatialContact(...)
    Game(...)
    DecisionPoint(...)

HostFact
    Spatial(GraphSpatialFact)
    Game(...)
```

Host-owned rule 使用当前接口：

```text
IOccurrenceRule<HostWorld, HostCandidate, HostFact>
```

`SpatialForecast / SpatialPlanner` 始终是纯领域函数，Spatial library 不依赖 Game、Player 或 Host implementation。Slice 1 的 mutation / arrival rule 可以机械委托并包装 Spatial 候选与事实；Slice 2 的 contact candidate 则只由一个 composite Host encounter rule 拥有。该 rule 调用纯 Spatial contact Forecast / Plan，并在 selected Plan 中追加 Game-owned encounter facts。两条 rule 不得同时 Forecast 同一个 contact key，否则当前 Kernel 会以 duplicate `CandidateKey` 拒绝本轮。

## 3.2 Forecast 必须枚举全部局部 candidates

```text
Forecast(spatial):
    for each scheduled passage entry change:
        yield PassageEntryChangeCandidate

    for each traversing entity:
        yield ArrivalCandidate

    // Slice 2
    for each unordered current-segment pair on the same Passage:
        if pair has an unconsumed strict interior crossing:
            yield PassageContactCandidate
```

Spatial 不得：

- 只返回自己最早的一项；
- 把同 Due 的 candidates 合成 Moment；
- 按 mutation/contact/arrival family 预排序；
- 读取或猜测 Kernel PRF rank；
- 在 Forecast 中修改 state、消费 RNG 或做 I/O。

## 3.3 CandidateKey

规范 key 至少编码：

```text
Passage entry change
    ["graph-spatial/entry-change",
     PassageId, Due,
     canonical patch mask + desired values]

Arrival
    ["graph-spatial/arrival",
     EntityId, MovementGeneration,
     PassageId, FromPlaceId, ToPlaceId,
     StartedAt, SpeedSnapshot, ArrivalDue]

Contact
    ["graph-spatial/contact",
     PassageId,
     canonical(EntityA, MovementGenerationA),
     canonical(EntityB, MovementGenerationB)]
```

Slice 2 泛化为 anchored traversal 后，arrival key 同步编码完整 current anchor/target motion fields；同一语义不同时保存 endpoint 与 anchor 两套候选身份。

Key 在 Player 调用前只从 committed world 推导。Candidate 不持久化；提交后的 Journal cause 只保存 Kernel 的 `CandidateKey`。

## 3.4 Entry access mutation 与 arrival

scheduled change winner 返回一个原子 fact：

```text
ScheduledPassageEntryChangeApplied(PassageId, Due)
```

Reducer 同时：

- 删除这一项 exact schedule；
- 把 non-null patch fields 应用于 Due 时的 effective entry access；
- 写入完整两位 sparse override，或在结果等于 Definition 初值时删除 override。

即使 patch 的 desired value 已经生效，也必须消费自己的 schedule，避免同 key 永久复发。它不能消费同 tick 的其它 schedule。

arrival winner 返回：

```text
TraversalArrived(EntityId, ExpectedMovementGeneration)
```

Reducer 验证当前 traversal identity 与 batch `LogicalInstant.ModelTime == ArrivalDue`，然后无条件把 Entity 变为 target `AtPlace`，并清理涉及旧 segment 的 contact keys。它不读取当前 entry access：arrival 是已获准 segment 的完成，不是新 segment。

## 3.5 Passage contact：目标法则与第二竖切

途中 contact 是本设计相对于“地点间计时跳转”的关键故事能力，但它不阻塞第一条 Place—Passage—Arrival 竖切。

对两条同 Passage constant-linear segments，planner 在内部使用 widened integer / rational 运算求严格交点。它只在双方 motion law 的共同有效窗口求交：

```text
t0 = max(A.AnchorTime, B.AnchorTime)

require both segments exist and are physically active at t0
ContactTime = exact intersection of the two current worldlines

require ContactTime > t0
require ContactTime lies before both exact physical exits

CandidateDue = ceil(ContactTime to 1ms)
```

只保留两个最小 kind：

```text
HeadOnMeeting
Overtake
```

约束：

- contact 严格位于 Passage 内部；endpoint 交会由 arrival 后的 same-place relation 表达；
- 相对于共同窗口起点 `t0` 的 `tau == 0` overlap 不报 contact；
- 相同 worldline 的 CoTravel 不报 contact；
- `CandidateDue` 不得早于当前 committed `ModelTime`；Kernel 会拒绝 past-due candidate；
- 已进入同一整数 tick 后，不得用 `ContactTime > current ModelTime` 过滤 peers：exact time 已过去但 `CandidateDue == current ModelTime` 的未消费 contact 仍须保留；
- exact fraction 不进入 Candidate、World、Fact、Journal、query 或 Player view；
- 同 tick contact 的顺序只由 Kernel `(Due, PRF rank, CandidateKey)` 决定；
- contact 与 arrival/change/DecisionPoint 没有 fixed priority。

entry access change 不删除、不重锚 active segment，也不改变其 speed、arrival 或内部 contact。它只改变此后能否创建某一方向的新 segment。

winner 返回：

```text
PassageContactOccurred(
    PassageId,
    EntityA, MovementGenerationA,
    EntityB, MovementGenerationB,
    Kind)
```

Reducer 在加入 key 前复用同一个 pair math，验证 current segment/generation 精确匹配、key 尚未消费、存在唯一严格内部有效交点、`ceil(ContactTime) == batch.LogicalInstant.ModelTime` 且 Kind 匹配。验证成功后，它只把自己的 current-segment pair key 加入 `ConsumedContacts`；不重锚、不调速、不移动参与者。这是 fact-local 领域真实性校验，不是跨 build audited replay。这样：

- 同一 contact 不会重复 Forecast；
- A-B 提交不会吞掉同 tick 的 C-D；
- A-B 提交也不会通过虚假 rebase 吞掉 A-C；
- ordinary Actor 仍然非阻挡并继续原 motion。

reference Forecast 可以是 `O(Σ n_passage²)`。在真实 profiler 证明瓶颈前不建 kinetic index、pair cache 或容量平台。

### Contact 后的 AI response

Spatial 不保证 contact 后紧接一个 Player phase。最小 Game 组合是：

```text
selected Host contact rule
→ Spatial(PassageContactOccurred)
→ Game(EncounterOpened(ContactKey, participants))
→ same TransitionDraft / AppendBatch
```

`EncounterOpened` 是改变后续 affordance 的 Game state，不是 Journal receipt。它以 exact domain `ContactKey` 为 identity，允许同一 Actor 同 tick 打开多个独立 encounter。下一轮普通 DecisionPoint candidate 可以让 AI 选择 Continue、Reverse 或其它 Game action；它仍与同 tick 的其它 causes 参加 Kernel 仲裁。

每个合法 response 都必须消费 exact pending encounter 并产生非空 draft：

```text
Continue
    → Game(EncounterResolved(ContactKey, Continue))

Reverse
    → Game(EncounterResolved(ContactKey, Reverse))
    → Spatial(TraversalReversed)
```

response candidate key 包含 encounter identity；只有已经 `EncounterResolved` 的 encounter 才停止 Forecast。若 arrival、remove 或其它 occurrence 先改变了空间条件，仍 open 的 encounter 必须继续产生一个可关闭它的 cleanup/response candidate，并提交：

```text
Game(EncounterResolved(ContactKey, WorldChanged | Expired))
```

也可以由使它失效的 composite occurrence 在同一 draft 中关闭。它不能因 affordance 过时而从 Forecast 静默消失并永久残留在 Game state。这里的 world-changed pending encounter 与 stale Player proposal 不同：后者零提交并可重问，前者必须最终留下 Game-owned 权威进展。Continue 因而不是空 draft，也不会让同一 DecisionPoint 永久复发。

V1 不提供 `MatchTraversalAtContact`、Journal receipt、event address/hash 或“必须立即回应”的 gateway。若一个故事必须让策略在 selected contact 的同一 `PlanSelectedAsync` 内原子回应，可以由具体 Host encounter rule 完成，但不提升为 Spatial 通用协议。

## 3.6 同 tick 语义

例如 T 同时有：

```text
bridge A-side entry close
Alice arrival
Bob/Carol contact
Dave DecisionPoint
```

四者都是普通 candidates。Kernel 选择一个，提交后从新 world 全量 Forecast。没有“Spatial internal first”，也没有“contact before arrival”。不同 WorldSeed 可以得到不同但可重放的因果顺序。

任何 public query 都读取当前 committed prefix。它不得因为还存在 `Due == current ModelTime` 的 Spatial candidate 而拒绝；否则就会把旧的整 tick barrier 偷渡回来。

---

# 4. Facts、纯规划与 reducer

## 4.1 最小 fact union

目标设计的 state-changing Spatial facts：

```text
EntityPlaced(EntityId, PlaceId)
EntityRemoved(EntityId)

TraversalStarted(
    EntityId, PassageId, FromPlaceId,
    SpeedSnapshot)

TraversalArrived(
    EntityId, ExpectedMovementGeneration)

PassageEntryAccessChanged(PassageId, ResultAccess)
PassageEntryChangeScheduled(PassageId, Due, Patch)
ScheduledPassageEntryChangeApplied(PassageId, Due)

// Slice 2：与 anchored traversal + 真实 encounter consumer 一起加入
TraversalReversed(
    EntityId, ExpectedMovementGeneration)

PassageContactOccurred(...)
```

Payload 只保存 reducer 不能从 pre-state 与 batch instant 唯一推导的输入。没有 `EventKind` 跨版本矩阵、expected→result 审计镜像、terminal fact 或 derived no-op relation facts。

所有 state change 只经过同一个 pure reducer。owning rule 可以先用 reducer scratch-fold draft 并验证 final `HostWorld`；Kernel 会再次 fold/validate 并原子发布整个 batch。

## 4.2 Pure Spatial planner

Spatial 提供给可信 Game rule 的是同步纯 planner，不是 command bus：

```text
TryPlaceEntity(...)
TryRemoveEntity(...)
TryStartTraversal(entity, passage, speed, at)
TrySetPassageEntryAccess(passage, patch)
TrySchedulePassageEntryChange(passage, due, patch)

    -> Accepted(facts) | Rejected(reason)
```

planner：

- 不排队；
- 不提交；
- 不创建 CandidateKey；
- 不读取 Journal；
- 不处理 command batch alias 或 caller input order；
- 不拥有 Player action economy；
- 只验证 objective Spatial invariants。

`TryStartTraversal` 与 reducer 共用 §2.2 的 direction predicate。一个 actor-specific ticket、阵营许可或守卫放行仍由 Game 检查，不能写入全局 entry access。

Slice 2 与 anchored traversal 一起加入 `TryReverseTraversal(entity, at)`：它在 `at < ArrivalDue` 物化当前 offset，以相同 speed、相反 endpoint 与 `MovementGeneration + 1` 建立新 segment。Reverse 是新的方向承诺，必须检查反方向 effective entry access；例如 A→B 的 Reverse 检查 `EnterableFromB`。零进度或 boundary reversal 可以稳定拒绝。AdjustPace、Stop、WaitOnPassage 与 MatchAtContact 继续延期。

## 4.3 非法 proposal 与世界内失败

owning Game rule 区分：

- malformed、stale、越权或不在当前 affordance 的 proposal：同一未完成 Step 内重问或失败，零提交；
- 合法行动在世界里失败：返回描述结果的非空 Game facts；
- Spatial objective precondition 不成立且尚未形成世界内行动：planner rejection，零提交。

Spatial 不定义通用 `RejectedCommand` Journal history。

## 4.4 Cross-domain atomicity

例如 AI 选择持票登船：

```text
DecisionPointRule.PlanSelectedAsync(frozen HostWorld)
    → build legal observation + affordances
    → await AI strategy
    → validate selected affordance
    → SpatialPlanner.TryStartTraversal(...)
    → return TransitionDraft([
          TicketConsumed,
          Spatial(TraversalStarted)
      ])
```

若 ticket 或 traversal 任一条件失败，draft 在发布前失败，Game、Spatial、Journal 与 `WorldVersion` 全部不变。无需 transaction coordinator、2PC 或 compensating event。

---

# 5. Objective queries、navigation 与 Player projection

## 5.1 Query 读取 committed causal state

最小 objective query：

```text
GetLocation(entityId, at)
GetPassageEntryAccess(passageId)
GetExits(placeId, speedSnapshot)
GetCoLocatedEntities(entityId)
GetSamePassageRelations(entityId, at)
GetCoTravelingEntities(entityId, at)
FindRoute(startPlaceId, goalPlaceId, speedSnapshot)
```

规则：

- `at` 必须来自当前 committed `ModelTime` 或 selected winner Due；
- 所有集合按稳定 ID 排序并返回 immutable value；
- unknown reference 与合法 empty result 分开；
- query 不扫描“还有什么 candidate 没 settle”；
- query 不 Forecast future contact；
- `AtPlace` 才属于 same-place relation；endpoint-boundary `Traversing` 仍不属于 Place。

`GetExits` 返回每条 incident Passage 的：

```text
PassageId
DestinationPlaceId
EffectiveEntryAllowed             // 从当前 Place 创建该方向 segment
ExpectedDuration at supplied SpeedSnapshot
```

`GetExits` 返回所有 incident Passages 的 objective descriptor，包括当前关闭的方向；Game / Perception 可以让 AI 看见“城门已关”，但只为 `EffectiveEntryAllowed == true` 的方向创建 `TakeExit` affordance。平行 Passage 因而始终可区分。

## 5.2 Current relations，不保存第二份 authority

CoLocation 从 `AtPlace` 直接推导。

CoTravel 是客观的“相同 future worldline”，不是朋友、party 或 escort：

```text
same Passage
same current offset at `at`
same target endpoint
same SpeedSnapshot
same affine line / ArrivalDue
```

只有瞬时同 offset 但方向或 future worldline 不同，不构成 CoTravel。

Spatial 不保存 CoLocation / CoTravel cache，也不提交 Started/Ended audit facts。若 Game 需要“第一次同行”“关系开始”或 Observation 历史，它在自己的 state 中消费当前关系与 Spatial causes。

## 5.3 Objective Navigator

V1 使用纯 Dijkstra：

```text
FindRoute(start, goal, speed)
    edge cost = TravelDuration(Passage.Length, speed)
    A → B edge exists iff effective EnterableFromA
    B → A edge exists iff effective EnterableFromB
```

Result 至少区分：

```text
RouteFound(TotalDuration, Legs[])
AlreadyAtGoal
NoRoute
UnknownStart
UnknownGoal
InvalidSpeed
CostOverflow
```

每个 leg 携带 `(PassageId, FromPlaceId, ToPlaceId)`。同 cost route 使用完整 leg sequence 的 Ordinal 字典序 tie-break；结果不依赖 Definition 集合插入顺序。

Navigator 不写 state、不保存 route cache、不被 reducer 调用。

## 5.4 AI-safe projection

Spatial query 是 objective，不是 Player-safe。owning Game / Perception rule 负责把合法子集投影成紧凑 view，例如：

```text
SpatialObservation
    CurrentLocation
    CoLocatedActors[]
    KnownExits[]
        AffordanceId
        DestinationHandle
        ExpectedDuration
    CurrentTravel?
        FromHandle
        TowardHandle
        ETA
    RecentEncounter?
```

Player 提交的是 semantic intent：

```text
TakeExit(affordanceId)
TravelTo(destinationHandle)         // Game-owned 长期目标，不直接指定 Passage
ContinueCurrentIntent
ReverseCurrentTraversal
```

对 immediate traversal，Game 用 frozen `AffordanceId` 精确映射唯一 objective Passage，因而 ferry 与 bridge 即使同终点也不会混淆。`DestinationHandle` 只用于显示或创建 Game-owned `TravelTo` 长期目标；Navigator / controller 再为它选择下一 Passage。Player 不直接提交 Spatial fact、CandidateKey、offset 或 hidden PassageId。

以下不得进入 Player observation：

- full objective Graph；
- hidden Place / Passage / Entity；
- complete same-Passage occupancy；
- future contact candidates；
- scheduler rank、WorldSeed 或 contender set；
- 其它 Actor 的 route、goal 或 private state。

---

# 6. Dynamic topology、Definition I/O 与 Replay

## 6.1 Passage entry access

```text
EffectiveEntryAccess = sparse full-value override
                    ?? Definition.InitialEntryAccess

CanCreateSegment(A → B) = EffectiveEntryAccess.EnterableFromA
CanCreateSegment(B → A) = EffectiveEntryAccess.EnterableFromB
```

entry access 只决定能否创建一个方向的新 movement segment：

- 从 Place 开始 traversal 时检查该 Place 所在 endpoint 的 bit；
- 途中 Reverse 创建反向 segment，因此检查反方向的 bit；
- Continue 不创建 segment，不重新检查；
- 已经提交的 segment 不因后续关闭而取消、改速或失效；
- arrival 始终允许 Actor 离开 Passage 进入目标 Place。

例如一条 `Outside(A) — City(B)` Passage：

- `EnterableFromB=false` 表达“不能从 City 经此路离城”；
- `EnterableFromA=false` 表达“不能从 Outside 经此路进城”；
- 只修改 main gate Passage 而不修改 secret Passage，就表达“城门走不通但密道仍可走”。

Spatial 不再保存 `PlaceSealed / PlaceCanEnter / PlaceCanLeave` 第二层 authority。一次“封城”若必须原子改变多条公共 Passage，由一个 Game / Host occurrence 在同一 draft 中按稳定 `(PassageId)` 顺序追加多条 `PassageEntryAccessChanged` facts；未列入的密道保持原状。V1 不运行时新增 Passage，因此该 incident set 在规划时是完整的。

若关闭必须拦住已经在途的 Actor，使用 §2.3 的城门外 Place。V1 明确不引入 `ExitAllowed`、`BlockedAtEndpoint`、reopen candidate 或任意 Passage stop state。

每个 scheduled change：

- Due 必须严格晚于创建它的 occurrence time；
- patch 至少指定 `EnterableFromA / EnterableFromB` 之一；
- `(PassageId, Due)` 最多一个 patch；
- 同一 Passage 两个方向属于同一原子原因时放入一个 patch；
- unspecified bit 在 Due 保留当时 current value；
- 即使 desired values 与当前 effective values 相同，也要保存到 Due 并由自己的 winner 消费；
- 不需要 `MutationId` allocator。

跨多个 Passages 的原子未来变化属于 owning Game / Host rule，不把多条独立 Spatial schedules 假装成一个原因，也不为此引入 schedule group identity。

## 6.2 Definition loading

V1 只需要普通内容适配器：

```text
source DTO
→ validate IDs / references / positive length / initialized entry access
→ sort by stable ID
→ immutable GraphDefinition
```

Runtime 不读文件，reducer/Forecast 不依赖 JSON。

本轮不冻结：

- exact canonical JSON bytes；
- writer/write-back；
- `ContentHash / DefinitionStamp / RulesVersion` matrix；
- 旧 schema detection 或 migration。

当真实地图编辑器、外部分发内容或需保留的持久 run 首次要求 definition binding 时，再加入最小 hash/format contract。此前 definition 改变就重建开发 run。

## 6.3 Replay 与 Fork

Spatial facts 作为 `HostFact` 的精确 union case 存在于当前 Journal batch。普通 Replay：

```text
read complete batch
→ fold facts in array order at the shared LogicalInstant
→ validate final HostWorld
→ expose next committed boundary
```

Replay 不 Forecast、不调用 AI、不重新算 route/contact winner，也不查 receipt。Fork 只发生在完整 batch boundary，并遵守当前 Kernel 的 new-lineage 语义。

同 build 的 `SchedulerConformance` 可以重算 cause winner；这不是 Spatial 自己的 event audit framework。

---

# 7. 可执行不变量与验收

## 7.1 核心不变量

### Definition

- stable typed IDs 唯一；
- Passage endpoints 合法且不同；
- Length 正；
- EndpointA/B order 保留；
- 每端 entry access 初始化，且不另存 whole-passage 或 directional authority；
- immutable collections 使用 canonical order。

### State

- Entity 恰好 `AtPlace | Traversing`；
- 一个 Entity 至多一条 active segment；
- movement generation 单调且只标识 motion law；
- traversal math 与 ArrivalDue 完整一致；
- sparse entry-access override 保存完整两位结果且 canonical；
- active segment 可以属于当前已经关闭的方向，因为合法性在 segment 创建时裁决；
- schedule key 唯一；
- Slice 2 consumed contact 只引用 current segments；
- 不保存 adjacency、route、relation或visibility cache第二真理。

### Kernel integration

- Spatial Forecast 枚举全部 candidates；
- 一个 winner 只消费自己的局部条件；
- 同 tick 没有 Spatial phase 或 local winner；
- contact 的 exact fraction 不参与排序；
- committed prefix 在 `Due == Now` 时仍可查询；
- facts 共享 batch LogicalInstant；
- rejected proposal 零提交；
- Player projection不泄露 simulator foresight。

## 7.2 最小验收矩阵

| ID | 必须证明 |
|---|---|
| DEF-1 | Definition 重排不改变 graph、exit order或route；parallel Passage保持可区分；坏 endpoint/非正 Length拒绝；两端 entry 初值保留。 |
| AI-1 | AI 只读取合法的 semantic location、known exits与ETA；选择一个 affordance后，Game把它映射为 objective traversal；hidden Graph/candidate/rank不泄露。 |
| ATM-1 | `TicketConsumed + TraversalStarted` 同batch成功；Spatial planner拒绝时 Game/Spatial/Journal/WorldVersion全不变。 |
| MOV-1 | length10/speed3：T0 start、Due T4、T1..T3 lazy offset、无progress facts；arrival fact前仍Traversing，提交后才AtPlace。 |
| QRY-1 | Kernel已位于T，而另一arrival `Due==T` 尚未赢时，query仍合法并返回endpoint-boundary Traversing，不要求整tick settled。 |
| ORD-1 | 两个arrival、一个passage entry change与DecisionPoint同T时各自独立；Kernel每次只提交一个，first winner不吞peers，注册/枚举顺序不改变结果。 |
| DIR-1 | `A=true/B=false` 时 A→B 可 start、B→A 不可 start；Slice 2 的 A→B 途中 Reverse 因需创建 B→A segment 而被拒绝。 |
| DIR-2 | entry close 与 start/reverse 同tick由Kernel仲裁：新segment先赢则获准完成，close先赢则不再合法；arrival从不因close失败。 |
| GATE-1 | `Outside → GateFront → City` 中关闭 GateFront→City方向后，Actor仍可完成前段抵达GateFront，但不能创建入城segment；反向bit可独立表达不能离城。 |
| GATE-2 | main gate关闭但secret Passage保持开放；一个Host winner可在同batch改变多条公共Passage而不修改密道。 |
| MUT-1 | idempotent scheduled entry patch仍消费自己；unspecified方向保留Due时current值；同tick另一Passage schedule继续存在；active traversal不被破坏。 |
| NAV-1 | Navigator只枚举origin endpoint当前允许的方向；equal-cost route使用完整leg key稳定tie-break；NoRoute、overflow与unknown input明确区分。 |
| REL-1 | same-place只包含已提交AtPlace；同offset但不同future worldline不构成CoTravel。 |
| CNT-1 | Slice 2：length10，A+4/B-3在 `10/7` 交会，CandidateDue=T2；Fact/World/Journal不保存fraction。 |
| CNT-2 | Slice 2：A-B与A-C同T，提交一对后另一对仍Forecast；已提交pair不复发；C-D也不被whole-tick消费。 |
| CNT-3 | Slice 2：contact、arrival、mutation同T仅由Kernel PRF仲裁；无contact-first；endpoint与`tau=0`不伪报。 |
| ENC-1 | Slice 2：真实Host consumer把contact与`EncounterOpened`同draft提交；Continue提交`EncounterResolved`，Reverse同batch再提交`TraversalReversed`；exact pending encounter只消费一次。 |
| RPL-1 | 当前格式full run/replay/fork在完整batch boundary重建同一HostWorld；Replay不调用AI、Navigator或Forecast。 |

---

# 8. 最小竖切顺序

## Slice 1：AI 能理解并使用的 Place—Passage—Arrival

使用 3–5 个有记忆点的 Place、可区分的平行 Passage、一条单向 Passage、一个城门外 Place 与一个动态关闭的方向：

```text
AI DecisionPoint
→ 看见合法 location / exits / ETA
→ 选择 TakeExit，或建立由Game controller执行的TravelTo目标
→ Game condition + TraversalStarted 同 batch
→ lazy travel
→ independent Arrival candidate
→ AtPlace 后得到新的局部 observation
→ Replay
```

Slice 1 同时覆盖 entry mutation/arrival/DecisionPoint 同tick的全局仲裁，并证明 Navigator、GetExits 与 traversal planner 复用同一个 direction predicate。它不得创建 exit gate、`BlockedAtEndpoint`、contact ledger、rational DTO、receipt、Moment或未来index占位。

## Slice 2：一个真实的途中 Encounter

只在 Slice 1 完成后加入，并且必须同时交付一个真实 Game/AI consumer：

```text
两名 scripted Actor 在同 Passage 相向而行
→ internal exact intersection
→ contact independent candidate
→ pair-local consumed progress
→ Spatial contact + Game EncounterOpened 同 batch
→ AI 看见对方并选择 Continue 或 Reverse
→ Reverse 只有在反方向 entry 当前允许时才成为合法新 segment
→ EncounterResolved（Reverse 时同batch包含 TraversalReversed）
```

没有 `EncounterOpened`/Observation/Decision 的真实使用，就不先实现 contact planner 或 state。这样 contact 是一条垂直产品能力，不是一层 speculative infrastructure。

## Slice 3：只由真实故事触发

可能的后续能力必须逐项由 playable trace 触发，不能横向预建：

- pace adjustment；
- delayed/multi-actor encounter response；
- known-map route planning；
- Area hierarchy；
- ViewLink；
- content writer/hash；
- performance index。

---

# 9. 明确延期与重开条件

| 延期项 | 重开条件 |
|---|---|
| Area tree | AI/Game 真实需要稳定 region containment 查询或区域本身参与规则 |
| directed ViewLink | 一个场景要求从 A 远距感知 B，且至少两个消费者需要复用该 objective relation |
| canonical writer/hash/schema version | 地图编辑器、外部分发或需保留 run 首次要求 definition binding |
| AdjustPace / Stop / WaitOnPassage | playable encounter 证明 Reverse 不足以表达必要回应 |
| `ExitAllowed / BlockedAtEndpoint` | 一个不能用有故事身份的门前 Place 表达的场景，确实要求 topology change 拦住已经提交的 active segment |
| persisted Place-wide seal | 运行时新增 Passage 必须自动继承 Place 状态，且一个 Host planner 原子修改已知 incident Passages 已不足 |
| `MatchTraversalAtContact` | 真实玩法要求瞬时对齐同行，且能在1ms量化语义下给出不依赖receipt的世界状态 |
| CoPresence/CoTravel Started/Ended facts | Game 需要关系delta而arrival/contact causes + current query不足 |
| Player known-map / Fog / opaque handles | Design Note 009 的触发条件满足 |
| contact kinetic index / caches | reference pair Forecast 被profiler证明是实际瓶颈 |
| domain capacities | 真实合法内容触发可复现资源失败，且Kernel/Journal现有边界不足 |
| old format migration | 本原型当前明确不做；未来若出现必须保留的数据，另立迁移设计 |

以下内容不作为“延期功能”，而是除非部署模型根本改变就不再引入：

- Spatial-local winner；
- whole-tick settlement watermark；
- fixed same-time phase；
- external Spatial command gateway；
- Journal receipt 作为 live World authority；
- Source/Candidate/Moment 多套 identity；
- 为法证而存在的 expected→result event 镜像。

---

# 10. 结论

Graph Spatial World 的最小甜点位置是：

1. 用稳定 Place / Passage 表达 AI 可以理解的语义世界；
2. 用 EndpointA / EndpointB 两个 entry bit 表达双向、单向与关闭，并让 start、Reverse 与 Navigator 共用一个方向法则；
3. 保证已经进入 Passage 的 segment 可以离开并抵达；需要门禁等待时用 Place 表达故事节点；
4. 用一条 length + speed motion law 同时服务 lazy progress、ETA、route 与途中交会；
5. 把 mutation、arrival、contact 分解为独立、局部可消费的 occurrence；
6. 让 Kernel 决定同 tick 哪个原因先成为历史；
7. 让 Game / Perception 把 committed objective state 投影为紧凑的 Player observation 与 affordance；
8. 用 composite HostWorld 的一个 draft 原子连接物品、规则、移动与 encounter；
9. 只实现有 playable consumer 的竖切，不建设第二 Kernel、审计平台或未来兼容层。

一句话总结：

> **Spatial 用两个端点入口定义 Passage 的可行方向，让已进入者必能离开；Kernel 逐个决定不可逆原因，AI Player 只从已提交世界的合法局部视角决定下一步。**
