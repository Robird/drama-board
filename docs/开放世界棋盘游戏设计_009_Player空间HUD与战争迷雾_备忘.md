# Design Note 009：Player 空间 HUD、战争迷雾与主观地图备忘

**状态：拆分备忘；暂不进入详细设计或实施**

**日期：2026-08-20**

**来源：** 从 [Design Note 008](./开放世界棋盘游戏设计_008_Graph_Spatial_World.md) 拆出。

---

# 1. 为什么拆分

`ConfirmedSpatialProjection` 最接近的成熟前人设计不是客观世界状态，而是 RTS 的战争迷雾与地图残影：

- Player 看见过某个地点；
- 离开视野后，地图可能保留最后一次观测；
- 残影不代表最新事实；
- 不同 Player 拥有不同地图；
- HUD 展示什么，不等于 Player 真正记得或相信什么。

因此这部分不属于 Graph Spatial World。

Graph Spatial World 只拥有：

- Graph Definition；
- Actor 位置与移动；
- Objective navigation；
- objective visibility opportunities；
- Kernel 中的客观空间事实。

Player-facing 层未来可以消费这些事实，但不能反向成为 World authority。

---

# 2. 候选分层

```text
Graph Spatial World
    objective location / movement / visibility opportunities
        ↓
Game / Perception
    把客观候选解释为某个 Player 实际收到的 Observation
        ↓
Player Spatial HUD / Helper
    战争迷雾、地图残影、路线辅助、笔记
        ↓
Human UI / LLM adapter
```

反向行动：

```text
Human / LLM intent
    → Game validator
    → explicit Spatial command
```

Spatial World 不读取 HUD、Player memory 或 belief。

候选 owner 与因果流水仅作备忘：

```text
Spatial World objective event / opportunity
    → Game / Perception produces player-scoped Observation
    → HUD / Fog projection updates disclosed material
    → Player-facing view or deterministic TravelActivity continuation
    → Game validator submits a new explicit Spatial command
```

高层 TravelActivity、Player intent、抵达后的继续 / 取消 / 重规划属于 Game / Player-facing 层。它们不能藏在 Spatial arrival batch 中，也不能让 Spatial reducer读取 Player memory。

---

# 3. HUD/Helper 不是 Player 大脑

未来实现应谨慎命名。

`ConfirmedSpatialProjection` 容易让人误以为系统权威拥有“Player 相信什么”。更准确的候选名称包括：

- `PlayerSpatialNotebook`；
- `SpatialDisclosureLedger`；
- `FogOfWarMap`；
- `PlayerMapHelper`。

它最多权威保存：

- 系统曾向 Player 披露什么；
- 上次观察某个动态状态的时间；
- HUD 当前画出的地图残影；
- Player 主动保存的地图注记；
- 可供路线工具使用的有限材料。

它不应声称：

- Player 一定记得；
- Player 一定相信；
- LLM 一定正确理解；
- 最后观测仍是当前事实；
- rumor 已经变成 World truth。

LLM 可以忽略、误解、怀疑或推翻 HUD 材料。真正的心智模型属于 Player 自身。

---

# 4. 从 008 移出的候选主题

以下主题保留为未来研究清单，本轮不冻结 schema：

## 4.1 战争迷雾与残影

- 当前可见；
- 曾经可见；
- last observed state / time；
- 远端状态变化后残影过时；
- visibility ended 不等于对象不存在；
- 只有显式 AbsenceEvidence 才可能支持“这里没有某物”，not-visible 本身不构成反证；
- 是否遗忘。

## 4.2 Player-scoped 地图 identity

- Player-local Place / Passage handle；
- Objective identity 的 server-side correlation；
- 同一入口重复观察的 identity reuse；
- HUD save / load / Replay / Fork；
- 多 Player 隔离。
- 已披露 Area 与 containment 如何形成 player-facing 层级摘要，而不枚举未知 ancestry / sibling。

## 4.3 未知出口

- ExitStub；
- 知道有出口但不知道终点；
- Explore / Return；
- 抵达后 identity resolution；
- 不泄漏 Objective endpoint。

## 4.4 Claims 与地图注记

- rumor；
- 错误地图；
- sourced claim；
- conflict / refute；
- Player 自己的推断；
- HUD 笔记与 Player belief 的区别。

## 4.5 Player-facing navigation

- 在 HUD Graph 上寻路；
- stale travel-time estimate；
- direction estimate / capability 是否以 `(PlayerPassageHandle, FromPlaceHandle)` 为 identity；
- no-known-route；
- 不使用 Objective hidden Passage；
- route assumptions。

## 4.6 DecisionView 与工具

- bounded local map；
- inspect place / route / claim；
- pagination 与 anti-enumeration；
- Human / LLM 共享安全 view-model；
- Prompt token budget。

## 4.7 Human realization

- Graph→Grid / 2D / 3D / text presentation；
- 玩家可见动画；
- HUD 与 renderer 的信息边界；
- Graph command mapping；
- 不让表现层成为第二套 topology。

Human realization 可能最终独立成另一份 Design Note；本文只记录它与 HUD/Fog 的 player-facing 接缝。

## 4.8 TravelActivity 与抵达续程

- 高层 destination / route intent 的 owner；
- Arrival 形成完整 AtPlace boundary 后，谁决定继续、取消或重规划；
- 自动续程必须是新的显式 Spatial command；
- Observation / HUD 更新与下一次决策的先后；
- Replay 时不重新运行当时的策略决策。

## 4.9 在途安全投影与能力

- opaque traversal receipt；
- World traversal 与 player-facing handle 的 durable correlation；
- Continue / Return / unknown-forward 的安全表达；
- 不披露 Objective Offset、endpoint、Length 或 Due；
- capability 只允许尝试，不承诺当前 World 一定接受；
- Arrival / Return 后 receipt 的终止与 alias。

---

# 5. 当前不做的决定

本备忘现在不裁决：

- 是否 event-sourced；
- 是否进入 Kernel；
- 具体 Fog tile / Graph data structure；
- Confirmed、Disclosed、Known 的最终术语；
- claim schema；
- confidence 或 trust；
- identity alias 算法；
- knowledge sharing；
- forgetting；
- LLM memory policy；
- Human UI；
- persistence format；
- network replication。

这些问题不应阻塞 Graph Spatial World V1。

---

# 6. 当前实施约束

在 HUD/Fog 详细设计之前：

- Graph Spatial Runtime 不出现 PlayerId；
- Spatial State 不保存 seen / known / confirmed；
- Visibility API 只返回 objective opportunity；
- Spatial Navigator 只提供 Objective query；
- MockPlayer 可以读取全 Objective Graph；
- RandomWalker 不需要记忆；
- Replay 只重放 live run 已提交的 committed events，不重新运行随机决策或命令处理器；
- Human / LLM 产品验收不属于首个 Spatial World gate。

首个 World vertical slice 使用 deterministic MockPlayer、SeededRandomWalker 和可选“大鱼吃小鱼”薄 Game rule，足以验证地图、位置、移动、可见机会、导航与时间集成。

---

# 7. 未来重新启动本设计的触发条件

满足以下任一条件时，再把本备忘升级为正式 Design Note：

- 需要真正的 Human HUD；
- 需要 LLM 只基于已披露地图导航；
- 两个 Player 必须看到不同地图；
- 动态世界需要 last-seen 残影；
- 需要错误地图、传闻或显式地图笔记；
- 需要未知出口探索；
- 需要保存、Replay 或 Fork Player-facing 地图；
- Objective API 已可能被 Player-facing 工具误用。

届时最重要的边界仍然是：

> **HUD 可以保存世界向 Player 展示过什么，但不能替 Player 定义它记得什么、相信什么或怎样思考。**
