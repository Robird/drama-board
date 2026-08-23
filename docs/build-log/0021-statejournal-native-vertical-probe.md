# Build Log 0021：StateJournal-native OOP vertical probe

> 状态：**Research charter / context anchor；尚未裁决 production cutover**
>
> 记录日期：2026-08-24
>
> 代码与文档基线：`6433945 docs(content): reconcile content and save refactor plan`
>
> 关联现行方案：[Build Log 0004](0004-game-content-and-composite-save.md)、[Build Log 0014](0014-journal-neutral-capture.md)–[0020](0020-runner-resume-successor.md)
>
> Atelia 使用契约：[`StateJournal usage-guide`](../../../Atelia-org/atelia/docs/StateJournal/usage-guide.md)
>
> 本文件用于在上下文压缩与后续专项实验之间保存已经收敛的事实、裁决和实验边界。它不授权通过字符串替换把 0014–0020 改成 StateJournal，也不宣称 StateJournal 路线已经胜出。

## 1. 为什么重开持久化裁决

现行 0004/0014–0020 选择：

```text
immutable POCO World
+ authoritative JournalBatch history
+ terminal EventJournal export
+ full Objective replay
+ independent Player checkpoints
```

重新考虑的候选不是“用 StateJournal 替换 EventJournal adapter”，而是 StateJournal 原本的目标使用方式：

```text
DurableXXX graph 就是 Runtime 持久状态骨架
+ 薄 OOP wrapper 提供领域 API
+ 每次 semantic transaction 同时推进 transaction ledger 与 materialized graph
+ checkout HEAD 直接恢复完整 Objective + Player state
```

这是一项 Runtime state model 与 mutation authority 迁移；若最终采纳，0004 与 0014–0020 必须整体重裁。

## 2. 当前用户决定与产品边界

本轮已经明确：

- 不以 persistence 性能为主要裁决依据；DramaBoard 当前规模下两条路线都足够快。
- 更重视能力完整性、日常编码复杂度和心智模型是否容易理解。
- 可以让领域 wrapper 私有持有 `_data: DurableDict/Deque/...`；不要求保留 POCO snapshot 或 reconciliation layer。
- StateJournal、EventJournal 都是自有代码；允许修改 API，没有已发布兼容包袱。
- 当前仍是可信、本地、单进程、single Authority writer。
- 可以接受 transaction 期间的 mutable working Revision 不对外可见。
- 可以接受 apply / validate / commit 发生异常后整个 Session fail-stop，dispose 并从 durable HEAD reopen；不要求在同一 Revision 上 rollback 后继续。
- Performance、lazy load、compaction、通用 query language、merge 和多 writer 不进入首个实验。

尚未最终决定：

- StateJournal-native 模型是否比当前 immutable World + EventJournal 路线更容易实现和维护。
- Player persistent closure 是否首版就成为 transaction ledger 可完整 rebuild 的 view，还是先保留独立 checkpoint authority。
- 实验通过后是 production cutover、继续两路线对比，还是只吸收 StateJournal API 改进。

## 3. 已核实的代码事实

### 3.1 DramaBoard

- `JournalBatch<TFact>` 保存一个 occurrence 的 `LogicalInstant + CandidateKey + ordered non-empty Facts[]`；facts order 是 authoritative fold order。
- 当前 `SimulationKernel` 使用 immutable scratch fold：逐 fact 得到 scratch World、batch-end Validate、Journal publication 成功后才安装 World。
- `Kernel.World` 当前是可公开持有的 immutable snapshot；fold / validation failure tests 断言同一 Kernel 的 World 与 Journal 不变。
- Presentation 不并发读取 Authority current World；它消费 committed batch channel，并维护自己的 replay/projection state。
- LLM Player 会在 Objective publication 前改变 Memory 与 previous-known-facts；Pipelined maintenance 还能跨 decision return 继续运行。

### 3.2 StateJournal

- StateJournal 不是任意 POCO serializer，只持久化 Revision-owned Durable containers 与受支持 scalar。
- `Repository.Commit(root)` 从 root 做 reachability walk，写 dirty object delta/rebase，最后用 branch ref 发布 HEAD。
- `CheckoutBranch` 直接 materialize HEAD 完整对象图；当前为 eager full load。
- commit parent、historical root、`CreateBranch(fromCommit)`、branch ref/reflog 已存在。
- `DurableObject.DiscardChanges()` 与 container revert 目前是 internal；没有 public `Revision.DiscardAll` 或 isolated graph draft transaction。
- 这不是 strict fail-stop 模型的 correctness blocker：任何 working mutation 异常后废弃整个 Repository/Revision，reopen 只读取 branch HEAD。
- `Repository.Commit` 结果可能 ambiguous：data 与 primary ref 已推进后，backup/reflog failure 仍可能向 caller 返回 failure 并 poison Repository。
- 当前没有 `Repository.OpenReadOnlyExisting`；普通 `Open` 获取 exclusive lock，并可能做 best-effort segment layout maintenance。
- `Repository.Commit(root, note)` 的 note 属 branch ref/reflog，不是 immutable commit-level domain payload。

### 3.3 EventJournal 对照证据

- EventJournal 与 current JournalBatch authority 同构，并已有 strict read-only、parent chain 与 ForwardPlan。
- 临时 Release 量级实验中，1,000 个当前 FirstBoard batches 的纯 fold 约几十毫秒；1,000 个 256-byte EventJournal frames 的 checked read 也为几十毫秒量级。当前 replay 不是已证明的产品瓶颈。
- 当前 `AppendEventFrame` 每帧执行 `DurableFlush()`；临时实验写 1,000 frames 约二十秒。若保留 0015 路线，repository pin 之外还需要 terminal batch append / deferred durability + single flush，不能朴素逐帧调用现有 API。
- 上述数字只用于识别量级与热点；OS cache、磁盘和 payload codec 会影响绝对结果，不是产品 SLO。

## 4. 收敛的 StateJournal-native authority model

### 4.1 Root

首个实验允许使用最容易理解、非最省空间的完整 ledger：

```text
StateJournal main.GraphRoot
  schemaId
  definitionSha256
  rulesetId
  playerCompositionId

  transactionLedger : DurableDeque<ByteString>
  frontier
    businessLineageId
    objectiveTransitionCount
    lastLogicalInstant?

  objectiveWorld
    game
      actorsById
      objectsById
      pending state
    spatial
      entitiesById
      entry overrides
      schedules
      consumed contacts

  playerStatesByActor
```

StateJournal `LocalId` / `CommitAddress` 是物理身份，不得替代 ActorId、EntityId、WorldVersion 或 transaction identity。

### 4.2 唯一 authority 分工

```text
canonical transaction ledger
    = 领域语义 authority

committed Durable objective/player graph
    = 同一 transaction frontier 的 mandatory materialized view

StateJournal object delta/rebase
    = storage implementation detail
```

一次 Objective transaction 的最小 envelope 是完整 `JournalBatchV1`，不是单个 fact：

```text
TransactionId
ParentWorldVersion
LogicalInstant
CandidateKey
ordered Facts[]
PlayerEffects[]?          # 若要从 ledger 完整 rebuild Player
```

所有随机、LLM、Human 或 tool 的 accepted outcome 必须写入 transaction；rebuild 不能重新调用外部 effect。

### 4.3 Mutation authority

禁止：

```text
任意 caller 直接 mutate durable graph
→ 事后补写一条描述性 event
```

唯一合法路径：

```text
strict immutable BoardTransaction
→ private projector / event applier
→ internal OOP wrapper mutators
→ batch-end Validate
→ append exact transaction envelope
→ advance frontier
→ one Repository.Commit(root)
```

OOP wrapper 对业务侧只暴露 query、command validation 或 transaction-draft generation；raw Durable containers 和 unrestricted setter 不出 Ruleset state assembly。

### 4.4 示例 wrapper

```csharp
public sealed class BoardActor
{
    private readonly DurableDict<string> _data;

    public long Id => _data.GetOrThrow<long>(Fields.Id);
    public string Key => _data.GetOrThrow<string>(Fields.Key)!;
    public long DecisionSequence =>
        _data.GetOrThrow<long>(Fields.DecisionSequence);

    internal void Apply(
        ActorDecisionCompletedEvent fact,
        WorldMutationLease lease)
    {
        lease.RequireActive();
        _data.Upsert(
            Fields.DecisionSequence,
            checked(DecisionSequence + 1));
    }
}
```

字段 key、union discriminator、composite identity、null/missing 语义和 enum encoding 都是 versioned Ruleset persistence schema；不得依赖 CLR member/type name。typed `string` 的 null-to-empty 规范化必须显式规避。

## 5. 最小 commit / failure law

### 5.1 Happy path

```text
Forecast / Plan 只读 committed facade
→ acquire private WorldMutationLease
→ Player accepted effects 已固定；首版不允许后台 maintenance 越界
→ 按 Facts[] 顺序 mutate Objective wrappers
→ Validate complete Objective + Player frontier
→ append exact transaction envelope
→ update frontier
→ Repository.Commit(root)
→ resolve committed HEAD
→ invalidate old read facade
→ publish exact batch to Presentation
```

### 5.2 Apply / validation / cancellation failure

首版不建设 deep draft/rollback：

```text
任何 post-Plan working mutation failure
→ 不 publish batch
→ poison Session 与全部 wrapper/read facade
→ dispose Repository
→ reopen branch HEAD
```

未被 branch HEAD 选择的 dirty memory / orphan data 不是 committed state。

### 5.3 Commit outcome unknown

每次 transaction 分配 stable nonce/ID，并把它写进 ledger envelope：

```text
Repository.Commit 返回 failure/throw
→ 禁止重试 Player/backend
→ dispose + reopen main
→ HEAD ledger tail TransactionId == proposed ID
     => committed
→ HEAD 仍为 expected parent
     => not committed
→ 其他
     => corruption / irreconcilable fault
```

成功 resolve 后才可 publish Presentation；commit 以下不再观察 cancellation。

### 5.4 被有意删除的旧 Kernel promises

若采纳 direct mutable model，以下不是继续暗中保留的能力：

- `Kernel.World` 不再是可永久持有的 immutable snapshot。
- transaction 进行中没有 concurrent reader 能读取 Authority graph。
- apply / validation failure 后不能继续使用同一个 Kernel/Revision。
- 旧 wrapper 引用在成功 commit 或 failure poison 后都可能失效。
- same-instance retry、speculation、nested transaction 与 partial rollback 不在 V1。

以下继续保留：

- single winner；
- 一个 ordered complete batch 是唯一 Objective transition；
- batch 中间态不可观察；
- branch HEAD 只能是 parent 或完整 child；
- Presentation 只收到已 resolve committed 的 exact batch；
- normal restore 不调用 backend。

若将来需要 nonfatal error、same-instance retry、concurrent state reader 或 speculative transactions，届时才以真实 failure trace引入 `BeginDraft / CommitDraft / AbortDraft`。

## 6. Player 与 Presentation

### 6.1 Player

```text
LlmPlayerDriver
  _state   = DurableLlmPlayerState        # in graph
  _backend = runtime-only service         # never persisted
```

Durable state 只允许 Memory shard contents、previous-known-facts 与必要的 wrapper frontier。CharacterCard/material/schema 来自 Definition；backend/model/effort 等 non-secret config 属 closed composition；credential、client、Task、CancellationToken、trace 和 profiler 不进入 graph。

首版候选是强制 Blocking / commit 前 flush，使 Player accepted state 与 Objective batch 同一 transaction commit。Pipelined maintenance 只有两个合法未来：

1. 成为独立、显式的 Player transaction；或
2. 在下一 Objective transaction 前 join，并明确其 frontier。

不得让 background Task 在 commit 后继续 mutate 同一个 Revision。

### 6.2 Presentation

Presentation 不持有 Authority durable wrapper。它只消费 committed transaction/batch channel，并维护自己的 projection/read model。旧 cue、P/C frontier、Human reveal barrier 仍以 committed batch 为输入。

若不想让 Presentation 重建完整 Objective World，transaction 可以增加足够的 pre/post presentation evidence；但不能只发送 latest graph 而丢失 batch boundary、facts order 或被覆盖的历史文本。

## 7. Save / resume 候选形状

性能不是重点时，最小 immutable successor protocol 是：

```text
new run
→ 在 unpublished working/staging directory 创建 StateJournal repo
→ 每个 transaction 提交 root
→ terminal clean + close
→ self-verify
→ manifest last / rename to Save-A

resume Save-A
→ 保持 A closed/immutable
→ 完整复制 A repo 到 unpublished Save-B working directory
→ 打开 B/main
→ 首先提交 LineageCreated transaction，分配 fresh child business lineage
→ 继续提交 suffix transactions
→ terminal close / self-verify / publish B
```

两次 resume 各复制 A，产生不同 child lineage；StateJournal branch/ref/CommitAddress 不替代业务 lineage。Save-B 自包含，source bytes 不修改。

正式 reader 最终应有 strict `OpenReadOnlyExisting`。首个实验可在关闭 source 后先复制到 temp，再只对副本执行普通 `Repository.Open`，以性能换取 source 不变；这不是最终 public verifier contract。

## 8. 专项实验：最小 vertical

### 8.1 范围

只实现一个真实、跨 Game + Spatial 的 automatic transaction，优先选择 deadline / passage-entry change，因为它能验证：

- 一个 batch 多个 ordered facts；
- 两个领域子图同时变化；
- batch-end cross-domain validation；
- StateJournal commit 只有一个 publication frontier。

首轮明确排除：

- LLM / Player；
- Presentation UI；
- Content Pack / loader；
- Composite Save manifest；
- production Runner cutover；
- generic serializer / reconciler；
- StateJournal wire redesign；
- merge、多 writer、query language、index framework；
- 性能优化。

### 8.2 建议步骤

1. 建立 test-only `DurableFirstBoardRootV1`、Game/Spatial wrappers 和 schema constants。
2. 从最小 Definition/Genesis 构造 root，提交唯一 Genesis transaction。
3. 定义 `BoardTransactionV1` envelope 与 strict canonical codec。
4. 通过 private projector 把 deadline batch 应用到 durable wrappers。
5. Validate、append ledger、advance frontier、commit。
6. close/reopen，直接 query HEAD graph 并与 expected exact 比较。
7. 仅从 Genesis + ledger 在独立 repo/rebuilder 中重建，要求与 HEAD graph exact。
8. 从 Genesis 或 deadline 前 commit 派生两个 suffix，证明 branch independence。
9. 在第一个 fact 后、第二个 fact后、Validate、data write、primary ref replace、backup/reflog点注入 failure。
10. 用 TransactionId + reopen 证明结果只为完整 parent 或完整 child，永不发布半批。

### 8.3 验收矩阵

| ID | 必须证明 |
|---|---|
| SJ-V1 | raw Durable container 不泄漏给 rule/query consumer；所有 mutation 要求 active lease。 |
| SJ-V2 | deadline transaction 只有一个 ledger envelope、一个 Objective frontier advance 和一个 StateJournal commit。 |
| SJ-V3 | Game + Spatial 多 fact order exact，HEAD 不暴露 batch prefix。 |
| SJ-V4 | close/reopen 直接得到 exact state，不执行 ledger replay。 |
| SJ-V5 | Genesis + ledger rebuild 得到与 HEAD exact 的 state。 |
| SJ-V6 | 第 k fact/Validate failure 后 working Session poisoned；reopen HEAD 为 parent。 |
| SJ-V7 | ambiguous commit 通过 TransactionId resolve 为 parent 或 exact child；禁止 blind retry。 |
| SJ-V8 | historical commit branch 产生两个独立 suffix，source HEAD不动。 |
| SJ-V9 | unknown schema/tag/field、wrong definition/ruleset/frontier、duplicate transaction ID fail-fast。 |
| SJ-V10 | 记录实现 LOC、测试可读性、领域 API 噪声和使用者主观 friction；性能只作观察。 |

## 9. 实验后的裁决问题

vertical 完成后必须回答：

1. wrapper 是否真的隐藏了 Durable lifecycle，还是 Ruleset 到处出现 storage ceremony？
2. imperative private projector 是否比 current pure reducer 更容易理解和测试？
3. fail-stop/reopen 是否足够，还是具体失败证明需要 graph draft？
4. ledger rebuild law 是否自然，还是 event 与 graph 容易漂移？
5. StateJournal fault/result API 是否需要小改，具体改动能否保持局部？
6. Spatial 作为跨游戏 Engine subsystem 是否愿意直接依赖 StateJournal，或需要更窄的 state wrapper boundary？
7. 加入一个 deterministic Player effect 后，World + Player 同 transaction 是否仍然清晰？
8. 若 production cutover，0004/0014–0020 能删掉多少 codec/checkpoint/package binding，而不是又保留两套 authority？

只有这些问题有实证答案后，才裁决：

```text
keep current EventJournal plan
| cut over to StateJournal-native model
| evolve StateJournal API first
| start a separate integrated store research project
```

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

最小不可约 transaction node：

```text
TransactionCommit
  TransactionId
  ParentTransaction
  EventCodecId
  accepted semantic event/outcome
  canonical object write set
  materialized graph root/frontier
```

例如不是只记录“B 对 A 施放火球”，而是记录已接受的完整 outcome：

```text
FireballResolved
  caster = B
  target = A
  hpDelta = -10
  mpDelta = -15
  random/tool outcome provenance = ...
```

rebuild 应用 immutable transaction 中的 canonical effects，不重新调用随机数、LLM、Human 或历史版本业务代码。semantic event 与 mechanical write set 属同一个 transaction authority；object graph、indexes 和 checkpoints 都绑定 source transaction frontier，可删除重建。

StateJournal 已积累 object identity、version chain、delta/rebase、root、branch、segment 与 recovery 经验；EventJournal 已积累 immutable payload、parent chain、read-only traversal、ref 与 forward-plan 经验。真正的研究难点主要是：

- transaction workspace、snapshot isolation 与 outcome reconciliation；
- semantic event completeness 与 external effects；
- event codec / projector behavior / object schema 三种兼容 authority；
- stable logical identity、reference/owned value、union 与 migration；
- history retention、checkpoint/new Genesis、GC/repack 与 portable export；
- index definition/frontier、异步 rebuild 与 fail-closed swap；
- Player privacy、redaction 与 immutable history 的冲突。

这条路线概念自洽，也不是遥不可及；但当前不以通用平台设计阻塞 DramaBoard vertical。先让一个真实 aggregate transaction 工作，再决定抽象。

## 11. 上下文恢复索引

压缩或新会话后，建议按以下顺序恢复：

1. 本文件 §2–§5：用户决定、authority 与 failure law。
2. 本文件 §8：专项实验边界和验收。
3. [`StateJournal usage-guide`](../../../Atelia-org/atelia/docs/StateJournal/usage-guide.md)：现有 public API 与限制。
4. `E:/repos/Atelia-org/atelia/src/StateJournal/Repository.cs`、`Revision.Commit.cs`、`Repository.BranchRefs.cs`：commit/ref outcome。
5. `src/Kernel/Simulation/SimulationKernel.cs`、`src/FirstBoard/FirstBoardDomain.cs`、`src/Spatial/State/GraphSpatialState.cs`：当前 immutable model。
6. [Build Log 0004](0004-game-content-and-composite-save.md) 与 0014–0020：尚未被本研究章程取代的 production plan。

最近收敛的团队裁决：

- direct StateJournal wrapper 在新假设下从 `defer` 上调为值得验证的 `keep` candidate；
- `BeginDraft/CommitDraft` 不是 strict fail-stop V1 的硬 gate；
- transaction ledger 必须是 semantic authority，graph 是同 commit materialized view；
- Player completeness、TransactionId outcome resolution 与 immutable Save reader 是下一层真实边界；
- 先做 Objective-only cross-domain vertical，不先开发通用 store。
