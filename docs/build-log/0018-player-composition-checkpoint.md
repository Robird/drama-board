# Build Log 0018：Runner Player composition checkpoint

> 状态：**Planned implementation slice**
>
> 依赖：[Build Log 0009](0009-content-neutral-runner.md) 的 Definition-driven Actor slots / resolved config，与 [Build Log 0017](0017-llm-player-checkpoint.md) 的 LLM state contract
>
> 范围：描述完整 Actor-slot composition，绑定 Objective decision frontier，并恢复 Runner-owned wrapper behavior；不写复合 Save 目录。

## 1. Versioned closed composition

每份 capture 只有一个 versioned `playerCompositionId`，例如：

```text
dramaboard.firstboard.player-composition/1
```

它是 closed union 的格式与恢复兼容性标识，不是运行实例 fingerprint。reader 只接受当前精确值以及当前代码声明支持的 driver / wrapper variants；未知 kind、未知字段或未来版本 fail-fast，不建立 runtime registry。

Definition 中每个 Actor 恰有一个、按 ActorId ordinal canonical 排序的 slot：

```text
actorId
driver =
    Human
  | Random(seed)
  | Null
  | Llm(
        canonical decision-backend config,
        canonical memory-backend config,
        memoryMaintenanceMode,
        checkpointFileSha256,
        checkpoint payload)
wrappers = [] | [ DecisionBudget(maxTurns) ]    # outer-to-inner，顺序有语义
capturedDecisionSequence
```

union variant 已唯一决定是否需要 state payload，不再保存可与 `driver.kind` 矛盾的 `stateful` boolean。Human、Random、Null 不写伪 checkpoint；Random 虽无 mutable state，seed 仍是决定未来行为的 canonical config。`ScriptedPlayerDriver` 包含 delegates，只用于 integration tests，V1 production capture 必须拒绝它，而不是发明 script persistence。

## 2. Canonical non-secret config

composition 保存每个 slot 已解析完成的 effective config，而不是 CLI default / override 的来源：

- LLM decision 与 memory backend kind、model、thinking effort、safe endpoint identity；
- `MemoryMaintenanceMode`；
- Random seed；
- wrapper kind、顺序与 `maxTurns`。

credential、API key、secret query / user-info、credential env value、client / connection、command process、CancellationToken、output path 与 profiler / trace state 不进入 composition。外部 credential 只在创建 resumed Session 时重新注入；它不可能也不应通过保存 secret hash 来证明“与旧 key 相同”。Runner 必须验证 runtime backend 的 canonical non-secret config 与保存值相同，并在创建 Session 前拒绝 model、thinking effort、safe endpoint identity 或 maintenance mode 的漂移。

0018 对 canonical 完整 capture 只计算一个 `PlayerCompositionSha256`。不再同时维护语义重叠的 slot fingerprint、roster fingerprint 与 config fingerprint：

- roster 完整性直接与 frozen Definition Actor set 比较；
- 0017 `checkpointFileSha256` 覆盖完整 canonical checkpoint bytes；decoded LLM mutable state 逐字段验证，不另算 state-only hash；
- `slotBindingSha256` 只用于把该 state 绑定到 `playerCompositionId + definitionSha256 + actorId + canonical driver config + canonical wrappers`；
- 0017 `checkpointFileSha256` 在 0019 的 checkpoint descriptor 中只持久化一次，不再叠加第二个 raw/state digest。

这些 hash 都是 canonical data 的派生验证值，不是可独立选择的配置或状态 authority。

为避免 hash cycle，`canonical driver config` 在 slot binding 中只指 driver variant 与 resolved non-secret immutable config；它明确排除 checkpoint bytes、`checkpointFileSha256`、`slotBindingSha256`、`capturedDecisionSequence` 与任何 aggregate hash。计算顺序唯一是：slot binding → canonical checkpoint bytes / file SHA → complete composition / derived aggregate SHA。

## 3. Runner Actor-slot guard

在所有 composition 最外层增加 Runner-owned Actor-slot guard；它是恢复完整性设施，不是可由 Save 任意组合的 wrapper kind。它必须：

1. 固定一个 Definition ActorId，并拒绝其他 `request.ActorId`；
2. 保证同一 slot single-flight，向 capture 暴露 active / returned-decision frontier；
3. new run 从 Objective sequence 0 初始化，resume 从 folded World 的 committed decision sequence 初始化；
4. 仅在 inner driver 成功返回 decision 后推进 returned frontier；
5. capture 时要求没有 active request，且 returned frontier 精确等于 committed `World.Actor(actorId).DecisionSequence`。

`capturedDecisionSequence` 保存在 slot capture 中，用于把 composition / Player payload 绑定到本次 World frontier。它不是 Player 自己拥有的 sequence；唯一 authority 仍是 0019 replay / fold 得到的 World value。reader 必须在 import Player state 前验证二者相等，然后用 folded value 初始化新的 slot guard。

该 guard 使以下真实窗口可被拒绝：Player 已返回并改变私有状态，但 Kernel 在 Journal publish 前取消或失败，此时 returned frontier 超前于 committed World，不能称为 clean checkpoint。

## 4. Wrapper state 的最小化

当 slot descriptor 含有当前 `DecisionBudgetPlayerDriver` 时，它从 genesis 起包裹同一 Actor，且 resume 不允许更换 composition。因此在 clean frontier：

```text
turns = min(World.DecisionSequence, maxTurns)
forcedSceneEndCount = max(0, World.DecisionSequence - maxTurns)
```

`maxTurns` 属 canonical wrapper config；两个 counters 都可由唯一 Objective authority 推导，不再创建 wrapper checkpoint payload。capture 必须读取 live counters 并验证上述关系；不一致表示取消、inner fault 或 composition lifecycle 已偏离，稳定拒绝。restore 用 folded World sequence 推导 counters，因此 split 前后 budget behavior 与累计 forced count 连续。

V1 明确禁止中途添加、删除或重排 wrapper。未来若允许 mid-lineage composition change，上述推导前提失效，届时再为 wrapper 增加独立 state contract，而不是提前保留第二份 counter authority。

## 5. Capture 与 restore

capture 的调用顺序是：

```text
停止产生新 Player request
→ 等待 Kernel / slot request 全部退出
→ Flush 所有 LLM maintenance
→ 验证 slot returned frontier == committed World decisionSequence
→ 验证 Budget derived-state invariant
→ export 0017 LLM state
→ canonicalize closed composition
→ 计算唯一 PlayerCompositionSha256
```

任一 Player、maintenance、trace-commit 或 wrapper fault 都使 capture 失败；不能在错误后把当前字段拼成看似 clean 的 checkpoint。

restore 先 strict decode 为不持有 backend 的 verified composition plan。它根据 frozen Definition 验证 exact Actor set、计算 slot bindings、验证 checkpoint file SHA、decoded state 与 saved config；0019 在 Objective fold 后提供 authoritative decision sequences。只有全部验证完成，Runner 才注入 credential / client、构造 drivers 与 outer slot guards。decode、validation、state import，以及对调用方提供的同一 request 所做的 Prompt comparison 均不发出 backend request。

## 6. 施工步骤

1. 定义 versioned `playerCompositionId`、strict closed driver union、ordered wrapper descriptors 与 canonical config encoding。
2. 将 Runner options 对 Definition 做两阶段绑定，capture 只保存 resolved per-Actor effective config。
3. 实现 outer Actor-slot guard 及 active / returned frontier 检查。
4. 为 DecisionBudget 增加 live-state inspection、derived invariant validation 与从 World sequence 恢复的构造路径。
5. 组合 0017 LLM payload，计算 slot binding、`checkpointFileSha256` 与唯一 `PlayerCompositionSha256`。
6. 无 backend 地重建 verified plan；对同一个 supplied request 比较 Prompt、Random 与 budget behavior，之后再测试 runtime client injection。Objective 调度产生“下一 request”的职责留给 0020 Session。

## 7. 非目标

- 不建立通用 `ISaveParticipant`、driver factory 或 checkpoint registry。
- 不保存 Scripted delegates、Human pending input、backend conversation / connection 或 trace / profiler state。
- 不写 save manifest / directory、不计算 raw file digest；这些属于 0019。
- 不允许 resume 时改变 driver kind、wrapper graph 或 canonical non-secret config。

## 8. 验收

- [ ] Definition 每个 Actor 恰有一个 canonical slot；missing / extra / duplicate Actor 稳定拒绝。
- [ ] Human / Random / Null / Llm union round-trip；只有 Llm 带 0017 payload，Random seed 精确保留。
- [ ] unknown composition version、driver、wrapper、codec、字段或非法 wrapper order fail-fast。
- [ ] wrong Actor request、并发 request、active capture 与 slot-binding mismatch 拒绝。
- [ ] `capturedDecisionSequence` 与 folded World mismatch 在 Player import 前拒绝；它不被当作 World authority。
- [ ] 在 `maxTurns - 1`、`maxTurns`、`maxTurns + 1` frontier split 后，next backend-call / forced-Wait behavior 与 control exact；live counter invariant 被故意破坏时 capture 拒绝。
- [ ] 交换两个 Actor 的 LLM payload 会因 slot binding 失败；改变 resolved backend/model/effort/endpoint/mode 会在 Session 创建前拒绝。
- [ ] `PlayerCompositionSha256` 是唯一 aggregate fingerprint；不生成重复 slot / roster / config fingerprints。
- [ ] decode、import 与 comparison backend calls 为 0；stateless composition 不产生伪 payload；全量测试通过。
