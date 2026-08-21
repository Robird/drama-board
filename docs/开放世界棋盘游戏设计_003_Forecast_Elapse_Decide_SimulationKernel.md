# Design Note 003：Forecast, Collapse, Commit —— 统一原子 Occurrence 的 Simulation Kernel

**状态：目标设计 Law（替代旧 internal/external 调度语义）**

**初稿日期：2026-08-09**

**本次修订：2026-08-21**

**定位：定义 DramaBoard 的时间、联合预测、确定性仲裁、原子提交、Player 边界与可回放 Simulation Kernel。当前实现状态与验收边界以 `研发计划_006_统一原子Occurrence与LogicalInstant_Kernel重构计划.md` 为权威。**

---

## 0. 冻结的设计法则

Kernel 只有一条路径：

```text
冻结 committed HostWorld + WorldVersion + Journal expected head
→ 所有 IOccurrenceRule 全量 Forecast
→ 按 CandidateDue、确定性 keyed hash、CandidateKey 选唯一 winner
→ winner 所属 rule 在同一未完成 Step 中生成完整 TransitionDraft
→ scratch-fold 全部 facts + 验证最终 HostWorld
→ AppendBatch 单帧原子发布
→ 安装新 World、WorldVersion 与 LogicalInstant
→ 丢弃旧 candidates；下一 Step 全量 re-Forecast
```

以下旧语义全部废弃：

- internal event、external input、Player action 使用不同调度或提交路径；
- external input 在全系统 Forecast 之前直接修改 World；
- scheduled、external、direct consequence、derived event 按固定 microstep phase 排序；
- 一次选择并批量 Resolve 多个“同时事件”；
- subsystem 自行判断全世界是否已经到达同刻屏障；
- Player answer、协议 payload 或 LLM output 直接成为 DomainFact；
- Source、Handler、Resolver 分别拥有预测、补全和 Plan authority；
- 一个 occurrence 的多条 facts 各自占据逻辑时刻；
- ForecastBasis、应用层 hash 链、旧 Journal 兼容和跨 build audited replay。

小球碰撞、票据到期、天气变化和 Actor 的行动机会，在 Kernel 看来都只是 `OccurrenceCandidate`。它们必须先参加同一个全局仲裁；胜出的可信领域 rule 才能给出完整原子变化。

`src/FirstBoard`、`src/FirstBoard.Demo`、`src/Spatial` 与这些法则冲突时修改调用方和领域边界，不要求 Kernel 保留兼容分支。

---

## 1. 世界由 Flow 与 Occurrence 交替组成

DramaBoard 不需要不断执行固定 Tick。世界主要由两类成分组成：

1. **Continuous Lazy Evolution（Flow）**：只要参数不变，就能由锚点、速率和时间按需求值；
2. **Atomic Occurrence（Jump）**：使一条或多条现行 Flow 失效、产生新信息或改变行动条件的瞬时原子影响。

例如旅行只需保存：

```text
Traversal
    Passage
    AnchorOffset
    AnchorTime
    SignedSpeed
    Generation
```

任意合法时刻的位置都可以按需求值。只有抵达、相遇、超车、调速、掉头等破坏现有运动方程的 occurrence 才需要提交。

同理：

- 饥饿可以是锚点、增长率和下一阈值；
- 工程可以是当前阶段、速率和下一里程碑；
- 天气可以是当前趋势和下一次状态变化；
- 票据可以是有效状态和到期边界；
- 挖矿发现可以是由领域 rule 稳定推导的下一跳变。

Kernel 寻找的是：

> 当前全部 Lazy law 中，首个会破坏至少一条现行求值假设的原子 occurrence。

当 winner 位于未来时，Kernel 不逐对象积分。把 `ModelTime` 设为 winner 的 Due，只表示此前区间的 Lazy law 始终有效；边界所需的累计量由 winner facts 原子 materialize。不存在一个可独立改写世界的“先全局 Elapse、再 Resolve”阶段。

---

## 2. 离散世界时间与已提交因果顺序

### 2.1 四种时间分离

系统区分：

- **Model Time**：世界内时间，权威粒度为 1ms；
- **Causal Ordinal**：同一 ModelTime 上已提交 occurrence 的因果序号；
- **Wall-Clock Time**：Human、LLM、网络和服务器实际耗时；
- **Presentation Time**：Godot 动画、镜头和表现耗时。

权威逻辑时刻为：

```text
LogicalInstant = (ModelTime, CausalOrdinal)
```

它只标识已经提交的 occurrence，不是 candidate 排序键。

### 2.2 `ModelTime` 真正离散为 1ms

连续方程若算出 sub-ms 时刻，领域 rule 必须先向上量化：

```text
CandidateDue = CeilToModelTick(exactTime, 1ms)
```

精确整数 tick 保持不变；`T + δ`（`0 < δ < 1ms`）量化为 `T + 1ms`。这保证 occurrence 不会早于连续方程给出的时刻发生。

落入同一 tick 的数学先后被有意丢弃。例如 T+0.1ms 与 T+0.9ms 的 contact 都在 T+1ms 参加仲裁，后者可以先胜出并改变下一轮候选集合。这是原型的正式世界语义，不是精度 bug。

Kernel 不接收或保存 `KnownExactDue`、`OrderTime`、rational codec 或 `OrderFrontier`。

### 2.3 `CausalOrdinal` 只在发布时形成

候选没有 `CausalOrdinal`。winner 选定后，Kernel 提议：

```text
currentModelTime = last committed ModelTime
                   or Genesis ModelTime for an empty lineage
require winner.Due >= currentModelTime

if no transition has been committed:
    next = (winner.Due, 0)
else if winner.Due > lastCommitted.ModelTime:
    next = (winner.Due, 0)
else:
    next = (lastCommitted.ModelTime,
            checked(lastCommitted.CausalOrdinal + 1))
```

只有 Journal 发布点成功后，`next` 才成为历史。同一 `ModelTime` 的 transitions 因而依次位于 `(T,0)、(T,1)、(T,2)...`；任何 subsystem 都没有保留区段或固定 phase。

`WorldVersion` 只包含：

```text
WorldVersion = (LineageId, TransitionCount)
```

每成功发布一个 batch，`TransitionCount` 加一。它已经表达 lineage 内的提交顺序，不再增加 `CommitOrdinal`。Journal 的 ref/head CAS token 是 adapter 私有存储状态，不进入 WorldVersion。

---

## 3. 最小概念模型

| 概念 | 权威含义 |
|---|---|
| `HostWorld` | 当前 committed、不可变的完整世界；需要多领域原子性时组合相应领域状态。 |
| `SimulationRules` | 当前 lineage 的最小不可变规则：`WorldSeed` 与同 ModelTime transition budget。 |
| `CandidateDue` | 候选发生的整数毫秒 tick。 |
| `CandidateKey` | rule 从 World 推导的完整、稳定、规范字节身份，也是 hash 碰撞兜底键。 |
| `OccurrenceCandidate` | 当前 Step 的临时预测：`CandidateKey + CandidateDue + rule-private immutable data`。 |
| `TransitionDraft` | owning rule 返回的非空、有序 facts 列表；没有 commit 权。 |
| Journal batch | 一个 occurrence 的持久化 envelope：一个 `LogicalInstant`、一个 cause `CandidateKey`、非空 `Facts[]`。 |

### 3.1 一个 `CandidateKey` 表达完整候选身份

Kernel 不再分别维护 `SourceId / CandidateId / Generation / HandlerId / OccurrenceId`。rule 若需要区分领域、主体、局部槽位或代际，应把它们规范编码进 `CandidateKey`。

要求：

- key 在调用 Player 或读取 Player proposal 之前，仅从 committed World 和 rule law 推导；
- Player payload 不得改变 key，否则 Player 可以磨出有利的 PRF rank；
- 同一轮 Forecast 出现重复 key 是确定性规则错误；
- Kernel 只在当前 Step 栈维护 `CandidateKey → owning rule/candidate` 映射；
- commit 后该映射和所有 candidates 一起丢弃。

Candidate 不持久化，也不要求承载通用 provenance、capability、ForecastBasis 或序列化后的完整 Plan。Candidate 的私有数据必须不可变，不能捕获可变 World、Journal、墙钟或 stateful RNG。

### 3.2 一个 batch 表达一个 transition

一个 occurrence 可以产生多条 facts：

```text
JournalBatch
    LogicalInstant
    CandidateKey
    Facts[]              // non-empty; array order is fold order
```

同一 batch 的 facts：

- 共享一个 `LogicalInstant` 和一个 cause；
- 没有各自的 timestamp、WorldVersion 或因果 ordinal；
- 按数组下标 fold，因此不持久化领域 `FactOrdinal`；
- 对 Query、订阅、Replay、Snapshot 和 Fork 不暴露 prefix。

只有相邻 batch 的 `LogicalInstant` 必须严格递增。一个成功 batch 就是一个 transition；不再额外定义 `CommittedTransition`、`AppendTransition` 或 Begin/End 事务协议。

---

## 4. 唯一 Rule、全量 Forecast 与确定性仲裁

### 4.1 单一 Rule 边界

以下接口只冻结职责，不冻结最终 C# 命名：

```text
IOccurrenceRule
    Forecast(HostWorld world, SimulationRules rules)
        -> zero or more OccurrenceCandidate

    PlanSelectedAsync(
        HostWorld frozenWorld,
        OccurrenceCandidate winner,
        CancellationToken cancellationToken)
        -> TransitionDraft
```

`IOccurrenceRule` 是 Host 注册的可信进程内领域代码，不是 capability/security plugin。Kernel 不理解 candidate 的私有 payload，也不知道 rule 是否需要调用 Player。

Forecast 必须：

- 只读取本轮 frozen `HostWorld` 与 `SimulationRules`；
- 枚举当前世界的全部 candidates，不使用 source-local minimum/frontier；
- 对相同输入产生相同候选语义；
- 不修改 World 或 rule state；
- 不读取墙钟、文件、网络、数据库或线程完成顺序；
- 不消费 stateful RNG。

领域随机若会影响 Forecast，rule 应使用由 World 身份和 generation 寻址的确定性采样。具体 API 属于领域；它必须与 scheduler tie-break 使用不同的 code-level domain separator，不能复用同一 rank 作为命中、掉落或 NPC 决策。

### 4.2 严格全序比较器

```text
compare(candidate):
    1. CandidateDue
    2. KeyedHash(
           WorldSeed,
           code-level scheduler domain separator,
           CandidateDue,
           CandidateKey canonical bytes)
    3. CandidateKey canonical bytes
```

标准 keyed hash 的算法、字节序、canonical key codec 和 domain separator 由当前 build 固定，并由 golden vectors 与枚举置换测试约束。它们不是 World、Journal 或运行时 `SchedulerSemanticsVersion`。

比较器必须与 rule 注册顺序、candidate 枚举顺序、字典插入顺序、线程完成顺序和进程 hash salt 无关。完整 CandidateKey 是 hash 碰撞时的最终确定性兜底。

PRF rank 只比较当前 Forecast 轮中的同 tick candidates。某个 winner 产生的新 candidate 即使 rank 更小，也只能参加下一轮，不能倒插到较小 `CausalOrdinal`。

### 4.3 每轮只选一个

```text
Forecast all rules
→ select exactly one winner
→ owning rule plans
→ commit one batch
→ invalidate every candidate
→ next Step forecasts all rules again
```

winner 提交后，其余旧 candidates 可以消失、改变 Due、改变 key，或继续存在。Kernel 不尝试维护跨轮候选有效性和增量 invalidation graph。

---

## 5. 权威单次 Step

一次 Step 最多提交一个 batch。Host 若要持续运行到某个边界，重复调用 Step；Kernel 不在一次 Step 中隐藏多 transition 循环。

```text
StepAsync(notAfter, cancellationToken):
    require no other Step/commit is in flight
    require notAfter >= currentModelTime

    frozenWorld    = committedWorld
    expectedVersion = WorldVersion
    // Journal adapter privately retains its expected ref head

    candidates, owners = ForecastAllRules(frozenWorld, simulationRules)
    RejectPastDueAndDuplicateKeys(candidates)

    if candidates is empty:
        return Exhausted

    winner = SelectUniqueMinimum(candidates)

    if winner.Due > notAfter:
        return BoundaryReached          // zero Plan; zero state change

    draft = await owners[winner.Key]
        .PlanSelectedAsync(frozenWorld, winner, cancellationToken)

    require draft.Facts is non-empty
    scratchWorld = ApplyFactsPure(frozenWorld, draft.Facts)
    ValidateHostWorld(scratchWorld)

    instant = ProposeLogicalInstant(winner.Due)
    batch = StampBatch(instant, winner.Key, draft.Facts)

    cancellationToken.ThrowIfCancellationRequested() // final cancellable boundary
    journal.AppendBatch(batch)                        // no cancellation; CAS publishes

    // CAS success onward is committed; later cancellation cannot undo it.
    committedWorld = scratchWorld
    WorldVersion = (
        expectedVersion.LineageId,
        expectedVersion.TransitionCount + 1)
    lastCommitted = instant
    DiscardAllCandidates()
    return Committed
```

`notAfter` 只防止 Step 越过调用者的模型时间边界：

- winner.Due `== notAfter` 可以提交；
- winner.Due `> notAfter` 返回 `BoundaryReached`；
- boundary result 不调用 Plan、不写 Journal、不伪造 ModelTime 前进；
- `notAfter` 不传给 rule，不改变候选集合、rank 或优先级。

因此正常结果只有：

- `Committed`：恰好一个 batch 已发布；
- `Exhausted`：当前世界没有 candidate；
- `BoundaryReached`：唯一 winner 位于调用边界之后，世界零变化。

Forecast、Plan、scratch-fold、最终验证或发布前取消失败时，active Journal、World、WorldVersion 与 LogicalInstant 均不变化。AppendBatch 的 ref CAS 是线性化点；CAS 成功后 transition 已经发生，即使进程尚未安装内存 World 或调用方未收到成功，也不得报告“零提交”并重试，只能以 Journal 为 authority 恢复。

---

## 6. Player 是 Kernel 外的策略函数

DecisionPoint 是普通 candidate。其 owning rule 可以在 `PlanSelectedAsync` 内：

```text
从 frozen World 构造合法 observation
→ 调用 Human / AI / LLM / script strategy
→ 校验 envelope、correlation、affordance 与领域规则
→ 生成完整 TransitionDraft
```

Kernel 不定义或引用 Player、Human、AI、LLM、Protocol、validator、request、inbox、resume、resolution contract 或 resolution audit 类型。Player 不能构造 facts，不能写 World/Journal，也不能改变已经选定的 CandidateKey。

一个 lineage 同时至多有一个未完成 Step。等待 Player 的 wall-clock 时间消耗零 ModelTime；期间不得 Forecast/commit 另一 occurrence。若 Alice 与 Bob 在同一 tick 都有 DecisionPoint，Kernel 先仲裁一个，只调用 winner 的策略；winner 提交后 Bob 才从新世界重新 Forecast。

### 6.1 非法 proposal 与戏剧性失败

必须区分：

- malformed、错误 correlation、stale、unauthorized 或违反当前 affordance 的 proposal：owning rule 在同一 Step 内重问，或令 Step 在发布前失败；零提交；
- 合法行动在世界内产生的失败，例如攻击落空、谈判失败：rule 返回描述该结果的非空 facts，提交正式 failure transition。

Kernel 不定义通用 `RejectedOccurrence`。若 rule 返回空 draft，或成功后仍在相同世界重复产生同一无进展 candidate，这是规则 fault，不应用伪造 rejection 历史掩盖。

### 6.2 票据与行动机会示例

`ExpireTicket` 与 `DecisionPoint(BoardingOpportunity)` 在同一 tick 统一仲裁：

- 到期先胜：提交到期；下一 Step 的 observation 不再提供乘船，并可说明“晚了一步”；
- DecisionPoint 先胜：owning rule 等待策略选择并可返回 `TicketConsumed + TraversalStarted`；两条 facts 同 batch 原子提交，到期候选随后消失。

竞争发生在 candidates 之间，不在 Player 回答后再造一轮 command-vs-deadline 仲裁。

---

## 7. 跨域原子性与 Journal 发布

### 7.1 Composite `HostWorld` 已足够

首个真实 Game + Spatial 组合用一个 composite `HostWorld` 解决。例如 `UseTicketAndBoard` 的 draft 可以包含：

```text
TicketConsumed
TraversalStarted
```

Kernel 在同一个 scratch World 上按数组顺序 fold 全部 facts，再验证最终跨域 invariant。任一 fact/reducer/invariant 失败都发生在发布前，因而整个 batch 零提交。不建设通用 transaction coordinator、两阶段提交或补偿协议。

### 7.2 `AppendBatch` 的目标语义

已经完成的基础能力是：Atelia 能把一个非空 batch 编码为一个 EventJournal Event / RBF Frame，并以 private expected-head ref CAS 一次发布；长度、CRC、单帧上限和 orphan 不可达性属于存储层保证。

仍必须迁移的逻辑语义是：

- 一个 batch 只有一个 `LogicalInstant` 和 `CandidateKey` header；
- facts 不再各自携带递增 Microstep/timestamp；
- batch 内按数组位置 fold，只在 batch 之间检查 LogicalInstant 严格递增；
- `TransitionCount`、Replay、checkpoint 和 Fork prefix 按 batch 数，而不是 fact/event 数；
- `Events` 的 flat prefix 不得再成为业务可观察或可 Fork 边界。

这不要求另一套 `CommittedTransition/AppendTransition`。可以调整现有 batch envelope/read API；具体 C# 命名属于实施细节。

### 7.3 CAS 是唯一发布点

```text
publish 前：
    failure / cancellation / serialization error / CAS failure
    → active history 与 World 均不变

ref CAS 成功：
    batch 已 committed
    → Journal 成为 authority
    → 正常路径安装已验证 scratch World
    → publish 后 crash 由 Replay 恢复，不能按未提交重试
```

CAS 失败留下的不可达 orphan 不是 committed history。AppendBatch 是短暂、不可取消的 commit section；普通取消只在进入发布前观察。

---

## 8. Replay、scheduler conformance 与 Fork

### 8.1 普通 Replay

普通 Replay 只读取当前格式的完整 batches：

```text
read one batch
→ validate batch boundary and LogicalInstant order
→ scratch-fold Facts[] in array order
→ expose final World and increment TransitionCount once
```

它不 Forecast、不调用 Player、不重新计算 rank，也不验证旧 build 的 Plan 合法性。只有完整 batch fold 后才能发布 World、Snapshot 或订阅通知。

本原型没有需要保留的旧 Journal 数据。格式或 scheduler semantics 改变时直接重建开发数据；不实现旧格式版本门、只读打开、转换或跨 build Replay。

### 8.2 同 build scheduler conformance

测试工具可以从 Genesis 逐 batch 重建前缀 World，在当前 build 中全量 Forecast 并重算 winner，再将 winner 的 `CandidateKey` 和 Due 与 batch 的 cause key、`LogicalInstant.ModelTime` 对照。

它用于发现非 winner 提交、枚举顺序泄漏和 comparator 退化，不保存 ForecastBasis、Player proposal、plan hash 或 resolution audit，也不重跑 Player/Plan。

### 8.3 Fork

Fork 只能发生在完整 batch boundary。它：

```text
inherits:
    committed World at prefix
    prefix TransitionCount
    last LogicalInstant
    WorldSeed
    complete Journal batch prefix

creates:
    a new LineageId

discards:
    unfinished Step
    owner/candidate maps
    Forecast cache
```

child 的版本是 `(NewLineageId, PrefixTransitionCount)`，不能继承父支完整 WorldVersion。因为仲裁输入不含 LineageId，相同 prefix World、WorldSeed 和当前 build 默认得到相同下一 winner；之后任一支的 rule/Player facts 可以使两支自然分歧。

---

## 9. 活性、Spatial 进展与资源边界

### 9.1 同 `ModelTime` 活锁预算

`CausalOrdinal` 消除了同刻歧义，但不能证明同一 tick 的因果链有限。Kernel 必须保留一个最小防线：

```text
MaxTransitionsPerModelTime
```

超过预算时确定性停止并报告，不伪造时间前进，也不静默丢 candidate。ordinal 增量使用 checked arithmetic。空 draft、提交后完全无进展的同 key candidate 也属于确定性 rule fault。

Journal 单帧大小由 AppendBatch/RBF 上限负责。候选数、缓存、heap 等其它容量和性能机制，只有在 profiler 或真实失败出现后才设计；V1 不把它们提升为 Kernel Law。

### 9.2 Spatial contact 必须留下局部权威进展

一个 contact 提交后，Spatial World 必须发生足以阻止同一 contact 永久复发的权威变化，例如更新 segment generation、关系状态或最小的 consumed-contact state。Journal receipt 不能替代 Forecast 可见的 World 状态。

该变化只能消费自身领域条件，不能写 `SettledThrough = T` 一类 whole-tick watermark，从而吞掉同 tick 的其它 contact。每个 contact batch 后都全量 re-Forecast；具体 key/state schema由首个 Spatial 垂直切片决定，不在 Kernel 预建通用 cursor/index。

---

## 10. 信息隔离与表现层

Simulator 可以知道 candidates，Player 不能因此预知未来：

```text
Simulator foresight ≠ Player foresight
```

Player observation 只能来自 frozen World 的合法感知投影。完整 contender set、PRF rank、其它 Actor 私有状态和尚未发生的 Forecast future 不得进入 observation。投影由 Kernel 外的领域 rule/adapter 负责。

Godot 的 NavMesh、动画与镜头属于 Presentation Time。视觉人物走了 38 秒还是 42 秒，不改变 ModelTime 上已经提交的 8 分钟旅行；Presentation 也不能因为动画尚未播放完而改变 winner。

这支持：

> 离散棋盘底层 + 连续 RPG 表现 + 小说式时间压缩。

---

## 11. 最小接口与职责边界

概念接口为：

```text
SimulationRules
    WorldSeed
    MaxTransitionsPerModelTime

IOccurrenceRule
    Forecast(HostWorld, SimulationRules)
        -> zero or more OccurrenceCandidate

    PlanSelectedAsync(HostWorld, OccurrenceCandidate, CancellationToken)
        -> TransitionDraft

ISimulationKernel
    StepAsync(ModelTime notAfter, CancellationToken)
        -> Committed | Exhausted | BoundaryReached

IJournalSink
    ReadBatches()
    AppendBatch(batch)       // no cancellation after entering publication
```

Kernel 是唯一 commit authority。rule、Player、Host 和 adapter 都不能直接安装 World 或推进 WorldVersion/LogicalInstant。Journal adapter 私有拥有 expected-head/ref CAS；Kernel 不把存储地址提升为领域版本。

Kernel assembly 不得引用 Host、Protocol、Player、Player.Llm、Decision.Validation 或 orchestration implementation assembly。领域 rule 可以依赖由 Host 注入的领域服务，但这些服务不成为 Kernel 类型。

不进入 Kernel 的概念：

- `IOccurrenceSource / IOccurrenceHandler / IOccurrenceResolver`；
- `ForecastBasis / SelectedWinnerContext / ValidatedOccurrenceResolution / ResolutionContract / ResolutionAudit`；
- `CommitOrdinal / FactOrdinal / OccurrenceId / HandlerId`；
- `KnownExactDue / EffectiveExactDue / OrderTime / OrderFrontier`；
- `WorldSnapshotHash / HeadHash / TransitionHash / plan hash / receipt hash`；
- 通用 provenance/capability、generic outbox、runtime Admin/Providence/Setup；
- 旧 Journal 兼容、通用跨域 coordinator、增量 Forecast 平台。

领域若真实需要 attribution、authorization、Command 或 outbox，应在拥有该需求的领域边界设计，不能预先升级成 Kernel 调度 Law。

---

## 12. 可执行不变量

实现至少必须证明：

| ID | 不变量 |
|---|---|
| TIME-1 | `CandidateDue` 是整数毫秒；精确 tick 不动，任意 sub-ms 余数向上量化。 |
| TIME-2 | candidate 没有 CausalOrdinal；一个成功 batch 只产生一个 LogicalInstant。 |
| ORD-1 | comparator 与 rule 注册、枚举、字典和线程完成顺序无关；hash 碰撞由完整 CandidateKey 兜底。 |
| ORD-2 | 同轮 CandidateKey 重复是 deterministic fault；key 在 Player 调用前确定。 |
| STEP-1 | 一个 Step 最多提交一个 batch；winner.Due 超过 notAfter 时零变化返回 BoundaryReached。 |
| STEP-2 | 同 lineage 只有一个未完成 Step；等待 Player 消耗零 ModelTime。 |
| ATM-1 | 一个 batch 的全部 facts 共享 LogicalInstant，prefix 不可观察；scratch/invariant 失败零发布。 |
| ATM-2 | ref CAS 是 commit point；publish 前失败零变化，publish 后 crash 以 Journal Replay 恢复。 |
| PLAN-1 | Kernel 只接受 owning rule 的完整非空 draft；Player/LLM 不能构造 facts。 |
| FAIL-1 | 非法 proposal 零提交；合法行动的世界内失败提交正式 failure facts。 |
| RPL-1 | 普通 Replay 只按当前格式完整 batch fold，不 Forecast、不调用 Player。 |
| FORK-1 | Fork 仅在 batch boundary，创建新 LineageId，继承 prefix count/World/instant/seed。 |
| LIV-1 | 同 ModelTime 超预算、空 draft或重复无进展 candidate 确定性停止。 |
| SPA-1 | contact 有 Forecast 可见的局部进展，且不以 whole-tick watermark 吞掉 peers。 |
| INFO-1 | observation 不泄露 contender set、rank 或其它尚未发生的 Forecast future。 |

更细的实施顺序和切片验收只维护在 006，不在本设计文档复制第二套计划。

---

## 13. 适用边界与设计价值

该模型保留 DramaBoard 的核心价值：

- 稀疏 occurrence 和小说式时间压缩；
- Activity/Traversal 等持续行为按需计算；
- Player/Human/AI 共享领域 Decision 语义，但 Kernel 不耦合决策过程；
- Player/AI latency 不污染 ModelTime；
- Forecast foresight 与角色认知隔离；
- 逻辑导航与 Godot 表现解耦；
- 单 driver、正确性优先、可解释的原型实现。

它不适合未经抽象的高频连续物理混沌、海量强耦合碰撞或 twitch gameplay。战斗、潜行、调查与社交应主动设计为可预测 Flow 与稀疏 Occurrence，而不是迫使 Kernel 模拟每一帧。

---

## 结语

DramaBoard 的时间内核不再问：

> 这一时刻有哪些 internal、external、direct 或 derived 事件应该一起处理？

它只反复问：

> 在当前 committed world 上，全部 rules 共同预测出的唯一下一原因是谁；它的 owning rule 给出的完整原子变化能否一次发布？

```text
Continuous Lazy Evolution
→ full Forecast
→ deterministic single-winner collapse
→ owning rule returns complete TransitionDraft
→ scratch validate
→ one AppendBatch commit
→ full re-Forecast
→ repeat
```

`ModelTime` 表达世界流过了多久，`CausalOrdinal` 表达该毫秒内已经提交了多少个不可逆原因。Player 只是某些 winner 在领域边界调用的策略函数，不是第二条外部输入时间线。
