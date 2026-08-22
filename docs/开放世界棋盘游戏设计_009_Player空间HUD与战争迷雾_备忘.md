# Design Note 009：Player 空间 HUD、战争迷雾与主观地图备忘

**状态：部分激活；`Player.Agency` 空间知识 Getter 已冻结，真实披露状态与战争迷雾仍延期**

**创建日期：2026-08-20；最近裁决：2026-08-22**

**来源：** 从 [Design Note 008](./开放世界棋盘游戏设计_008_Graph_Spatial_World.md) 拆出；首个施工 consumer 见 [Build Log 0001](./build-log/0001-travel-to.md)。

---

# 1. 为什么必须与客观 Spatial 分开

Player 已知地图最接近的成熟前人设计不是客观世界状态，而是 RTS 的战争迷雾与地图残影：

- Player 看见过某个地点；
- 离开视野后，地图可能保留最后一次观测；
- 残影不代表最新事实；
- 不同 Player 可以拥有不同地图；
- HUD 展示什么，不等于 Player 真正记得或相信什么。

因此这部分不属于 Graph Spatial World。Graph Spatial World 只拥有：

- immutable Graph Definition；
- Actor 客观位置与移动；
- objective navigation；
- objective visibility opportunities；
- Kernel 中已经提交的客观空间事实。

Player-facing 层可以消费合法披露的客观材料，但不能反向成为 World authority。特别是：

- Spatial Runtime 不出现 PlayerId；
- Spatial State 不保存 seen / known / confirmed；
- Spatial reducer 不读取 Player memory；
- Player 知识图不能直接修改客观 location、Passage entry access、Traversal 或 Arrival。

# 2. 已接受的 Player Agency 层

我们显式承认一个位于 committed world 与 conscious Player 之间的逻辑层：

```text
Graph Spatial World + Game World
    objective committed state
        ↓ Game / Perception 合法披露
Player Agency
    player-scoped knowledge projection
    delegated automatic activity（首个 consumer：TravelTo）
    future attention handoff
        ↓ frozen DecisionRequest
Conscious Player
    Human / LLM / scripted decision policy
```

反向执行：

```text
Conscious Player intent
    → Game validator / Game-owned concrete controller
    → controller consumes Player Agency knowledge projection
    → explicit Game + Spatial proposal
    → Kernel atomic commit
```

当前只把 **Player-scoped static spatial knowledge Getter** 做成正式程序集边界。TravelGoal、票券、叙事结果和具体 occurrence 仍由 FirstBoard Game/Host 拥有；Spatial 继续独占客观运动 authority。

本批 `Player.Agency` 不拥有 TravelTo planner/controller，也不定义通用 skill abstraction；FirstBoard `TravelTo` 只是该知识投影边界的首个 consumer。

这不是一个拥有独立时钟、scheduler、journal 或异步 event ingress 的第三模拟器。Player Agency 的确定性自动行为仍作为普通 Host occurrence 参加 Kernel 全局仲裁。

# 3. `Player.Agency` 不是 Player 大脑

新程序集建议为：

```text
src/Player.Agency/Player.Agency.csproj
assembly:  DramaBoard.Player.Agency
namespace: DramaBoard.Player.Agency.Spatial
```

选择较宽的程序集名，是为了稳定表达“客观世界与主意识之间的代理层”；当前 public surface 仍保持极窄，只有 Spatial Knowledge Getter、知识快照和全图默认实现。

严格章程：

- 它可以表达系统保证的 player-scoped knowledge projection；
- 它将来可以承载被多个 Game 真实复用的 delegated activity / attention 原语；
- 它现在不拥有 TravelGoal、facts、reducer、Player memory、belief、prompt 或 LLM loop；
- 它不是 `Cognition`、`Mind`、`Subconscious`、HUD renderer 或通用 skill registry；
- 没有第二个跨 Game consumer 前，具体 skill 与 committed state 继续留在 owning Game。

系统维护的 knowledge snapshot 最多声称“此材料可供该 Player 的系统代理使用”，不声称：

- Player 一定记得；
- Player 一定相信；
- LLM 一定正确理解；
- 最后一次观测仍是当前事实；
- rumor 已经变成 World truth。

`Player.Llm.MemoryBank` 继续是 Player 私有的叙事记忆，可以遗忘、误解、怀疑或推翻材料；自动 world transition 不得读取它作为权威输入。

# 4. 已冻结的最小 Spatial Knowledge Getter

## 4.1 项目依赖

```text
FirstBoard → Player.Agency → Spatial → Kernel（传递）
```

`Player.Agency` 只直接引用 `Spatial`。它不得直接引用：

- `Kernel`；
- `FirstBoard` 或其它具体 Game；
- `Host`；
- `Protocol`；
- `Player` / `Player.Llm`。

Spatial 与 Kernel 不得反向引用 `Player.Agency`。两个 solution 都加入 `Player.Agency` 与配套测试项目，并用 dependency guard 固化这条边。

## 4.2 最小 public contract

建议的首版 public surface：

```csharp
namespace DramaBoard.Player.Agency.Spatial;

public interface IPlayerSpatialKnowledgeGetter<in TWorld>
    where TWorld : notnull
{
    PlayerSpatialKnowledgeSnapshot GetKnownGraph(
        TWorld committedWorld,
        string subjectId,
        GraphDefinition objectiveGraph);
}

public sealed class PlayerSpatialKnowledgeSnapshot
{
    private PlayerSpatialKnowledgeSnapshot(GraphDefinition knownGraph);

    public GraphDefinition KnownGraph { get; }

    public static PlayerSpatialKnowledgeSnapshot FullMap(
        GraphDefinition objectiveGraph);

    public static PlayerSpatialKnowledgeSnapshot CreateExactSubgraph(
        GraphDefinition objectiveGraph,
        GraphDefinition exactSubgraph);
}

public sealed class FullMapPlayerSpatialKnowledgeGetter<TWorld>
    : IPlayerSpatialKnowledgeGetter<TWorld>
    where TWorld : notnull
{
    public static FullMapPlayerSpatialKnowledgeGetter<TWorld> Instance { get; }

    public PlayerSpatialKnowledgeSnapshot GetKnownGraph(
        TWorld committedWorld,
        string subjectId,
        GraphDefinition objectiveGraph);
}
```

`TWorld` 与 `subjectId` 是有意保留的 seam：

- FirstBoard 当前传 `FirstBoardWorld` 与 `actor.Key`，不新增第二套 Player/Actor identity；
- 默认实现忽略二者，返回完整静态地图；
- 未来 provider 可以从 committed world 中按 subject 读取可 replay/fork 的 known-token state；
- provider 不需要把 run-mutable token dictionary 藏在 service 私有字段中。

Getter 必须是 committed-world snapshot 的纯、同步、确定性函数。它不分配 token、不修改 world、不做 I/O、不缓存 run-mutable knowledge，也不拥有 state/facts/reducer。

## 4.3 Exact-subgraph 法则

`PlayerSpatialKnowledgeSnapshot` 是语义包装，也是 invariant gate。它只允许：

- `FullMap` 持有原 immutable objective `GraphDefinition`；
- `CreateExactSubgraph` 持有 objective definition 的精确子图。

精确子图只能删除 Place / Passage，不能发明或修改 retained content：

- 所有 retained `PlaceId` 必须存在于 objective graph；
- 所有 retained `PassageId` 必须存在于 objective graph；
- retained Passage 的 `EndpointA/B` 顺序、`Length` 与两个 `InitialEntryAccess` bits 必须逐项等于 objective definition；
- retained Passage 的两个 endpoints 都必须已经存在于 supplied subgraph；不自动补入未知 endpoint；
- parallel Passages 按 `PassageId` 独立保留，不能按 endpoint pair 合并；
- 输入顺序不影响 canonical graph/snapshot。

本批复用 objective `PlaceId` / `PassageId`，不引入 opaque player-local handles。未来真的需要未知 endpoint、错误地图或 identity alias 时，无旧 API 兼容义务，可重新设计 snapshot，而不是现在预建 DTO 矩阵。

## 4.4 “默认地图全开”的准确含义

```text
FullMapPlayerSpatialKnowledgeGetter
    (committed world, subject, objective GraphDefinition)
→ PlayerSpatialKnowledgeSnapshot.FullMap(objective GraphDefinition)
```

“全开”只表示完整 **静态结构**：Places、Passages、length 与 initial directions。默认 Getter 不接收或返回：

- `GraphSpatialState`；
- runtime entry overrides；
- scheduled entry changes；
- entity/location/traversal；
- ticket、inventory 或 Game capability；
- Navigator route；
- Kernel candidates、Due、rank、WorldSeed 或 Journal 数据。

因此远端 gate 的 runtime 开关不会通过 Getter 提前泄露。当前 Place 的 live entry access 和票券能力仍由 owning Game 投影并覆盖；真正启程仍由 objective Spatial planner 对真实 state 重验。

## 4.5 FirstBoard 的 frozen-snapshot 使用法则

在一个 selected DecisionPoint 内只调用 Getter 一次：

```text
knownSnapshot = Getter(world, actor.Key, instance.Graph)
    → BuildRequest destination affordance
    → PlayerDriver response
    → FirstBoardActionPlanner 使用同一个 snapshot
```

不能在广告 destination 后为 selected action 再取一次知识图。活动 TravelGoal 每到一个 Place 后，由 selected `TravelGoalRule` 对新的 committed world 调用一次 Getter，再只提交下一腿。

`Forecast` 不调用 Getter；Candidate 不保存 map、route 或 next leg。Replay 与 fork creation 也不调用 Getter；从 fork 继续运行 live Kernel 时可以重新投影。

TravelTo 的路线输入是：

```text
KnownGraph static route material
+ current Place objective live exits
+ current Actor Game capability
→ ephemeral planning graph
→ 只选第一腿
→ objective SpatialPlanner 最终重验
```

Snapshot、scratch graph、route 与 next leg 都不进入 Game state、facts、Journal 或 persistence codec。

## 4.6 Future known-token seam

用户已接受的未来最小方向是：

```text
subject → independent known-token set
known-token set + objective immutable GraphDefinition
    → exact known subgraph
    → PlayerSpatialKnowledgeSnapshot
```

本批只冻结 seam，不冻结 token schema。未来 token 若会影响 TravelTo 自动行为，必须：

- 由 Game / Perception 的 committed facts 与 reducer 演进；
- 成为 `TWorld` 中可 replay/fork 的 player-scoped state；
- 由 Getter 纯读取，而不是藏在 provider 或 `Player.Llm.MemoryBank`；
- 不修改 objective GraphDefinition；知识集合变化只是投影子集变化；
- 明确 token 粒度后再决定 Passage 是否隐含 endpoints、是否按方向披露、是否允许遗忘。

当前不实现 token 类型、ledger、disclosure facts、persistence shape 或 mutation API。

当前产品还冻结：一个 run 的 objective `GraphDefinition` 不发生结构性增删。known-token 集合将来可以改变 Player 的投影子集，但不能创建、删除或改写 objective Place/Passage；如果以后真的需要运行时改变 Definition 结构，应重新设计 snapshot/token correlation，而不是沿用本批假设。

# 5. 仍然延期的主观地图主题

以下主题保留为未来研究清单，本轮不冻结 schema。

## 5.1 战争迷雾与残影

- 当前可见 / 曾经可见；
- last observed state / time；
- 远端状态变化后残影过时；
- visibility ended 不等于对象不存在；
- 只有显式 AbsenceEvidence 才可能支持“这里没有某物”；
- 是否遗忘。

## 5.2 Player-scoped identity 与未知出口

- Player-local Place / Passage handle；
- Objective identity 的 server-side correlation；
- 同一入口重复观察的 identity reuse；
- ExitStub；
- 知道有出口但不知道终点；
- Explore / Return；
- 抵达后的 identity resolution；
- 多 Player 隔离。

## 5.3 Claims 与地图注记

- rumor、错误地图与 sourced claim；
- conflict / refute；
- Player 自己的推断；
- confidence / trust；
- HUD 笔记与 Player belief 的区别。

## 5.4 更丰富的 Player-facing navigation

- stale travel-time estimate；
- direction estimate；
- no-known-route 与 route assumptions；
- waypoint / avoid / prefer；
- 获取票券、载具或能力后的资源规划。

## 5.5 DecisionView、HUD 与 Human realization

- bounded local map；
- inspect place / route / claim；
- pagination 与 anti-enumeration；
- Human / LLM 共享安全 view-model；
- Prompt token budget；
- Graph→Grid / 2D / 3D / text presentation；
- 玩家可见动画；
- 不让 renderer 成为第二套 topology。

## 5.6 TravelActivity、途中安全投影与注意力

- Arrival 后继续 / 取消 / 重规划；
- pending attention cues 的 identity、累积与 acknowledge；
- Contact / Encounter 何时暂停自动活动；
- opaque traversal receipt；
- Continue / Return / unknown-forward；
- capability 只允许尝试，不承诺 World 一定接受。

# 6. 当前明确不做

- 不实现真实 known-token store、披露 facts 或 Fog state；
- 不实现 Player-local opaque identity 或 token resolver；
- 不实现错误地图、rumor、confidence、forgetting 或 knowledge sharing；
- 不保存 last-seen dynamic access；
- 不新增 PlayerSpatialWorld、第二个 Graph authority 或 route cache；
- 不新增 signal bus、attention queue、ack facts 或通用 activity/skill registry；
- 不让 Protocol / LLM 直接依赖 `Player.Agency`；
- 不改变 Spatial definition/state/facts/reducer；
- 不为未发布格式增加迁移或兼容层。

# 7. 最小实施与验收

首批只实现：

```text
Player.Agency project + dependency boundary
IPlayerSpatialKnowledgeGetter<TWorld>
PlayerSpatialKnowledgeSnapshot exact-subgraph invariant
FullMapPlayerSpatialKnowledgeGetter<TWorld>
FirstBoard TravelTo consumer injection
```

至少证明：

- FullMap 对不同 subject 返回完整静态 objective graph，重复调用相等且无 state mutation；
- exact subgraph 拒绝 unknown Place/Passage、被修改的 Passage payload 和缺失 endpoint；
- road/ferry 等 parallel Passage 不合并；
- `A=true/B=false` 的 endpoint order 与方向 bits 原样保留；
- 两个 runtime state 不同但 Definition 相同的 world 得到相同 FullMap snapshot；
- fake subset Getter 隐藏更短捷径后，FirstBoard TravelTo 不会偷读 objective graph 选取未知 Passage；
- destination advertisement 与 selected first leg 使用同一 snapshot；
- current live access 可否决 stale/static first leg，真实 Spatial planner 仍是最后 authority；
- Replay 与 fork creation 不调用 Getter；续跑 fork 正常调用；
- `Player.Agency` 只直接引用 Spatial，Kernel/Spatial production diff 为零。

# 8. 重新启动完整主观地图设计的触发条件

满足以下任一条件时，再把本备忘升级成完整 Player spatial knowledge 设计：

- 两个 Player 必须看到不同地图；
- TravelTo 必须真正受已披露 token 限制；
- 动态世界需要 last-seen 残影；
- 需要错误地图、传闻或显式地图笔记；
- 需要未知出口探索或 player-local handle；
- 需要保存、Replay 或 Fork player-facing map；
- Objective API 已被 Player-facing consumer 误用；
- 多个关键 cue 必须在下一合法 DecisionPoint 前累积。

届时最重要的边界仍然是：

> **Player Agency 可以保存系统向 Player 披露过什么，并执行已授权的具体技能；它不能替 Player 定义自己记得什么、相信什么或怎样思考。**
