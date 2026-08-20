# Design Note 003：Forecast, Collapse, Commit —— 统一原子 Occurrence 的 Simulation Kernel

**状态：目标方案与重构计划（替代旧 internal/external 调度语义）**

**初稿日期：2026-08-09**

**本次修订：2026-08-21**

**定位：定义 DramaBoard 的时间、联合预测、确定性仲裁、原子提交、Decision 与可回放 Simulation Kernel。**

---

## 0. 本次重构决定

Kernel 采用以下唯一核心循环：

```text
可 Lazy 求值的连续演化
    ↓
所有 OccurrenceSource 联合 Forecast
    ↓
选出全局唯一最早的 AtomicOccurrenceCandidate
    ↓
Plan 并原子 Commit 一个 AtomicTransition
    ↓
丢弃旧 Forecast，全量重新 Forecast
    ↓
循环
```

下列旧语义被明确废弃：

- `internal event` 与 `external input` 使用不同提交路径；
- external input 在全系统 Forecast 之前直接写入世界；
- scheduled、external、direct consequence、derived event 按固定 microstep phase 排序；
- 一次选择并批量 Resolve 多个“同时事件”；
- Spatial 或其他单一子系统自行判断世界是否已经达到同刻屏障；
- Player answer 由 Host 直接翻译为已经成功的 DomainEvent。

取而代之的是：

> **小球碰撞、票据到期、Player/AI 的行动机会、天气变化以及冻结于 World/Manifest 的 Providence/预编排法则，在调度语义上没有类别差异。它们都先成为候选，只能在赢得一次全局仲裁后，以一个原子 transition 改变世界。**

需要 winner 之外的数据才能完成的 occurrence 由同一次尚未完成的 Kernel Step 调用领域中立 resolver；resolver 返回与 basis/winner/ProposedLogicalInstant 绑定的完整 `ValidatedOccurrenceResolution`，Kernel 再让原 handler 在同一冻结 WorldSnapshot 上 Plan 和原子提交。等待 resolver 消耗零 ModelTime，期间 World 与 Journal 均不改变。DecisionPoint 只是一个领域用例，Kernel 不认识 Player、Human、AI、LLM 或其进程状态。

来源、权限与干预理由仍然保留，但它们属于 provenance 与 authorization，不构成时间优先级。

本方案是 Kernel 的目标语义。`src/FirstBoard`、`src/FirstBoard.Demo`、`src/Spatial` 中与其冲突的现有流程和 API 应迁移或删除，不得反向迫使 Kernel 保留两套时间法则。

---

## 1. 核心直觉：世界由 Flow 与 Occurrence 交替组成

传统实时游戏通常不断执行固定 Tick：

```text
Tick → Tick → Tick → Tick → ...
```

DramaBoard 更适合把世界理解为两类成分：

1. **Continuous Lazy Evolution（Flow）**：只要规则参数不变，就能由锚点和时间按需求值的连续过程；
2. **Atomic Occurrence（Jump）**：使一条或多条 Flow 失效、产生新信息或改变行动条件的瞬时原子影响。

例如旅行只需保存：

```text
Traversal
    Passage
    AnchorOffset
    AnchorTime
    SignedSpeed
    Generation
```

任意时刻的位置可按需求值。只有抵达、相遇、超车、调速、掉头等会破坏现有运动方程的 occurrence 才需要进入 Journal。

同理：

- 饥饿可以是锚点、增长率和下一阈值；
- 工程可以是当前阶段、速率和下一里程碑；
- 天气可以是当前趋势和下一次制度化状态变化；
- 票据可以是有效状态和到期边界；
- 挖矿发现可以是带稳定随机身份的下一跳变。

因此 Kernel 不逐帧更新世界，而是寻找：

> **当前全部 Lazy 法则之中，首个会破坏至少一条现行求值假设的 occurrence。**

---

## 2. 目标时间模型：`LogicalInstant = (ModelTime, CausalOrdinal)`

### 2.1 四种时间仍须分离

系统继续区分：

- **Model Time**：世界内时间；
- **Causal Ordinal**：同一 ModelTime 上已提交 occurrence 的严格因果序号；
- **Wall-Clock Time**：Human、LLM、网络与服务器实际花费的时间；
- **Presentation Time**：Godot 动画、镜头和表现消耗的时间。

权威逻辑时间为：

```text
LogicalInstant = (ModelTime, CausalOrdinal)
```

其字典序构成严格全序。

### 2.2 “同时”不再是因果概念

在同一个 `ModelTime = T` 上可以提交很多 occurrence，但它们分别位于：

```text
(T, 0)
(T, 1)
(T, 2)
...
```

所以两个已提交 occurrence 永远不会具有同一个逻辑时刻。`CausalOrdinal` 不是“规则后果阶段”“感知阶段”等类别编号，也不为任何 subsystem 预留区段；它只是 Kernel 每原子提交一次便递增的序号。

当 winner 的 `Due > Current.ModelTime` 时：

```text
ModelTime      = winner.Due
CausalOrdinal = 0
```

此后若重新 Forecast 得到更多 `Due == ModelTime` 的候选，则逐次使用 `1, 2, 3...`。

空 lineage 不伪造一个已提交 occurrence。初值冻结为：

```text
WorldVersion.Empty
    LineageId
    TransitionCount = 0
    HeadHash = GenesisHash(RunManifestVersion, LineageId)

LogicalCursor.Empty
    InitialModelTime
    LastCommittedInstant = null
    NextCausalOrdinalAtInitialModelTime = 0
```

第一条 winner 无论 `Due == InitialModelTime` 还是位于未来，都使用 `CausalOrdinal = 0`；以后只有在相同 ModelTime 上才从上一条 ordinal 加一。`CausalOrdinal` 本身始终非负，不使用 `-1` 哨兵。

### 2.3 已知精确时间不能被伪随机覆盖

某些领域的权威提交时间是整数 ModelTime，但还能证明桶内的精确物理先后。例如两个 Passage contact 的精确时间分别是 `T + 1/10` 与 `T + 9/10`，两者可能都在 `SettlementDue = T + 1` 提交。

所有 candidate 必须具有可全局比较的：

```text
Due                         // 权威 ModelTime bucket
EffectiveExactDue           // 必填的 canonical signed rational

EffectiveExactDue = Canonicalize(
    KnownExactDue ?? CanonicalRational(Due))
```

Run Manifest 必须冻结唯一 epoch、基础时间单位、signed-rational codec 以及 `SettlementBucket(exact) -> Due` 规则。分母必须为正、分数必须约分且零只有一种编码；若提供 KnownExactDue，它必须与 `Due` 落在同一个规范 bucket，否则 Forecast 立即失败。不存在 `ExactTimeDomain` 或“这两个值碰巧可以比较”的 pairwise 分支。

全局比较无条件使用 `EffectiveExactDue`。整数 deadline 通过 `CanonicalRational(Due)` 参加同一比较；Spatial contact 等更精确证据不能先 ceil 后被 PRF 颠倒。`EffectiveExactDue` 是预测与排序元数据，不建立第二套可被任意查询的世界时钟。

---

## 3. 统一概念模型

### 3.1 ForecastBasis：一轮预测的完整权威前沿

每轮 Forecast 开始前，Kernel 冻结一个纯模拟前沿：

```text
ForecastBasis
    WorldVersion
    WorldSnapshotHash
    LogicalCursor
    RunManifestVersion
```

所有 Source 在同一 immutable WorldSnapshot/hash、LogicalCursor 和 Run Manifest 上联合预测。Candidate、AtomicTransitionPlan 与最终 CommittedTransition 都必须携带同一 ForecastBasis；任何世界 commit 后旧 basis 与全部旧 candidate 一起失效。

Resolution 不是 ForecastBasis 之外的异步候选流。只有某个需要 resolution 的 candidate 已经成为本轮唯一 winner 后，同一个未完成 Step 才调用 generic resolver；等待完整 validated resolution 期间 basis 不变，也不允许提交其它 occurrence。

### 3.2 Candidate：尚未发生的未来提案

`AtomicOccurrenceCandidate` 是对当前 immutable WorldVersion 的预测：

```text
AtomicOccurrenceCandidate
    SourceId
    CandidateId
    Generation
    Due
    EffectiveExactDue
    HandlerId
    Payload
    ResolutionContract?
    ForecastBasis
    Provenance
```

要求：

- 身份稳定、可序列化、可比较；
- `CandidateId` 由可信 Source 按领域协议分配，不能由之后提供 resolution 的一方操纵；
- 对相同世界重复 Forecast 得到相同语义结果；
- 不携带捕获可变内存的任意 closure；
- 不得自行写 Journal 或修改 World；
- 只在该 `ForecastBasis` 上成立；任一 commit 或新一轮 basis capture 后全部视为失效。

Forecast 是候选未来，不是已经写入历史、随后又被“取消”的事实。

### 3.3 Command：对世界提出的原子改变

Command 表达意图及其参数，例如：

```text
UseTicketAndBoard
TurnBack
ExpireTicket
ResolvePassageContact
SetPassageEnabled
ResolveProvidenceLaw
```

普通 candidate 可以直接引用完整 payload 与稳定的 `HandlerId`；需要补全 payload 的 candidate 只声明 `ResolutionContract`，winner 选出后由同一未完成 Step 内的独立验证边界提供完整 `ValidatedOccurrenceResolution`。两者最终都由注册 handler 做 pure Plan：

```text
Plan(currentWorld, selectedCandidate, optional ValidatedOccurrenceResolution)
    → AtomicTransitionPlan
```

`Plan` 必须是可重跑的纯规划过程。它可以跨 Game、Spatial、Inventory、Faction 等多个 projector 做 scratch-fold，但没有 commit 权力。

### 3.4 DomainFact：已经发生的不可否认事实

DomainFact 只在 transition 成功 commit 后成为历史。例如一个 `UseTicketAndBoard` occurrence 可以展开为：

```text
Fact[0] = TicketConsumed
Fact[1] = TraversalStarted
Fact[2] = TravelPartyUpdated
Fact[3] = ResolutionRecorded
```

这些不是四个互相竞争的 occurrence，而是同一个原子原因的审计展开。一个 `CommittedTransition` 内的全部 DomainFact **共享同一个 `LogicalInstant`**；连续的 `FactOrdinal = 0..N-1` 只负责规范化序列化、projector fold 与定位诊断。

`FactOrdinal` 不是时间，不能成为新的可观察因果前沿。任何 Query、Decision、Forecast、Snapshot、Fork 或外部订阅都只能观察 transition 提交前或全部 Facts fold 完成后的世界，绝不能观察“只应用到 Fact[k]”的 prefix。

必须始终保持：

```text
Candidate ≠ Command ≠ DomainFact
```

### 3.5 AtomicTransition：全局原子提交单位

```text
AtomicTransitionPlan
    OccurrenceId
    ForecastBasis
    ExpectedWorldVersion
    ProposedLogicalInstant
    ResolutionAudit?
    Facts[]
    CommandResult
    Provenance
    ExpectedFinalStateHash?
```

```text
ResolutionAudit
    ValidatorId / ValidatorVersion
    ResolutionContractId / ContractVersion
    CanonicalResolutionPayload
    ResolutionPayloadHash
    ValidationReceiptRef / ReceiptHash
```

Resolution contract codec、validator trust policy 与 canonical payload codec 必须冻结进 RunManifestVersion。ResolutionAudit 是审计/恢复锚点，不使 payload 在普通 Replay 中变成可执行输入。

Kernel 必须：

1. 检查 candidate 和 plan 绑定同一份当前 ForecastBasis；
2. 在 scratch world 上按 `FactOrdinal` fold 全部 Facts；
3. 验证所有跨域 invariant；
4. 任一失败则零事件提交；
5. 成功则把整个 `CommittedTransition` 原子追加到 Journal；
6. 一次性安装最终 WorldVersion；
7. 推进 `LogicalInstant`；
8. 丢弃所有旧 Forecast。

一个 occurrence 可以产生多个 Facts，但不能产生半个成功世界。`IJournalSink.AppendTransition` 的目标契约必须是真正的 all-or-nothing，而非对单条 `AppendFact` 的便利循环。

`WorldVersion` 只在完整 transition boundary 上递增。权威 WorldSnapshot、checkpoint 与 Fork 也只能指向 transition boundary；transition 内部的 scratch prefix 没有 WorldVersion，不可持久化或公开。

这里 `AtomicTransitionPlan.ExpectedWorldVersion` 是 handler 刚刚读取并规划的当前 transition boundary，必须严格匹配；它不同于原始 command 的 `BasedOnWorldVersion`。后者通常只是 provenance，只有 command 显式声明 CAS 语义时才构成严格版本前置条件。

Validated resolution 必须严格绑定本次未完成 Step 的 ForecastBasis/FrozenWorldSnapshot 与 winner。独立验证边界可以做 schema、envelope、identity 与 capability 检查；Kernel 只验证 opaque receipt/payload hash、winner binding 与 manifest-pinned validator identity，不能换用新世界。

### 3.6 TransitionHash 与 ResultingWorldVersion 的无自引用编码

CommittedTransition 使用固定两阶段算法：

```text
CommitOrdinal = ExpectedWorldVersion.TransitionCount   // empty lineage 首条为 0
```

```text
body = CanonicalEncode(
    CodecVersion,
    ExpectedWorldVersion,
    ForecastBasis,
    CommitOrdinal,
    LogicalInstant,
    OccurrenceId,
    Source/Candidate/Generation/Handler,
    Provenance,
    ResolutionAudit?,
    Facts[],
    CommandResult)
// body 明确排除 TransitionHash 与 ResultingWorldVersion

TransitionHash = Hash(
    HashRulesVersion,
    ExpectedWorldVersion.HeadHash,
    body)

ResultingWorldVersion = (
    ExpectedWorldVersion.LineageId,
    ExpectedWorldVersion.TransitionCount + 1,
    TransitionHash)
```

随后把 `body + TransitionHash + ResultingWorldVersion` 作为一个 CommittedTransition 原子持久化。读取时先由 body 和 previous head 重算 hash，再验证 resulting version；任何实现都不得把待计算的 TransitionHash/ResultingWorldVersion 放回自身 hash 输入。空 lineage 的 `GenesisHash` 与第一条 `CommitOrdinal = 0` 也必须由 Run Manifest 的 codec/hash golden 固定。

---

## 4. 联合 Forecast 与唯一 winner

### 4.1 所有来源进入同一个候选宇宙

V1 运行期只有两条合法 payload 来源：

1. `WorldSnapshot + RunManifest` 的 pure Forecast 产生 candidate，winner 的 pure handler 产生 plan；
2. 已经胜出且声明 ResolutionContract 的 candidate 在同一个未完成 Step 内调用 generic resolver；独立验证边界只补全该 winner 的 `ValidatedOccurrenceResolution`，再由同一个 pure handler 产生 plan。

第一类可以覆盖：

- Travel / Spatial 物理边界；
- Activity / Work；
- Combat；
- Needs / Fatigue；
- Weather / Environment；
- Timer / Deadline；
- Game rules；
- Player turn / action-opportunity rules 产生的 `DecisionPointCandidate`（领域 candidate，不是 Kernel 特例）；

Human/AI Player 的 orchestration 在 Kernel 外实现 generic resolver，把 DecisionPoint context 解析为 validated resolution。Providence 在 V1 只能是冻结于 World+RunManifest 的 pure law，走第一类；LLM/planner Providence 留给未来 ADR，不属于本设计。

V1 禁止 Admin runtime live command。Setup 只负责创建 Genesis/new lineage；预编排脚本必须在运行前冻结进 World 或 RunManifest，随后由第一类 pure Forecast 读取。每个 runtime plan 都必须追溯到一个 World/Manifest 所产生的 winner，以及必要时同一 Step 针对该 winner 获得的 validated resolution，不能来自隐藏候选队列或 spontaneous ingress。

Kernel 不再调用：

```text
ApplyExternalEvent(...)
ResolveInternalEvent(...)
```

而只认识：

```text
Forecast(context) → candidates
Plan(selectedWinner, optional ValidatedOccurrenceResolution) → atomic transition plan
```

### 4.2 Source 的完备性契约

每个 Source 必须暴露它当前可能产生的全局最小候选，不能隐藏一个更早 occurrence。

正确性优先的 reference implementation 可以让 Source 返回全部当前候选。若要优化成 `ForecastNext`，Source 必须使用与 Kernel 完全相同的 comparator 求出自己的局部最小值，因为：

```text
min(union(all source candidates))
    = min(union(each source local minimum))
```

若 Source 内的候选不可完全枚举，则它必须提供可证明不会漏掉更早边界的领域算法。

### 4.3 全局比较器

每轮比较顺序冻结为：

```text
1. Due
2. EffectiveExactDue
3. DeterministicArbitrationRank
4. StableCandidateKey（仅作 hash 碰撞兜底）
```

四项都使用规范字节编码并构成严格全序；不允许 `ExactTimeDomain`、nullable exact-time 分支或 comparator 返回“不可比较”。

稳定身份：

```text
StableCandidateKey =
    SourceId
    + CandidateId
    + Generation
    + HandlerId
```

其中 `CandidateId` 必须由领域 Source 在调用 resolver 前分配。之后提供的 resolution payload 不能直接或间接改变 StableCandidateKey，否则 resolver 可以通过改写 payload 身份磨出有利的 PRF rank。

仲裁 rank 推荐采用固定版本、无状态的 PRF：

```text
rank = PRF(
    RunArbitrationSeed,
    OrderingRulesVersion,
    CanonicalDue,
    CanonicalEffectiveExactDue,
    StableCandidateKey)
```

要求：

- 不消耗可变 RNG stream；
- 不依赖 Source 注册顺序、线程调度、集合枚举顺序或进程 hash salt；
- 相同 seed、规则版本、CanonicalDue、CanonicalEffectiveExactDue 与稳定身份必须给出相同 rank；
- rank 完全相同时使用 canonical byte ordering 的 StableCandidateKey 兜底；
- `OrderingRulesVersion` 必须进入存档兼容性与 Replay 元数据。

PRF 避免固定 `SourceId` 优先级长期偏袒某个 subsystem。它不是物理时间，也不是可倒插历史的“小数时间”。

### 4.4 每个因果前沿只选一个

即使多个候选具有相同 Due，Kernel 也只能选择一个 winner：

```text
Forecast all
→ select exactly one
→ plan
→ atomic commit
→ invalidate all forecasts
→ Forecast all again
```

新 occurrence 可能使其余候选：

- 消失；
- 改变 Due；
- 改变 Generation；
- 变为拒绝结果；
- 继续留在下一因果前沿。

新生成候选即使拥有更小的 PRF rank，也只能在其原因 occurrence 之后参加下一轮，不能倒插到较小 `CausalOrdinal`。Journal 中的因果顺序由 `LogicalInstant` 决定，而不是由 rank 决定。

---

## 5. 权威 Kernel 循环

参考语义：

```text
StepAsync(until, limits):
    require no other Kernel operation is executing

    while true:
        basis = FreezeForecastBasis(
            WorldVersion,
            Hash(WorldSnapshot),
            LogicalCursor,
            RunManifestVersion)
        forecastContext = Freeze(World, LogicalNow, basis)
        candidates = ForecastAllSources(forecastContext, basis)

        ValidateForecastPurityAndBounds(candidates)

        winner = SelectGlobalMinimum(candidates)
        if winner is null or winner.Due > until:
            return HorizonReached

        nextInstant = ProposeLogicalInstant(winner.Due) // pure，不推进 cursor

        resolution = none
        if winner declares ResolutionContract:
            selected = CreateEphemeralSelectedWinnerContext(
                basis, WorldSnapshot, winner, nextInstant)
            resolution = await OccurrenceResolver.Resolve(selected)

            integrity = VerifyValidatedResolutionBinding(
                resolution.ForecastBasis == basis,
                resolution.StableCandidateKey == winner.Key,
                resolution.Generation == winner.Generation,
                resolution.ProposedLogicalInstant == nextInstant,
                resolution.ContractId/Version == winner.ResolutionContract,
                validator identity/version is allowed by RunManifest,
                receipt hash and payload hash are internally consistent)
            if integrity invalid:
                return ResolutionBindingFault(integrity.Reason) // zero commit

        plan = Handler(winner.HandlerId)
            .Plan(WorldSnapshot, winner, resolution, basis, nextInstant)

        // 结构已验证但领域前置失败时，handler 返回非空 rejection plan，
        // 并消费/推进 winner generation。
        if resolution is not none:
            plan = AttachResolutionAudit(plan, winner, resolution)

        return Commit(plan, basis, nextInstant)

Commit(plan, basis, nextInstant):
        require plan.ForecastBasis == basis

        scratch = FoldAndValidateAtomically(World, plan.Facts)
        transition = EncodeCommittedTransition(plan, basis, nextInstant)
        Journal.AppendTransitionAtomically(transition)
        World = scratch.FinalWorld
        WorldVersion = transition.ResultingWorldVersion
        LogicalNow = nextInstant

        DiscardAllForecasts()
        return Committed(transition)
```

`SelectedWinnerContext` 只是当前 `StepAsync` 调用栈中的 ephemeral binding，可由 canonical `(ForecastBasis, StableCandidateKey, winner.Generation, ProposedLogicalInstant, ResolutionContract)` 计算校验 hash；它不是 public/persisted request、DomainFact 或可恢复 lifecycle。`ProposeLogicalInstant` 与 resolver 等待都不推进权威 cursor；只有 Commit 成功才安装新的 LogicalInstant。等待 resolver 期间同一 Kernel instance 不接受另一个 Step 或 commit。Crash/取消发生在 append 前，则该 Step 没有发生；重启后重新 Forecast，并允许外部 resolver 给出不同 resolution。

### 5.1 Advance/Elapse 的语义

当 winner 位于未来，Kernel 不需要逐对象积分。将 `ModelTime` 推进至 `Due` 只意味着：

- 当前所有 Lazy law 在该区间内保持有效；
- handler 可按 `Due` materialize 其原子边界所需数据；
- Query 可以从锚点按需计算任意合法时间的 effective state。

不存在独立、可改变世界的“先全局 Elapse 再 Resolve”阶段。若某个领域必须在边界上结算累计量，该 materialization 属于 winner 的原子 transition。

### 5.2 全量 re-Forecast 是规范实现

每次 commit 后，全系统 Forecast 都重新运行。其价值是：

- 不需要维护容易出错的跨域 invalidation graph；
- Player action、天气和物理碰撞自然拥有同样影响范围；
- 更容易证明 replay determinism 与 split-run confluence；
- Debug 时能解释每轮世界为何选择该 occurrence。

未来可以加入依赖索引和增量缓存，但优化结果必须与 reference full re-Forecast 的 winner、CommittedTransition 和 Journal 完全等价。

---

## 6. Unfinished-step winner resolution

### 6.1 SelectedWinnerContext 只是临时调用状态

需要补全 payload 的 winner 在同一次尚未完成的 `StepAsync` 中产生：

```text
SelectedWinnerContext                 // ephemeral, not persisted
    ForecastBasis
    FrozenWorldSnapshotRef / Hash
    StableCandidateKey / Generation
    HandlerId
    ResolutionContractId / ContractVersion
    ProposedLogicalInstant
```

它不是 public request、DomainFact、Journal record 或可跨进程恢复的 lifecycle。Kernel 不对外暴露 `Request → Resume` 协议，也不维护 durable active-invocation state。resolver 等待期间 Step 尚未返回，World、LogicalInstant、WorldVersion 与 Journal 全部不变；同一 Kernel instance 禁止并发 Step/commit。

### 6.2 独立验证边界

Kernel 不获取或解释 raw output，也不定义 Player/AI/LLM 调用。Host、Protocol 或领域 adapter 在 Kernel 外完成 schema、envelope、identity、size、capability 及产品 policy 校验；由领域中立 resolver 接口向尚未完成的 Step 返回：

```text
ValidatedOccurrenceResolution
    ForecastBasis
    StableCandidateKey / Generation
    ProposedLogicalInstant
    ResolutionContractId / ContractVersion
    ValidatorId / ValidatorVersion
    CanonicalPayload
    PayloadHash
    ValidationReceiptRef / ReceiptHash
```

Kernel 仅验证这些 opaque 字段的 binding、hash、manifest-pinned validator identity 与刚选出的 winner/context 一致；失败时零提交并结束为 fault。校验边界如何重问、等待、持久化或恢复其进程状态，不属于 Kernel Law，Kernel 也不承诺 provider exactly-once。

### 6.3 Domain Plan 与 rejection

binding/integrity 通过后，同一个 winner handler 在冻结 snapshot 上运行 pure Plan。结构已验证的 resolution 仍可能违反 ticket、位置、资源等 world-dependent precondition；此时 handler 必须返回非空 rejection plan，提交原因 Fact 并消费/推进 winner generation。Domain rejection 是 occurrence 的世界结果，不能重新调用 resolver 替换选择。

```text
StepAsync selects one winner
→ if required, await generic resolver           // zero ModelTime, no commit
→ verify ValidatedOccurrenceResolution binding
→ pure Plan on the same basis/snapshot
→ accepted or domain-rejection transition
→ one atomic commit
→ StepAsync returns
```

### 6.4 DecisionPoint 领域示例

A/B 两个 DecisionPointCandidate 同位时，global comparator 只选择一个。A 若胜出，同一个未完成 Step 只 resolve A；B 不会因 Human/AI/网络更快而插队。A resolution 原子提交后 full re-Forecast，B 的旧 candidate 失效并在新世界中重新判断。

票据竞争同样发生在 `ExpireTicket` 与 `DecisionPoint(BoardingOpportunity)` 之间。到期先胜则新 Observation 不再提供乘船；DecisionPoint 先胜则其 validated resolution 可以让同一 handler 原子提交 `TicketConsumed + TraversalStarted`，等待期间到期 occurrence 不得插入。

这些 Human/AI/Observation/WorldCommand 细节属于 Decision 领域与外部 orchestration；Kernel 只看到 ResolutionContract、opaque validated payload 和 pure handler。

### 6.5 Replay、Fork 与 crash 边界

普通 Replay 只 fold Facts，不重新取得 resolution，也不执行 ResolutionAudit payload。Audited Replay 可重建 winner，使用 Journal 中的 validated payload 重跑 pure Plan，并逐字节比较 Facts/CommandResult。

Fork 只发生在 CommittedTransition boundary；ephemeral SelectedWinnerContext 不属于 Fork 权威状态。若 append 前 crash/取消，没有 occurrence 被提交，整个 Step 视为没有发生；重启从相同 World/Manifest 重新 Forecast，resolver 可以给出不同结果。Kernel 不持久化 provider 状态，也不承诺 restart 与 one-shot byte-equal。append 成功后的恢复只遵循 Journal 自身的原子 commit 语义。

---

## 7. Provenance、权限与调度解耦

统一调度不意味着抹掉来源差异。所有 command/candidate/transition 应携带可审计 envelope：

```text
Provenance
    Origin = ScheduledLaw | Player | AI | Providence | Admin | Setup
    PrincipalId
    Authority / Capability
    CommandId
    BasedOnWorldVersion
    DecisionId?
    PlayerId?
    InterventionId?
    ProvidenceId?
    BudgetRef?
    ReasonRef?
```

这些字段用于：

- 是否有权提出某项 command；
- 幂等去重；
- Providence 预算和世界内原因审计；
- Player agency 分析；
- Debug、Replay 与责任追踪；
- 隔离 bootstrap 和运行期权限。

它们不得用于给某类来源固定的调度优势。时间比较器只看 Due、合法的精确时间证据、PRF rank 与稳定身份兜底。

对 DecisionPoint 领域而言，这些字段可以描述“哪个 winner 最终采用了哪个 validated resolution”，但 Kernel 不解释 PlayerId、DecisionId 或领域 payload。

`Origin = Admin | Setup` 可以出现在 Genesis、离线迁移或历史 provenance 中，但不授权 V1 runtime ingress；`Origin = Providence` 必须追溯到 World+RunManifest pure law 的 winner，不能来自 runtime planner。

---

## 8. 随机性的统一规则

### 8.1 Forecast 不得消费可变 RNG

以下实现不可接受：

```text
Forecast():
    rng.Next()
```

因为一次额外的 Forecast 调用会改变未来。

领域随机候选必须使用 addressable deterministic randomness，例如：

```text
sample = PRF(
    WorldSeed,
    RandomRulesVersion,
    DomainStreamId,
    EntityOrActivityId,
    Generation)
```

相同 WorldState、身份和 Generation 重复 Forecast 必须得到相同候选。

### 8.2 仲裁随机与领域随机分离

`DeterministicArbitrationRank` 只选择真正同位候选的因果先后，不得被复用为：

- 命中率；
- 掉落；
- NPC 决策；
- 物理误差；
- 任何领域结果。

两类 PRF 使用不同 domain separator 和独立的版本号，以免排序规则升级意外改变游戏随机内容。

---

## 9. Journal、Replay 与 Fork

### 9.1 Journal 记录 CommittedTransition，不记录 Forecast 为事实

Journal 中每个 `CommittedTransition` 至少包含：

```text
OccurrenceId
LogicalInstant
CommitOrdinal
ForecastBasis
ExpectedWorldVersion
ResultingWorldVersion
SourceId
CandidateId
Generation
HandlerId
Provenance
ResolutionAudit?
OrderingRulesVersion
CodecVersion / HashRulesVersion
Facts[]
    FactOrdinal
    DomainFact
CommandResult
PreviousHash
TransitionHash
```

同一 transition 的全部 Facts 共享 header 中的 `LogicalInstant`；`FactOrdinal` 连续但不构成时间。Journal、projector API 和订阅器不得暴露可被业务观察的 Facts prefix。Forecast 可以写入独立 debug trace，但不能混入权威历史。

### 9.2 Replay

普通 Replay 直接按 Journal 中严格递增的 `LogicalInstant` 原子 fold `Facts[]`，不执行 `ResolutionAudit` 中的 canonical payload，不需要重新 Forecast，也不需要重新调用任何外部 resolver。

Replay 必须验证：

- ModelTime 单调不减；
- 同一 ModelTime 的 CausalOrdinal 连续递增；
- 一个 LogicalInstant 只有一个 CommittedTransition；
- transition 完整性、Facts 的连续 FactOrdinal 与 hash chain；
- WorldVersion 连续；
- projector fold 后的 checkpoint hash。

Replay 只能在完整 transition fold 后发布新 WorldVersion、Snapshot 或订阅通知。任意 Fact prefix 都只是 reducer 内部临时值。

可选的 audited Replay 按每条 transition 记录的纯模拟 ForecastBasis 重新 Forecast，验证 occurrence 是当时的全局 winner。若 transition 带 ResolutionAudit，则以 recorded validated payload 重跑同一个 pure handler，并要求所得 `Facts[]`/`CommandResult` 与 Journal 逐字节相同；不得重新调用任何外部 resolver。这是测试与迁移工具，不是普通加载存档的必要条件。

### 9.3 Fork

Fork 必须复制：

- 当前 World 与 WorldVersion；
- 当前 `LogicalInstant`；
- RunArbitrationSeed、WorldSeed、OrderingRulesVersion、RandomRulesVersion、RunManifestVersion；
- 所有 source 的 generation、watermark 和 pending state；
- Journal 前缀与 hash anchor。

相同前缀和相同规则版本应产生相同 winner；Fork 中外部领域 resolver 返回不同 validated payload 后可以自然分歧。

Forecast cache 和 ephemeral SelectedWinnerContext 都不属于 Fork 权威状态；Fork 可以丢弃并全量重算。Fork point 必须是 CommittedTransition boundary，不能指向某个 FactOrdinal 或未完成 Step。

---

## 10. 活性、容量与失败语义

### 10.1 winner 必须产生进展

一次 winner 被 commit 后，必须至少满足一项：

- 消费/推进 winner 或 resolution opportunity；
- 推进 candidate generation；
- 推进领域 settlement watermark；
- 改变足以使同一 candidate 不再以相同身份 Forecast 的权威状态。

不得让一个无状态 no-op candidate 在相同世界上反复获胜。Accepted 或 domain-rejected resolution 都必须产生能消费/提升 winner generation 的非空 CommittedTransition。

### 10.2 同一 ModelTime 仍可能出现零时间循环

`CausalOrdinal` 消除了同时歧义，但不能自动证明同一 ModelTime 的因果链有限。例如两个规则可能互相反复触发。

Kernel 必须有：

- `MaxOccurrencesPerModelTime`；
- `MaxForecastRoundsPerRun`；
- `MaxCandidatesPerSource/GlobalForecast`；
- `MaxFactsPerCommittedTransition`；
- `MaxJournalBytesPerRun/Transition`；
- repeated candidate/world fingerprint 检测。

达到上限时应产生确定性的 fault/report 并停止，不得静默丢事件或用不稳定顺序继续运行。

### 10.3 Plan 失败

Forecast 与 Plan 之间按规范不会有其他世界 commit，但 handler 仍须防御 stale、溢出和领域不变量错误：

- Validated resolution 的 binding/integrity 不匹配：零提交并以 deterministic fault 结束本次 Step；
- 领域前置条件失败：handler 规划非空 rejection transition 并消费 winner generation；
- candidate/plan 的 ForecastBasis 与本轮冻结 basis 不匹配：Kernel fault，丢弃本轮并诊断 source 契约；
- scratch-fold 或 Journal 原子提交失败：世界和 Journal 均保持原状，Kernel 停止并报告；
- 不允许先提交一部分，再用补偿事件假装原子性。

---

## 11. 信息隔离与表现层

Simulator 可以知道未来 candidate，Player 不能因此预知未来：

```text
Simulator foresight ≠ Player foresight
```

Decision 领域的 Observation 只能来自冻结 WorldSnapshot 上的合法感知投影。Forecast queue、PRF rank、其它 candidate 与 opaque resolution metadata 都不能因此成为 Player 信息；具体投影由 Kernel 外的领域 adapter 负责。

Godot 的 NavMesh、动画与镜头属于 Presentation Time。视觉人物走了 38 秒还是 42 秒，不应改变 ModelTime 上已经提交的 8 分钟旅行；反过来，Presentation 也不能因为动画尚未播放完而改变 winner。

这继续支持：

> **离散棋盘底层 + 连续 RPG 表现 + 小说式时间压缩。**

---

## 12. 建议接口形状

以下仅冻结职责，不冻结最终 C# 命名：

```text
interface IOccurrenceSource
{
    SourceId Id { get; }

    IReadOnlyList<AtomicOccurrenceCandidate> Forecast(
        ForecastContext context);
}

interface IOccurrenceHandler
{
    HandlerId Id { get; }

    AtomicTransitionPlan Plan(
        WorldSnapshot frozenWorld,
        AtomicOccurrenceCandidate winner,
        ValidatedOccurrenceResolution? resolution,
        ForecastBasis basis,
        LogicalInstant proposedInstant);
}

interface ISimulationKernel
{
    ValueTask<StepResult> StepAsync(
        ModelTime horizon,
        CancellationToken cancellation);
}

interface IOccurrenceResolver
{
    // 输入只在本次 StepAsync 调用期间有效；实现须在 Kernel 外完成结构/权限校验。
    ValueTask<ValidatedOccurrenceResolution> ResolveAsync(
        SelectedWinnerContext context,
        CancellationToken cancellation);
}

interface IAtomicJournal
{
    CommitResult AppendTransitionAtomically(
        CommittedTransition transition);
}
```

`ForecastContext` 至少包含：

```text
WorldSnapshot
ForecastBasis
LogicalNow
WorldSeed
CapacityLimits
```

`StepResult` 可以是 `Committed`、`HorizonReached` 或 fault；`StepAsync` 未完成时同一 Kernel instance 拒绝另一个 Step/commit。Kernel 是唯一 commit authority。Source、Handler 与 Kernel 外的 resolver 都不获得 `IJournal` 或可变 World 的直接写权限。

依赖方向也是本设计的 Law：**Kernel assembly 不得引用 Host、Protocol、Player、Player.Llm 或任何 orchestration implementation assembly**。`IOccurrenceResolver`、ephemeral `SelectedWinnerContext`、`ValidatedOccurrenceResolution` 及其 opaque binding/receipt 是领域中立的 inward contracts；schema/capability validator、Human/AI adapter、网络 transport 与持久化策略都在 Kernel 外实现或装配这些 contracts。Kernel 只调用抽象 resolver，不识别实现类型。

---

## 13. 重构实施步骤

本节给出目标依赖顺序；具体交付波次、项目清单与退出条件以 `研发计划_006_统一原子Occurrence与LogicalInstant_Kernel重构计划.md` 为实施权威。若两份文档的阶段编号或迁移细节不一致，以 006 为准，但 006 不得削弱本文的目标 Law。

### 阶段 0：冻结行为基线和冲突清单

在改变代码前，为当前 Kernel 建立 characterization tests，并列出所有旁路：

- `Run(externalEvents)` 在 Forecast 前提交；
- `EventCause.ExternalInput` / `ResolveBatch` 的调度含义；
- `ApplyExternalEvent` / `ResolveInternalEvent`；
- Decision answer 直接生成 DomainEvent；
- subsystem-specific gateway 或 same-time phase；
- 非原子的逐 Fact 追加或 `AppendBatch`；
- FirstBoard、Demo、Spatial 中依赖旧 external-first/internal-first 的代码。

测试用于记录迁移前行为，不代表要保留旧语义。

### 阶段 1：引入新的时间和值对象

新增并测试：

- `LogicalInstant(ModelTime, CausalOrdinal)`；
- `WorldVersion`；
- `StableCandidateKey`；
- `AtomicOccurrenceCandidate`；
- `ForecastBasis`；
- `Provenance`；
- `RunManifestVersion / OrderingRulesVersion / RunArbitrationSeed`；
- ResolutionContract、canonical resolution payload 与 ValidationReceipt codec/version；
- `FactOrdinal` 与 `CommittedTransition`；
- global epoch/unit/bucket、`EffectiveExactDue` 与 canonical serialization/comparator；
- empty lineage、GenesisHash 与首个 CausalOrdinal goldens。

Journal/checkpoint 先能够同时读旧格式和写新格式；存档迁移策略必须显式版本化。

### 阶段 2：原子 Journal 与 pure scratch transition

实现：

- `CommittedTransition` 与无自引用 TransitionHash codec；
- 跨 projector pure scratch-fold；
- atomic `AppendTransition`，全部 Facts 共享一个 LogicalInstant；
- commit 后一次性安装 ResultingWorldVersion；
- transition-boundary Snapshot/Fork；
- append 前/中/后 crash fault injection。

### 阶段 3：统一 occurrence scheduler 与 migration adapter

实现：

- `IOccurrenceSource` 与 `IOccurrenceHandler.Plan`；
- 每轮原子冻结 ForecastBasis；
- reference `ForecastAllSources`；
- Due / EffectiveExactDue / PRF / StableKey 严格全比较器；
- addressable PRF；
- 每次只提交一个 occurrence；
- commit 后无条件 full re-Forecast；
- capacity、zero-time-loop 防护及 candidate/rank/winner trace。

迁移期可用 adapter 包装旧 `ISimSystem` 或 legacy input，但 adapter 必须把结果转成统一 candidate/Plan，不能保留 external-first/internal-first 或本地优先级。此时只允许测试与迁移调用方使用，不能成为目标兼容层。

### 阶段 4：迁移 generic winner resolution

把流程改为：

```text
candidate with ResolutionContract wins global arbitration
→ same unfinished Step calls generic resolver         // zero ModelTime
→ independent boundary returns ValidatedOccurrenceResolution
→ handler Plan on the same basis/snapshot
→ accepted or domain-rejection Facts commit as one transition
```

实现单次 `StepAsync → resolver → Plan → Commit`、in-flight Step single-writer guard、opaque binding/integrity 校验、domain Plan rejection 与 resolution audit。schema/envelope/identity/size/capability 等结构校验在独立边界完成；Kernel 不持有 public request、resume API、raw output、provider retry、memo 或 strategy state。删除 Host 直接把 answer 投影进 World 的路径；**本阶段结束后删除全部 live `Run(externalInputs)`/direct result-fact ingress**。补充 A/B DecisionPoint 仲裁、票据 vs 行动机会、等待期间禁止 commit、binding fault/domain rejection，以及 crash-before-commit 可重新选择测试。

### 阶段 5：迁移跨域 Game command

优先迁移会同时影响 Game 与 Spatial 的 command，例如：

- 消耗 ticket 并开始 Traversal；
- 支付资源并创建/修改 Place；
- 战斗结果与移除/移动 Actor；
- Providence 的世界内原因和空间变化。

每项改为一个 handler 规划、一个 CommittedTransition、一个 LogicalInstant。任一域失败必须零提交。

### 阶段 6：让 FirstBoard、Demo、Spatial 服从目标内核

逐项目迁移：

1. `src/FirstBoard`：规则、定时器与 Actor 行为改为 OccurrenceSource/Handler；
2. `src/FirstBoard.Demo`：UI/领域 adapter 在 Kernel 外实现 generic resolver，把 DecisionPoint context 解析并验证为 `ValidatedOccurrenceResolution`，不再构造结果事件；
3. `src/Spatial`：arrival、contact、mutation、同行关系变化统一为 candidate 或同一 transition 内的 DomainFacts；移除 Spatial 自己的外部命令同刻屏障；
4. Host：负责运行循环、展示及 Kernel 外的 resolution orchestration；`StepAsync` 未完成期间不得启动另一个 Step。其 transport、validator、provider 与持久化实现不得成为 Kernel 的项目依赖。

若这些项目的现有 API、事件粒度或调用顺序与目标模型冲突，应修改调用方和领域边界，而不是在 Kernel 中增加兼容性分支。

Spatial 尤其不得把旧式“处理完整个 T”的 watermark 原样带入一次只选一个 occurrence 的 Kernel：处理一个 contact 后不能立刻写 `SettledThrough = T`，否则同 T 的其余 contact 会被错误吞掉。V1 reference semantics 冻结为在 Spatial WorldState 持久化：

```text
ConsumedContactKey
    PassageId
    FirstSegmentId / SecondSegmentId       // stable segment pair，canonical 排序
    FirstGeneration / SecondGeneration     // 与规范 pair 对齐
    EffectiveExactContactInstant           // canonical signed rational
```

contact 的 CommittedTransition 写入对应 consumed Fact；Forecast 排除完全相同的 key。任一 segment law 改变必须提升 generation，因而产生新 key。每个 contact transition 后都重新 Forecast，直到同 T 不再存在未消费候选。按 comparator 推进紧凑 prefix cursor 仅是未来可由差分测试证明等价后的优化，不属于 V1 权威语义。

### 阶段 7：删除双轨语义

所有调用方迁移后删除：

- external-first 入口；
- internal-first/gateway 补丁；
- 具有调度含义的 `ExternalInput` cause；
- 固定 microstep phase；
- 一轮批量 Resolve “同时 candidates”；
- handler/source 的 Journal 直写能力；
- 仅为旧 Demo 或 Spatial 保留的 Kernel 特例。

若仍需导入旧存档或测试 fixture，应离线转换成已有的 legacy CommittedTransition，或从 transition boundary 重新建立领域 candidate；不得恢复运行期 live input 旁路。

### 阶段 8：Replay/Fork、压力测试与优化

完成：

- 新旧 Journal 迁移；
- replay hash audit；
- fork 继承 world pending state、watermark、generation 与规则版本，不继承未完成 Step 的 ephemeral resolver context；
- 大量同 Due 候选的性能测试；
- 同一 ModelTime 长因果链的 capacity 测试；
- full re-Forecast reference 与未来 incremental implementation 的差分测试。

只有 profiler 证明 Forecast 是瓶颈后才引入增量 invalidation、局部 heap 或缓存。

---

## 14. P0 验收矩阵

### ORD-1：唯一因果全序

任意 CommittedTransition 的 `LogicalInstant` 唯一且严格递增；同一 ModelTime 不存在相同 CausalOrdinal。

### ORD-1A：Fact 不形成时间 prefix

一个 transition 的全部 Facts 共享同一 LogicalInstant，FactOrdinal 连续；任意 Query、订阅、Snapshot、Replay checkpoint 或 Fork 都只能看到 fold 前或全部 fold 后的世界。

### ORD-2：注册顺序无关

打乱 Source 注册、候选枚举和字典插入顺序，winner 序列及 Journal byte-for-byte 相同。

所有 candidate 即使未提供 KnownExactDue，也由 `EffectiveExactDue = CanonicalRational(Due)` 进入 `(Due, EffectiveExactDue, Rank, StableKey)` 严格全序；不存在 nullable 或不可比较分支。

### ORD-3：确定性同位仲裁

同 seed、规则版本和候选身份得到相同 PRF 顺序；改变 seed 可以得到另一条仍然合法、可 Replay 的顺序。

resolver 返回的 resolution payload、receipt 或外部自选标识不得反向改变已经胜出的 CandidateId 或仲裁 rank；StableCandidateKey 必须在调用 resolver 前由 Source/Host 规范分配。

### ORD-4：精确时间优先

已知 `T + 1/10` 的 contact 必须先于 `T + 9/10`，不能被 PRF 反转；不同领域使用同一 epoch/unit/rational codec，KnownExactDue 与 Due bucket 不一致、非约分或负分母编码必须在 Forecast 时拒绝。

### CAU-1：新候选不得倒插

`A@(T, n)` 产生的新候选即使 rank 小于 A，也只能提交于 `(T, n+1)` 或更晚。

### BAS-1：ForecastBasis 原子冻结

所有 Source、ephemeral SelectedWinnerContext、validated resolution、winner Plan 与 CommittedTransition 必须绑定同一纯模拟 `(WorldVersion, WorldSnapshotHash, LogicalCursor, RunManifestVersion)`；resolver 等待期间 basis 不变，任何 world commit 均被拒绝。

### UNI-1：无 external 旁路

运行期只有 World+RunManifest pure Forecast 的 candidate 可以参与仲裁；只有声明 `ResolutionContract` 的唯一 winner 可以在同一个未完成 Step 内调用 resolver，其 validated payload 只能补全该 winner。不存在 async input source、Forecast 前直接修改 World、public request/resume 或预先提交的 request Fact。

### UNI-1A：runtime 来源闭包

每个 runtime effect 必须追溯到 World+RunManifest pure Forecast 的 winner；需要外部补全时，还必须追溯到同一 Step 对该 winner 的 resolver 调用。隐藏队列、runtime Providence planner、spontaneous Admin ingress 与运行期 Setup 均被拒绝。

### UNI-1B：assembly 依赖闭包

Kernel assembly 对 Host、Protocol、Player、Player.Llm 与 orchestration implementation assembly 的项目引用数必须为零。独立 validation/provider/transport 只能依赖领域中立 resolution contracts；Kernel 不得反向调用或识别其实现类型。

### UNI-2：票据竞态

覆盖两种 seed：

- TicketExpired 先于 BoardingOpportunity DecisionPoint，随后领域 Observation 不再提供乘船并说明来迟；
- BoardingOpportunity 先胜出，validated resolution 表示 UseTicketAndBoard 后扣票、resolution audit 与 TraversalStarted 同 transition 原子成功，到期候选消失。

两者均可 Replay，且不存在部分扣票或无解释丢失。

### ATM-1：跨域 all-or-nothing

Game scratch-fold 成功而 Spatial 失败、以及相反情况，都必须保持 Journal 与 World 零变化。

### ATM-2：Journal 原子失败

注入 transition persistence failure 后，不能出现部分 Facts、已推进 WorldVersion 或已推进 LogicalInstant。

### HASH-1：空 lineage 与无自引用 hash

codec golden 固定 Empty WorldVersion/GenesisHash、第一条 CommitOrdinal 与 CausalOrdinal；TransitionHash 只计算不含自身和 ResultingWorldVersion 的 canonical body，重算结果逐字节相同。

### RES-1：单次未完成 Step

声明 ResolutionContract 的 winner 在同一个 `StepAsync` 内调用 generic resolver；validated resolution、Plan 与全部 Facts 绑定同一 basis/snapshot/ProposedLogicalInstant，最终只提交一个 CommittedTransition、增加一次 CausalOrdinal。resolver 等待不占 ModelTime 或 CausalOrdinal，Kernel 不暴露 request/resume lifecycle。

### RES-2：A/B 顺序与 in-flight world

A/B 两个需要 resolution 的 candidate 同位时只 resolve 全局 winner；等待期间另一个 Step 或任何 commit 都被拒绝。A commit 后 full re-Forecast，B 不能因外部 resolver 更快而插队。DecisionPoint A/B 必须作为一个领域实例覆盖。

### RES-3：validation boundary 与 domain rejection

schema/envelope/identity/size/capability 校验必须发生在 Kernel 外；Kernel 只接受 `ValidatedOccurrenceResolution`。basis/winner/proposed-instant/contract/validator receipt/payload hash 的 opaque binding 或完整性不匹配时零提交并 fault。binding 合法但 world-dependent domain precondition 失败时，pure Plan 必须提交非空 rejection transition 并消费 winner generation；不得把领域拒绝误报为 protocol retry。

### RES-4：crash boundary

append 前 crash/取消必须留下零 Journal、零 WorldVersion、零 LogicalInstant 变化，整个 Step 视为没有发生；restart 重新 Forecast，可以由 resolver 给出不同结果。Kernel 无 durable invocation、resume、provider exactly-once 或 restart==one-shot 承诺。append 开始后的 fault injection 继续由 ATM-2 的 Journal 原子性覆盖。

### RES-5：resolution audit

带 resolution 的 CommittedTransition 必须记录 `ResolutionAudit`：validator/contract 版本、canonical payload/hash 与 validation receipt ref/hash。普通 Replay 不执行这些 payload；audited Replay 才把它们交给同一个 pure handler 验证结果。

### RPL-1：Replay

普通 Replay 不 Forecast、不调用任何外部 resolver、不执行 ResolutionAudit payload，只 fold Facts 并重建相同 checkpoint hash 与 LogicalInstant。Audited replay 验证 winner，并以 recorded validated payload 重跑 pure Plan，要求 Facts/CommandResult 逐字节相同。

### RPL-2：Fork

相同前缀保持相同 winner；未完成 Step 的 SelectedWinnerContext 及外部 resolver 私有状态不进入 Fork。Fork 从 transition boundary 全量 Forecast，可以在相同领域 winner 获得不同 validated resolution 后自然分歧；Kernel 不对外部 resolver 的分支继承作承诺。

### LIV-1：no-op 重复

同一 candidate/world fingerprint 重复出现时，在预算内确定性 fault；正常 winner 必须推进 consumption、generation、watermark 或状态。

### SPA-1：同 T contact 不被整刻水位吞没

同一 Passage 在 T 有多个 contact 时，每个 contact 依次成为独立 winner并触发 re-Forecast；处理第一个 contact 不能通过 `SettledThrough = T` 隐藏其余未消费 contact。WorldState 中的 `ConsumedContactKey(PassageId, normalized segment pair, generations, exact instant)` 可 Replay 地防止同一 contact 重复；V1 不使用整刻/prefix cursor 替代它。

### CAP-1：容量

候选数、同刻 occurrence 数、transition Fact 数或 Journal bytes 超限时，必须在 commit 前确定性拒绝或停止。

### REF-1：reference semantics

任何增量 Forecast 优化都与 full re-Forecast reference 在随机生成场景中产生相同 winner、CommittedTransition 和 Journal。

---

## 15. 仍成立的设计优势与边界

本重构保留原方案的核心价值：

- 稀疏事件和小说式时间压缩；
- Activity 作为持续行为；
- Forecast 与领域主体认知隔离；
- Human/AI 共享 Decision 语义；
- AI latency 不污染 ModelTime；
- Human trajectory 可记录为 imitation learning 样本；
- 逻辑导航与 Godot 表现解耦；
- Forecast queue 与 Journal 分离；
- 单线程、正确性优先、可解释的第一版实现。

它依然不适合未经抽象的高频连续物理混沌、海量强耦合碰撞和 twitch gameplay。战斗、潜行、调查与社交应主动设计为可预测 Flow 与稀疏 Occurrence，而不是迫使 Kernel 模拟每一帧。

---

## 结语

DramaBoard 的时间内核不再问：

> 这一时刻有哪些 internal、external、direct 或 derived 事件应该一起处理？

它只反复问一个问题：

> **在当前已提交的因果前沿上，所有来源共同预测出的下一个原子 occurrence 是谁？**

答案永远只有一个。

```text
Continuous Lazy Evolution
→ Forecast global next occurrence
→ deterministic collapse
→ ordinary winner: atomic global commit
→ winner requiring resolution: await resolver inside the same Step, then atomic commit
→ full re-Forecast
→ repeat
```

`ModelTime` 表达世界流过了多久；`CausalOrdinal` 表达在该时间上已经发生了多少个不可逆原因。二者共同构成严格的 `LogicalInstant`，从模型上消除“同时事件”的因果歧义。

Forecast 负责看见候选未来，PRF 只在真正同位时选择顺序，AtomicTransition 负责让世界不可分割地改变，Journal 负责保存已经发生的唯一历史。若 winner 声明 ResolutionContract，同一个未完成 Step 会等待领域中立 resolver 补全并验证 resolution，再由同一个 winner 原子改变世界；等待本身没有世界内时间或状态。DecisionPoint 只是这种机制的一个领域用例。
