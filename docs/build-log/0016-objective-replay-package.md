# Build Log 0016：Objective Save component 与 verifier

> 状态：**Planned implementation slice**
>
> 依赖：[Build Log 0012](0012-game-definition-codec.md) 与 [Build Log 0015](0015-durable-atelia-journal.md)
>
> 范围：复用 0012 production Definition codec，实现 Composite Save 内部的 Objective component writer / verifier，证明 frozen Definition + sealed Journal 可精确重建客观世界。本 slice 不发布独立 `ReplayPackage` / `replay.json`，也不恢复 Player。

## 1. 内部 component contract

```text
Save staging/
  game-definition.json
  journal/

writer result:
  ObjectiveDescriptor

reader result:
  VerifiedObjective
```

`ObjectiveDescriptor` 是一个由 0019 收入 `save.json` 的值，不是第二份 manifest。它最小绑定：

```text
definitionSha256              # exact canonical bytes == semantic Definition identity
worldSeed
expected payloadCodecId       # 零 batch Save 也能绑定续写 codec
selectedJournalChainSha256
```

Definition schema / RulesetId 由 canonical Definition 自描述；lineage / parent frontier 由 checked lineage metadata 取得；transition count 从 strict `JournalBatchV1` frame 数派生。V1 只选择 package-local `main`，因此 descriptor 不重复 branch name。Physical `EventAddress` head、`batchCount`、`transitionCount` 与 whole-directory digest 都不是 manifest authority。

## 2. Selected-chain identity

`selectedJournalChainSha256` 只覆盖从实际 `main` ref head 可达的 checked chronological chain，不覆盖 orphan frame、inactive branch、reflog、cache、压缩方式或物理 layout。

实现时冻结带 domain / version separation 的无歧义编码：

```text
SHA256(
  "dramaboard.selected-journal-chain/1\0"
  || frameCount:u64le
  || Σ(
       opaqueFrameKind:u32le
       || logicalPayloadLength:u64le
       || exactLogicalPayloadBytes
     )
)
```

digest 覆盖所有 selected frames，包括 lineage metadata 与 batch envelopes。active tail、truncation、frame reorder、metadata / envelope replacement 都必须改变 digest；同一 logical chain 的合法 physical rewrite 仍可接受。

## 3. 三阶段验证

1. **Inspect Objective bytes**：验证 canonical Definition file SHA；用 0015 read-only inspector 打开固定 `main`，checked traversal 全链，拒绝 unknown frame kind / malformed envelope / 错 lineage metadata，验证所有 batch envelope 的 `payloadCodecId`，计算并比较 selected-chain SHA，派生 lineage 与 transition count。
2. **Decode Definition**：使用 0012 的 production codec strict lossless reader decode，随后 `Validate → Freeze → canonicalize`，要求 re-encoded bytes / SHA exact，并验证 Runner-supported `RulesetId`。阶段结果必须允许 0019 在任何 fact fold 前插入完整 Player roster / composition 验证。
3. **Decode and replay facts**：只对 phase 1 已验证 codec 的 raw fact payload 调用 0013 decoder；用 Definition + world seed 创建 Genesis，通过 production `SimulationReplay` 或等价的逐 batch scratch-fold / validate law 恢复 World，保留 batch 与 facts 的已知因果顺序，最后返回 `VerifiedObjective`。

phase 1 inspector 不接收 `TFact` decoder，可使用“decoder 调用为 0”测试作为内部 layering proof；这不是一条独立产品 law。产品 law 是：不把 codec-mismatched envelope 交给 fact decoder，所有结构 / identity mismatch 在 fold 前拒绝，Provider / Content / Player / backend / Session 调用为 0。

## 4. 施工步骤

1. Objective writer 用 0012 codec 写 canonical Definition，调用 0015 exporter 写 sealed exact Journal，close 后计算 Definition / selected-chain SHA，返回 `ObjectiveDescriptor`。
2. 实现可组合的 phase 1 / 2 / 3 production API；任何 read 路径只读 source component。
3. 实现 selected-chain digest canonical encoder，并将 actual head 只作为 traversal 起点，不暴露为 package identity。
4. 用 wrong-Journal mix-up、active tail、truncation、frame reorder、lineage metadata replacement、坏 codec / Ruleset / Definition SHA 证明 mismatch 在 fold 前拒绝。
5. 证明 orphan / reflog / cache 不影响 selected identity，而 selected logical frame 的任何变化都会失配。
6. 删除或替换 Pack B DLL 后仍能 verify / reopen。

## 5. 命名与非目标

本 slice 产出的是 Composite Save 的内部 Objective component API，不是独立产品格式。不新增 `ReplayPackage`、`replay.json`、standalone publication transaction 或公共 inspect CLI；不恢复 Player、不原位继续、不启动 Session。

只有可分享 regression corpus、用户 replay / bug-report 上传、独立 forensic export 或正式 replay CLI 出现真实消费者时，才重开独立 Objective Replay artifact。

## 6. 验收

- [ ] one-shot 与 verify / reopen 的 World、派生 Version、batch boundary、instant、cause 与 facts order exact。
- [ ] 复用 0012 Definition reader；A / B bytes → Definition → bytes exact，且本 slice 不产生第二份 reader/codec authority。
- [ ] wrong Journal 即使 physical head / count 恰好相同，也因 selected-chain digest 失配在 fold 前拒绝。
- [ ] active tail、truncation、selected frame / lineage metadata 修改、unknown frame kind、Definition / codec / Ruleset mismatch 在 fold 前拒绝。
- [ ] physical rewrite 不改 selected identity；orphan / inactive branch / reflog / cache 不参与 authority。
- [ ] phase 1 的 decoder-zero-call 只作为内部 layering test；任何 codec-mismatched envelope 绝不调用 fact decoder。
- [ ] Provider / Content file / Player / backend / Session access 为 0；source component bytes 在成功 / 失败路径均不变。
- [ ] 本 slice 不生成独立 manifest；部分写入只是未发布 staging，最终 publication 由 0019 唯一拥有。
