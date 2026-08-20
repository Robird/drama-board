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
→ 若 winner 需要外部解析，则在同一个未完成 Step 内调用外部通用 resolution boundary
→ boundary 返回完整 ValidatedOccurrenceResolution 或 AtomicTransitionPlan
→ Plan 一个全局原子 transition
→ 原子 Commit
→ 全量 re-Forecast
→ repeat
```

核心决定：

1. 删除 internal event / external input 的特殊调度路径；
2. V1 runtime candidate 全部由 World+Manifest 纯推导；需要外部解析的 winner 在同一个未完成 Step 内取得完整已校验结果，不形成第二轮候选、公开 request 或 durable continuation；V1 只有 `DecisionPointCandidate` 使用该机制；
3. 权威逻辑时刻为 `LogicalInstant = (ModelTime, CausalOrdinal)`；
4. 每个因果前沿只允许一个 `AtomicOccurrence` 成为下一原因；
5. 同一 `ModelTime` 上**已经提交**的 occurrence 以不同 `CausalOrdinal` 记录严格因果顺序；`CausalOrdinal` 不参与候选仲裁，不再拥有 simultaneous causality；
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
- 单个未完成 Step 内的 ephemeral winner/basis 与外部 `ValidatedOccurrenceResolution` 集成边界；
- 跨 Game、Spatial、Inventory、Faction 等域的原子 transition；
- FirstBoard、Demo、Protocol、Player.Llm 与 Spatial 的迁移；
- 旧 Journal/checkpoint 的版本门与迁移策略；
- determinism、liveness、capacity、recovery 和性能验收。

### 2.2 本计划不负责

- 具体战斗、经济、社交或 Providence 的玩法规则；
- Providence 的 LLM/planner suspension；如未来需要，必须另立 ADR，不复用 V1 的 DecisionPoint resolution 机制；
- 把 PRF rank 解释成物理时间；
- 让 Kernel、Forecast、Plan 或领域 reducer 解释 raw Player/LLM/Protocol 输出或产生外部副作用；V1 被选中的 `DecisionPointCandidate` 只允许未完成 Step 等待外部通用 resolution boundary，取得与校验细节均在 Kernel 外完成；
- 暴露或持久化 `OccurrenceResolutionRequest / ResumeOccurrence`、selected-winner token、pending answer 或 admission inbox；
- 定义 Player/Human/AI/LLM 的调用、重试、费用、私有 memory、driver store 或 fork/restart 等价语义；这些属于 Player/Host 组件，不进入 Kernel 目标；
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
| `DecisionPointCandidate` | V1 中表示“当前世界需要某个 Actor 的决定”的普通候选；它与其它 candidate 先统一仲裁，胜出后才在同一未完成 Step 中进入通用解析。 |
| `SelectedWinnerContext` | 单次未完成 Step 栈内的 ephemeral winner、冻结 basis 与拟议 LogicalInstant；不得公开、Journal 化、checkpoint 或跨 restart 恢复。 |
| `ValidatedOccurrenceResolution` | 独立 validation/domain boundary 返回的完整 canonical 解析结果；Kernel 不接收 raw Player/协议输出。它只在当前 Step 内存在，除非随成功 transition 进入审计。 |
| `ResolutionContractId` | 由领域 handler 声明的版本化解析契约标识；Host 组合的 resolver/validator 用它路由，Kernel 不解释具体协议。 |
| `OccurrenceResolutionAudit` | 成功 `CommittedTransition` 中对 validated resolution、contract、provenance 与 canonical payload/hash 的通用审计锚点。 |
| `AtomicTransitionPlan` | winner 对完整 `HostWorld` 的不可分割影响计划。 |
| `CommittedTransition` | 已原子写入 Journal 的 occurrence、结果与事实集合。 |
| `DomainFact` | transition 内的审计事实，不独占新的因果时刻。 |
| `LogicalInstant` | `(ModelTime, CausalOrdinal)`，一个 occurrence 的唯一逻辑时刻。 |
| `FactOrdinal` | 同一 transition 内事实的稳定编码顺序；不是时间。 |
| `ForecastBasis` | 一轮 Forecast 冻结的纯模拟基线：WorldSnapshot、WorldVersion、LogicalNow 与 Run Manifest 版本。 |
| `StableCandidateKey` | Host 分配、跨 Replay/Fork 稳定的候选身份。 |
| `ArbitrationRank` | 当前因果前沿中真正同位候选的确定性选择分数。 |
| `Provenance` | ordinary rule、V1 Player resolution 与 pure Providence law 的来源、依据和权限；不构成调度优先级。Setup 只属于 Genesis，V1 无 runtime Admin origin。 |

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
- Candidate 在 Forecast 和仲裁阶段没有 `CausalOrdinal`；不得按 candidate 创建、枚举、Source 注册或线程完成顺序预分配该值；
- `CausalOrdinal` 不得进入 candidate comparator、`ArbitrationRank` 或任何领域二次优先级；它只在 winner 已选出后被纯函数 `ProposeLogicalInstant` 提议，并在 atomic commit 成功后成为权威历史；
- 同一 transition 的全部 facts 共享同一 `LogicalInstant`；
- `FactOrdinal` 不得进入 World query、SelectedWinnerContext、Snapshot/Fork 地址或候选优先级；
- reducer 可以在 scratch 中按 FactOrdinal fold，但中间 prefix 永远不可观察；
- `WorldVersion = (LineageId, TransitionCount, HeadHash)` 只指向完整 transition boundary；
- Snapshot、Fork 与 checkpoint 不得指向 transition 内部；未完成 Step 的 ephemeral context 不是可寻址的世界边界。

这意味着不能只把旧 `Microstep` 重命名成 `CausalOrdinal`。旧模型给同一 batch 的每条 event 分配不同 Microstep；目标模型把整个 batch 视为一个不可分割原因。所谓“同一 ModelTime 由 CausalOrdinal 严格排序”，是对**提交后历史**的描述，不是先给一批 Forecast candidates 编号再据此选择 winner；选择 winner 的确定性伪随机职责属于下文 `ArbitrationRank`。

### 3.3 Candidate、Resolution、Fact 不得混同

```text
Candidate  = 尚未发生的预测数据
Resolution = 对已胜出 candidate 的解析数据，可被领域 Plan 接受或拒绝
Fact       = commit 后不可否认的过去事实
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

每轮 Forecast 只依赖当前完整 World 与冻结的运行规则。不存在另一个等待注入的 command/resolution 候选集合：

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
- V1 只有已胜出的 `DecisionPointCandidate` 可以在当前未完成 Step 内调用外部 resolution boundary；Host 如何从 Player 获得并校验结果不属于 Kernel；
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

Kernel 对最后一条没有例外：当 V1 `DecisionPointCandidate` 已成为唯一 winner 时，单个未完成 Step 只把 ephemeral selected context 交给 Kernel 外的通用 resolution boundary；Player/Human/AI/LLM 调用与 validation 发生在 Kernel 外。boundary 返回前不得 Plan、commit、推进 LogicalInstant 或启动另一 Step。Providence/Admin 不使用这条集成路径。

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

`CausalOrdinal`、`CommitOrdinal`、Forecast 枚举序号和 candidate 创建顺序均不在 comparator 中。前两者在 winner 产生后才有意义，后两者不是世界语义；把其中任一项用作排序键都会把实现细节偷渡成因果。

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

这里的 `CanonicalDue` 就是 candidate 所属的 ModelTime `T`。因此 PRF 在语义上等价于“用运行 seed、时刻 T 与稳定候选身份生成本轮排序 sub-key”，而不是使用 candidate 创建顺序。对于相同 `Due / EffectiveExactDue` 的 contenders，完整 PRF 输出按固定无符号字节序比较；不得先对 candidate 数量取模，也不得使用会引入低位相关性或取模偏差的临时 LCG 代替规范 PRF。

要求：

- PRF 算法、常量、canonical codec 与规则版本进入 Run Manifest；
- Fork 默认继承 seed，使相同 World 前缀与 Manifest 继续产生相同 winner；
- rank 不使用 `.NET GetHashCode`、对象地址或枚举顺序；
- 任何 resolution id 或 payload 不得进入已经选定的 winner rank；
- Host 根据 Decision、Actor、source、occurrence generation 分配稳定 CandidateId；
- rank 相同用完整 stable key 兜底；
- Replay 直接重放 committed winner；audited replay 才重新计算 PRF。

PRF 必须在冻结的 seed/ModelTime/host-assigned stable-key 测试语料上表现为近似均匀的 tie-break；这里的“概率公平”是对 seed、ModelTime 与候选身份样本空间而言。单个 Run 内结果仍是完全确定的，不能把 wall-clock RNG 或每次调用重抽解释为公平。

PRF rank 只是当前因果前沿的选择分数，不是可持久化的小数时间。新 candidate 即使 rank 更小，也只能在产生它的 occurrence 之后参加下一轮。不要把 `CausalOrdinal` 混入 PRF 来让同一 ModelTime 的每次 re-Forecast 重新洗牌；持续存在的同一 candidate 应保持 rank，已经完成的新一代 occurrence 则通过 `Generation` 进入 `StableCandidateKey` 而获得新的 rank。

### 4.4 规范循环

```text
Step(world, cursor, resolutionBoundary):
    require no other Step/commit is in flight

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

    resolutionSpec = ResolveHandler(winner.HandlerId)
        .DescribeResolution(basis.WorldSnapshot, winner)
    if resolutionSpec.RequiresValidatedResolution:
        selected = CreateEphemeralSelectedWinnerContext(
            basis,
            winner,
            proposedInstant,
            resolutionSpec.ContractId,
            resolutionSpec.ContextDigest)
        resolved = await resolutionBoundary.ResolveSelectedWinner(selected)
        // boundary 内部取得 raw output 并由独立 validation/domain 层校验；
        // Kernel 从不观察 raw output，selected 也不得逃逸为 durable/public request。
        ValidateResolvedOccurrenceBinding(selected, resolved)
        plan = resolved.AtomicTransitionPlan
            ?? ResolveHandler(winner.HandlerId).Plan(
                basis.WorldSnapshot,
                winner,
                resolved.ValidatedOccurrenceResolution,
                proposedInstant)
        plan = AttachResolutionAudit(plan, resolved)
        return CommitWinner(plan, basis, proposedInstant)

    plan = ResolveHandler(winner.HandlerId)
        .Plan(basis.WorldSnapshot, winner, NoDeferredResolution, proposedInstant)
    return CommitWinner(plan, basis, proposedInstant)

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

这是 breaking API：单次 `Step` 要么原子返回一个 `CommittedTransition`，要么以零 World/Journal 变化失败/取消；不再返回 `DecisionRequested`、`OccurrenceResolutionRequest` 或任何可供以后 `Resume` 的 session。需要解析的 winner 只存在于该次未完成调用栈中；boundary 返回前，`LogicalInstant`、WorldVersion 与 Journal head 均未推进，`ProposedLogicalInstant` 也只是临时计算值。

同一 lineage 同时至多有一个未完成 Step；它等待 boundary 时再次 Step、处理另一 candidate、提交管理命令或推进时间都属于非法调用。取得 resolution 所耗的 wall-clock 时间为零 ModelTime。boundary 的 timeout、exception、取消或进程 crash 使整次 Step 零提交；随后重新调用 Step 会从 committed World 全量 re-Forecast，先前 winner 和 Player 选择没有权威性，也不保证相同。Kernel 不定义 retry、memo、exactly-once、fallback 或 restart 等价。

Audited Replay 按 transition 记录的 ForecastBasis 重建 contender set，并使用已记录的 canonical `ValidatedOccurrenceResolution` 重跑纯 Plan；不重新调用 resolver、validator 或 Player。

任何**成功提交**的 winner 都必须被消费、改变 Generation 或产生显式 rejection。失败/取消的 Step 没有 occurrence，可以在下次调用重新预测同一 winner。空 committed transition、相同 candidate 的已提交无进展复发、无限同 ModelTime occurrence chain 都是确定性失败，并受规则版本化预算限制。

---

## 5. 单 Step Resolution 边界、Host 组合与 V1 Player 映射

### 5.1 Kernel 只持有 ephemeral selected context

Kernel 不区分 internal/external、AI/Human 或 Player/Providence 的调度优先级，也不定义或调用 `IPlayerStrategy`。winner 选出后，当前 Step 栈内可以临时形成：

```text
SelectedWinnerContext          // ephemeral; scoped to this Step call
    ForecastBasis / FrozenWorldSnapshot
    WinnerStableCandidateKey / Generation / HandlerId
    ProposedLogicalInstant
    ResolutionContractId / ContractVersion
    OpaqueResolutionContext / ResolutionContextDigest

ResolvedOccurrence             // complete result returned within the same call
    ValidatedOccurrenceResolution?
    AtomicTransitionPlan?
    CanonicalPayloadHash / ValidationAttestation / Provenance
```

这些值不是公开调度 API、World fact、Journal record、checkpoint 或 durable inbox；不得生成可跨调用 `Resume` 的 RequestId。Kernel 只检查 resolved result 与当前 ephemeral basis/winner/contract 的绑定完整性，不解释 raw protocol、不执行 capability policy，也不知道结果来自 Human、AI、LLM 还是脚本。只有 commit 成功后，resolution/plan 的审计数据才成为历史。

### 5.2 责任边界与程序集

| 组件 | 责任 | 明确禁止 |
|---|---|---|
| Kernel | Forecast、仲裁 winner、在单 Step 内保留 ephemeral context、校验 complete plan/result 的当前调用绑定、scratch validate、原子 commit | 引用 Host/Protocol/Player；取得或解释 raw output；定义 retry/timeout/cost/memory；公开 pending/resume API |
| Decision.Validation | 对 V1 Player raw decision 做 envelope、schema、capability、correlation、size 与 canonicalization 校验，返回 complete validated resolution | Forecast、选 winner、改 World、做 world-dependent domain legality |
| Domain Handler | 在该 Step 冻结的 WorldSnapshot 上 pure Plan；处理 world-dependent accepted/rejected 结果 | I/O、读取墙钟、换 snapshot、直接 commit |
| Player abstractions/resolver | 定义 Player 交互抽象并取得 raw decision；实现细节可为 Human、script 或 LLM | 被 Kernel 引用；直接改 World/Journal；把回答变成新 candidate |
| Host | 在一次 Step 调用链中组合 Kernel、Decision.Validation、Player/resolver 与 Protocol，向 generic resolution boundary 返回 complete result/plan | 暴露 selected token 为 durable request；绕过 validation；在 pending 时并发 Step/commit |

目标程序集依赖为：

```text
Kernel                         // 不引用 Host / Protocol / Player / Player.Llm
Protocol                       // wire/domain DTO
Decision.Validation            // 当前只引用 Protocol；未来领域 validator 也不得反向依赖 Kernel 实现
Player                         // 引用 Protocol；当前 RandomPlayerDriver 单向复用 Kernel deterministic-random utility；不引用 Host
Player.Llm                     // 引用 Player abstractions + Protocol，不引用 Host
Host                           // 组合 Kernel + Decision.Validation + Player + Protocol
FirstBoard                     // 领域 source/handler 与 pure Plan
FirstBoard.Demo                // composition/UI；经 Host 驱动
```

这里区分 direct edge 与 transitive closure：当前 `Player → Kernel` 只因 `RandomPlayerDriver` 复用 `DeterministicRandom`，不会形成 `Kernel → Player` 的反向认知；`Player.Llm` 的直接引用仍只有 Player + Protocol。若未来希望 Player 连 Kernel utility 也不传递依赖，应把 deterministic-random primitive 下沉为更底层的通用程序集，不能复制算法或让 Kernel 反向引用 Player。

### 5.3 V1 Player 是 Host 侧的一种 resolver

V1 只有 `DecisionPointCandidate` 声明 deferred `ResolutionContractId`。它与 ticket deadline、碰撞、到达、天气、另一 Actor 的 DecisionPoint 使用完全相同的 comparator。成为唯一 winner 后，同一未完成 Step 经 Host 组合的 boundary 构造领域 observation、调用 Player resolver、取得 raw decision，再由 `Decision.Validation` 形成 complete resolution；Kernel 始终不含 `PlayerId`、Observation、WorldCommand schema 或 strategy identity 的专用 API。

若 Alice 与 Bob 都在 T 拥有 DecisionPoint，PRF 先选择其中一个，例如 Alice。本次 Step 只解析 Alice；Alice 的 complete result 在冻结 snapshot 上 Plan 并原子提交后，下一次 Step 才 full re-Forecast Bob。不存在同时收集 A/B 回答或按网络返回顺序决定世界的阶段。

票据到期同理：

- 若 `ExpireTicket` 先赢，本次 Step 先提交到期；下一次 Step 中 Actor 才可能产生/胜出 DecisionPoint，并看到“晚了一步”的新世界；
- 若 `DecisionPoint` 先赢，同一 Step 等待 complete `UseTicketAndBoard` resolution；领域 Plan 可把 `TicketConsumed + TraversalStarted` 作为该 winner 的同一 atomic transition 提交。

竞争发生在 `DecisionPointCandidate` 与其它 candidate 之间，而不是在回答后再造一轮 command-vs-deadline 仲裁。

### 5.4 校验、领域拒绝与失败语义

两层 invalid 必须分开：

- envelope/schema/capability/correlation 等结构错误由 `Decision.Validation` 拒绝，raw output 不得进入 Kernel；resolver/Host 可在当前调用内重试、返回失败或取消，Kernel 不规定其 policy；
- 结构合法但 world-dependent 领域前置条件失败，由 pure domain `Plan` 生成明确 rejection facts，并消费该 winner/generation，作为非空 atomic transition 提交。

等待、重试、timeout、exception 或取消都不改变 ModelTime。当前 Step 尚未 commit 时，World、Journal、WorldVersion 与 LogicalInstant 必须零变化；并发 Step/commit 非法。若该 Step 被放弃或进程崩溃，其 ephemeral selected context 直接消失；restart 从 committed World re-Forecast，Player 可以作出不同选择。这不是 causality violation，因为先前结果从未成为 occurrence。Kernel 不提供 exactly-once、memo、continuation、resume 或 Player crash/fork 等价保证。

### 5.5 Provenance、来源闭包与外部副作用

成功 transition 的 `OccurrenceResolutionAudit` 至少锚定 basis/winner/contract、canonical resolution/plan、validator identity/version、validation attestation 与领域 provenance。canonical resolution 必须以内联 canonical bytes 或由 transition hash 覆盖的 durable content-addressed ref 保存，不能只留无法恢复内容的 hash；否则 audited Replay 无法重跑 Plan。它们只服务 committed history 的审计，不参与时间或 PRF。V1 runtime candidates 仍全部来自 World+Manifest：Player answer 只是当前已选 winner 的 call-local resolution，不是 ingress candidate；Providence 只能是 pure law；runtime/live Admin 禁用；Setup 只构造 Genesis；预编排脚本冻结进 World/Manifest。

邮件、网络发送、Godot 通知等不进入 Plan。Committed transition 可产生 outbox fact，Host 在 commit 后幂等执行；resolver/validator 不得直接修改 World 或 Journal。

---

## 6. Journal、Replay、Fork 与恢复

### 6.1 Journal 目标接口

Atelia 适配器须把一个完整 `CommittedTransition` 编码为一个 EventJournal Event / 一个 RBF EventFrame，并以 expected-head ref CAS 作为 active branch 的发布点。serialized transition 超过 `EventJournalOptions.MaxLogicalPayloadLength` 时整批失败；不拆帧、不引入 Begin/Part/End。所有 sink 遵循 single-driver 契约，append 期间禁止并发读写事件视图。

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

普通 Replay 只原子 fold `Facts[]`；不 Forecast、不重新抽 PRF、不调用 resolver、validation 或 Player，也不把 resolution payload 当作第二条状态写入路径。canonical validated resolution 与领域 result 只是 CommittedTransition 中可审计的 cause/result payload。

Audited Replay 在每个 transition boundary 重算 candidates/ranks，验证记录的 candidate 确实是 winner；对含 resolution 的 transition，它读取已提交的 canonical `ValidatedOccurrenceResolution` 重跑 pure Plan，并逐字节比较重建的 Facts、领域 result 与 canonical plan hash，但仍不重新取得或重新校验 resolution。

crash 发生在 resolution/plan 返回后、append 前时，该 occurrence 从未发生；Kernel 只从 committed Journal 恢复 World 并重新 Forecast。若 deterministic world/manifest 未变，winner 仍由仲裁规则重算，但 Player/resolver 可给出不同结果；不存在恢复旧 selected context 的义务。append 成功后的 crash 仍以 Journal 为 authority 完整 Replay committed transition。

### 6.3 Fork

Fork 只发生在 transition boundary，并继承：

- World、WorldVersion、LogicalInstant、Journal prefix/hash；
- RunArbitrationSeed 与 OrderingRulesVersion；
- source generation/watermark/pending world state。

Forecast cache 可丢弃并全量重建。Fork 只能发生在 committed transition boundary；未完成 Step 不是可 Fork 状态，既不复制 ephemeral selected context，也不承担其 continuation 语义。Player/resolver 私有状态是否随 Fork 继承不属于 Kernel；Host 若需要保证相同 Player 未来，必须另行定义其组件契约。

---

## 7. 当前代码冲突与处置

| 当前位置 | 冲突 | 目标处置 |
|---|---|---|
| `Kernel/Simulation/SimulationLoop.Run(externalInputs)` | external 在 Forecast 前直接提交 | 删除参数与路径；改为单个 `Step` 选择 winner、call-local 取得 complete resolution/plan 并提交。 |
| `Kernel/Scheduling/ForecastQueue` | 同 Due 固定按 SourceId/CandidateId | 改为 Due → KnownExactDue → PRF rank → stable key。 |
| `Kernel/Journal/EventCause` | `ResolveBatch / ExternalInput` 是调度分类 | 改为统一 occurrence cause + provenance。 |
| `LogicalTimestamp / Microstep` | batch 内每 fact 占一个时间 | 改为 transition 级 LogicalInstant + FactOrdinal。 |
| `SimulationLoop.CommitAndApply` | Journal append 后才 reducer | 改为 scratch-fold/validate → atomic append → install。 |
| `IJournalSink.AppendBatch` | 只能原子发布离散 DomainEvent batch，缺少 transition header、FactOrdinal、hash 与 WorldVersion 边界 | 以 `AppendTransition` 和一级 `CommittedTransition` envelope 取代。 |
| Kernel decision predicate/stop reason | 先提交 `DecisionRequested`，再由 Host 特殊停机 | 删除；单个未完成 `Step` 在 call-local selected context 上等待 complete resolution/plan，并直接 commit 或零提交失败。 |
| `PlayerDecisionSession` | answer 先翻译成 external event，再形成 PendingAction | 删除；Host 在同一 Step 调用链内组合 Player resolver 与 Decision.Validation，不保留 session/request/resume 或“回答成为第二轮候选”的调度器。 |
| `FirstBoard.DecisionSchedulingSystem` | 一次产生批量 DecisionRequested facts，并用 internal-first barrier 推迟 Player | 改为每个 eligible Actor 的 `DecisionPointCandidate`，声明 versioned resolution contract；全局 arbiter 一次只选择一个 winner。 |
| `FirstBoard.ActionResolutionSystem / PendingAction` | Player command 先写成世界 pending state、下一轮才 resolve | 删除两阶段；FirstBoard handler 从 validated domain payload 在冻结 FirstBoardWorld 上直接 pure Plan。 |
| `FirstBoard.Demo` | 展示旧 Microstep/external cause，并通过 session 注入回答 | 跟随 LogicalInstant/FactOrdinal；UI/Host 在当前 Step 内取得 raw output，经 Decision.Validation 后返回 complete result。 |
| `Host / Protocol / Decision.Validation` | `DecisionRequest → PlayerDecision → externalInputs` 且协议校验与 Kernel/Host 混杂 | Host 在单次 Step 调用链内组合 generic boundary 与独立 validation assembly；Protocol 提供 wire/domain DTO，validation 负责结构/schema/capability/correlation，domain Plan 负责 world-dependent legality。 |
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
- 冻结 EffectiveExactDue codec、严格全序 comparator、PRF、seed、CandidateKey、纯模拟 ForecastBasis、call-local resolution 与未完成 Step 不可 Fork 规则；
- 决定旧 Journal 是只读、离线迁移还是放弃；不得模糊兼容。

退出条件：没有未登记的 commit path、时间字段、resolution ingress 或 subsystem gateway。

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

- `CommittedTransition` 一级 envelope，取代 `DomainEventBatch`；
- `AppendTransition(expectedHead, transition)`，InMemory 与 Atelia 均以一个 transition 为发布单位；
- Atelia 保持单 EventJournal Event / 单 RBF Frame，不引入 Begin/Part/End 或多帧 batch；
- pure batch reducer/scratch-fold 与 final invariant/hash validation；
- FactOrdinal 与 transition-boundary Snapshot/Fork；
- crash-at-before/during/after-append fault injection。

完成后，Journal、World 与事件订阅均不能暴露 reducer prefix。

### 波次 3：统一 occurrence scheduler

实施：

- `IOccurrenceSource` 与 `IOccurrenceHandler.Plan`；
- full Forecast reference loop；
- Due/KnownExactDue/PRF/fallback comparator；
- 每轮唯一 winner；
- 每次 winner 后全量 re-Forecast；
- no-op、same-ModelTime budget、capacity 防线；
- candidate/rank/winner debug trace；
- `Step` 的 `Committed / FailedWithoutCommit / Exhausted` 判别结果；
- `ProposeLogicalInstant` 只计算、不占用 ordinal；只有 atomic Commit 成功才推进时间；
- in-flight Step single-flight guard，移除旧 Kernel 的 decision-event predicate。

先以 timer、reroute、collision、ticket、loot contention toy models 锁定语义。为保证每波可运行，旧 ingress 此时只允许存在于显式标记、不可进入新 lineage 的 migration adapter；它不得成为第二条 live scheduler path。

### 波次 4：单 Step Occurrence resolution integration

实施：

- call-local `SelectedWinnerContext / ValidatedOccurrenceResolution / ResolutionContractId` contracts；
- 单次 `Step` 内 selection → await generic boundary → Plan → Commit；不返回 request，不提供 Resume；
- selected context 只绑定当前栈帧的 winner、冻结 WorldSnapshot/basis、contract 与 ProposedLogicalInstant，不得持久化或跨 restart；
- Kernel 只验证 complete result/plan 的当前调用绑定，不调用 Player、不解析 Protocol、不做 schema/capability validation；
- in-flight single-flight guard：boundary 返回前其他 Step/commit 非法；失败/取消/crash 零提交，restart 重新 Forecast；
- 独立 `Decision.Validation` assembly，负责 V1 envelope/schema/capability/correlation/canonicalization；
- pure domain Plan 对结构合法 resolution 处理 world-dependent accepted/rejected；
- CommittedTransition 锚定 generic resolution audit；
- crash-before-append 不恢复旧选择、after-append 由 Journal 恢复；audited Replay 使用已记录 resolution 重跑 pure Plan；
- generic outbox。

本波结束后，Kernel 项目仍不引用 Host/Protocol/Player/validation 实现；新 lineage 不暴露 pending/request/resume API。resolution 只服务当前 Step 的 winner，不得成为新的 candidate。尚未迁移的 FirstBoard 只能通过波次 3 所述隔离 migration adapter 保持编译，不能进入新 lineage。

### 波次 5：跨域 composite commands

优先迁移并测试：

- 消耗 ticket + BeginTraversal；
- 支付资源 + 创建/修改 Place；
- 战斗结算 + Actor remove/move；
- 纯 Providence law 的原因 + 对应领域/空间 facts；
- inventory transfer + ownership change。

每项均须一个 handler、一个 LogicalInstant、一个 atomic transition。失败时全域零提交。

### 波次 6：FirstBoard、Protocol、Player.Llm、Demo

实施：

- FirstBoard rules/actions 迁成 source/handler；
- 把 `DecisionSchedulingSystem` 改成逐 Actor `DecisionPointCandidate` source并声明 resolution contract，删除 internal-first barrier；
- 删除 `PendingAction → ActionResolutionSystem` 两阶段，validated domain payload 在冻结 FirstBoardWorld 上直接 Plan；
- 删除 Object contention 二次 RNG/round；
- 两个 eligible Actor 的 DecisionPoint 由 Kernel arbiter 排序；当前 Step 只解析 winner，另一个在 commit 后重新 Forecast；
- Protocol 定义 V1 Player wire/domain DTO；独立 validator 将 raw answer 变为 generic validated resolution，并把 Microstep 改为 CausalOrdinal；
- Player.Llm 的 prompt/observation/correlation 由 Host/Protocol adapter 绑定当前 ephemeral selected context 与冻结 WorldVersion；
- Host 在同一 Step 调用中路由 selected context → Player resolver → Decision.Validation → complete result/plan；
- Demo UI 在未完成 Step 中取得回答并校验；trace 只展示 committed LogicalInstant、FactOrdinal 与 provenance，不把 pending choice 伪装成历史。

本波结束时删除 live `Run(externalInputs)`、`DecisionRequested`-event/session 与 `PendingAction` ingress；所有正式调用方已经迁移到 generic API，且程序集依赖图满足 §5.2。

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
- WorldVersion 与 committed resolution audit envelope；
- committed resolution audit codec 与 tooling；不增加 pending request codec。

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
- Journal bytes、transition size、recovery latency、in-flight resolution wall-clock 与 validation 指标；
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

存在 open DecisionRequest、PendingAction 或只在旧 Host session 内存中的 Player answer 时，不直接续跑；只迁移到最后一个完整旧 batch boundary，再由新 Kernel 重新 Forecast `DecisionPointCandidate`。旧 answer 不得伪装成当前 Step 的 `ValidatedOccurrenceResolution` 绕过重新仲裁与 validation。

---

## 10. P0 验收矩阵

| ID | 范围 | 硬断言 |
|---|---|---|
| ORD-1 | LogicalInstant | 任意两个 committed occurrences 的 LogicalInstant 不同；同 ModelTime 的 CausalOrdinal 从 0 连续。 |
| ORD-2 | Fact boundary | 一个多事实 transition 只增加一次 CausalOrdinal；Facts 共享 LogicalInstant，FactOrdinal 连续且 prefix 不可观察。 |
| ORD-3 | Reordering | system 注册、candidate 枚举、并行 Forecast 完成顺序任意置换，Journal bytes 完全相同。 |
| ORD-4 | Exact time | contact T1.1、deadline T1.5、contact T1.9 必须按已知精确时间提交，PRF 不得颠倒。 |
| ORD-5 | Strict total order | Comparator 通过 antisymmetry/transitivity/totality 性质测试；KnownExactDue 有/无混排仍有唯一稳定 winner。 |
| ARB-1 | PRF | 相同 manifest/state 产生相同 winner；不同 seed 可覆盖“票先到期”与“DecisionPoint 先得到用票机会”；碰撞 fallback 稳定。固定大样本 seed/ModelTime/stable-key corpus 的 n-way tie winner 频率落在冻结容差内，且 candidate 枚举置换不改变结果；测试确定性执行，不依赖 wall-clock 随机。 |
| ARB-2 | Identity | resolution 在 winner 选定后才存在，不能影响 candidate identity/rank；stable identity 可 Replay/Fork。 |
| CAU-1 | Dynamic frontier | 新 candidate 即使 rank 更小也不能倒插历史，只能参加下一轮。 |
| BAS-1 | ForecastBasis | ForecastBasis 只含冻结 WorldSnapshot/WorldVersion/LogicalNow/Manifest；wall-clock answer 或未提交 command 不能改写 contender set。 |
| BAS-2 | Source closure | runtime candidate 全部来自 World+Manifest pure source；V1 仅已胜出的 DecisionPoint 可在当前 Step 内调用 generic resolution boundary，resolution 不形成 candidate 或 durable ingress。Providence planner、runtime/live Admin、runtime Setup 与未冻结脚本均被拒绝。 |
| UNI-1 | Unified path | 代码守卫确认不存在 `externalInputs`、`FromExternalInput`、`CauseKind.ExternalInput` 或 direct result-event ingress。 |
| ATM-1 | Cross-domain | TicketConsumed + TraversalStarted 全成或全败；Game/Spatial 任一失败时 Journal、World、LogicalInstant 均零变化，整个 Step 未提交。 |
| ATM-2 | Persistence | plan/reducer/append 任一 fault 均不暴露部分 transition；append 成功后 crash 可完整 Replay。 |
| RES-1 | Breaking API | 单次 Step 内完成 selection → resolution/plan → commit；没有公开/persisted request、Resume、DecisionRequested-event/session 或 externalInputs。 |
| RES-2 | Kernel boundary | Kernel 不引用 Host/Protocol/Player/Player.Llm/validation 实现，不定义或调用 IPlayerStrategy，也不知道 Human/AI/LLM、raw retry、memory、cost；只验证 complete result/plan 与当前 call-local context 的绑定。 |
| RES-3 | In-flight atomicity | boundary waiting、invalid、timeout、exception、取消与 crash-before-append 都是零 commit/零 ordinal；同 lineage 其他 Step/commit 在调用未结束前非法。 |
| RES-4 | Ephemeral lifecycle | selected winner/basis 不进入 Journal/checkpoint/public DTO；失败或 crash 后直接丢弃，restart 全量 re-Forecast，允许 Player 给出不同结果且无 exactly-once 保证。 |
| VAL-1 | Independent validation | envelope/schema/capability/correlation invalid 不能形成 ValidatedOccurrenceResolution 或进入 Kernel；validation assembly 不 Forecast、不改 World。 |
| DOM-1 | Domain result | 结构合法但 world-dependent precondition fail 由冻结 snapshot 上的 pure Plan 提交明确 rejection，并消费 winner/generation；validation 不越权做领域裁决。 |
| DEC-1 | A/B order | 两个同 T DecisionPoint 的当前 Step 只解析全局 winner；其 resolution 提交后才重新 Forecast 另一 Actor，Player/网络返回顺序不参与候选仲裁。 |
| DEC-2 | Ticket race | ExpireTicket 胜出时直接提交；DecisionPoint 胜出时在同一 Step、同一 snapshot 上取得 validated UseTicketAndBoard，并可原子提交扣票+上船。 |
| RPL-1 | Replay | 普通 Replay 不 Forecast、不调用 resolver/validation/Player，重建相同 World/LogicalInstant/head hash。 |
| RPL-2 | Audited replay | 用纯模拟 ForecastBasis 重建 contenders，重算 winner/rank；含 resolution 的 transition 使用已记录 canonical validated resolution 重跑 Plan；boundary 直接返回 plan 时验证已记录 canonical plan。两者都逐字节匹配 Facts/domain result/plan hash，不重新取得或校验 raw output。 |
| VER-1 | Version/hash | 空 lineage、连续提交与 Fork 均满足两阶段 hash 递推，无 HeadHash 自引用且 codec bytes 稳定。 |
| FORK-1 | Fork | Fork 只允许 committed boundary；未完成 Step/ephemeral selected context 不可复制。相同 World 前缀/seed/规则得到相同下一 winner；Player/resolver 私有状态与未来行为不属于 Kernel 保证。 |
| PRV-1 | Provenance | 每个 Providence transition 可追溯到 World+Manifest 中的 pure law identity/parameters；不存在 Providence/Admin strategy transition。Setup 只存在于 Genesis，legacy origin 只用于旧 Journal 迁移审计。 |
| LIV-1 | Progress | selected candidate 返回空 plan 立即失败；重复 no-op、Generation 不变与无限同 T 链被确定性防线捕获。 |
| SPL-1 | Spatial | 不存在 SpatialMoment/fixed phase；单 contact 不吞掉同 T 其它 contact；contact 防重复且 Replay 相等。 |
| SPL-2 | Contact authority | ConsumedContactKey 进入 Spatial world、按 generation 清理并被 Fork 继承；Journal receipt 不能替代 Forecast 可见状态。 |
| FB-1 | FirstBoard | 双方都 eligible 时，只为 DecisionPoint winner 取得并校验 resolution；其 Take 提交后另一 Actor 在新世界重新获得 Observation/affordance，无 PendingAction 和第二套 contention RNG。 |
| PERF-1 | Reference | full Forecast 只读取纯模拟 ForecastBasis；任何优化与 reference 在随机/压力 corpus 上逐字节等价。 |

P0 全部通过、旧调度路径完全删除，才算重构完成。

---

## 11. 主要风险与应对

### 风险 1：只是重命名 Microstep

如果 batch 内 facts 继续各占 CausalOrdinal，就仍可观察撕裂中间态。以 transition envelope、FactOrdinal 与 snapshot boundary tests 阻止。

### 风险 2：把 resolution 错当成第二轮 candidate

如果 resolution 返回后再次参与 Forecast/PRF，就恢复了旧式 input-injection 双轨。它只能在当前未完成 Step 中为已选 winner 的冻结 snapshot 生成 plan。

### 风险 3：策略返回前提前占用时间

若需要解析的 winner 一胜出就推进 CausalOrdinal 或先写 `DecisionRequested`，等待与 crash 会留下无结果的世界历史。只能 `ProposeLogicalInstant`；atomic commit 成功时才真正占用 ordinal。

### 风险 4：KnownExactDue 仍被 ceil 隐藏

Spatial T1.9 可能被排在 T1.1 前；pairwise “可比较”还会破坏传递性。冻结全局 EffectiveExactDue codec 与严格全序性质测试，比较精确证据后才 PRF。

### 风险 5：领域继续二次仲裁

FirstBoard contention、Spatial fixed phase 或 Providence 本地 RNG 可能推翻 Kernel winner。代码搜索和验收要求删除所有重复 winner selection。

### 风险 6：原子 Journal 名义化

若只把现有 batch envelope 改名，仍会缺少 transition header、FactOrdinal、hash、scratch-fold 与 WorldVersion 边界。完整 `CommittedTransition` 必须继续使用单 Event/单 RBF Frame 和 ref 发布点，不得退回逐 fact append 或引入 Begin/End 多帧协议。

### 风险 7：未完成 Step 泄漏半状态

resolution boundary 等待期间若开放另一个 Step、checkpoint 或 commit，就会把 call-local 选择偷渡成可观察世界。以 lineage single-flight guard 阻止并发；失败/取消只能让整次 Step 零提交结束，下一次调用从 committed World 重新 Forecast，不恢复旧 selected context。

### 风险 8：全量 Forecast 性能不足

先保留规范 reference；用 profiling 决定优化，并强制差分等价。不得以性能为理由恢复 subsystem 特殊时间法。

### 风险 9：Kernel 边界重新耦合 Player/Protocol

若 Kernel 为便利直接构造 Observation、调用 `IPlayerStrategy`、解析 wire DTO 或维护 driver memo/retry，它会重新拥有 external-input 特殊路径。程序集依赖测试、call-local complete-result contract 与独立 Decision.Validation assembly 必须同时阻止源码和语义反向依赖。

### 风险 10：把结构校验与领域裁决混在一起

独立 validator 若读取可变 World 裁决票据/资源，会与冻结 Plan 形成第二个权威；反之 Kernel 若接受 raw payload，则 capability/correlation 可被绕过。validator 只产生结构完整的 canonical resolution，world-dependent accepted/rejected 只由同 snapshot 的 pure Plan 决定。

### 风险 11：无状态 contact 永久复发

只在 Journal 留 interaction receipt 而不改变 Spatial world，Forecast 会重复产生同一 contact。第一版强制投影 ConsumedContactKey；连续 prefix cursor 只能在差分证明等价后作为优化。

---

## 12. 完成定义

本计划完成时必须同时满足：

1. 003 与目标实现一致；
2. Kernel 只有统一 occurrence scheduler 与唯一 commit authority；
3. Journal 一级原子单位是 `CommittedTransition`；
4. 所有 facts 共享 occurrence 的 LogicalInstant；
5. external result-event ingress、internal-first gateway 和 fixed same-time phase 全部删除；
6. 单次 `Step: Select → await external complete resolution/plan → atomic Commit` 已完全取代 DecisionRequested-event/session/externalInputs，且没有 public/persisted request 或 Resume API；
7. Comparator 是严格全序，Candidate/transition 都绑定并验证纯模拟 ForecastBasis；
8. resolution 等待不 commit、不占 CausalOrdinal、不允许其他 Step；失败/取消/crash-before-append 丢弃整个 call-local context，restart 重新 Forecast；
9. Kernel 的新 Step/resolution API 只接受 generic complete result/plan，不取得或解释 raw output，也不定义 Player driver/memo/retry/memory/cost；独立 validation 负责结构/schema/capability/correlation，领域 Plan 负责 world-dependent accepted/rejected；
10. V1 runtime 来源闭包成立：只有已胜出 DecisionPoint 在当前 Step 内使用 generic resolution boundary；Providence 仅 pure law、Admin runtime 禁用、Setup 仅 Genesis；
11. FirstBoard、Demo、Host、Protocol、Player.Llm 与 Spatial 已迁移；
12. 旧格式只读或通过明确迁移边界继续，不存在双运行时语义；
13. P0 验收矩阵全部通过；
14. full Forecast reference、audited Replay 与 crash recovery 证据完整。

最终 Kernel 应只回答一个问题：

> 基于当前完整世界，哪一个原子 occurrence 是唯一的下一原因？若它需要解析，当前这一次未完成 Step 能否取得完整 validated resolution/plan 并原子提交？

回答被原子提交后，旧未来全部作废，世界从新的 `LogicalInstant` 继续 Lazy 演化。
