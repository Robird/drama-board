# Build Log 0001：`TravelTo` 多段旅行竖切

> 状态：**Implemented and verified**
> 记录日期：2026-08-22
> 实现基线：`d9165cf feat(spatial): replace Grid with Graph Spatial slice`
> 记录创建前的 HEAD：`1e02a57 归档Design Note 007：Spatial Framework`；它相对实现基线只移动文档，没有代码差异。
> 后续裁决：先建立独立 `Player.Agency` 程序集与 Spatial Knowledge Getter；默认实现返回完整静态地图。
> 实现提交：`dac5356 feat(player-agency): add spatial knowledge seam`、`dd35cf4 feat(firstboard): implement delegated TravelTo`

## 1. 目的与边界

下一批实现 **Slice 1.5：FirstBoard Game-owned `TravelTo` 多段旅行闭环**。它是 `Player.Agency` Spatial Knowledge Getter 的首个 consumer：接通 Graph Spatial 导航能力和 Player destination 协议面，同时建立客观世界与 conscious Player 之间的第一个正式程序集边界。

权威上下文：

- 总体空间设计：[Graph Spatial World](../design/graph-spatial-world.md)
- 已部分激活的 Player Agency / 主观地图边界：[Player spatial knowledge](../research/player-spatial-knowledge.md)
- 当前 Kernel 语义背景：[Kernel occurrence baseline](../implementation/kernel-occurrence-baseline.md)

本批约束：

- 无旧数据、旧 API 或旧 persistence codec 兼容；只维护新的当前格式。
- 简单性、灵活性优先，不引入审计型路线缓存、自动 phase 或第二套导航系统。
- 新建 `src/Player.Agency/**`，但本批 public surface 只包含 Spatial Knowledge Getter、知识快照和全图默认实现。
- `src/Kernel/**` 与 `src/Spatial/**` 预期零修改；若实现必须改它们，先停下重新论证。
- Spatial 继续独占客观位置、Passage 进入权、Traversal 与 Arrival；Game 只拥有旅行意图和叙事结果。
- Kernel 继续在同一 model time 对所有 occurrence 做全局确定性仲裁；rule 注册顺序不表示优先级。
- Host 一次提交完整 fact batch；不存在可观察的半批状态。

本文先作为压缩后可直接施工的上下文交接件使用，现已同时补入第 13 节竣工记录；第 3—12 节继续保留为实现语义、取舍与回归依据。

## 2. 当前代码证据与接缝

竣工后的接缝如下；它们也是后续演进应继续守住的边界：

- [`src/Spatial/Navigation/SpatialNavigator.cs`](../../src/Spatial/Navigation/SpatialNavigator.cs) 继续独占确定性最短路；[`src/FirstBoard/FirstBoardTravelGoalPlanner.cs`](../../src/FirstBoard/FirstBoardTravelGoalPlanner.cs) 是首个生产调用方，只负责 KnownGraph overlay、route 与 live first-leg 映射。
- [`src/Protocol/Intent.cs`](../../src/Protocol/Intent.cs) 的 `DestinationId` 与 [`src/Protocol/DecisionRequest.cs`](../../src/Protocol/DecisionRequest.cs) 的 `CandidateDestinationIds` 直接承载 `action.travel-to`，没有新增 Player DTO。
- [`src/Decision.Validation/PlayerDecisionValidator.cs`](../../src/Decision.Validation/PlayerDecisionValidator.cs) 已冻结 `travel + ExitId` 与 `travel-to + DestinationId` 两种互斥 shape。
- [`src/Player/RandomPlayerDriver.cs`](../../src/Player/RandomPlayerDriver.cs) 与 [`src/Player.Llm/LlmOutputParser.cs`](../../src/Player.Llm/LlmOutputParser.cs) 已通用地传递 destination；不要另造 Player DTO 或第二个 parser。
- [`src/FirstBoard/FirstBoardSpatialProjection.cs`](../../src/FirstBoard/FirstBoardSpatialProjection.cs) 已能投影当前 Place 的 live exit、票券要求与 `CanTakeNow`。
- [`src/FirstBoard/FirstBoardSystems.cs`](../../src/FirstBoard/FirstBoardSystems.cs) 中的 `ActorTravelStartedEvent` 路径会完成一次 Player decision，只能继续服务精确单腿 `travel`。
- [`src/FirstBoard/FirstBoardDomain.cs`](../../src/FirstBoard/FirstBoardDomain.cs) 中 `CompleteDecision` 同时推进 `Generation` 和 `DecisionSequence`，而 activity completion 只推进 `Generation`。
- [`src/FirstBoard/FirstBoardScenario.cs`](../../src/FirstBoard/FirstBoardScenario.cs) 负责 rule 注册、request、snapshot、fact name/summary 等集成面。
- [`tests/FirstBoard.Persistence.Tests/FirstBoardPersistenceTests.cs`](../../tests/FirstBoard.Persistence.Tests/FirstBoardPersistenceTests.cs) 的当前测试 codec 为 `firstboard-host-fact-json/4`；没有 `/3` reader 或 migration。
- [`src/Player.Agency`](../../src/Player.Agency) 已进入两个 solution；依赖方向保持为 `FirstBoard → Player.Agency → Spatial`，`Player.Agency` 只有一个直接生产依赖 `Spatial`。

继续演进前，以这些文件中的实际代码和第 13 节实现提交为准；若它们已经被后续 commit 改动，先重新核对本文假设，不要机械套用。

## 3. 冻结的产品语义

保留现有精确旅行：

```text
action.travel + ExitId
→ 精确选择一条 Passage
→ 抵达下一个 Place 后重新询问 Player
```

新增显式委托旅行：

```text
action.travel-to + DestinationId
→ Game 只保存目标 Place
→ 每次位于 Place 时重新计算下一腿
→ 自动经过中间 Place，不询问 Player
→ 到达目标或在当前 Place 无路时清除目标
→ 在同一 model time 恢复普通 DecisionPoint
```

因此，自动续行是 `travel-to` 的选择语义，不是所有旅行的默认行为。AI 想在下一站重新评估时，继续选择 `action.travel`。

FirstBoard MVP 仍把完整静态 `GraphDefinition` 视为已向每个 Player 披露，但所有 TravelTo consumer 必须先经过 `IPlayerSpatialKnowledgeGetter`。默认 `FullMapPlayerSpatialKnowledgeGetter` 返回完整静态地图，因此当前行为等价于地图全开；不能再在 destination advertisement 或 remote route planning 中绕过 Getter 直接枚举 `instance.Graph`。

这个形式边界不是战争迷雾的伪实现。真实 per-player known-token state、披露 facts、未知出口、错误地图与 last-seen 动态残影仍延期；未来替换 Getter 后，TravelTo 算法只消费它返回的 exact objective subgraph。

## 4. 最小持久状态与 Game facts

建议直接在 `BoardActor` 增加一个可空的强类型字段：

```text
PlaceId? TravelGoalPlaceId
```

只增加两类 Game payload：

```text
ActorTravelGoalSetEvent(
    ActorId,
    DestinationPlaceId)

ActorTravelGoalResolvedEvent(
    ActorId,
    DestinationPlaceId,
    Resolution: Completed | Blocked)
```

明确不持久化：route、next passage、起点、当前位置副本、ETA、Due、speed、动态拓扑快照或 planning state。每条实际运动继续由 Spatial facts 和 Spatial state 表达。

### 4.1 Reducer 语义

`ActorTravelGoalSetEvent`：

- actor 必须 `Activity == null`、没有现存 goal，并客观位于一个 Place；
- destination 必须是已定义且不等于 current Place 的 Place；
- 设置 `TravelGoalPlaceId`；
- 调用现有 `CompleteDecision`：`Generation + 1`、`DecisionSequence + 1`；
- 写一条简短的 `LastOutcome`，说明目标旅行已接受。

`ActorTravelGoalResolvedEvent`：

- payload destination 必须与 active goal 精确相等；
- actor 必须位于 Place；
- `Completed` 要求 current Place 等于 destination；
- `Blocked` 要求 current Place 不等于 destination；
- 清除 goal，`Generation + 1`，但 **不增加** `DecisionSequence`；
- 写一条本地、概括性的 `LastOutcome`。`Blocked` 不泄露远端哪一道门或哪次未来变化导致无路。

Reducer 只按 facts 折叠，不调用 Navigator。`Blocked` 是 live controller 形成的世界事实，不需要 reducer 重新证明“无路”。

### 4.2 完整世界不变量

- Genesis 中所有 actor 的 goal 为 `null`。
- Wait activity 与 TravelGoal 互斥。
- TravelGoal destination 必须存在于 GraphDefinition。
- `TravelGoal + Traversing` 合法。
- `TravelGoal + AtPlace` 也合法：它是中间 arrival 与下一次自动 start/resolve 之间可 replay、可 query、可 fork 的 committed prefix。
- Goal 活跃时 actor 不满足 `IsReadyForDecision`，不会产生普通 DecisionPoint。
- 不引入“tick 已 settle”屏障；AtPlace+Goal 立即 Forecast 一个 due=`world.Now` 的自动候选，由 Kernel 正常参与同 tick 仲裁。

## 5. AI-safe 的纯下一腿算法

### 5.1 Frozen Player knowledge snapshot

新增独立项目：

```text
src/Player.Agency/Player.Agency.csproj
namespace DramaBoard.Player.Agency.Spatial
```

规范 contract 由 Design Note 009 §4 拥有。施工时至少提供：

```text
IPlayerSpatialKnowledgeGetter<TWorld>
PlayerSpatialKnowledgeSnapshot
FullMapPlayerSpatialKnowledgeGetter<TWorld>
```

Getter 显式接收 committed world、`subjectId` 与 objective `GraphDefinition`，返回只读 `KnownGraph`。Snapshot factory 验证它只能是 objective graph 的 exact subgraph：retained Place/Passage 必须存在，Passage 的 endpoints、length 与两个 initial entry bits 不得被修改，parallel Passage 不得合并。

在一个 selected DecisionPoint 内只取一次 snapshot，并把同一实例依次交给 BuildRequest 与 ActionPlanner。不能先用一张图广告 destination，再为 Player 已选 action 重新调用 Getter。活动 TravelGoal 的 selected rule 在每个新的 AtPlace committed world 上取一次 snapshot；Forecast、Candidate、World 与 Journal 都不保存 snapshot。

默认 Getter 返回完整 `instance.Graph` 的 snapshot，但只返回静态 Definition，不读取 `GraphSpatialState`、remote runtime overrides、schedule、entity 或 ticket。未来 provider 若按 Player token 过滤，token state 必须来自 committed `TWorld`，不能藏在 provider 私有可变字段或 LLM MemoryBank。

`FirstBoardScenario.CreateKernel` 通过 constructor/composition parameter 接收 `IPlayerSpatialKnowledgeGetter<FirstBoardWorld>`，未显式提供时规范化为 FullMap default，并把同一个 policy 实例交给 `DecisionPointRule` 与 `TravelGoalRule`。不要使用 service locator 或让 FirstBoard 自行寻找 provider。

### 5.2 Current-live overlay 与 route

不能简单地“枚举一条 live 第一腿，再从它的终点跑静态 tail”。例如 Cellar gate 已关闭时，这种算法可能反复选择 `GateFront → Market → GateFront → Cellar` 的静态尾部，使 actor 在 GateFront 与 Market 之间往返。

建议新增 `src/FirstBoard/FirstBoardTravelGoalPlanner.cs`，每次调用构造一个 **ephemeral planning graph**：

1. 读取 actor 的 current Place，并调用现有 `FirstBoardSpatialProjection.GetExits` 得到当前所有平行 Passage 的 live `CanTakeNow`。
2. 复制本次 `PlayerSpatialKnowledgeSnapshot.KnownGraph` 的 Places 和 Passages；TravelTo 不纳入 snapshot 中不存在的 objective Passage。
3. 对 KnownGraph 中每条与 current Place 相接的 Passage，只把“从 current endpoint 进入”的 initial bit 替换为对应 objective live exit 的 `CanTakeNow`；反向 endpoint 仍保留 KnownGraph 的 initial bit。
4. 其他所有方向只保留 Definition 的 `InitialEntryAccess`，不复制 runtime overrides、scheduled changes 或 entities。
5. 用复制出的 `GraphDefinition` 和 `GraphSpatialState.Create(planningGraph, [])` 调用现有 `SpatialNavigator.FindRoute(current, goal, BoardTiming.TravelSpeed)`。
6. `RouteFound.Legs[0]` 必须按 `PassageId + From + To` 精确映射回当前一个 `CanTakeNow` 的 objective `FirstBoardExit`；随后仍用真实 `instance.Graph + world.Spatial` 调用 `SpatialPlanner.TryStartTraversal`。

这个 scratch graph/state 只存在于纯规划调用栈，不进入 World、fact、Journal 或 snapshot，也不要求修改 Spatial。它同时保证：

- 当前已知的 gate/entry override 和当前 actor 的票券能力会约束第一腿；
- 远端 runtime gate 不会提前影响 route 或 destination affordance；远端只读取 KnownGraph 的静态内容；
- 从本地关闭点离开后再回到同一点，不能绕过本地已知关闭方向；
- Navigator 原有的 duration 比较和完整 leg sequence Ordinal tie-break 保持唯一权威；不复制 Dijkstra。

票券的 MVP 边界也是有意局部化的：`CanTakeNow` 只覆盖当前 endpoint，远端 tail 不做 consumable/resource-state search。抵达远端 Place 后再按届时实际持有的票券重算。这足以覆盖当前 FirstBoard 内容；若真实内容要求“现在决定把同一张票留给后面哪条 Passage”，应停下讨论，而不是把背包规划偷偷塞进本批。

不要为了小图优化或缓存。BuildRequest 只遍历 KnownGraph 中非 current 的 Places；仅 `RouteFound` 的 destination 进入 `CandidateDestinationIds`，按 PlaceId Ordinal 排序。没有 candidate 时不广告 `TravelTo` affordance。现有 exact `travel` 仍可使用当前 Observation 已广告的 live ExitId，不被 remote known graph 替代。

### 5.3 Navigator 结果映射

| 结果 | BuildRequest / 初始 action | 活动 goal controller |
|---|---|---|
| `AlreadyAtGoal` | current destination 不广告；伪造选择无效 | 直接提交 `Completed`，正常情况下在调用 Navigator 前已处理 |
| `RouteFound` | 广告 destination；选择后启动第一腿 | 启动第一腿 |
| `NoRoute` | 不广告 | 提交 `Blocked` 并清 goal |
| `CostOverflow` | 不广告 | 提交 `Blocked` 并清 goal |
| `UnknownStart` / `UnknownGoal` / `InvalidSpeed` | scenario/host invariant error | scenario/host invariant error |

由于 DecisionRequest 是 frozen snapshot，已 advertised 的 `TravelTo` 在同一 DecisionPoint 中重新规划却得不到同一条合法第一腿，属于实现不变量破坏，不降级为普通 `ActionRejectedEvent`。

## 6. Protocol、request 与原子工作流

新增：

```text
ActionKinds.TravelTo = "action.travel-to"
```

PlayerDecisionValidator 冻结两种不同 shape：

- `travel`：必须有 advertised `ExitId`，`DestinationId == null`；保留当前其他语义。
- `travel-to`：必须有 advertised `DestinationId`；`ExitId`、actor/object target、duration 和 until 字段都必须为空。与现有 `travel` 一样允许可选 `FreeText`，但本批不把出发台词另存为 Game fact。
- 两者不能混用；current、伪造、未 advertised destination 都在 Player boundary 失败并零提交。

一个 idle-at-place actor 的 request 可同时拥有：

- 一个现有 `Travel` affordance，`CandidateExitIds` 是当前可用的精确 exits；
- 一个新的 `TravelTo` affordance，`CandidateDestinationIds` 是 §5 helper 在本次 frozen KnownGraph 中判定为 `RouteFound` 的 Places。

DecisionPoint rule 在调用 Player 前取得一次 knowledge snapshot；BuildRequest 和随后 selected `TravelTo` 的 ActionPlanner 必须复用这个 snapshot。若 advertised route 无法在同一 frozen world/snapshot 中映射为第一腿，属于 Host invariant failure。

### 6.1 初始 `TravelTo`

fact 顺序固定为：

```text
Game(ActorTravelGoalSet)
Game(TicketConsumed)?
Spatial(TraversalStarted)
```

三者在一个 Host batch 中全成全败。**不得**加入 `ActorTravelStartedEvent`：GoalSet 已完成本次 Player decision，再加入它会把 `DecisionSequence` 推进两次。

### 6.2 自动续行

有下一腿时：

```text
Game(TicketConsumed)?
Spatial(TraversalStarted)
```

自动腿同样不得使用 `ActorTravelStartedEvent`，也不推进 Game actor generation；Spatial movement generation 和 location 已足以使旧 candidate 失效。每腿客观历史由 Spatial facts 保留。

到达或受阻时只提交：

```text
Game(ActorTravelGoalResolved(Completed | Blocked))
```

每个被选中的 goal candidate 必须产生进展：start 改变 Spatial location/generation，或 resolve 清 goal。禁止 empty draft、no-op 和可重复出现的同一 cause。

### 6.3 失败分类

| 情况 | 结果 |
|---|---|
| malformed、DecisionId 不符、current/伪造/未 advertised destination | Validator 失败；零提交 |
| advertised route 无法映射 live exit，或真实 Spatial 拒绝 advertised first leg | host invariant exception；整个 batch 零提交 |
| GoalSet、ticket、Spatial start 任一 fold/完整验证失败 | Kernel 不发布半批；Game/Spatial/Journal/WorldVersion 全不变 |
| active goal 在当前 Place 得到 `NoRoute`/`CostOverflow` | 提交非空 `Blocked` world outcome，清 goal |
| active goal 已到 destination | 提交非空 `Completed` world outcome，清 goal |

初始合法 `TravelTo` 不存在单独的“NoRoute 但仍算 advertised”分支：BuildRequest 已用同一 helper 过滤。这避免新增一套初始拒绝叙事。

## 7. 自动 occurrence

在 [`src/FirstBoard/FirstBoardSystems.cs`](../../src/FirstBoard/FirstBoardSystems.cs) 增加：

```text
TravelGoalCandidate(
    ActorId,
    ActorGeneration,
    SpatialMovementGeneration,
    CurrentPlaceId,
    DestinationPlaceId)
```

CandidateKey 使用 canonical structured encoding（例如 canonical JSON UTF-8 bytes + `CandidateKey.FromBytes`），而不是用分隔符直接拼接可能含任意字符的 ID。逻辑内容为：

```text
["firstboard/travel-goal",
 ActorKey,
 ActorGeneration,
 SpatialMovementGeneration,
 CurrentPlaceId,
 DestinationPlaceId]
```

不要把选出的 Passage、route 或 ETA 放入 candidate identity。

`TravelGoalRule.Forecast` 只枚举：

```text
TravelGoalPlaceId != null
Activity == null
Spatial location is AtPlace
```

每个 actor 最多一个 candidate，Due=`world.Now`。Forecast 不调用 knowledge Getter。`PlanSelectedAsync` 重验 candidate 的五个状态字段和 key/due，随后对 selected actor/current world 取得一次 knowledge snapshot，再按顺序处理：

1. current == destination：Completed；
2. helper `RouteFound`：ticket? + Spatial start；
3. `NoRoute` / `CostOverflow`：Blocked；
4. 其他结果或 first-leg 映射/Spatial 拒绝：invariant exception。

在 `FirstBoardScenario.CreateKernel` 注册 rule。它与 Spatial arrival、gate close、deadline 和 DecisionPoint 一样参加 Kernel 全局仲裁；不要用注册顺序制造 phase。Goal resolve 后 actor generation 改变且 goal 清除，普通 DecisionPoint 可以在同一 model time 重新出现。

## 8. Replay、fork 与 AI 信息边界

- Replay 只折叠 journal facts，不调用 Player、Navigator 或 goal controller。
- Fork creation 同样只复制/折叠 committed state，不调用 Navigator。
- Fork 创建后若继续运行 Kernel，正常 Forecast/Plan 当然会再次调用 Navigator；“Fork 不调用 Navigator”只指创建过程。
- Replay 与 fork creation 也不调用 PlayerSpatialKnowledge Getter；fork 续跑时 Getter 从 fork 的 committed world 重新投影。
- 每一腿已经选择的确切 Passage 在 `TraversalStartedFact` 中，重放不需要重新选路。
- 当前 request 可以暴露 KnownGraph destinations 和本地 live exits；不能读取远端 runtime entry override、scheduled gate change 或远端失败原因。
- Gate close 与自动续行同 tick 时不加 phase：不同 world seed 可以让任一候选先赢；已进入 Passage 后 arrival 必须完成，尚未进入则留在门外并 Blocked。

## 9. 依赖顺序与文件落点

按以下顺序施工，保持每一步可编译：

1. 新建 `Player.Agency` 与 tests 项目，加入两个 solution；先落 exact-subgraph snapshot、Getter、FullMap default 与 dependency guard。
2. Protocol 增 `TravelTo` stable identifier；Decision.Validation 增新的 intent shape 与测试。
3. `BoardActor` goal、两类 Game facts、reducer、完整 world validator 与 Genesis 初始化。
4. 新增纯 `FirstBoardTravelGoalPlanner`；以 injected KnownGraph 为静态底图完成 overlay-route 测试。
5. DecisionPoint 一次获取 knowledge snapshot；BuildRequest 生成 destination affordance；ActionPlanner 复用 snapshot 原子启动 `TravelTo`。
6. 增 `TravelGoalCandidate/Rule`，注册进 FirstBoard kernel，并让 active goal suppress DecisionPoint。
7. 补 world snapshot、fact name/payload summary、Demo drama record exhaustive cases。
8. 测试 persistence codec 直接从 `/3` 升 `/4`，加入两种新 Game fact shape；不写 `/3` reader 或 migration。
9. 跑 focused、全 solution、local solution 和 Demo build 验收。

预期生产修改：

- 新建 `src/Player.Agency/Player.Agency.csproj`
- 新建 `src/Player.Agency/Spatial/IPlayerSpatialKnowledgeGetter.cs`
- 新建 `src/Player.Agency/Spatial/PlayerSpatialKnowledgeSnapshot.cs`
- 新建 `src/Player.Agency/Spatial/FullMapPlayerSpatialKnowledgeGetter.cs`
- `DramaBoard.slnx`、`DramaBoard.Local.slnx`
- `src/Protocol/ProtocolKinds.cs`
- `src/Decision.Validation/PlayerDecisionValidator.cs`
- `src/FirstBoard/FirstBoard.csproj`
- `src/FirstBoard/FirstBoardDomain.cs`
- `src/FirstBoard/ScenarioDefinition.cs`
- `src/FirstBoard/FirstBoardSpatialProjection.cs`（只在确有共享 helper 需要时改）
- 新建 `src/FirstBoard/FirstBoardTravelGoalPlanner.cs`
- `src/FirstBoard/FirstBoardSystems.cs`
- `src/FirstBoard/FirstBoardScenario.cs`
- `src/FirstBoard.Demo/DramaRecordWriter.cs`

预期测试修改：

- 新建 `tests/Player.Agency.Tests/Player.Agency.Tests.csproj`
- 新建 `tests/Player.Agency.Tests/SpatialKnowledgeTests.cs` 与 dependency guard
- `tests/FirstBoard.Tests/FirstBoard.Tests.csproj`
- `tests/Protocol.Tests/StableIdentifierTests.cs`
- `tests/Protocol.Tests/IntentJsonTests.cs`
- `tests/Decision.Validation.Tests/PlayerDecisionValidatorTests.cs`
- 新建 `tests/FirstBoard.Tests/TravelGoalHostTests.cs`，不要继续膨胀现有 `CompositeGraphHostTests.cs`
- `tests/Player.Llm.Tests/LlmOutputParserTests.cs`、`PromptRendererTests.cs` 只补行为/golden；生产 parser 已有通用 destination 管线
- `tests/FirstBoard.Persistence.Tests/FirstBoardPersistenceTests.cs`

`ScenarioDefinition` 的 content revision/ruleset 不因新增运行时行为自动升级；run manifest 已记录代码版本。除非实际 content schema 改变，不为可审计性增加额外版本层。

> 后继说明（2026-08-23）：本段记录的是 production Save 出现前的 current-build-only 决定。自 [Game content and Save boundary](../implementation/game-content-save-boundary.md) 的 resumable contract 起，software provenance 不再承担 restore gate；会改变旧 Save fold 或续局未来行为的 Ruleset 变化必须更换 `RulesetId`，仍不提供旧格式迁移。

## 10. 验收矩阵

| ID | 必须证明的行为 |
|---|---|
| T01 | `travel` 抵达下一 Place 后重新询问 Player；`travel-to` 自动续行。 |
| T02 | Alice 从 Tavern `TravelTo(Cellar)` 完成多腿旅行；goal 最终清除；由这次 TravelTo 选择引起的 `DecisionSequence` 只增加一次，自动腿与 resolve 不再增加。 |
| T03 | 初始 GoalSet、票券消费与 first start 同 batch；故障注入证明零半批提交。 |
| T04 | 自动每腿不产生 `ActorTravelStartedEvent`，Game 不保存 route/next leg/ETA。 |
| T05 | Bob 从 Market `TravelTo(Tavern)` 没有 ferry 票时选择 cart，而不是 ferry、road 或 NoRoute。 |
| T06 | 远端 Cellar gate 已关闭时，从 Market 仍广告并先走到 GateFront；抵达后才 Blocked。 |
| T07 | GateFront 本地 gate 关闭时 helper 返回 NoRoute，不选择 `GateFront→Market→GateFront` 往返。 |
| T08 | Gate close 与续行同 tick 的两种 seed 分别证明“先进入则必抵达”和“先关闭则留在门外”。 |
| T09 | current、伪造、未 advertised 或 Travel/TravelTo 混合字段在 Player boundary 失败，零提交。 |
| T10 | selected goal candidate 必然 start、Completed 或 Blocked，不产生 empty/no-op/repeated cause。 |
| T11 | replay 与 fork creation 不调用 Player/Navigator；从 fork 继续运行时 controller 正常恢复。 |
| T12 | 在 `AtPlace + active goal` prefix 停止、snapshot/replay/fork 均合法，并可继续自动推进。 |
| T13 | 同成本路线沿用 Navigator 的完整 leg Ordinal tie-break；parallel Passage 不被 destination 合并。 |
| T14 | Slice 1 的 immediate exit、parallel Passage、arrival、endpoint gate、atomic batch 与 replay 测试全部回归通过。 |
| K01 | FullMap Getter 对任意 subject 返回完整静态 graph；不同 runtime overrides 不改变 snapshot。 |
| K02 | Exact-subgraph snapshot 拒绝 unknown/modified/missing-endpoint content，并保留 parallel Passage 与 A/B direction bits。 |
| K03 | fake subset Getter 隐藏 objective shortcut 后，TravelTo 只走较长的 known route，不能回读 `instance.Graph` 偷路。 |
| K04 | DecisionPoint advertisement 与 selected first leg 使用同一个 knowledge snapshot；Getter 不在 Forecast 中调用。 |
| K05 | snapshot 只影响规划；current live/ticket 与真实 SpatialPlanner 仍可否决第一腿，且失败零提交。 |

## 11. 明确不做与复杂性停线

本批不做 Contact/Encounter、Reverse/pause/resume、途中 anchored interaction、waypoint/avoid/prefer、Area/ViewLink、真实 known-token store、披露 facts、战争迷雾、远端动态知识投影、route cache、旧格式迁移，也不建立通用 cognition/skill/signal 框架。

遇到以下真实需求或失败时，先与用户讨论，不在实现中临时扩张模型：

- 同一 consumable 可用于多条 Passage，需要决定“在哪里花”；
- 路线必须先取得票券、载具或其他能力，或需要资源状态搜索；
- 玩家需要 waypoint、avoid/prefer、暂停/恢复或中间 Place 交互；
- 真实 content 要求 destination 可见性因 actor 而异，此时启动 known-token state/facts 设计；
- 产品要求远端 runtime 变化影响当前决策；
- Contact/Reverse 与 active goal 的取消语义无法用一条简单 resolve fact 表达；
- 实现需要改变 Kernel 仲裁或 Spatial authority 才能成立。

## 12. Definition of Done

至少执行：

```powershell
dotnet restore DramaBoard.Local.slnx --nologo
dotnet test tests/Player.Agency.Tests/Player.Agency.Tests.csproj --no-restore --nologo
dotnet test DramaBoard.slnx --no-restore --nologo
dotnet test DramaBoard.Local.slnx --no-restore --nologo
dotnet build src/FirstBoard.Demo/FirstBoard.Demo.csproj --no-restore --nologo
git diff --check
git diff -- src/Kernel src/Spatial
git status --short
```

最后一个 Kernel/Spatial diff 必须为空；`git status` 只能包含本批预期文件。

## 13. 实施结果（2026-08-22）

本批按依赖顺序分两次实现提交完成：

1. `dac5356 feat(player-agency): add spatial knowledge seam`
   - 新建 `Player.Agency` 与 8 项测试，加入 standard/local solution。
   - 落地 exact-subgraph snapshot、Getter、FullMap default 与依赖守卫。
   - 增加 `action.travel-to` stable identifier、协议文档与 validator shape。
2. `dd35cf4 feat(firstboard): implement delegated TravelTo`
   - 落地 actor travel goal、Set/Resolved facts、reducer/world invariants 与 persistence codec `/4`。
   - 落地 KnownGraph 驱动的 `FirstBoardTravelGoalPlanner`、destination advertisement、原子首腿和自动续行 rule。
   - 补齐 LLM 输入输出、snapshot/fact summary、Demo 展示、replay/fork、并发仲裁、失败注入与旧 Spatial slice 回归。

实现与冻结语义一致，没有需要反向修改本文的设计偏差。`FirstBoardSpatialProjection` 未修改，因为新 planner 可以复用已有投影和 Spatial API，不需要为形式统一增加一层共享 helper。没有持久化 route、knowledge snapshot、candidate 或 ETA，也没有增加 phase、cache、旧 codec migration 或第二套导航器。

独立复审覆盖了程序集边界、FirstBoard 状态机/Kernel 语义、Protocol/LLM/persistence 和测试缺口。初审提出的三段 batch fold 故障注入、精确 `travel` 二次询问、真实 prefix replay/fork continuation、stale candidate 全字段拒绝均已补齐；复审结论为 PASS，无未解决的 blocking、high 或 normal finding。

最终验证证据：

- `dotnet restore DramaBoard.Local.slnx --nologo`：成功。
- `dotnet test tests/Player.Agency.Tests/Player.Agency.Tests.csproj --no-restore --nologo`：8/8 通过。
- `dotnet test DramaBoard.slnx --no-restore --nologo`：254/254 通过。
- `dotnet test DramaBoard.Local.slnx --no-restore --nologo`：272/272 通过，其中 Journal.Atelia 14/14、FirstBoard.Persistence 4/4。
- `dotnet build src/FirstBoard.Demo/FirstBoard.Demo.csproj --no-restore --nologo`：成功，0 warning、0 error。
- `git diff --check`：通过。
- `git diff -- src/Kernel src/Spatial`：为空；本批没有修改 Kernel 或 Spatial 生产代码。

第 10 节验收矩阵均有自动测试覆盖；第 11 节延期项保持延期，下一批不应把它们误认为 `TravelTo` 的隐含欠账。
