# Design Note 004：Providence、Drama Management 与因果模板 —— 面向戏剧轨迹生成和环境课程设计的功能架构

**状态：概念 / 功能设计草案**  
**日期：2026-08-17**  
**定位：在 Design Note 001–003 已确立的 DramaBoard、Player Boundary、Journal/Fork 与 DEVS-like Simulation Kernel 基础上，讨论一种受约束的“命运干预”层（Providence），以及由此延伸出的自动戏剧动力维持、决策边界探索、对抗式环境设计、Scenario Pool 与可泛化因果模板。本文的直接用途，是向后续软件工程设计提供演进方向与不可破坏的语义边界；并不要求当前原型一次性实现全部功能。**

---

## 1. 问题来源：自主角色不会自动产生戏剧

DramaBoard 的基本立场是：

> **世界是棋盘，Player 是棋手；剧情是棋谱，而不是预写剧情树。**

这意味着 Simulation Kernel 应尽可能保持“无情”：

- 角色是否相遇，由时间、空间与行动决定；
- 信息是否获得，由客观世界与感知规则决定；
- Player 可以形成自己的意图，但不能直接宣称世界结果；
- 世界不会因为某个角色是“主角”而暗改规则。

这一原则保证了：

- Replay / Fork 的可信性；
- RL Gym 的可解释性；
- Human / AI Player 的规则公平；
- 真正的 emergent narrative。

但第一批实际模拟很快暴露了一个重要事实：

> **局部合理的自主行为，并不保证全局具有戏剧动力。**

两个 Role Agent 完全可能在同一个小世界中持续彼此错过；多个角色也可能进入长期无新信息、无冲突、无关系变化的“垃圾时间”。

最直接的解决方法，是修改 Prompt，让角色主动去接触另一角色。

但这会混淆两个本应分离的层次：

```text
“这个角色自己为什么想见对方？”
vs.
“世界为什么恰好给了他们一次相遇机会？”
```

如果为了全局故事需要而修改 Player 内部动机，实际上是在偷偷夺取 Player agency。

因此需要探索一种更克制的机制：

> **不替 Player 作决定，只在合法世界因果链上，以有限成本改变机会结构。**

本文暂称这一层为：

# Providence

中文可理解为：

- 命运；
- 天意；
- 因缘拨动；
- 受约束的戏剧干预。

它和传统互动叙事中的 **Drama Manager / Experience Manager** 有明显亲缘关系，但 DramaBoard 希望赋予它更严格的本体边界。

---

# 2. 前人工作的直接启发

本节区分“已有研究”与“DramaBoard 自己的推导”。

## 2.1 Drama Management：后台观察并有限干预故事

Search-Based Drama Management 将 drama management 表述成一个搜索问题：给定 plot points、Drama Manager 可以执行的动作，以及对故事质量的评价函数，由搜索寻找能够改善体验的干预策略。[R1]

Declarative Optimization-Based Drama Management（DODM）进一步把：

- 故事情节；
- 可用干预动作；
- 故事质量评价；

明确分离，再由优化算法选择干预。[R2]

后续关于 **authorial leverage** 的研究特别指出，Drama Manager 的意义之一，是让作者用较少的显式脚本获得更大的非线性故事空间，而不是人工枚举所有分支。[R3]

这与 DramaBoard 的目标高度一致：

> **我们需要的不是更多作者手写剧情，而是一个能把少量设计投入放大成大量有效轨迹的系统。**

近年来的 Shepherd 工作还展示了一条特别相关的方向：持续分析正在运行的模拟，从中寻找已经具有叙事潜力、如果继续跟进可能变得更有趣的事件结构。[R4]

这与本文提出的“发现垃圾时间 / 发现潜在决策边界 / 选择是否进行小干预”非常接近。

---

## 2.2 自动环境设计：不是手工设计所有关卡，而是寻找 Player 的能力边界

Unsupervised Environment Design（UED）提出：开发者不必预先枚举完整任务分布，而可以提供一个带可变参数的环境空间，再自动生成训练环境。[R5]

PAIRED 的核心思路尤其重要：

> **环境生成器不应只制造“最难”的世界，而应寻找困难但仍有学习价值的世界。**

PAIRED 用 protagonist 与 antagonist 的表现差异（regret）作为环境生成信号，从而自动形成越来越复杂的 curriculum，并改善新环境上的泛化。[R5]

之后的 PLR、minimax-regret refinement、动态环境生成等工作继续研究：

- 怎样保留高价值环境；
- 怎样避免生成器只制造无解任务；
- 怎样识别“参数只改变一点，所需策略却突然复杂很多”的关键区域；
- 怎样让环境课程随 Agent 能力演化。[R6][R7][R8]

这与 DramaBoard 的一个自然目标几乎同构：

> **寻找环境的小变化在哪里会导致 Player 的策略发生相变。**

例如：

```text
场景 S0：Alice 与 Bob 稳定合作
场景 S1：增加一条轻微可疑信息 —— 仍然合作
场景 S2：再增加一次可获私利的隐瞒机会 —— 开始出现分裂
场景 S3：背叛收益略增 —— 大量轨迹转为背叛
```

真正高价值的不是 S0 或 S3 本身，而是：

> **S1 ↔ S2 附近的决策边界。**

---

## 2.3 从“生成故事”升级为“生成故事世界和机制”

互动叙事研究过去经常默认 story world 与 mechanics 已经由设计者提供。

2023 年的 *Evolving Interactive Narrative Worlds* 明确尝试用 evolutionary search 生成交互叙事的世界与机制本身，而不只是在固定世界中生成剧情。[R9]

更早的研究也尝试向已有 story world **添加新的 plot/world events**，通过进化优化寻找能提高整体体验的新增内容，而不是直接指导 Player 去做某事。[R10]

这些工作支持一个很重要的区分：

```text
直接操纵 Player
        vs.
修改 Player 所处的因果环境
```

DramaBoard 的 Providence 应优先属于后者。

---

# 3. DramaBoard 的四层本体

建议从概念上明确四层，不要求立即对应四个独立进程或大型模块。

## 3.1 Law —— “大道无情”

即 Simulation Kernel 与客观世界规则。

职责：

- 时间；
- 因果；
- 位置；
- 感知；
- 资源；
- Action legality；
- Forecast / Event；
- 随机过程；
- 世界状态转移。

原则：

> **Law 不认识“剧情需要”。**

Providence 不允许直接篡改：

- Player 的内部状态；
- 已发生的 Journal；
- Kernel 的判定规则；
- 为某个 Player 临时改变物理定律。

如果 Alice 与 Bob 具有相同属性和处境，同一种规则必须同样适用。

---

## 3.2 Players —— “众生”

Human Player / AI Player 均在这一层。

Player 根据合法 Observation：

- 形成 Belief；
- 产生 Desire / Fear；
- 决定 Intent；
- 选择行动；
- 承担结果。

Providence 最重要的限制是：

> **Providence 可以塑造 Opportunity，但不能替 Player 选择 Intention。**

例如：

允许：

> 路人告诉 Alice：“我刚才似乎在旧港见过 Bob。”

不允许：

> 修改 Alice 的 Prompt：“你突然非常想去见 Bob。”

前者改变世界中的信息条件；后者直接篡改主体。

---

## 3.3 Providence —— “拨动因缘”

Providence 是一个可选的、高层的世界控制器。

它观察足够多的世界信息和轨迹统计，决定：

```text
NoOp
```

或者提交一个**合法的 World Intervention**。

Providence 的理想行为不是：

> “我要把故事变成我想要的样子。”

而是：

> **“如果世界自己还会产生有意义的发展，就不干预；只有当轨迹进入低信息价值区域，或者存在值得探索的策略边界时，以最小干预创造新的机会条件。”**

因此 `NoOp` 必须是默认、廉价而常见的动作。

---

## 3.4 Curator —— “什么轨迹值得保存和继续探索”

Providence 不应该同时兼任自己成功与否的最终裁判，否则很容易 Goodhart。

因此概念上应有一个独立的 Curator / Scenario Evaluator。

Curator 不决定角色行为，也不直接改变世界。

它负责分析：

- 本局是否有信息价值；
- 是否出现新的决策边界；
- 某个 Fork 是否值得继续扩展；
- 哪个场景应进入 Scenario Pool；
- 哪些只是重复、无解或强迫性的垃圾样本。

在最初阶段，Curator 完全可以只是：

- 离线统计；
- 人工规则；
- LLM 分析器；
- 实验脚本；

不需要成为实时系统。

---

# 4. Providence 的核心设计原则：最小因，长程果

Providence 最有价值的干预，应该具备以下特点：

1. **干预很小；**
2. **干预合法；**
3. **Player 仍然拥有真实选择；**
4. **长期轨迹发生显著分叉。**

可以定义一个粗略的“Providence Leverage”概念：

```text
Leverage(I)
    ≈ TrajectoryDifference(with I, without I)
      ---------------------------------------
             InterventionCost(I)
```

它不是当前必须实现的数学公式，而是设计指导。

例如：

高杠杆：

> 一个陌生人给出一句真假不明的线索，最终改变两名 Player 的关系。

低质量强干预：

> 系统直接把两人传送到同一地点，并强制他们成为盟友。

后者虽然对轨迹影响巨大，但：

- intervention cost 高；
- agency violation 高；
- 学习价值低；
- 容易生成“主角光环”式伪轨迹。

因此 Providence 的目标应更接近：

> **用最小因果干预触及真实的 Player 决策边界。**

---

# 5. Providence 应操作什么，不应操作什么

建议把干预能力设计为一个**显式、受限、可扩展的 action space**。

## 5.1 合法干预类型

### Information Intervention

例如：

- 产生一条传闻；
- 投递一封信；
- 让某个 Rule Actor 提供目击信息；
- 暴露或隐藏一个原本可被发现的线索；
- 增加证据可靠性的歧义。

重点：

> 信息进入世界以后，Player 信不信由 Player 决定。

---

### Opportunity / Resource Intervention

例如：

- 出现一份可竞争的短期利益；
- 某件稀缺物品暂时变得可取得；
- 引入一个需要合作才能利用的机会；
- 产生背叛可获利的窗口。

这种干预特别适合探索：

- trust；
- temptation；
- cooperation；
- reciprocity；
- sacrifice。

---

### Timing Intervention

例如：

- 某个非关键活动合理延迟；
- 某商店提前关闭；
- 一名 Rule Actor 早到 / 晚到；
- 一个窗口期缩短。

必须限定在世界允许的时间扰动范围内，不能无因果来源地暂停某个 Player。

---

### Spatial / Accessibility Intervention

例如：

- 道路临时封闭；
- 某入口开放；
- 天气造成绕行；
- 公共交通改变；
- 某区域因事件变得更容易 / 更难进入。

目标可以是增加 Encounter 的自然概率，而不是直接执行：

```text
ForceEncounter(Alice, Bob)
```

---

### Third-Party Intervention

引入一个低复杂度 Rule Actor：

- 旅人；
- 信使；
- 商人；
- 目击者；
- 调解者；
- 预言者；
- 小偷；
- 官吏。

古代神话与戏剧中常见的“神秘老人”“信使”“神谕者”在这里可以被理解为：

> **低带宽、高杠杆的 causal actuator。**

---

### Stochastic Intervention

对于本来就存在随机性的事件：

```text
P(rain) = 0.30
```

Providence 可以在预算允许范围内改变 hazard / probability：

```text
0.30 → 0.45
```

但不应无中生有地产生完全不属于世界模型的事件。

这是一种很有潜力的“克制命运”：

> Providence 调整概率，Law 仍然负责最终采样和发生。

---

## 5.2 明确禁止的干预

Providence 默认不应：

- 直接修改 Player 的 Desire；
- 直接修改 Player 的 Fear；
- 直接写入 Player Belief；
- 修改 Player 隐藏记忆；
- 强制 Player 执行某动作；
- 绕过 Kernel 修改 Objective World；
- 重写已经 Commit 的历史；
- 为特定 Player 临时修改世界规则；
- 把“未来 Forecast”泄露给 Player，除非世界内确有预知机制。

---

# 6. Online Providence：让正在运行的棋局保持信息价值

Online Providence 运行在单局世界内部。

它可以持续观察：

```text
TrajectorySoFar
Forecast Summary
TimeSinceLastDecisionBoundary
Encounter Rate
NewInformation Rate
Relationship Change
Action Repetition
Scenario Objectives
Intervention Budget
```

然后判断：

```text
NoOp
```

或者提出一个 Intervention。

一个典型触发理由是：

> **轨迹已经进入低信息增益 / 低戏剧动力的吸引子。**

例如：

- Alice 重复采集；
- Bob 重复读书；
- 两人预计数小时不会产生接触；
- 没有新的 Secret / Conflict / Opportunity；
- Player policy 在当前状态下已经高度稳定。

此时 Providence 的目标不是单纯“加戏”，而是：

> **寻找一个便宜的小扰动，测试 Player policy 是否会变化。**

因此 Online Providence 可以理解成：

# 信息增益驱动的主动实验者

---

# 7. Offline Providence / Scenario Forge：更重要的反事实实验层

长期来看，Offline Providence 很可能比 Online Providence 更重要。

它不需要污染一条正在发生的世界线。

而是利用 Journal / Fork：

```text
Checkpoint
    ↓
fork
    ├── Variant A
    ├── Variant B
    ├── Variant C
    └── Variant D
```

例如在 Alice 出现犹豫的 DecisionPoint：

```text
A：Bob 说真话
B：Bob 隐瞒部分信息
C：Alice 提前获得弱证据
D：第三者在场
```

分别运行多次。

比较：

- Cooperation Rate；
- Betrayal Rate；
- Survival；
- Goal completion；
- Relationship changes；
- Decision consistency；
- Long-horizon consequence。

如果一个很小的变动导致策略分布急剧变化：

> **这里就是高价值 Decision Boundary。**

Offline Providence 可以继续在边界附近做局部 mutation。

这使 Journal / Fork 从“调试能力”升级为：

> **自动反事实实验仪器。**

---

# 8. 双 Providence：两个受约束的“故事作者”对弈

Providence 不一定只有一个。

可以引入两个或多个拥有不同目标的 Providence，在相同 Law 和 Intervention Budget 下对抗。

例如：

## Concord Providence

目标：

> 促进 Alice 与 Bob 形成长期稳定合作，但不能强制双方选择合作。

## Discord Providence

目标：

> 创造真实而有吸引力的背叛机会，尝试导致两者关系破裂。

二者都只能改变：

> **Player 所面对的因果条件。**

不能改变 Player 本身。

这样一局世界实际上由四个策略主体共同“写”出来：

```text
Alice
Bob
Providence A
Providence B
```

但前两者属于世界内主体，后两者属于 meta-world。

---

## 8.1 不要把双 Providence 简化成善 vs 恶

更有研究价值的方式是：

> **两个竞争的策略 / 文学假设。**

例如：

Providence A：

> 长期互信在重复互动中具有更高生命力。

Providence B：

> 当私利诱惑超过某阈值时，合作必然崩溃。

双方不能只用语言辩论。

它们需要：

> **各自在有限干预预算内构造世界条件，然后让 Player 自己跑出结果。**

这种系统可以被理解为一种：

> **simulation-based experimental philosophy**

需要强调：模拟结果不能直接当成现实社会规律；它能说明的是：

> **某类 Agent policy 在某类形式化环境中的行为结构。**

---

# 9. 防止 Providence 学成“恶意关卡生成器”

单纯最大化：

```text
让 Player 失败
```

会产生大量无意义世界。

UED / PAIRED 的一个重要启发就是：

> 困难不等于无解；高训练价值往往来自“当前 Agent 难，但存在合理解决策略”的环境。[R5]

DramaBoard 应建立类似的约束。

例如 Discord 想诱发背叛。

垃圾解：

> 制造“必须背叛才能活”的强制世界。

高价值解：

> 合作仍然可行，但背叛具有真实短期收益，而且信息不完全。

因此高价值对抗 Scenario 应同时满足：

- 存在多个合理策略；
- 没有单一外力强制结果；
- 各策略具有真实代价；
- Player 有足够信息作出选择，但不一定拥有全知；
- 结果能体现长期差异。

同样：

Concord 只有在存在真正背叛诱惑时促成合作，才有较高价值。

---

# 10. Curator：不应只优化“戏剧性”

如果只定义：

```text
DramaScore
```

系统很可能自动长成狗血连续剧。

因此 Curator 应采用多维评价，而不是一个未经约束的“好故事分数”。

建议至少考虑以下概念指标。

## 10.1 Decision Sensitivity

小环境改变是否引起明显策略分布变化？

这是“决策边界”价值的核心指标。

---

## 10.2 Counterfactual Diversity

从同一个 checkpoint 做合理小 Fork，是否能得到多个不同但合理的未来？

---

## 10.3 Intervention Efficiency

Providence 是否用很少的干预造成了很大的长期差异？

---

## 10.4 Agency Preservation

最终结果是不是 Player 自己作出的决定？

如果 Providence 基本已经规定了结局，则价值下降。

---

## 10.5 Novelty

该轨迹 / 场景是否只是 Scenario Pool 中已有模式的重复？

---

## 10.6 Long-Horizon Consequence

较早的一个决定，是否经过较长时间仍然产生可识别后果？

这对 DramaBoard 的“棋谱 / 因果复盘”价值很重要。

---

## 10.7 Solvability / Meaningfulness

是否存在至少一个合理可行策略，而不是纯粹必死 / 必败 / 被迫选择？

---

## 10.8 Generalization Value

如果未来用于后训练：

> 这个场景是在测试一个抽象策略结构，还是只在训练模型记住某个固定故事？

这直接引出 Scenario Template。

---

# 11. Scenario 不是固定关卡，而应逐步抽象成“因果模板”

一个非常重要的设计方向是：

> **Scenario Pool 不应最终只保存具体剧情实例。**

否则模型容易学成：

> “看见酒馆 + Alice + 匿名信，就应该怀疑 Bob。”

我们真正想保存的是：

> **一个能够产生某类策略问题的因果结构。**

例如：

# Trust Under Temptation

它可以抽象为：

```text
Participants:
    A
    B

InitialRelation:
    mutual cooperation history

SharedGoal:
    G

PrivateOpportunity:
    A can gain P by hiding information

DetectionProbability:
    d

EvidenceReliability:
    e

CommunicationDelay:
    t

FutureInteractionProbability:
    f

ThirdPartyPressure:
    q

Deadline:
    D
```

其中真正的结构是：

> **长期合作关系 + 私利诱惑 + 信息不完全 + 未来重复互动。**

这可以实例化成：

- 两名修士护送经书；
- 两个商人共同押运货物；
- 两名太空船员共享氧气；
- 两个公司合伙人；
- 两名逃亡者；
- 两个诸侯暂时结盟。

故事皮肤完全不同。

策略结构相似。

---

# 12. Causal Template 应保存什么

建议未来的 Scenario Template 至少区分：

## 12.1 Structural Roles

不是具体姓名，而是：

```text
Trustor
Trustee
Rival
Witness
Mediator
Beneficiary
Victim
```

一个 Character 可以填入一个或多个 role。

---

## 12.2 Causal Relations

例如：

```text
SecretKnownBy(A)
ResourceDesiredBy(A, B)
EvidenceSupports(X)
Commitment(A, B)
OpportunityRewards(Betrayal)
FutureInteractionCreatesCost(Betrayal)
```

---

## 12.3 Parameter Ranges

例如：

```text
EvidenceReliability ∈ [0.3, 0.9]
PrivateBenefit ∈ [low, high]
TimePressure ∈ [...]
RelationshipStrength ∈ [...]
```

Providence / Scenario Forge 可以在这些维度上 mutation。

---

## 12.4 Invariants

必须保持的结构：

> 两人必须仍然存在真实合作路径。

> 私利必须是真实存在的，而不是假奖励。

> 证据不能百分之百确定，否则策略问题会退化。

这些 invariant 负责保证：

> **模板实例化以后仍然属于同一种“棋形”。**

---

## 12.5 Skin / Setting Slots

例如：

```text
Setting
ObjectTypes
SocialRoles
HistoricalPeriod
ThreatForm
RewardForm
CommunicationMedium
```

这些用于内容泛化，而不改变核心策略结构。

---

# 13. Scenario Genome、Instance 与 Pool

可以采用三个概念层次。

## Scenario Template

抽象因果模板。

例如：

```text
TrustUnderTemptation
```

---

## Scenario Instance

某次具体实例化：

```text
Alice 与 Bob
旧港
一封匿名信
一件遗失圣物
三天期限
```

---

## Scenario Mutation

对 Instance 或 Template 参数的受控变更：

```text
EvidenceReliability: 0.8 → 0.6
PrivateBenefit: 20 → 35
CommunicationDelay: 5min → 30min
```

然后保存实验统计：

```text
ParentScenario
Mutation
Runs
OutcomeDistribution
DecisionSensitivity
InterventionCost
Novelty
```

于是 Scenario Pool 不是“作者写好的 100 个关卡”，而逐步成为：

> **一组经过实际 Player 轨迹验证的策略边界样本库。**

---

# 14. 建议的自动 Scenario 演化流程

```text
Seed Scenario
    ↓
Run N trajectories
    ↓
Analyze / Curate
    ↓
发现垃圾时间或高敏感 DecisionPoint
    ↓
Fork
    ↓
Providence / Scenario Forge 提出 mutation
    ↓
Run variants
    ↓
Compare outcome distributions
    ↓
保留：
    - 高 Decision Sensitivity
    - 高 Counterfactual Diversity
    - 高 Generalization Value
    - 合理 Solvability
    ↓
Scenario Pool
    ↓
继续 mutation / recombination / re-instantiation
```

这与 UED / curriculum generation 有明显亲缘关系，但 DramaBoard 的环境参数不再主要是：

- 墙在哪里；
- 地形怎么摆；
- 障碍有多少。

而是：

- 谁知道什么；
- 谁相信谁；
- 谁欠谁；
- 哪种利益冲突存在；
- 谁拥有退出机会；
- 承诺何时到期；
- 证据有多可靠；
- 哪种偶然事件可能发生。

也就是：

> **semantic environment design。**

---

# 15. Providence 与 RL / 后训练：三个运行模式必须分开

如果未来 DramaBoard 被用于 RL Gym / imitation learning，建议明确区分三个模式。

## 15.1 Natural Mode

Providence 关闭，或者只允许纯 NoOp。

用途：

- 测量 Agent 自然策略；
- 测量未经引导的世界动力；
- 获得 unbiased baseline。

---

## 15.2 Curriculum / Providence Mode

允许 Providence 有限干预。

用途：

- 提高高价值 DecisionPoint 的产生率；
- 自动寻找策略边界；
- 产生罕见但合理的训练情景；
- 降低人工关卡设计成本。

目标不是：

> “暗中奖励善良角色。”

而是：

> **让具有学习价值的选择条件更频繁出现。**

---

## 15.3 Evaluation Mode

固定：

- Scenario；
- seed；
- Providence policy；
- intervention budget；

或者直接禁用 Providence。

否则无法区分：

> “Player policy 更强”

和：

> “这一轮 Providence 更照顾它”。

---

# 16. Providence 与 Reward Shaping 必须严格分离

一个很容易出现的概念错误是：

> Providence 想让 Agent 学会某种价值，因此偷偷让“好人”更容易获胜。

不建议这么做。

经典 potential-based reward shaping 的研究说明，在特定形式下可以增加 shaping reward 而不改变原问题的最优策略；这说明“帮助学习原有目标”和“重新定义什么叫好策略”是两件不同的事情。[R11]

如果 DramaBoard 的客观制度真的奖励：

> 霸权、欺诈、一次性背叛，

那么简单增加一点“善良奖励”并不能解决根本问题。

应分开处理：

### Providence

创造值得学习的情境。

### Reward / Objective / Constraint

定义训练目标。

### Law / Institution

定义策略的真实长期后果。

### Player

自己选择行为。

---

# 17. Providence 不应默认读取 Player 隐藏心智

这是一个值得保留的设计选项。

最容易实现的 Providence 是全知的：

```text
读取 Alice 所有 Desire / Fear / Memory / hidden reasoning
```

然后精准操纵她。

但这会有两个问题：

1. 太强，很容易产生人工感；
2. 训练得到的 Scenario 可能依赖现实中不可获得的 privileged information。

一个更有研究价值的默认模式可能是：

> Providence 看到完整 Objective World + 可观察行为历史，但不直接读取 Player 私有 reasoning。

它需要根据：

- 行动；
- 对话；
- 历史选择；

推测什么环境变化可能触及决策边界。

这使 Providence 自己也需要做：

> **system identification / player modeling。**

如果未来需要调试，可增加 privileged-debug mode，但不应让它成为唯一设计。

---

# 18. Journal / Replay：Providence 必须具有完整 provenance

Providence 绝不能成为一个不可追踪的“上帝补丁”。

任何真实发生的干预都必须进入可回放历史。

至少需要在研究 / debug 元数据中保存：

```text
InterventionId
ProvidenceId
BasedOnWorldVersion
InterventionType
Parameters
BudgetCost
Reason / Objective
Resulting WorldCommand
```

同时区分：

## World-visible Journal

世界中实际发生：

> 16:43 开始下雨。

## Meta / Providence Journal

为什么出现这种扰动：

> Providence #A 对天气 hazard 做了 +0.12 的合法偏置。

Player 只能看到前者。

训练与研究工具可以访问后者。

---

## 18.1 Replay 不应重新运行 Providence

Replay 已经拥有：

> 当时实际提交并 Commit 的 Intervention。

因此 Replay 应重放结果，而不是重新向 Providence 请求一次决策。

否则同一 Journal 无法保证得到同一世界。

---

## 18.2 Fork 可以重新启用 Providence

从 checkpoint Fork 后：

```text
Original future discarded
↓
Providence may decide again
↓
new timeline
```

这正是 Offline Scenario Forge 的基础。

---

# 19. 一个可能的软件边界

以下接口只是建议形状，不是要求当前立即实现。

```text
IProvidencePolicy
    Observe(metaState)
    ProposeIntervention(...)

InterventionProposal
    Objective
    BasedOnWorldVersion
    InterventionKind
    Parameters
    EstimatedCost

IInterventionValidator
    Validate(proposal)

WorldInterventionCommand
    // 与普通世界命令一样经过 Kernel

InterventionCommitted
    // 进入 Journal
```

非常重要：

> **Providence 不应获得一个“直接修改 WorldState”的 API。**

正确路径应始终类似：

```text
Providence
    ↓
InterventionProposal
    ↓
Validator / Budget / Policy
    ↓
WorldInterventionCommand
    ↓
Simulation Kernel
    ↓
Domain Events
    ↓
Journal
```

这样 Providence 永远处于 Law 之下。

---

# 20. 当前代码演进最应该避免的“埋雷”

即使暂时完全不实现 Providence，现在的架构也建议避免以下耦合。

## 20.1 不要让场景初始化只能靠硬编码

如果 FirstBoard 的：

- 初始 Actor；
- Place；
- Secret；
- Relationship；
- Deadline；
- scheduled events；

全部散落在 imperative setup code 中，未来很难：

- Fork；
- 参数 mutation；
- Template instantiation；
- Scenario Pool；
- 批量统计。

不要求现在就做完整 DSL。

但最好逐渐让：

> **Scenario Definition 成为可提取、可复制、可参数化的数据对象。**

---

## 20.2 不要把 Drama-specific 判断塞入 Kernel

例如：

```text
if (AliceHasNotMetBobForTooLong)
    CreateEncounter();
```

绝对不应属于 Kernel。

Kernel 只实现普遍规则。

这类逻辑未来属于 Providence / Scenario policy。

---

## 20.3 不要让 PlayerDriver 自己注入世界

Player 只能通过统一 WorldCommand / Intent grounding 边界改变世界。

否则未来 Providence、Human、AI、RL 四种控制器会获得不一致权限。

---

## 20.4 不要把 Intervention 做成修改 Prompt

Prompt steering 可以用于 AI runtime 本身的技术修复，但不应被当成 Providence 的世界机制。

否则：

- 无法 Replay；
- 无法比较 Human 与 AI；
- 无法区分“角色改变”与“环境改变”；
- 无法准确评估 agency。

---

## 20.5 不要让 Scenario Template 退化成剧情脚本

模板应该描述：

> 因果结构与策略问题。

而不是：

```text
第 1 天 Alice 遇见 Bob
第 2 天 Bob 背叛
第 3 天 Alice 原谅
```

后者只是 disguised story tree。

正确模板更像：

```text
双方存在共同目标
+
一方获得私人利益机会
+
存在不完全证据
+
未来仍有重复互动可能
```

至于最终：

- 是否背叛；
- 是否被发现；
- 是否原谅；

都由 Player 与 Law 决定。

---

# 21. 分阶段实现建议

## Phase 0 —— 现在：只保留演进空间

无需写 Providence 系统。

只确保：

- Kernel 没有剧情特判；
- WorldCommand 是统一世界入口；
- Journal 能记录外部世界干预；
- Scenario 初始化有逐步数据化的空间；
- Fork 能从明确 checkpoint 重新运行；
- Player 与 Objective World 边界清楚。

---

## Phase 1 —— Passive Curator

只分析，不干预。

输出：

- 长时间无 DecisionPoint；
- 重复行动区段；
- Encounter density；
- information gain；
- relation change；
- 高影响 DecisionPoint。

这是风险最低的第一步。

---

## Phase 2 —— Manual / Scripted Providence

人工规定：

```text
若出现条件 X
允许从 intervention set Y 中选一种
```

先验证：

> 通过“世界条件”而不是 Prompt，是否真的能低成本提高轨迹质量。

---

## Phase 3 —— Offline Scenario Forge

基于：

- Journal；
- Fork；
- parameter mutation；

自动产生场景变种。

先不影响玩家实时游戏。

---

## Phase 4 —— Curated Scenario Pool

引入统计：

- Decision Sensitivity；
- outcome distribution；
- novelty；
- solvability；
- intervention cost。

开始形成真正的“戏剧定式库”。

---

## Phase 5 —— Learned / Adversarial Providence

再考虑：

- 两个 Providence；
- regret-like objective；
- automatic curriculum；
- LLM semantic mutation；
- evolutionary search；
- RL environment generation。

这一阶段才真正接近 UED / PAIRED 的 DramaBoard 版本。

---

# 22. 一个值得长期保留的总图

```text
                         ┌───────────────┐
                         │   Curator     │
                         │ evaluate/run  │
                         └───────▲───────┘
                                 │ trajectory stats
                                 │
        ┌────────────────────────┴─────────────────────────┐
        │                                                  │
┌───────┴────────┐                                ┌────────┴───────┐
│ Providence A   │                                │ Providence B   │
│ constrained    │                                │ constrained    │
│ intervention   │                                │ intervention   │
└───────┬────────┘                                └────────┬───────┘
        │ proposals                                        │
        └──────────────────┬────────────────────────────────┘
                           ▼
                  ┌──────────────────┐
                  │ Validator/Budget │
                  └────────┬─────────┘
                           ▼
                  World Intervention
                           │
                           ▼
                ┌──────────────────────┐
                │ Law / Simulation     │
                │ Kernel               │
                └──────────┬───────────┘
                           │ Observation
                  ┌────────┴─────────┐
                  │                  │
             Human Player       AI Players
                  │                  │
                  └────────┬─────────┘
                           │ decisions
                           ▼
                         World

                 all committed causality
                           ↓
                        Journal
                           ↓
                    Replay / Fork
                           ↓
                     Scenario Forge
                           ↓
              Template / Instance / Pool
```

---

# 23. 这套机制最终可能意味着什么

如果以上机制成立，DramaBoard 的定位会进一步变化。

它不只是：

> 一个能让 AI Player 自主生活的游戏世界。

也不只是：

> 一个自动生成 emergent story 的沙盒。

它可能成为：

> **一台自动发现 Player 决策边界与“戏剧定式”的机器。**

其工作方式不是人工写一万个剧情分支。

而是：

1. 设计少量有效的 Law；
2. 提供少量 Providence 可以合法拨动的因；
3. 让 Player 自己行动；
4. 用 Journal / Fork 做反事实实验；
5. 发现什么小变化会真正改变 Player；
6. 把这些结构抽象为可跨皮肤实例化的因果模板；
7. 用 Scenario Pool 反复生成新的策略世界。

这也解释了为什么：

> **“一个能够产生某类策略问题的因果模板”**

可能比“一个写得很好的固定关卡”更重要。

固定关卡主要生产一次体验。

因果模板可以生产：

- 多种文化皮肤；
- 多种角色组合；
- 不同强度参数；
- 大量反事实变体；
- 不同 Player policy 下的新轨迹；
- 可用于泛化训练的 scenario family。

如果能够做成，作者的工作将逐渐从：

> **写剧情**

转变为：

> **设计能够稳定产生某类人类 / AI 决策困境的因果结构。**

这与 DramaBoard 最初“Director 设计棋盘，让 Player 自己下棋”的项目精神完全一致。

---

# 24. 下一阶段适合研究的问题

下一轮可以开始从神话、文学、历史与现实事件中反向挖掘：

> **哪些广泛复现的故事母题，本质上是某种可参数化的策略 / 因果模板？**

例如可以寻找：

- 信任 vs 私利；
- 忠诚 vs 生存；
- 承诺 vs 新信息；
- 复仇 vs 长期合作；
- 身份暴露；
- 替罪羊；
- 共同敌人；
- 囚徒困境；
- 第三方挑拨；
- 预言的自我实现；
- 错误证据；
- 资源突然稀缺；
- 道德运气；
- 权力真空；
- “不可同时满足”的双重义务；
- 牺牲一人救多人；
- 重复博弈中的声誉；
- 先手承诺与不可撤销行动；
- 两个高层 Providence 借凡人博弈。

重点不是复刻具体故事。

而是从故事中抽出：

```text
Roles
Initial Beliefs
Resources
Information Asymmetry
Temptations
Commitments
Deadlines
Intervention Points
Possible Strategic Equilibria
Decision Boundaries
```

最终逐渐形成 DramaBoard 自己的：

# Dramatic Causal Pattern Library

---

# 参考资料

**[R1]** Nelson, M. J., & Mateas, M. (2005). *Search-Based Drama Management in the Interactive Fiction Anchorhead*. Proceedings of the AAAI Conference on Artificial Intelligence and Interactive Digital Entertainment, 1(1), 99–104. doi:10.1609/aiide.v1i1.18723.  
要点：将 Drama Management 表述为“plot points + DM actions + story evaluation”上的搜索问题。

**[R2]** Nelson, M. J., Ashmore, C., & Mateas, M. (2006). *Authoring an Interactive Narrative with Declarative Optimization-Based Drama Management*. AIIDE 2(1), 127–129. doi:10.1609/aiide.v2i1.18761.  
要点：明确区分故事抽象、Drama Manager 干预动作与故事质量函数，由优化方法选择 DM action。

**[R3]** Chen, S., Nelson, M. J., & Mateas, M. (2009). *Evaluating the Authorial Leverage of Drama Management*. AIIDE 5(1), 136–141. doi:10.1609/aiide.v5i1.12377.  
要点：讨论 Drama Management 相对于传统 script-and-trigger 的 authorial leverage，关注用较少作者结构获得更大非线性空间。

**[R4]** Deo, S., Chung, J., & McCoy, J. (2024). *Shepherd: An Incremental Story Sifting-Based Drama Manager*. AIIDE 20(1), 256–259. doi:10.1609/aiide.v20i1.31887.  
要点：持续分析进行中的模拟，识别具有叙事潜力、值得进一步跟进的事件结构。

**[R5]** Dennis, M. et al. (2020). *Emergent Complexity and Zero-shot Transfer via Unsupervised Environment Design*. NeurIPS 33.  
要点：提出 UED 与 PAIRED，用 protagonist/antagonist regret 引导环境生成，自动形成越来越复杂的课程，并强调 valid / solvable environment distribution。

**[R6]** Parker-Holder, J. et al. (2022). *Evolving Curricula with Regret-Based Environment Design*. ICML 2022, PMLR 162, 17473–17498.  
要点：进一步研究 regret-based environment design 和 curriculum evolution。

**[R7]** Beukman, M. et al. (2024). *Refining Minimax Regret for Unsupervised Environment Design*. ICML 2024, PMLR 235, 3637–3657.  
要点：讨论 UED 中 minimax regret 目标和估计方式本身的重要性及局限。

**[R8]** Mead, H., Lacerda, B., Foerster, J., & Hawes, N. (2025). *Improving Regret Approximation for Unsupervised Dynamic Environment Generation*. NeurIPS 38. doi:10.52202/085713-5622.  
要点：指出环境参数只有小部分区域可能造成策略复杂度骤增，研究动态环境生成与更有效的 regret approximation。

**[R9]** Robertson, J., Heiden, J., & Cardona-Rivera, R. E. (2023). *Evolving Interactive Narrative Worlds*. AIIDE 19(1), 126–135. doi:10.1609/aiide.v19i1.27508.  
要点：不仅在固定 story world 中生成剧情，而用 evolutionary search 生成互动叙事世界与 mechanics。

**[R10]** Giannatos, S., Nelson, M., Cheong, Y.-G., & Yannakakis, G. (2011). *Suggesting New Plot Elements for an Interactive Story*. AIIDE 7(2), 25–30. doi:10.1609/aiide.v7i2.12474.  
要点：通过优化向 story world 添加新的事件内容，以提高作者定义的体验目标，同时不直接指导 Player 行动。

**[R11]** Ng, A. Y., Harada, D., & Russell, S. (1999). *Policy Invariance Under Reward Transformations: Theory and Application to Reward Shaping*. ICML 1999.  
要点：经典 potential-based reward shaping 工作，说明“加速学习原目标”和“改变什么策略才是最优”必须概念分离。

---

## 一句话结论

> **Law 决定什么能够发生；Player 决定自己想做什么；Providence 只用有限的世界内因果扰动创造更有信息价值的机会；Curator 发现其中真正值得保存的决策边界；Scenario Template 再把这些边界抽象成可以跨角色、跨背景、跨故事皮肤复用的“戏剧定式”。**
