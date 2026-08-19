# Design Note 007：Spatial Framework
## ——单向依赖 Kernel 的统一分层四向二维网格空间子系统

**状态：首版已实现，设计与实现同步**

**日期：2026-08-19**

**实现位置：`src/Spatial`；验收位置：`tests/Spatial.Tests`**

**定位：** 定义 DramaBoard 首版框架层空间子系统的职责、依赖方向、统一网格模型、时间语义、事件边界、确定性约束与首批验证实验。

本文承接：

- Design Note 002 对“世界不能属于表现引擎”和 Kernel 纯确定性边界的要求；
- Design Note 003 的 `Forecast → Advance → Resolve → Decide` 时间模型；
- Design Note 006 对语义行动、Interrupt / Reconsider、主观 Observation 与伴生旅程的产品要求；
- 当前 `src/Kernel` 已实现的 `ISimSystem`、全量 re-Forecast、event-sourced reducer、Journal batch 与 superdense time 语义。

本文不是完整 GDD，也不要求首版空间系统承担战斗、潜行、认知或表现引擎的职责。

---

# 1. 阶段性决定

首版采用：

> **由多张有限四向二维网格组成的统一空间；正常移动只沿四方向格边发生，Portal 是唯一的非局部或跨地图连接。**

同一套模型同时用于：

- 世界地图；
- 地区地图；
- 城镇；
- 室内；
- 地下空间；
- 独立楼层；
- 特殊事件地图。

不同地图可以具有不同的：

- 单格逻辑尺度；
- 移动耗时；
- 视野半径；
- 地形成本；
- 语义 Zone 与 Anchor。

但它们不使用不同的空间代数。

权威位置始终是：

```text
CellRef
    MapId
    X
    Y
```

首版进一步固定：

- 只允许 North / East / South / West 四方向普通移动；
- 不存在斜向移动与对角穿角；
- 普通 Actor 默认不阻挡其他 Actor；
- 一个格可以同时容纳多个 Actor；
- 移动由语义目标启动，Spatial 内部逐格推进；
- Player 不逐格决策；
- 逻辑位置只在 committed 空间事件后改变；
- 表现层可以在格间插值，但插值位置不是世界真理；
- Spatial 单向依赖 Kernel，不依赖 Protocol、Host、FirstBoard 或表现引擎；
- Kernel 不增加任何空间特判。

这是一项有意的范围收缩。

它牺牲：

- 斜向移动；
- 连续位置；
- 真 3D；
- NavMesh；
- Actor 身体碰撞；
- 精细局部避障；
- 连续轨迹上的相遇。

换取：

- 一套坐标；
- 一套导航；
- 一套移动时间语义；
- 一套 Portal 规则；
- 一套视野算法；
- 一套 Replay 契约；
- 更小的测试空间；
- 更容易解释给 Human 与 AI Player 的世界。

首版的目标不是证明 DramaBoard 可以重新实现成熟 RPG 的所有空间能力，而是验证：

> **当 Human 与 AI Player 共享同一个有时间、局部知识和长期后果的世界时，游戏是否会产生值得继续发展的共同经历。**

---

# 2. Spatial 在总体架构中的位置

## 2.1 依赖方向

建议新项目：

```text
src/Spatial/Spatial.csproj
```

其唯一项目依赖为：

```text
DramaBoard.Spatial
        ↓
DramaBoard.Kernel
```

完整依赖关系可表示为：

```text
Kernel ← Spatial

Kernel + Spatial + Protocol + Host
                 ↑
        CompanionGame / Board

Presentation 读取游戏状态与 Spatial 查询结果，
但不拥有权威空间状态。
```

`DramaBoard.Spatial` 不引用：

- `DramaBoard.Protocol`；
- `DramaBoard.Host`；
- `DramaBoard.FirstBoard`；
- AI Player 或 LLM SDK；
- Godot、MonoGame 或其他表现引擎；
- Combat、Relationship、Inventory 等具体游戏领域；
- IO、wall-clock、环境变量或全局可变状态。

Kernel 也完全不知道 Spatial 的存在。

## 2.2 Kernel 与 Spatial 的职责分工

Kernel 只负责：

```text
收集所有子系统 Forecast
→ 选择全局最早 candidate
→ 推进 ModelTime
→ Resolve 一个事件 batch
→ Commit Journal
→ Reducer 投影世界
→ 全量重新 Forecast
```

Spatial 只负责回答：

```text
下一个空间变化时刻是什么？
该时刻全部空间工作合起来会发生什么？
这些 committed spatial events 如何更新 SpatialState？
```

因此 Spatial 是：

> **Kernel 时间模型下的一个离散事件子系统。**

它不是另一个游戏循环，也不拥有独立时钟。

## 2.3 “统一处理事件流逝”的准确含义

当前 Kernel 没有也不需要：

```text
Elapse(dt)
Update(dt)
Tick()
```

Spatial 不会在 Kernel 跳过一段 ModelTime 时偷偷修改位置。

正确语义是：

```text
Spatial Forecast 下一个空间变化
↓
Kernel 将 ModelTime 推进到 Due
↓
Spatial Resolve 到期的全部空间工作
↓
Kernel commit SpatialEvent batch
↓
SpatialReducer 更新 SpatialState
```

所以：

> **Spatial 统一预测空间事件，并通过 committed spatial events 表达时间流逝后的离散状态变化。**

空间状态仍然只有一条写入路径：

```text
Committed SpatialEvent
    ↓
SpatialReducer
    ↓
SpatialState'
```

---

# 3. 为什么采用统一四向网格

## 3.1 四方向不是单纯少四个邻居

由八方向收缩为四方向，会同时消除或缩小：

- 斜向穿过两个阻挡格之间的 corner-cutting；
- 直走成本与斜走成本的差异；
- `sqrt(2)`、定点近似或特殊整数权重；
- 两个 Actor 沿对角线交叉但没有共格的情形；
- 八方向 facing 与动画资产；
- 更复杂的地图通道宽度判断；
- 大量导航与 replay 边界测试。

首版的普通网格邻接严格为：

```text
(x, y - 1)  North
(x + 1, y)  East
(x, y + 1)  South
(x - 1, y)  West
```

枚举顺序固定为：

```text
North → East → South → West
```

该顺序属于确定性契约，不由容器插入顺序决定。

## 3.2 世界地图也使用网格的收益

首版不再同时维护：

- 高层任意 Place / Route 图；
- 低层 Grid 或 NavMesh；
- 两种位置类型；
- 两套寻路与轨迹算法。

世界地图同样是一张有限 GridMap。

例如：

```text
┌────┬────┬────┬────┐
│山地│山道│北塔│荒野│
├────┼────┼────┼────┤
│森林│道路│道路│旧港│
├────┼────┼────┼────┤
│沼泽│青石镇│农田│海岸│
└────┴────┴────┴────┘
```

Actor 在世界地图移动时仍然使用同一个：

```text
CellRef + MoveGoal + CurrentLeg
```

一格可以代表几公里、一个道路区段或一个有持续时间的交互地域。

## 3.3 统一不等于一张无限大地图

首版是：

> **Grid of Maps，而不是 one global grid。**

每张地图有自己的 `MapId`、尺寸与比例。

不同地图：

- 不要求坐标对齐；
- 不要求比例严格嵌套；
- 不共享一个全局像素坐标；
- 不从父地图坐标推导子地图坐标；
- 不形成递归几何变换系统。

Actor 任一时刻只存在于一张地图的一个权威格中。

Actor 进入镇内地图后，不再同时占据世界地图上的“青石镇格”。

两者只通过 Portal 关联。

---

# 4. 统一空间仍然是 Grid-dominant Graph

“全部采用网格”不能被误解为没有显式图边。

真实拓扑是：

```text
四方向普通格边
    +
少量显式 Portal 边
```

因此更准确的名称是：

> **Grid-dominant graph。**

## 4.1 Portal 的职责

Portal 是唯一允许：

- 跨 Map；
- 非相邻格连接；
- 单向连接；
- 楼层切换；
- 桥面与桥下切换；
- 城镇入口；
- 地下入口；
- 船、传送点和特殊捷径；

的空间机制。

概念结构：

```text
PortalDefinition
    PortalId
    FromCell
    ToCell
    TraversalDuration
    InitiallyEnabled
```

首版约束：

- `PortalId` 稳定且唯一；
- Portal 可以单向；
- 两端必须是合法 `CellRef`；
- 普通 Portal 耗时必须为正；
- 开放 / 关闭状态显式存在于 SpatialState；
- Portal 不嵌入任意脚本回调；
- Portal 不读取 Inventory、Faction、Relationship；
- 首版视线不穿过 Portal；
- 双向 Portal 必须被规范化为明确的双向定义或两条有向边。

“是否拥有钥匙”不是 Spatial 的问题。

产品规则可以在确认开门条件后，提交：

```text
SetPortalState(portalId, enabled: true)
```

Spatial 只判断 Portal 当前是否 enabled。

## 4.2 不提供零耗时 Portal 环

普通移动边和普通 Portal 边都必须具有正 `ModelDuration`。

若未来确实需要 teleport：

- 在发起命令的同一个 external-input batch 内原子完成；
- 不把它表示为可循环 Forecast 的零耗时 Portal。

这样可以避免同一 ModelTime 的无限空间循环与 resolve budget 耗尽。

---

# 5. 静态定义与动态状态

## 5.1 SpatialDefinition

不可变空间内容建议集中为：

```text
SpatialDefinition
    DefinitionId
    Revision
    ContentHash
    SpatialRulesVersion
    Maps
    Portals
    Anchors
    Zones
```

`SpatialDefinition` 是 Scenario Definition 的一个组成部分。

它在一个 Run 中不可变。

SpatialSubsystem 构造时绑定一个定义；`SpatialState` 保存对应的 `DefinitionId / ContentHash / SpatialRulesVersion`，恢复、Replay 或 Fork 时必须匹配。

修改地图后，不允许旧 Journal 在没有版本声明的情况下使用新地图重新解释。

`ContentHash` 由 `SpatialDefinition` 对规范化内容自行计算，不接受调用方声称的 hash。Map、Portal、Anchor、Zone 与 Zone cell 在编码前都按稳定总序排列；相同内容不因输入集合的插入顺序不同而得到不同 hash。

`ContentHash` 只锁定地图内容；`SpatialRulesVersion` 另行锁定：

- 邻接与边成本公式；
- Dijkstra tie-break；
- LOS corner rule；
- 同刻 phase 顺序；
- CurrentLeg 到期与中断语义。

定义加载时必须至少验证：

- `Width / Height > 0`；
- `OrthogonalStepDuration > 0`；
- `VisionRange >= 0`；
- Cell 数量等于 `Width × Height`；
- Map、Portal、Anchor、Zone ID 全局唯一；
- Anchor、Zone cell 与 Portal endpoint 都是合法 `CellRef`；
- Portal duration 为正整数；
- 所有参与排序的 ID 具有跨进程稳定总序。

## 5.2 GridMapDefinition

```text
GridMapDefinition
    MapId
    Width
    Height
    OrthogonalStepDuration
    VisionRange
    Cells[]
```

这里不需要把地图硬编码成：

```text
WorldMap / TownMap / InteriorMap
```

它们只是不同内容配置，不应产生三套运行时代码分支。

## 5.3 CellDefinition

首版格子只需要：

```text
CellDefinition
    TerrainId
    MoveCost
    BlocksMovement
    BlocksSight
```

Zone membership 只由 `ZoneDefinition.Cells` 权威定义；SpatialDefinition 可以建立可丢弃的 per-cell Zone 索引。不能同时让 `CellDefinition.ZoneIds` 成为第二套真理。

约束：

- `MoveCost` 为正整数；
- 所有时间换算使用 checked integer arithmetic；
- 静态格内容运行中不变；
- 临时道路封闭、门、烟雾墙等使用动态 override，而不是修改 Definition。

## 5.4 SpatialState

动态状态概念上包含：

```text
SpatialState
    DefinitionId / ContentHash / SpatialRulesVersion
    Revision
    NextMomentOrdinal
    NextJourneyOrdinal
    NextMutationOrdinal
    Entities
    Journeys
    PortalOverrides
    CellOverrides
    ScheduledMutations
```

首版的空间 Entity 只表示具有一个客观位置、可参与空间关系的 Actor-like 对象：

```text
SpatialEntityState
    EntityId
    Cell
    ObservationEnabled
    MovementGeneration
```

所有已放置 Entity 都参与 Zone、CoPresence，并可作为几何可见目标；`ObservationEnabled` 只表示 Spatial 是否需要为它产生几何可见差量，不表示它真的注意、识别或记住了目标。`MovementGeneration` 是当前 Entity placement lifetime 内的移动调度代数。门、墙、烟雾、路障不建模为 Entity，而是 Cell / Portal definition 或 override。

`NextJourneyOrdinal` 与 `NextMutationOrdinal` 是持久 allocator，已完成 Journey 或已消费 mutation 也不能导致 ID 复用。所有 allocator 与 revision 的递增都使用 checked arithmetic。

首版不持久化 Zone membership、CoPresence pair、visible cells 或 visible entity contacts。它们都由 committed definition + state 纯推导；需要事件差量时，由统一 transition 对 pre/post 投影作比较。

`SpatialState` 具有不可变值语义。

外部项目不能直接修改其集合。

所有更新必须来自 `SpatialReducer`。

内部可以在 profiler 证明必要时使用 copy-on-write 或其他优化，但不能改变纯函数语义。

---

# 6. Anchor 与 Zone：保留 AI Player 所需的语义空间

统一网格解决的是几何和时间，不解决语义寻址。

裸坐标：

```text
map-3 / (17, 23)
```

对 AI Player 没有稳定意义。

因此首版必须保留 Anchor 与 Zone。

## 6.1 Anchor

```text
AnchorDefinition
    AnchorId
    Cell
```

例如：

- `town.bluestone`；
- `town.bluestone.north-gate`；
- `old-port.warehouse`；
- `camp.river-hill`。

首版一个 Anchor 对应一个确定格。

## 6.2 Zone

```text
ZoneDefinition
    ZoneId
    Cells[]
```

Zone 可以：

- 重叠；
- 跨多个连续格；
- 表达市场、森林、危险区、私人区域；
- 产生 Entered / Left 空间事实。

Zone 不直接改变 Player Knowledge、敌意或剧情。

## 6.3 Spatial 接受的目标

首版可支持：

```text
CellGoal
AnchorGoal
ZoneGoal
```

但 Player Boundary 原则上只暴露语义目标：

```text
MoveTo(AnchorId)
MoveTo(ZoneId)
CancelMovement
```

LLM 不提交：

```text
CellRef
Direction
Path
Waypoint[]
```

游戏 Controller 负责：

- 判断目标是否属于 Actor 合法已知信息；
- 把 Intent 翻译成 SpatialCommand；
- 把 Spatial 结果翻译成主观 Observation；
- 决定是否产生 DecisionPoint。

Spatial 的客观 Pathfinder 可以使用完整客观地图，但完整地图、隐藏 Anchor 和计算路径不能因此泄露给 Player。

---

# 7. Entity、共格与阻挡

## 7.1 Spatial 不认识敌我

Spatial 不读取：

- 阵营；
- 关系；
- 敌意；
- Combat 状态；
- Player 类型；
- Actor 性格。

这些都属于上层游戏规则。

## 7.2 普通 Entity 默认不阻挡

首版直接固定：

> **普通移动实体永不因为“占着一个格”而改变 passability。**

因此：

```text
CellRef → EntityId[]
```

是正常状态。

收益包括：

- 队伍同行不需要格预约；
- 非敌对 Actor 不互相堵路；
- 追随不需要局部避让；
- Actor 数量不改变其他人的最短路；
- 不需要交换、推挤、死锁解决；
- 世界地图一格可以容纳多个旅行者。

同格只表示：

> **多个 Entity 共享同一个粗粒度 interaction locality。**

它不自动意味着：

- 碰撞；
- 谈话；
- 敌对；
- 战斗；
- 已互相注意；
- 必须产生 DecisionPoint。

Spatial 可以产生客观：

```text
CoPresenceStarted
CoPresenceEnded
```

上层 Encounter / Perception / Game Rule 决定其意义。

这里的 Entity 集合有意保持为 Actor-like 空间参与者，因此不会为门、道具、区域标记或表现代理生成无意义 pair。若第二个真实用例需要把普通物件放入 Spatial，再增加纯空间参与标志，而不是提前引入敌我或玩法分类。

## 7.3 显式阻挡

真正需要阻挡时，必须来自：

- `BlocksMovement` 地形；
- 关闭的 Portal；
- 动态 Cell blocker；
- 显式 `BlockPassage` 一类上层 Activity 投影出的空间障碍。

例如“守住门口”不应因为 Actor 恰好站在门格而自动成立。

上层规则应明确把这个意图投影成 Spatial 能理解的 blocker 或 Portal state。

`BlocksMovement` 的首版语义是“禁止进入目标格”，不是“禁止从所在格离开”。因此某格在 Actor 已位于其中后变为 blocked，不会把 Actor 永久困住；Orthogonal 与 Portal 都只检查目标格以及边自身是否可用。需要禁足时应使用显式 movement interrupt 或其他上层规则。

首版不引入：

- 每地图 OccupancyPolicy；
- 每格 Capacity；
- Actor footprint；
- Reservation；
- 局部避让。

等真实战术玩法出现后，再增加第二个用例。

---

# 8. MoveGoal、Journey 与 CurrentLeg

## 8.1 一次语义命令，内部逐格推进

概念流程：

```text
Player / Rule Actor 提交语义 Intent
↓
Game Controller 校验主观 affordance
↓
SpatialCommand: AssignMoveGoal
↓
Spatial 验证客观可达性
↓
JourneyStarted 或稳定 rejection code
↓
Spatial 内部逐格运行
↓
到达 / 阻断 / 空间信息变化
↓
Game 决定是否要求 Player 重新决策
```

Player 不会收到：

```text
move north
move east
move east
...
```

复古的是空间底层，不是 Player 输入粒度。

## 8.2 JourneyState

```text
JourneyState
    JourneyId
    EntityId
    Goal
    Generation
    CurrentLeg
```

合法 active Journey 始终恰有一个非空 `CurrentLeg`。成功 step 的细粒度 reducer prefix 暂时保留刚完成的旧 leg，此时唯一允许的不完整形态是 `Entity.Cell == old CurrentLeg.To`；随后必须由 continued / completed / blocked 修复。实现不使用“先清空 leg、稍后补回”的中间态，合法 commit / Fork 边界也不能留下 step prefix。

新目标、取消、强制中断都会改变 Entity 的 `MovementGeneration`；active Journey 与 CurrentLeg 保存相同 generation。

旧 candidate 在全量 re-Forecast 后自然消失，不需要 cancellation API。

命令边界固定为：

- `AssignMoveGoal` 只接受没有 active Journey 的 Entity；已有 Journey 必须显式 `RetargetMoveGoal`。接受时递增 Entity 的 MovementGeneration；
- 若 Entity 当前格已经满足 Cell / Anchor / Zone goal，仍分配并消费一个 JourneyId、递增 MovementGeneration，并直接记录 `JourneyCompleted(AlreadySatisfied)`；不创建零时长 leg 或悬空 Journey；
- `RetargetMoveGoal` 先在当前 working state 上验证目标并规划完整新 leg；若不可达，整个命令无 SpatialEvent、旧 Journey / CurrentLeg 原样保留；若成功，才递增 MovementGeneration 并原子替换旧 leg。Entity 留在 `From`，旧 leg 已投入时间不折算为新 leg 进度；若新目标已由当前格满足，则保留原 JourneyId、不消费 allocator，以 `JourneyCompleted(RetargetedAlreadySatisfied)` 单事件递增 generation 并结束 Journey；
- `CancelMoveGoal` 与 `InterruptMovement` 递增 MovementGeneration、清除 CurrentLeg、结束 Journey，Entity 留在 `From`；
- `RemoveEntity` 必须在同一权威事件中移除它的 active Journey；
- 这些转换都递增 Journey generation，使旧 candidate 失效。

## 8.3 CurrentLeg

首版不保存完整权威路径，只保存当前已经开始的一步：

```text
CurrentLeg
    From
    To
    EdgeKind        // Orthogonal / Portal
    PortalId?
    StartedAt
    Due
    JourneyGeneration
```

这样：

- 无关事件导致 re-Forecast 时，`Due` 不会漂移；
- 不会每次用 `now + duration` 把抵达不断向后推；
- 动态门和道路变化自然影响后续 hop；
- 不需要持久化或失效整条路径；
- Replay 不重新运行历史 Pathfinder。

每完成一格后，Spatial Resolve 在临时 post-step state 上重新寻找下一 hop，并把完整下一 `CurrentLeg` 写入 committed event。

世界很小，首版可以接受逐格重新寻路。

`SpatialReducer` 只应用 event 中已经决定的：

```text
From / To / EdgeKind / PortalId? / StartedAt / Due / JourneyGeneration
```

它不能在 Replay 时重新调用 Pathfinder。这样未来导航算法升级也不会重写旧 Journal 的历史结果。

## 8.4 移动一步期间 Actor 在哪里

首版明确采用粗粒度语义：

> **CurrentLeg 到期以前，Entity 的唯一权威位置仍是 `From`；到期事件 commit 后，位置原子变为 `To`。**

这意味着 `CellRef` 是 interaction locality，不是身体的连续物理坐标。

step duration 表示“从 `From` locality 完成离开的耗时”，不是已经获得连续进度的边遍历。

移动中的 Entity：

- 仍可在 `From` locality 中被空间查询找到；
- 仍参与 `From` 的 CoPresence 与几何 LOS；
- 尚不参与 `To` 的 CoPresence 与几何 LOS；
- 具有 active Journey，因此不视为 idle；
- 上层规则可以限制它在移动中发起本地动作；
- 可以被显式中断；
- 中断时留在 `From`；
- 不存在“仍保存起点，但用 `IsPresent=false` 使其从世界消失”的特殊状态。

Presentation 可以根据：

```text
From / To / StartedAt / Due
```

平滑插值。

插值结果不参与：

- Encounter；
- LOS；
- Reachability；
- Journal；
- Replay。

这是首版明确接受的近似。

如果世界地图上一格几十分钟导致严重交互歧义，应优先：

- 缩小世界格的语义范围；
- 增加道路中间格；
- 调整 step duration。

只有真实实验仍无法接受时，才引入 `TraversingEdge` 和边上相遇，而不是预先增加连续轨迹。

## 8.5 CurrentLeg 到期时的通行检查

当前 leg 的绝对 `Due` 不因地图变化而移动。

leg 开始后发生的 MoveCost 变化不追溯修改其 `Due`；新成本只用于替代路线和后续 leg。到期时仍要重新验证 passability。

到期时 Spatial 使用该 ModelTime 已生效的通行状态重新验证 `To`：

- 仍可通行：完成 step；
- 已不可通行但存在替代路线：保留在 `From`，记录失败 leg 与 `JourneyRerouted`，再开始新的 leg；
- 当前 leg 已不可通行且不再可达：保留在 `From`，产生 `JourneyBlocked(LegInvalidNoRoute)`；
- step 已成功但目标尚未满足，而从新的 `To` 不再存在 continuation：保留在 `To`，产生 `JourneyBlocked(NoContinuationAfterStep)`。

这两个首版 reason 标识阻塞发生在 step 前还是 step 后；其中“无 route / continuation”指当前规则下不存在**可调度**的下一 leg，也包括导航总成本或下一 leg 的绝对 `Due` 超出 V1 表示范围。若产品以后必须向玩家区分地形不可达、成本溢出与时间范围耗尽，再增加正交的 cause 字段，而不是继续扩充 phase reason。

因此当前 leg 表示：

> **已经投入的移动时间，而不是不可撤销的穿墙许可。**

如果上层规则需要立即停止某个正在移动的 Actor，应提交显式 interrupt，而不是依赖未来到期时才发现。

若 external input 恰在 `cursor.Now == CurrentLeg.Due` 时提交，Kernel 会先 commit/apply 本次 `Run` 携带的输入，再 Forecast，因此 cancel / retarget / topology change 先于该 leg 的到期解析；如果调用方已经让 SimulationLoop 解析完该 Due，则不能追溯改写历史。

---

# 9. 导航算法

## 9.1 首版统一使用确定性 Dijkstra

仅有四方向网格时，Manhattan A* 很自然。

但 Portal 可能形成捷径：

```text
同一地图上相距很远的两个格
→ 进入另一张地图
→ 经 Portal 返回
→ 实际路径更短
```

此时普通 Manhattan heuristic 可能高估真实最短成本，不再保证 admissible。

因此首版统一使用：

> **确定性 Dijkstra，等价于 A* with `h = 0`。**

世界与演员规模都很小，正确性、统一性和可解释性优先于性能。

## 9.2 边成本

所有边使用正整数 `ModelDuration`：

```text
Orthogonal edge cost
    = Map.OrthogonalStepDuration × target Cell.MoveCost

Portal edge cost
    = Portal.TraversalDuration
```

普通格边有意采用“进入目标地形的成本”，因此两个方向的成本可以不同；不要擅自改为 source cost 或两端平均值。

具体公式可以在原型后调整，但必须满足：

- 正数；
- 整数；
- checked arithmetic；
- 不依赖 wall-clock；
- 不依赖容器迭代顺序。

## 9.3 稳定 tie-break

建议规范化排序：

```text
普通邻居：North, East, South, West
Portal：PortalId
frontier：totalCost, MapId, Y, X, incomingEdgeKey
ZoneGoal 终点：totalCost, MapId, Y, X
```

Actor 注册顺序、Dictionary 插入顺序和线程调度不得改变结果。

所有 ID 比较都必须具有跨进程稳定总序：数值 ID 按数值比较，字符串 ID 使用 Ordinal，不使用本地文化排序。equal-cost predecessor 的替换规则也必须固定并测试，不能只固定 priority queue 的 key。

首版固定为：在规范化 frontier 与邻接顺序下，equal-cost 时首次发现的 predecessor 获胜；相同 cost 不替换 predecessor，也不重复入队。

## 9.4 首版不提供路径偏好 DSL

暂不提供：

- fastest / safest / quietest 多目标权重；
- 移动目标拦截；
- Follow；
- Formation；
- 局部 path smoothing；
- HPA*；
- 权威路径缓存。

首先用第二个真实场景证明这些能力确有产品价值。

---

# 10. Spatial 对 Kernel 只暴露一个最早 Moment

## 10.1 为什么不能每个 Actor 一个 candidate

当前 Kernel 对同一 Due 的候选按：

```text
Due → SourceId → CandidateId
```

顺序 Resolve，并在每次 Resolve 后全量 re-Forecast。

如果每个 Actor 独立 Forecast：

```text
Alice: A → B @ T
Bob:   B → A @ T
```

Alice 先 Resolve 时会暂时与 Bob 共格，可能产生虚假 Encounter；结果还可能依赖 Actor ID。

## 10.2 SpatialMomentCandidate

对 Kernel 而言，Spatial 每次返回：

- 没有空间工作：`[]`；
- 有空间工作：恰好一个全局最早 candidate。

概念形状：

```text
SpatialMomentCandidate
    ExpectedSpatialRevision
    MomentOrdinal
```

Kernel metadata：

```text
Due
    = 所有 Spatial 内部工作的最早 Due

SourceId
    = composition root 全局唯一、稳定保留的 SpatialSystemSourceId

CandidateId
    = SpatialState 中持久化的 NextMomentOrdinal
```

`NextMomentOrdinal` 是下一次实际 SpatialMoment 的因果身份，不是 topology cache revision。每个成功的 SpatialMoment batch 必须恰好包含一个 `spatial.moment-resolved`，由它递增 ordinal；每个改变 SpatialState 的 primary event 都将 `Revision` checked `+1`，包括 `moment-resolved`；派生 outcome 的 reducer case 是 no-op，不递增 revision。

`ExpectedSpatialRevision` 用于拒绝 stale candidate。首版不再增加 `DueWorkFingerprint`：当前 Kernel 每次事件后都会重建 forecast queue，一个 Spatial source 又只产生一个 candidate，revision 已足够表达失效。若以后有明确失败案例，再引入基于 canonical work keys 的稳定摘要；绝不能使用进程相关的 `GetHashCode()`。

最早工作包括：

- CurrentLeg completion；
- Portal traversal completion；
- 已计划的 Cell / Portal 状态变化；
- 已计划的空间 blocker 变化；
- 其他明确属于 Spatial 的 deadline。

candidate payload 不向外暴露完整路径搜索细节。

## 10.3 Forecast 不变量

```text
ForecastNext(SpatialState state, ModelTime now)
```

`SpatialDefinition` 已在 `SpatialSubsystem` 构造时绑定，仍是决定性输入，但不是当前 Kernel 接口上的独立参数。

必须：

- 无副作用；
- 不修改缓存真理；
- 相同输入得到相同 candidate；
- 正常状态下不返回 `Due < now`；
- Due 来自持久化的 CurrentLeg 或 scheduled mutation；
- 不在每次 Forecast 中重新采样随机数；
- 不依赖系统注册顺序。

一个有效 SpatialMoment candidate 的 Resolve 必须产生非空 batch，并至少包含一个会消费、推进或替换当前 due work 的权威事件。idempotent scheduled mutation 也必须有 `mutation-consumed`；每个有效 moment 都以 `moment-resolved` 收尾。不能用空 Resolve 表示“检查过但没有变化”，否则会触发 Kernel 的 repeated no-op 防护。

`Resolve` 收到 stale 或包装错误的 candidate 时抛出稳定异常；它必须核对 SourceId、CandidateId、payload ordinal、ExpectedSpatialRevision、当前最早 Due 与 Definition stamp，不能用空 batch 表示拒绝。`moment-resolved.ResolvedWorkCount` 必须等于 pre-state 中 `Due == T` 的 CurrentLeg 与 scheduled mutation 总数；无到期工作、仅有未来工作或计数不符都不能借无关 primary event 消费 MomentOrdinal。

---

# 11. 同刻空间工作的内部批处理

在最早时刻 `T`，Spatial 一次 Resolve 所有 `Due == T` 的内部空间工作。

建议首版固定以下内部语义阶段：

```text
Phase 0
    若本次 Run 携带 external inputs，Kernel 已在 cursor.Now 提交并应用它们

Phase 1
    应用 T 时刻到期的 Cell / Portal / blocker / sight override

Phase 2
    在 Phase 1 后的拓扑上验证所有到期 CurrentLeg

Phase 3
    从同一个 pre-step occupancy snapshot 同时决定所有 step 结果

Phase 4
    得到 post-step occupancy，并为继续移动者规划下一 leg

Phase 5
    比较 pre/post 投影，计算 Arrival / Blocked / Zone / CoPresence / GeometricVisibility delta

Phase 6
    按规范化顺序生成一个 non-interleaved SpatialEvent batch
```

普通 Actor 不阻挡，所以大多数 step 没有互斥竞争。

首版明确：

```text
Alice: A → B
Bob:   B → A
```

- 交换合法；
- 只看 batch 完成后的格位；
- 不产生瞬时假共格；
- 不算边上相遇。

若两人同时进入同一个目标格：

- 两人都成功；
- post-step occupancy 包含两人；
- 产生稳定的 CoPresence 变化；
- 是否构成 Encounter 由上层规则决定。

## 11.1 同刻门状态变化

首版采用保守规则：

> **T 时刻的通行性变化先于 T 时刻 step 结果验证。**

所以：

```text
Door closes @ T
Actor step through door due @ T
```

结果为 Actor 被阻挡或改道。

这个规则简单、稳定，并且不依赖 Actor ID。

若未来需要“已经进入门洞者优先”，应显式增加 traversal reservation，而不是借用 SourceId 排序。

从未来时刻自然推进到 `T` 时，并不存在一个会自动接收 external input 的 Phase 0；只有调用方在 cursor 已位于某时刻时传入的 external batch 才享有上述先提交语义。

## 11.2 首版没有移动占位 blocker

首版不存在“移动 Entity 作为占位 blocker”的语义。所有显式 blocker 都投影为 Cell / Portal dynamic override，并服从 Phase 1：在 `T` 生效或失效后的 blocker 状态决定 `T` 的 CurrentLeg 是否可通行。

若未来加入移动占位者，再独立定义 reservation、同时让路与争抢规则；不能把普通 Actor occupancy 或 ID 顺序临时当作 blocker 仲裁。

---

# 12. 空间事件与 Reducer

## 12.1 建议的事件族

权威状态转换：

```text
spatial.entity-placed
spatial.entity-removed
spatial.observation-state-changed
spatial.journey-started
spatial.journey-retargeted
spatial.journey-cancelled
spatial.journey-interrupted
spatial.entity-stepped
spatial.journey-rerouted
spatial.journey-continued
spatial.journey-completed
spatial.journey-blocked
spatial.portal-state-changed
spatial.cell-state-changed
spatial.mutation-scheduled
spatial.mutation-consumed
spatial.moment-resolved
```

由 pre/post 空间状态纯推导、但可供上层消费的 outcome：

```text
spatial.zone-entered
spatial.zone-left
spatial.copresence-started
spatial.copresence-ended
spatial.geometric-visibility-changed
```

派生 outcome 不修改 `SpatialState`；它们存在于 Journal 中是为了审计和提升产品事件，不成为比位置与 topology 更高的第二套真理。命令拒绝不是 `SpatialEvent`，Spatial 返回稳定 rejection code，由游戏层按需记录 `game.action-rejected`。

正式 Reducer 必须同时核对 EventKind Id、Version 与 concrete payload type。Kernel 的 `EventKind` equality 只表示 routing identity，不能替 Spatial 完成 schema 校验。

`journey-started / continued / rerouted` payload 必须携带完整 resulting `CurrentLeg`；`entity-stepped` 必须携带 `From / To / JourneyGeneration`。Reducer 不重新寻路。

任何写入 event 的新 leg 都必须在产生时精确满足：正交边 `Due == StartedAt + Map.OrthogonalStepDuration × 当前有效 target MoveCost`，Portal 边 `Due == StartedAt + Portal.TraversalDuration`；乘法与时间加法均 checked。完成旧 leg 时不按后来变化的 MoveCost 重算 Due。

`JourneyCompleted` 至少区分 `ReachedGoal / AssignedAlreadySatisfied / RetargetedAlreadySatisfied`，因为三者对 Journey allocator 与 MovementGeneration 的投影不同。`JourneyBlocked` 至少区分“当前 leg 到期但已不可通行”与“已成功 step、但从新格无 continuation”两个 prefix；Reducer 只核对 event 携带的旧 leg 与局部状态形态，不重新证明“没有替代路线”。

所有具体 SpatialEvent 类型可以公开供上层 pattern match，但权威 event 的构造器保持 internal，只允许统一 Handler / SpatialSubsystem 产生。`moment-resolved` payload 还携带正数 `ResolvedWorkCount`；Reducer 校验 ordinal、完整边界与没有残留 due work，但不尝试从单一收尾 event 反推整个 batch 的起始工作集。

首版可以调整具体事件粒度，但必须满足：

- 同一个 SpatialMoment 的事件来自同一个 Kernel Resolve batch；
- 共享 `ModelTime` 与 `EventCause.BatchOrdinal`；
- 以 Microstep 形成稳定总序；
- Kernel 不会在该 batch 中间 re-Forecast；
- 派生接触与视野基于最终 post-step state，而不是逐 Actor 临时状态。

细粒度 event reducer 可以在 batch 内出现短暂中间态，但所有公开不变量必须在完整 batch 结束后恢复。实现应验证：Resolve scratch fold 的最终 `SpatialState` 等于同一 batch 经正式 `SpatialReducer` fold 的最终状态。

## 12.2 事件顺序

建议：

```text
Topology / Override：ScheduledMutationId
Step：EntityId
Journey outcome / continuation：EntityId
Zone：EntityId, ZoneId
CoPresence：canonical EntityId pair
Geometric Visibility：ObserverId；Added / Removed 数组内部按 EntityId
Moment completion：SpatialEvent 子序列中的最后一个
```

全部 state-changing body primary（包括 Journey continuation）先恢复为完整 post-primary state，再统一计算 derived outcomes；不能为了展示顺序把 continuation event 倒排到 derived event 之后。这样 step mismatch prefix 最短，scratch fold 与正式 reducer fold 保持同序。

游戏侧 lifting 之后仍可在同一个 Kernel batch 追加产品级 event，例如 `DecisionRequested`；因此 `moment-resolved` 不保证是整个顶层 batch 的最后一个 event。

排序仅决定 Journal 可读性和 replay 总序。

它不能决定谁在物理上“获胜”。

若以后出现互斥争夺，必须使用明确的：

- initiative；
- priority；
- contention rule；
- stable deterministic random sample。

不能默认 ID 小者获胜。

同一 target + Due 的互斥 scheduled mutation 必须在 schedule 时以稳定 `ConflictingMutation` 拒绝；相同目标状态可以规范化去重。同一个 command batch 中互相矛盾的即时 Portal / Cell set 也整体拒绝该冲突组。首版不使用 external input 顺序、ScheduledMutationId 或“最后写入者”决定物理结果。

同一 Entity 在一个 command batch 中出现多个互斥 lifecycle / movement 命令也作为冲突组整体拒绝，不能让规范 phase 隐式选择赢家。

scheduled mutation 的 Portal target 是整个 PortalId，Cell target 是整个 CellOverride；`Due` 必须严格晚于 command time。相同 target + Due + resulting value 规范化去重并让相关命令都 accepted；不同 resulting value 的整个冲突组拒绝且不消费 MutationId。

## 12.3 临时投影与正式状态

所有会改变位置、Entity 集合、topology、遮挡或 observer 开关的转换，都必须经过同一个纯 `SpatialTransition`：

1. 以 committed pre-state 为起点；
2. 生成并 scratch-fold 权威状态事件；
3. 得到最终 post-spatial-state；
4. 比较 pre/post 的 Zone、CoPresence 与 geometric visibility 投影；
5. 追加规范化排序的派生 outcome。

SpatialMoment Resolve 与当前时刻的 `HandleBatch` 都复用这条 transition。于是 `PlaceEntity`、`RemoveEntity`、立即改变 Cell 遮挡等操作，即使没有任何 Journey 或 future candidate，也会在同一个输出 batch 中产生完整关系差量；不需要持久化 contact cache，也不需要依赖下一次 SpatialMoment 补算。Portal toggle 不传播 LOS，因此它自身通常没有 visibility delta，但仍可能改变导航结果。

外部项目不得手工构造 primary SpatialEvent 绕过这条入口。游戏顶层 reducer 只负责应用已经由 Spatial 产生并包装的 event。

但正式状态仍然只能由：

```text
Kernel commit
→ SpatialReducer.Apply
```

更新。

Resolve 不直接返回一个旁路 WorldState，也不保留不可重放的隐藏 mutation。

scratch fold 与正式 reducer 共同委托一个不依赖 `EventCause / Microstep` 的纯 `SpatialProjector.Apply(definition, state, kind, payload, modelTime)`。Definition 显式参与 Cell、Portal、Goal、边与 sparse override 的局部校验；需要参与状态语义的 ModelTime 必须来自当前 transition time 或 committed event timestamp，不能伪造尚未由 Kernel 分配的 DomainEvent metadata。

静态定义与动态 override 合成出的有效空间规则也必须只有一份内部实现，至少统一回答 Cell 的 movement / sight blocker、MoveCost、Portal enabled、leg passability 与边耗时。Navigator、Projector、Queries 与 SpatialMoment 都委托该实现；不能各自复制一套看似相同的公式。

`SpatialReducer` 的逐 event API 没有 batch-end callback，因此不会在每个细粒度 prefix 后调用完整状态验证。统一 Handler / SpatialMoment 必须在 scratch-fold 完成后调用 `SpatialStateValidator.ValidateComplete`；Replay/Fork 验证器也只在完整 BatchOrdinal 边界调用。这样既允许 step prefix，又不会把不完整状态暴露为合法 Fork 边界。

---

# 13. 空间写入与未来调度的唯一所有者

当前 Kernel 的同刻候选不是自动并行求解。

以下设计不安全：

```text
MovementSystem: Actor crosses door @ T
ScenarioSystem: Door closes @ T
```

它会使结果取决于不同系统的 SourceId，并可能由第一个事件压掉第二个 Forecast。

首版因此区分两种“唯一”：

> **SpatialEvent / SpatialReducer 是空间状态的唯一写入路径；SpatialMoment 是所有未来空间变化的唯一调度与同刻仲裁者。**

当前 `cursor.Now` 的立即空间命令可由 `HandleBatch` 直接产生 SpatialEvent，并在 Kernel 下一次 Forecast 前生效；具有未来 Due 的变化必须先产生 `mutation-scheduled`，到期后由 SpatialMoment 与移动统一 Resolve。无论立即还是计划变化，外部都不能绕过 Spatial transition / reducer 修改位置、通行性、移动成本或遮挡。

其他子系统如果已知未来变化，应提前提交：

```text
ScheduleSpatialMutation(
    target,
    newState,
    due)
```

例如：

- 10:00 城门关闭；
- 暴雨后沼泽不可通行；
- 15 分钟后升降梯抵达；
- 火势蔓延到某格；
- 临时路障被移走。

到 Due 时由 Spatial 与移动一起仲裁。

若未来出现不可拆分的跨领域原子事件：

```text
爆炸同时：
    毁墙
    伤害角色
    改变火区
```

仅仅让另一个 game system 在自己的 Resolve 中同时发出 SpatialEvent 与游戏事件，**不能**自动与已经存在的 `SpatialMomentCandidate @ T` 原子合并；两个 candidate 仍受 `Due → SourceId → CandidateId` 顺序影响。

首版因此不承诺“在 T 才突然产生的跨领域效果”具有无顺序的同时性：

- 若空间效果在 T 之前已知，提前 `ScheduleSpatialMutation`；
- 若只在 T 才能解析，接受并显式测试稳定 SourceId 顺序；或由一个更高层 coordinator 成为该时刻唯一的空间 candidate owner，并实际 subsume SpatialMoment；
- 不得把普通 composite event 描述为已经解决跨领域原子性。

只有这类真实案例反复出现后，才考虑给 Kernel 增加显式 Phase 或 same-time candidate-set resolve。

首版不修改 Kernel。

---

# 14. 视野：几何可见不等于玩家知道

## 14.1 首版几何规则

首版 Spatial 负责客观几何可见性：

```text
范围
    ManhattanDistance <= Map.VisionRange

遮挡
    严格 supercover line-of-sight

朝向
    360°，无 facing cone

跨 Map
    永不直接可见

Portal
    即使开放也不传播视线
```

严格 corner 规则：

- 射线接触的任一中间不透明格都会阻挡后方；
- 不透明目标格自身可以被看到，但其后方不可见；
- 不能从两个墙角相接的缝隙中看过去；
- 算法应满足 `LOS(A, B) == LOS(B, A)`；
- 边界、旋转与镜像结果必须由测试锁定。

可编码的首版定义为：从 source cell center 到 target cell center，以整数 / 有理数比较枚举闭线段接触的全部格；精确经过角点时同时纳入两个侧邻格。source 与 target 不作为中间 blocker，不透明 target 自身可见；任一被接触的中间不透明格都足以阻挡，不要求角点两侧同时有墙。算法不得依赖浮点舍入。

四方向移动消除了移动 corner-cutting，但没有自动消除斜向视线的墙角语义，所以 LOS 必须有独立规则。

## 14.2 Query 与事件

Spatial 提供只读查询：

```text
GetVisibleCells(observerId)
GetVisibleEntities(observerId)
HasLineOfSight(firstCell, secondCell)
```

这些查询完全由当前 committed position + topology 推导，不读取持久 contact cache，也不需要 Journal 每个 visible cell。公开 Query 只接受通过完整边界校验的 `SpatialState`，不得读取 `entity-stepped` 之后但 Journey 尚未 continued / completed / blocked 的 reducer prefix。SpatialMoment 内部的 Navigator 可以在受控 transition 中读取该合法临时 prefix，以便从新位置规划 continuation；最终关系差量只能在全部 body primary 已恢复完整状态后计算。

对于 `ObservationEnabled` 的 Entity，每次统一 transition 比较 pre/post 可见 Entity 集合，并可产生：

```text
GeometricVisibilityChanged
    ObserverId
    AddedEntityIds[]
    RemovedEntityIds[]
```

`GetVisibleEntities` 排除 observer 自身；同格其他 Entity 几何可见；active CurrentLeg 全程按 `From` 参与 LOS；Portal 不传播 LOS。所有已放置空间 Entity 都可成为几何目标，至于上层是否把某类对象视为可感知 subject，由 Perception 层决定。

`GetVisibleCells / GetVisibleEntities` 可对任何已放置 Entity 做客观查询，不受 `ObservationEnabled` 限制。该开关只控制 transition 是否产生 visibility delta；从 true 切换到 false 时产生原可见集合的 Removed，从 false 切换到 true 时产生当前集合的 Added。

该 event 只是 transition 时刻的客观几何差量，不修改 SpatialState，也不得直接当作 Protocol 的 `VisibleActorIds` 发布。

## 14.3 Spatial 明确不负责

Spatial 不判断：

- 是否注意到；
- 是否认出身份；
- 是否理解动作；
- 是否形成 KnownFact；
- 是否记住最后位置；
- 是否相信看到的内容；
- 是否值得中断当前 Intent；
- 是否产生 DecisionPoint。

因此必须保持：

```text
Geometric Visibility
        ≠
Perception / Identification
        ≠
Protocol.Observation
        ≠
Knowledge / Belief / Memory
```

游戏层可以把 `GeometricVisibilityChanged` 与角色感知能力、隐蔽规则和当前认知结合，再生成主观事件。

首版不做：

- 光照；
- 草丛与半透明；
- 朝向视锥；
- 隐身；
- 概率侦测；
- 注意力累积；
- 听觉；
- 跨 Portal 看见下一地图。

---

# 15. Spatial 与 Player / DecisionPoint 的边界

Spatial 不依赖 Protocol，也不构造 `DecisionRequest`。

它输出的只是空间事实：

- Journey completed；
- Journey blocked；
- Zone entered / left；
- CoPresence changed；
- Geometric visibility changed；
- Portal / Cell state changed。

游戏层决定：

- 哪个 Entity 是 Player；
- 哪些空间变化对该 Player 合法可知；
- 是否形成 Observation；
- 是否更新 Knowledge；
- 是否构成 danger / encounter；
- 是否值得 Interrupt；
- 是否只是 ContinueCurrentIntent；
- 是否产生 DecisionPoint。

必须坚持：

> **每格空间状态变化不等于每格 Player 决策。**

一个 Actor 可以内部走过 100 格，只在真正有意义的时刻被唤醒。

## 15.1 立即 Decision 的同刻要求

当前 Kernel 会在完整 Resolve batch commit/apply 后检查 decision predicate。

若某个空间结果必须阻止后续世界推进，游戏侧 outcome policy 不应只等待一个独立 `DecisionSchedulingSystem` 在下一轮 Forecast 再发请求；否则另一个尚未 Resolve 的同刻系统可能先执行。

建议游戏侧包装当前 Spatial candidate 的 resolver：

1. 调用 Spatial Resolve；
2. 对 Spatial event batch 做一次纯 scratch projection，得到预计 post-spatial-state；
3. 由 `GameSpatialOutcomePolicy / PerceptionProjection` 检查 Arrival / Blocked / CoPresence / GeometricVisibility delta；
4. 根据 pre-game-world + 预计 post-spatial-state 计算未提交的主观信息事件；
5. 在同一个 Kernel Resolve batch 末尾追加必要的游戏级 `DecisionRequested`；
6. 对同一 Actor 的多个空间原因聚合为一个请求。

Resolve 阶段尚未拥有 committed post-world，不能直接修改或读取“已经提交后的 GameWorld”。正式世界仍由完整 batch commit 后的顶层 reducer 得到。

这样产生的 Decision 能看到完整预计 post-spatial-state，而不是某个逐 Actor 中间态；但它不能撤销此前已经按较小 SourceId Resolve 的同刻事件。首版只保证阻止这个 batch 之后尚未 Resolve 的推进。

这段产品 outcome policy 属于游戏项目，不属于 Spatial，也不应伪装成通用 glue。

---

# 16. 真实游戏世界中的组合方式

## 16.1 当前 Kernel 的泛型现实

`SimulationLoop<TWorld, TCandidatePayload, TEventPayload>` 要求参与同一游戏的所有系统共享：

- 顶层 `TWorld`；
- 顶层 candidate union；
- 顶层 event union。

因此 Spatial 不可能同时做到：

- 只知道 `SpatialState / SpatialCandidate / SpatialEvent`；
- 又直接作为任意游戏世界的 system 注册。

## 16.2 Spatial 的独立形状

隔离测试时，概念 API 为：

```csharp
public sealed class SpatialSubsystem :
    ISimSystem<SpatialState, SpatialCandidate, SpatialEvent>
{
    IReadOnlyList<EventCandidate<SpatialCandidate>> ForecastNext(
        SpatialState state,
        ModelTime now);

    IReadOnlyList<UncommittedDomainEvent<SpatialEvent>> Resolve(
        SpatialState state,
        EventCandidate<SpatialCandidate> candidate);
}

public sealed class SpatialReducer :
    IEventReducer<SpatialState, SpatialEvent>
{
    SpatialState Apply(
        SpatialState state,
        DomainEvent<SpatialEvent> domainEvent);
}
```

## 16.3 游戏侧 system 与 reducer lifting

游戏项目提供薄包装：

```text
GameWorld.Spatial
    → SpatialState

GameCandidate.Spatial(value)
    ↔ SpatialCandidate

GameEvent.Spatial(value)
    ↔ SpatialEvent
```

`SpatialSystemLift` 只负责：

- 从 `GameWorld` 选择 SpatialState；
- 包装 / 解包 Spatial candidate；
- 委托 Spatial Forecast / Resolve；
- 把 SpatialEvent 包装成 GameEvent。

真正把 reducer 结果放回顶层世界的是 `IEventReducer<GameWorld, GameEvent>` 的 Spatial case：

1. 解包 SpatialEvent；
2. 保留原 `Timestamp / Cause / Kind` 构造对应 `DomainEvent<SpatialEvent>`；
3. 调用 `SpatialReducer`；
4. 用结果替换 `GameWorld.Spatial`。

产品解释另放在：

```text
GameSpatialOutcomePolicy
PerceptionProjection
EncounterPolicy
```

它们可以由同一个 game-side resolver 协调，以便在当前 Spatial Resolve batch 追加产品事件；但它们不是薄 adapter，也不属于 Spatial 项目。

不要为消除几十行 glue 而：

- 让 Spatial 依赖游戏类型；
- 让 Spatial 通过 callback 读取任意 GameWorld；
- 在首版给 Kernel 增加通用 subsystem composition DSL；
- 用反射或 dynamic 隐藏类型边界。

如果第二款真实游戏重复出现相同 adapter，再提炼组合辅助设施。

---

# 17. SpatialCommands：所有空间意图的唯一入口

建议 Spatial 公开小型纯命令边界：

```text
PlaceEntity
RemoveEntity
SetObservationEnabled
AssignMoveGoal
RetargetMoveGoal
CancelMoveGoal
SetPortalState
SetCellOverride
ScheduleSpatialMutation
InterruptMovement
```

命令不是 committed history。

首版的正确入口是批处理纯函数：

```text
HandleBatch(
    SpatialState preState,
    IReadOnlyList<SpatialCommand> simultaneousCommands, // 每条带稳定 CommandId
    ModelTime now)
    → accepted SpatialEvents[] + per-command rejection codes
```

它在临时 working state 上按规范 phase 处理：

```text
1. 检测同 target / due 的冲突组
2. Portal / Cell 的即时 topology 与 sight override
3. Entity placement / removal / observer 开关
4. cancel / interrupt / retarget
5. assign goal
6. 从初始 pre-state 与最终 working state 产生一次关系差量
```

每组接受的权威 event 都先 scratch-fold，再验证后续命令；不能让每条命令都独立读取同一个原始 pre-state。稳定 ID 必须来自显式 allocator state 或规范化 command identity，不能让两个同时 translator 各自猜测“下一个 ID”。

同批 `CommandId` 必须唯一。JourneyId 与 ScheduledMutationId 由 State allocator 按规范化 CommandId 顺序分配，调用方不能提供或猜测。

handler 输出：

- 接受后的 `UncommittedDomainEvent<SpatialEvent>[]`；
- 每条拒绝命令的稳定 rejection code。

游戏层应把接受或拒绝结果包装进顶层 Journal。

当前 Host 会让多个 pending PlayerDecision translator 读取同一个 pre-batch world，因此不得在每个 translator 内直接把 SpatialCommand 变成最终 SpatialEvent。首版要求 composition layer 在调用 `SimulationLoop.Run` 前集中收集同刻 SpatialCommands，调用一次 `HandleBatch`，再把 resulting SpatialEvents 作为同一个 external batch 提交。若现有 Host API 无法提供这条集中入口，应在游戏集成层补一个 batch coordinator；不能退化为多个 translator 各自验证。

首版不采用“先 journal `game.command-requested`，再由独立 action-resolution candidate 于同刻解析”的路线，因为它会和 `SpatialMomentCandidate` 再次按 SourceId 竞争，无法保证 cancel / retarget / topology command 先于已经 Due 的 CurrentLeg。未来若改走 queued-command 模型，pending SpatialCommands 必须被 SpatialMoment 自身吸收，或 Kernel / composition root 必须提供并声明显式 phase 契约。

单命令 `Handle` 只适用于初始化、管理输入，或调用方能证明没有共享 ID、Journey 与 topology 冲突的情况。

规则：

- 外部不能直接改 `SpatialState`；
- 外部不能替 Spatial 计算并写入下一格；
- 外部不能把一条未提交路径当作世界事实；
- Inventory / Key / Faction 等条件先由游戏规则判断；
- 判断结果显式投影为 Portal / Cell state 或 Move command；
- rejection code 稳定、结构化，不依赖本地化文本。

命令在 `now` 立即生效；未来变化只能使用 `ScheduleSpatialMutation`。`HandleBatch` 与 SpatialMoment Resolve 必须走 §12.3 的同一 `SpatialTransition`，确保立即放置、移除或改变 Cell 遮挡时，Zone / CoPresence / GeometricVisibility outcome 不会漏发。

---

# 18. Replay 与确定性不变量

## 18.1 状态来源

1. `ForecastNext` 不修改状态。
2. `Resolve` 不修改状态。
3. 只有 committed `SpatialEvent` 经 `SpatialReducer` 改变状态。
4. Forecast queue 不持久化。
5. Replay 只 fold Journal。
6. Reducer 不调用随机数。
7. Reducer 不依赖 wall-clock、IO 或全局服务。
8. Reducer 不调用 Pathfinder 或 LOS 来重新决定已提交结果。
9. 所有 immediate command 与 SpatialMoment 都经统一 SpatialTransition 生成完整 pre/post outcome。

## 18.2 位置与拓扑

1. 一个 Entity 任一时刻只有一个权威 `CellRef`。
2. Entity 不同时占据多个地图尺度。
3. 普通格边只连接同 Map、Manhattan distance 为 1 的格。
4. 跨 Map 或非局部连接必须有显式 Portal。
5. 普通边和普通 Portal 耗时都为正整数。
6. 普通 Entity 数量与排列不改变 passability。
7. Portal 与 Cell 动态状态只能由 SpatialEvent 改变。
8. SpatialEntity、override、Journey 与 CurrentLeg 必须引用有效 definition 对象 / CellRef。
9. 完整边界上 `CurrentLeg.From == Entity.Cell`，其 `To` 是合法普通邻边或指定 Portal endpoint；新 leg 的 `Due` 必须等于 `StartedAt +` 当时有效边成本，而不只是任意正时长。

## 18.3 Journey

1. 在合法 commit / Fork 边界，active Journey 恰有一个 CurrentLeg。
2. CurrentLeg 的 `Due` 存在于 state，不能用 `now + duration` 反复重算。
3. retarget / cancel / interrupt 改变 generation。
4. 同一 Due 的全部空间工作由一个 SpatialMoment 解决。
5. 逐格事件不自动产生 DecisionPoint。
6. Journey generation 与 CurrentLeg generation 一致。
7. retarget / cancel / interrupt 清除旧 leg，Entity 留在 From；旧 leg 已投入时间不转移。
8. 下一 leg 完整写入 event，Replay 不重新选路。
9. JourneyId 与 ScheduledMutationId 由持久 allocator 产生，完成或消费后不得复用。

## 18.4 排序

1. 邻接顺序固定。
2. Portal 顺序固定。
3. Dijkstra frontier tie-break 固定。
4. 事件输出按规范化 key 排序。
5. 不依赖 Dictionary / HashSet 自然遍历顺序。
6. Actor ID 只能影响展示顺序，不能默认决定竞争胜负。
7. 数值 ID 使用数值总序，字符串 ID 使用 Ordinal。

## 18.5 定义与版本

1. `SpatialDefinition` 有稳定 Id、Revision 与 ContentHash。
2. Run Manifest 同时固定 definition hash 与 `SpatialRulesVersion`。
3. restore / continue 时 definition 或 rules version 不可用、不兼容必须失败；本文不承诺仓库自动保存或加载所有旧实现。
4. 每个 `SpatialEventKind` 使用稳定 Id，并通过 Kernel `EventKind.Version` 声明 payload schema version。
5. rules version 锁定 Dijkstra、LOS、phase 与 CurrentLeg 规则；content hash 不能代替它。
6. Presentation 插值和路径缓存不属于 replay contract。

当前 runtime 只执行 `SpatialRules.CurrentVersion == 1`。Definition / State 可以保留未来或归档版本的 metadata，但 Reducer、Queries、CommandHandler 与 SpatialSubsystem 在绑定不受支持的版本时必须立即失败，不能悄悄用 V1 算法解释它。

`SpatialState.Create(definition)` 固定 Definition stamp；SpatialSubsystem、SpatialCommandHandler 与 SpatialQueries 的每个公开入口都先验证 stamp，SpatialQueries 还必须验证它收到的是完整 commit 边界状态。hash / rules mismatch 必须在 Forecast / Resolve 或命令 / 查询执行前失败，不能使用新规则解释旧 state。

## 18.6 Fork

1. Fork 发生在完整 commit batch 边界。
2. Kernel scheduler、其他 ISimSystem 与 Host 不观察 SpatialMoment 的中间世界；合法 Replay / Fork 边界不得位于同一 `BatchOrdinal` 内。
3. 从合法 Journal prefix replay 后，下一 Spatial candidate 必须一致。
4. speculative path 不能写入 committed state。

具体 `IJournalSink` 是否具有 crash-atomic storage transaction，由 sink 实现保证；当前 Kernel 的调度原子性不等同于所有存储实现天然具备事务性。

---

# 19. 首版明确非目标

Spatial 首版不实现：

- 真 3D；
- 连续 Z、高度、坡度与斜坡碰撞；
- 八方向移动；
- 对角成本和 corner-cutting；
- 连续位置、速度、加速度与物理积分；
- NavMesh；
- Actor body collision；
- Actor footprint；
- 局部避障；
- Reservation、推挤、拥堵和容量；
- 边上的连续相遇；
- 移动目标追踪；
- Formation；
- 战斗距离、掩体、弹道；
- 自动敌我 Encounter；
- 光照、听觉、朝向视锥、潜行概率；
- 跨 Portal LOS；
- Player knowledge、belief、memory；
- `Protocol.Observation`；
- Host 调度与 `DecisionRequest`；
- Godot scene、sprite、动画与 camera；
- 无限地图与流式加载；
- 递归坐标变换；
- 任意脚本式 Portal predicate；
- 通用路径策略 DSL；
- HPA*、增量寻路和权威路径缓存；
- 对未来 NavMesh 的透明无痛迁移承诺。

Spatial 首版也不是：

> 小型 RPG 所有地图能力的万能工具箱。

它只解决当前伴生游戏真正需要的客观空间机制。

---

# 20. 首个验证切片

建议不要立即迁移 FirstBoard，而是先建立独立 Spatial toy model。

## 20.1 内容

```text
3 张 GridMap
    WorldMap
    TownMap
    CellarMap

至少 3 个 Portal
    双向城镇入口
    单向捷径
    可关闭地下室门

至少 3 个 Actor
    Human candidate
    Companion candidate
    Remote antagonist / Rule Actor

至少包含
    多人同格
    同刻换位
    同刻进入同一格
    一次 retarget
    一次 Portal 关闭
    一个墙角 LOS
    一个 Zone enter/leave
```

## 20.2 必须通过的实验

### Dependency Guard

- Spatial 项目只引用 Kernel；
- 不引用 Protocol、Host、FirstBoard、Godot；
- 无 IO、wall-clock、环境变量。

### Layered Journey

- Actor 经 Portal 在三张地图之间移动；
- Actor 任一时刻只有一个 CellRef；
- 镇内 Actor 不同时占据世界地图城镇格；
- 单向 Portal 不能反向穿越；
- 关闭 Portal 不可用于新 leg。

### Deterministic Navigation

- 不产生对角移动；
- Portal shortcut 下仍得到真实最短路；
- 打乱地图、Portal、Actor 与 Dictionary 插入顺序，路径和 Journal 不变；
- 所有耗时为正整数。

### Atomic Multi-Actor Movement

- `A → B` 与 `B → A` 同刻交换不产生假共格；
- 两人同刻进入一个目标格均成功；
- 普通 Actor 不阻挡彼此；
- 交换 Actor 数值 ID 不改变物理结果。

### Reroute / Topology Change

- retarget 后旧 Forecast 不再提交；
- retarget / cancel / interrupt 清除旧 leg、保留 From，并丢弃已投入时间；
- 不可达 retarget 原子拒绝，旧 Journey / CurrentLeg 完全不变；
- 已满足 goal 不创建零时长 leg 或悬空 Journey；
- 无关事件不推迟 CurrentLeg.Due；
- CurrentLeg 开始后的成本变化不追溯 Due；
- 门状态改变影响到期验证和后续 leg；
- 到期受阻但可绕路时 Journal 明确记录失败 leg 与新 CurrentLeg；
- 不可达时产生稳定 JourneyBlocked。

### Immediate Command Batch

- `PlaceEntity / RemoveEntity / SetObservationEnabled` 即使没有 future candidate，也在同一输出 batch 产生完整关系 delta；
- 立即修改 Cell 视线遮挡会立刻产生 GeometricVisibility delta；Portal toggle 自身不传播 LOS；
- 同批 topology → placement → movement command 按规范 phase 读取 working state；
- 两个同时命令不会分配相同 JourneyId；
- 同 target 的矛盾即时命令或 scheduled mutation 得到稳定冲突结果，不使用 last-write-wins。

### Vision Corners

- 墙角；
- 两墙接角；
- 地图边界；
- 不透明目标格；
- Portal 两侧；
- 旋转与镜像；
- `LOS(A, B) == LOS(B, A)`。

### Mid-step Event

- 在一个长世界地图 step 中间安排一个真实到期的 scheduled spatial mutation，或先由另一个真实 candidate 将 Kernel cursor 推进到中点再提交 external input；不能把 `Run(until: midpoint)` 误认为 cursor 已自动推进到 midpoint；
- Actor 权威位置仍为 From；
- Journey 仍 active；
- Actor 仍与 From 的 Entity 共处、按 From 计算几何可见；
- Actor 尚未与 To 的 Entity 共处或按 To 计算几何可见；
- Portal leg 使用相同规则；
- Presentation progress 不改变世界查询；
- 显式 interrupt 后 Actor 留在 From；
- 门在 Due 关闭时，Actor 失去已投入时间但仍留在 From。

### Replay / Fork / Split Run

- 单次运行与切成 N 段运行产生相同 Journal；
- Replay 得到相同 SpatialState；
- 下一 candidate 相同；
- Resolve scratch fold 与正式 reducer fold 的最终 SpatialState 相同；
- idempotent mutation 仍由 completion event 消费，不触发 repeated no-op；
- batch 边界 Fork 稳定；
- definition hash 或 SpatialRulesVersion 不匹配时拒绝恢复。

### AI Player Ergonomics

- Player 只提交一次 `MoveTo(Anchor)`；
- 内部走过大量格不会产生等量 Player 请求；
- 只有配置为有意义的空间变化才进入 Decision；
- Player Observation 不包含完整地图、隐藏 Anchor、内部路径或 Forecast。

---

# 21. 从 FirstBoard 迁移时应替换什么

当前 FirstBoard 的空间模型是一个正确的最小玩具：

```text
BoardPlace
    AdjacentPlaceIds

BoardActor
    PlaceId

BoardActivity.Travel
    DestinationId
    Due

Visibility
    同 Place 且 IsPresent
```

新 Spatial 验证成功后，迁移方向可以是：

```text
BoardPlace.AdjacentPlaceIds
    → SpatialDefinition.GridMaps + Portals

BoardActor.PlaceId + IsPresent
    → SpatialState.Entity.Cell + Journey

BoardActivity.Travel
    → Spatial MoveGoal / Journey / CurrentLeg

同 Place 可见
    → Spatial geometric visibility query / delta

固定 TravelTicks
    → GridMap step duration + Cell move cost + Portal duration
```

但不要在 Spatial toy model尚未通过验收前直接重写 FirstBoard。

FirstBoard 继续承担当前 AI Player / Information Gameplay 回归基线，新 Spatial 先独立证明空间语义。

---

# 22. 演进路径

## 22.1 升级为八方向

若真实游戏证明四方向严重限制表现力：

- 在内部邻接生成器增加 diagonal edge；
- 增加明确 corner rule；
- 增加整数 diagonal duration；
- 为旧 Run 保留原空间规则版本。

为降低迁移成本，committed movement event 应记录：

```text
FromCell
ToCell
EdgeKind
```

而不是把 `North / East / South / West` 作为长期 event schema 的核心。

## 22.2 加入高层图或层次寻路

若 profiler 证明统一 Dijkstra 成为瓶颈，可以：

- 从 Zone / Portal 派生高层图；
- 加入缓存；
- 使用分层寻路；
- 保持 MoveGoal 与 committed step events 不变。

高层图首先是内部加速索引，不必成为第二套世界真理。

## 22.3 加入 TraversingEdge

如果世界地图中途相遇或精确中断成为真实核心玩法，可以把：

```text
AtCell only
```

扩展为：

```text
AtCell
TraversingEdge
```

届时再定义：

- edge progress；
- 反向错身；
- 追赶；
- 中途可见；
- 路上 Encounter。

这不应在没有真实玩法压力时提前加入。

## 22.4 加入更丰富感知

几何 LOS 可以作为后续基础，但：

- 光照；
- 隐蔽；
- 注意力；
- 识别；
- 听觉；

应优先进入独立 Perception / game-domain 规则，而不是让 Spatial 读取 Character、Belief 或 Memory。

## 22.5 NavMesh 或连续空间

不承诺网格到 NavMesh 的透明迁移。

真正需要长期保持稳定的是：

- Actor / Entity stable identity；
- Anchor / Zone / Portal 的语义身份；
- MoveGoal；
- Arrival / Blocked / CoPresence 等上层结果；
- Kernel 的 Forecast / Resolve / Reduce 边界；
- Journal 与 Replay 语义。

若未来替换空间表示，应做明确的 schema / scenario version migration，而不是现在为假想未来发明 universal topology DSL。

Portal、Anchor、Zone 的稳定语义身份是未来迁移底层 topology 的主要锚点；Map / Cell 坐标不承诺跨 `SpatialRulesVersion` 永久稳定。

---

# 23. 主要风险与未决问题

## 23.1 世界格的粗粒度是否会使 mid-step locality 不自然

这是首个切片必须主动测试的问题。

如果 Actor 在一个 30 分钟 step 中始终归属 From locality 导致明显荒谬，应首先调整地图粒度，而不是立刻引入连续位置。

## 23.2 逐格 Journal 体积

首版接受机械事件较多。

少量核心 Actor 的空间事件成本远低于 LLM 推理。

只有 profiler 和真实存档证明成为问题后，才设计：

- 连续空白路段压缩；
- `ActorTraversed`；
- checkpoint；
- 派生索引。

## 23.3 派生关系的计算成本

首版已经裁决：Zone、CoPresence 与 geometric visibility 都从 committed state 纯推导，不持久化 contact cache。

这会重复计算一些小集合，但避免了第二套真理、reconciliation dirty state 与恢复缓存。只有 profiler 证明它成为瓶颈后，才可加入非权威 cache；cache 必须可丢弃、可重建，不能改变 event 结果。

## 23.4 跨子系统同刻原子事件

Spatial 内部批处理不能自动解决所有跨领域同刻冲突。

首版通过：

- 空间状态唯一所有权；
- scheduled spatial mutation；
- 立即 Decision 同 batch lifting；

控制问题。

普通 game-side composite event 不能吸收已存在的 SpatialMoment；在 T 才新产生的跨领域效果仍受 SourceId 顺序影响。首版避免设计依赖这种无预告精确同刻仲裁；确有需要时由唯一 coordinator subsume 空间解析，或升级 Kernel 语义。

只有真实案例频繁出现时，才推动 Kernel 增加 Phase 或 parallel same-time semantics。

## 23.5 游戏 lifting 是否会重复

首版接受几十行显式 system / reducer wrapper，并把产品 outcome policy 保持为独立游戏规则。

第二款游戏重复后再判断是否需要 Kernel / Spatial composition helper。

---

# 24. 阶段性结论

首版 Spatial Framework 可以压缩为：

> **多张有限四向 GridMap + 少量 Portal；普通 Actor 默认不阻挡；语义 MoveGoal 在内部逐格推进；所有到期的未来空间工作由单一 SpatialMoment 批量 Resolve；即时空间命令由统一 HandleBatch 处理；状态只由 committed SpatialEvent 更新；几何可见性与 Player 认知严格分离。**

其架构价值不是提供最先进的移动或视野，而是形成一个极小、统一、确定、可回放的客观空间底座。

Kernel 继续只负责：

> **时间和多个子系统如何在时间上整合。**

Spatial 负责：

> **在这条时间线上，客观空间下一次何时变化，以及变化后空间事实是什么。**

游戏层负责：

> **这些空间事实对 Actor 的知识、意图、关系、Encounter 与 DecisionPoint 意味着什么。**

表现层负责：

> **把离散空间事实表现成清晰、流畅、有情感密度的旅程。**

这使 DramaBoard 能够把首版工程复杂度集中在真正需要验证的地方：

- AI Player 是否拥有持续主体性；
- Human 与 AI 是否共享公平的世界规则；
- 主观信息是否真实影响决策；
- 长期行动是否会被世界自然打断；
- 共同经历是否能积累成关系与记忆。

如果这些核心体验成立，空间子系统以后可以有针对性地升级。

如果它们不成立，那么提前实现八方向、NavMesh、连续碰撞或真 3D 也不会挽救产品方向。
