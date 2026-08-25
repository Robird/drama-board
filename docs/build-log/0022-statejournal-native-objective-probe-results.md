# Build Log 0022：StateJournal-native Objective probe results

> 状态：**Phase 1/2 evidence preserved；Phase 3 capability probes executable；尚未裁决 production cutover**
>
> 首次记录：2026-08-24
>
> Phase 2 refactor：2026-08-25
>
> Phase 3 commits：`bd4a6e9`、`3cecbfb`、`605b179`、`94d49d5`
>
> 研究章程与当前裁决：[Build Log 0021](0021-statejournal-native-vertical-probe.md)
>
> Phase 1 实验基线：`8ff97cd docs(state): record StateJournal-native experiment charter`

本文件记录StateJournal-native研究的三阶段可执行证据：Phase 1测试rebuildable semantic ledger + materialized graph；Phase 2删除ledger/codec/rebuild，把durable graph、direct frontier与physical commit history提升为authority；Phase 3验证正式Atelia API pin、order-sensitive Encounter、Player closure与Presentation nonzero baseline seam。

这些仍是test-only或consumer-seam证据，不把多个独立窄schema冒充统一FirstBoard root、production LLM turn、StateJournal/Runner接线、portable Save或production Kernel migration。

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

## 2. Phase 2：deadline graph authority（历史基础实现）

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

## 3. Phase 2 可执行证据（历史基础）

| 当前证据 | 结果 |
|---|---|
| Real rule oracle | native planner 的 CandidateKey、due、causal ordinal与ordered facts exact等于 production `CellarDeadlineRule.Forecast/PlanSelectedAsync`；immutable reducer得到相同 deadline post-state。 |
| Graph/frontier co-commit | deadline child同 commit保存Game、Spatial、lineage/count、last instant/cause、commit kind与summary；没有ledger。 |
| Direct reopen | dispose/open/checkout后直接 query exact state/frontier/summary；不调用codec、rebuild或replay。 |
| Missing summary | test-only metadata-only commit移除`commitSummary`后仍可reopen；状态与frontier不依赖summary，也不伪装成新的domain commit。 |
| Prefix failure | 第一个 Game fact已改working graph后注入异常；Session poison，reopen仍为exact parent。 |
| Validation failure | 两个 facts都改完后在batch-end validator注入异常；仍不commit，reopen exact parent。 |
| Ambiguous Objective child | Phase 2 commit前捕获完整authoritative parent/child；当时generic API以physical parent edge + exact authority fallback裁决，exact e21 override再验证reported candidate。Phase 3 current pin已改为直接消费强类型error。 |
| Ambiguous lineage boundary | lineage-start也使用同一恢复协议；reflog失败后确认fresh-lineage child；明确NotPublished时先确认exact parent，再在既有branch安全完成同一boundary；两条路径都保持main ref不变。 |
| Historical fork | main deadline不被移动；两个branches从exact Genesis address创建，各自co-commit fresh lineage boundary和独立suffix。 |
| Nonzero fork | 从`(L0,1)` historical HEAD建立`(L1,1)` lineage boundary，保留last instant/cause、Game和Spatial state。 |
| Per-root summaries | 两个effective histories均按Genesis、lineage-start、objective-transition读取对应summary；当前读取会materialize historical root。 |

Phase 2结束时，deadline focused为 **9/9 passed**，完整Persistence为 **19/19 passed**；detached exact `e21fc61a` override也曾以9/9验证stable structured error details。

当时`DramaBoard.Local.slnx`为 **481/481 passed**；这与Phase 1的历史 **478/478** 是两次不同代码基线的证据，不是Phase 3当前总数。

## 4. StateJournal API friction：历史与上游修复

以下三个friction在Phase 1确实存在，后来由包含`e21fc61a`的Atelia commit chain修复。`bd4a6e9`已把CI secondary checkout精确pin到官方Atelia main `742fcd62e691b6b6acca4113a3ac3638bc7275ba`；current CI不再依赖旧generic fallback或临时detached override。

`AteliaRepositoryRoot` MSBuild property仍允许开发者指向local sibling，这只是override便利；任意local checkout是否匹配CI pin仍由开发者负责。

### 4.1 Typed `ByteString` collections

Phase 1：`ByteString`只注册在mixed value catalog，`Revision.CreateDeque<ByteString>()`不可用，ledger只能使用wrapper-hidden mixed `DurableDeque`。

当前：[`StateJournalApiContractTests`](../../tests/FirstBoard.Persistence.Tests/StateJournalNative/StateJournalApiContractTests.cs)直接用typed `DurableDeque<ByteString>`完成commit/reopen。ledger虽然已删除，这项contract仍作为StateJournal API pin smoke proof保留。

### 4.2 Repository-owned object lifetime

Phase 1：`Repository.Dispose`不会让已materialize的`DurableObject`自动失效，Session必须以epoch/poison guard阻止旧facade继续使用。

当前：`StateJournalApiContractTests.RepositoryDispose_InvalidatesOwnedRevisionRootAndLiveView`验证Revision、root、live keys view与enumerator在Repository dispose后fail-fast。Session guard仍保留为领域层fail-stop边界，不再承担底层对象生命周期correctness。

### 4.3 Structured commit publication outcome

当前Deadline、Encounter与Player stores直接匹配强类型`RepositoryCommitError`。它提供expected/candidate address、failure phase、publication state与reopen requirement；Objective、lineage-start、Encounter与Player ambiguity tests都在`AppendReflog / Published`后reopen，要求HEAD等于exact candidate、candidate parent等于expected parent且full authority等于captured child。authority包含schema/binding、frontier及该vertical的完整nested domain/player state，明确排除summary。

Deadline还覆盖lineage publication前的已知failure：`NotPublished`时resolver先证明exact parent；resume只在同一branch仍为该parent authority时重施已捕获的deterministic lineage boundary。这不是blind external-effect retry。任何`MayHavePublished`都必须reopen分类，不能直接重试Player/backend。

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

## 5. Phase 3专项实验

### 5.1 Order-sensitive Encounter

`605b179`通过[`EncounterProbeStore.cs`](../../tests/FirstBoard.Persistence.Tests/StateJournalNative/EncounterProbeStore.cs)及其[tests](../../tests/FirstBoard.Persistence.Tests/StateJournalNative/StateJournalNativeEncounterProbeTests.cs)建立独立imported traveling baseline，而不是偷偷依赖deadline root或宣称production startup已经迁移。它从真实production rules取得exact transaction：

```text
Spatial PassageContactOccurredFact
→ Game PassageEncounterOpenedEvent
```

contact以5-tuple `(passageId, entityA, generationA, entityB, generationB)`持久化；actor identity、两个current traversal segments、consumed contacts与Game pending encounter都进入full authority。reverse-order test触发真实Game guard，证明facts order不是summary decoration。

pending encounter是独立Game state：open当下要求对应Spatial contact已经consumed，但complete-state validation故意不建立`pending => contact仍在consumed set`永久invariant，因为production允许traversal改变后清掉segment contacts而pending仍存在。不得为了方便测试重新发明这条law。

该vertical覆盖direct reopen、working/validation fail-stop、same-repo fork、nested authority与strongly typed `RepositoryCommitError`的Published ambiguity，共 **6 tests**。

### 5.2 Alice Observe Player closure

`94d49d5`通过[`PlayerClosureProbeStore.cs`](../../tests/FirstBoard.Persistence.Tests/StateJournalNative/PlayerClosureProbeStore.cs)及其[tests](../../tests/FirstBoard.Persistence.Tests/StateJournalNative/StateJournalNativePlayerClosureProbeTests.cs)建立另一个独立root，沿真实production Objective Observe路径验证：

```text
pre DecisionRequest observation: 2 held facts
→ ActorObserved Objective outcome
→ post Objective actor facts: outcome + 2 visible = 3
→ deterministic test cognition effect
     Memory: 4 shards
     previousKnownFacts: the pre-request 2 held facts
     composition + slot binding + decision sequence
→ next request observation: post 3 + held 2 = 5
→ exact next prompt after reopen
```

Objective actor、四个Memory shards、previous-known-facts、Player composition/slot binding与decision sequence在一个commit中推进。prompt exact comparison和mutants证明previous-known-facts既不能丢失，也不能误写成post-current facts；fault injection、composition/binding/privacy rejection、Published ambiguity均比较完整nested Objective + Player authority。

historical test在c1后fork，main与fork先exact继承相同nested closure，再分别提交不同c2 cognitive states；两个next prompts分别等于各自full-POCO oracle，main ref不移动。

边界必须写准：Objective Observe事实与reducer是production exact；Memory replacement是deterministic test cognition effect；next `DecisionRequest`由完整POCO world oracle supplied。它不是production LLM turn、backend reconnect或完整FirstBoard world restore。该vertical共 **12 tests**。

### 5.3 Presentation nonzero baseline consumer seam

`3cecbfb`让`LiveSessionCoordination`从任意verified nonzero `WorldVersion N`初始化`Committed = Presented = N`。`FirstBoardPresentationLoop`接收closed baseline world与last instant，要求world seed一致、`C == P`、zero/nonzero instant shape正确、baseline world time等于last instant model time。

测试证明旧prefix cue不会重播，commit/cue crash gap在resume后也不会合成旧cue；loop只fold/present exact suffix batch并推进到`N+1`。Presentation focused **18/18**，完整Demo **134/134**。

这只是consumer seam：尚无StateJournal/Runner把durable root转换为closed full Objective baseline并接线进去。

### 5.4 Phase 3 aggregate evidence

| Suite | Result |
|---|---:|
| StateJournalNative（API 2 + Deadline 9 + Encounter 6 + Player 12） | **29/29** |
| `FirstBoard.Persistence.Tests` | **39/39** |
| Presentation baseline focused | **18/18** |
| `FirstBoard.Demo.Tests` | **134/134** |
| `DramaBoard.Local.slnx` | **505/505** |
| CI-pinned Atelia StateJournal | **1961/1961** |

## 6. 复杂度与可读性观察

### 6.1 Phase 1 historical LOC

| 文件 | 行数 | 主要成本 |
|---|---:|---|
| `DeadlineProbeStore.cs` | 803 | Session、root、projector、derived frontier、outcome resolver |
| `DeadlineTransactionProbeV1Codec.cs` | 315 | narrow strict canonical JSON codec |
| `StateJournalNativeDeadlineProbeTests.cs` | 279 | oracle、reopen、rebuild、fail-stop、ambiguous fault |
| **合计** | **1,397** | test-only ledger vertical |

### 6.2 Phase 2 deadline LOC

以下是`06a5dad`附近Phase 2结束时的physical line baseline，不是Phase 3 current file count：

| 文件 | 行数 | 主要成本 |
|---|---:|---|
| `DeadlineProbeStore.cs` | 1,404 | Session、direct graph/frontier、branch/history、双API-shape outcome resolver |
| `StateJournalNativeDeadlineProbeTests.cs` | 636 | oracle、reopen、summary、fail-stop、Objective/lineage ambiguity、Genesis/nonzero fork |
| **合计** | **2,040** | test-only graph-authority vertical |

删除315-line codec没有让gross LOC自动下降，因为Phase 2新增了same-repo fork、fresh lineage boundary、historical traversal、per-root summary与更多failure assertions。当前数字是research coverage，不是production LOC估算，也不能与immutable reducer直接相减。

真正得到简化的是authority surface：production候选不再需要完整event union/codec、ledger-derived frontier、projector compatibility或rebuild proof。object graph wiring、Session lifecycle、branch provenance、failure reconciliation与schema evolution仍然是实在成本。

### 6.3 Phase 3 specialized vertical LOC

| Vertical | Store | Tests | Gross total |
|---|---:|---:|---:|
| Encounter | 1,867 | 451 | **2,318** |
| Player closure | 1,849 | 489 | **2,338** |

两个vertical都包含独立schema、wrapper、full-authority freeze/compare、validation、failure、fork、oracle与negative tests。它们是research coverage，不能直接当production LOC；但也不能因此忽略一个清楚事实：删掉ledger降低了authority/runtime-path负担，却没有自动消除hand-written durable modeling ceremony。Phase 3不能支持“StateJournal路线已经更简单”的结论，只能支持“能力显著更充分，authoring ergonomics仍待决”。

## 7. 当前裁决

StateJournal-native direct wrapper当前为：

```text
capability-credible leading test-only direction
+ unresolved authoring ergonomics
```

Phase 1/2已证明graph-authority模型的基本storage、resume、failure、summary与same-repo branch law。Phase 3新增证明：

- CI exact Atelia pin常规提供typed ByteString、dispose lifetime与strongly typed commit outcomes；
- order-sensitive Spatial→Game Encounter可用完整nested graph direct reopen/fork/recover；
- 一个真实Objective Observe与deterministic cognition effect可co-commit足以恢复next prompt的Player closure；
- Presentation可以从nonzero closed baseline开始且只消费exact suffix。

这些证据显著提高“StateJournal能力是否足够”的置信度，但没有证明代码更简单。Encounter与Player两个独立vertical各约2.3K gross lines，清楚展示hand-written schema/wrapper/validation/failure/fork ceremony。research coverage不能冒充production LOC，也不能被用来抹掉真实authoring friction。

尚未证明：

- Deadline、Encounter与Player合入一个canonical root/coordinator后的整体可读性；
- production LLM/backend turn与完整FirstBoard world restore；
- StateJournal/Runner生成closed full Objective baseline并接到Presentation seam；
- meta-only history enumeration；
- portable Save/export、ancestor retention与strict verifier；
- production Runner、Content与0004/0014–0020的整体迁移。

因此，Phase 3仍不是production cutover。既有production docs尚未在本次变更中重写；后续必须整体重裁，不能让EventJournal与StateJournal graph长期并列为双重state authority。

## 8. 最佳下一步

不要继续堆第三个独立probe root，也不要立即cutover。下一项应是一个decisive test-only integration slice：

```text
one canonical FirstBoard root
+ one transaction coordinator

Player passage-encounter response
  cognition update
  + Game encounter resolution
  + optional Spatial reverse
→ one commit
→ direct reopen
→ historical fork with divergent suffix
→ derive closed full Objective baseline
→ feed existing Presentation exact-suffix seam
```

这个slice同时检验Objective + Player + optional Spatial是否能在一个authority/failure boundary中保持清楚，以及durable root能否成为Presentation的closed baseline source。若仍需大量复制freeze/compare/schema/validation代码，应先改善StateJournal API、生成器或wrapper authoring ergonomics，再扩大production迁移。

portable export继续延期。当前缺少public `OpenReadOnlyExisting(strict)`与branch closure pack/export API；不得手拼segments，也不得复制整个repository并冒充“最小branch export”。meta-only summary traversal同样不是integration slice的阻塞项。

## 9. 验证命令

```powershell
dotnet test tests\FirstBoard.Persistence.Tests\FirstBoard.Persistence.Tests.csproj `
  --no-restore --configuration Release --verbosity minimal `
  --filter "FullyQualifiedName~StateJournalNative"

dotnet test tests\FirstBoard.Persistence.Tests\FirstBoard.Persistence.Tests.csproj `
  --no-restore --configuration Release --verbosity minimal

dotnet test tests\FirstBoard.Demo.Tests\FirstBoard.Demo.Tests.csproj `
  --no-restore --configuration Release --verbosity minimal `
  --filter "FullyQualifiedName~FirstBoardPresentationLoopTests|FullyQualifiedName~LiveSessionCoordinationTests"

dotnet test tests\FirstBoard.Demo.Tests\FirstBoard.Demo.Tests.csproj `
  --no-restore --configuration Release --verbosity minimal

dotnet test DramaBoard.Local.slnx `
  --no-restore --configuration Release --verbosity minimal
```

Phase 3当前结果：StateJournalNative **29/29**；Persistence **39/39**；Presentation focused **18/18**；Demo **134/134**；DramaBoard solution **505/505**；CI-pinned Atelia StateJournal **1961/1961**。
