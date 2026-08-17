# Design Note 005：分块 MemoryBank——不同稳定性的独立认知维护

**状态：WP21 已采纳。日期：2026-08-17。**

## 问题

WP13 的单块 Memory 每轮由角色完整重写。它简单且有界，但把近期处境、长期承诺、猜想和关系判断置于同一个遗忘概率中。WP19 真场里，爱丽丝第一次整体重写便摘要掉等待约定；这不是“场景提示不够强”，而是不同更新频率的信息共享一个粗粒度生命周期。

固定栏目能降低空栏概率，但一次生成仍同时负责所有栏目。向量数据库和知识图谱则过早引入检索、实体归一和一致性问题。当前甜点方案是固定少量文本分块，各块由同一种 maintainer 以不同维护 prompt 独立更新。

## 裁决：MemoryBank + 独立 maintainer

`MemoryBank` 是按稳定 key 排序的不可变快照，每个 `MemoryShard` 含：

- `Key`：程序路由身份；
- `Title`：角色可读栏目名；
- `MaintenanceInstructions`：该类认知的更新频率与保留策略；
- `Content`：第一人称自由文本。

FirstBoard 原型采用四块，但 Player.Llm 不硬编码这套 schema：

1. `working_context`：当前处境与未决线索，更新快；
2. `commitments`：承诺、期限和多步计划，未完成时默认保留；
3. `beliefs`：猜想、来源、反证和置信变化；
4. `relationships`：信任、戒备、情绪与社会债务，更新慢。

分块允许重叠，不追求知识图谱式正交。少量有意义的冗余能抗摘要遗忘。

## 每轮认知流程

```text
ReferenceMaterial + old MemoryBank + Observation
                     │
                     ▼
       actor call: 独白 / 行动 / 台词 / 记忆提议
                     │
              frozen old snapshot
          ┌──────────┼──────────┬──────────┐
          ▼          ▼          ▼          ▼
       working   commitments  beliefs  relationships
       maintainer maintainer  maintainer  maintainer
          └──────────┴──────────┴──────────┘
                     │
                     ▼
              merged new MemoryBank
```

主 actor 不再输出下一轮完整 Memory，而输出“本轮希望记住、修正或忘却的要点”。这保留角色主动产生猜想的入口。每个 maintainer 都以该角色身份工作，不是客观事实整理器，并看到：

- 同一个旧完整 MemoryBank；
- 该角色可查阅的长期材料；
- 当前 Observation/KnownFacts 与拒绝反馈；
- 本轮独白、台词、记忆提议；
- 刚选择但尚未被世界确认成功的行动。

maintainer 只能返回自己的：

```json
{"operation":"keep"}
```

或：

```json
{"operation":"replace","content":"更新后的完整分块"}
```

`keep` 是一等短路，不要求模型为了填栏目而改写稳定内容。多个 maintainer 基于冻结旧快照并行运行，所以本轮不会读取其他分块的“半更新”；跨块传播可在下一轮发生。

## 失败、提交与信息边界

- 单块超时、异常、非法 JSON、错 key 或空 replacement 都局部降级为 `FallbackKeep`。
- 一个 maintainer 的失败不清空该块、不撤销有效行动，也不阻止其他块提交。
- 外层取消仍传播，不被吞掉。
- `LlmTurnTrace` 记录角色记忆提议、合并后的 MemoryBank 以及每块 Keep/Replace/FallbackKeep，供戏剧记录观察。
- maintainer 只接收该角色的私有输入，不读全局 journal 或其他角色 Memory。
- 刚选择的 Intent 不是已发生事实；世界结果只能由下一轮 Observation/行动回执确认。

## 后端与成本

决策模型和认知维护模型解耦。Demo 增加独立 memory backend 配置，默认跟随 Alice；混合场可以由 DeepSeek 并行维护两个角色的全部分块，同时让 Alice/Bob 使用不同决策模型。maintainer 是压缩与自我整理部件而非行动者，但仍带角色卡和第一人称维护约束，避免变成全知旁白。

一次 actor turn 从一次调用增加为一次决策调用 + N 次分块维护调用；N 个维护调用可并行。当前优先观察认知质量，暂不增加 dirty router、分层调用频率或 batched maintainer。真实延迟/收益将决定后续是否优化。

### WP23：调用观测与延迟提交流水线

WP23 为每次后端调用补充统一 response metadata：OpenAI-compatible adapter 读取 provider 返回的 prompt/completion/reasoning/cache hit/cache miss token，Codex adapter 区分本地顺序门的 queue time 与 app-server service time。Demo 再按 actor、role decision / memory maintenance、shard、backend、model 与 thinking effort 实时写 `llm-runtime.jsonl`，完成或取消时生成分组摘要。实时逐调用写入意味着整体超时也不会丢掉已经完成的测量。

四个 shard 早已由 `Task.WhenAll` 并行；新实验优化的是 actor turn 之间的空档。`MemoryMaintenanceMode.Pipelined` 在 role decision 解析成功后启动四块维护并立即把 `PlayerDecision` 交回 Host，不让它们阻塞其他 actor 的 role call；同一 actor 下次 `DecideAsync` 开头必须先 join、合并并提交上轮 MemoryBank。场景结束必须显式 `FlushMemoryAsync`，driver disposal 会取消仍未完成的私有维护。冻结旧 bank、单块 fallback、下一轮不读半提交状态等语义不变。

该模式保留为 opt-in，默认仍是 `Blocking`。首个同 seed、同配置的 3+3 turn 混合样本中，blocking / pipelined 的观测调用跨度为 88.1s / 90.3s；pipelined 确实把峰值并发从 4 提到 5，但第二局生成的 completion/reasoning token 分别多约 38%/47%，并发请求本身也变慢。用 blocking 样本的同一组实测调用时长做反事实调度，关键路径预计仅缩短 14.7%，因为该轨迹有 Bob 连续三次决策而非严格 Alice/Bob 交替。结论是“流水线机会真实存在，但不是无条件翻倍”；需要多 seed 样本和 effort matrix 才能判断默认策略。

## 非目标

- 不建立向量检索、知识图谱、事实实体归一或全局一致性协调器。
- 不强制角色保留承诺；角色可以明确完成、放弃或取代它，只是不应被无声摘要掉。
- Memory 仍是 Player 私有状态，不进入世界 authority/journal。
- 不要求不同分块完全无重复。

## 验收

1. 每个 maintainer 看到同一个旧完整 MemoryBank，但只能替换自己的 key。
2. `commitments` 可在其他块变化时返回 keep，约定不会静默消失。
3. 单块非法输出局部 FallbackKeep，其他块照常提交。
4. ReferenceMaterial 在所有 maintainer 中仍保持“来源原文”语义，不被自动升级为信念。
5. 真混合场多轮后，约定仍存在或留下明确完成/放弃理由，而非因摘要缺失。
