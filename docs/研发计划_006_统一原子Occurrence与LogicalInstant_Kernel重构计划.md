# 研发计划 006：统一原子 Occurrence 与 LogicalInstant Kernel 重构

**状态：经复杂度审查收敛后的目标方案与剩余实施计划**

**日期：2026-08-21**

**关联核心设计：`开放世界棋盘游戏设计_003_Forecast_Elapse_Decide_SimulationKernel.md`**

---

## 1. 已冻结的目标

Kernel 的完整职责收敛为：

```text
冻结 committed world + WorldVersion + journal head
→ 所有 rule 全量 Forecast
→ 按 CandidateDue、确定性 keyed hash、CandidateKey 选唯一 winner
→ winner 所属的可信 rule 在同一未完成 Step 中生成完整 TransitionDraft
→ scratch-fold + invariant validation
→ AppendBatch 单帧原子提交
→ 安装新 world
→ 下次 Step 全量 re-Forecast
```

本计划冻结以下决定：

1. 删除 internal event、external input、Player action 的特殊调度路径。小球碰撞、票据到期、Player 决定和规则触发，在 Kernel 看来都是候选原子 occurrence。
2. 每轮所有 rule 基于同一个 committed world 全量 Forecast；每次只选择一个 winner；每次成功提交后废弃所有旧候选并重新 Forecast。
3. `LogicalInstant = (ModelTime, CausalOrdinal)` 只描述已经提交的严格因果顺序。`CausalOrdinal` 不参与候选排序，也不是候选创建序号。
4. `ModelTime` 的权威粒度固定为 1ms。连续方程算出的 sub-ms 时刻一律向上量化到首个不早于它的整数 tick；同 tick 内不存在更精细的权威物理先后，只使用确定性伪随机仲裁。
5. 同一 lineage 同时至多有一个未完成的 `Step`。winner 的规划尚未返回时，不得启动另一轮 Forecast 或提交另一项变化。
6. winner 可以产生多个 facts；它们作为一个 transition 全成或全败，数组下标就是 fact 顺序，不再持久化 `FactOrdinal`。
7. Kernel 完全删除 resolution 类型和 resolution 阶段。可信领域 rule 的 `PlanSelectedAsync` 负责在 Kernel 外调用 Player、校验 proposal、执行领域规划，最终只向 Kernel 返回完整 `TransitionDraft`。
8. Player、Human、AI、LLM、Protocol、重试和费用都不属于 Kernel 概念。Player 是跟随 Kernel 调度的策略函数，不是实时异步输入源。
9. `WorldVersion` 只有 `(LineageId, TransitionCount)`。Journal adapter 私有持有 opaque expected-head/CAS token，不把存储地址或 hash 提升为世界身份。
10. 继续使用已经实现的 `IJournalSink.AppendBatch` 原子发布边界：一个非空 batch 就是一个 transition，并由 Atelia 写成一个 EventJournal Event / RBF Frame。不再另造 `AppendTransition`、Begin/End 协议或第二套事务抽象。
11. 删除应用层 hash 链和重型 audited replay。普通 Replay 重建世界；同 build 的 scheduler conformance test 负责重算 winner。
12. 原型阶段不存在需要保留的旧 Journal 数据；不实现旧格式版本门、只读打开、转换或迁移。格式变化后直接丢弃开发数据并重建。
13. 当前不存在通用跨域 transaction coordinator 的需求。首个真实 Game + Spatial 组合由一个 composite `HostWorld`、一个 draft 和一次 `AppendBatch` 解决。

`src/FirstBoard`、`src/FirstBoard.Demo`、`src/Spatial` 与这些法则冲突时，为新法则让路，不要求 Kernel 保留双轨兼容。

---

## 2. Kernel 边界

### 2.1 最小概念

| 概念 | 含义 |
|---|---|
| `HostWorld` | Kernel 当前唯一的 committed world；可以是多个领域状态的不可变组合。 |
| `WorldVersion` | `(LineageId, TransitionCount)`；每成功提交一个 transition，计数加一。 |
| `LogicalInstant` | `(ModelTime, CausalOrdinal)`；标识一个已经提交的 occurrence。 |
| `CandidateDue` | 候选的整数 `ModelTime` tick，单位和权威粒度均为 1ms。 |
| `CandidateKey` | rule 从世界状态推导的完整、稳定、可规范编码的候选身份，也是 hash 碰撞时的最终兜底键。 |
| `OccurrenceCandidate` | 当前 Forecast 中的临时值：`CandidateKey + CandidateDue + rule-private immutable data`。不持久化。 |
| `TransitionDraft` | winner 对完整 `HostWorld` 的非空 facts 列表。没有 commit 权。 |
| transition | 一次成功的 `AppendBatch`；不再额外引入一个与 batch 重复的持久化抽象。 |

删除以下重复概念：

- `SourceId / CandidateId / Generation / HandlerId / OccurrenceId`；
- `CommitOrdinal / FactOrdinal`；
- `ForecastBasis` 及其持久化/hash；
- `SelectedWinnerContext / ValidatedOccurrenceResolution / ResolutionContractId / OccurrenceResolutionAudit`；
- Source/Handler 两套接口、registry 和 source-local minimum/frontier。

若 rule 需要区分领域、主体、局部槽位或代际，应把这些内容规范编码进一个 `CandidateKey`。同一轮 Forecast 出现重复 key 是确定性错误。Kernel 只在当前调用栈维护 `CandidateKey → owning rule/candidate` 临时映射；该映射不是 World、Journal 或公开 DTO。

### 2.2 唯一 rule 接口

概念接口为：

```text
IOccurrenceRule
    Forecast(HostWorld world, SimulationRules rules)
        -> zero or more OccurrenceCandidate

    PlanSelectedAsync(
        HostWorld world,
        OccurrenceCandidate winner,
        CancellationToken cancellationToken)
        -> TransitionDraft
```

`IOccurrenceRule` 是 Host 注册的可信、进程内领域代码，不是 capability/security 框架。Kernel 不解释 candidate 的私有数据，也不理解 rule 如何得到 draft。

Forecast 必须是纯函数：不得修改 World、消费 stateful RNG、读取墙钟、依赖集合枚举/线程完成顺序或执行 I/O。`PlanSelectedAsync` 可以在领域边界外等待 Player 策略，但在其返回前 World 和 Journal 均不变化。

`SimulationRules` 只保留当前运行真正需要的不可变规则，例如：

```text
WorldSeed
SchedulerSemanticsVersion
MaxTransitionsPerModelTime
```

运行供应商、模型名、墙钟、timeout、git 状态等 operational manifest 不得进入 Forecast。没有真实消费者前，不增加 rules hash、plan hash 或内容寻址。

### 2.3 Kernel 不拥有 Player 决策过程

被选中的 DecisionPoint 仍只是一个普通 candidate。其 owning rule 可以在 `PlanSelectedAsync` 内：

```text
构造领域 observation
→ 调用 Human / AI / LLM / script strategy
→ 校验 envelope、correlation、affordance 与领域规则
→ 生成完整 TransitionDraft
```

这些步骤的程序集、协议和重试策略都在 Kernel 之外。Kernel 看到的只有“可信 rule 返回了完整 draft”或“本次 Step 失败/取消且零提交”。Player/LLM 永远不能直接构造 facts 或写 Journal。

Alice 与 Bob 同一 `ModelTime` 都可行动时，Kernel 先仲裁其中一个 DecisionPoint。winner 提交后才在新世界重新 Forecast 另一个 Actor；不存在同时收集回答、durable inbox 或按网络返回顺序决定世界的阶段。

---

## 3. 时间、仲裁与原子提交

### 3.1 `CausalOrdinal` 的唯一含义

候选没有 `CausalOrdinal`。winner 选定后，Kernel 根据最后一个 committed instant 提议下一个值：

```text
require winner.Due >= last.ModelTime

if winner.Due > last.ModelTime:
    LogicalInstant = (winner.Due, 0)
else:
    LogicalInstant = (last.ModelTime, last.CausalOrdinal + 1)
```

空 lineage 的首个 winner 使用 `(winner.Due, 0)`。只有 `AppendBatch` 成功后，该值才成为历史。提交失败、取消或进程在发布前崩溃都不占用 ordinal。

`TransitionCount` 已经表达 lineage-wide commit 顺序，所以不再保存 `CommitOrdinal`。同一 transition 内 facts 的数组下标已经表达 reducer/序列化顺序，所以不再保存 `FactOrdinal`。

### 3.2 最终全局比较器

```text
CandidateDue = integer ModelTime tick

compare(candidate):
    1. CandidateDue
    2. KeyedHash(WorldSeed, CandidateDue, CandidateKey)
    3. CandidateKey canonical bytes
```

只有 `CandidateDue` 完全相等才计算确定性伪随机 rank。使用标准 keyed hash，不自制 LCG；固定算法、字节序和 canonical codec，并以 golden vectors、候选枚举置换测试和碰撞兜底测试锁定行为。不设“统计公平率必须落入某阈值”的 P0 门槛。

连续规则若算出 sub-ms 精确时刻，必须先执行统一量化：

```text
CandidateDue = CeilToModelTick(exactTime, 1ms)
```

因此 occurrence 永远不会早于连续方程给出的时刻发生，但落入同一 tick 的数学先后会被有意丢弃。Kernel 不接收 `OrderTime`，也不维护 `OrderFrontier`。

### 3.3 规范 Step

```text
Step():
    require no other Step is in flight

    frozenWorld   = committedWorld
    expectedWorld = WorldVersion
    // journal adapter privately freezes its expected head for this Step

    candidates, owners = ForecastAllRules(frozenWorld, simulationRules)
    RejectDuplicateCandidateKeys(candidates)

    if candidates is empty:
        return Exhausted

    winner = SelectUniqueMinimum(candidates)
    draft  = await owners[winner.CandidateKey]
        .PlanSelectedAsync(frozenWorld, winner, cancellationToken)

    require draft.Facts is non-empty
    scratchWorld = ApplyFactsPure(frozenWorld, draft.Facts)
    ValidateHostWorld(scratchWorld)

    instant = ProposeLogicalInstant(winner.Due)
    batch   = StampFacts(draft.Facts, instant, winner.CandidateKey, winner.Due)

    journal.AppendBatch(batch)  // adapter 以私有 expected-head 做 CAS；单帧发布
    committedWorld = scratchWorld
    WorldVersion = (LineageId, expectedWorld.TransitionCount + 1)
    AdvanceCursor(instant)
    return Committed
```

expected head 始终是 adapter 私有并发控制信息，不进入 Kernel DTO 或公共 `WorldVersion`。当前 `IJournalSink.AppendBatch` 的具体签名可以在实现切片中适配，但不能因此把存储地址变成领域版本。

每次 Step 只有三类结果：

- `Committed`：一个非空 transition 已完整发布并安装；
- `Exhausted`：当前世界没有候选；
- 失败/取消：World、Journal、WorldVersion、LogicalInstant 全部不变。

### 3.4 非法 proposal 与戏剧性失败

两类失败不能混成一种 occurrence：

- malformed proposal、错误 correlation、违反当前 affordance：owning rule 在同一未完成 Step 内重问或失败；零提交；
- 合法行动在世界内产生失败，例如攻击落空、谈判失败：rule 返回描述该结果的非空 facts，提交正式 failure transition。

Kernel 不定义通用 `RejectedOccurrence`，也不要求所有非法结果留下 Journal 记录。若一个规则会稳定地返回零 draft 而不改变其候选条件，这是规则错误，不是可提交的 occurrence。

### 3.5 跨域原子性

一个 rule 可以针对 composite `HostWorld` 规划多域 facts，例如：

```text
TicketConsumed
TraversalStarted
```

Kernel 对完整 facts 列表做 scratch-fold 和最终 invariant validation；任一领域失败则不调用 `AppendBatch`。这已经提供当前需要的跨域原子性，不建设通用 transaction coordinator、两阶段提交或跨服务协议。

---

## 4. 正式时间法则：世界在 1ms 粒度离散

DramaBoard 是追求灵活性与多样性的快速原型，角色行动主要以秒或分钟计。文学叙事几乎不会依赖优于毫秒的时间精度；为这种不可感知的差异引入有理数时间、额外 frontier 和跨程序集 codec，收益不足以抵偿复杂度。

因此正式采用以下产品法则：

- `ModelTime` 的最小权威单位是 1ms；
- occurrence 的连续数学时刻向上量化到首个不早于它的整数 tick；
- 同一 tick 内没有可供规则查询或预测的 sub-ms 权威先后；
- 同 tick candidates 由 `KeyedHash(WorldSeed, CandidateDue, CandidateKey)` 决定顺序；
- 不同 WorldSeed 可以产生不同顺序，同一 lineage、seed 和 build 必须完全可重放。

例如两个 contact 的连续解分别为 T+0.1ms 与 T+0.9ms，它们都会量化到 T+1ms，再由 PRF 决定谁先成为原因。数学上较晚的 contact 可能先提交，并可能使另一个 contact 在下一轮 Forecast 中消失。这是被接受的世界语义，不是精度 bug。

“量子化导致随机性”可以作为世界观比喻；工程上它仍是确定性伪随机：单次运行没有不可复现的墙钟随机或竞态。

由此永久删除：

- `OrderTime`、`KnownExactDue`、rational time codec；
- `OrderFrontier` 及其 checkpoint/Fork 状态；
- Spatial 向 Kernel 暴露 sub-ms 排序值的接口；
- 精确 contact 混排、防 sub-ms 倒插及 A/B 双比较器兼容测试。

003、008 及后续实现只能采用这一种法则，不保留 feature flag 或兼容分支。

---

## 5. Journal、Replay 与 Fork

### 5.1 原子 Journal

当前 `AppendBatch` 单帧切片直接作为目标基础：

- 非空 batch 编码为一个 EventJournal Event / 一个 RBF Frame；
- RBF 长度与 CRC 负责单帧完整性；
- active branch ref/CAS 是发布点，CAS 失败的 orphan 不可见；
- 超过单帧上限则整批失败，不拆成 Begin/Part/End；
- World 只在 batch 发布成功后安装 scratch result；
- 一个 `AppendBatch` 成功对应一次 `TransitionCount` 和一次 `CausalOrdinal` 推进。

不再规划 `CommittedTransition` 第二套接口、应用层 `HeadHash / TransitionHash / WorldSnapshotHash`，也不做 content-addressed plan/resolution 保存。若未来出现敌意篡改、跨存储内容寻址或法证消费者，再以真实失败场景另立设计。

### 5.2 Replay

普通 Replay 读取完整 batch，按数组顺序在 scratch world 上 fold，成功后才暴露新 world。它不 Forecast、不调用 Player、不重新仲裁，也不校验旧 build 的规划合法性。

重型 audited replay 降级为同一 build 下的 scheduler conformance test：

```text
从 Genesis 重建 transition 前缀 world
→ 全量 Forecast
→ 重算 comparator winner
→ 与 batch 记录的 CandidateKey / CandidateDue 对照
→ fold batch 后继续
```

它用于发现非 winner 提交、枚举顺序泄漏和 scheduler 退化，不要求持久化每轮 contender set、Player 回答、plan hash 或完整 Forecast basis。普通 Replay 也只面向当前格式；本原型不承诺跨 build 或跨格式读取开发期数据。

### 5.3 Fork

Fork 只允许发生在完整 batch boundary，并继承 World、`WorldVersion`、最后 `LogicalInstant` 与 WorldSeed。Forecast cache 和当前未完成 Step 一律丢弃；没有 sub-ms frontier 需要保存或恢复。

Fork 只服务当前格式和当前运行中的 committed history。Journal 格式或 scheduler semantics 改变时，已有开发数据直接废弃并重新生成；Kernel 与 Journal adapter 均不提供旧格式识别、只读兼容或迁移入口。

---

## 6. 必须保留的进展约束

### 6.1 同 `ModelTime` 活锁预算

规则可能连续产生不推进 `ModelTime` 的合法 transition。Kernel 按 `SimulationRules.MaxTransitionsPerModelTime` 设置确定性上限；超限时以诊断失败停止，不伪造时间前进，也不静默丢 candidate。

### 6.2 Spatial contact 的权威进展状态

一个 contact 提交后，Spatial world 必须发生足以阻止同一 contact 永久复发的权威变化，例如更新 traversal segment generation、关系状态或最小的 consumed-contact state。Journal receipt 不能替代 Forecast 可见的 World 状态。

具体表示由首个 Spatial 垂直切片决定，不预建通用 watermark/index 框架。一个 contact 也不得消费整个时间桶，导致同 tick 的其它 contact 消失；它只消费自己的领域条件，随后全量 re-Forecast。

---

## 7. 明确删除或延期的复杂度

| 旧设计 | 裁决 | 最小替代 |
|---|---|---|
| Source/Handler 两阶段与 registry | 合并 | 一个 `IOccurrenceRule.Forecast + PlanSelectedAsync` |
| Kernel resolution DTO/contract/audit | 删除 | rule 返回完整 `TransitionDraft` |
| Candidate 五套 identity | 合并 | 一个完整 `CandidateKey`，owner 临时保存 |
| source-local minimum/frontier | 延期 | V1 枚举所有 candidates |
| `CommitOrdinal`、持久 `FactOrdinal` | 删除 | `TransitionCount`、facts 数组下标 |
| 三元 `WorldVersion` 与应用 hash 链 | 简化 | `(LineageId, TransitionCount)` + adapter 私有 CAS token |
| Forecast/plan/resolution/receipt hash | 删除 | 当前 Step 栈内值 + 普通 Journal |
| rational `OrderTime` 与 `OrderFrontier` | 删除 | 整数 `CandidateDue`；同 tick 使用确定性 PRF |
| capability attestation、content-addressed resolution、generic outbox | 延期 | 首个真实消费者出现后单独设计 |
| runtime Providence/Admin/Setup P0 | 删除 | Setup 只构造 Genesis；其余不在当前原型范围 |
| 通用跨域 transaction coordinator | 删除 | composite `HostWorld` + 一个 draft + 一次 batch |
| 旧 Journal 兼容（含版本门、只读和转换） | 删除 | 当前无须保留的数据；格式变化后重建 |
| 重型 audited replay | 简化 | 同 build scheduler conformance test |
| 增量 Forecast、heap、kinetic index | 延期 | full Forecast reference；由 profiler 触发优化 |

---

## 8. 剩余实施步骤

每一步都必须形成可运行的垂直切片。已完成的 `AppendBatch` 单帧原子发布不再列为待实施工作。

### 切片 1：实现离散时间与最小值对象

- 同步 003、008 的目标法则：`CandidateDue` 是 1ms 整数 tick，sub-ms 连续解统一向上量化；
- 实现 `LogicalInstant`、二元 `WorldVersion`、`CandidateKey` 和整数 `CandidateDue`；
- 用标准 keyed hash 实现 comparator；
- 加入 golden vectors、重复 key、hash collision fallback 和 candidate 枚举置换测试；
- 加入精确整数时刻不移动、任意 sub-ms 余数向上进入下一 tick 的量化边界测试；
- 删除 `CausalOrdinal` 参与 candidate 排序的任何可能。

退出条件：给定 WorldSeed、时间和 candidates，winner 与创建/注册/线程顺序无关。

### 切片 2：一个 rule、一个 winner、一个 batch

- 引入单一 `IOccurrenceRule`；
- 实现 single-flight `Step`、full Forecast、唯一 winner 和 commit 后 full re-Forecast；
- 实现 pure scratch-fold、最终 HostWorld invariant validation、空 draft 防线和同时间预算；
- 让成功的 `AppendBatch` 一次推进 `TransitionCount`/`CausalOrdinal`；失败时所有状态零变化；
- 先迁移一个无 Player、无 Spatial 的 timer/deadline 垂直案例。

退出条件：新路径能独立运行，且没有第二个 commit authority。

### 切片 3：Player/FirstBoard 垂直迁移

- 用普通 `DecisionPointCandidate` 替换 `DecisionRequested` event、session、external input 与 `PendingAction` 两阶段；
- owning FirstBoard rule 在 `PlanSelectedAsync` 中经 Host 调用 Player/validator，并只返回完整 draft；
- 删除 Kernel 对 Host、Protocol、Player、Human/AI/LLM 类型的引用；
- 验证 Alice/Bob 同 tick 只调用 winner 的策略，提交后 loser 基于新世界重新 Forecast；
- 分别测试 malformed proposal 零提交与合法行动失败正式提交。

退出条件：live `Run(externalInputs)` 和 internal-first decision barrier 不再被正式调用方使用。

### 切片 4：首个 Game + Spatial 原子案例

- 只实现 `TicketConsumed + TraversalStarted` 这一项真实组合；
- 用 composite `HostWorld` scratch-fold 验证任一领域失败均零提交；
- 不创建 coordinator、通用 command envelope 或五类假想跨域案例。

退出条件：一张票的消耗与上船在一个 batch 中全成或全败。

### 切片 5：Spatial 单 contact 迁移

- 拆掉 whole-T `SpatialMoment`、fixed phases 和 command batch 仲裁；
- mutation、arrival、contact 分别成为 candidates，每轮仍只提交一个；
- 将 contact 的连续解按 §4 向上量化到 1ms tick，不向 Kernel 暴露 sub-ms 排序值；
- 以最小权威状态防止 contact 永久复发；
- 同步修订 007、008、009 中冲突的时间语义。

退出条件：一个 contact 不吞掉同 tick 的其它候选，且 re-Forecast 不会无进展复发。

### 切片 6：删除旧路径并补 Replay/Fork

- 删除 external/internal cause 分支、Microstep phase、Decision session、pending/resume 和 subsystem gateway；
- 普通 Replay 按 batch 原子 fold；Fork 只接受 committed boundary；
- 增加同 build scheduler conformance test；
- 用代码搜索和依赖测试确认 Kernel 不认识 Player/Protocol。

退出条件：代码、003 和本计划只剩一套调度与提交语义。

---

## 9. 最小验收矩阵

| ID | 硬断言 |
|---|---|
| ORD-1 | 同 `ModelTime` 的 committed transitions 使用连续 `CausalOrdinal`；一个 batch 只增加一次。 |
| ORD-2 | system 注册、candidate 枚举和并行 Forecast 完成顺序任意置换，winner 与 Journal 结果不变。 |
| TIM-1 | 精确整数 tick 保持不变；任意 `T + δ`（`0 < δ < 1ms`）统一量化为 `T + 1ms`；Kernel API、cursor 与存档均无 sub-ms 排序字段。 |
| ARB-1 | 相同 WorldSeed/state/build 得到相同 winner；keyed-hash 碰撞由完整 `CandidateKey` 稳定兜底。 |
| ARB-2 | 同 tick 顺序只由 WorldSeed、`CandidateDue` 与 `CandidateKey` 决定；测试允许 sub-ms 数学顺序反转。 |
| ATM-1 | 多 facts 和 Game + Spatial facts 全成或全败；任何 append 前失败都不改变 World/Journal/version/instant。 |
| UNI-1 | 正式路径不存在 external input、internal-first、fixed same-time phase 或领域二次 winner selection。 |
| PLN-1 | Kernel 只从 owning rule 接受完整非空 `TransitionDraft`，不存在任何 resolution DTO 或第二 Plan authority。 |
| DEC-1 | 两个 Actor 同 tick 时只调用 winner 的策略；另一 Actor 在 commit 后看到新世界。 |
| FAIL-1 | malformed/unauthorized/stale proposal 零提交；合法的世界内失败提交非空 failure facts。 |
| RPL-1 | 普通 Replay 不 Forecast、不调用 Player，按 batch boundary 重建相同 World、WorldVersion 和 LogicalInstant。 |
| FORK-1 | Fork 只发生于 committed batch boundary；未完成 Step 和 Forecast cache 不可复制。 |
| LIV-1 | 空 draft、重复无进展 candidate 和超预算同时间链确定性失败。 |
| SPL-1 | 已提交 contact 有 Forecast 可见的进展；单 contact 不消费整个 tick。 |

---

## 10. 完成定义

本计划完成时：

1. Kernel 只有 `Forecast all → select one → owning rule plans → scratch validate → AppendBatch` 一条路径；
2. Kernel 项目不引用 Player、Protocol、Human、AI、LLM、resolution 或 validator 类型；
3. `LogicalInstant` 是提交后的因果地址，不是候选排序工具；
4. `WorldVersion`、candidate identity、fact order、Journal 发布边界各有且只有一个表示；
5. 普通 Replay、committed-boundary Fork、同时间活锁预算和 Spatial contact 进展约束通过验收；
6. 旧调度路径已删除，003、008 与实现采用同一套时间法则；
7. 本文已完成的切片从计划中删除，而不是转写成永久的实施历史。

最终 Kernel 只回答并落实一个问题：

> 基于当前 committed world，哪一个 candidate 是唯一的下一原因；它的 owning rule 给出的完整原子变化能否一次提交？
