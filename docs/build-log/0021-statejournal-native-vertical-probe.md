# Build Log 0021：StateJournal-native OOP vertical probe

> 状态：**Phase 1/2 evidence preserved；Phase 3 capability probes complete；尚未裁决 production cutover**
>
> 首次记录：2026-08-24
>
> Phase 2 裁决：2026-08-25
>
> Phase 3 证据：`bd4a6e9`、`3cecbfb`、`605b179`、`94d49d5`
>
> 初始代码与文档基线：`6433945 docs(content): reconcile content and save refactor plan`
>
> 关联既有方案：[Build Log 0004](0004-game-content-and-composite-save.md)、[Build Log 0014](0014-journal-neutral-capture.md)–[0020](0020-runner-resume-successor.md)
>
> 实验结果：[Build Log 0022](0022-statejournal-native-objective-probe-results.md)
>
> Atelia 使用契约：[`StateJournal usage-guide`](../../../Atelia-org/atelia/docs/StateJournal/usage-guide.md)

本文件保存 StateJournal-native 研究的时间顺序、当前裁决与实验边界。Phase 1 的 rebuildable ledger 与 Phase 2 的 deadline graph-authority probe 都是真实发生过的研究，不因 Phase 3 增加 API、Encounter、Player 与 Presentation evidence而被改写；但“ledger 是 semantic authority、graph 是 materialized view”的 Phase 1 结论已经被后续裁决取代。

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
- 一个统一 canonical FirstBoard root/transaction coordinator，而不是继续堆独立 probe roots；
- StateJournal root 与 production Runner/Presentation baseline 的端到端接线；
- 沿 commit 父链只读取 meta info、而不 materialize historical roots 的 fast path；
- portable/self-contained Save、branch closure export、ancestor retention、GC/repack 与 strict verifier；

## 3. 已核实的代码事实

### 3.1 DramaBoard

- `JournalBatch<TFact>` 保存一个 occurrence 的 `LogicalInstant + CandidateKey + ordered non-empty Facts[]`；facts order 是 authoritative apply order。
- 当前 `SimulationKernel` 使用 immutable scratch fold：逐 fact 得到 scratch World、batch-end Validate、Journal publication 成功后才安装 World。
- `Kernel.World` 当前是可公开持有的 immutable snapshot；StateJournal direct-wrapper 若 production cutover，会有意放弃这项 same-instance promise。
- Presentation 不并发读取 Authority current World；它消费 committed batch channel，并维护自己的 projection/read model。
- Presentation loop与coordination现已接受nonzero baseline：初始化时`Committed == Presented == N`，baseline world time与last instant一致，并只消费exact suffix；尚未从StateJournal/Runner自动构造该baseline。
- LLM Player 会在 Objective publication 前改变 Memory 与 previous-known-facts；Pipelined maintenance 还能跨 decision return 继续运行。

### 3.2 StateJournal 与当前依赖边界

- StateJournal 不是任意 POCO serializer，只持久化 Revision-owned Durable containers 与受支持 scalar。
- `Repository.Commit(root)` 从 root 做 reachability walk，写 dirty object delta/rebase，最后用 branch ref 发布 HEAD。
- `CheckoutBranch` 直接 materialize HEAD 完整对象图；当前为 eager full load。
- commit parent、historical root、`CreateBranch(fromCommit)`、branch ref/reflog 与 effective-history traversal 已存在。
- `Repository.Commit(root, note)` 的 note 属 branch ref/reflog，不是 immutable commit-level domain payload；Phase 2 summary 因而存在 historical root 内。
- StateJournal `LocalId`、branch name 与 `CommitAddress` 是物理身份，不得替代 ActorId、EntityId、business lineage 或 `WorldVersion`。

CI 已把 secondary checkout pin到官方 Atelia main `742fcd62e691b6b6acca4113a3ac3638bc7275ba`；`e21fc61a` 是其祖先。DramaBoard current tests因此常规编译并使用typed `ByteString`、Repository-owned lifetime与强类型`RepositoryCommitError`，不再走旧`7e56aa37` generic fallback。

`FirstBoard.Persistence.Tests.csproj` 的`AteliaRepositoryRoot`仍允许指向相邻local checkout，便于开发override；这不表示所有local环境都会自动处于CI pin。CI exact secondary checkout才是可复现依赖事实。

`StateJournalApiContractTests`直接证明typed `DurableDeque<ByteString>` commit/reopen与Repository dispose后Revision/root/live view失效；Deadline、Encounter、Player三个vertical都直接消费typed`RepositoryCommitError`，并在reopen后比较full authority。

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

  playerStatesByActor       # canonical目标；Phase 3已在独立Player root证明closure
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

未被 branch HEAD 选择的 dirty memory / orphan data 不是 committed state。当前CI pin已让Repository-owned Revision、DurableObject与live views在dispose后fail-fast；Session poison guard仍保留明确的领域边界，不能用旧wrapper绕过reopen。

### 5.3 Commit outcome unknown

当前CI pin直接提供强类型`RepositoryCommitError`。Objective、lineage-start、Encounter与Player commit都在调用前freeze完整authoritative parent/child snapshot；它包含schema/Definition/Ruleset binding、WorldVersion、`ParentWorldVersion`、last instant/cause、commit kind与该vertical的完整domain/player state，但有意排除lossy summary。

```text
Repository.Commit 返回 failure/throw
→ 禁止重试 Player/backend
→ 读取 expected/candidate address、failure phase、publication state
→ dispose + reopen branch
→ HEAD == expected physical parent
     且完整 authority == captured parent
     => not committed
→ HEAD == reported candidate
     且 HEAD parent == expected physical parent
     且完整 authority == captured child
     => committed
→ 其他
     => corruption / irreconcilable fault
```

当前裁决不再比较ledger tail、canonical bytes、digest或summary。`PublicationState`帮助分类，但不能替代reopen后的candidate address、physical parent edge与full authority验证，也不得把`MayHavePublished`变成blind retry。Deadline还证明：若lineage commit在publication前明确`NotPublished`，recovery API只在既有branch仍是exact parent authority时重新应用已捕获的deterministic lineage boundary；这不是重调Player/backend。

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

Phase 3的独立Alice Observe vertical已经证明一个窄但真实的durable closure：

```text
pre DecisionRequest: 2 held facts
→ production DecisionPointRule / Observe Objective outcome
→ post Objective actor facts: LastActionOutcome + 2 visible facts = 3
→ deterministic test cognition effect
     4 Memory shards
     previousKnownFacts = pre-request 2 held facts
     composition + slot binding + decision sequence
→ next full-POCO oracle request: post 3 + held 2 = 5 facts
→ exact next LLM prompt
```

Objective actor、Memory四shard、previous-known-facts、composition/slot binding与decision sequence在一个StateJournal commit中推进；close/reopen后的prompt与完整POCO oracle exact相等。mutants证明把previous-known-facts丢空或错设为post-current会改变`[新近变化]`。fault、binding、privacy、post-c1 fork后main/fork各自c2 nested isolation与Published ambiguity都已执行。

这个vertical使用production Objective Observe事实与reducer oracle，但cognition/memory replacement是deterministic test effect，next request由完整POCO world oracle supplied。它不是production LLM turn、backend reconnect或完整FirstBoard world restore。Blocking/commit前flush仍是当前候选；不得让background Task在commit后继续mutate同一个Revision。

### 6.2 Presentation

Presentation不持有Authority durable wrapper。`3cecbfb`已经提供nonzero baseline consumer seam：构造时要求`Committed == Presented == N`，nonzero baseline必须有last instant，且baseline world time必须等于该instant的model time；之后只接受同lineage的exact `N → N+1` suffix batch。

测试明确证明old prefix cue不会重播，commit成功但cue publication前crash的baseline也不会合成旧cue；这正是当前graph-authority/summary-only模型接受的语义。live suffix仍依靠exact batch boundary、facts order与pre/post state产生新cues。Presentation focused **18/18**、完整Demo **134/134**。

尚未证明的是StateJournal/Runner接线：当前没有从durable root生成closed full Objective POCO baseline并交给Presentation loop。若未来要求补播crash-gap cue，应增加窄durable Presentation outbox，而不是恢复完整Objective event-sourcing。

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

portable/self-contained Save 是另一项需求。当前StateJournal没有public `OpenReadOnlyExisting(strict)`，也没有把某条branch的有效closure打包/导出的public API。若将来需要把branch导出到独立介质，必须另行定义ancestor retention、segment closure、GC/repack、manifest与strict verifier；不能手拼segments，也不能用“复制整个repository”冒充最小branch export。same-repo branching的成功不能自动推出portable archive能力。

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

### 8.2 Phase 2：deadline graph authority（历史基础证据）

Phase 2 test-only refactor验证：

| ID | Phase 2 disposition | 当前证据 |
|---|---|---|
| SJ-G1 | **passed** | schema `/2` 同 commit保存 Objective graph、direct frontier、commit kind 与 summary；root 不含 ledger。 |
| SJ-G2 | **passed** | exact `JournalBatch` 只存在于内存；代码中没有 envelope codec、digest、rebuild 或 replay path。 |
| SJ-G3 | **passed** | close/reopen直接取得 exact graph/frontier/summary；test-only `metadata-only` commit移除summary后仍可reopen。 |
| SJ-G4 | **passed** | first-fact 与 batch-end validation failure均 fail-stop；reopen exact parent。 |
| SJ-G5 | **passed（历史API过渡）** | Objective与lineage-start的reflog ambiguity保存完整parent/child authority；generic fallback与exact e21 override均曾验证。Phase 3已由正式CI pin取代该过渡路径。 |
| SJ-G6 | **passed** | 同一 repo 从 exact Genesis commit创建两个 fresh-lineage branches；main不移动，siblings独立；从nonzero frontier fork也保留count、instant/cause与完整domain graph。 |
| SJ-G7 | **partial** | effective history得到每个 historical root 的 summary，但当前 helper会 materialize root；meta-only fast path未证明。 |
| SJ-G8 | **partially superseded** | Player closure与Presentation baseline已在Phase 3独立vertical证明；portable export与production integration仍deferred。 |

Phase 2结束时，deadline focused为 **9/9 passed**、Persistence为 **19/19 passed**、当时Local solution为 **481/481 passed**。这些是`06a5dad`附近的历史计数，不是Phase 3当前总数。

### 8.3 Phase 3：API、Encounter、Player、Presentation

Phase 3位于`bd4a6e9`、`3cecbfb`、`605b179`、`94d49d5`，把Phase 2的“下一步问题”逐项变成独立可执行证据：

| Slice | 当前证据 | 明确边界 |
|---|---|---|
| StateJournal API contract | CI exact pin为官方Atelia main `742fcd62e691b6b6acca4113a3ac3638bc7275ba`；typed ByteString与dispose lifetime tests通过；三个vertical常规使用strongly typed `RepositoryCommitError`。 | local `AteliaRepositoryRoot`仍可override，不能推断任意local sibling自动等于CI pin。 |
| Encounter | imported traveling baseline按production exact `Spatial PassageContactOccurredFact → Game PassageEncounterOpenedEvent`顺序；5-tuple `(passage, entityA, generationA, entityB, generationB)` identity、独立pending、direct reopen、真实reverse-order guard、fail-stop、fork与Published ambiguity通过。 | 不建立`pending encounter => contact仍在consumed set`永久invariant；production允许traversal变化后留下stale pending。 |
| Player | Alice Observe真实Objective outcome与deterministic cognition effect co-commit；4-shard Memory、previous facts、composition/slot/sequence、exact prompt、mutants、fault/binding/privacy、c1后main/fork各自c2 nested isolation与Published ambiguity通过。 | 不是production LLM turn，也不是完整world restore；next request来自full-POCO oracle。 |
| Presentation | nonzero baseline建立`C=P=N`、last instant/world-time binding；old cues drop、crash-gap no replay、only exact suffix通过。 | consumer seam已就绪，尚未StateJournal/Runner接线。 |

当前验证计数：StateJournalNative **29/29**，Persistence **39/39**；Presentation focused **18/18**，Demo **134/134**；DramaBoard solution **505/505**；CI-pinned Atelia StateJournal **1961/1961**。

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

Phase 3显著增强了“能力够不够”的证据：

- rebuildable ledger不是 DramaBoard 快速原型的 V1 requirement；
- ledger codec、projector、derived frontier 与 rebuild proof 可以从当前 probe删除；
- same-repo historical branch 是 fork/rewind 的 V1 物理原语；
- CI exact pin已常规验证typed ByteString、dispose lifetime与structured commit outcomes；
- order-sensitive Encounter nested graph、Player durable closure与Presentation nonzero suffix seam都存在可执行proof。

但“删掉ledger所以实现一定更简单”并未被证明。两个新的专用vertical各约2.3K gross lines：Encounter **2,318**，Player **2,338**。它们包含research oracle、fault、fork、privacy与negative tests，不能直接当production LOC；同时也真实展示了hand-written schema、wrapper、validation、full-authority snapshot、failure resolver与fork ceremony很重。能力充分性与authoring便利性必须分开评价。

仍需实证回答：

1. Encounter、Player与deadline合入同一个canonical root/coordinator时，cross-subsystem commit是否仍清楚？
2. closed full Objective baseline能否从统一root干净导出并交给Presentation suffix seam？
3. 重复schema/wrapper/freeze/validation/failure代码能否通过StateJournal API、生成器或authoring convention显著下降？
4. meta-only history enumeration是否值得成为StateJournal public API？
5. portable Save如何在缺少strict read-only open与branch closure export API时安全落地？
6. production cutover时，0004/0014–0020应如何整体重裁，避免两套authority？

最佳下一步不是继续建设第三个独立root，也不是立刻production cutover，而是一个decisive test-only integration slice：

```text
one canonical FirstBoard root + one transaction coordinator
→ Player passage-encounter response
     cognition update
     + Game encounter resolution
     + optional Spatial reverse
→ one commit + direct reopen + historical fork
→ derive closed full Objective baseline
→ feed the existing Presentation exact-suffix seam
```

若该slice仍需大量重复手写代码，应先改善StateJournal/生成器或wrapper authoring ergonomics，再扩大production migration。

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
2. 本文件 §8 与 [Build Log 0022](0022-statejournal-native-objective-probe-results.md)：三阶段实验结果。
3. [`StateJournal usage-guide`](../../../Atelia-org/atelia/docs/StateJournal/usage-guide.md)：当前 public API。
4. `tests/FirstBoard.Persistence.Tests/StateJournalNative/`：Deadline、API contract、Encounter与Player executable evidence。
5. `src/FirstBoard.Demo/Live/FirstBoardPresentationLoop.cs`、`LiveSessionCoordination.cs`及其tests：nonzero baseline consumer seam。
6. `E:/repos/Atelia-org/atelia/src/StateJournal/Repository.cs`、`RepositoryCommitError.cs`、`Repository.BranchRefs.cs`：commit/ref/history outcome。
7. [Build Log 0004](0004-game-content-and-composite-save.md) 与 0014–0020：尚未在本次任务中重写的 production plan。

最近收敛的团队裁决：

- direct StateJournal wrapper的能力充分性证据显著增强，尚非production cutover；
- canonical graph/frontier是状态 authority，summary不是；
- exact batch只保留为当前 transaction/live Presentation contract；
- normal fork/rewind使用同一 repository 的 branch/ref，不复制整个 repository；
- Encounter、Player closure与Presentation baseline seam已分别证明，但尚未进入同一canonical root/Runner path；
- 两个约2.3K-line专用vertical证明hand-written ceremony仍重，不能把删除ledger等同于代码简单；
- 下一步是统一FirstBoard integration slice；portable export与meta-only history继续延期；
- CI已pin官方Atelia main并常规消费typed/lifetime/structured outcome API，local sibling property仅是override便利。
