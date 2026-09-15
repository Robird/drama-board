# Build Log 0020：Runner resume 与 immutable successor

> 状态：**Planned implementation slice**
>
> 依赖：[Build Log 0019](0019-composite-save-package.md)
>
> 范围：从 verified Save 的精确 frontier 创建 child continuation Session，并把停稳后的完整逻辑历史发布为 immutable successor。

## 1. CLI 与身份裁决

```text
dramaboard resume --save Save-A --out-save Save-B --until-ms ABSOLUTE_MODEL_TIME
```

`resume` 不接受 `--content`，不探测 Pack，不装载 Content Module。外部 credential 可以重新注入，但必须匹配保存的 non-secret config identity。`--until-ms` 是本次 invocation 的必需绝对 ModelTime 边界，必须不早于 restored current ModelTime；它决定 continuation 何时停稳并尝试发布 Save-B，不进入 Definition 或 Player composition，也不能借机修改保存的 `DecisionBudget(maxTurns)`。

Save 是可复制、可多次读取的 immutable checkpoint，不是全局一次性消费 capability。因此每次 resume 都必须在 Save-A 的精确 saved frontier 创建一个 fresh child lineage：

```text
Save-A current version = (L0, N)

resume A -> B
    parentWorldVersion = (L0, N)
    child initial version = (L1, N)

resume A -> C
    parentWorldVersion = (L0, N)
    child initial version = (L2, N)

require L0 != L1 != L2 and L1 != L2
```

否则 B、C 在第一个不同的 Human / LLM 决定后会产生内容不同、却同为 `(L0, N + 1)` 的 committed prefixes，破坏 `WorldVersion(LineageId, TransitionCount)` 的身份语义。产品层未来仍可把 `fork` 命令保留给“选择任意历史 prefix”这一独立功能；resume 是只允许从 verified saved frontier 创建 child 的受限 continuation branch。

Runner 使用可注入的 lineage allocator。测试注入固定 ID；production allocator 必须 fresh、collision-resistant，并拒绝与已知 ancestry / current lineage 冲突。child ID 不得只由 parent frontier 确定性派生，否则同一 Save 两次 resume 仍会碰撞。沿用当前 `long` identity 时，具体随机编码与保留值由实现切片固定并以 golden / collision tests 锁定，本计划不借机扩张成通用 identity service。

child 的 parent frontier 最小为完整 `ParentWorldVersion = ParentLineageId + ParentTransitionCount`。它只由 successor selected Journal chain 中的 lineage metadata 持久化，并受 `selectedJournalChainSha256` 绑定；`save.json` 不重复保存。reader 必须验证 child metadata 的 parent ID 与 prefix boundary 精确等于 source `VerifiedObjective` 派生的 WorldVersion。parent Journal head / EventAddress 是物理定位与 CAS 信息，不进入 WorldVersion、manifest 或 lineage identity。

## 2. Verified capture 与 preflight

0019 reader 必须先完成全部验证，并返回 closed `VerifiedSaveCapture`；其中的客观组件记为 `VerifiedObjective`。0020 不重新解释未验证的 package bytes。capture 至少包含：

```text
frozen Definition / DefinitionSha256 / RulesetId
WorldSeed
source WorldVersion (L0, N)
exact chronological JournalBatch envelopes [0, N)
folded Objective World
last committed LogicalInstant / current ModelTime
complete Player composition descriptors and imported checkpoint payloads
checked lineage metadata and selected logical-chain provenance
```

在创建 Session、调用 Player 或 backend 前，Runner：

1. 分配 fresh child lineage `L1`，形成 `ParentWorldVersion=(L0,N)` 与 child initial version `(L1,N)`；
2. 创建 `InMemoryJournal<FirstBoardFact>(L1)`，按顺序复制 verified capture 的 N 个完整 batches，不改变 `LogicalInstant / CandidateKey / Facts[]`；
3. 用 Definition 创建 Genesis，并对 `VerifiedObjective` 的 child in-memory prefix 做 ordinary Replay，验证 folded World、transition count、last instant 与 current ModelTime 和 verified capture exact；Replay 不调用 Player、backend、Provider 或 Pack；
4. 无 backend 地导入所有 Player / wrapper checkpoint，再验证 exact Actor descriptor set、captured decision sequence、canonical Player state、derived `PlayerCompositionSha256` 与 resolved non-secret config。preflight 到此为止，不推进 Kernel 来猜测“下一 request”。

任何 preflight mismatch 都在 Session 启动和新 backend call 前拒绝，且不创建 published Save-B。任意合法 frontier 之后可能先有若干 automatic occurrences，也可能在本次 `--until-ms` 前根本没有 DecisionPoint；因此 next request / Prompt 只在 Session 自然到达首个实际 DecisionPoint 时构造。

## 3. Nonzero live continuation

live continuation 继续使用 in-memory Journal；本 slice 不让正在运行的 Session 直接写 Save-A、Save-B staging 或 reopened Atelia branch。这样 live Authority 仍只有当前 `IJournalSink` 这一条 commit authority，package export 只发生在停稳后。

Runner 从 verified/replayed boundary 建立 Session：

1. Kernel 使用 folded committed World、child version `(L1,N)`、含完整 inherited prefix 的 child `InMemoryJournal`，以及 inherited exact `LastCommittedInstant`；`notAfter` 取已经验证的 `--until-ms`；
2. `LiveSessionCoordination` 必须允许从非零 verified frontier 初始化，并令 `C=P=(L1,N)`；
3. Presentation 私有 replay state 以 folded committed World 和 inherited `LastPresentedInstant` 为 baseline；旧 prefix 不重新播放 cue，live channel 只接收新 suffix `(N, M]`；
4. Human reveal barrier 从 `P=C=N` 开始工作；首次新 commit 必须是 `(L1,N+1)`，Presentation 仍逐完整 batch ack；
5. `LiveAuthorityLoop.CommittedTransitionCount` 只表示本次 resume invocation 提交的 suffix 数量，不冒充 lineage 的总 transition count；总数始终来自 `WorldVersion.TransitionCount` / complete Journal batch count；
6. Player、Kernel、Presentation 与 Journal 到达 clean frontier 后才允许进入 successor export；pending request、in-flight Step、`P<C` 或未 Flush maintenance 均拒绝发布。

共享 folded World 只作为两个 loop 启动前的 immutable baseline。Session 启动后 Presentation 不读取 Kernel current World / Version，也不并发枚举 Journal；新 suffix 仍只通过现有 committed-transition channel 传递。

## 4. Quiesced successor export

Session 停稳后，Runner 组合 verified Save-A 的 selected logical frames、child lineage metadata 与 child in-memory Journal 的新 suffix，导出到 Save-B staging。Objective batch history 仍是 `[0,M)`，但 lineage metadata 也必须保留在 selected chain 中：

```text
verified Save-A selected frames
 child LineageCreatedV1 at batch boundary N
 child live JournalBatchV1 frames [N,M)
= Save-B self-contained selected chain with Objective batches [0,M)
```

导出必须满足：

1. inherited prefix 的全部 selected lineage metadata 与 batch envelopes 都和 Save-A verified capture frame-by-frame exact；suffix 来自本次 child Authority 的 committed in-memory batches。不能只复制 N 个 batches 而丢掉 Save-A 已有 ancestry；
2. 在 N 边界持久化 child lineage metadata，至少绑定 child `L1` 与 `ParentWorldVersion=(L0,N)`；该 metadata 不是 Objective `JournalBatch`，不会增加 TransitionCount；
3. Save-B `journal/` 的 active branch 仍命名 `main`，但 `main` 只是 package-local selector，不表示与 Save-A 是同一 branch identity；
4. Save-B manifest 只按 0019 绑定 Definition SHA、world seed、selected-chain SHA、payload codec、`playerCompositionId` 与 closed Player descriptors；current/parent lineage、count 与 last instant 从 checked selected chain 派生；
5. child lineage metadata 会改变 logical selected chain，也可能改变 physical chain/head。不得要求 Save-A 与 Save-B 的 EventAddress、raw Journal bytes、directory digest 或 head 相同，也不得把它们提升为世界身份；
6. Save-B 在同卷 sibling staging 中写完 components、最后写 `save.json`、用 0019 reader self-verify，再 rename 到不存在的 final path；Save-A 与其 Journal bytes 永不修改；
7. Save-B 是 self-contained checkpoint，单独恢复不读取 Save-A，也不依赖外部 parent-chain package。parent WorldVersion 只记录在 lineage metadata 中，不引入 restore dependency。

Atelia adapter 的 export / inspection 必须验证 lineage metadata chain：每个 child 的 `ParentLineageId` 等于此前 active lineage，`ParentTransitionCount` 等于 metadata 所在的完整 batch boundary。package-local `main` 可以在不同 Save 中指向不同 lineage。

## 5. Uninterrupted 等价性

等价性测试使用 deterministic stateful Player 与 fake backend，在相同 Definition、WorldSeed 和输入脚本下比较 uninterrupted control 与 `run -> save -> resume -> continue`。比较分三层：

### 5.1 Session 启动前的 split frontier

以下必须 exact：

- Definition canonical identity、WorldSeed；
- inherited `LogicalInstant + CandidateKey + Facts[]` batch sequence；
- split-point Objective World、transition count、last instant、current ModelTime；
- canonical Player state、closed composition 与 derived comparison hashes；
- Provider / Pack / backend 调用次数为 0。

### 5.2 首个实际 DecisionPoint

test harness 用 capture-only driver 让 control / resumed Session 正常推进 automatic occurrences，直到各自首个实际 DecisionPoint，但不调用 backend。到达时，两边从该 request 与 restored Player state 构造的 `DecisionRequest`、Prompt，以及此前 automatic logical batch suffix 必须 exact。若 `--until-ms` 前没有 DecisionPoint，本层没有 request / Prompt 断言，只比较自动 suffix 与最终 World；production preflight 不执行这次探测。

### 5.3 deterministic fake backend 继续后

以下必须 exact：

- child suffix 的 `LogicalInstant + CandidateKey + Facts[]`；
- 最终完整 batch sequence 与 Objective World；
- 最终 transition count、last instant、current ModelTime；
- 最终 canonical Player state、derived composition hash，以及若存在的 next request / Prompt；
- backend 请求顺序与 fake responses 的消费顺序。

以下明确不比较：

- lineage equality：resume control 的 child lineage 必须不同，并单独断言 parent frontier；
- Atelia EventAddress、physical head、ref identity、raw Journal bytes、component digests；
- branch object identity；每个 package 只需各自断言 selected branch name 为 package-local `main`；
- Host 本次 invocation 的 `CommittedTransitionCount`；uninterrupted 是全程数量，resume 是 suffix 数量；
- 非确定性真实 backend 在首次新调用后的回答。

## 6. 验收

- [ ] 删除或修改 Pack B 后仍能 resume；Provider / Content file access 为 0。
- [ ] production preflight 只 replay / import / validate，不推进 Kernel；Provider / Pack / backend 调用为 0。test-side capture-only driver 在首个实际 DecisionPoint 比较 request / Prompt 时 backend 调用仍为 0；无 DecisionPoint 的 continuation 不伪造 request。
- [ ] 同一 Save-A 分别 resume 为 Save-B / Save-C 时产生两个不同 child lineage；二者 parent 均为 Save-A exact WorldVersion，并允许 divergent suffix 而不产生相等 WorldVersion。
- [ ] child in-memory Kernel 从 `(L1,N)` 与 inherited last instant 启动；Coordination 初始 `C=P=(L1,N)`，Presentation 不重播 prefix cue 且只消费新 suffix。
- [ ] missing / malformed `--until-ms` 或 boundary 早于 restored current ModelTime 在 Session 启动前拒绝；到达该 finite boundary 后按 clean-frontier protocol 终止并发布 successor。
- [ ] deterministic fake backend 下 uninterrupted 与 save / reopen / continue 的 batch semantics、World、canonical Player state、derived composition hash，以及若存在的 next request / Prompt exact；lineage 与 physical EventAddress 不参加等值比较。
- [ ] Save-A 与 source Journal 字节不变；运行中的新状态只存在于 child in-memory authority，停稳后只发布到 Save-B。
- [ ] Save-B active branch 叫 package-local `main`，current lineage 为 child；lineage metadata 记录 exact parent WorldVersion 并受 selected-chain digest 绑定，不在 manifest 双写；lineage metadata 不计作 Objective transition。
- [ ] cancellation、Player / export component failure 或最终 reopen 验证失败不会发布 Save-B；Save-A 始终可恢复。
- [ ] Save-B directory 可单独移动 / 复制并恢复，不存在未声明 parent-chain dependency。
- [ ] source semantic / Ruleset / codec / digest / ancestry / frontier / Player mismatch 均在 Session 启动前拒绝；target export mismatch 在发布 Save-B 前拒绝。

## 7. 非目标

- mid-decision save、未提交 proposal、crash-zero-loss autosave、旧格式 migration、原位续写；
- 从任意历史 prefix 的用户可选 fork CLI；
- production live Session 直接向 Atelia / Save staging 持续写入；
- 把 physical Journal head、EventAddress 或 package digest 加入 `WorldVersion`。
