# Design Note 003：Forecast, Elapse, Decide —— 按信息密度流动的 Simulation Kernel

**状态：概念设计草案**  
**日期：2026-08-09**  
**定位：定义 DramaBoard 的时间流动、事件预测、DecisionPoint 与可回放 Simulation Kernel 的总体模型。**

---

## 1. 核心直觉

传统实时游戏往往按固定时间步更新：

```text
Tick
Tick
Tick
Tick
...
```

即使这段时间什么重要的事情都没有发生，系统仍然持续积分、检查和推进。

DramaBoard 的世界并不需要这样工作。

一个更适合本项目的模型是：

> **Simulation 不需要模拟每一刻，只需要准确跨越那些没有新信息产生的时间。**

当系统已经知道：

- 某个角色正在从 A 前往 B；
- 如果没有干扰，他将在 17 分钟后抵达；
- 另一个角色会在 8 分钟后与他相遇；
- 其他子系统在这之前都不会产生变化；

那么世界没有必要模拟中间 8 分钟的每一秒。

可以直接：

```text
Forecast
   ↓
找到最近事件
   ↓
Elapse / AdvanceTo
   ↓
事件发生
   ↓
重新 Forecast
```

这构成 Simulation Kernel 的基本工作循环。

---

# 2. 思想来源：DEVS-like，而不是固定 Tick

该模型与多类成熟模拟思想具有高度相似性：

- Discrete-Event Simulation；
- next-event simulation；
- event-driven hard-body simulation；
- DEVS / Parallel DEVS；
- kinetic data structures；
- semi-Markov decision process；
- options / temporal abstraction。

本项目不需要机械实现完整学术 DEVS 标准，但可以借用它最有价值的思想：

> **每个足够简单的子系统，都能表达“如果没有外部打扰，我下一次什么时候会发生内部变化”。**

多个子系统组合后，Kernel 只需要找到全局最早的未来事件。

---

# 3. 基本循环：Forecast → Advance → Resolve → Decide

概念循环：

```text
             ┌────────────────────────────┐
             │        World State         │
             └──────────────┬─────────────┘
                            │
                     Forecast systems
                            │
                            ▼
                  EventCandidate[]
                            │
                      choose earliest
                            │
                            ▼
                    AdvanceTo(tNext)
                            │
                            ▼
                 validate candidate(s)
                            │
                            ▼
                     Resolve events
                            │
                            ▼
                   update observations
                            │
                            ▼
                    DecisionPoint?
                     │           │
                    no          yes
                     │           │
                     │       ask Players
                     │           │
                     └─────┬─────┘
                           │
                         loop
```

核心不是“每一帧更新世界”，而是：

> **找到下一次世界状态中需要新增信息的时刻。**

---

# 4. Forecast 是候选未来，不是事实

`Forecast` 是 Simulation Kernel 对当前确定性或随机过程的预测结果。

它不能被理解为已经写入世界历史的事实。

例如：

```text
Alice 正在前往北塔
Bob 正在从北塔前往市场

Forecast:
    Alice/Bob encounter @ 14:37
    Alice arrives NorthTower @ 14:42
    Bob arrives Market @ 14:50
```

如果 14:31 Alice 收到新信息并改变计划，那么：

```text
Encounter @ 14:37
```

并没有“被取消”。

更准确地说：

> **原 Forecast 已失效。**

因此 Forecast 产生的对象应当是类似：

```text
EventCandidate
    EventId
    SourceSystem
    EarliestTime
    Generation
    Dependencies
    Payload
```

第一版可以不实现复杂依赖追踪。

最简单、最可靠的策略甚至是：

> 每次真实事件完成后，全量重新 Forecast。

对于少量核心 Player 与中小规模世界而言，CPU 成本远低于 LLM 推理成本。

只有 profiler 证明 Forecast 成为瓶颈后，才值得引入局部 invalidation。

---

# 5. Forecast 的信息必须与 Player 认知隔离

这是极其重要的系统边界：

> **Simulator foresight ≠ Player foresight.**

Kernel 可以为了效率提前知道：

```text
Alice 将在 09:47 发现矿脉
Bob 将在 10:03 到达酒馆
两人若不改变计划将在 10:11 相遇
```

这些只是模拟器内部的未来候选。

角色的 Observation 不能因此包含：

> “你预感 17 分钟后会遇见 Bob。”

除非世界规则真的允许预知。

这使得 Kernel 可以高度“先知”，而 Player 仍然生活在不完全信息中。

---

# 6. Elapse 不应等同于逐对象积分

概念上，事件之间存在一段可预测的时间流动：

```text
Elapse(dt)
```

但实现上不一定需要：

```text
foreach subsystem:
    subsystem.Update(dt)
```

很多状态可以采用 lazy materialization。

例如旅行：

```text
Journey
    Origin = Inn
    Destination = NorthTower
    StartedAt = 14:00
    ArrivalAt = 14:42
    Path = ...
```

14:21 的位置可以按需计算，而不是每秒写一次 position。

类似地：

- 饥饿；
- 疲劳；
- 天气趋势；
- 工程进度；
- 挖矿进度；
- 调查进度；

都可以只记录起点、速率、阶段和下一临界点。

因此 `Elapse` 更适合作为语义：

> **世界时钟从 t₀ 跨越到 t₁，并在需要时 materialize 中间持续过程产生的状态。**

---

# 7. 持续活动应当是一等对象

DramaBoard 的大量 RPG 行为不是瞬时动作，而是 Activity：

- Travel；
- Investigate；
- Sleep；
- Mine；
- Guard；
- Read；
- Work；
- TreatWound；
- Follow；
- WaitForSomeone。

Activity 可以：

- 持续一段逻辑时间；
- 在中途被外部事件打断；
- 自己产生阶段性事件；
- 拥有自己的 Forecast；
- 产生下一次 DecisionPoint。

例如：

```text
StartMining @ 09:00

Forecast candidates:
    Alice arrives mine      @ 09:22
    scheduled reconsider    @ 09:30
    ore discovery           @ 09:47
    stamina threshold       @ 10:00
    vein exhausted          @ 13:20
```

Kernel 选择：

```text
NextEvent = Alice arrives @ 09:22
```

矿工不需要收到：

```text
09:01 还在挖
09:02 还在挖
09:03 还在挖
...
```

到 09:22 时 materialize：

> 已经连续挖矿 22 分钟。

这种运行方式天然具有“小说式时间压缩”。

---

# 8. DecisionPoint：Player 能动性的时间接口

Human Player 与 AI Player 应尽量共享相同的 DecisionPoint 模型。

DecisionPoint 不是固定每 N 秒产生。

更合理的触发条件是：

> **世界的信息状态或行动条件发生了足以重新开放能动性的变化。**

典型 DecisionPoint：

- 当前 Activity 完成；
- 遇见重要 Actor；
- 收到新信息；
- 原计划失败；
- 环境发生危险；
- 发现异常；
- 达到 Player 主动设定的 reconsider 时间；
- 世界规则明确要求立即选择。

因此 Player 的长期行动可以表达为：

```text
GoTo(NorthTower)

InterruptOn:
    Encounter
    Danger
    NewInformation

ReconsiderAt:
    +5 min
```

这比固定每走一格问一次更自然，也比“一下子锁死到目的地”更有控制感。

---

# 9. Human Player 与 AI Player 统一粒度的价值

统一 DecisionPoint 有几项重要收益。

## 9.1 世界规则公平

同一盘棋不需要知道某个 Player 是人还是 AI。

## 9.2 AI latency 不污染世界时间

AI 推理 8 秒，人类思考 30 秒，都属于 wall-clock。

在 DecisionPoint 内：

```text
ModelTime = paused
```

模型供应商 latency 不会让 AI 角色在游戏世界里无故损失时间。

## 9.3 Human trajectory 直接可用于 imitation learning

如果 Human 与 AI 共享：

```text
DecisionRequest → PlayerDecision
```

那么玩家授权共享的轨迹可以天然记录为：

```text
Observation
DecisionPointReason
AvailableInformation
ChosenAction
ExpectedOutcome
ActualOutcome
```

它比鼠标 clickstream 更接近未来 AI Player 所需训练样本。

尤其必须保存 Human 当时合法可见的 canonical Observation，而不是最终全知 WorldState。

---

# 10. 四种时间必须概念分离

建议从第一版就区分：

## Model Time

DramaBoard 世界里的时间。

例如：

```text
Day 3, 14:13:00
```

## Microstep

同一个 Model Time 内的因果顺序。

例如：

```text
14:13:00 / μ0
14:13:00 / μ1
14:13:00 / μ2
```

## Wall-Clock Time

LLM 与 Human 实际花费的现实时间。

## Presentation Time

Godot 为动画、镜头和视觉表现花费的现实时间。

这四者不应互相决定。

世界可以在 1 ms CPU 时间内跳过 5 个小时，也可以用 20 秒动画表现同一个 2 秒的戏剧事件。

---

# 11. 同时事件与 Superdense Time

事件驱动系统必须明确解决：

```text
14:00 Alice 到达门口
14:00 Bob 关门
14:00 炸弹爆炸
```

谁先发生？

不能依赖：

- PriorityQueue 插入顺序；
- 线程调度；
- LLM 谁先返回。

因此建议逻辑时间使用类似：

```text
(ModelTime, Microstep)
```

同一个物理时刻允许多个确定顺序的因果阶段。

初步可以考虑：

```text
μ0  scheduled / external events
μ1  direct rule consequences
μ2  derived events
μ3  perception / observation updates
μ4  DecisionPoint creation
```

具体阶段需要原型验证，但 `ModelTime + Microstep` 这个概念值得保留。

这会极大提升：

- replay determinism；
- Journal 可读性；
- 同时事件语义稳定性。

---

# 12. 随机事件仍然可以 Forecast

随机事件不意味着必须固定 Tick roll dice。

例如挖矿过程中“平均隔一段时间会发现特殊矿物”，可以在 Activity 创建时采样下一事件时间：

```text
DiscoveryEvent @ 09:47
```

Kernel 知道它。

Player 不知道它。

因此随机系统可以表现为：

> 确定性 flow + 随机 jump。

需要注意 Replay：

```text
Forecast()
    rng.Next()
```

如果被多调用一次就改变未来，是不可接受的。

所以随机 Forecast 必须有稳定身份，例如：

```text
ActivityId = Mining#73
Generation = 4
RandomStream = Discovery

→ sampled candidate = 09:47
```

在相同 WorldState / Generation 下重复 Forecast，应得到同样结果。

Journal 或 deterministic RNG 机制必须能够保证 Fork 与 Replay 的语义明确。

---

# 13. Speculative Cognition：用 Forecast 隐藏 LLM latency

Forecast 还有一个高度 AI Native 的用途。

假设：

```text
14:00 Leo → NorthTower
14:00 Alice → Market
```

Kernel 已经能够确定：

```text
如果双方当前计划不变，
14:03:17 两人将相遇。
```

Godot 仍然在播放移动动画。

系统可以提前构造预计相遇时 Alice 的 DecisionRequest，并调用 AI Player。

这相当于：

> **Speculative Cognition。**

如果 Forecast 成真，预计算的响应可以立即提交。

如果 Human 中途改道：

```text
ProjectedEncounter invalidated
```

该 AI response 直接丢弃。

必须保证：

> speculative inference 不能提前修改正式 Memory / Belief / Journal。

只有当预测事件真实发生后，才能 commit。

概念上类似事务或分支预测：

```text
snapshot
  ↓
speculate
  ↓
validate
  ├─ commit
  └─ discard
```

这可能成为产品体验上非常重要的 latency hiding 技术。

---

# 14. Simulation Kernel 的可能子系统模型

世界可以被拆成多个相对独立、能够 Forecast 的 subsystem。

例如：

- Travel / Navigation；
- Activity / Work；
- Combat；
- Needs / Fatigue；
- Weather；
- Social / Appointment；
- Investigation；
- Environment hazard；
- Timer / Deadline；
- Spawn / Population；
- Scripted world rule；
- Player reconsider scheduler。

每个 subsystem 最重要的能力不是 `Update(dt)`，而是：

```text
ForecastNext(world, now)

ApplyExternalEvent(...)

ResolveInternalEvent(...)
```

它们组合后形成全局 next-event simulation。

第一版不需要强求所有东西都使用同一抽象接口。

关键是保持一致的时间语义。

---

# 15. 不要把视觉导航变成世界真理

世界逻辑与 Godot movement 应分开。

例如 Core：

```text
Travel:
    Market → NorthTower
    logical duration = 8 min
```

Godot：

```text
NavMesh
Animation
Path following
```

视觉人物走了 38 秒还是 42 秒，不应改变世界逻辑中的 8 分钟。

反过来，Kernel 也可以提前知道 4 分钟后两人会相遇，而 Presentation 只负责把这段轨迹演出来。

这使：

> **离散棋盘底层 + 连续 RPG 表现**

能够同时成立。

---

# 16. Journal 与 Event Queue

需要明确区分：

## Forecast Queue

保存可能发生的未来。

可失效、可重算、不是历史。

## Journal

保存真正发生过的事件。

不可因为 Forecast 改变而改写。

因此：

```text
EventCandidate ≠ DomainEvent
```

只有 candidate 被验证并 resolve 后，产生的 DomainEvent 才进入 Journal。

这个区分对于 Replay、Debug、Speculative Cognition 都非常重要。

---

# 17. 原型阶段的实现策略

建议第一个 Simulation Kernel 极端追求正确性和可观察性，而不是性能。

可以接受：

- 每个真实事件后全量 Forecast；
- 单线程运行；
- 简单 PriorityQueue；
- 显式打印所有 EventCandidate；
- 显式显示失效 Forecast；
- 每个 DecisionPoint 暂停；
- 所有 RNG 可追踪；
- 完整 Journal。

第一个目标不是规模。

而是能够解释：

> 为什么世界时间从 09:00 直接跳到了 09:22？

> 为什么 Alice 在 09:22 获得 DecisionPoint？

> 哪个 Forecast 因为什么事件失效？

> 一个动作为什么被打断？

如果这些问题能从 Debug UI 和 Journal 中清楚回答，Kernel 的基础就比较稳。

---

# 18. 当前最大的设计风险

DEVS-like 架构不是万能的。

它天然喜欢：

- 有明确 phase 的活动；
- 可预测终点；
- 稀疏事件；
- 可枚举的中断条件；
- 较少主体的高意义模拟。

它不天然喜欢：

- 高频连续物理混沌；
- 大量强耦合、无法提前预测的实时动作；
- 需要每一帧精确控制的 twitch gameplay；
- 极高频局部碰撞；
- 依赖连续操作技能的动作游戏。

因此本项目的游戏设计应该主动顺着 Kernel 的优势设计，而不是强迫它模拟所有类型 RPG。

例如：

- 战斗更适合事件 / stance / intent / exchange 驱动，而不是精确 hitbox 格斗；
- 潜行可以围绕暴露概率与观察事件，而不是模拟每个脚步声波；
- 移动可以由 Activity 和 encounter 事件驱动；
- 调查可以由进度阶段与发现事件驱动；
- 社交天然适合 DecisionPoint。

哪些传统 RPG 系统需要重新设计，正是下一阶段最值得研究的问题。

---

# 19. 下一步：用 DEVS-like Kernel 重新解释 RPG 世界

下一轮设计应当逐系统回答：

## Movement

- 地图应该连续还是图结构？
- encounter 如何 Forecast？
- 中途改道如何处理？
- Human 玩家能多细粒度反悔？

## Combat

- 攻击是瞬时 Event 还是持续 Activity？
- 如何表达 feint、guard、interrupt？
- 如何避免退化成传统回合制？
- 如何避免连续物理成为 Kernel 负担？

## Investigation

- 搜查一个房间是什么 Activity？
- Evidence 什么时候产生？
- 随机发现和必然发现怎样统一？

## Social Interaction

- Conversation 是否暂停全局时间？
- 多角色谈话如何产生 DecisionPoint？
- interruption 怎样建模？

## Needs / Daily Life

- 睡眠、吃饭、疲劳怎样用 threshold Forecast？
- 哪些东西应该只是 lazy state，而不是事件？

## Stealth / Perception

- “看到某人”能否预测？
- line-of-sight 与注意力如何简化？
- 信息传播如何成为 Event？

## World Simulation

- 店铺营业、天气、日夜、社会 schedule 如何 Forecast？
- 普通 Rule Actor 如何用低成本 Activity 系统运行？

这些问题将决定 DramaBoard 是一款“使用 DEVS 的 RPG”，还是一款真正围绕事件驱动时间重新设计出来的新型游戏。

---

## 结语

传统游戏循环常常默认：

> 世界一直流动，玩家偶尔行动。

DramaBoard 可以反过来理解：

> **世界在可预测的部分迅速流过；只有当新信息、冲突或选择出现时，时间才重新变得昂贵。**

因此时间不是匀速资源。

它按照**信息密度**变速。

没有交互意义的几个小时，可以一瞬而过。

一个角色犹豫是否说出一句话的几秒钟，却可以展开成多个微小的 DecisionPoint。

这不仅是性能优化，也可能成为整个游戏的时间美学：

> **Forecast 负责看见即将到来的世界。**  
> **Elapse 负责掠过没有新信息的时间。**  
> **Event 负责让世界变得不同。**  
> **Decision 负责让 Player 再次拥有选择。**
