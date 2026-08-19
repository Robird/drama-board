# Design Note 008：面向 LLM Player 的 Graph-first 空间世界
## ——以 Place、Passage、旅程与主观认知图构成 RPG-like 世界

**状态：概念 / 功能架构候选稿**

**日期：2026-08-20**

**讨论基线：`spatial-grid-v1-baseline`（`main @ ed6b6d3`）**

**定位：** 本文重新审视 DramaBoard 的空间本体。它优先回答“怎样的世界表示最适合 LLM 驱动的 AI Player 生活、判断和行动”，而不是先回答怎样复用当前 `src/Spatial`。现有 GridMap-first Spatial 的利用、重构或归档只在文末作为执行层候选讨论，不构成本文主体。

本文承接以下既有判断：

- 世界是棋盘，Player 是棋手，剧情是棋谱；
- Law 决定客观上能够发生什么，Player 只提交意图；
- AI Player 应接收局部、主观、有来源的信息，而不是上帝视角；
- LLM 应决定语义行动，不应管理逐格移动和“腿部肌肉”；
- Kernel 以模型时间、Forecast、事件提交、Replay 与 Fork 保存可信因果；
- 地图首先是一种机会结构、信息结构和决策结构，视觉几何是次级目标。

---

# 1. 阶段性决定

本文提出以下候选裁决：

> **DramaBoard 的权威 RPG 空间应是一张由语义 Place、带正耗时的 Passage 和 Area containment 构成的 Graph。**

Graph 是：

- 地点身份的真值；
- 可通行关系的真值；
- 旅行耗时与路线选择的真值；
- Actor 当前位于地点或正在某条通路上旅行的真值。

Graph 不是：

- 完整物理几何；
- 任意知识关系的通用 Knowledge Graph；
- 自动推导视线、方向、距离感和互动含义的魔法数据结构；
- 可以整体交给 Player 的全知世界地图。

因此更准确的表述不是：

> 世界的一切空间事实都是 Graph。

而是：

> **Place / Passage Graph 是世界唯一的拓扑与旅行真值；其它空间感知和表现必须以明确的、受限的投影建立在它之上。**

在这一模型下，二维 GridMap 降为可选的 Human realization：

- AI Player 不依赖 GridMap 才能导航、观察和行动；
- Simulation 不需要先把 Graph 编译成 GridMap 才能运行；
- Human 客户端未来可以把 Graph 实现成 2D、3D、文本或其它表现；
- 任何表现层都不能反过来成为第二套客观拓扑。

---

# 2. 为什么 Graph 更适合作为第一真理

GridMap 是一种成熟、确定、容易渲染的物理表示。它并不必然把 Cell 暴露给 LLM；Design Note 007 已经用 Anchor、Zone 与语义 MoveGoal 证明可以在 Grid 之上建立较好的 Player 接口。

Graph-first 的核心收益更窄，也更可信：

- Place / Passage 直接成为 authoring 与 state 的基本粒度；
- 长途在途事实成为一等状态，而不是逐格移动的副作用；
- 内容作者先表达决策拓扑和信息披露，再选择是否制作几何 realization；
- Objective、Confirmed 与 Claim 三种空间事实更容易在身份和 authority 上分开。

因此这里不是断言“Grid 必然导致错误 API”，而是判断：对 LLM-first RPG，Grid 携带的大量局部几何通常不是最合适的第一作者表示。

一个 80 × 80 的地图可能包含 6400 个 Cell，但 Player 真正关心的通常只是：

```text
我在旧港。
北边山路通往修道院，约需两小时。
东侧渡口暂时关闭。
隔海能看见灯塔，但我不知道怎样到达。
Alice 最后一次被人看见是在市场。
```

Cell 级表示把以下内容混在一起：

- 有决策意义的分岔；
- 只是让道路弯曲的格子；
- 有信息意义的瞭望点；
- 纯美术留白；
- 角色不会逐项思考的局部导航细节。

这会造成几个结构性问题。

## 2.1 LLM 被迫处理低价值空间细节

LLM 擅长：

- 目标取舍；
- 社会推理；
- 信息可信度；
- 路线风险；
- 计划和重新考虑。

它不应负责：

```text
向北一格
向东一格
绕过墙角
再向北三格
```

即使这些动作由工具代劳，以 Cell 为第一真理仍会诱导上层接口围绕坐标、范围和局部几何组织，而不是围绕地点、通路和旅程组织。

## 2.2 几何可见不等于叙事上应当披露

“从山崖先看到人质，但不能直接到达”是一种作者有意设计的信息结构。

若它完全依赖几何 LOS，则一次无关的道路美化、墙体移动或地图压缩都可能改变披露时机。对 LLM-native 世界，更重要的是明确声明：

```text
从 cliff-overlook 可以感知 hostage-tower；
但不存在从 cliff-overlook 直接到 hostage-tower 的已知 Passage。
```

## 2.3 网格中的距离常常不是 Player 所关心的距离

相同的十格路程可能分别是：

- 十分钟城内步行；
- 两小时泥泞山路；
- 一天海上航行；
- 一次瞬时但有代价的传送。

对 Player 有意义的是旅行时间、风险、访问条件和沿途事件，而不是 Cell hop count 本身。

## 2.4 Grid 很容易让表现事实变成 Law

如果一个 Human 地图编辑者为了美观挪动墙壁，不应意外改变：

- 两地是否连通；
- 路程耗时；
- 隐藏捷径是否存在；
- 何时第一次看见目标；
- 两名 Actor 是否必经同一地点。

将 Graph 设为 Ground Truth，可以把这些功能关系从表现几何中解耦。

---

# 3. 四种空间表示与明确所有权

Graph-first 不意味着所有系统共享同一个 Graph 对象。

DramaBoard 至少需要区分四种含义完全不同的空间表示。

```text
Objective Spatial Graph
        │
        ├── Law / Navigation / Simulation
        │
        ├── Perception candidates
        ▼
Committed Player-scoped Observation
        │
        ├── Confirmed Spatial Facts
        ├── Rumor / Map / Inference Claims
        ▼
Bounded Spatial Decision View
        │
        ▼
Human or AI Player

Objective Spatial Graph
        │
        └── optional Grid / 2D / 3D realization
```

## 3.1 Objective Spatial Graph

唯一客观真值，包含：

- 客观存在的 Place；
- 客观存在的 Passage；
- Area containment；
- Passage 当前是否可用于新旅行；
- Actor 客观位于 Place 或正在 Passage 上旅行；
- 客观、受限的感知候选关系。

完整 Objective Graph 只能供 Law、授权的模拟系统和全知调试工具使用。

## 3.2 Confirmed Spatial Projection

Player 通过亲历、直接感知或规则明确建立的空间事实，例如：

- 去过旧港；
- 亲眼看见北门连接山路；
- 在 T 时刻看见桥是开放的；
- 沿路实际抵达修道院。
- 确认旧港属于北境，而北境属于已知世界。

它是 player-scoped、可回放的 Knowledge projection，不是 Objective Graph 的“当前可见切片”，也不是查询 Objective Graph 时临时套上的 mask。

Player 离开以后：

- 已确认的 Place 不会自动消失；
- 已确认的 Passage 不会因为当前看不见而删除；
- “T 时刻桥是开的”会逐渐变成可能过时的事实，而不是自动同步远端真值。

## 3.3 Believed Spatial Claims

传闻、纸质地图、推断、谎言和陈旧知识属于 claim：

```text
商人说森林里有一条密道。
旧地图声称河上有桥。
Alice 推测塔楼下面存在地牢入口。
```

这些 claim 可以来自 committed Observation，也可以来自 Player / LLM 提议后经规则接受并提交的 `ClaimAsserted` / `EvidenceRecorded` event。LLM 不能直接改 projection。这些 claim：

- 可以彼此矛盾；
- 必须保存来源；
- 可以被证实、反驳或保持未决；
- 不能直接创建 Objective Passage；
- 不能因为 LLM 相信它就升级为 Confirmed Spatial Projection。

首版不需要通用 RDF、本体推理器或贝叶斯信念网络。少量有类型的空间 claim 已经足够：

```text
PlaceExists
PassageConnects
PassageStateAt
```

未确认 claim 使用 player-local ClaimRef 和描述性 referent，不能携带尚未发现的 Objective PlaceId / PassageId。日后把 claim 与 Confirmed identity 关联，必须产生带 evidence 的显式 alias，而不是按名称字符串自动合并。

## 3.4 Human Spatial Realization

可选的 2D Grid、3D scene、文字描述、示意图或动画。

它负责：

- 把 Place 画成房间、城镇或地标；
- 把 Passage 画成道路、门、渡船或楼梯；
- 把旅行进度映射到画面；
- 为 Human 提供符合习惯的空间体验。

Human 也是 Player。Player-facing 地图、路线和 action list 必须读取与 AI Player 相同的 Confirmed Facts、Claims 与 Observation boundary。即使 renderer 为了绘制场景而加载了隐藏 geometry，也不能把它用于显示秘密入口或生成未授权 action。

Human realization 不能：

- 新增 Objective Graph 中不存在的捷径；
- 删除必须存在的 Passage；
- 改写权威 TravelDuration；
- 让视觉表现成为新的 Simulation authority。

## 3.5 所有权冻结

本文冻结以下 bounded-context 边界，不留到实现阶段猜测：

| Owner | 权威拥有 | 明确不拥有 |
|---|---|---|
| Objective Spatial | Area / Place / Passage definition，Entity location，Traversal 时间，global Passage enabled，perception candidate | PlayerId，Confirmed facts，Claims，Observation cursor，DecisionView |
| Perception | 把客观 candidate 与游戏感知规则解析成 player-scoped committed Observation | Objective topology mutation，Player belief |
| Spatial Knowledge Projection | 从 committed Observation / ClaimAsserted / Evidence event 确定性投影 Confirmed facts、ExitStub、带来源 Claims、evidence history 与 acknowledgement cursor | Objective truth，LLM 自由解释 |
| Game Activity | 权威拥有 TravelActivity、当前 intent、StopAtNextPlace 与自动续程意图 | Objective Spatial state，Knowledge projection |
| Player Composition | 纯读取某个 Player 的 Knowledge snapshot、Game Activity 与 committed Observation，构造 bounded DecisionView、route tools 与 semantic actions | 持久化 Activity/cursor，读取隐藏 Objective Graph 来补全结果 |

`Spatial Knowledge Projection` 可以在未来成为独立 project，也可以先由游戏层实现；但它在概念上已经是独立、event-sourced 的 authority，绝不能退化成 Objective query helper。

## 3.6 Objective 到 Decision 的因果栅栏

同一模型时间内，顺序必须是：

```text
Objective Spatial body committed
    ↓
Perception candidates resolved
    ↓
player-scoped Observation committed
    ↓
Confirmed facts / Claims projected
    ↓
DecisionView or deterministic continuation built
    ↓
Player DecisionPoint or explicit next traversal command
```

LLM 不能被调用在 candidate 与 Knowledge 更新之间。Milestone 只先产生客观 `PassageMilestoneReached`；它对某个 Player 的含义必须经过后续 Perception 与 Knowledge 处理。后续结果可以处在同一 ModelTime 的更晚 microstep，但不能倒流改变已经完成的 Spatial batch。

这条栅栏由 **composition coordinator** 权威拥有，不能依赖多个子系统以独立、同刻 Forecast candidate 竞争出来的偶然顺序。各阶段不得相互交错；若宿主不能把 lifting 与 projection 放入同一可信提交流水线，就必须在 Spatial commit 后先把控制权交还 Host，依次完成 Observation 与 Knowledge commit，再重新 Forecast 并决定是否提交下一条 traversal command。

---

# 4. 最小权威本体：Area、Place、Passage

为了控制复杂度，首版不公开 generic `Node`、generic `Edge` 或任意 property bag。

对内容作者和 Player 使用领域词汇：

```text
Area
Place
Passage
```

权威静态内容的最小边界是：

```text
SpatialGraphDefinition
    DefinitionId
    Revision
    RulesVersion
    ContentHash
    Areas[]
    Places[]
    Passages[]
    ViewLinks[]
```

## 4.1 Area：摘要和 containment，不是移动边

Area 用于表达层级和压缩：

```text
world
└── northern-realm
    ├── old-port
    ├── ash-forest
    └── monastery
```

候选定义：

```text
AreaDefinition
    AreaId
    ParentAreaId?
```

首版约束：

- 全部 Area 构成一棵有唯一 root 的树；
- Area 不是 Actor 可占据的位置；
- `Contains` 不参与寻路；
- 一个 Place 恰有一个 direct Area；
- 危险区、阵营领地、天气区等可重叠分类先作为游戏内容 tags，不成为第二套 containment authority。

在 Objective / authoring 边界内，这样可以稳定回答：

- 当前位于哪个城镇、地区和世界；
- 远端地图应折叠到何种粒度；
- LLM prompt 如何只展开当前 Area 附近的细节。

首版不把 Area 直接作为 Spatial destination。`去北境寻找 Alice` 是上层 Search / Travel Activity，需要从 Player 已确认的地点中选择具体下一目标；Objective Spatial 不能从 Area 的隐藏 descendants 中替 Player 挑选终点。

Objective `AreaPath` 只在 Entity `AtPlace` 时由 Place 的祖先链派生；`InTransit` 在 Spatial 核心中没有 Area membership。同 Area 从不等于 CoPresence。旅途中的区域描述由 PassageId 对应的 Game Content 提供。

Player-facing `AreaPath` 不能直接读取这条 Objective 祖先链，而必须来自 §11 中已经确认的 Area 与 containment facts。抵达 Place 是否同时确认某一级 ancestry，必须由显式 committed `SpatialContainmentConfirmed` 规则结果表达；不能顺手枚举未知 parent、sibling 或 descendant。

## 4.2 Place：可停留、可选择、可互动的原子 locality

Place 是空间 Graph 的 Node，但它不是任意几何点。

一个地点值得成为 Place，至少因为它满足一项：

- Actor 或 Object 可以稳定停留；
- 可以开始或完成语义行动；
- 到达会改变 Observation 或 available affordance；
- 是路线分岔、访问边界或旅程停靠点；
- 产品需要稳定的 Arrival、Departure 或共同见证 identity。

候选定义：

```text
PlaceDefinition
    PlaceId
    DirectAreaId
```

首版刻意不在 Spatial 核心中加入：

- 任意坐标；
- 朝向；
- 面积和形状；
- 任意 property dictionary；
- 名称和长篇描述；
- Inventory、Faction 或 Quest 条件。

名称、叙述、氛围、风险和 affordance 属于引用 `PlaceId` 的 Game Content。

### Place 粒度判据

以下通常不是 Place：

- 道路为了好看而拐弯的位置；
- 没有事件的第七块地砖；
- 只能路过、不能停留的风景点；
- 纯表现用的桥墩。

以下通常应当成为 Place：

- 可以决定走左路还是右路的岔口；
- 可以停下调查的石碑；
- 可以等待另一个 Actor 的驿站；
- 需要钥匙才能继续的门厅；
- 能与人质交谈的牢门前；
- 可能成为重逢地点的篝火营地。

## 4.3 Passage：带时间的有向旅行机会

Passage 是一等内容对象，不只是邻接表中的匿名 Link。

候选定义：

```text
PassageDefinition
    PassageId
    FromPlaceId
    ToPlaceId
    TravelDuration
    Milestones[]
    InitiallyEnabled
```

首版约束：

- Passage 有方向；
- 双向道路必须显式产生两个方向，不能从视觉布局猜测；
- 相同端点之间允许多条 Passage，例如公路、渡船和密道；
- Objective Graph 不要求平面嵌入；两条 Passage 在示意图上交叉不代表相通，只有共享 Place 才形成分岔或路口；
- TravelDuration 必须为正；
- 禁止 self-loop；
- Passage identity 全局稳定；
- Passage 可以连接不同 Area 的 Place；
- Passage 是否可用于新旅行是动态状态；
- 首版不动态修改 TravelDuration。

Actor 在途时的客观 locality 就是 PassageId。天气、风景、危险区和叙述作用域由引用 PassageId 的 Game Content 表达；首版不为一条跨境 Passage 再声明单一、可能失真的 `InteriorAreaId`。若跨越 Area 边界本身有玩法意义，应把边界做成 Place 或 milestone。

### Passage 不读取其它子系统

Spatial 核心只知道 Passage 客观是否 enabled。

它不直接读取：

- Actor 是否持有钥匙；
- 阵营是否允许通行；
- 船票是否有效；
- Quest 是否完成；
- Player 是否知道这条路。

这些规则由权威游戏行动验证器先裁决，再把一个明确的 `BeginTraversal(EntityId, PassageId)` 意图提交给 Spatial。Spatial 只验证自己的局部客观条件，例如 Actor 当前 AtPlace、Passage 起点匹配且 global enabled。

游戏组合层可以产生：

- Passage 当前对该行动是否可尝试；
- 一个授权的路线计划；
- 客观 Passage state mutation；
- 稳定的拒绝或受阻结果。

首版不在 Passage 中嵌入任意条件 DSL。

## 4.4 为什么不使用一类万能 Relation

以下关系不能共用一个没有语义的 `Edge(type=...)` 系统并期待上层自行猜测：

```text
can-travel-to
can-see
inside
near
heard-route-to
believes-connected-to
```

它们拥有不同的：

- authority；
- 生命周期；
- 可见范围；
- Replay 语义；
- Player 知识边界；
- 更新来源。

因此首版宁可提供少量强类型关系，也不追求通用 Knowledge Graph。

---

# 5. Actor 的权威位置：AtPlace 或 InTransit

长途 Passage 不能继续沿用“到期前仍在起点”的客观位置语义。

若 Alice 离开酒馆，沿北路旅行六小时，则这六小时里：

- 她不应继续参与酒馆的共处和互动；
- 她也尚未参与修道院的共处和互动；
- 客观事实应是她正在北路上。

为避免同一 traversal 在 EntityLocation 与 Travel Activity 中重复存储，候选状态把位置引用和 traversal 事实分开：

```text
EntityLocation
    AtPlace(PlaceId)
    InTransit(TraversalId)

TraversalState
    TraversalId
    EntityId
    PassageId
    StartedAt
    ArrivalDue
    NextMilestoneIndex
```

根状态还包含：

```text
SpatialGraphState
    DefinitionStamp
    Revision
    Entities[]
    Traversals[]
    PassageEnabledOverrides[]
    ScheduledPassageMutations[]
    NextTraversalOrdinal
    NextMutationOrdinal
    NextMomentOrdinal
```

任一已放置 Actor 在完整状态边界上恰好拥有其中一种位置。每个 `InTransit` 恰好引用一个属于该 Entity 的 TraversalState，每个 active TraversalState 也恰好被一个 EntityLocation 引用。From / To 由 PassageDefinition 唯一推导，不在动态状态中复制。

TraversalId 由持久 allocator 分配、不可复用。`ArrivalDue` 必须精确等于 `checked(StartedAt + Passage.TravelDuration)`；溢出必须在开始 traversal 前原子拒绝。

## 5.1 InTransit 不是连续物理模拟

首版不持久化：

- 浮点坐标；
- 每 tick 的 progress；
- 速度和加速度；
- 朝向；
- 碰撞体；
- 同路 Actor 的精确距离。

Human renderer 可以根据起止时间自行插值动画，但这个插值不是 Spatial 查询或 gameplay authority。Milestone 到期只由 committed `StartedAt + OffsetFromStart` 决定，不依赖浮点 progress。

## 5.2 Passage Milestone：途中事件阈值，不是隐藏 Place

候选定义：

```text
PassageMilestoneDefinition
    MilestoneId
    OffsetFromStart
```

约束：

- `0 < OffsetFromStart < TravelDuration`；
- 同一 Passage 内严格递增且唯一；
- 到期时总是提交结构化 `PassageMilestoneReached(EntityId, TraversalId, MilestoneId)`，并消费 NextMilestoneIndex；
- 不允许 Actor 在 milestone 上任意停留；
- 不允许从 milestone 分岔；
- 不把 milestone 当作 CoPresence locality。

判断规则：

> **如果一个途中点可以停留、调查、转向、等待、会面或成为移动目标，它就不是 milestone，而必须提升为 Place。**

Spatial 不解释 MilestoneId 的叙事含义。Game Content、Perception、Decision 与 Providence 分别以 MilestoneId 关联自己的规则；即使没有下游效果，Spatial 仍提交已到达并已消费的事实。这一规则既切断 generic callback / content DSL，也防止 Edge Progress 演化成另一套隐蔽 GridMap。

## 5.3 首版不支持任意路中停车

`Cancel` 在 InTransit 状态下含义模糊：

- 停在不存在的连续坐标？
- 返回起点？
- 立刻到终点？
- 生成一个临时 Place？

首版更诚实的上层 Travel Activity 语义是：

```text
StopAtNextPlace
```

即：

- 当前 Passage 继续完成；
- Spatial 到达 ToPlace 后总是结束这一次 traversal；
- Travel Activity 可以在到达后停止，也可以稍后显式提交下一条 Passage；
- Retarget 只更改上层 intent，不回滚当前 Passage；
- 若产品必须允许中途掉头，应把允许掉头的位置建成 Place，或在后续规则版本中显式加入新语义。

## 5.4 Passage 关闭不追溯已开始的普通旅行

普通 `SetPassageEnabled(false)` 影响新的进入，不自动取消已经开始的 traversal，也不取消其尚未消费的 milestone。

理由：

- traversal 的 ArrivalDue 已经是 committed 因果事实；
- 远端门关闭不应让已经在路上的 Actor 瞬移；
- 不需要在每个时刻重新解释剩余路程。

桥梁坍塌、船只沉没或强制拦截不是普通 enabled toggle，应由游戏规则提交显式的 traversal interruption 结果。首版可以把这种灾害列为非目标，而不是让普通 topology override 暗中承担它。

---

# 6. Travel Activity：Player 委托目标，Spatial 一次执行一条 Passage

LLM Player 应提交：

```text
前往旧港
去北境寻找 Alice
沿已知山路去修道院
在下一个驿站停下
```

不应提交：

```text
move node-17
advance progress 0.125
```

语义 Journey / Travel Activity 由 Game Activity 权威拥有，而不是 Objective Spatial 或纯读 Player composition 的第二套路径 authority：

```text
TravelActivity
    ActivityId
    PlayerId / EntityId
    Goal: ConfirmedPlaceId
    StopAtNextPlace
```

Spatial V1 只接受一条已经由上层授权的：

```text
BeginTraversal(EntityId, PassageId)
```

它不拥有 TravelActivity，不保存未来路线，也不在 arrival batch 内读取 Knowledge、Inventory 或 Player intent。

## 6.1 不把整条未来路径写成空间权威事实

Navigator 可以规划路线，但完整 path 不是权威状态。

Spatial 只需提交当前已经开始的 Passage：

```text
TraversalStarted
    EntityId
    TraversalId
    PassageId
    StartedAt
    ArrivalDue
```

`TraversalId` 必须等于并原子消费当前 `NextTraversalOrdinal`。对应的 `TraversalArrived(EntityId, TraversalId)` 必须精确匹配 Entity 当前 `InTransit` 引用；目标 Place 可以由 committed PassageDefinition 推导。这样 Reducer 不依赖 Kernel envelope 猜 aggregate identity，也不在 Replay 时重新分配 ID。

抵达 ToPlace 后，Objective Spatial 必须形成完整状态：

```text
EntityLocation = AtPlace(ToPlace)
TraversalState removed
```

随后才经过 §3.6 的 Observation / Knowledge 栅栏。完成栅栏后：

- 当前拓扑可能已经变化；
- Player 可能获得新信息；
- Travel Activity 可能要求停止或重新考虑；
- Player composition 可以显式提交下一条已授权 Passage。

因此后续 Passage 在需要时重新规划，不提前成为不可变历史，也不由 SpatialMoment 偷偷续发。

## 6.2 抵达一个 Place 不等于必须唤醒 LLM

Place 是客观决策可能点，不代表每次经过都值得一次昂贵 LLM turn。

抵达并完成 Knowledge projection 后，Player composition 可以：

- 若无新信息、无互动、原意图仍合法，则确定性地提交明确的下一条 Passage；
- 若出现新的 Observation、风险、阻断或选择，则请求 Decision；
- Player 可以选择 `ContinueCurrentIntent`；
- 内容作者可以把某些 Place 标记为 Journey checkpoint，但该标记属于游戏体验层。

“自动继续”可以发生在同一 ModelTime 的后续 microstep，但它仍是新的显式 command / event cause；Spatial arrival batch 自身已经结束。这样 Replay 不需要重新读取当时的 Knowledge 或重新决定授权。

## 6.3 已知路线与客观路线必须分开

最危险的知识泄漏不是把隐藏 Passage 打印到 Prompt，而是：

> Player 只说“去城堡”，系统却在 Objective Graph 上自动选择了它尚未发现的密道。

因此必须存在三个不同的操作面，不能用一个 `Passage eligibility view` 混合：

1. `ObjectiveGraphAnalyzer`：供内容验证、全知模拟或 Providence 使用，读取完整 Objective Graph；
2. `ConfirmedRoutePlanner`：只读取某个 Player 的 immutable ConfirmedSpatialSnapshot，使用 player-visible stable handles；
3. `ObjectiveTraversalValidator`：验证上层已经解析出的明确 Passage attempt 是否满足当前客观 Spatial 条件。

Player composition 负责把 `ConfirmedPassageId` 或 ExitStub capability 安全解析成 server-side traversal attempt。已确认路线失效时，应受阻或触发重新考虑，不能偷偷切换到未知捷径。内部 Law rejection 也不能原样投给 Player；Player-facing 原因只能来自随后合法产生的 Observation。

换言之：

> **没有输出隐藏路径，不代表没有泄漏；替 Player 使用隐藏路径本身就是泄漏。**

---

# 7. 导航：Objective 与 Confirmed 两张图使用同一确定性算法

底层算法可以相同，但输入类型和 authority 必须不同。

Objective analysis 的输入是完整 Objective Place / Passage snapshot；Confirmed planning 的输入只能是 player-scoped ConfirmedSpatialSnapshot。二者都可以归一为：

```text
Start Place
Goal Place
Authorized directed passages
```

Objective analyzer 使用权威 TravelDuration。首版中，一条 ConfirmedPassage 只有在 Knowledge Projection 同时保存了正的、player-owned duration estimate 时，才能进入 Confirmed Dijkstra；尚无估计的连接继续保持 ExitStub / Claim，或明确标记为不可用于 route planning，绝不能借用 Objective 数字。

Objective analyzer 输出精确 total duration；Confirmed planner 使用独立的 player-facing result contract，输出 Player 当前材料支持的估计与 assumptions：

```text
RouteFound / NoConfirmedRoute / CostOverflow
EstimatedTotalDuration
RouteAssumptions[]
FirstPassage: ConfirmedPassageId
```

首版继续采用确定性 Dijkstra 即可：

- Objective cost = 正 TravelDuration；Confirmed cost = player-owned positive estimate；未知且没有估计的 Passage 不能借用 Objective cost；
- 允许有向边和平行边；
- 无负权和零时长环；
- 相同 total duration 时使用各自 authority 内的稳定 Passage / Place key tie-break；Confirmed planner 不能让隐藏 Objective ID 参与排序；
- 路由选择不依赖集合插入顺序；
- Reducer 不重新寻路；
- Replay 只投影已经提交的 traversal 事实。

## 7.1 不使用 hop count 冒充距离

一扇门和一次跨海航行都可能只有一个 hop。

因此：

- hop count 只适合结构分析；
- Player ETA 与默认 fastest route 使用 TravelDuration；
- 风险、金钱和偏好是更高层 route policy；
- 首版不建立多目标路径偏好 DSL。

## 7.2 首版路线表达力的甜点

Player composition 首版可支持：

- 自动选择 Confirmed snapshot 中估计最快的路线；
- 明确指定下一条 Passage；
- `StopAtNextPlace`；
- 发现阻断后重新请求决策。

首版不支持：

- 任意自然语言 route constraint 编译器；
- 风险、隐蔽、景观、费用的通用加权 DSL；
- LLM 自己枚举完整路径；
- 持久化 path cache 作为第二真理。

---

# 8. 时间、动态拓扑与唯一 Spatial Moment

Graph-first 不改变 Kernel 的时间模型。

Spatial 仍应只向 Kernel 暴露一个最早候选：

```text
NextSpatialMomentDue
    = min(
        scheduled passage mutations,
        traversal next milestones,
        traversal arrivals)
```

## 8.1 同刻建议顺序

若在模型时间 `T` 有多项 Graph 空间工作，建议一个 Spatial Resolve batch 内按稳定 phase 处理：

```text
Phase 1
    应用 T 到期的 Passage state mutations

Phase 2
    消费 T 到期的 Passage milestones

Phase 3
    同时投影全部 T 到期 traversal arrival；每个 arrival 原子地删除 TraversalState 并把 Entity 置为 AtPlace

Phase 4
    在完整最终状态上计算 Area membership / Place CoPresence delta

Phase 5
    MomentResolved
```

Passage mutation at `T`：

- 影响 `T` 时刻以后新开始的 traversal；
- 不追溯取消已经在 Passage 上、同刻到达终点的 traversal；
- 不追溯取消既有 traversal 尚未消费的 milestone；
- 会被 §3.6 栅栏以后新提交的 traversal command 看到。

Spatial batch 内不存在自动续程，因此 milestone 的外部后果不会倒流进入当前 Phase 1，也不会在 Knowledge 尚未更新时偷偷改变下一条路线。

## 8.2 Milestone 是真实时间工作

Milestone 不是查询时临时重算的叙事装饰。

每个到期 milestone 都必须通过 Forecast 在准确 ModelTime 提交 `PassageMilestoneReached`，即使没有下游后果。Game / Perception 后续可以让它导致：

- 新 Observation；
- World Event；
- DecisionPoint；
- Providence 可观察信号；

这些后果使用后续 cause / microstep，不在 Replay 时重新解释 MilestoneId。

## 8.3 Candidate 与顺序不变量

概念上必须保证：

- candidate 绑定 definition stamp、state revision、moment ordinal 与最早 Due；
- Forecast 不返回 overdue candidate；
- 同一 `(Due, PassageId)` 不允许两个结果不同的 scheduled mutation；
- mutation 以稳定 MutationId 排序；
- milestone 与 arrival 以稳定 EntityId、TraversalId、milestone index 排序；
- derived family 和 key 使用稳定总序；
- 非空 Resolve 恰有一个严格位于最后的 `MomentResolved`；
- resolved work count 等于 Resolve 开始时到期的 mutation、next milestone 与 arrival 数量。

## 8.4 同刻全部 Arrival 先完成，再计算关系

多个 Actor 在同一时刻抵达、离开或交换 Place 时：

- 不按逐 Actor 中间态产生抖动；
- 先投影全部 arrival；
- 再比较 pre / final state；
- CoPresence 只反映完整最终事实。

这延续 Grid Spatial 已验证的同刻批处理原则。

---

# 9. Interaction locality 与多 Actor 语义

首版最简单、最可靠的 locality 是：

```text
两个 Actor 都 AtPlace(P)
    → spatially co-present
```

以下不自动产生 CoPresence：

- 同属一个 Area；
- 同时 InTransit 且引用同一 Passage；
- Passage 时间区间有重叠；
- 一人在起点、一人在途中；
- 一人从 Place 离开、另一人在同刻抵达但最终没有共同停留。

## 9.1 普通 Actor 不阻挡

与 Grid V1 一致：

- 多个 Actor 可以共处一个 Place；
- Actor 不占用 Passage capacity；
- 普通旅行互不阻挡；
- 敌我、碰撞和排队不是 Spatial 核心概念。

## 9.2 首版不自动计算同路相遇

“同时在同一条十公里山路上”不等于能看见或交谈。

自动计算相向相遇会立即引入：

- Passage 几何长度；
- 方向和速度；
- 追及、超车和掉头；
- 相遇点持久身份；
- 两两 candidate 和去重；
- 零时循环风险。

因此首版明确：

- 同 Passage traversal 不自动可见；
- 不自动产生 PassedOnPassage；
- Party / Convoy 由游戏侧 shared activity 表达；
- 伏击、路遇和同行见闻属于引用 Passage / MilestoneId 的 Game event，不是 Spatial 推导的相遇；首版也不让这类事件暂停或改写当前 traversal；
- 必须可靠会面的地点应建成 Place。

这是有意识牺牲几何涌现，换取确定性和可解释性。

---

# 10. 感知：从几何 LOS 转向显式空间披露

Graph 不能从 adjacency 自动推导 visibility。

首版建议区分：

```text
same-Place perception candidate
directed ViewLink
Passage milestone candidate
```

## 10.1 Same-Place candidate

若两个 Entity 同处一个 Place，它们可以成为彼此的客观感知候选。

但最终是否：

- 注意到；
- 识别身份；
- 看清物品；
- 受到伪装或黑暗影响；

仍由 Perception / Game rules 决定。

Spatial 不直接写入 Player Belief。

## 10.2 Directed ViewLink

为了支持“先看见、后到达”，可以定义受限的有向视野关系：

```text
ViewLinkDefinition
    ViewLinkId
    FromPlaceId
    TargetPlaceId
```

含义是：

> 位于 FromPlace 的观察者，可以把 Target 作为客观感知候选。

它不意味着：

- 两地可通行；
- Target 必然被注意；
- Player 知道怎样到达；
- 观察者知道 Target 的全部属性；
- 反方向也可见。

首版只允许 `Place → Place`。路线相关的远景必须建成 Place；纯风景、某个 Entity 的识别和具体描述由引用 ViewLinkId 的 Perception / Game Content 解释。这样 Spatial 不需要验证一个外部 Landmark registry。

## 10.3 Passage milestone candidate

旅途中可以在准确时刻产生：

```text
你第一次看见山谷里的修道院。
远处塔楼窗口似乎有人影。
河对岸有一座桥，但这里没有通路。
```

Spatial milestone 只提交客观 `PassageMilestoneReached`。后续 Perception 可以据此产生 player-scoped Observation；它不直接宣告 Player 的最终解释，也不能跳过 §3.6 的因果栅栏。

## 10.4 “没有看到”不等于“不存在”

Player-facing 系统必须区分：

```text
currently not visible
explicitly observed absent
never observed
previously observed but may be stale
```

Spatial 不建立通用 coverage / negative-visibility engine。只有 Perception 或授权 Game rule 提交 committed `AbsenceEvidence` 后，Knowledge 才能据此更新 claim；Knowledge 只消费 evidence，不能自己证明自己的判断。一次离开 viewpoint 只结束 current-visible，不删除 Confirmed facts，也不证明目标不存在。

---

# 11. Confirmed Spatial Projection：被确认的地图不是全图遮罩

Spatial Knowledge Projection 应保存稳定的 player-scoped identity 和 evidence 边界。

## 11.1 已确认 Area 与 containment

为了在不读取 Objective Graph 的情况下提供层级摘要，Projection 最少需要：

```text
ConfirmedArea
    ConfirmedAreaId

ConfirmedContainment
    ChildConfirmedAreaOrPlaceId
    ParentConfirmedAreaId
    EstablishedAt
```

这些记录只能由 committed `SpatialContainmentConfirmed` 建立，使用 player-scoped opaque identity。Player-facing `AreaPath` 与 `inspect_confirmed_area` 只读取这份 projection；确认当前 town 不会自动披露未知 region、siblings、descendants 或 Objective AreaId。

## 11.2 已确认 Place

Place 可以通过以下方式被知道：

- 亲自抵达；
- 从 ViewLink 或 milestone 直接看见；
- 由显式、committed `SpatialIdentityConfirmed` 类规则结果建立身份关联。

地图、传闻和他人陈述无论看起来多可信，首版一律先进入 Claims；不能用 confidence threshold 自动升级为 Confirmed。

“知道某地存在”不等于“知道怎样到达”。

## 11.3 已确认 Passage

Passage 可以通过：

- 亲自走过；
- 在 Place 观察到入口并确认目标；
- 由显式、committed 规则结果确认连接。

Confirmed route planner 只能使用该 Player 已确认、带正 player-owned duration estimate 且已获 action capability 的 Passage。

这里的 capability 只代表稳定命名与“Player 有理由尝试”的 attempt authority，由 committed Knowledge / action evidence 授予；它不证明 Passage 当前客观 enabled，不证明钥匙、阵营或其它 Law permission，也不能因未感知的远端变化而被悄然撤销。实际 permission 只由权威 validator 在 attempt 时判断。

对 Player 暴露的 `ConfirmedPlaceId / ConfirmedPassageId` 是稳定、player-scoped 的 opaque handle。Projection 在服务端私下关联 Objective ID；未知、根本不存在和无权访问的 handle 对工具调用返回不可区分结果，避免枚举探测。

## 11.4 ExitStub：知道有出口，但不知道终点

需要表达：

```text
这里有一扇上锁的门。
森林里有一条向北的小路。
港口有一艘明早启航的船。
```

但 Player 可能不知道它通往哪个 Objective Place。

因此 Confirmed Spatial Projection 需要 `ExitStub`：

```text
ExitStub
    ExitStubId
    ConfirmedFromPlaceId
    DescriptionRef
    LastObservedAt
    ObservedState
```

ExitStub 的 player-facing serialization 不携带隐藏 endpoint。用于执行 `ExploreExitStub` 的 objective correlation / capability 必须进入 server-side committed SpatialKnowledge state 与 Journal，随 Replay / Fork 恢复；它只是不能进入玩家可见序列化。任何消费、失效或 alias 也必须由 committed event 表达，command resolver 只能读取这份状态，不能成为第二个 owner。同一入口重复观察应复用稳定 ExitStubId；确认终点后提交显式 resolved / alias evidence，旧 StubId 仍保留在历史中。

## 11.5 动态状态必须带观测时间

Player 看到的不是：

```text
bridge.enabled = true
```

而是：

```text
在 T=120 时，你看见 bridge 是开放的。
```

若 T=300 远端桥梁关闭，但 Player 没有合法感知：

- Confirmed Projection 不能自动同步；
- Travel attempt 仍可基于旧知识提出；
- Objective validator 可以内部拒绝，但 Player-facing 粗粒度原因只能来自当下合法感知；内部 Law reason 不能原样泄漏；
- AvailableAction 不能因为隐藏 Objective 变化而悄然消失。

这正是世界能够对 Player 说“不”的必要条件。

---

# 12. 面向 LLM 的 Spatial Decision View

即使 Confirmed Spatial Projection 很小，也不应在每个 DecisionPoint 全量塞入 Prompt。

每次只提供有界、局部、与当前决策相关的视图：

```text
SpatialDecisionView
    Where
        CurrentPlace / ActivePassage
        ConfirmedAreaPath?        // only when AtPlace and supported by confirmed containment

    TravelActivity
        CurrentGoal
        CurrentPassage
        ExpectedArrival

    NewObservations
        Deltas since last decision

    LocalExits
        Confirmed Passage / ExitStub
        LastObservedState
        EstimatedDuration

    RelevantDestinations
        Confirmed route summary
        Route assumptions

    RelevantClaims
        Claim
        Source
        Status

    AvailableSemanticActions
```

## 12.1 Prompt 规模必须与局部相关性相关

一个关键验收不变量是：

> 在 Player 完全未知的远端增加 10,000 个 Place，不应改变当前 SpatialDecisionView。

Prompt 大小应近似：

```text
O(local exits + new observations + relevant goals + relevant claims)
```

而不是：

```text
O(total objective world size)
```

相关性选择、排序和截断只能读取：

- 该 Player 的 immutable Knowledge snapshot；
- 已 committed 的 local Observation；
- 当前 Player intent。

不能用 Objective 距离、Objective 真伪、隐藏 mutation 或全知 path 来决定“什么最相关”。每类记录必须有固定 cap、稳定 player-visible key 排序与不泄漏总数的 continuation cursor。超限 Observation 必须分页或确定性摘要，不能静默丢弃。

`NewObservations` 的 replayable cursor 由 Spatial Knowledge Projection 权威拥有；Player composition 构造或 inspect DecisionView 都是纯读，只能提交 acknowledgement command，由 committed acknowledgement event 推进 cursor。

验收可使用：

- record 数量上限；
- UTF-8 bytes 上限；
- 序列化结果稳定性；

而不绑定某个模型 tokenizer。

## 12.2 远端信息通过工具按需展开

LLM 可以使用只读工具：

```text
inspect_confirmed_area(areaId)
inspect_confirmed_place(placeId)
inspect_exit(exitId)
plan_confirmed_route(destinationId)
inspect_spatial_claim(claimId)
```

这些工具：

- 只读取 Player 已授权的 Confirmed / Claim material；
- 不读取完整 Objective Graph；
- 对“没有已知路线”返回诚实结果；
- 返回 route assumptions 和信息新鲜度；
- 不因工具调用暴露隐藏节点数量或 ID；
- 只接受 player-scoped opaque capability handles；
- 对猜测的未知、无权和根本不存在 ID 给出不可区分的结果与稳定 timing class；
- 分页不返回 Objective total count。

## 12.3 候选语义行动

Player composition 首版可提供：

```text
TravelToConfirmedPlace
TraverseConfirmedPassage
ExploreExitStub
SeekRouteToSeenPlace
InvestigateSpatialClaim
AskAboutPlaceOrRoute
ContinueCurrentIntent
StopAtNextPlace
```

这些是 Player / Game semantic actions，不是全部都要进入 Spatial command handler。`Available` 表示 Player 有理由尝试，不表示 Objective 世界保证成功。

---

# 13. Graph Content 的作者表示

Graph 对 Coding Agent 友好的原因，不只是 JSON 比 Grid 简短，而是设计意图成为显式对象。

候选内容：

```json
{
  "schema": "dramaboard.spatial-graph/1",
  "id": "pilgrimage-road",
  "revision": 1,
  "rulesVersion": 1,
  "areas": [
    { "id": "world", "parent": null },
    { "id": "north-valley", "parent": "world" }
  ],
  "places": [
    { "id": "village-gate", "area": "north-valley" },
    { "id": "cliff-overlook", "area": "north-valley" },
    { "id": "monastery-gate", "area": "north-valley" },
    { "id": "hostage-tower", "area": "north-valley" }
  ],
  "passages": [
    {
      "id": "village-to-overlook",
      "from": "village-gate",
      "to": "cliff-overlook",
      "travelDuration": 30,
      "initiallyEnabled": true,
      "milestones": []
    },
    {
      "id": "overlook-to-monastery",
      "from": "cliff-overlook",
      "to": "monastery-gate",
      "travelDuration": 90,
      "initiallyEnabled": true,
      "milestones": [
        {
          "id": "first-view-of-hostage-tower",
          "offsetFromStart": 20
        }
      ]
    }
  ],
  "viewLinks": [
    {
      "id": "overlook-sees-hostage-tower",
      "from": "cliff-overlook",
      "target": "hostage-tower"
    }
  ]
}
```

这里的 JSON 只展示信息形状，字段名和 duration 编码不是当前冻结的软件 schema。它是 authoring source，不应被运行时直接当作未验证状态。

它仍需经过：

```text
parse
→ schema validation
→ stable identifier validation
→ reference resolution
→ containment validation
→ graph validation
→ duration / milestone validation
→ canonicalization
→ immutable compiled definition
→ content hash
```

长篇地点描述、角色材料和叙事文本应放在独立 Markdown / localization content 中，由稳定 ID 引用，不塞进核心 Graph。

---

# 14. 面向 Coding Agent 的操作面

Graph-first 的一个直接收益，是 Agent 可以做小而可验证的修改：

```text
添加一个 Place
在两个 Place 间增加单向 Passage
把旧路分解为两个 Passage 和一个营地 Place
增加一条尚未被 Player 知道的密道
调整旅行时间
增加一个途中 milestone
让某个 Place 把远端 Place 作为感知候选
检查删除 Passage 是否破坏可达性
```

未来 CLI / MCP 应围绕语义操作，而不是文件重写：

```text
create_graph_from_template
inspect_place_or_passage
add_or_split_place_and_passage
set_passage_duration
add_milestone_or_view_link
analyze_reachability
preview_patch
validate_graph
```

每次 patch 应返回：

- 直接改变的 Place / Passage；
- 可达性变化；
- 最短时间变化；
- 是否影响已有稳定 ID 和 content hash。

Scenario invariant、Knowledge leakage 和 semantic diff 需要组合 Scenario / Knowledge context，不能由裸 Spatial Graph 工具假装单独证明。本文也不设计具体 CLI、MCP schema 或编辑事务协议。

---

# 15. Graph 到 Human GridMap 的关系

Graph→Grid 不是首版运行依赖，也不是本文第一验证目标。

未来若实现，它更接近：

```text
Objective Graph
    → Place placement
    → Passage orthogonal routing
    → visual scene generation
    → conformance validation
```

首版 Graph→Grid 可以只承诺：

- 不新增可操作 Passage；
- 不删除源 Passage；
- Human 行动仍提交 Graph command；
- Human map / affordance 仍服从同一 Confirmed / Claim boundary；
- Grid 只负责动画和选择映射；
- Graph state 是唯一 authority。

不要一开始声称完整 gameplay bisimulation。

若未来 Grid 本身允许 Human 逐格操作，就需要另写 Human realization Design Note，定义窄的 entry / exit abstraction 与 player-visible trace contract。本文不承诺任意有向 Graph 都能被一般 2D Grid 完整等价实现，也不让未来的美术约束反过来塑造 V1 Spatial。

---

# 16. 权威不变量

## 16.1 Definition

- Area 构成唯一 root 的无环树；
- 每个 Place 属于恰一个 direct Area；
- Passage endpoints 必须存在；
- PassageId、PlaceId、AreaId、MilestoneId 稳定且唯一；
- Passage 有向、正耗时、无 self-loop；
- 同一 Passage 的 milestone offset 严格递增且位于内部；
- ViewLink 有向、两端 Place 必须存在，且不能暗示 Movement connectivity；
- canonical hash 不依赖输入集合插入顺序；
- RulesVersion 明确约束运行时解释。

## 16.2 Dynamic State

- 一个 Entity 恰好 `AtPlace` 或 `InTransit(TraversalId)`；
- 每个 active TraversalState 恰好被所属 Entity 引用一次；
- Traversal 的 Passage 必须存在且起点与开始前 AtPlace 精确匹配；
- `ArrivalDue == checked(StartedAt + Passage.TravelDuration)`；
- 每个 milestone due 精确等于 `checked(StartedAt + OffsetFromStart)`；
- NextMilestoneIndex 与已消费事件一致；
- TraversalId 正值、稳定、不可复用；
- 开始 traversal 必须原子创建 TraversalState 并把 Entity 置为 InTransit；
- 移除 InTransit Entity 必须原子移除其 TraversalState；
- Passage enabled override 不改写 definition；
- 动态状态不持久化派生 progress、Area ancestry 或完整 route cache。

## 16.3 Event / Replay

- Reducer 不重新寻路；
- Reducer 不调用 LLM；
- Reducer 不从当前 Grid realization 重建拓扑；
- milestone、arrival 与 mutation 由 committed absolute time 决定；
- `TraversalArrived` 原子删除 traversal 并把 Entity 放到目标 Place，不留下双重位置 prefix；
- 同刻 body state 完整后才计算 derived relation；
- Replay 不重新决定 Player 当时是否知道某条路；
- Grid realization 改变不能改变既有 Graph Journal 的结果。

## 16.4 Knowledge Boundary

- Objective Graph 不整体暴露给 Player；
- Player-facing AreaPath / Area tool 只读取 ConfirmedArea 与 ConfirmedContainment，不能从 Objective ancestry 补全；
- 未感知的远端 mutation 不更新 Confirmed Spatial Projection；
- rumor / map claim 不直接创建 Objective 或 Confirmed Passage；
- visibility removed 不等于遗忘或 observed absent；
- Confirmed route planner 不使用 hidden Objective Passage；
- Objective、无权与不存在的 handle 不能通过错误类型、数量或排序被探测；
- 未知世界扩容不改变当前 bounded Decision View。

---

# 17. 首版明确非目标

为了守住 Graph-first 的甜点位置，首版不做：

- 通用 RDF / ontology / property graph engine；
- 连续坐标、NavMesh 或自由移动；
- 精确路中停车；
- 同 Passage 追逐、超车、迎面相遇或碰撞；
- Passage capacity 和 reservation；
- actor-specific movement condition DSL；
- actor-specific speed / dynamic traversal-duration profiles；
- 动态修改正在 traversal 的 duration；
- arbitrary runtime Place / Passage creation；
- 从 Graph 自动推导几何 LOS；
- 火、水、声音或爆炸的几何扩散；
- 战术掩体、队形、射程或 Area of Effect；
- 自动遗忘和概率 belief truth maintenance；
- 全 Confirmed Graph 每回合注入 Prompt；
- Human GridMap 编辑器；
- Graph→Grid 的完整玩法双模拟或强等价证明。

特殊战术空间未来可以作为某个 Place 关联的局部 metric scene，但不得在首版预先为它设计通用框架。

---

# 18. 可证伪的验收场景

新本体不能只靠类型图证明。至少需要以下产品级实验。

## 18.1 先看见但不可达

```text
Actor 抵达 cliff-overlook
→ 看见 hostage-tower
→ ConfirmedPlace 增加
→ 没有 Confirmed Passage 到达它
```

应允许：

- Investigate；
- SeekRoute；
- AskAbout；

不应出现：

- 保证成功的 TravelTo；
- 自动使用隐藏 Objective route。

`plan_confirmed_route(hostage-tower)` 必须精确返回 `NoConfirmedRoute`。即使 Objective Graph 中存在一条隐藏可达路径，以上完整 player-visible 输出也不改变。

## 18.2 隐藏捷径

Objective Graph 有一条 secret passage。

发现前：

- Prompt 不出现；
- AvailableAction 不出现；
- confirmed-route planner 不使用；
- 不能从 ID 数量推断。

验收分三阶段比较完整 bytes：

1. 未发现：没有任何相关 handle；
2. 听到传闻：只增加 player-local claim 与 Investigate，不出现 Objective PassageId；
3. 亲自发现：才建立 ConfirmedPassage 与 Traverse capability。

## 18.3 错误地图与冲突来源

旧地图声称河上有桥，向导说桥在上游。

系统必须：

- 保留两个带来源 claim；
- 不修改 Objective Graph；
- 不用 last-write-wins 抹除冲突；
- 只有明确 committed evidence 能将 claim 标记为 Confirmed 或 Refuted；
- Refuted claim 保留来源和历史，不 last-write-delete；
- 假桥 claim 不需要创建一个假的 Objective Place / Passage。

## 18.4 远端动态封路不泄漏

Player 在 T0 看见道路开放。

T1 远端道路关闭，但 Player 没有感知。在同一个 ConfirmedSpatialSnapshot 下，关闭前后的以下输出必须字节级相同：

- confirmed-route result 与 ETA；
- action IDs；
- DecisionView；
- route tool result。

Actor 到达能够合法感知入口以后，才提交 Observation 并更新 Projection。内部 rejection code 不能提前披露远端原因。

## 18.5 Passage milestone

Actor 沿长路旅行，在准确 ModelTime 经过石碑：

- 石碑若只能看见，保留 milestone；
- 若允许停下调查，测试必须失败并要求将其提升为 Place；
- Journal 不产生每个表现 Cell 的无意义 step；
- Spatial 先提交 `PassageMilestoneReached`，Perception 后提交 player Observation，Knowledge 再投影，最后才允许唯一 Decision；
- Replay 不重新解释 milestone content。

## 18.6 StopAtNextPlace

Actor 正在 Passage 上时改变主意：

- 不瞬移回起点；
- 不生成任意连续坐标；
- 当前 traversal 正常抵达；
- 在下一个 Place 停止并触发需要的 Decision。

## 18.7 层级与 prompt 压缩

World 包含：

```text
world → region → town → interior
```

只有已经 committed 的 ConfirmedArea / ConfirmedContainment 能形成当前 AreaPath、展开本地 exits 或提供远端摘要。Objective ancestry 不得补全 Player view；未知 parent、sibling 与 descendant 不得被枚举。`Contains` 不能成为寻路捷径。

即使 Objective 中有 Player 未知的 Place 属于目标 Area，Confirmed planner 也不能把它当成隐藏终点；首版没有 AreaGoal。

## 18.8 未知世界规模不影响本地视图

在 Player 完全未知、不可感知的分支增加 10,000 Place：

- 当前 Observation 字节级不变；
- action IDs 不变；
- confirmed-route result、排序与工具 cursor 不变；
- 完整 player-visible serialization 字节级不变，而不仅是大小不增长；
- Objective definition hash 正常改变。

## 18.9 Replay / Fork

在 traversal 中途 milestone 后 Fork：

- 相同 committed state 得到相同 next SpatialMoment；
- Replay 不重新寻路；
- 不重新调用 LLM 解释 milestone；
- Fork 同时复制 Confirmed / Claim evidence 与 Observation acknowledgement cursor；
- 同一 opaque handle 及其 server-side correlation 在 Replay / Fork 后仍解析为同一个 authorized attempt，但任何 player-visible bytes 都不含 Objective ID；
- 两支在新 evidence 出现前完全相等，之后才合法分叉；
- 是否存在 Grid realization 不影响 Graph Journal。

## 18.10 发现后遮蔽

Player 已确认洞口后，雾或伪装使它当前不可见：

- current-visible 可以结束；
- ConfirmedPlace / Passage 与 last-observed state 保留；
- 不产生 absent 或 forgotten；
- 雾散后的新 Observation 更新 timestamp，不创建第二个 identity。

## 18.11 两个 Player 的知识隔离

同一 Objective 世界、同一 ModelTime：Alice 已发现密道，Bob 未发现。

- 两份 Confirmed projection、tools、actions 与 DecisionView 必须不同；
- 共享 route cache 不得把 Alice 的结果泄给 Bob；
- Bob 听到 Alice 的陈述时只获得 sourced claim，除非游戏规则另行建立确认。

## 18.12 工具 ID 探测

LLM 枚举或猜测 secret Objective IDs：

- 未知、无权和不存在 handle 返回不可区分结果；
- 不暴露 Objective ordinal、总数、pagination count 或不同错误类型；
- 合法 opaque handle 的稳定性不受未知世界扩容影响。

## 18.13 同 Passage 重叠不产生相遇

两名 Actor 反向遍历同一物理道路对应的 Passage，时间区间重叠：

- 不产生 CoPresence；
- 不产生 Passed / Saw / Encounter；
- 不制造虚构 Place；
- 只有显式 Game milestone / event 可以产生沿途见闻。

这个测试证明首版主动牺牲了同路几何相遇，而不是把语义留在模糊地带。

## 18.14 Not-visible 与 AbsenceEvidence

Player 离开河岸 viewpoint，不得自动反驳“这里有桥”的 claim。只有 Perception 或授权 Game rule 提交 committed `AbsenceEvidence` 后，Knowledge 才能改变 claim status；Knowledge 只消费 evidence，Spatial 本身也不运行 coverage 推理。

## 18.15 截断与相关性不泄漏

Confirmed exits、claims 或 observations 超过 DecisionView cap：

- 使用 player-owned stable key 排序；
- continuation cursor 不暴露 Objective 总数；
- 新增隐藏 Objective 内容或改变 claim 的客观真值不改变 top-N；
- 只有新增 committed player evidence 才能按确定规则改变结果。

## 18.16 Grid-free slice

完整 Scenario 必须能够在没有 GridMap、坐标或 LOS 的情况下运行：

- Actor 导航；
- 动态封路；
- 途中披露；
- 先见不可达；
- Confirmed route planning；
- LLM Decision；
- Replay / Fork。

这是验证 Graph 真正成为 Ground Truth，而不是 Grid 的辅助索引。

## 18.17 Arrival 后续程栅栏

Actor 在 T 抵达一个此前只知道入口、尚未确认内部结构的 Place：

- `TraversalArrived` 先提交完整 `AtPlace` objective state；
- 该 Place 的 perception candidate、committed Observation 与 Confirmed projection 全部完成后，才能规划下一 Passage；
- deterministic continuation 必须读取 post-arrival Knowledge snapshot，不能读取 pre-arrival snapshot；
- 下一条 `BeginTraversal` 是 T 的后续显式 command / cause，不能藏在 arrival batch；
- 即使 Objective Graph 存在更短的隐藏出口，也不能被自动续程使用；
- 在 observation pipeline 未完成前，不得调用 LLM 或发布旧的 DecisionView。

---

# 19. 主要风险与未决问题

## 19.1 Graph 是否过度压缩 locality

若“整座城市”只有一个 Place，则旅馆房间、市场和城门会错误地成为同一互动 locality。

解决方向不是给 Place 加任意子坐标，而是拆成少量真正有互动意义的原子 Place，并用 Area 折叠摘要。

## 19.2 InTransit 是否值得首版复杂度

完全不建模 InTransit，会让长途 Actor 在客观上一直留在起点。

本文选择：

- 保留 InTransit + 单一 TraversalState；
- 不持久化连续进度；
- 不支持任意停车；
- 不推导同路相遇。

这是时间真实性与模型简单性之间的甜点。

## 19.3 ViewLink 会不会重新长成 LOS DSL

应限制 ViewLink 只表达作者明确需要的远端披露候选。

若开始加入：

- 角度；
- 高度；
- 距离衰减；
- 任意 blocker geometry；

就说明需求已经进入局部 metric scene，不应继续扩张 Graph Spatial。

## 19.4 Player-visible identity correlation

§3.5 已冻结：Confirmed facts 与 Claims 由独立、event-sourced Spatial Knowledge Projection 拥有，而非 Objective Spatial。

剩余风险是 player-local handle 与 Objective identity 的安全关联：

- 未确认 claim 不能携带隐藏 Objective ID；
- ExitStub 必须可执行，却不能序列化秘密 endpoint；
- 同一入口重复观察必须复用 identity；
- claim 与后来确认的 Place / Passage 需要显式 alias evidence；
- Fork 必须复制 correlation 与 acknowledgement cursor。

这些关系必须由 committed projection events 建立，不由 LLM 猜测或按字符串自动合并。

## 19.5 Actor-specific access

“Alice 有钥匙、Bob 没有”会让同一 Passage 对两者可用性不同。

首版不把 Inventory/Faction DSL 塞进 Spatial。本文已经选择一个窄 seam：

```text
Objective topology
+ authoritative game action validation
+ explicit BeginTraversal(EntityId, PassageId)
```

剩余工程风险是跨子系统同刻原子性：必须确保 action validator 的 non-spatial precondition 与提交给 Spatial 的明确 attempt 属于同一个可信协调流程，并在 Journal 中记录当时实际接受的结果。不能让 Spatial 在 Replay 时重新读取 Inventory，也不能把内部失败原因直接当作 Player Observation。

## 19.6 Arrival 后立即继续是否构成会合

本文选择：每次 `TraversalArrived` 都形成一个真实、完整的 `AtPlace` committed boundary。Perception / Knowledge 必须先处理这个 Place 的新候选，composition 才能在后续 microstep 自动继续。

因此若另一 Actor 确实停留在该 Place，arrival 可以产生真实的 same-Place perception 和 reconsideration；它不是 Spatial batch 内不可见的 transient prefix。若内容作者不希望某个纯路线点具有这种会合意义，就不应把它建成 Place，而应使用 milestone 或保持为 Passage 内部表现。

---

# 20. 第一个创新性验证切片

建议先设计一个完全没有 GridMap 的“小而密”旅程：

```text
Areas
    World
    Valley
    OldPort
    Monastery

Places
    VillageGate
    CliffOverlook
    ForestFork
    RoadsideShrine
    FerryLanding
    OldPortMarket
    MonasteryGate
    HostageTower

Passages
    8–12 条有向通路
    一条隐藏捷径
    一条定时关闭的渡船
    一条包含 milestone 的长路
```

角色：

- Human Player；
- Companion AI；
- 一个远端 Antagonist 或 Rule Actor。

必须产生：

- Companion 与 Human 拥有不同 Confirmed Spatial Projection；
- 一方先看见但无法到达 HostageTower；
- 一方听到错误路线传闻；
- 动态封路不会向不知情 Player 泄漏；
- AI Player 使用 confirmed-route tool 作出计划；
- Passage milestone 触发一次有意义的 reconsideration；
- Replay / Fork 能比较“透露路线”和“不透露路线”的后果。

为了不把 Objective、Knowledge 和 LLM 调试混成一个巨型首包，实验分成三个 gate：

```text
Gate A — Objective
    Area / Place / Passage
    AtPlace / InTransit
    单 Passage arrival / mutation / milestone
    Replay / Fork

Gate B — Epistemic
    两个 Player 的不同 Confirmed facts
    一个 false claim
    无泄漏 confirmed-route tool
    全程先不用 LLM

Gate C — Product
    Human + LLM Decision
    milestone reconsideration
    hidden route disclosure
    Fork outcome comparison
```

每个 gate 独立通过后再进入下一个。开发期只需要：

- JSON Graph；
- Graphviz / ASCII 调试图；
- CLI validation；
- 文本化 SpatialDecisionView；
- Journal 检查。

不需要：

- Tile art；
- Grid compiler；
- Human map editor；
- 战术移动；
- 大规模 procedural generation。

这个 Slice 的问题不是“自动生成的地图好不好看”，而是：

> **LLM Player 是否能用更少、更清晰、更不泄漏的信息，形成真正属于自己的空间计划，并在世界拒绝、披露和变化时合理重新考虑。**

---

# 21. 对现有 `src/Spatial` 的执行层影响

本节刻意放在最后。它记录候选方向，但不在本文裁决具体重构方案。

当前 Grid Spatial 已经验证了大量仍然有价值的机制：

- 单向依赖 Kernel；
- immutable definition 与 content hash；
- RulesVersion gate；
- event-sourced state；
- stable identifier 和 total order；
- Command batch；
- Journey 中已验证的 ID、generation 与 stale candidate 设计经验；
- scheduled topology mutation；
- 唯一 earliest SpatialMoment；
- 同刻批处理；
- scratch transition 与正式 reducer 一致；
- Replay / Fork / split-run；
- normal Actor non-blocking；
- pre / final derived relation delta。

真正发生变化的是空间本体：

| Grid-first | Graph-first |
|---|---|
| `GridMapDefinition` | `SpatialGraphDefinition` |
| `CellRef` | `AtPlace / InTransit(TraversalId)` |
| Orthogonal Cell edge | Explicit Passage |
| Portal special edge | 普通有向 Passage / traversal profile |
| Cell MoveCost | Passage TravelDuration |
| Cell / Portal override | Passage enabled override |
| Anchor | PlaceId |
| Zone cells | Area ancestry 或 game tags |
| CurrentLeg cell step | Passage traversal |
| `EntityStepped` | `TraversalStarted / PassageMilestoneReached / TraversalArrived` |
| Same Cell | Same Place |
| StrictSupercover LOS | Same-place candidate / ViewLink / milestone lifting |

后续软件工程至少有三条候选路径：

## 21.1 就地重构 `src/Spatial`

优点：

- 最终只有一套权威 Spatial；
- 可复用现有 Kernel seam 和测试思想；
- 不长期维护双系统。

风险：

- `CellRef`、CurrentLeg、LOS 和大量 event payload 都是 breaking change；
- 在新本体尚未经过产品 Slice 验证前，可能过早破坏已完成基线。

## 21.2 并行引入实验性 Graph Spatial

例如暂时建立独立 project 或 prototype namespace。

优点：

- 能用最少代码验证 Graph-free Slice；
- 不必先兼容旧 event schema；
- 容易比较两种 Player experience。

风险：

- 两套系统可能复制 Kernel integration；
- 若不设退出条件，会形成长期双权威。

## 21.3 先做最薄的研究原型，再决定归宿

只实现：

- immutable graph definition；
- AtPlace / InTransit；
- deterministic navigator；
- milestone / arrival Forecast；
- bounded player view；
- 一个真实 LLM Slice。

原型验证后再裁决：

- 就地替换；
- 新 project 接管并归档旧 Spatial；
- 或保留 Grid Spatial 作为局部 metric scene engine。

本文倾向第三条，但不把它升级为执行计划。

无论选择哪条路径，都必须遵守：

> **同一运行中的同一空间事实只能有一个 authority。不能让 Actor 同时权威地位于 Graph Place 和 Grid Cell，再靠同步维持一致。**

---

# 22. 外部研究与已有经验

Graph-first 空间的基本思想并非凭空出现。

Benjamin Kuipers 的 Spatial Semantic Hierarchy 将大型空间知识组织成多个相互作用的层次，其中拓扑层以 places、paths 和 regions 表达大尺度结构，度量地图只在需要时建立。它尤其强调不同表示支持不同推理，并允许 Agent 在部分知识下工作：

- Benjamin Kuipers, *The Spatial Semantic Hierarchy*, Artificial Intelligence 119, 2000.
  <https://web.eecs.umich.edu/~kuipers/research/pubs/Kuipers-aij-00.html>

Hybrid Spatial Semantic Hierarchy 进一步采用“全局拓扑 + 局部度量”的组合，这为未来在特殊 Place 内挂接局部战术空间提供了比全世界 Grid 更自然的演进方向：

- Kuipers et al., *Local Metrical and Global Topological Maps in the Hybrid Spatial Semantic Hierarchy*, ICRA 2004.
  <https://www.cs.utexas.edu/~qr/papers/Kuipers-etal-icra-04.html>

游戏内容生成研究也长期探索从 action / mission graph 生成空间 layout，而不是直接从格子反推玩法结构：

- van der Linden, Lopes, Bidarra, *Designing Procedurally Generated Levels*, AIIDE 2013.
  <https://ojs.aaai.org/index.php/AIIDE/article/view/12592>

若未来实现 Graph→Grid，正交 Graph Drawing 与 EDA placement/routing 提供了成熟的实现素材；但它们优化的是折弯、面积、交叉和拥塞，不会自动保证 DramaBoard 的信息披露与 Player knowledge 边界：

- Roberto Tamassia, *On Embedding a Graph in the Grid with the Minimum Number of Bends*, 1987.
  <https://doi.org/10.1137/0216030>

本文真正需要验证的新组合是：

```text
LLM-first semantic action
+ Objective / Confirmed / Claimed spatial separation
+ positive-time Passage
+ Kernel Forecast / Journal / Replay / Fork
+ Providence / Scenario mutation
+ optional Human realization
```

---

# 23. 阶段性结论

Grid-first Spatial 的核心问题不是算法不成熟，而是它默认从 Human 图形游戏的表现粒度出发，再向上提取 AI Player 所需的语义。

Graph-first 选择反过来：

1. 先定义对 Player 有意义的 Place；
2. 用 Passage 定义机会、时间和路线；
3. 用 Area 压缩大型世界；
4. 用 milestone 和 ViewLink 明确设计信息披露；
5. 用 Confirmed Spatial Projection 和 claims 保存主体差异；
6. 只把局部、相关、可行动的信息交给 LLM；
7. 让 Kernel 保存旅程和变化的真实时间；
8. 最后才为 Human 编译视觉空间。

一句话总结：

> **DramaBoard 的地图首先应当是 Player 能够理解、误解、探索和据此作出计划的世界关系；只有在需要服务 Human 表现时，它才进一步成为一幅几何地图。**

如果这一方向成立，DramaBoard 就不再只是“让 LLM 操作传统 RPG 地图”，而是在建立一种从本体、信息边界、时间模型到内容工具都以 LLM Player 为一等服务对象的 RPG-like 世界。
