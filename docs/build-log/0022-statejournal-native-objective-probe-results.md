# Build Log 0022：StateJournal-native Objective probe results

> 状态：**Phase 1 executable probe complete；尚未裁决 production cutover**
>
> 记录日期：2026-08-24
>
> 研究章程：[Build Log 0021](0021-statejournal-native-vertical-probe.md)
>
> 实验基线：`8ff97cd docs(state): record StateJournal-native experiment charter`

本文件记录第一个 StateJournal-native vertical 的可执行证据。它只证明一个 default-Genesis deadline Objective transaction 的 storage mechanics、failure law 与 one-tail rebuild；不把窄 probe 冒充完整 FirstBoard、Player、Save/resume 或 production Kernel migration。

## 1. 辩论后的实验边界

Round 1 由 demand skeptic、minimal architect、semantic defender 与 StateJournal API auditor 独立审查；Round 2 只质询仍有分歧的 rebuild、branch、transaction identity 与 frontier。

首轮保留：

- test-only StateJournal integration，不让 production `FirstBoard` 或 `Spatial` assembly 依赖存储；
- default Genesis 的 empty-ledger baseline HEAD；Genesis 不算 Objective transition；
- 一个真实 `CellarDeadlineRule` 对照的 ordered Game + Spatial transaction；
- private Session + query-only facade；raw Durable containers 不出边界；
- canonical `ByteString` envelope、同 commit materialized graph 与 ledger；
- close/reopen 后直接 query graph，不执行 ledger replay；
- fresh repo 从 deterministic Genesis + one ledger tail rebuild；
- first-fact failure、batch-end validation failure后的 poison/dispose/reopen-parent；
- 一个真实 gray-box ambiguous commit：primary ref 已发布后 reflog failure；
- WorldVersion / last instant 从 strict-scanned ledger 派生，不再持久化第二份 writable frontier。

首轮延后：

- 完整 actors/objects/entities/schedules/contacts schema；
- full FirstBoard fact codec 与多 transaction history；
- Player durable closure；
- `LineageCreated`、historical sibling business lineage 与 Save/resume；
- sealed Save、manifest、Runner cutover；
- StateJournal wire redesign、draft/rollback、read-only open、正式 fault-injection seam；
- performance 优化。

business sibling lineage 是现行产品真需求，不是被删除的 speculative requirement；它被放到紧邻的 Save/resume slice，因为把 `LineageCreated` union 塞入本次 Objective probe 会混合两种 authority。

## 2. 实际实现

实现位于：

- [`DeadlineProbeStore.cs`](../../tests/FirstBoard.Persistence.Tests/StateJournalNative/DeadlineProbeStore.cs)
- [`DeadlineTransactionProbeV1Codec.cs`](../../tests/FirstBoard.Persistence.Tests/StateJournalNative/DeadlineTransactionProbeV1Codec.cs)
- [`StateJournalNativeDeadlineProbeTests.cs`](../../tests/FirstBoard.Persistence.Tests/StateJournalNative/StateJournalNativeDeadlineProbeTests.cs)

测试项目通过 local `AteliaRepositoryRoot` 直接引用自有 StateJournal project；main solution 仍不获得该外部开发依赖。

### 2.1 Durable root

首轮只保存 deadline 实际读取或改变的最小 state：

```text
mixed DurableDict<string> root
  schemaId
  definitionSha256
  rulesetId
  worldSeed
  genesisLineageId

  transactionLedger : mixed DurableDeque
    each value must be canonical ByteString

  game : mixed DurableDict<string>
    cellarSealed
    nowMs

  passageEntryOverrides : DurableDict<string, byte>
    PassageId -> two entry bits
```

这不是“完整 FirstBoard 已改成 durable graph”。probe 显式拒绝 Genesis actor 位于 Cellar 的 scenario，因为当前窄 schema 没有 actor KnownFacts，不能偷偷漏掉 `CellarSealedEvent` 的 witness effect。

### 2.2 Authority 与 transaction

```text
DeadlineProbeView
  -> 从 durable query 规划真实 deadline facts
  -> canonical encode whole transaction
  -> private projector 顺序改写 Game、Spatial
  -> batch-end validation
  -> append exact ByteString ledger tail
  -> one Repository.Commit(root)
```

canonical envelope 包含：

```text
ParentWorldVersion
LogicalInstant
CandidateKey
ordered facts exactly:
  1. CellarSealedEvent
  2. PassageEntryAccessChangedFact
```

首轮没有随机 nonce。canonical bytes 是 commit-attempt correlation；其 SHA-256 是派生的 `TransactionDigest`，不是第二份持久化 identity。ambiguous resolution 比较保留的原始 bytes，不能在 failure 后重新序列化并假设结果相同。

### 2.3 Derived frontier

root 不保存 mutable `transitionCount` 或 `lastLogicalInstant`：

```text
current = (genesisLineageId, 0)
last = null

for each strict-decoded ledger envelope:
  require envelope.ParentWorldVersion == current
  require instant strictly advances
  current.TransitionCount += 1
  last = envelope.Instant
```

`game.nowMs` 仍按当前 FirstBoard domain model 物化，并在 open/commit validation 中要求等于 derived last model time。它是否也应删除并改成 derived query，留给更宽 domain migration 判断。

## 3. 可执行证据

| 证据 | 结果 |
|---|---|
| Real rule oracle | native planner 的 CandidateKey、due、causal ordinal 与 ordered facts exact 等于 production `CellarDeadlineRule.Forecast/PlanSelectedAsync`；immutable reducer oracle得到相同 deadline post-state。 |
| One StateJournal child | baseline HEAD 到 deadline HEAD 只有一个 parent edge；Game、Spatial、ledger 位于同一个 root commit。 |
| Direct reopen | dispose/open/checkout 后直接 query `cellarSealed=true` 与 gate `(false,true)`；normal restore 不调用 projector/replay。 |
| Ledger authority smoke proof | 只复制 source ledger bytes；fresh repo 从同一 deterministic Genesis strict decode + private projector，得到与 source materialized graph exact 相同的 schema-aware snapshot。 |
| Prefix failure | 第一个 Game fact 已改 working graph后注入异常；旧 facade失效，Session poison，reopen仍是 empty-ledger parent。 |
| Validation failure | 两个 facts 都改完 working graph后在 batch-end validator注入异常；仍不 commit，reopen exact parent。 |
| Strict binding | noncanonical/unknown envelope field 与 wrong Definition binding fail-fast。 |
| Ambiguous child | 删除 reflog file并建同名目录；StateJournal先更新 backup、再发布 primary ref，最后 append reflog失败。caller收到 failure，但 reopen得到 child；resolver以 physical parent + derived child version + exact tail bytes + exact materialized post-state确认 committed。 |

审查中曾出现一个被修掉的假证明：probe 为了捕获漏掉 Spatial fact，临时发明了 `CellarSealed == !gate.EnterableFromA` 永久产品 invariant。当前 FirstBoard 并无这条 law。最终实现删除该 invariant；正常 complete-state validation 只要求 materialized graph 与实际 canonical ledger tail一致，validation failure使用明确的测试注入。

## 4. StateJournal API 的真实摩擦

### 4.1 `DurableDeque<ByteString>` 当前不可用

`ByteString` 只注册在 mixed value catalog，不在 typed scalar helper registry。`Revision.CreateDeque<ByteString>()` 会在 runtime 报 unsupported。

probe 使用：

```text
mixed DurableDeque
+ wrapper-only PushBack/GetAt<ByteString>
+ read 时拒绝 empty/type mismatch
```

这不影响 correctness，业务侧也看不到 mixed container，但它是确凿的 API friction。暂不立刻扩 typed blob wire：只有第二个真实 consumer 也需要 typed blob collection时，再判断该改动是否值得。

### 4.2 Dispose 不会让 DurableObject 自己失效

StateJournal `Repository.Dispose` 关闭 files/lock，不会让已经 materialize 的 DurableObject 自动抛错。因而 Session 必须拥有 facade epoch 与 poisoned state；所有 query 先经过 Session guard。这个责任边界清楚，但不是零 ceremony。

### 4.3 Commit failure 没有结构化 `may-have-published` outcome

当前 `Repository.Commit` 在 branch publication后 reflog失败时只返回 failure并 poison。caller必须保留：

- expected physical parent `CommitAddress`；
- proposed canonical envelope原始 bytes；
- expected domain post-state。

随后 dispose/open并做三态裁决：parent、exact child、irreconcilable。模型可行，但 StateJournal 将来可考虑 internal phase fault hook和更结构化的 failure phase/candidate address；不应伪装成透明 retry。

### 4.4 Normal resume 很方便；semantic history 并不免费

StateJournal 已经兑现核心便利：打开 HEAD 即得到可 query graph，不需要完整 history replay，也不需要从尾部反推最小完整状态。

但只要继续承诺：

```text
canonical ledger = semantic authority
materialized graph = view
```

就仍然需要：

- closed/versioned event schema；
- canonical codec；
- private projector；
- ledger-derived frontier validation；
- 至少一个 ledger-only rebuild proof。

StateJournal 消除了 normal resume replay，不会自动消除 event completeness 与 projector drift 问题。

## 5. 复杂度与可读性观察

首轮新增 gross LOC：

| 文件 | 行数 | 主要成本 |
|---|---:|---|
| `DeadlineProbeStore.cs` | 803 | Session/facade lifecycle、durable root、projector、derived frontier、outcome resolver |
| `DeadlineTransactionProbeV1Codec.cs` | 315 | narrow strict canonical JSON codec |
| `StateJournalNativeDeadlineProbeTests.cs` | 279 | oracle、reopen、rebuild、fail-stop、真实 ambiguous fault |
| **合计** | **1,397** | test-only research vertical |

这不是 production LOC 估算，也不能与 current reducer 文件直接相减：其中包含大量故障实验、strict negative validation与oracle assertions。但它明确反驳了“换成 StateJournal 后 event-sourced完整性会自然免费获得”的乐观想象。

复杂度来源可分开：

- StateJournal object graph wiring本身较直接：一个 mixed root、一个 ledger、一个 Game dict、一个 typed override dict。
- normal reopen非常直接。
- event codec、projector、failure reconciliation与facade lifetime才是主要 ceremony。
- 若 production 同时保留 immutable POCO reducer与 durable projector，必然形成两套 mutation authority；真正 cutover 必须迁移/删除旧一套，不能长期双写。

## 6. 当前裁决

StateJournal-native direct wrapper 从 `keep candidate` 上调为：

```text
leading candidate for the next experiments
```

已证明：

- 现有 StateJournal public API足以完成 single-writer Objective co-commit；
- strict fail-stop模型不需要先建设 deep draft/rollback；
- close/reopen直接取得对象图的使用体验确实成立；
- semantic ledger与materialized graph可以同 commit并完成最小 rebuild；
- ambiguous commit能在不 blind retry的情况下裁决。

尚未证明：

- 完整 FirstBoard OOP wrapper是否比 immutable POCO reducer更少 ceremony；
- 真正 order-sensitive、含多实体引用的 batch是否仍清楚；
- Player Memory/previous-known-facts 与 Objective同 transaction是否易用；
- business sibling lineage、immutable Save source与严格 read-only verifier；
- production Runner、Presentation、Content integration；
- full codec/migration evolution。

因此，本结果不修改 0004/0014–0020 的 production authority，也不宣布 EventJournal路线已被替换。

## 7. 下一组专项实验

### 7.1 Objective schema pressure：order-sensitive encounter

下一个最有信息量的 Objective slice不是再加一个标量，而是：

```text
Spatial PassageContactOccurredFact
then Game PassageEncounterOpenedEvent
```

它具有真实 order dependency：Game opening会检查 Spatial contact已被 consumed。它还迫使 wrapper处理 actor/entity identity、traversal generation、consumed contacts与pending encounter，能回答 deadline subset无法回答的“完整 OOP durable model会不会 storage ceremony到处泄漏”。

### 7.2 Deterministic Player effect

Objective schema压力可接受后，再把一个 deterministic Player Memory/previous-known-facts effect放入同一 canonical transaction与root commit，验证 Player closure能否从 ledger完整 rebuild。

### 7.3 Save/resume 与 business lineage

随后实现：

- sealed source Save保持closed/immutable；
- copy到 successor working directory；
- fresh child business lineage + exact ParentWorldVersion；
- 同一 source两次 resume得到不同 lineage；
- `OpenReadOnlyExisting` 或等价 strict verifier contract。

在这个 slice 之前，不用 StateJournal branch name、LocalId或CommitAddress冒充业务 WorldVersion。

## 8. 验证命令

```powershell
dotnet test tests\FirstBoard.Persistence.Tests\FirstBoard.Persistence.Tests.csproj `
  --no-restore --configuration Release --verbosity minimal

dotnet test DramaBoard.Local.slnx `
  --no-restore --configuration Release --verbosity minimal
```

定向项目：16/16 passed。最终变更的 Local solution：478/478 passed。
