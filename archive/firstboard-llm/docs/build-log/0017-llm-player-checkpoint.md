# Build Log 0017：LLM Player checkpoint

> 状态：**Planned implementation slice**
>
> 依赖：[Game content and Save boundary](../implementation/game-content-save-boundary.md) 的 Player continuation 边界；不依赖 0011 命名清理。
>
> 可与 0012–0016 并行；0019 同时依赖两条线。
>
> 范围：只让 `Player.Llm` 自身的已提交认知状态可严格 export / import；不处理 Runner Actor slot、wrapper、World frontier 或 Save 目录。

## 1. 持久状态的最小闭包

`LlmPlayerDriver` 会改变下一 Prompt 的已提交 mutable state 只有：

- 当前 `MemoryBank` 中每个 Definition-owned shard key 对应的 **content**；
- `_previousKnownFacts`，即 Prompt `[新近变化]` 使用的上一次观察 frontier。

`_previousKnownFacts` 不能从恢复后的 Objective World 推导。它记录的是上一次成功认知提交所看到的事实，而 World 已可能包含该次决策随后产生的事实；两次 LLM 输出都解析失败而 fallback Wait 时，它还会有意保持更早的 frontier。checkpoint 必须保留这个真实状态，不能用当前 World known facts 覆盖。

以下信息不属于 LLM mutable state：

- ActorId 与 committed decision sequence 属于 Runner slot / Objective World，由 0018 绑定；
- CharacterCard、ReferenceMaterial、shard key / title / maintenance instructions / order 属于 frozen Game Definition；
- backend、model、thinking effort、endpoint identity 与 maintenance mode 属于 Runner resolved composition config；
- trace / profiler counters 与输出文件属于非权威观测面。

checkpoint 只保存 shard key + current content，不复制 Definition-owned card、material 或 shard schema。import 由调用方提供当前 Definition-derived schema，并严格拒绝 missing、extra、duplicate 或 unknown shard key。

## 2. Slot binding 与 checkpoint contract

0017 接收 0018 计算的 opaque `slotBindingSha256`，把它写入 checkpoint。该值把 Player state 绑定到 Definition Actor slot 与 resolved composition，但它是派生的交叉验证断言，不是 Actor、Definition 或 config 的第二 authority。

closed checkpoint 只包含：

```text
checkpointCodecId
slotBindingSha256
memory: ordered key / content
previousKnownFacts
```

codec 使用 strict、canonical encoding。`checkpointFileSha256` 是完整 canonical checkpoint bytes（包含 codec ID、slot binding 与 LLM mutable state）的 SHA-256；0019 只把同一个值写入 checkpoint descriptor，用于 package integrity 与 exact checkpoint comparison。decoded Memory / previous-known-facts 另做逐字段验证，不再计算一个范围不同却名字近似的 `PlayerStateSha256`。checkpoint payload 不内嵌自己的 fingerprint。

credential、client / connection、CancellationToken、endpoint secret、pending Task、monologue / dialogue / trace history 与本地输出路径均不持久化。checkpoint codec ID 只覆盖 bytes 到 Memory / previous-known-facts 的 decode 与 restore mapping；Prompt renderer、driver 或 wrapper behavior 的不兼容变化只 bump 0018 `playerCompositionId`。不再为同一语义叠加多个互相重叠的 compatibility authority。

## 3. Quiescence 与 failure boundary

`FlushMemoryAsync` 不是充分的并发证明：Blocking 模式的 `DecideAsync` 执行期间 `_pendingMemoryMaintenance` 始终可以为空。export 必须在 Runner 已停止新请求后，同时验证：

- 没有 active `DecideAsync`；
- pipelined `_pendingMemoryMaintenance` 已成功 Flush 并提交；
- driver 未 disposed、lifetime cancellation 未触发；
- 当前 driver 没有未解决的 decision、maintenance 或 trace-commit fault。

active / pending / faulted / disposed 任一条件成立都拒绝 export，不尝试保存半完成认知。需要为 driver 增加 single-flight / lifecycle guard，或提供等价的原子 snapshot boundary，避免在 export 检查后又开始一个 request。

import 只解码和验证 closed data，不创建或调用 backend。Runner 在后续 Session composition 阶段注入外部 client，并用 frozen Definition 重建 CharacterCard、ReferenceMaterial、Memory schema 与 maintainers。

## 4. 施工步骤

1. 盘点 `LlmPlayerDriver`、`MemoryBank` 与 maintenance 的 mutable fields，确认下一 Prompt 消费者只有 current Memory contents 与 previous-known-facts frontier。
2. 增加 active / pending / faulted / disposed 状态守卫，定义原子 export boundary。
3. 定义 closed checkpoint record、strict codec、`slotBindingSha256` 验证与唯一 canonical `checkpointFileSha256` 计算。
4. import 时用调用方提供的 Definition-derived shard schema 重建 `MemoryBank`；不访问 backend。
5. 用 deterministic fake backend 做多轮、retry fallback、Blocking 与 Pipelined round-trip。
6. 对同一个 supplied next `DecisionRequest` 比较逐字段状态与 exact Prompt；Objective next request 的恢复由 0018–0020 验收。

## 5. 验收

- [ ] round-trip 后 Memory contents、previous-known-facts 与 `checkpointFileSha256` exact；同一 supplied request 渲染出 exact Prompt。
- [ ] 删除或重置 previous-known-facts 的 mutant 会令 `[新近变化]` 验收失败。
- [ ] pending / unflushed、Blocking active decision、faulted、disposed 与 slot-binding / codec mismatch 稳定拒绝。
- [ ] wrong / missing / extra / duplicate shard key 与 strict-codec unknown property 拒绝。
- [ ] retry 两次失败后的 frontier 滞后状态仍 exact round-trip。
- [ ] export/import/comparison backend calls 为 0。
- [ ] checkpoint bytes 不含 CharacterCard、ReferenceMaterial、credential、secret、endpoint 或 trace；全量 `Player.Llm` tests 通过。
