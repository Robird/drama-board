# Build Log 0015：Hermetic Atelia dependency 与 sealed Journal export

> 状态：**Planned implementation slice with build prerequisite**
>
> 依赖：[Build Log 0014](0014-journal-neutral-capture.md)
>
> 范围：先使 Atelia dependency 可由仓库 / CI 确定恢复，再把 0014 terminal capture 导出为 sealed Atelia Journal component，并提供严格只读的 envelope inspector。Live Session 仍使用 InMemory；本 slice 不发布 Replay / Save package。

## 1. 必须先解决的构建边界

当前 `Journal.Atelia.csproj` 默认引用仓库外的 `..\..\..\Atelia-org\atelia`，并且只在 `DramaBoard.Local.slnx`。公共 Runner 不能把这个本机 sibling path 变成传递依赖。

这个问题不阻塞 0012–0014，但是本 slice 的硬 entry gate。施工开始时必须首先选择并落地一种 repository-controlled pin：

1. 首选可恢复的 pinned package；
2. 若尚无 package，则使用仓库记录 commit 的 source / submodule 方式；
3. 不接受“CI 机器碰巧存在 sibling checkout”或默认 MSBuild property 退回本机路径。

若无法得到可重复依赖，本 slice 不得让 Runner / Save code 引用 `Journal.Atelia`，也不得以 Local-only build 冒充完成。

## 2. Sealed export 边界

exporter 只接受 0014 已终止且不可变的 committed-batch snapshot。它在明确的新 staging directory 中创建恰好一个 package-local `main` branch，先在 batch boundary 0 写入唯一 root `LineageCreatedV1(LineageId, ParentWorldVersion=null)`，再使用 0013 production payload codec 按原始顺序写入完整 batches，最后 close writer。没有 root metadata 的非空或零-batch chain 都不是合法 V1 export。

导出是 capture 的语义复制，不是继续 live branch：

```text
immutable batches
→ append exact JournalBatch in order
→ close
→ read-only inspect / reopen
→ batch-by-batch exact
```

`LogicalInstant`、`CandidateKey`、batch boundary 与 `Facts[]` order 均不得重排或 flatten。

## 3. Read-only envelope inspector

现有 `AteliaJournalSink.OpenAndReplay` 通过 writable `OpenOrCreate` 路径打开 Journal，且会立即 decode `TFact`，不能作为 Save verifier。本 slice 新增一条独立只读路径：

- 只使用 `OpenReadOnlyExisting`，不 create journal / branch，不 advance ref，不 append frame；
- 要求 package-local `main` 存在且是唯一 selected branch；
- 从实际 ref head 执行 checked chronological traversal，只返回 selected chain 的 frame kind 与 exact logical envelope bytes；
- unknown frame kind、坏 checked chain、错 lineage metadata adjacency 与 malformed envelope 均 fail-fast；
- 第一帧必须是唯一 root metadata，位于 boundary 0、parent 为 null；后续 root metadata 或首帧缺失均拒绝；
- child lineage metadata 使用 `ParentLineageId + ParentTransitionCount`，其中 count 与 `WorldVersion.TransitionCount` 同为 `long`；每个 child 的 parent ID 与 metadata 所在的 batch boundary 必须和前一 active lineage精确衔接；
- inspector 不接收 `TFact` decoder，不返回 writable sink，成功或失败均不改变 source bytes。

DramaBoard batch / lineage envelope 以 Atelia opaque frame kind 作为唯一 wire version，现有值明确命名为 `JournalBatchV1` / `LineageCreatedV1`，不在 JSON 内再增一个 `v`。`JournalBatchEnvelopeCodec` 与 `LineageMetadataCodec` 的 strict parser 拒绝 unknown / duplicate / missing property、错误 value kind、empty facts 与 invalid base64 / identifier。不兼容的 envelope shape 改变必须使用新 opaque frame kind。

## 4. 施工步骤

1. 先落地 hermetic pin，去掉默认 sibling dependency。
2. 将 `Journal.Atelia`、`Journal.Atelia.Tests` 与 `FirstBoard.Persistence.Tests` 加入主 solution，证明 clean checkout 可 restore / build / test。
3. 实现 immutable capture 到新 `main` Journal component 的 sealed exporter，并写入 boundary-0 root lineage metadata。
4. 实现 strict envelope codecs 与 read-only inspector；inspection 不依赖 production fact decoder。
5. 用 read-only path reopen export，对比 batch boundary、instant、cause 与逐 fact canonical bytes。
6. 保持 `LiveSession` 使用 `InMemoryJournal`，运行期不创建 Atelia writer。

## 5. 非目标

- 不写 replay/save manifest、Definition snapshot、selected-chain digest 或 Player checkpoints。
- 不向 production live commit path 注入 Atelia，不实现 crash autosave、forensic recovery、branch successor 或旧 Journal migration。
- 只有 crash recovery / autosave、实时 tail / audit、长局内存压力，或实测 final export 成本成为问题时，才重开 live durable injection。

## 6. 验收

- [ ] 主 solution / CI 在无 sibling checkout、无 local override 的 clean checkout 中可 restore / build / test。
- [ ] sealed export 与 InMemory capture 的 batch boundary、instant、cause、facts 及原始顺序 exact；close / reopen 后仍 exact。
- [ ] exported Journal 只选择 package-local `main`；missing branch、checked-chain corruption、unknown frame kind、malformed envelope 与 payload codec mismatch fail-fast。
- [ ] inspector 成功 / 失败前后 source Journal bytes 不变；不可达 orphan frame 与 cache 不进入 selected chain。
- [ ] root metadata 恰好一条、位于首帧/boundary 0 且 parent 为 null；child adjacency、fresh lineage identity 与 `long` parent count 被 strict 验证，缺 root、第二 root、伪造 parent 或错误 boundary 均拒绝。
- [ ] LiveSession 仍只使用 InMemory；全量测试通过。
