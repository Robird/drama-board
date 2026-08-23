# Build Log 0014：Journal-neutral capture 与 report surface

> 状态：**Planned implementation slice**
>
> 依赖：[Build Log 0013](0013-firstboard-fact-codec.md)
>
> 范围：移除 application / report 对 `InMemoryJournal<FirstBoardFact>` 具体类型的依赖；Live Session 有意继续以 InMemory 运行，并只在 Authority / Presentation 已终止且 join 后产生不可变 capture。

## 1. 当前阻塞

`BoardRunCapture`、`LiveSessionCanceledCapture`、`DramaRecordWriter`、`FirstBoardRuleset.EventSnapshots/FormatJournal` 等直接持有 `InMemoryJournal`。后续 Save exporter 只需要已提交的语义历史，不应获得 sink 类型、writer lifecycle 或可继续增长的 list view。

仅在运行中某一瞬间观察到 `P == C` 不是 capture barrier：Authority 可以立即再提交一批。V1 的 capture 必须来自已终止且 join 的 Session，使 World、Version 与 batch snapshot 在导出期间不再变化。

## 2. 施工步骤

1. Authority 先停止并 join；Presentation 排空已发布 prefix 后 join，要求 `Presented == Committed == Version`。
2. 在这个 terminal boundary 验证没有 in-flight Kernel Step / Player request，`Version.LineageId == journal.LineageId`，`Version.TransitionCount == journal.Batches.Count`，last instant 与最后一批对齐。
3. capture 复制完整 `JournalBatch<FirstBoardFact>[]`，只暴露 immutable committed-batch snapshot、World / Version 与 last committed instant；不保留指向 sink internal list 的 read-only view。
4. report / formatter 消费 immutable batch snapshot，不拥有 Journal sink。
5. Live authority loop 继续通过 `IJournalSink` 提交，composition root 继续使用 `InMemoryJournal`；capture 不反向控制 sink lifecycle。
6. 迁移 cancel / complete manifest、report 与 tests，保持当前并发 / cancellation semantics。只有已经 cleanly terminated 的 cancel prefix 可产生 capture；faulted / uncertain publication 不得假装成可保存 frontier。

## 3. 非目标

- 不新增通用 committed-history framework 或第二个 Runner.Core project。
- 不引用 Journal.Atelia、不写磁盘、不改 run artifact 格式。
- 本 slice 不定义 Player maintenance Flush 与 checkpoint barrier；完整 Save clean frontier 由 0019 在此 terminal capture 之上补齐。

## 4. 验收

- [ ] production app / report surface 不依赖具体 `InMemoryJournal` 类型。
- [ ] capture 只在 Authority / Presentation terminal joined 后成功；单次 `P == C` 的运行中瞬时观察不可 capture。
- [ ] committed batches 是独立 copy；后续 sink mutation 或 dispose 不改变 capture bytes。
- [ ] batches count / lineage / last instant、World 与 Version 精确对齐。
- [ ] Live / Presentation / cancel tests 全部通过。
- [ ] diff 不包含 durable storage 或 Save 逻辑。
