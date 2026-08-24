# Build Log 0022：StateJournal-native Objective probe results

> 状态：**Phase 1 ledger probe preserved；Phase 2 graph-authority probe executable；尚未裁决 production cutover**
>
> 首次记录：2026-08-24
>
> Phase 2 refactor：2026-08-25
>
> 研究章程与当前裁决：[Build Log 0021](0021-statejournal-native-vertical-probe.md)
>
> Phase 1 实验基线：`8ff97cd docs(state): record StateJournal-native experiment charter`

本文件记录 StateJournal-native deadline vertical 的两阶段可执行证据：Phase 1 测试 rebuildable semantic ledger + materialized graph；Phase 2 在产品需求复核后删除 ledger/codec/rebuild，把 durable graph、direct frontier 与 physical commit history提升为 authority。

两阶段都只是 default-Genesis deadline Objective 的 test-only probe，不把窄 schema 冒充完整 FirstBoard、Player、Presentation、portable Save 或 production Kernel migration。

## 1. Phase 1：rebuildable ledger（历史实验）

Phase 1 由 demand skeptic、minimal architect、semantic defender 与 StateJournal API auditor审查；其代码落在 `d543307 test(state): probe StateJournal-native objective transactions`，结果文档落在 `8f40a89 docs(state): record objective probe evidence`。

### 1.1 实验边界

Phase 1 保留：

- test-only StateJournal integration，不让 production `FirstBoard` 或 `Spatial` assembly依赖存储；
- default Genesis 的 empty-ledger baseline HEAD；
- 一个真实 `CellarDeadlineRule` 对照的 ordered Game + Spatial transaction；
- canonical `ByteString` envelope、同 commit materialized graph 与 ledger；
- close/reopen直接 query graph，不执行 ledger replay；
- fresh repo 从 deterministic Genesis + one ledger tail rebuild；
- first-fact、batch-end validation failure后的 poison/dispose/reopen-parent；
- reflog failure after primary publication 的 gray-box ambiguity；
- WorldVersion / last instant 从 strict-scanned ledger派生。

Phase 1 延后完整 schema、Player closure、business sibling lineage、Save/resume、Runner cutover、read-only open与 production integration。

### 1.2 历史实现与证据

历史实现包含：

- `DeadlineProbeStore.cs`：803 lines；
- `DeadlineTransactionProbeV1Codec.cs`：315 lines；
- `StateJournalNativeDeadlineProbeTests.cs`：279 lines；
- 合计：1,397 gross lines。

`DeadlineTransactionProbeV1Codec.cs` 已在 Phase 2 删除；如需核查原实现，应查看 commit `d543307`，而不是把已删除路径当成当前文件链接。

Phase 1 证明：

| 历史证据 | 结果 |
|---|---|
| Real rule oracle | native planner 的 CandidateKey、due、causal ordinal 与 ordered facts exact等于 production rule；immutable reducer得到相同 post-state。 |
| One StateJournal child | baseline 到 deadline HEAD只有一个 parent edge；Game、Spatial、ledger位于同一个 root commit。 |
| Direct reopen | dispose/open/checkout后直接 query graph；normal restore不调用 projector/replay。 |
| Ledger authority smoke proof | 只复制 source ledger bytes；fresh repo从 deterministic Genesis + one tail rebuild出 exact graph。 |
| Failure law | first-fact与batch-end validation failure均不发布 child；reopen exact parent。 |
| Ambiguous child | generic failure 后 reopen，以 physical parent + exact ledger tail + derived frontier + post-state确认 child。 |
| Strict binding | noncanonical/unknown envelope field 与 wrong Definition binding fail-fast。 |

Phase 1 当时定向项目 **16/16 passed**，Local solution **478/478 passed**。这些是旧实现的历史计数，不是 Phase 2 当前 solution-wide 结果。

Phase 1 还纠正过一个假证明：实验曾为了捕获漏掉的 Spatial fact而发明 `CellarSealed == !gate.EnterableFromA` 永久 invariant，但产品没有这条 law。最终删除该 invariant，validation failure改用明确的测试注入。Phase 2 继续遵守这一边界。

## 2. Phase 2：graph authority（当前实现）

当前实现位于：

- [`DeadlineProbeStore.cs`](../../tests/FirstBoard.Persistence.Tests/StateJournalNative/DeadlineProbeStore.cs)
- [`StateJournalNativeDeadlineProbeTests.cs`](../../tests/FirstBoard.Persistence.Tests/StateJournalNative/StateJournalNativeDeadlineProbeTests.cs)

测试项目仍通过 local `AteliaRepositoryRoot` 引用自有 StateJournal project；main solution没有获得 production StateJournal dependency。

### 2.1 Durable root `/2`

Phase 2 root 只保存 deadline实际读取或改变的最小 state，以及直接 authority metadata：

```text
mixed DurableDict<string> root
  schemaId = firstboard.statejournal-deadline-root/2
  definitionSha256
  rulesetId
  worldSeed

  lineageId
  transitionCount
  parentWorldVersion?
  lastTransition?
    modelTime
    causalOrdinal
    causeKey

  commitKind                # 另有test-only metadata-only compatibility fixture
  commitSummary?          # lossy；open允许缺失

  game
    cellarSealed
    nowMs

  passageEntryOverrides : DurableDict<string, byte>
```

root 不含 transaction ledger。`lineageId`、`transitionCount`、last instant/cause与`ParentWorldVersion`直接持久化，不从 summary 或 history派生。

这仍不是“完整 FirstBoard 已改成 durable graph”。probe 显式拒绝 Genesis actor位于 Cellar 的 scenario，因为当前窄 schema没有 actor KnownFacts，不能偷偷漏掉 `CellarSealedEvent` 的 witness effect。

### 2.2 Authority 与 transaction

```text
DeadlineProbeView
  -> 从 committed durable query 规划真实 deadline facts
  -> 创建 exact ordered JournalBatch in memory
  -> private wrapper mutator 顺序改写 Game、Spatial
  -> batch-end validation
  -> 直接推进 lineage/count/last instant/cause
  -> 写 objective-transition kind + one lossy summary
  -> one Repository.Commit(root)
```

exact `JournalBatch<FirstBoardFact>` 只在当前调用栈内存在。当前项目没有 canonical envelope、transaction codec、digest、ledger、rebuild或replay path。

summary与状态在同一个 root commit，但它不是状态 authority：删除`commitSummary`字段后，root仍能正常 reopen并取得相同 graph/frontier。

### 2.3 Same-repository branch / rewind

当前 factory 从一个精确 historical `CommitAddress` 创建 branch，然后立即 co-commit fresh lineage boundary：

```text
CreateBranch(childName, fromCommit)
→ checkout child
→ lineageId = fresh child lineage
→ transitionCount = selected source transitionCount N
→ ParentWorldVersion = source WorldVersion
→ commitKind = lineage-start
→ commit one summary
→ append independent Objective suffix
```

测试先让 main提交 deadline，再让两个 branches从 historical Genesis commit分别产生 fresh lineage和deadline suffix。main ref保持不变；两个 branch的 effective histories都是：

```text
Genesis → lineage-start → objective-transition
```

当前 history helper使用`RepositoryHistoryReader.EnumerateBranchEffectiveCommitAddresses`枚举地址，再用`LoadRootAtCommit`读取每个 historical root，所以确实会 materialize roots。无需完整对象图反序列化的 meta-only fast path尚未实现或证明。

same-repo branch共享已有 repository历史并避免正常 fork/rewind复制整个 repo；本 probe不声称 branch创建严格 O(1)。portable Save/export与ancestor retention是独立的延后问题。

lineage-start不增加Objective transition count；它保留选中root的last instant/cause与完整domain graph。除Genesis sibling history外，Phase 2还从main的nonzero deadline HEAD创建branch，证明`(L0,1) → (L1,1)`的count与状态都不被重置。

## 3. Phase 2 可执行证据

| 当前证据 | 结果 |
|---|---|
| Real rule oracle | native planner 的 CandidateKey、due、causal ordinal与ordered facts exact等于 production `CellarDeadlineRule.Forecast/PlanSelectedAsync`；immutable reducer得到相同 deadline post-state。 |
| Graph/frontier co-commit | deadline child同 commit保存Game、Spatial、lineage/count、last instant/cause、commit kind与summary；没有ledger。 |
| Direct reopen | dispose/open/checkout后直接 query exact state/frontier/summary；不调用codec、rebuild或replay。 |
| Missing summary | test-only metadata-only commit移除`commitSummary`后仍可reopen；状态与frontier不依赖summary，也不伪装成新的domain commit。 |
| Prefix failure | 第一个 Game fact已改working graph后注入异常；Session poison，reopen仍为exact parent。 |
| Validation failure | 两个 facts都改完后在batch-end validator注入异常；仍不commit，reopen exact parent。 |
| Ambiguous Objective child | commit前捕获完整authoritative parent/child；当前main generic failure后以physical parent edge + exact authority snapshot裁决，e21 stable error出现时还会强制匹配reported candidate。 |
| Ambiguous lineage boundary | lineage-start也使用同一恢复协议；reflog失败后确认fresh-lineage child；明确NotPublished时先确认exact parent，再在既有branch安全完成同一boundary；两条路径都保持main ref不变。 |
| Historical fork | main deadline不被移动；两个branches从exact Genesis address创建，各自co-commit fresh lineage boundary和独立suffix。 |
| Nonzero fork | 从`(L0,1)` historical HEAD建立`(L1,1)` lineage boundary，保留last instant/cause、Game和Spatial state。 |
| Per-root summaries | 两个effective histories均按Genesis、lineage-start、objective-transition读取对应summary；当前读取会materialize historical root。 |

当前Atelia main下，Focused StateJournalNative tests：**9/9 passed**；完整`FirstBoard.Persistence.Tests`：**19/19 passed**。同一focused tests还通过临时`AteliaRepositoryRoot`指向detached exact `e21fc61a` worktree执行，结果同为 **9/9 passed**；两个reflog faults都取得candidate并断言`AppendReflog / Published`。

最终`DramaBoard.Local.slnx`为 **481/481 passed**；这与Phase 1的历史 **478/478** 是两次不同代码基线的证据。

## 4. StateJournal API friction：历史与上游修复

DramaBoard当前 local sibling是Atelia `main@7e56aa37`。以下三个friction在Phase 1确实存在；用户随后已在`origin/feature/derived-recap-grid-rewrite@e21fc61a`通过源码与StateJournal tests完成修复。DramaBoard尚未pin/integrate该commit chain，因此本节同时区分“upstream fixed”和“current probe available”。

### 4.1 Typed `ByteString` collections

Phase 1：`ByteString`只注册在mixed value catalog，`Revision.CreateDeque<ByteString>()`不可用，ledger只能使用wrapper-hidden mixed `DurableDeque`。

上游：`e21fc61a`所含改动已支持typed `ByteString` scalars/collections。当前Phase 2已经删除ledger与blob deque，所以即使dependency尚未pin，这项friction也不再影响当前probe。

### 4.2 Repository-owned object lifetime

Phase 1：`Repository.Dispose`不会让已materialize的`DurableObject`自动失效，Session必须以epoch/poison guard阻止旧facade继续使用。

上游：`e21fc61a`所含改动让Repository-owned Revision/DurableObject operational APIs在dispose后fail-fast。DramaBoard pin后应验证该保证；Session guard仍可保留为领域层fail-stop边界，不再承担底层对象生命周期correctness。

### 4.3 Structured commit publication outcome

Phase 1及当前DramaBoard默认local sibling：branch publication后reflog失败只表现为generic commit failure。Phase 2已经让Objective与lineage-start共享恢复协议：commit前捕获完整authoritative parent/child snapshot；generic fallback在reopen后核对expected parent address、immediate child parent edge和exact authority state，summary不参与。

上游：`e21fc61a`所含`RepositoryCommitError`提供expected/candidate address、failure phase、publication state与reopen requirement。Phase 2不依赖尚未pin的强类型，而是识别stable error code `SJ.Repository.CommitFailed`并读取`AteliaError.Details`；当前main返回generic `SJ.Repository`，所以这些字段为null并走上述完整authority fallback。

该双shape兼容已经被实际执行：同一focused suite通过临时project override在exact `e21fc61a` worktree运行 **9/9**，Objective与lineage-start两个reflog faults都报告candidate、`AppendReflog`与`Published`，resolver随后按exact candidate + authority snapshot确认child。若lineage commit明确`NotPublished`，recovery API只在branch仍为exact parent authority时重施同一个deterministic lineage boundary；这不是blind external-effect retry。临时worktree已删除，Atelia main未修改；正式dependency pin仍是后续集成任务。即使未来遇到`MayHavePublished`，也绝不能伪装成透明retry。

### 4.4 Normal resume很方便；semantic history并不免费

StateJournal已兑现核心便利：打开HEAD直接得到可query graph，不需要完整history replay，也不需要从尾部反推最小完整状态。

Phase 1进一步承诺：

```text
canonical ledger = semantic authority
materialized graph = view
```

因此必须承担closed/versioned event schema、canonical codec、private projector、ledger-derived frontier validation和ledger-only rebuild proof。若Player/LLM/tool outcomes也必须重建，这套completeness成本还会扩散。

Phase 2接受的V1降级是：

```text
graph/frontier = state authority
physical commit history = version authority
per-root summary = lossy human-facing history
exact batch = ephemeral live transition contract
```

这保留normal resume、historical state load、fork与rewind，同时明确放弃event-level audit、旧cue replay、alternative-projector replay和从semantic events重建状态。StateJournal消除normal resume replay，但不会免费提供semantic history；DramaBoard当前选择不购买后半部分能力。

## 5. 复杂度与可读性观察

### 5.1 Phase 1 historical LOC

| 文件 | 行数 | 主要成本 |
|---|---:|---|
| `DeadlineProbeStore.cs` | 803 | Session、root、projector、derived frontier、outcome resolver |
| `DeadlineTransactionProbeV1Codec.cs` | 315 | narrow strict canonical JSON codec |
| `StateJournalNativeDeadlineProbeTests.cs` | 279 | oracle、reopen、rebuild、fail-stop、ambiguous fault |
| **合计** | **1,397** | test-only ledger vertical |

### 5.2 Phase 2 current LOC

| 文件 | 行数 | 主要成本 |
|---|---:|---|
| `DeadlineProbeStore.cs` | 1,404 | Session、direct graph/frontier、branch/history、双API-shape outcome resolver |
| `StateJournalNativeDeadlineProbeTests.cs` | 636 | oracle、reopen、summary、fail-stop、Objective/lineage ambiguity、Genesis/nonzero fork |
| **合计** | **2,040** | test-only graph-authority vertical |

删除315-line codec没有让gross LOC自动下降，因为Phase 2新增了same-repo fork、fresh lineage boundary、historical traversal、per-root summary与更多failure assertions。当前数字是research coverage，不是production LOC估算，也不能与immutable reducer直接相减。

真正得到简化的是authority surface：production候选不再需要完整event union/codec、ledger-derived frontier、projector compatibility或rebuild proof。object graph wiring、Session lifecycle、branch provenance、failure reconciliation与schema evolution仍然是实在成本。

## 6. 当前裁决

StateJournal-native direct wrapper当前为：

```text
leading test-only direction under graph authority
```

Phase 1已证明：

- StateJournal能够single-writer co-commit跨Game/Spatial graph；
- strict fail-stop模型无需先建设deep draft/rollback；
- normal reopen直接取得对象图；
- ledger/materialized graph与one-tail rebuild在窄范围技术上可行。

Phase 2新增证明：

- graph与direct frontier可以成为唯一恢复authority，不需要ledger/codec/rebuild；
- summary可以与commit对齐且读取时可缺失，不进入状态correctness；
- exact batch可以只保留为内存transaction contract；
- historical `CommitAddress` + same-repo branch/ref足以构造两个fresh business lineages和独立suffix，并能从nonzero frontier保留完整状态；
- Objective与lineage-start都能fail-stop/reopen；当前main走完整authority fallback，e21 stable details可被同一代码自动解析；明确NotPublished的lineage boundary可在既有branch上安全resume。

尚未证明：

- 完整FirstBoard OOP wrapper是否比immutable POCO reducer更少ceremony；
- order-sensitive、多实体引用的batch是否仍清楚；
- Player Memory/previous-known-facts的durable closure；
- Presentation resume baseline、old-cue drop与crash gap；
- meta-only history enumeration；
- portable Save/export、ancestor retention与strict verifier；
- repository-controlled dependency pin（stable error details的exact e21执行路径已经通过）；
- production Runner、Content与0004/0014–0020的整体迁移。

因此，Phase 2取代的是本probe内部的ledger authority，不是已经发布production cutover。既有production docs尚未在本次变更中重写；后续必须整体重裁，不能让EventJournal与StateJournal graph长期并列为双重state authority。

## 7. 下一组专项实验

### 7.1 Pin并集成StateJournal API修复

将Atelia repository-controlled dependency pin到包含`e21fc61a`修复的已审定commit或后继版本。stable commit-error details路径已经通过detached exact e21 override验证；正式pin后仍应在常规build中覆盖：

- typed `ByteString` collection API（即使当前probe不再需要ledger）；
- dispose后Repository-owned object fail-fast；
- `RepositoryCommitError`的publication state/candidate address；
- DramaBoard ambiguity resolver先读structured outcome、再reopen核对HEAD。

### 7.2 Objective schema pressure：order-sensitive encounter

下一个Objective slice仍应选择真实order dependency，例如：

```text
Spatial PassageContactOccurredFact
then Game PassageEncounterOpenedEvent
```

它迫使wrapper处理actor/entity identity、traversal generation、consumed contacts与pending encounter，能回答deadline subset无法回答的“完整durable OOP model是否让storage ceremony泄漏”。它不需要重新引入永久event ledger。

### 7.3 Player durable closure

把一个deterministic Player Memory/previous-known-facts effect放入同一canonical graph commit，验证resume/fork后下一次decision所需状态完整。目标是从graph直接恢复Player，而不是从summary或ledger rebuild。

### 7.4 Presentation baseline与portable export

Presentation专项实验应从historical root建立baseline，只消费新suffix exact batches，并明确commit成功但cue未发布时的V1语义。

portable Save/export另行定义需要保留的ancestor closure、segment/manifest/verifier；不得回退为“每次normal fork复制整个repository”，也不得把same-repo branch自动等同于self-contained archive。

meta-only summary traversal可以作为StateJournal API调研，但不是上述correctness slices的阻塞项。

## 8. 验证命令

```powershell
dotnet test tests\FirstBoard.Persistence.Tests\FirstBoard.Persistence.Tests.csproj `
  --no-restore --configuration Release --verbosity minimal `
  --filter "FullyQualifiedName~StateJournalNative"

# 临时验证exact e21 API shape；worktree在验证后删除
dotnet test tests\FirstBoard.Persistence.Tests\FirstBoard.Persistence.Tests.csproj `
  --configuration Release --verbosity minimal `
  --filter "FullyQualifiedName~StateJournalNative" `
  -p:AteliaRepositoryRoot=<detached-worktree-at-e21fc61a>

# 删除临时worktree后，强制恢复默认Atelia main的generated restore graph
dotnet restore DramaBoard.Local.slnx --force

dotnet test tests\FirstBoard.Persistence.Tests\FirstBoard.Persistence.Tests.csproj `
  --no-restore --configuration Release --verbosity minimal

dotnet test DramaBoard.Local.slnx `
  --no-restore --configuration Release --verbosity minimal
```

Phase 2当前结果：Atelia main focused StateJournalNative **9/9 passed**；完整Persistence project **19/19 passed**；exact `e21fc61a` override focused **9/9 passed**；`DramaBoard.Local.slnx` **481/481 passed**。
