# Build Log 0021：StateJournal-native OOP vertical probe

> 状态：**Phase 1 research charter preserved；Phase 2 graph-authority probe accepted；尚未裁决 production cutover**
>
> 首次记录：2026-08-24
>
> Phase 2 裁决：2026-08-25
>
> 初始代码与文档基线：`6433945 docs(content): reconcile content and save refactor plan`
>
> 关联既有方案：[Build Log 0004](0004-game-content-and-composite-save.md)、[Build Log 0014](0014-journal-neutral-capture.md)–[0020](0020-runner-resume-successor.md)
>
> 实验结果：[Build Log 0022](0022-statejournal-native-objective-probe-results.md)
>
> Atelia 使用契约：[`StateJournal usage-guide`](../../../Atelia-org/atelia/docs/StateJournal/usage-guide.md)

本文件保存 StateJournal-native 研究的时间顺序、当前裁决与实验边界。Phase 1 的 rebuildable ledger 实验是真实发生过的研究，不因 Phase 2 改变方向而被改写；但其中“ledger 是 semantic authority、graph 是 materialized view”的结论已经被后续裁决取代。

本文件不授权通过字符串替换修改 0004/0014–0020，也不宣称 production `FirstBoard`、`Spatial`、Runner 或 Save 已经迁移到 StateJournal。

## 1. 为什么重开持久化裁决

现行 0004/0014–0020 选择：

```text
immutable POCO World
+ authoritative JournalBatch history
+ terminal EventJournal export
+ full Objective replay
+ independent Player checkpoints
```

Phase 1 重新考虑的候选不是“用 StateJournal 替换 EventJournal adapter”，而是改变 Runtime state model 与 mutation authority：

```text
DurableXXX graph 是 Runtime 持久状态骨架
+ 薄 OOP wrapper 提供领域 API
+ semantic ledger 与 materialized graph 同 commit
+ checkout HEAD 直接恢复完整状态
```

Phase 1 的窄 probe 证明了 graph/ledger co-commit、normal reopen、one-tail rebuild 与 fail-stop mechanics，也暴露出 event codec、projector、derived frontier 和 rebuild proof 的真实 ceremony。随后用户明确降低 semantic-history 需求：快速原型阶段更重视 normal resume、fork 与 rewind，不要求从完整 event history 重建状态。

Phase 2 因而采用更小的模型：

```text
canonical durable graph + directly persisted frontier
+ StateJournal physical commit history
+ optional lossy per-root summary
+ exact JournalBatch only for the current in-memory transition
```

这不是“event-sourcing 仍然存在，只是少记几个 event”。DramaBoard V1 probe 明确放弃 rebuildable semantic history；若未来重新需要审计、event-level query 或替换 projector 后重算历史，必须以新的真实需求重新引入相应成本。

## 2. 当前用户决定与产品边界

已经明确：

- 不以 persistence 性能为主要裁决依据；DramaBoard 当前规模下两条路线都足够快。
- 更重视能力完整性、日常编码复杂度和心智模型是否容易理解。
- 可以让领域 wrapper 私有持有 `_data: DurableDict/Deque/...`；不要求保留 POCO snapshot 或 reconciliation layer。
- StateJournal、EventJournal 都是自有代码；允许修改 API，没有已发布兼容包袱。
- 当前仍是可信、本地、单进程、single Authority writer。
- 可以接受 transaction 期间的 mutable working Revision 不对外可见。
- 可以接受 apply / validate / commit 发生异常后整个 Session fail-stop，dispose 并从 durable HEAD reopen；不要求在同一 Revision 上 rollback 后继续。
- committed durable Objective/Player graph 与直接持久化的 frontier 是状态与版本 authority。
- exact ordered `JournalBatch<TFact>` 是当前 transaction 的内存契约，也是 live Presentation 的输入；它不进入永久 ledger。
- 每个 committed historical root 可以保存一条 lossy summary。summary 只供人类浏览、诊断或 UI 使用，不参与恢复、frontier 推导、去重或 outcome 裁决。
- fork/rewind 的 V1 物理原语是在同一个 StateJournal repository 中从精确 historical `CommitAddress` 创建 branch/ref；不为每个分支复制整个 repository。
- Performance、lazy load、compaction、通用 query language、merge 和多 writer 不进入当前 probe。

仍然延后：

- production cutover，以及 0004/0014–0020 的整体重裁；
- 完整 FirstBoard OOP wrapper 的 schema pressure；
- Player Memory/previous-known-facts 的 durable closure；
- Presentation resume/fork baseline 与 commit 后 cue 丢失边界；
- 沿 commit 父链只读取 meta info、而不 materialize historical roots 的 fast path；
- portable/self-contained Save、export、ancestor retention、GC/repack 与 strict verifier。

## 3. 已核实的代码事实

### 3.1 DramaBoard

- `JournalBatch<TFact>` 保存一个 occurrence 的 `LogicalInstant + CandidateKey + ordered non-empty Facts[]`；facts order 是 authoritative apply order。
- 当前 `SimulationKernel` 使用 immutable scratch fold：逐 fact 得到 scratch World、batch-end Validate、Journal publication 成功后才安装 World。
- `Kernel.World` 当前是可公开持有的 immutable snapshot；StateJournal direct-wrapper 若 production cutover，会有意放弃这项 same-instance promise。
- Presentation 不并发读取 Authority current World；它消费 committed batch channel，并维护自己的 projection/read model。
- LLM Player 会在 Objective publication 前改变 Memory 与 previous-known-facts；Pipelined maintenance 还能跨 decision return 继续运行。

### 3.2 StateJournal 与当前依赖边界

- StateJournal 不是任意 POCO serializer，只持久化 Revision-owned Durable containers 与受支持 scalar。
- `Repository.Commit(root)` 从 root 做 reachability walk，写 dirty object delta/rebase，最后用 branch ref 发布 HEAD。
- `CheckoutBranch` 直接 materialize HEAD 完整对象图；当前为 eager full load。
- commit parent、historical root、`CreateBranch(fromCommit)`、branch ref/reflog 与 effective-history traversal 已存在。
- `Repository.Commit(root, note)` 的 note 属 branch ref/reflog，不是 immutable commit-level domain payload；Phase 2 summary 因而存在 historical root 内。
- StateJournal `LocalId`、branch name 与 `CommitAddress` 是物理身份，不得替代 ActorId、EntityId、business lineage 或 `WorldVersion`。

DramaBoard 当前 local project reference 对应 Atelia sibling `main@7e56aa37`。用户对三个 Phase 1 API friction 的修复已在 `origin/feature/derived-recap-grid-rewrite@e21fc61a` 由源码和 StateJournal tests 验证：typed `ByteString` scalar collections、Repository-owned object lifetime invalidation、结构化 commit publication outcome。它们尚未成为 DramaBoard 的 repository-controlled dependency pin，因此默认 build仍使用旧 API shape。

Phase 2没有强依赖`RepositoryCommitError` CLR类型，而是消费基类`AteliaError`的stable code/details。相同DramaBoard focused tests已在当前main fallback与detached exact `e21fc61a` worktree各跑 **9/9**：e21路径确实取得candidate，并断言`FailurePhase=AppendReflog`、`PublicationState=Published`。这证明兼容路径可执行；仍不能把临时override冒充正式dependency pin。

### 3.3 EventJournal 对照证据

- EventJournal 与原 JournalBatch authority 同构，并已有 strict read-only、parent chain 与 ForwardPlan。
- Phase 1 的 Release 量级实验表明，当前规模的 replay 并非已证明的性能瓶颈；Phase 2 放弃 event-sourcing 是为了缩小 authority/codec/projector 复杂度，而不是为了修复一个已证实的性能故障。
- EventJournal 的这些能力仍可服务未来 semantic-history 需求，但不再是 StateJournal-native V1 probe 的状态恢复前提。

## 4. 当前 StateJournal-native authority model

### 4.1 Root

Phase 2 probe 的 schema `/2` 形状是：

```text
StateJournal GraphRoot
  schemaId
  definitionSha256
  rulesetId
  worldSeed

  lineageId
  transitionCount
  parentWorldVersion?       # child lineage 的精确业务来源
  lastTransition?
    logicalInstant
    causeKey

  commitKind                # genesis | lineage-start | objective-transition | metadata-only
  commitSummary?            # lossy；读取时允许缺失

  objectiveWorld
    game
    spatial

  playerStatesByActor?      # production 候选；当前 probe 尚未实现
```

`lineageId + transitionCount + lastTransition` 直接存在 graph 中，不从摘要或 event history 派生。`parentWorldVersion` 是 business lineage provenance；它不等于 StateJournal commit parent。

每个 writer commit 写入描述“本次 commit”的 `commitKind` 与 summary。历史 StateJournal root 保留该时刻的字段值，所以遍历 physical commit parent chain并加载各 historical root，可以得到一条与 commit 对齐的摘要序列。当前 helper 会 materialize historical roots；“只反序列化 meta info”的优化尚未验证，也不是 correctness 前提。

### 4.2 唯一 authority 分工

```text
committed Durable objective/player graph + persisted frontier
    = 状态恢复、WorldVersion、fork/rewind 的 canonical authority

StateJournal commit parent + branch/ref
    = physical version graph

per-root commitSummary
    = optional lossy description；不是 reducer input

in-memory exact JournalBatch
    = 当前 Objective transition 与 live Presentation contract

StateJournal object delta/rebase
    = storage implementation detail
```

summary 缺失时必须仍能 reopen 和继续运行。不能从 summary 推导状态、frontier、commit identity 或重放外部 effect；summary 文案和结构将来可以演进或被截断。

### 4.3 Mutation authority

禁止：

```text
任意 caller 直接 mutate durable graph
→ 事后用 summary 猜测发生了什么
```

唯一合法路径：

```text
Forecast / Plan against committed facade
→ construct exact ordered JournalBatch in memory
→ internal OOP wrapper mutators
→ batch-end Validate complete graph
→ advance persisted frontier
→ write commitKind + lossy summary
→ one Repository.Commit(root)
→ resolve physical outcome
→ publish the original in-memory batch to live Presentation
```

OOP wrapper 对业务侧只暴露 query、command validation 或 transaction-draft generation；raw Durable containers 和 unrestricted setter 不出 Ruleset state assembly。

### 4.4 Phase 1 模型为什么被取代

Phase 1（`d543307`）曾选择：

```text
canonical transaction ledger = semantic authority
committed durable graph       = mandatory materialized view
frontier                       = strict-scanned ledger derivative
```

它用完整 `JournalBatchV1` canonical codec、private projector 与 one-tail rebuild 证明了最小 event-sourcing consistency。该证据仍记录在 [0022](0022-statejournal-native-objective-probe-results.md)，但不再是当前模型：normal resume 不需要 replay，而为了让 Player、随机数、LLM、Human 与 tool outcomes 都可重建，仍需支付 closed event schema、codec、projector evolution 和 completeness proof。当前产品需求不足以证明这些成本必要。

## 5. Commit 与 failure law

### 5.1 Happy path

```text
Forecast / Plan 只读 committed facade
→ Player accepted effects 已固定；首版不允许后台 maintenance 越界
→ 形成 exact in-memory ordered batch
→ 按 Facts[] 顺序 mutate Objective wrappers
→ Validate complete Objective + Player frontier
→ update direct frontier + commit metadata/summary
→ Repository.Commit(root)
→ resolve committed HEAD
→ invalidate old read facade
→ publish exact in-memory batch to Presentation
```

一个 ordered complete batch 仍是唯一 Objective transition。summary 不是 batch，也不能替代 batch boundary、facts order 或被覆盖的瞬时 presentation evidence。

### 5.2 Apply / validation / cancellation failure

V1 不建设 deep draft/rollback：

```text
任何 post-Plan working mutation failure
→ 不 publish batch
→ poison Session 与全部 wrapper/read facade
→ dispose Repository
→ reopen branch HEAD
```

未被 branch HEAD 选择的 dirty memory / orphan data 不是 committed state。StateJournal lifetime 修复 pin 进来后，Repository-owned DurableObject 自己也必须 fail-fast；Session poison guard 仍保留明确的领域边界。

### 5.3 Commit outcome unknown

当前 DramaBoard 仍编译于 Atelia `main@7e56aa37` 的 generic failure API。Phase 2 在每次 Objective 或 lineage-start commit 前都捕获完整 authoritative parent/child snapshot；其中包含 schema/Definition/Ruleset binding、WorldVersion、`ParentWorldVersion`、last instant/cause、commit kind，以及完整的 probe Game/Spatial state，但有意排除 lossy summary。

probe 同时认识 e21 的稳定 `AteliaError.ErrorCode/Details` 协议：若 error code 为 `SJ.Repository.CommitFailed`，便解析并验证 expected/candidate address、failure phase 与 publication state；当前 main 返回 generic `SJ.Repository` error 时，这些结构化字段为空，resolver 使用兼容 fallback：

```text
Repository.Commit 返回 failure/throw
→ 禁止重试 Player/backend
→ dispose + reopen branch
→ HEAD == expected physical parent
     且完整 authority == captured parent
     => not committed
→ 若有 reported candidate，HEAD 必须等于 candidate
→ HEAD parent == expected physical parent
     且完整 authority == captured child
     => committed
→ 其他
     => corruption / irreconcilable fault
```

当前裁决不再比较 ledger tail、canonical bytes、digest或summary。当前 main fallback仍会核对 immediate physical parent edge与完整 authority state；exact `e21fc61a`验证已证明同一代码还会强制 reopened HEAD 等于stable-details报告的candidate。无论publication state为何，structured metadata都不能替代reopen验证，也不得blind retry。

只有未来出现 delayed reconciliation 或 cross-process external-effect dedupe 的真实需求时，才考虑在 graph 中增加窄的 stable operation ID；这不要求恢复完整 semantic ledger。

### 5.4 保留与放弃的 promises

若 production 采纳 direct mutable model，以下不是继续暗中保留的能力：

- `Kernel.World` 不再是可永久持有的 immutable snapshot。
- transaction 进行中没有 concurrent reader 能读取 Authority graph。
- apply / validation failure 后不能继续使用同一个 Kernel/Revision。
- 旧 wrapper 引用在成功 commit 或 failure poison 后失效。
- same-instance retry、speculation、nested transaction 与 partial rollback 不在 V1。
- 状态不能由永久 event ledger 重建；旧 Presentation cues 不从 summary replay。

以下继续保留：

- single winner；
- 一个 ordered complete batch 是唯一 Objective transition；
- batch 中间态不可观察；
- branch HEAD 只能是 parent 或完整 child；
- Presentation 只收到已 resolve committed 的 exact live batch；
- normal restore 不调用 backend 或 event replay；
- historical root 可以作为精确 fork/rewind 状态来源。

## 6. Player 与 Presentation

### 6.1 Player

```text
LlmPlayerDriver
  _state   = DurableLlmPlayerState        # in graph
  _backend = runtime-only service         # never persisted
```

凡是会影响下一次决策的 Memory shard contents、previous-known-facts 与必要 frontier，都必须与 Objective co-commit到 canonical graph。CharacterCard/material/schema 来自 Definition；backend/model/effort 等 non-secret config 属 closed composition；credential、client、Task、CancellationToken、trace 和 profiler 不进入 graph。

Blocking/commit 前 flush 仍是首版候选。Pipelined maintenance 只能成为独立显式 Player transaction，或在下一 Objective transaction 前 join；不得让 background Task 在 commit 后继续 mutate 同一个 Revision。

当前 Phase 2 probe 没有实现或证明 Player durable closure。

### 6.2 Presentation

Presentation 不持有 Authority durable wrapper。live session 只在 Objective commit outcome 已确认后收到该次 exact in-memory batch，并继续依靠 batch boundary、facts order 与 pre/post evidence产生新 cues。

resume/fork 不再 replay旧 semantic prefix。它必须从选中 historical root 的 durable current state 建立 baseline，然后只消费新 suffix batches。由此明确接受一个尚待专项验证的降级：若进程在 state commit 后、live cue publication 前崩溃，普通 reopen 不会从 summary 补播该 cue。若未来不能接受，应引入窄的 durable Presentation outbox，而不是恢复完整 Objective event-sourcing。

## 7. Same-repository fork / rewind

V1 的 fork/rewind protocol 是：

```text
选择 exact historical CommitAddress
→ CreateBranch(newBranch, fromCommit) in the same repository
→ checkout new branch
→ 立即 co-commit lineage-start root
     fresh business lineageId
     transitionCount = selected source transitionCount N
     ParentWorldVersion = selected source WorldVersion
     commitKind = lineage-start
     one lossy summary
→ 提交独立 suffix Objective transactions
```

rewind 是从旧 commit 创建新 branch，不是破坏性地向后移动 source branch。两次从同一 historical state fork 必须分配不同 business lineage；branch name、`LocalId` 与 `CommitAddress` 都不冒充 `WorldVersion`。

lineage-start只改变business lineage/provenance/commit metadata；它不增加Objective transition count，也不清空选中root的last instant、cause或domain graph。Phase 2既验证了Genesis `(L0,0) → (L1,0)` fork，也验证了从nonzero `(L0,1)` fork得到`(L1,1)`并保留完整状态。

同一 repository 的双 sibling 测试中，main 先提交 deadline；两个 branch 都从精确 Genesis `CommitAddress` 创建，各自立即提交 lineage boundary 和独立 deadline suffix；main ref 不移动；每条 effective history 都是：

```text
Genesis → lineage-start → objective-transition
```

这种方式共享已有 repository 历史，避免把“复制整个 repository”设为正常 fork/rewind primitive。本 probe 不声称 branch 创建严格 O(1)，也不以性能性质作为 correctness 结论。

portable/self-contained Save 是另一项需求。若将来需要把某条 branch 导出到独立介质，必须另行定义 ancestor retention、segment closure、GC/repack、manifest 与 strict verifier；不能从 same-repo branching 自动推出这些能力。

## 8. 专项实验与 disposition

### 8.1 Phase 1：rebuildable ledger（历史证据）

Phase 1 位于 commits `d543307` / `8f40a89`，其 ledger/codec/rebuild 代码后来被 Phase 2 refactor 删除，但实验结果不被改写：

| ID | Phase 1 disposition | 历史证据 |
|---|---|---|
| SJ-V1 | **passed** | private Session + query-only facade 隐藏 raw Durable containers。 |
| SJ-V2 | **passed，frontier derived** | 一个 canonical ledger envelope 与一个 StateJournal child commit。 |
| SJ-V3 | **passed** | Game + Spatial facts exact order apply；prefix failure 后 reopen parent。 |
| SJ-V4 | **passed** | close/reopen直接 query graph，不执行 replay。 |
| SJ-V5 | **passed（one-tail）** | Genesis + canonical tail rebuild 与 source graph exact。 |
| SJ-V6 | **passed** | working/validation failure poison Session；reopen exact parent。 |
| SJ-V7 | **passed，旧 identity law** | physical parent + canonical bytes/digest + post-state裁决 ambiguous child。 |
| SJ-V8 | **deferred** | historical sibling business lineage 未在 Phase 1 验证。 |

Phase 1 证明“可以这样做”，没有证明“DramaBoard 必须这样做”。SJ-V2、SJ-V5 与 SJ-V7 的 ledger-specific law 已被 Phase 2 的 graph-authority model取代。

### 8.2 Phase 2：graph authority（当前可执行证据）

当前 test-only refactor 验证：

| ID | Phase 2 disposition | 当前证据 |
|---|---|---|
| SJ-G1 | **passed** | schema `/2` 同 commit保存 Objective graph、direct frontier、commit kind 与 summary；root 不含 ledger。 |
| SJ-G2 | **passed** | exact `JournalBatch` 只存在于内存；代码中没有 envelope codec、digest、rebuild 或 replay path。 |
| SJ-G3 | **passed** | close/reopen直接取得 exact graph/frontier/summary；test-only `metadata-only` commit移除summary后仍可reopen。 |
| SJ-G4 | **passed** | first-fact 与 batch-end validation failure均 fail-stop；reopen exact parent。 |
| SJ-G5 | **passed（双 API shape）** | Objective与lineage-start的reflog ambiguity都保存完整parent/child authority；当前main以physical parent edge + exact authority fallback裁决；exact e21 override也以9/9证明stable candidate/phase/publication details路径。lineage commit明确NotPublished时，既有branch可在重新核对exact parent后安全完成同一boundary。 |
| SJ-G6 | **passed** | 同一 repo 从 exact Genesis commit创建两个 fresh-lineage branches；main不移动，siblings独立；从nonzero frontier fork也保留count、instant/cause与完整domain graph。 |
| SJ-G7 | **partial** | effective history得到每个 historical root 的 summary，但当前 helper会 materialize root；meta-only fast path未证明。 |
| SJ-G8 | **deferred** | Player durable closure、Presentation baseline、portable Save/export 与 production integration。 |

当前main的focused StateJournalNative tests为 **9/9 passed**，完整`FirstBoard.Persistence.Tests`为 **19/19 passed**；exact `e21fc61a` override的focused tests也为 **9/9 passed**。最终`DramaBoard.Local.slnx`为 **481/481 passed**。

## 9. 当前裁决与剩余问题

当前裁决是：

```text
StateJournal-native graph authority
+ direct persisted frontier
+ same-repository branch/ref fork/rewind
+ optional lossy per-root summary
+ ephemeral exact live batches
= leading test-only direction
```

已经解决：

- rebuildable ledger不是 DramaBoard 快速原型的 V1 requirement；
- ledger codec、projector、derived frontier 与 rebuild proof 可以从当前 probe删除；
- same-repo historical branch 是 fork/rewind 的 V1 物理原语；
- Phase 1 的三个 StateJournal API friction 已在上游 `e21fc61a` 修复；Phase 2的stable commit-error details路径已用exact e21 override执行验证，剩余前置是正式dependency pin以及更宽typed API/lifetime integration。

仍需实证回答：

1. 完整 FirstBoard OOP wrapper 是否比 immutable POCO reducer更少 ceremony？
2. order-sensitive、多实体引用的 batch是否仍清楚？
3. Player Memory/previous-known-facts 与 Objective同 transaction是否易用？
4. Presentation baseline与commit/cue crash gap采用何种最小契约？
5. meta-only history enumeration是否值得成为 StateJournal public API？
6. portable Save如何保留所需 ancestors而不复制无关 branches？
7. production cutover时，0004/0014–0020 应如何整体重裁，避免两套 authority？

## 10. 长期新存储研究假说

用户提出的远期系统可概括为：

```text
OOP object database
+ event-is-transaction
+ append-only semantic history
+ state-is-materialized-view
+ rebuildable indexes/checkpoints
+ branch/version management
```

其中 semantic event 与 mechanical write set可以属于同一个 immutable transaction authority；object graph、indexes 和 checkpoints 绑定 source transaction frontier，并允许删除重建。StateJournal 已积累 object identity、version chain、delta/rebase、root、branch、segment 与 recovery 经验；EventJournal 已积累 immutable payload、parent chain、read-only traversal、ref 与 forward-plan 经验。

真正的研究难点仍包括 event completeness、external effects、codec/projector/object-schema compatibility、history retention、privacy/redaction、indexes 与 snapshot isolation。这条路线概念自洽，但它是独立存储研究项目，不是 DramaBoard V1 被删掉的 ledger 换个名字重新回来。

## 11. 上下文恢复索引

压缩或新会话后，建议按以下顺序恢复：

1. 本文件 §2、§4–§7：当前 authority、failure、Player/Presentation 与 fork/rewind law。
2. 本文件 §8 与 [Build Log 0022](0022-statejournal-native-objective-probe-results.md)：两阶段实验结果。
3. [`StateJournal usage-guide`](../../../Atelia-org/atelia/docs/StateJournal/usage-guide.md)：当前 public API。
4. `tests/FirstBoard.Persistence.Tests/StateJournalNative/DeadlineProbeStore.cs` 与 `StateJournalNativeDeadlineProbeTests.cs`：Phase 2 executable evidence。
5. `E:/repos/Atelia-org/atelia/src/StateJournal/Repository.cs`、`Revision.Commit.cs`、`Repository.BranchRefs.cs`：commit/ref/history outcome。
6. [Build Log 0004](0004-game-content-and-composite-save.md) 与 0014–0020：尚未在本次任务中重写的 production plan。

最近收敛的团队裁决：

- direct StateJournal wrapper 是下一阶段 leading test-only direction，尚非 production cutover；
- canonical graph/frontier是状态 authority，summary不是；
- exact batch只保留为当前 transaction/live Presentation contract；
- normal fork/rewind使用同一 repository 的 branch/ref，不复制整个 repository；
- Player closure、Presentation baseline、portable Save与meta-only history仍是下一层边界；
- 先 pin/integrate 已验证的 StateJournal API 修复，再让 DramaBoard 使用结构化 commit outcome。
