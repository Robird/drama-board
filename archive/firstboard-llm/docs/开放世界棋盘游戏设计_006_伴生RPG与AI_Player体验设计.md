# Design Note 006：伴生 RPG 作为 DramaBoard 的试金石
## ——从 Human + AI Player 的“共同旅程”反推产品体验与引擎能力

**状态：阶段性产品 / 功能设计笔记**  
**日期：2026-08-18**  
**代码审计基线：`main @ 593c744`**  
**定位：** 本文不是一份完整 GDD，也不要求立即实现其中全部机制。它试图回答一个更基础的问题：

> **DramaBoard 需要一款什么样的伴生游戏，才能像早期《Unreal》反过来推动 Unreal Engine 那样，用真实玩家体验持续逼出正确的引擎能力？**

本文承接此前关于 DramaBoard 的几个核心判断：

- 世界是棋盘，Player 是棋手，剧情是棋谱；
- Human Player 与 AI Player 应共享尽可能一致的世界规则和决策抽象；
- LLM 不能直接宣称客观世界结果；
- 世界时间采用 DEVS-like 的 `Forecast → Elapse → Event → DecisionPoint` 思路；
- 信息、关系、承诺、记忆与长期因果，应比传统数值资源更接近 DramaBoard 的核心；
- Providence 只能塑造机会条件，不能替 Player 改写意图。

---

# 1. 为什么 DramaBoard 需要一款真正能玩的伴生游戏

如果只从“通用 AI 游戏引擎”的角度设计 DramaBoard，很容易得到一套抽象漂亮、却没有被真实体验验证过的基础设施。

传统成熟游戏引擎往往并不是凭空规划出来的。实际作品持续提出：

- 这个场景需要什么；
- 玩家为什么觉得无聊；
- 角色为什么不自然；
- 时间系统为什么不好用；
- 哪种编辑能力反复出现；
- 哪种“通用抽象”其实没有第二个真实用例。

DramaBoard 尤其需要这种压力，因为它面对的核心 unknown 并不是：

> “怎么画一个 3D 人物？”

而是：

> **当 Human 与 LLM 驱动的 AI 真正以 Player 身份共享同一个世界时，怎样的游戏才好玩？**

因此伴生游戏首先是一件产品，其次也是一台**引擎需求发现仪**。

第一阶段目标不应是“展示 AI NPC 会聊天”，而应是：

> **找到一种如果没有 AI Player 就明显失色、而采用 AI Player 后整个玩法结构自然成立的游戏体验。**

---

# 2. 第一产品命题：玩家真正想带走的是“共同经历”

对于旅途型 RPG，一个很值得重视的心理假设是：

> 玩家未必真正需要一个无限大的世界；  
> 玩家更容易长期记住的是——**当时是谁和我一起在那里。**

地图、遗迹、港口、山道、篝火本身当然重要。

但真正产生情感价值的是：

- 某个人和我一起第一次看见它；
- 她还记得我们上次在那里发生过什么；
- 同一件事在她眼中有不同意义；
- 我们曾经争执、分开，又在另一个地方重逢；
- 很久以前一句话，在后来真的改变了她的决定。

传统 RPG 可以用脚本模拟这些效果。

DramaBoard 则有机会让它们成为**真实世界史的后果**。

因此第一款伴生游戏可以把一个非常朴素的体验承诺放到最前面：

> **关掉游戏以后，玩家想念的不是地图，而是那个和自己一起走过地图的人。**

这可以称为：

## Shared-History / Witness Value

“共同见证者”不是 flavor，而是一种产品机制。

---

# 3. 最小高价值演员表：Human + Companion + Antagonist

如果 LLM 成本和响应速度决定只能长期维持极少数真正的 AI Player，那么第一原型不应平均撒给十几个 NPC。

一个很有吸引力的最小结构是：

```text
Human Player
     ↕
Companion AI Player
     ↕
Antagonist AI Player
     ↕
Human Player
```

再加：

```text
Rule Actors
Providence（meta-world）
```

即可形成足够复杂的关系和策略网络。

> **范围说明：** 以下“红颜知己型 Companion”只是当前男性向原型的一个具体产品假设，用于压缩问题空间，并不意味着所有玩家、所有游戏或未来女性向版本都应采用同一种关系结构。

---

# 4. Companion AI：不是高级 Assistant，而是另一个正在旅行的人

第一核心 AI Player 的价值，不是“随时有一个聊天机器人陪玩家”。

如果底层 utility 仍然近似：

> 帮助 Human Player、同意 Human Player、配合 Human Player。

那么即使语言表现再生动，长期相处后仍然会出现明显的 Assistant 味。

一个真正值得建立依恋的 Companion 应具有：

- 私人 Desire；
- 私人 Fear；
- 不完全且可能错误的 Belief；
- 玩家不知道的 Secret；
- 自己想去的地方；
- 自己在乎的人；
- 自己不愿跨越的底线；
- 可以主动发现并提出事情；
- 有权不同意；
- 有权拒绝；
- 有权独自行动；
- 有权暂时离队；
- 在合理条件下有权重新回来；
- 会因为双方共同历史而改变未来判断。

一个极其重要的产品测试是：

> **她能不能在完全不靠作者临时 Prompt 强迫的情况下，对玩家说“不”？**

更进一步：

> 她说“不”以后，世界是否真的允许这个“不”产生长期后果？

这是区分：

```text
Character-shaped Assistant
```

和：

```text
AI Player
```

的关键。

## 4.1 Companion 的核心功能：Attachment / Continuity

Companion 并不需要承担所有剧情推进任务。

她更像世界中的：

> **情感连续性引擎。**

她不断让“现在发生的事情”与“过去共同经历过的事情”发生联系。

这使 Journal、Memory、Belief、Relationship 不再只是 AI 基础设施，而变成玩家可以直接感受到的产品价值。

---

# 5. Antagonist AI：第二个最值得支付 LLM 成本的角色

如果第二个长期运行的 AI Player 必须在几十名候选 NPC 中裁掉绝大多数，一个真正的主要 Antagonist 很可能是最值得保留的那个。

传统 Boss 常常是：

```text
Boss Phase 1
→ Boss Phase 2
→ Scripted Revelation
→ Final Battle
```

DramaBoard 的 Antagonist 可以是：

> **另一个持续和你下棋的人。**

它可以：

- 根据玩家行动改变计划；
- 对 Companion 形成独立认识；
- 先于玩家抵达某地；
- 留下间接后果；
- 利用自己的信息网络；
- 错误理解玩家；
- 被玩家误导；
- 与 Companion 有独立历史；
- 必要时合作；
- 并不一定是道德意义上的“纯坏人”。

玩家甚至可以长时间看不到反派本人，却不断看到：

> 他来过。  
> 他改变了这里。  
> 他正在做另一件事。  
> 他似乎已经预判了我的行动。

## 5.1 Antagonist 的核心功能：Pressure / Strategic Continuity

如果 Companion 解决的是：

> “为什么这段旅程对我有情感意义？”

Antagonist 更接近：

> “为什么我不能永远在世界里闲逛？”

因此可以粗略理解为：

```text
Companion  → Attachment / Continuity
Antagonist → Pressure / Counterplay
Providence → Opportunity / Causal Perturbation
```

三个系统功能彼此不同，不应互相替代。

---

# 6. AI Player Ergonomics：世界应该让模型做它擅长的事

DramaBoard 不应被设计成 LLM 能力考试。

目标不是难住 AI Player，而是让它稳定发挥：

- 角色动机；
- 社会推理；
- 语言；
- 关系解释；
- 局势重新理解；
- 模糊多目标取舍。

而把它不擅长、也没有产品价值的工作下沉到 Kernel / Tool / Controller。

因此第一款游戏应主动遵守以下原则。

## 6.1 世界必须能对 Player 说“不”

LLM 的 Intent 不能自动成为 Reality。

```text
我试图做 X
我预期 Y
↓
Law / Kernel
↓
Actual Outcome
```

失败、阻碍、误判和 prediction error 是“经历”的来源。

一个永远顺着语言生成结果的世界，不会真正形成 Player 感。

---

## 6.2 给 Player 局部知识，而不是上帝视角

AI Player 不应该知道：

- 全地图；
- 全部人物位置；
- 全部秘密；
- Providence 的预测；
- 真相数据库。

它应拥有：

> **主观 Observation + Belief + Memory。**

“我不知道 Bob 在哪里”应该是真实的世界状态，而不是一句角色扮演台词。

---

## 6.3 允许私人状态

一个真正的 Player 应拥有只属于自己的：

- Memory；
- Secret；
- Commitment；
- Belief；
- Goal；
- Relationship interpretation。

否则它只是另一个 World API client。

---

## 6.4 不要让 LLM 管理腿部肌肉

LLM 应该说：

> “我想从侧路绕进城。”  
> “我去旧港找 Alice。”  
> “我想在不被 Bob 发现的情况下观察他。”

不应该负责：

```text
move 1 tile
rotate 7°
move 1 tile
...
```

Navigation、精确路径、动画、局部避障应该属于更低频率的控制层。

---

## 6.5 允许无目的好奇

所有动作都服务于 Quest Progress，会使 AI Player 迅速退化成优化器。

世界应该允许：

> “山坡上似乎有点奇怪，我想去看看。”

哪怕最终没有装备奖励。

因为“无功利探索 + 共同经历”本身可能就是旅途产品的核心。

---

## 6.6 长期后果必须真实存在

AI Player 的长期感不是靠一次 Prompt 宣称出来的。

真正重要的是：

> 三小时前的话，现在回来找你了。  
> 上一局的承诺，这一局还留下痕迹。  
> 早期的误会，后来成为关系分叉点。

因此 Journal / Memory / Fork / persistent identity 都与产品体验直接相关。

---

# 7. 控制粒度：不是实时操纵身体，而是委托“语义行动”

DEVS-like Kernel 很可能天然决定了 DramaBoard 第一款游戏的手感不会是传统 WASD Action RPG。

Human 与 AI 更接近：

> **提交一个具有语义和持续时间的 Activity，然后让世界自己运行到值得重新考虑的时候。**

例如：

```text
去青石镇
调查遗迹
在这里等到日落
尾随商队
找 Alice 谈谈
休息到天亮
```

Kernel 负责：

```text
Forecast
→ Elapse
→ Event
→ DecisionPoint
```

而不是要求玩家操作每一步。

这使表现层仍然可以是：

- 2D JRPG；
- 固定镜头 3D；
- 风格化低模；
- 大量 Character Portrait；

但**交互粒度**更接近 Text ADV / GAL / command-based RPG。

---

# 8. DecisionPoint：Player 的“回合”来自信息变化，而不是固定轮次

核心体验可以不是：

```text
轮到 Alice
轮到 Bob
轮到 Human
```

而是：

```text
Human：前往北塔（预计 15 分钟）
Alice：调查市场（预计 8 分钟）
Bob：去见某人（预计 20 分钟）

世界推进……
```

然后在：

```text
+4 分钟：发生新事件
```

只唤醒真正受到影响的 Player。

这使世界拥有按**信息密度变速**的节奏：

- 空白旅行：快速跳过；
- 普通生活：分钟级；
- 重要交涉：细粒度；
- 战斗关键点：秒级；
- 具体动画和碰撞：交给表现 / controller 层。

---

# 9. Pass / ContinueCurrentIntent 必须成为 Human 与 AI 共享的概念

并非每个 DecisionPoint 都值得改变计划。

看到路边一辆马车：

```text
继续赶路
停下来观察
搭话
改道
...
```

Human Player 应可以：

> **继续原计划。**

AI Player 也一样。

因此未来比 `Wait` 更核心的概念可能是：

```text
ContinueCurrentIntent
```

或者某种通用：

```text
Pass / NoReconsideration
```

这使“打断机会”不会退化成：

> 游戏不停弹窗问你做不做事。

同时也让 AI Player 有机会明确表达：

> “我注意到了，但这不足以让我改变当前计划。”

这是有价值的决策轨迹。

---

# 10. Action Commitment 与 Interrupt Window：可以从《Grandia II》借“节奏”，而不是复制战斗系统

《Grandia II》的 IP Gauge 让角色到达 COM 点后选择行动，但行动不会立即发生；角色还需要沿时间条移动到 ACT 点，COM→ACT 之间可以被对手的 Cancel 类攻击打断。[G4]

这和 DramaBoard 的通用 Activity 模型高度契合：

```text
DecisionPoint
↓
Commit Activity
↓
Forecast completion
↓
World runs
↓
External Event?
├─ No  → Complete
└─ Yes → Interrupt / Reconsider / Continue
```

真正值得借鉴的不是某条具体战斗条，而是：

> **“决定行动”和“行动完成”之间存在可被世界改变的时间。**

这正好是 DEVS-like Kernel 的优势。

---

# 11. Camp / Safe Phase：大事件之间让角色重新成为“人”

《Fire Emblem: Radiant Dawn》的 Base Menu 出现在大量战斗章节之间，其中 `Info` 可以查看 Base Conversations；这些对话有的主要用于人物与背景塑造，有的会提供情报、物品、金钱或角色等实际收益。[G3]

这说明一个非常朴素但容易被现代开放世界忽视的节奏：

> **高压事件之后，需要一个低压的人物沉淀区。**

对 DramaBoard 来说，这可以从“菜单”升级为真正的世界阶段。

例如：

## Camp Phase

- 生火；
- 休息；
- 分配物品；
- 处理伤势；
- 整理今日信息；
- 主动聊天；
- 某 AI Player 找另一个人单独谈；
- 某人沉默；
- 某人离营；
- 复盘白天发生的事情；
- 形成第二天的新 Intent。

LLM 在这个阶段特别有价值，因为：

- 不要求低延迟；
- 对话可以从真实 Journal 中自然长出来；
- 关系变化有安全空间展开；
- 玩家不需要为了“刷对话”到处撞 NPC trigger。

因此 Camp/Safe Phase 很可能应该是第一款游戏的一等节奏，而不是装饰 UI。

---

# 12. 信息就是玩法：《Final Fantasy II》的古早启发

《Final Fantasy II》的 Key Term / Keyword 系统允许玩家在对话中 Learn 关键术语，并在之后使用 Ask 向其他角色询问这些术语；手册也明确把 Learn / Ask 作为对话系统的一部分。[G1]

它在当时仍然主要是脚本式剧情推进。

但其结构在 DramaBoard 中可以被真正泛化：

```text
从 A 得知 Concept / Fact
↓
进入我的 Belief / Memory
↓
我决定向 B 询问
↓
B 根据自己的 Belief / Motivation 回答
↓
新的信息传播
```

因此知识不应只是：

> UI 上“Quest Updated”。

而可以成为 Player 真正可以：

- 携带；
- 询问；
- 隐瞒；
- 交换；
- 验证；
- 伪造；
- 误解；

的世界资源。

这与 DramaBoard 已经形成的：

```text
Observation
KnownFact
Secret
Evidence
Rumor
Belief
```

方向完全一致。

---

# 13. 《Metal Max》：开放感来自“世界允许你自己决定为什么出发”

初代《Metal Max》是 1991 年由 Crea-Tech / Data East 推出的非线性 RPG，其设计者宫冈宽后来回顾时明确强调过它对当时王道 RPG 的反叛和自由取向；他甚至把“没有传统幻想中那种神明引导”作为世界设计意图的一部分，并让玩家能够选择“退休”而提前结束冒险。[G2]

对 DramaBoard 来说，这里最值得借鉴的不是战车系统，而是：

> **世界有大量可以做的事情，却没有一只作者之手始终把玩家拖向下一条主线。**

因此第一款伴生游戏可以拥有：

- 目的地；
- 长期威胁；
- 大反派；
- 世界级稀缺目标；

但尽量不要拥有：

```text
MainQuestStep = 37
```

角色旅行的理由应该主要来自：

```text
Desire
Fear
Relationship
Information
World Events
```

而不是脚本游标。

---

# 14. 《Radical Dreamers》：语义选择足以构成“行动”

1996 年 Square 的《Radical Dreamers -盗めない宝石-》本身就是以文本叙述与选项为主要交互方式的作品；探索和战斗都大量通过语义选择推进，一些选择存在隐藏时间限制，战斗中犹豫可能直接带来受伤或失败后果。[G5]

DramaBoard 不一定要复制其“隐藏计时器”——对现代玩家来说这甚至可能显得不公平。

真正有价值的启发是：

> **“行动”可以是一句有意义的选择，而不需要 60 帧连续手柄操作。**

例如：

```text
“追上去。”
“让她走。”
“先检查伤者。”
“什么都不做，继续观察。”
```

对于 DEVS-like 世界，这种语义粒度反而是天然交互单位。

---

# 15. 五款老游戏可以拆成五种“祖传零件”

| 作品 | 可验证机制 / 体验 | 对 DramaBoard 的可迁移启发 |
|---|---|---|
| **Final Fantasy II** | Learn / Ask Key Term | **Information Gameplay**：知识可以被获取、携带和用于下一次交互 |
| **Metal Max** | 非线性旅行、弱“神谕式”主线约束 | **World Structure**：世界允许自己选择理由与方向 |
| **Fire Emblem: Radiant Dawn** | 战斗间 Base / Info / Base Conversations | **Rhythm**：高压事件之间需要人物沉淀的安全阶段 |
| **Grandia II** | COM→ACT 的行动准备时间，期间可 Cancel | **Temporal Interaction**：决定与完成分离，世界可以打断 committed action |
| **Radical Dreamers** | 文本探索 / 情境选择，部分 timed choice | **Interaction Granularity**：语义行动本身可以构成完整游戏操作 |

这五种元素拼在一起，已经很接近一款为 Human + AI Player 从底层重新设计的 RPG：

> **开放旅途 + 信息游戏 + 事件驱动行动 + 安全关系阶段 + 语义级交互。**

---

# 16. 第一款伴生游戏的候选形状：一段有终点的开放旅程

相比：

> 巨型 seamless open world

更适合第一阶段的可能是：

# 有终点、无固定路线的旅程

例如：

- 一个长程目标；
- 6–10 个高密度地区；
- 多条连接路线；
- 每个地区少量高意义地点；
- 临时人物与 Rule Actors；
- 数个可以跨地区发展的世界事件；
- Human + Companion 一起旅行；
- Antagonist 在世界另一处沿自己的 trajectory 行动。

这种结构天然产生：

- 出发；
- 抵达；
- 离别；
- 重逢；
- 夜宿；
- 新地区；
- 路线选择；
- 临时资源；
- 陌生关系；
- 共同见证；
- 反派留下的痕迹。

同时不会要求项目模拟：

> 一个百万居民城市的完整经济系统。

---

# 17. 世界可以“小”，但必须密

不要用地图面积衡量开放感。

第一作宁可只有：

> 七个真正有记忆点的地方。

也不要拥有：

> 一百平方公里没有关系意义的草地。

每一个地区可以提供若干**因果模板**：

- 陌生人求助；
- 双方争夺；
- 错误指控；
- 稀缺资源；
- 有代价的捷径；
- 可隐瞒的秘密；
- 短窗口机会；
- 双重义务；
- 共同敌人；
- 暂时结盟。

Providence / Scenario Template 可以根据当前关系和历史进行实例化、mutation 与 fork。

因此“地图内容量”不完全等于“人工写剧情量”。

---

# 18. 第一款游戏应该主动逼 DramaBoard 长出哪些能力

伴生游戏不是 Kernel Showcase。

它应该故意选取能反复压测关键抽象的玩法。

建议优先逼出：

### Travel

- 有持续时间；
- 可以 Forecast；
- 可以遭遇事件；
- 可以中断 / 重新考虑；
- 可以抵达。

### Encounter

- 不是 Prompt 硬安排；
- 来自世界时空；
- 可以由 Providence 轻量提高机会；
- Player 可以选择忽略。

### Information

- Observation；
- KnownFact；
- Secret；
- Rumor；
- Evidence；
- provenance；
- 传递 / 隐瞒 / 验证。

### Relationship

先通过真实历史和 Memory 表现。

不要太早退化成单一：

```text
Affection = 73
```

### Object / Inventory

只做真正影响决策的物品：

- 钥匙；
- 信件；
- 信物；
- 药；
- 有争夺价值的资源。

避免先做大量装备刷取。

### Commitment / Promise

“明天在这里见。”

应成为真实的未来世界约束和记忆，而非一句对白。

### Investigation

重点是：

> 找到信息，形成 Belief。

而不是复杂像素级搜图。

### Combat

让 LLM 决定：

- 为什么打；
- 打谁；
- 是否撤退；
- 是否愿意杀；
- 战术目标。

低层格挡 / 动画 / 具体招式可以交给规则 Controller。

### Separation / Reunion

这是 Companion 真实性的重要压力测试。

### Camp / Safe Phase

让关系与记忆自然沉淀。

### Long-Horizon Plan

允许：

> 目标保持 + 局部 replan，

而不是要求 LLM 一次输出 40 步最优计划。

### Providence

只创造 Opportunity。

不改写 Player 内部动机。

### Journal / Fork

不仅用于 Debug，还是未来：

- Scenario Forge；
- Counterfactual；
- RL trajectory；
- Companion history

的基础。

---

# 19. 当前代码已经有哪些真正可用的骨架

本节来自 2026-08-18 对 `main @ 593c744` 的本地只读代码审计，不是根据设计文档推测。

## 19.1 时间与调度已经是真实实现

`src/Kernel/Time` 中已经存在：

- `ModelTime`
- `ModelDuration`

并使用与现实日历无关的毫秒 tick。

`LogicalTimestamp` 已经按照：

```text
(ModelTime, Microstep)
```

表达同一世界时间上的确定性因果顺序。

`ForecastQueue` / `EventCandidate` 已经按：

- due time；
- source ID；
- candidate ID；

形成确定性排序。

`SimulationLoop` 在每次 resolve 后重新从所有 `ISimSystem.ForecastNext` 构建候选集合。

这意味着目前采用的是：

> **简单但正确的全量 re-forecast。**

旧 Forecast 的失效通过“不再被新世界状态重新预测出来”隐式完成，暂时没有复杂 cancellation API。

这个选择非常适合当前原型期。

---

## 19.2 Activity 已经有一个很小的真实种子

当前 `FirstBoard` 只有：

```text
BoardActivityKind.Travel
BoardActivityKind.Wait
```

并由 `ActivityCompletionSystem` 完成。

这很好。

它证明了：

> “Player 提交有持续时间的 Activity → Kernel Forecast 完成事件”

这条路径是可行的。

但证据还远远不足以抽象一个巨型 universal activity framework。

---

## 19.3 Player Boundary 已经非常接近我们需要的方向

当前接口是：

```text
IPlayerDriver.DecideAsync(
    DecisionRequest,
    CancellationToken)
```

已有：

- `NullPlayerDriver`
- `RandomPlayerDriver`
- `ScriptedPlayerDriver`
- `LlmPlayerDriver`

还没有：

```text
HumanPlayerDriver
```

这其实是好消息：

> Human 应该作为新的 `IPlayerDriver` adapter 长出来，而不是重新发明一套 Player hierarchy。

`DecisionRequest` 已经携带：

- subjective `Observation`；
- affordances；
- decision reason；
- decision ID；
- journal-prefix version；
- lineage。

`PlayerDecision` 返回：

- correlated `Intent`；
- optional `ExpectedOutcome`。

其中 `ExpectedOutcome` 当前还没有真正进入后续翻译逻辑，这恰好可以留给后续“Intent + Expectation → Actual Outcome”深化。

---

## 19.4 同时决策与 stale answer 已经考虑

当前 host 会：

- 验证 Decision ID；
- 验证 journal prefix；
- 验证 lineage；
- 识别 stale request；
- 对同时需要行动的 actors 批量收集答案，再作为一个 external-input batch 提交。

这非常重要。

因为 Human 与多个 AI Player 真正共享一个世界时：

> **LLM 谁先响应，不能决定世界里的谁先行动。**

当前骨架已经在保护这条原则。

---

# 20. 当前实现与伴生 RPG 之间最重要的压力差

## 20.1 现在没有真正的 Pass / ContinueCurrentIntent

当前很多“不行动”语义由：

```text
Wait
```

代替。

甚至 null、LLM format failure、forced fallback 最终也可能退化成 Wait。

对第一款伴生游戏来说应该逐渐区分：

```text
我决定等待
```

与：

```text
我看见了这个新情况，但继续执行原计划
```

后者是非常重要的 Player Decision。

---

## 20.2 Interruption 已经有萌芽，但还不统一

当前 FirstBoard 中，如果正在 `Wait` 的 Actor：

- 被 Talk；
- 被 Show；
- 目击公共放置 Object；

Wait 会被取消，generation 改变后原 completion forecast 自然消失。

但：

- Travel 目前不可中断；
- `DecisionReasons.Interrupted` 已定义但未使用；
- 没有显式 interruption event；
- 没有 remaining duration；
- 没有 resume / reconsider protocol。

这几乎正好是第一款旅途游戏最应该继续施压的方向。

---

## 20.3 当前九类世界动作已经足够证明“语义行动”路线

FirstBoard 当前已实现的 action family 包含：

- travel；
- wait；
- talk；
- observe / inspect；
- take；
- put；
- give；
- show；
- use。

其中：

- Travel 有持续时间；
- Wait 可以指定时长 / 时间点；
- 其他多数目前即时 resolve；
- Talk 可以通过 `fact:<kind>` 共享已知 Fact；
- Observe 能发现可见 Actor / Object，并能验证 / 阅读 letter；
- Take contention 使用确定性随机；
- Give / Put / Show 已经区分所有权转移、公共放置和展示；
- Use 可以用 brass key 打开 cellar chest；
- 世界还有 timed cellar deadline。

这些动作已经非常接近：

> **以语义交互而不是身体操作组成游戏。**

---

# 21. 第一批不要急着“通用化”的东西

代码审计特别支持一个原则：

> **继续让伴生游戏逼出第二个真实用例，再升级抽象。**

现在不建议先做：

- Universal Action Class Hierarchy；
- Generic Phase Engine；
- Formal Epistemic Logic；
- Universal Activity DSL；
- Providence Class Family；
- RuleActor 大框架；
- 超复杂 Relationship 数值模型。

例如：

> Camp Phase

完全可以先作为第一款游戏的具体 World State + Systems 做出来。

等第二个场景也需要类似结构，再提炼。

Providence 同理。

---

# 22. 当前值得特别防止的几个耦合陷阱

本地审计已经发现几个未来伴生游戏可能很快撞上的点。

## 22.1 DecisionSchedulingSystem 当前假定 idle BoardActor 都是 Player-driven

以后出现：

- Rule Actor；
- 动物；
- 店主；
- 交通工具；
- Providence-controlled world actor；

时，这个假设会开始受压。

但不要现在就设计完整 Actor ontology。

先保留演进空间即可。

---

## 22.2 Intent / AvailableAction 的字段结构可能被复杂语义动作撑爆

当前协议已经固定了一些：

- actor；
- object；
- destination；
- free-text；
- time slots。

当未来出现：

> “不被 Alice 看见地尾随 Bob”

这种组合 Intent 时，不要不断追加几十个 nullable field。

但同样不应在没有真实动作样本前先做 DSL。

建议继续：

> **缺什么补什么 → 收集十几二十个真实动作 → 第二轮抽 action algebra。**

---

## 22.3 LLM Memory 目前不属于 committed World Journal

这对 Demo 没问题。

但对未来：

- Replay；
- Fork；
- AI identity；
- Separation / Reunion；
- Counterfactual；
- RL trajectory；

会成为明显压力。

最终需要解决：

> **世界 checkpoint 与 Player cognition checkpoint 怎样对齐。**

这不是要求立即把所有 LLM memory event-source 化，而是必须意识到它将成为产品能力边界。

---

## 22.4 FirstBoard 的 Fact identity 已经露出扩大演员表后的歧义风险

当前 `KnownFacts` 身份包含：

```text
(Kind, RelatedId)
```

但 `ApplySpoke` 在共享时按 kind 选 Fact。

两个人的小场景问题不大。

演员和秘密一多：

> 同一种 Fact kind 可能对应多个对象。

这正是“Information Gameplay”继续成长时很快会碰到的真实压力。

---

# 23. 第一阶段产品原型的最小闭环

不需要先做一部 40 小时 JRPG。

可以做：

## Journey Slice

```text
Human Player
Companion AI
Antagonist AI

3–4 个地区
若干 Rule Actors
2–3 个 Camp
1 个长期目标
1 个跨地区 Secret
1 次真实分离 / 重逢可能
1 个可被反派利用的信任问题
```

要求完整出现：

```text
一起出发
↓
Travel
↓
获得新信息
↓
双方看法不同
↓
选择不同
↓
共同经历
↓
Camp 沉淀
↓
Antagonist 产生间接压力
↓
一次关系 / 计划分叉
↓
重新汇合或进一步分离
↓
阶段性终局
```

如果这个 Slice 在简陋 UI 下就让玩家产生：

> “她居然会这样做。”

以及：

> “我想知道下一站她会怎样。”

那么 DramaBoard 的产品方向就成立了。

---

# 24. 建议接下来优先做的实验

## Experiment A：HumanPlayerDriver

把当前相同 `DecisionRequest` 真正交给 Human。

目标不是 UI 漂亮。

只验证：

> Human 与 LLM 是否真的能共享同一套 DecisionPoint / Intent pipeline。

---

## Experiment B：Continue / Reconsider

为正在执行的 Activity 引入最小的：

```text
ContinueCurrentIntent
Reconsider
```

用一个 Travel encounter 场景验证。

---

## Experiment C：Interruptible Travel

例如：

> 去市场途中看见 Companion 被人带走。

验证：

- Forecast；
- interruption；
- DecisionReason；
- continue；
- reroute；
- Journal

能否完整工作。

---

## Experiment D：Camp Slice

一天结束后进入 Safe Phase。

不写任何固定对话。

只给：

- 当天 Journal；
- 角色当前 Belief；
- affordances。

观察 AI Player 会不会主动：

- 复盘；
- 询问；
- 回避；
- 争执；
- 分享东西。

---

## Experiment E：一个跨人物的信息对象

不要只传 `fact:<kind>`。

做一个真正需要：

```text
Source
Subject
Claim
Evidence
Confidence / provenance
```

的 Secret / Evidence 小场景。

检验 Information Gameplay 的真实数据压力。

---

## Experiment F：Companion Says No

设计一个：

> Human 想做 X，但 Companion 有充分角色理由反对。

严禁 Prompt 里写：

> “这次你必须拒绝玩家。”

只通过：

- Character；
- Belief；
- Desire；
- World consequence；

看 AI Player 能不能自然拒绝。

这是一个非常值得长期保留的 behavioral regression scenario。

---

## Experiment G：Antagonist as Remote Player

先不要做 Final Boss。

让 Antagonist 在世界另一端：

- Travel；
- Investigate；
- Acquire；
- Talk；
- Commit。

玩家只能通过世界痕迹逐渐感知它。

这将很好地测试：

> “一个不在镜头里的 AI Player 是否仍然能给世界施加持续策略压力。”

---

# 25. 衡量第一款伴生游戏是否成功，不应先看“AI 对话有多聪明”

更重要的指标可能是：

### Shared History Density

有多少当前互动真实依赖过去共同经历？

### Initiative

AI Companion 有多少重要行动不是对 Human prompt 的直接响应？

### Disagreement Integrity

角色在合理情况下是否能维持自己的立场？

### Consequence Persistence

早期决定是否在后续真实产生可见后果？

### Information Play

Player 是否真的因为“知道什么 / 不知道什么”而改变行动？

### Meaningful Reconsideration

DecisionPoint 是否集中在真正值得重新决策的地方？

### World Pushback

Player 的期待与世界结果是否存在真实差异？

### Relationship Without Script

是否出现作者没有明确写下的、但事后可解释的关系变化？

---

# 26. 阶段性结论

DramaBoard 第一款伴生游戏不应该是：

> **传统 JRPG + 给几个 NPC 接聊天模型。**

更值得探索的是：

> **一款从 Player abstraction、世界时间、信息、记忆和长期因果开始，就为 Human 与 AI 一起参与而设计的 RPG。**

它可以看起来像 JRPG。

它可以拥有：

- 城镇；
- 荒野；
- 战斗；
- 旅馆；
- 篝火；
- 人物立绘。

但它底层的游戏操作更像：

```text
我决定做什么
↓
我把一段未来交给世界
↓
世界自行运行
↓
新信息出现
↓
我是否改变决定
```

其中最核心的两个 AI Player 暂时可以是：

> **一个让我舍不得这段旅程结束的人；  
> 一个让我不能只是漫无目的闲逛的人。**

前者建立依恋。

后者制造压力。

而世界、Providence、Journal 与 DEVS-like Kernel 共同保证：

> 他们都不是剧情道具，而是真正参与这盘棋的人。

如果这个伴生游戏能够成立，那么它不仅会成为 DramaBoard 的 showcase。

它会不断告诉我们：

> **DramaBoard 到底应该是什么。**

---

# References / 设计素材来源

## [G1] Final Fantasy II — Key Term / Learn / Ask

- *Final Fantasy I & II: Dawn of Souls* manual scan，Key-Term System 部分：  
  https://www.phantomcastle.it/phantom/ffspiritscastle/ff1/Final%20Fantasy%20I%20%26%20Ii%20-%20Dawn%20Of%20Souls%20%28Gba%29%20-%20Manual.pdf
- Final Fantasy II GameFAQs walkthrough，对 Learn / Ask 机制的具体说明：  
  https://gamefaqs.gamespot.com/nes/563414-final-fantasy-ii/faqs/70140

**本文使用方式：** 只借鉴“知识被学习并用于后续人物交互”的结构，不主张复刻 FFII 的脚本式 Key Term 系统。

---

## [G2] Metal Max — 自由、非线性与“没有神明引导”的设计取向

- 宫冈宽访谈（電ファミニコゲーマー），讨论初代《Metal Max》的设计自由、退休机制，以及希望表现“不依赖传统幻想式神明引导”的世界：  
  https://news.denfaminicogamer.jp/interview/180418
- 同一访谈后续页：  
  https://news.denfaminicogamer.jp/interview/180418/2
- 2014 开发者访谈英文翻译：  
  https://shmuplations.com/metalmax/

**本文使用方式：** 借鉴“世界提供行动空间，而不是主线脚本持续牵引”的结构，不主张复制战车 / 赏金系统。

---

## [G3] Fire Emblem: Radiant Dawn — Base / Info / Base Conversations

- Serenes Forest，Radiant Dawn Base Conversations 列表与条件：  
  https://serenesforest.net/radiant-dawn/miscellaneous/base-conversations/
- GameFAQs，Radiant Dawn Base Conversation FAQ，对 `Info` 中 Base Conversation 的功能分类说明：  
  https://gamefaqs.gamespot.com/wii/932999-fire-emblem-radiant-dawn/faqs/51037

**本文使用方式：** 借鉴“高压战斗与低压人物阶段交替”的节奏，进一步推导 LLM 时代的 Camp/Safe Phase。

---

## [G4] Grandia II — IP Gauge / COM → ACT / Cancel

- RPGFan，Grandia II review，对 COM、ACT、行动执行与 Cancel 的说明：  
  https://www.rpgfan.com/review/grandia-ii-4/
- GameFAQs / Neoseeker 的系统攻略也记录了 COM→ACT 区间可被 Cancel：  
  https://gamefaqs.gamespot.com/dreamcast/197485-grandia-ii/faqs/30107  
  https://www.neoseeker.com/grandia2/faqs/63095-grandia-ii-a.html

**本文使用方式：** 借鉴“Decision 与 Action Completion 在时间上分离，因此 committed action 可以被世界打断”的节奏。

---

## [G5] Radical Dreamers -盗めない宝石-

- Square Enix，《Chrono Cross: The Radical Dreamers Edition》官方页面，确认 Radical Dreamers 的官方收录：  
  https://www.jp.square-enix.com/cc_rd/
- Radical Dreamers gameplay 概述，文本选择与部分 invisible timer 机制：  
  https://en.wikipedia.org/wiki/Radical_Dreamers
- RPG Site walkthrough，可用于交叉验证文本探索与选项式推进：  
  https://www.rpgsite.net/feature/12630-radical-dreamers-walkthrough-guide

**本文使用方式：** 借鉴“语义选择本身即是完整操作单位”以及“某些 Decision Window 可以有时间意义”；不主张复制隐藏计时器这一具体 UX。

---

# Repository Audit Note

本文“当前代码”章节来自通过 Local Codex Bridge 对：

```text
E:\repos\drama-board
main @ 593c744
```

进行的只读审计，范围包括：

- `src/Kernel`
- `src/FirstBoard.Demo`
- 直接引用的 `FirstBoard`
- `Host`
- `Protocol`
- `Player.Llm`

审计时工作树与 `origin/main` 一致且 clean；未修改代码。由于该仓库仍处于快速原型阶段，本文描述的当前实现应被视为 **2026-08-18 的时间截面**，而非永久 API 规范。
