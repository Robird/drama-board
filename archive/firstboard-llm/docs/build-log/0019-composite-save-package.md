# Build Log 0019：复合 Save 的 fail-closed publication

> 状态：**Planned implementation slice**
>
> 依赖：[Build Log 0016](0016-objective-replay-package.md) 与 [Build Log 0018](0018-player-composition-checkpoint.md)
>
> 范围：把 production `run --content <pack> --save <new-save>` 接到 terminal clean capture writer，并严格读取一个完整 Save；暂不启动 successor Session，也不声称提供通用文件系统事务或掉电 durability。

## 1. 可保存的 terminal frontier

Save writer 不观察仍在运行的 Session，也不靠先后读取多个 mutable frontier 拼出 checkpoint。Runner 必须按以下顺序取得唯一输入：

1. Authority 与 Presentation 已经终止并完成 join；此后没有 Kernel、Journal、channel 或 Presentation writer。
2. 最后一个完整 Journal batch 已发布并安装，且 Presentation 已 drain 到 `P == C`。
3. 在不会再发生 Player call 的前提下 Flush 全部 stateful Player maintenance。
4. 对每个 Definition Actor slot 验证 driver 没有 request / maintenance / wrapper operation in flight，Player decision sequence 与 committed World 精确对齐，driver / wrapper config 与 checkpoint contract 相符。
5. 一次性冻结 Definition、World frontier、ordered committed batches 与完整 Player composition，形成 immutable capture；后续 package writer 只消费该 capture。

以下窗口必须特别检测：

```text
Player driver 已经返回 decision
→ cancellation / failure 发生在 Journal publication 之前
→ World 与 Journal 没有提交该 decision，但 Player 内部状态可能已经前进
```

这种 terminal state 即使已经没有 in-flight request，也必须拒绝 capture；不得把 Player-ahead-of-World 的状态发布成 Save，也不得猜测回滚 Player。V1 不在运行中的 Session 上实现 save barrier。

## 2. 目录与最小 manifest

```text
Save/
  save.json                  # staging 内最后写入的 publication marker
  game-definition.json
  journal/
  players/0001.json
  reports/                   # optional, non-authoritative
```

`save.json` 只保存不能由权威组件无歧义派生、或需要把组件绑定为同一次 publication 的字段：

```text
format = dramaboard.save/1
definitionSha256
worldSeed
selectedJournalChainSha256
payloadCodecId
playerCompositionId
players[]                    # closed descriptor set
  actorId
  closed driver union + canonical non-secret resolved config
  capturedDecisionSequence
  checkpoint? = checkpointCodecId / opaqueFilename / checkpointFileSha256
```

`playerCompositionId` 是 0018 closed union 的唯一格式/行为 compatibility ID；每个 Definition Actor slot 恰好有一个 descriptor。union variant 与 checkpoint presence 决定 stateful 与否，不再双写 `driverKind + stateful`。Human、Random、Null 不写伪 checkpoint；Random seed 属 canonical config。opaque filename 不拼接 author-owned ActorId。credential 不落盘，resume 时只重新注入 runtime secret/handle；保存的 backend/model/effort/safe endpoint/mode 等 canonical non-secret config 不得漂移。

以下值不写入 manifest：

```text
physical EventAddress / head
branch name
batchCount / transitionCount
lineageId / parent frontier
whole journal directory digest
roster fingerprint
PlayerStateFingerprint
```

Journal package 格式固定只选择 package-local `main`。reader 从 actual ref head 做 checked chronological traversal，并对 selected chain 中每个允许的 frame 使用带版本和 domain separator 的 length-prefixed `(frame kind, exact logical payload bytes)` 计算 `selectedJournalChainSha256`。digest 覆盖 lineage metadata 与全部 ordered batch envelopes；active tail、截断、替换或重排都会改变 digest，orphan frame、cache 文件和合法的 physical rewrite 不改变逻辑 authority。

batch / transition count、active lineage 与 parent frontier 从 checked chain 派生；roster 从 decoded Definition 与 closed descriptors 验证；Player state equality 由 canonical checkpoint bytes、file SHA 和逐字段 restore contract 证明，不再维护第二个 fingerprint。`definitionSha256` 同时绑定 exact canonical Definition bytes 与 semantic identity。

## 3. Fail-closed publication

new-run 的 `--save` 必须是不存在的目标；Program 只在 Session terminal/joined 且 §1 clean capture 成功后调用本 slice 的 writer。`resume --out-save` 由 0020 复用同一个 publication API。writer 不替换任何已发布 Save，也不修改 source Save / Journal。

1. 在 final path 的同一 parent、同一 volume 创建唯一 sibling staging directory。
2. 从 immutable capture 写 frozen canonical Definition、sealed exact Journal chain、Player payloads 与 optional reports。
3. close 所有 component writers，计算 manifest 所需 SHA 与 identities。
4. 在 staging 内最后写入并 close `save.json`；没有 manifest 的目录一律未发布。
5. 使用与正式读取相同的 strict read-only reader 对 staging 做完整 self-verification。
6. self-verification 成功后，将 staging directory rename 到此前不存在的 final path；rename 失败则 final path 不得被当作成功结果。

进程在任一注入点终止后，final path 必须满足 **valid-or-reject**：不存在，或能被 strict reader 完整接受。leftover staging 可以清理，但不覆盖、补写或修复为另一个已发布 Save。即使 rename 已完成而调用方尚未收到成功，final path 中也只能是已 self-verified 的完整 Save。

这个协议只承诺进程异常 / kill 下的 fail-closed publication 与旧 Save 安全，不承诺断电后的 file / directory flush durability。V1 不把跨平台 `fsync`、directory flush 或任意文件系统上的强原子 rename 写成产品保证。

## 4. Strict read-only reader

reader 必须以 read-only existing 模式打开 Save 及 `journal/main`；不得调用 `OpenOrCreate`、创建 branch / lineage metadata、advance ref、append frame、截断或修复 source。

验证顺序：

1. strict parse `save.json`，拒绝未知 format、`playerCompositionId`、checkpoint codec、未知字段、重复 Actor descriptor 与不安全 checkpoint filename。
2. 验证 canonical `game-definition.json` 的 SHA，随后 strict decode、Validate、Freeze、canonical re-encode，并验证 Ruleset 与 schema compatibility。
3. 验证完整 Actor descriptor set 精确等于 decoded Definition roster；验证 canonical resolved config、captured sequence binding、所有 checkpoint file SHA 与 self-described codec，无 checkpoint 的 union variant 不得带 payload。
4. 只读打开固定 `journal/main`，checked-traverse 允许的 lineage / batch frames，重算 selected chain SHA，并验证 lineage ancestry、`payloadCodecId`、batch LogicalInstant、CandidateKey 与 facts array order。
5. 所有 component identity、digest、roster 与 codec 验证通过后，按完整 batch boundary decode / replay Objective World。
6. 用 folded World 校验每个 Player decision sequence 与 wrapper frontier，再 import checkpoints；reader 返回可用于 0020 的 verified immutable capture，但不启动 Session。

read、replay、checkpoint import 与 comparison 的 Provider、Content Pack、Player decision backend 和 memory backend 调用次数均为 0。任何失败都不改变 source bytes，也不返回部分 World、部分 Player composition 或 writable Journal handle。

本 slice 发布的每个 Save 都是 self-contained；不定义 parent-chain restore，也不允许 reader 为补全 prefix 去读取另一个 Save。

## 5. 验收

- [ ] Authority / Presentation 未 join、`P < C`、未 Flush、pending request / maintenance、sequence 或 wrapper frontier mismatch 均拒绝 capture。
- [ ] production `run --content <pack> --save <new-save>` 端到端产生可由本 slice strict reader 接受的 Save-A；目标已存在时在启动 writer 前拒绝。
- [ ] driver 返回后、Journal publish 前发生 cancellation / failure 时，即使 World 与 Journal 仍对齐，也因 Player 已超前而拒绝 capture。
- [ ] Definition、selected logical Journal chain、World、ordered batches 与全部 Player slots 来自同一个 immutable terminal capture。
- [ ] manifest 精确包含 §2 最小字段，不含 physical head / branch / count / transition / lineage / directory digest / roster fingerprint / state fingerprint。
- [ ] active tail、truncation、frame replacement / reorder、payload codec、Definition / checkpoint SHA 或 Actor descriptor mismatch 在 fold / import 前拒绝；orphan / cache 不改变 selected chain identity。
- [ ] component write、manifest write、self-verification 与 final rename 各点注入 failure / process kill 后，final path 均 valid-or-reject，source 与此前 published Save 始终可读且 bytes 不变。
- [ ] reader 全程只读；对缺 branch、坏 lineage metadata 或坏 manifest 不创建或修复任何文件。
- [ ] 单独复制目标 Save directory 后仍能完成全部验证与重建 capture；合法 physical Journal rewrite 在 logical chain 相同时仍可验证。
- [ ] 全量测试通过。

## 6. 非目标

- 不继续 Session、不创建 successor lineage、不调用 backend。
- 不实现 mid-decision save、running-session barrier、crash-latest autosave、old-save migration 或通用 participant registry。
- 不承诺 power-loss durability、跨 volume publication、覆盖已有 final path 或通用 filesystem transaction。
