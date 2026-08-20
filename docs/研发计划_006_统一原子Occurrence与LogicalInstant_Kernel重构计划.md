# 研发计划 006：统一原子 Occurrence 与 LogicalInstant Kernel 重构

**状态：目标方案与实施基线**

**日期：2026-08-21**

**关联核心设计：`开放世界棋盘游戏设计_003_Forecast_Elapse_Decide_SimulationKernel.md`**

---

## 1. 目标与决策

本计划把 Simulation Kernel 重构为统一的离散 occurrence 内核：

```text
ContinuousFlow（可 Lazy 求值）
→ Forecast 全局下一批候选
→ 确定性仲裁唯一 winner
→ 若 winner 是 DecisionPoint，则挂起调用 Player 策略，校验并 stage DecisionProposal
→ Plan 一个全局原子 transition
→ 原子 Commit
→ 全量 re-Forecast
→ repeat
```

核心决定：

1. 删除 internal event / external input 的特殊调度路径；
2. V1 runtime candidate 全部由 World+Manifest 纯推导；唯一 runtime strategy 例外是已胜出的 Player `DecisionPointCandidate` continuation，Player 返回的 `DecisionProposal` 经校验/stage 后恢复原 winner，不形成第二轮候选；
3. 权威逻辑时刻为 `LogicalInstant = (ModelTime, CausalOrdinal)`；
4. 每个因果前沿只允许一个 `AtomicOccurrence` 成为下一原因；
5. 同一 `ModelTime` 的 occurrence 以不同 `CausalOrdinal` 严格排序，不再拥有 simultaneous causality；
6. 已知精确时间先于伪随机仲裁；只有真正同位的候选才计算确定性 PRF rank；
7. winner 对整个 `HostWorld` 的影响必须 all-or-nothing；
8. 一个 occurrence 可以产生多条审计事实，但它们共享同一个 `LogicalInstant`；
9. commit 后所有旧 Forecast 无条件失效，规范实现全量重新 Forecast；
10. `src/FirstBoard`、`src/FirstBoard.Demo`、`src/Spatial` 与目标设计冲突时必须迁移或删除旧行为，不得要求 Kernel 保留双轨兼容。

这是一项 breaking refactor。开发期间可以有短命适配器，但发布路径不得同时存在两套时间法则或两个 commit authority。

---

## 2. 范围与非目标

### 2.1 本计划负责

- Kernel 时间、Forecast、仲裁、Plan、Commit 与 Replay 契约；
- Journal 的 transition 原子记录；
- Player/AI 的回合制策略调用、`DecisionInvocation` 挂起、proposal staging 与恢复协议；
- 跨 Game、Spatial、Inventory、Faction 等域的原子 transition；
- FirstBoard、Demo、Protocol、Player.Llm 与 Spatial 的迁移；
- 旧 Journal/checkpoint 的版本门与迁移策略；
- determinism、liveness、capacity、recovery 和性能验收。

### 2.2 本计划不负责

- 具体战斗、经济、社交或 Providence 的玩法规则；
- Providence 的 LLM/planner suspension；如未来需要，必须另立 ADR，不复用 V1 的 Player-only 例外；
- 把 PRF rank 解释成物理时间；
- 让 Forecast、普通 Plan 或领域 reducer 执行网络 I/O、调用 Player/LLM 或产生外部副作用；被选中的 `DecisionPointCandidate` 在 Plan 前进入受控策略调用挂起，是唯一例外；
- 在实现前预设增量 Forecast、局部 heap 或 kinetic index；
- 维护旧 Demo/Spatial 的旧语义兼容性。

---

## 3. 目标概念模型

### 3.1 术语

| 术语 | 权威含义 |
|---|---|
| `ContinuousFlow` | 两个 occurrence 之间，可从 anchor/rate/law 在查询时 Lazy 求值的连续演化。 |
| `AtomicOccurrenceCandidate` | 尚未发生、可失效、可重算的未来提案。 |
| `AtomicOccurrence` | 全局仲裁胜出的唯一瞬时原因。 |
| `WorldCommand` | 被选中的 Player/AI 策略在 DecisionProposal 中提出的世界改变请求；可能被领域 Plan 拒绝，不是既成事实。 |
| `DecisionPointCandidate` | 表示“现在应由某个 Player 为某个 Actor 决定下一步”的普通全局候选；它先参与仲裁，胜出后才调用策略。 |
| `DecisionInvocation` | `DecisionPointCandidate` 胜出后形成的唯一挂起控制状态，绑定 winner、冻结 WorldSnapshot 与 invocation identity；本身不是 World fact。 |
| `DecisionProposal` | Player strategy 的原始返回：WorldCommand 与私有 strategy-state staged delta；尚未通过协议校验或取得提交资格。 |
| `PersistedDecisionProposal` | 通过 protocol validation 后，以 InvocationId first-write-wins 持久化的唯一 canonical proposal；只以 opaque refs/hashes 暴露给 Kernel。 |
| `DecisionAudit` | CommittedTransition 中对 Invocation、Observation、Strategy、WorldCommand 与 strategy-state refs/hashes 的审计锚点。 |
| `AtomicTransitionPlan` | winner 对完整 `HostWorld` 的不可分割影响计划。 |
| `CommittedTransition` | 已原子写入 Journal 的 occurrence、结果与事实集合。 |
| `DomainFact` | transition 内的审计事实，不独占新的因果时刻。 |
| `LogicalInstant` | `(ModelTime, CausalOrdinal)`，一个 occurrence 的唯一逻辑时刻。 |
| `FactOrdinal` | 同一 transition 内事实的稳定编码顺序；不是时间。 |
| `ForecastBasis` | 一轮 Forecast 冻结的纯模拟基线：WorldSnapshot、WorldVersion、LogicalNow 与 Run Manifest 版本。 |
| `StableCandidateKey` | Host 分配、跨 Replay/Fork 稳定的候选身份。 |
| `ArbitrationRank` | 当前因果前沿中真正同位候选的确定性选择分数。 |
| `Provenance` | Player/AI、普通规则与纯 Providence law 的来源、依据和权限；不构成调度优先级。Setup 只属于 Genesis，V1 无 runtime Admin origin。 |

### 3.2 时间与原子性

```text
LogicalInstant
    ModelTime
    CausalOrdinal       // non-negative; resets when ModelTime advances

CommittedTransition
    CommitOrdinal       // lineage-wide, strictly increasing
    LogicalInstant
    OccurrenceId
    Cause
    Provenance
    Facts[]
        FactOrdinal     // 0..N-1, serialization/reducer order only
```

硬约束：

- 每成功提交一个 atomic occurrence，`CausalOrdinal` 恰增加一次；
- `ModelTime` 前进时，首个 occurrence 使用 `CausalOrdinal = 0`；
- 同一 transition 的全部 facts 共享同一 `LogicalInstant`；
- `FactOrdinal` 不得进入 World query、DecisionInvocation 的逻辑前沿、Snapshot/Fork 地址或候选优先级；
- reducer 可以在 scratch 中按 FactOrdinal fold，但中间 prefix 永远不可观察；
- `WorldVersion = (LineageId, TransitionCount, HeadHash)` 只指向完整 transition boundary；
- Snapshot、Fork、committed Observation 与 checkpoint 不得指向 transition 内部；DecisionInvocation 只能绑定完整的 pre-transition WorldVersion。

这意味着不能只把旧 `Microstep` 重命名成 `CausalOrdinal`。旧模型给同一 batch 的每条 event 分配不同 Microstep；目标模型把整个 batch 视为一个不可分割原因。

### 3.3 Candidate、Command、Fact 不得混同

```text
Candidate = 尚未发生的预测数据
Command   = 对世界提出的请求，可成功或拒绝
Fact      = commit 后不可否认的过去事实
```

Candidate 必须是可序列化纯数据，只保存 stable identity、Due、Generation、HandlerId 与 payload。不得捕获闭包、Journal、可变 World、墙钟、网络 client 或状态型 RNG。

Handler 的 `Plan` 是纯函数。Candidate/Handler 均没有 commit 权；Kernel 是唯一 commit authority。

### 3.4 一个 occurrence 可以包含多事实

例如 `UseTicketAndBoard` 可以产生：

```text
TicketConsumed
TraversalStarted
CommandAccepted
```

三条都是同一 occurrence 的 `DomainFact`，共享一个 `LogicalInstant`。Kernel 必须先对完整 HostWorld scratch-fold，再整体提交；不能把扣票与移动拆成两个可被其它候选插入的 occurrence。

如果 Game scratch 成功但 Spatial 失败，或反过来，World、Journal、WorldVersion 与 LogicalInstant 都必须零变化；已选 winner 仍处于原来的未提交解析边界。

---

## 4. 联合 Forecast 与确定性坍缩

### 4.1 Source 契约

每轮 Forecast 只依赖当前完整 World 与冻结的运行规则。Player 尚未被调用，也不存在另一个等待注入的 command 候选集合：

```text
ForecastBasis
    WorldSnapshot                 // immutable value/handle used by Forecast and Plan
    WorldSnapshotHash
    WorldVersion
    LogicalNow
    RunManifestVersion
```

`ForecastBasis` 是纯模拟前沿。所有 source 基于同一个 immutable WorldSnapshot Forecast：

```text
IOccurrenceSource.ForecastNext(
    WorldSnapshot,
    ForecastContext)
    -> zero or one local-minimum candidate
```

Source 若只返回一个 candidate，必须使用与 Kernel 完全相同的 comparator 选择本地最小值；否则会隐藏真正的全局 winner。Reference implementation 可以让 source 返回完整 earliest frontier，由 Kernel 取最小，以便差分验证。

Candidate 必须携带完整 `ForecastBasis` 或其规范 hash；Plan 与 commit 都要校验它。相同 WorldVersion、LogicalNow 与 Run Manifest 产生相同候选宇宙，不存在 wall-clock ingress 改写本轮 contender set 的入口。

V1 来源闭包为：

- 普通规则、物理与纯 Providence law 只能从 World+Manifest Forecast；
- AI/Human Player 使用 `DecisionPointCandidate → DecisionInvocation`；
- Providence 不得调用 Human/AI/LLM planner；这类能力留给未来 ADR；
- runtime/live Admin command 与 Admin decision 一律禁用；
- Setup 只允许构造 Genesis / 新 lineage，不是 runtime ingress；
- 预编排脚本必须在 Genesis 时冻结进 World 或 Run Manifest。

除此以外的 runtime payload 来源一律视为隐藏 external ingress。

Forecast/Plan 禁止：

- 修改 World 或 source state；
- 消费 stateful RNG；
- 墙钟、线程顺序、集合插入顺序或运行时 hash；
- 文件、网络、数据库写入；
- 调用 Human、LLM、AI driver 或 Providence planner。

最后一条只允许在 Player `DecisionPointCandidate` 已经成为唯一 winner 后，由 Kernel 的受控 suspension protocol 例外执行；策略返回前不得 Plan、commit、推进 LogicalInstant 或重新 Forecast。Providence/Admin 不共享这个例外。

### 4.2 全局比较器

```text
1. Due.ModelTime
2. EffectiveExactDue
3. ArbitrationRank
4. StableCandidateKey canonical bytes     // collision fallback
```

其中：

```text
EffectiveExactDue = KnownExactDue ?? CanonicalRational(Due.ModelTime)
```

`KnownExactDue` 必须采用 Kernel 版本化的 canonical signed-rational codec，且全部映射到同一全局 ModelTime epoch/unit，并满足 `SettlementBucket(KnownExactDue) == Due.ModelTime`。普通整数边界可表示为 denominator=1；Spatial contact 等桶内精确时间必须进入该比较，不能先 ceil 后让 PRF 颠倒已知物理先后。不能证明桶内精确时刻的候选使用权威 Due 作为 EffectiveExactDue，不允许使用 pairwise “可比较/不可比较”分支。

因此 comparator 对任意 candidate 都是严格全序，必须通过 antisymmetry、transitivity、totality 性质测试；有/无 KnownExactDue 混排也要有 golden。

### 4.3 PRF

建议坐标：

```text
ArbitrationRank = PRF(
    RunArbitrationSeed,
    OrderingRulesVersion,
    CanonicalDue,
    CanonicalEffectiveExactDue,
    StableCandidateKey)
```

要求：

- PRF 算法、常量、canonical codec 与规则版本进入 Run Manifest；
- Fork 默认继承 seed，使相同 World 前缀与 Manifest 继续产生相同 winner；
- rank 不使用 `.NET GetHashCode`、对象地址或枚举顺序；
- Player 返回的 WorldCommandId 或 payload 不得进入已经选定的 DecisionPoint rank；
- Host 根据 Decision、Actor、source、occurrence generation 分配稳定 CandidateId；
- rank 相同用完整 stable key 兜底；
- Replay 直接重放 committed winner；audited replay 才重新计算 PRF。

PRF rank 只是当前因果前沿的选择分数，不是可持久化的小数时间。新 candidate 即使 rank 更小，也只能在产生它的 occurrence 之后参加下一轮。

### 4.4 规范循环

```text
Step(world, cursor):
    require cursor has no suspended DecisionInvocation

    basis = Freeze(
        world.Snapshot,
        world.Version,
        cursor.LogicalNow,
        runManifest.Version)
    candidates = ForecastAllSources(basis)

    if candidates empty:
        return Exhausted

    winner = SelectGlobalMinimum(candidates)
    proposedInstant = ProposeLogicalInstant(winner.Due.ModelTime)

    if winner is DecisionPointCandidate:
        invocation = CreateDecisionInvocation(
            DeriveInvocationId(basis, winner),
            basis,
            winner,
            proposedInstant,
            BuildCanonicalObservation(world, winner))
        cursor = SuspendWithoutAdvancing(invocation)
        return DecisionInvocation(invocation)

    plan = ResolveHandler(winner.HandlerId)
        .Plan(world, winner, basis, proposedInstant)
    return CommitWinner(plan, basis, proposedInstant)

PrepareDecisionProposal(invocationId, rawStrategyOutput):
    invocation = require exactly matching suspended invocation
    protocol = ValidateProtocolOutput(
        invocation,
        rawStrategyOutput,
        invocation.WorldCommandSchema)
    if protocol invalid:
        return InvalidStrategyOutput(same invocation remains suspended)
        // diagnosis is non-authoritative; do not write final memo

    proposal = CanonicalizeDecisionProposal(rawStrategyOutput)
    memo = DriverStore.StageAndCompareExchangeFirstWriteWins(
        invocationId,
        expected = absent,
        proposal)
    if memo exists with same canonical proposal:
        return PersistedDecisionProposal(memo)       // idempotent
    if memo exists with different proposal:
        return InvocationResultConflict(memo.StagedProposalHash)
    return PersistedDecisionProposal(memo)           // persist-before-expose

ResumeDecision(invocationId, persistedProposal):
    committed = Journal.FindByOccurrenceId(DeriveOccurrenceId(invocationId))
    if committed exists:
        if committed.DecisionAudit.StagedProposalHash
            == persistedProposal.StagedProposalHash:
            return Committed(committed)              // idempotent retry / lost ACK
        return InvocationResultConflict(
            committed.DecisionAudit.StagedProposalHash)

    invocation = require exactly matching suspended invocation
    require WorldVersion, Journal head and ForecastBasis unchanged
    require persistedProposal was CAS-persisted-before-expose
    require persistedProposal.StrategyStateBaseRef/Hash
        == invocation.CommittedStrategyStateRef/Hash
    if RevalidateProtocolEnvelopeAndCapability(
        invocation, persistedProposal) fails:
        return DriverStoreInvariantFault             // stop; final memo cannot be replaced

    plan = ResolveDecisionCommand(
        invocation.FrozenWorldSnapshot,
        invocation.Winner,
        persistedProposal.WorldCommand,
        invocation.ProposedLogicalInstant)
    plan = AttachDecisionAudit(plan, invocation, persistedProposal)
    transition = CommitWinner(
        plan,
        invocation.ForecastBasis,
        invocation.ProposedLogicalInstant)
    clear suspension
    return Committed(transition)

CommitWinner(plan, basis, logicalInstant):
    ValidateCandidateBasis(plan, basis)
    scratchWorld = ApplyFactsPure(basis.WorldSnapshot, plan.Facts)
    ValidateFinalWorld(scratchWorld)
    ValidatePlanAndCapacity(plan, scratchWorld)
    transition = EncodeCommittedTransition(plan, basis.Hash, logicalInstant)
    Journal.AppendTransition(expectedHead, transition)   // atomic
    InstallAlreadyValidatedWorld(scratchWorld)
    AdvanceCursorAfterCommit(transition.ResultingWorldVersion, logicalInstant)
    DiscardAllForecasts
    return transition
```

这是 breaking API：`Step → DecisionInvocation`、`PrepareDecisionProposal → PersistedDecisionProposal` 与 `ResumeDecision(PersistedDecisionProposal) → CommittedTransition` 取代旧 `DecisionRequested` event、`PlayerDecisionSession` 和 `Run(externalInputs)`。`DecisionPointCandidate` 被选中但 Player 尚未返回时，winner 还没有 commit，`LogicalInstant`、WorldVersion 与 Journal head 均未推进；`ProposedLogicalInstant` 只是挂起解析的预留值。

一个 session 同时至多存在一个 `DecisionInvocation`。挂起期间再次 `Step`、处理另一 candidate、提交管理命令或推进时间都属于非法调用；timeout、断连或 invalid output 也不能释放 winner 后重新 Forecast。只有继续等待/重试同一 invocation，或者由冻结的运行 policy 返回显式 `Pass / Wait / Fallback WorldCommand` 并作为这个已选 occurrence 提交。

Audited Replay 按 transition 记录的 ForecastBasis 重建 contender set。Decision transition 还记录 `DecisionInvocationId`、Observation hash、Strategy identity/version 与最终 `WorldCommand`；审计时使用已记录 command 重跑纯 Plan，不重新调用 Player。

任何 winner 都必须被消费、改变 Generation 或产生显式 rejection。空 transition、相同 candidate 无进展复发、无限同 ModelTime occurrence chain 都是确定性失败，并受规则版本化预算限制。

---

## 5. Player、AI、Host 与 Providence

### 5.1 删除调度分类，保留 provenance

调度器不再区分：

- internal / external；
- rule / Player；
- scheduled / direct；
- AI / Human；
- Providence / ordinary Game rule。

但策略调用与 Journal 必须保留：

```text
RuntimeOrigin = ScheduledLaw | PlayerHuman | PlayerAI | ProvidencePureLaw
Authority / Capability
ActorId?
DecisionId?
DecisionInvocationId?
StrategyId / StrategyVersion?
WorldCommandId?
InterventionId?
ProvidenceId?
BasedOnWorldVersion
ObservationHash?
```

这些字段决定授权、幂等、审计与信息归属，不参与通用时间优先级，也不凭空赋予 runtime ingress 能力。`Providence` 只标识可从 World+Manifest 重算的 pure law；V1 不定义 runtime `Admin` origin；`Setup` 只写入 Genesis/Run Manifest，不是 CommittedTransition 的 runtime origin。旧 Journal 的 legacy origin 只服务迁移审计。尤其 `WorldCommand` 在 winner 选定后才存在，绝不能回头改变该 winner 的 PRF rank。

### 5.2 Player 是被 Kernel 调度的回合制策略函数

每个可由 Player/AI 控制的 Actor 通过普通 Source 预测自己的 `DecisionPointCandidate`。Candidate 只表示“该 Actor 现在有权决定下一步”，不提前调用 Player，也不携带尚不存在的 action：

```text
DecisionPointCandidate
    PlayerId / ActorId
    DecisionGeneration
    Due / EffectiveExactDue
    StableCandidateKey
    DecisionReason
    StrategyId
    ObservationSpec
    WorldCommandSchema
```

胜出后派生的 continuation 至少包含：

```text
DecisionInvocation
    DecisionInvocationId
    ForecastBasis / FrozenWorldSnapshot
    DecisionPointCandidate
    CanonicalObservation / ObservationHash
    StrategyId / StrategyVersion
    CommittedStrategyStateRef / Hash
    WorldCommandSchema
    ProposedLogicalInstant
```

它与 ticket deadline、碰撞、到达、天气、另一 Actor 的 DecisionPoint 等使用完全相同的全局 comparator。只有它成为唯一 winner 后，Kernel 才从同一个冻结 WorldSnapshot 构造合法 Observation 与 action schema，并调用：

```text
IPlayerStrategy.DecideAsync(DecisionInvocation)
    -> DecisionProposal
```

策略调用可以在 wall-clock 上挂起任意时间，但 ModelTime 不前进。此时 candidate 尚未 commit，也不存在一条 `DecisionRequested` 世界事件。Proposal 先经 protocol validation 和 DriverStore first-write-wins staging；只有得到 `PersistedDecisionProposal` 后才能恢复该 invocation。其 WorldCommand 不是新的 candidate，不再参加一次 PRF 仲裁，也不能在另一个 WorldSnapshot 上 Plan。

对外 API 的最小判别形状为：

```text
KernelStepResult =
    Committed(CommittedTransition)
    | DecisionInvocation(InvocationId, PlayerId, Observation, AvailableCommands, FrozenWorldVersion)
    | Exhausted
    | HorizonReached

PrepareDecisionProposalResult =
    PersistedDecisionProposal(opaque refs/hashes)
    | InvalidStrategyOutput(InvocationId, validationErrors)
    | InvocationResultConflict(InvocationId, stagedProposalHash)

ResumeDecisionResult =
    Committed(CommittedTransition)
    | DriverStoreInvariantFault(InvocationId, diagnostics) // stop/quarantine
    | InvocationResultConflict(InvocationId, committedProposalHash)
```

Human UI、脚本 Player 与 LLM Player 都实现同一策略接口。V1 只有 Player 可以进入该 suspension；Providence 必须保持 pure law，Admin runtime/live decision 禁用，二者都不能借此恢复 external-result ingress。

### 5.3 A/B 顺序与票据竞态

若 Alice 与 Bob 都在 T 拥有 DecisionPoint：

```text
DecisionPoint(Alice) @ T
DecisionPoint(Bob)   @ T
```

PRF 先选择其中一个，例如 Alice。Kernel 只调用 Alice；Alice 的 command 在冻结 snapshot 上 Plan 并原子提交。随后 full re-Forecast，Bob 的 DecisionPoint 才在 Alice 已改变的世界上重新判断 Due、Observation 与合法 action。不存在“同时收集 A/B 回答”或按网络返回先后决定世界的阶段。

票据到期同理：

```text
ExpireTicket
DecisionPoint(Actor)
```

- 若 `ExpireTicket` 先赢，先提交到期；重新 Forecast 后 Actor 才被调用，并看到“晚了一步”的新世界；
- 若 `DecisionPoint` 先赢，Kernel 暂停并调用 Actor。若 Actor 返回 `UseTicketAndBoard`，则 `TicketConsumed + TraversalStarted` 在该 winner 的同一原子 transition 中提交；到期 candidate 随后重新 Forecast 并消失或按新世界处理。

因此竞争发生在 `DecisionPointCandidate` 与其它世界 candidate 之间，而不是在 Player 返回 command 后再造一轮 command-vs-deadline 仲裁。

### 5.4 输出校验、挂起与 crash/retry

策略输出分两层处理：

- envelope/schema、InvocationId、大小和 capability 等结构错误：`ValidateProtocolOutput` 拒绝，不写 authoritative result memo，不 commit、不释放 winner、不 re-Forecast；以同一个 invocation 重问或重试。可记录的 invalid-attempt diagnosis 不是 final result，不能阻止后续修正 payload；
- 结构合法但当前领域前置条件失败：pure Plan 生成明确 rejection facts，并把本次 DecisionPoint 作为一个已完成 occurrence 原子提交；
- timeout、断连或 cancellation：世界继续保持 suspended；只有运行前冻结的 policy 明确产生 `Pass / Wait / Fallback WorldCommand` 时，才能作为同一 invocation 的结果提交，不能悄悄跳过 winner 去处理下一 candidate。

任一结构合法的 accepted、domain-rejected 或 fallback Decision transition 都必须把该 Actor 的 `DecisionGeneration` 恰推进一次；invalid output 不推进。这样已完成的 DecisionPoint 不会以相同 identity 再次 Forecast。

`DecisionInvocationId` 固定由 `Canonical(ForecastBasis, StableCandidateKey, winner.Generation, ProposedLogicalInstant)` 派生；codec/hash/domain separator 进入 Run Manifest。为保证 crash/restart 与 one-shot 等价，Human UI、脚本和 LLM adapter 必须按该 ID 幂等并 durable memoize 已经返回的 canonical `WorldCommand`。顺序必须是：

```text
raw strategy output
→ ValidateProtocolOutput
    invalid: diagnose and keep suspended; no final memo
    valid: canonicalize WorldCommand
→ persist final memo by InvocationId CAS / first-write-wins
→ expose PersistedDecisionProposal to ResumeDecision
```

对第一条 protocol-valid command，同 invocation、同 canonical payload 是幂等重试；另一条 protocol-valid payload 是确定性的 conflict，不能覆盖第一次选择。结构合法但 domain-invalid 的 command 仍属于 protocol-valid final memo，随后由 pure Plan 提交 rejection。底层 LLM/网络 provider 调用通常最多只能保证 at-least-once，Kernel 保证的是同一 invocation 至多接受并提交一个 memoized command。

final CAS 完成后，Resume 对 store receipt、StagedProposalHash、StrategyStateBaseRef/Hash 或 protocol validator 不变性的任何不匹配都是 `DriverStoreInvariantFault / CorruptPersistedProposal`：必须停止并隔离诊断，不能把它当成可重问的 invalid output，也不能释放 winner 或接受替换 proposal。只有 CAS 前的 `ValidateProtocolOutput` failure 才允许修正后重试。

这个 continuation memo 只恢复已经选中的 winner：

- 不进入 ForecastBasis；
- 不产生新 candidate；
- 不允许换 WorldSnapshot；
- `ResumeDecision` 先按 InvocationId 派生的 OccurrenceId 查 committed index：已有且 StagedProposalHash 相同则返回既有 transition，hash 不同则 conflict；只有尚未提交时才检查 active invocation/basis；
- crash 发生在 append 前时，重启后从相同 WorldVersion 全量 Forecast，得到相同 winner 与 InvocationId，再恢复 memoized command；
- crash 发生在 append 后、清理 memo 前时，以 Journal 为 authority，丢弃 continuation memo。

若 PlayerStrategy 有会影响未来选择的可变 Memory/Belief/随机游标，Driver store 必须先持久化 protocol-valid staged proposal 与 staged state delta，再把 opaque refs/hashes 交给 Resume：

```text
StrategyStateBaseRef / Hash
StagedProposalRef / Hash
ResultStrategyStateRef / Hash
```

World Journal 不保存 raw Human/LLM Memory delta。最终 CommittedTransition 只在 provenance 中锚定上述 refs/hashes；Journal append 成功才使 `ResultStrategyStateRef` 成为该 lineage 的 committed strategy state。append 失败时 staged state 对后续调用不可见并可回收；append 成功但 Driver projection 尚未激活便 crash 时，恢复按 Journal anchor 幂等激活 result state，不需要 World Journal 与 Driver store 的分布式事务。

strategy state 不进入 ForecastBasis，OccurrenceSource 也不得读取它来改变 Candidate/Due/rank；Fork manifest 必须继承最后一个 committed `ResultStrategyStateRef/Hash`。若某第三方 adapter 不提供这些保证，只能声明“Kernel World 可恢复”，不能宣称 Player 行为、费用或未来分支 restart == one-shot。

本计划的正式完成标准要求 Host 与内置 Human/Scripted/LLM adapter 实现上述 memo/idempotency 与 strategy-state 版本契约。

暂停是 control-plane 状态，不是 WorldVersion。Snapshot/Fork 只能发生在 committed transition boundary；存在未完成 invocation 时不得 Fork。普通 Replay 从不恢复或调用 Player，只重放已经提交的 command 结果。

### 5.5 外部副作用

除受控 `IPlayerStrategy` 挂起外，邮件、网络发送、Godot 通知等不进入 Plan。Committed transition 产生 outbox fact，Host 在 commit 后幂等执行；恢复时可重试，不污染 World 原子性。Player/LLM 调用只返回 `DecisionProposal`，不得在策略回调中直接修改 World 或 Journal；raw state delta 只能进入 driver staged store。

---

## 6. Journal、Replay、Fork 与恢复

### 6.1 Journal 目标接口

```text
IAtomicJournal.AppendTransition(
    ExpectedJournalHead,
    CommittedTransition)
```

强制契约：

- 整个 transition 一次可见；
- append 失败时零 fact 可见；
- FactOrdinal 从 0 连续；
- transition 非空；
- CommitOrdinal、CausalOrdinal 与 hash chain 连续；
- 所有 facts 共享 header 的 LogicalInstant、Cause 与 Provenance；
- Kernel 在 append 前已经完成 scratch-fold 和 invariant validation。

持久化成功而进程在安装内存 World 前崩溃时，恢复通过 Journal Replay 得到 committed final world。Plan 不得提前产生不可重放副作用。

`WorldVersion.HeadHash` 与 transition hash 采用无自引用的两阶段递推：

```text
ExpectedWorldVersion = (lineage, N, previousHash)

TransitionHash = Hash(
    previousHash,
    canonical transition header
        excluding TransitionHash
        excluding ResultingWorldVersion,
    Facts)

ResultingWorldVersion = (lineage, N + 1, TransitionHash)
```

空 lineage 的 `TransitionCount = 0`，第一条 CommittedTransition 的 `CommitOrdinal = 0`，不伪造 `(ModelTime, -1)`。Cursor 保存 `FrontierModelTime + LastCommittedLogicalInstant?`；某 ModelTime 的第一条 transition 分配 `CausalOrdinal = 0`。Fork 只复制完整的 ResultingWorldVersion，不允许指向 transition 内部。该算法及 canonical header 必须有 codec、empty-lineage 与 Fork golden。

### 6.2 Replay

普通 Replay：

```text
read CommittedTransition
→ validate envelope/hash/ordinals
→ atomically fold Facts[]
→ expose only final world
```

普通 Replay 只原子 fold `Facts[]`；不 Forecast、不重新抽 PRF、不调用 Player/LLM，也不把 `WorldCommand` 当作第二条状态写入路径。WorldCommand 与 CommandResult 只是 CommittedTransition 中可审计的 cause/result payload。

Player strategy state 不由 World reducer fold；Host recovery 按最后一个 committed Decision transition 锚定的 `ResultStrategyStateRef/Hash` 恢复并校验 Driver store，幂等激活对应 projection。该控制面恢复不能改变已经 Replay 出的 World，Journal 也不读取或复制 raw strategy-state delta。

Audited Replay 在每个 transition boundary 重算 candidates/ranks，验证记录的 candidate 确实是 winner；对 Decision transition，它读取已提交的 `DecisionInvocationId + ObservationHash + WorldCommand` 重跑 pure Plan，并逐字节比较重建的 Facts、CommandResult 与 canonical plan hash，但仍不调用 Player。

### 6.3 Fork

Fork 只发生在 transition boundary，并继承：

- World、WorldVersion、LogicalInstant、Journal prefix/hash；
- RunArbitrationSeed 与 OrderingRulesVersion；
- source generation/watermark/pending world state；
- Player/Strategy identity、版本化 policy 与最后一个已提交 transition 锚定的 `ResultStrategyStateRef/Hash`。

Forecast cache 可丢弃并全量重建。Fork 只能发生在 committed transition boundary；若 session 正挂起一个 `DecisionInvocation`，必须先用原 invocation 完成 commit 或终止整个 session，不能把未提交策略调用复制进子 lineage。

---

## 7. 当前代码冲突与处置

| 当前位置 | 冲突 | 目标处置 |
|---|---|---|
| `Kernel/Simulation/SimulationLoop.Run(externalInputs)` | external 在 Forecast 前直接提交 | 删除参数与路径；改为 `Step` 选择 winner，或用 `PersistedDecisionProposal` 恢复已经选中的 Decision winner。 |
| `Kernel/Scheduling/ForecastQueue` | 同 Due 固定按 SourceId/CandidateId | 改为 Due → KnownExactDue → PRF rank → stable key。 |
| `Kernel/Journal/EventCause` | `ResolveBatch / ExternalInput` 是调度分类 | 改为统一 occurrence cause + provenance。 |
| `LogicalTimestamp / Microstep` | batch 内每 fact 占一个时间 | 改为 transition 级 LogicalInstant + FactOrdinal。 |
| `SimulationLoop.CommitAndApply` | Journal append 后才 reducer | 改为 scratch-fold/validate → atomic append → install。 |
| `IJournalSink.AppendBatch` | 默认逐条 Append，不保证原子 | 替换为强制 `AppendTransition`。 |
| Kernel decision predicate/stop reason | 先提交 `DecisionRequested`，再由 Host 特殊停机 | 删除；`Step` 在 DecisionPoint winner 未提交时返回 `DecisionInvocation`，`ResumeDecision` 才提交该 occurrence。 |
| `PlayerDecisionSession` | answer 先翻译成 external event，再形成 PendingAction | 以 `IPlayerStrategy`、单一 suspended invocation 与 continuation memo 取代；不得保留“回答成为第二轮候选”的调度器。 |
| `FirstBoard.DecisionSchedulingSystem` | 一次产生批量 DecisionRequested facts，并用 internal-first barrier 推迟 Player | 改为每个 eligible Actor 的 `DecisionPointCandidate`；全局 arbiter 一次只选择并调用一个 Player。 |
| `FirstBoard.ActionResolutionSystem / PendingAction` | Player command 先写成世界 pending state、下一轮才 resolve | 删除两阶段；`ResumeDecision` 从 persisted proposal 读取 WorldCommand，在 winner 的冻结 FirstBoardWorld 上直接 pure Plan。 |
| `FirstBoard.Demo` | 展示旧 Microstep/external cause，并通过 session 注入回答 | 跟随 LogicalInstant/FactOrdinal；UI 接收 `DecisionInvocation`，raw output 必须经 Host validate/stage 后才能 Resume。 |
| `Host / Protocol / Player.Llm` | `DecisionRequest → PlayerDecision → externalInputs` | breaking replacement：`Step → DecisionInvocation`、`IPlayerStrategy → DecisionProposal`、validate/stage → `PersistedDecisionProposal`、`ResumeDecision → CommittedTransition`；LLM adapter 按 InvocationId 幂等/memoize。 |
| `SpatialSubsystem` | `SpatialMoment` 批量处理同 T work、固定 phases | 拆成单 work-item candidates；必要 facts 留在 winner transition。 |
| `SpatialCommandHandler` | simultaneous command batch、alias/conflict phase | 普通 command 独立 candidate；显式 composite command 才成一个 occurrence。 |
| Spatial interaction watermark | 一次 Moment 消费整个 T | 改为 pair/segment/generation 或精确 interaction cursor；一个 contact 不得吞掉同 T 其它 contact。 |

现有可复用资产：

- `ForecastAll → one candidate → re-Forecast` 的循环骨架；
- immutable World 与 pure reducer 方向；
- `ISimSystem.Resolve` 不直接改 World 的约束，可演进为 pure Plan；
- Atelia 单 frame batch 的持久化基础；
- `DeterministicRandom` 的 addressable sampling 思路；
- Spatial scratch transition 与 complete-state relation diff。

---

## 8. 实施波次

每一波结束必须编译、通过该波新增测试，并保持新旧语义边界明确。临时适配器只能存在于迁移分支，不得成为正式兼容层。

### 波次 0：冻结 ADR、基线与破坏面

交付：

- 将 003 作为目标 Law；
- 锁定本计划中的术语与核心不变量；
- 为旧 Kernel、Host、FirstBoard、Spatial 建 characterization tests；
- 列出所有 external-first/internal-first/same-time phase 旁路；
- 冻结 EffectiveExactDue codec、严格全序 comparator、PRF、seed、CandidateKey、纯模拟 ForecastBasis、Decision suspension/Fork 规则；
- 决定旧 Journal 是只读、离线迁移还是放弃；不得模糊兼容。

退出条件：没有未登记的 commit path、时间字段、Decision ingress 或 subsystem gateway。

### 波次 1：时间、身份与 Manifest 值对象

新增：

- `CausalOrdinal`；
- `LogicalInstant`；
- occurrence-based `WorldVersion`；
- `FactOrdinal`；
- `StableCandidateKey / OccurrenceId / Generation / HandlerId`；
- canonical `KnownExactDue / EffectiveExactDue`；
- `OrderingRulesVersion / RunArbitrationSeed` manifest；
- comparator 与 codec goldens。

暂不改变业务行为，但禁止新代码继续扩散 `Microstep` 的旧语义。

### 波次 2：原子 Journal 与 pure scratch transition

实施：

- `CommittedTransition` 一级 envelope；
- `AppendTransition(expectedHead, transition)`；
- InMemory 与 Atelia 原子实现；
- pure batch reducer/scratch-fold；
- final invariant/hash validation；
- FactOrdinal 与 transition-boundary Snapshot/Fork；
- crash-at-before/during/after-append fault injection。

完成后，任何 batch 都不能暴露 reducer prefix。

### 波次 3：统一 occurrence scheduler

实施：

- `IOccurrenceSource` 与 `IOccurrenceHandler.Plan`；
- full Forecast reference loop；
- Due/KnownExactDue/PRF/fallback comparator；
- 每轮唯一 winner；
- 每次 winner 后全量 re-Forecast；
- no-op、same-ModelTime budget、capacity 防线；
- candidate/rank/winner debug trace；
- `Step` 的 `Committed / DecisionInvocation / Exhausted` 判别结果；
- `ProposeLogicalInstant` 只计算、不占用 ordinal；只有 atomic Commit 成功才推进时间；
- suspension single-flight guard，移除旧 Kernel 的 decision-event predicate。

先以 timer、reroute、collision、ticket、loot contention toy models 锁定语义。为保证每波可运行，旧 ingress 此时只允许存在于显式标记、不可进入新 lineage 的 migration adapter；它不得成为第二条 live scheduler path。

### 波次 4：Player strategy 与 Decision suspension

实施：

- `DecisionPointCandidate` source contract；
- `Step → DecisionInvocation` breaking API；
- `IPlayerStrategy.DecideAsync(invocation) → DecisionProposal`；
- `PrepareDecisionProposal → PersistedDecisionProposal`；
- `ResumeDecision(invocationId, persistedProposal) → CommittedTransition`；
- winner 的冻结 WorldSnapshot、Observation 与 ProposedLogicalInstant；
- invocation single-flight、invalid output 重问和 explicit Pass/Wait/Fallback policy；
- `ValidateProtocolOutput → canonicalize → persist-before-Resume` 顺序；invalid attempt 不写 final memo；
- Host continuation memo 的 InvocationId CAS/first-write-wins 与 protocol-valid payload-conflict 契约；
- persisted receipt/hash/base-ref corruption 触发 deterministic invariant fault，不进入重问；
- Resume 的 committed-index-first 幂等路径，以及 append/clear-suspension 后 ACK 丢失与 restart resend；
- 内置 Human/Scripted/LLM Player 的 InvocationId 幂等，以及 Driver store 中 staged proposal/state refs；
- CommittedTransition 只锚定 strategy base/staged/result refs+hashes，append 后按 Journal 幂等激活 result state，不保存 raw Memory delta、不使用分布式事务；
- crash-before/after-append 恢复；
- audited Replay 使用已记录 command 重跑 pure Plan，不调用 Player；
- generic outbox；

本波结束后，新 lineage 的 Kernel/Host 路径只暴露新 API；Player output 只能恢复当前已选 invocation，不得成为新的 candidate。尚未迁移的 FirstBoard 只能通过波次 3 所述隔离 migration adapter 保持编译，不能进入新 lineage。

### 波次 5：跨域 composite commands

优先迁移并测试：

- 消耗 ticket + BeginTraversal；
- 支付资源 + 创建/修改 Place；
- 战斗结算 + Actor remove/move；
- 纯 Providence law 的原因 + World command；
- inventory transfer + ownership change。

每项均须一个 handler、一个 LogicalInstant、一个 atomic transition。失败时全域零提交。

### 波次 6：FirstBoard、Protocol、Player.Llm、Demo

实施：

- FirstBoard rules/actions 迁成 source/handler；
- 把 `DecisionSchedulingSystem` 改成逐 Actor `DecisionPointCandidate` source，删除 internal-first barrier；
- 删除 `PendingAction → ActionResolutionSystem` 两阶段，WorldCommand 在冻结 FirstBoardWorld 上直接 Plan；
- 删除 Object contention 二次 RNG/round；
- 两个 eligible Actor 的 DecisionPoint 由 Kernel arbiter 排序；只调用 winner，另一个在 commit 后重新 Forecast；
- Protocol 用 `DecisionInvocation / DecisionProposal / PersistedDecisionProposal` 取代旧 Request/Decision external-ingress contract，并把 Microstep 改为 CausalOrdinal；
- Player.Llm 实现 `IPlayerStrategy`，prompt/observation 绑定 InvocationId、冻结 WorldVersion 与 ObservationHash；raw response 先验证，再把 proposal/state delta stage 到 driver store；
- Player.Llm 的 Memory/Belief/随机游标使用 invocation-aware staged state，只有 Decision commit 锚定的 `ResultStrategyStateRef/Hash` 对后续调用与 Fork 可见；
- Demo UI 展示 invocation，回答交由 Host validate/stage 后以 persisted proposal Resume；trace 显示 LogicalInstant、FactOrdinal 与 provenance；
- persistence one-shot/reopen 保持等价。

本波结束时删除 live `Run(externalInputs)`、`DecisionRequested`-event/session 与 `PendingAction` ingress；所有正式调用方已经迁移到新 API。

### 波次 7：Spatial 破坏性迁移

实施：

- 拆除 `SpatialMomentCandidate / MomentResolved` 的 whole-T 聚合；
- mutation、journey boundary、arrival、contact 分别 Forecast；
- 精确 contact time 暴露给全局 comparator；
- 每次一个 Spatial occurrence，必要 primary/relation facts 同 transition 提交；
- 第一版冻结 `ConsumedContactKey = PassageId + normalized segment identity pair + both generations + exact contact instant`；
- contact transition 把该 key 投影进 Spatial authoritative state，Forecast 排除已消费 key，segment generation 结束后确定性清理；
- 禁止一个 contact 推进 whole-T watermark；
- command batch 改为独立 candidates；
- 移除 SpatialCommandGateway/internal-first；
- `MatchTraversalAtContact` receipt 改为引用 transition/fact/interaction identity；
- same-ModelTime rebase guard 若保留，明确为 Game rule 而非 Kernel 安全补丁。

本波完成后同步修订 007、008、009 中受影响的时间与同刻语义。

### 波次 8：持久化迁移与清理迁移残留

升级：

- DomainEvent/transition envelope；
- Cursor snapshot；
- Run Manifest；
- WorldVersion/Decision protocol；
- Decision continuation memo codec 与 audit tooling。

本波验证 live `Run(externalInputs)` 已在波次 6 删除，并清理：

- 旧 external-input codec、fixture 与 migration adapter；
- `CauseKind.ExternalInput` 等只服务旧 lineage 的 runtime 分支；
- 固定 microstep phase；
- internal-first gateway；
- simultaneous command batch 作为 Kernel 原语；
- source/handler Journal 直写；
- 只为旧 FirstBoard/Demo/Spatial 保留的特例。

### 波次 9：压力、差分与优化

- full Forecast reference 与任何增量优化逐步差分；
- 大量同 Due candidates、长零时间链、Spatial pair planner 性能基线；
- Journal bytes、transition size、recovery latency、LLM strategy wall-clock 与 invocation retry 指标；
- 只有 profiler 证明瓶颈后才引入 dependency invalidation、heap 或 cache。

---

## 9. 旧 Journal 与存档策略

首选 clean break：新 lineage 只写新 transition 格式，旧 lineage 保持只读。

若需要迁移：

```text
旧 EventCause.BatchOrdinal
    → 新 CommitOrdinal

同一 ModelTime 的旧 batch order
    → CausalOrdinal

旧 batch 内 event index
    → FactOrdinal

旧 ExternalInput
    → Provenance.Origin = LegacyExternal
```

旧 external batch 含多个 Player 输入时，必须保持为一个 legacy atomic transition，不能事后猜测新的仲裁顺序。

更安全的延续方式：

```text
Replay 旧 lineage 到完整 batch boundary
→ 创建新 semantics-version child lineage
→ 写 MigrationAnchor(old prefix/hash, new state hash)
→ 从新 LogicalInstant 继续
```

存在 open DecisionRequest、PendingAction 或只在旧 Host session 内存中的 Player answer 时，不直接续跑；只迁移到最后一个完整旧 batch boundary，再由新 Kernel 重新 Forecast `DecisionPointCandidate`。旧 answer 不得伪装成新 `WorldCommand` 绕过 winner 仲裁。

---

## 10. P0 验收矩阵

| ID | 范围 | 硬断言 |
|---|---|---|
| ORD-1 | LogicalInstant | 任意两个 committed occurrences 的 LogicalInstant 不同；同 ModelTime 的 CausalOrdinal 从 0 连续。 |
| ORD-2 | Fact boundary | 一个多事实 transition 只增加一次 CausalOrdinal；Facts 共享 LogicalInstant，FactOrdinal 连续且 prefix 不可观察。 |
| ORD-3 | Reordering | system 注册、candidate 枚举、并行 Forecast 完成顺序任意置换，Journal bytes 完全相同。 |
| ORD-4 | Exact time | contact T1.1、deadline T1.5、contact T1.9 必须按已知精确时间提交，PRF 不得颠倒。 |
| ORD-5 | Strict total order | Comparator 通过 antisymmetry/transitivity/totality 性质测试；KnownExactDue 有/无混排仍有唯一稳定 winner。 |
| ARB-1 | PRF | 相同 manifest/state 产生相同 winner；不同 seed 可覆盖“票先到期”与“DecisionPoint 先得到用票机会”；碰撞 fallback 稳定。 |
| ARB-2 | Identity | `WorldCommand` 在 winner 选定后才存在，不能影响 DecisionPointCandidate identity/rank；stable identity 可 Replay/Fork。 |
| CAU-1 | Dynamic frontier | 新 candidate 即使 rank 更小也不能倒插历史，只能参加下一轮。 |
| BAS-1 | ForecastBasis | ForecastBasis 只含冻结 WorldSnapshot/WorldVersion/LogicalNow/Manifest；wall-clock answer 或未提交 command 不能改写 contender set。 |
| BAS-2 | Source closure | runtime candidate 全部来自 World+Manifest pure source；唯一 strategy continuation 是已胜出的 Player DecisionPoint。Providence planner、runtime/live Admin、runtime Setup 与未冻结脚本均被拒绝。 |
| UNI-1 | Unified path | 代码守卫确认不存在 `externalInputs`、`FromExternalInput`、`CauseKind.ExternalInput` 或 direct result-event ingress。 |
| ATM-1 | Cross-domain | TicketConsumed + TraversalStarted 全成或全败；Game/Spatial 任一失败时 Journal、World、LogicalInstant 均零变化，Decision invocation 仍未提交。 |
| ATM-2 | Persistence | plan/reducer/append 任一 fault 均不暴露部分 transition；append 成功后 crash 可完整 Replay。 |
| DEC-1 | Breaking API | `Step → DecisionInvocation` 时 Journal/World/LogicalInstant 不变；只有通过 validate-before-CAS 的 matching `PersistedDecisionProposal` 才能经 ResumeDecision 提交该 winner。旧 DecisionRequested-event/session/externalInputs 路径不存在。 |
| DEC-2 | A/B order | 两个同 T DecisionPoint 只调用全局 winner；其 command 提交后才重新 Forecast 另一 Actor，网络/LLM返回顺序不参与仲裁。 |
| DEC-3 | Ticket race | ExpireTicket 胜出时不调用 Player，Actor 稍后看到过期世界；DecisionPoint 胜出时同 snapshot 调 Player，并可原子提交扣票+上船。 |
| DEC-4 | Suspension | strategy waiting、timeout、断连、invalid output 都是零 commit/零 ordinal，且不能取消 winner 后 re-Forecast；只能继续同 invocation，或提交 strategy/policy 返回的合法 Pass/Wait/Fallback command。 |
| DEC-5 | Output semantics | `ValidateProtocolOutput` 在 final CAS 前执行；结构 invalid 不写 final memo并保持 invocation，修正 payload仍可提交。accepted/domain-rejected/fallback transition 都恰推进一次 DecisionGeneration。Kernel 不维护 retry count/budget。 |
| DEC-6 | Crash/idempotency | InvocationId 由 canonical `(ForecastBasis, StableCandidateKey, winner.Generation, ProposedLogicalInstant)` 派生；第一条 protocol-valid proposal persist-before-Resume、CAS first-write-wins。Resume 先查 committed OccurrenceId：同 StagedProposalHash 返回旧 transition、异 hash conflict；覆盖 crash-before-append、append/clear 后 ACK 丢失与 restart resend；provider 只要求 at-least-once。 |
| DEC-7 | Strategy state | Driver store 先持久化 proposal/state delta；Journal 只锚定 base/staged/result refs+hashes。只有 committed result ref 对后续调用/Fork 可见；append 后 crash 按 Journal 幂等激活，无 raw Memory delta 或分布式事务。 |
| DEC-8 | Persisted integrity | final CAS 后 receipt/hash/base-ref 或 validator 不变性 mismatch 必须 deterministic fault 并停机隔离；不得重问、释放 winner或替换 proposal。 |
| RPL-1 | Replay | 普通 Replay 不 Forecast、不调用 Player/LLM，重建相同 World/LogicalInstant/head hash。 |
| RPL-2 | Audited replay | 用纯模拟 ForecastBasis 重建 contenders，重算 winner/rank；Decision transition 使用已记录 WorldCommand 重跑 Plan，逐字节匹配 Facts/CommandResult/plan hash，不调用 Player。 |
| VER-1 | Version/hash | 空 lineage、连续提交与 Fork 均满足两阶段 hash 递推，无 HeadHash 自引用且 codec bytes 稳定。 |
| FORK-1 | Fork | Fork 只允许 committed boundary；suspended invocation 不得复制。Fork 继承 committed `ResultStrategyStateRef/Hash`；相同前缀/seed/规则/strategy state 得到相同下一 winner与内置 Player 行为。 |
| PRV-1 | Provenance | 每个 Providence transition 可追溯到 World+Manifest 中的 pure law identity/parameters；不存在 Providence/Admin strategy transition。Setup 只存在于 Genesis，legacy origin 只用于旧 Journal 迁移审计。 |
| LIV-1 | Progress | selected candidate 返回空 plan 立即失败；重复 no-op、Generation 不变与无限同 T 链被确定性防线捕获。 |
| SPL-1 | Spatial | 不存在 SpatialMoment/fixed phase；单 contact 不吞掉同 T 其它 contact；contact 防重复且 Replay 相等。 |
| SPL-2 | Contact authority | ConsumedContactKey 进入 Spatial world、按 generation 清理并被 Fork 继承；Journal receipt 不能替代 Forecast 可见状态。 |
| FB-1 | FirstBoard | 双方都 eligible 时，只调用 DecisionPoint winner；其 Take 提交后另一 Actor 在新世界重新获得 Observation/affordance，无 PendingAction 和第二套 contention RNG。 |
| PERF-1 | Reference | full Forecast 只读取纯模拟 ForecastBasis；任何优化与 reference 在随机/压力 corpus 上逐字节等价。 |

P0 全部通过、旧调度路径完全删除，才算重构完成。

---

## 11. 主要风险与应对

### 风险 1：只是重命名 Microstep

如果 batch 内 facts 继续各占 CausalOrdinal，就仍可观察撕裂中间态。以 transition envelope、FactOrdinal 与 snapshot boundary tests 阻止。

### 风险 2：把 Player command 错当成第二轮 candidate

如果 `WorldCommand` 返回后再次参与 Forecast/PRF，就恢复了旧式 command-injection 双轨，并可能让 Player 在看到自己已赢得回合后重新选择排序身份。Command 只能恢复既有 `DecisionInvocation`，在该 winner 的冻结 snapshot 上 Plan。

### 风险 3：策略返回前提前占用时间

若 DecisionPoint 一胜出就推进 CausalOrdinal 或先写 `DecisionRequested`，timeout、invalid output 和 crash 会留下无结果的世界历史。只能 `ProposeLogicalInstant`；atomic commit 成功时才真正占用 ordinal。

### 风险 4：KnownExactDue 仍被 ceil 隐藏

Spatial T1.9 可能被排在 T1.1 前；pairwise “可比较”还会破坏传递性。冻结全局 EffectiveExactDue codec 与严格全序性质测试，比较精确证据后才 PRF。

### 风险 5：领域继续二次仲裁

FirstBoard contention、Spatial fixed phase 或 Providence 本地 RNG 可能推翻 Kernel winner。代码搜索和验收要求删除所有重复 winner selection。

### 风险 6：原子 Journal 名义化

默认循环 Append 或 append 后 reducer 会恢复撕裂。接口不提供逐 fact commit，fault injection 覆盖每个边界。

### 风险 7：挂起被 timeout/invalid 偷偷取消

一旦 DecisionPoint 成为 winner，另一个 Actor 或 deadline 就不能因 Player 慢、断连或输出无效而越过它。Kernel 必须保持同一 invocation suspended；driver 可以内部重试或返回合法 fallback，但 Kernel 不维护 retry budget，也不无提交 re-Forecast。

### 风险 8：全量 Forecast 性能不足

先保留规范 reference；用 profiling 决定优化，并强制差分等价。不得以性能为理由恢复 subsystem 特殊时间法。

### 风险 9：crash 后重复调用产生另一条未来

LLM/Human adapter 若在返回 command 后、commit 前崩溃，重新调用可能给出不同答案或重复计费。稳定 InvocationId、durable continuation memo/idempotent driver 与 Journal OccurrenceId 去重共同保证正式运行的 restart/one-shot 等价；memo 只恢复 winner，不能演化成新的候选来源。

### 风险 10：无状态 contact 永久复发

只在 Journal 留 interaction receipt 而不改变 Spatial world，Forecast 会重复产生同一 contact。第一版强制投影 ConsumedContactKey；连续 prefix cursor 只能在差分证明等价后作为优化。

---

## 12. 完成定义

本计划完成时必须同时满足：

1. 003 与目标实现一致；
2. Kernel 只有统一 occurrence scheduler 与唯一 commit authority；
3. Journal 一级原子单位是 `CommittedTransition`；
4. 所有 facts 共享 occurrence 的 LogicalInstant；
5. external result-event ingress、internal-first gateway 和 fixed same-time phase 全部删除；
6. `Step → DecisionInvocation → DecisionProposal → PersistedDecisionProposal → ResumeDecision → CommittedTransition` 已完全取代 DecisionRequested-event/session/externalInputs；
7. Comparator 是严格全序，Candidate/transition 都绑定并验证纯模拟 ForecastBasis；
8. 策略挂起不 commit、不占 CausalOrdinal、不允许其他 Step；invalid/timeout/crash 不能无提交释放 winner；
9. Host 与内置 Player adapter 以稳定 InvocationId、persist-before-Resume CAS memo 和 committed `ResultStrategyStateRef/Hash` 恢复 command/Player 状态，Kernel World 与内置 Player 行为均满足 restart 与 one-shot 等价；
10. V1 runtime 来源闭包成立：除已胜出 Player DecisionPoint 外不调用任何 strategy/planner；Providence 仅 pure law、Admin runtime 禁用、Setup 仅 Genesis；
11. FirstBoard、Demo、Host、Protocol、Player.Llm 与 Spatial 已迁移；
12. 旧格式只读或通过明确迁移边界继续，不存在双运行时语义；
13. P0 验收矩阵全部通过；
14. full Forecast reference、audited Replay 与 crash recovery 证据完整。

最终 Kernel 应只回答一个问题：

> 基于当前完整世界，哪一个原子 occurrence 是唯一的下一原因？若它是 DecisionPoint，哪一个 Player 应在这个已胜出的世界前沿上返回命令？

回答被原子提交后，旧未来全部作废，世界从新的 `LogicalInstant` 继续 Lazy 演化。
