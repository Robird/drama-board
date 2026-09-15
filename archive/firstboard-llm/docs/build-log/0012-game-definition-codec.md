# Build Log 0012：Game Definition production codec 与 artifact contract

> 状态：**Planned implementation slice**
>
> 依赖：[Build Log 0009](0009-content-neutral-runner.md)
>
> 不依赖 Build Log 0011；CLR/project rename 是非阻塞 leaf。
>
> 范围：完成 Game Definition 的 current-format production read/write contract，并把 Runner artifact 名称收敛为 `game-definition.json`。

## 1. 唯一 codec identity

Ruleset 拥有一个常量：

```text
GameDefinitionCodec.Id = "dramaboard.game-definition/1"
```

canonical JSON root 的 `schema` 精确等于这个常量；schema 与所谓 `gameDefinitionCodecId` 不是两个版本 authority。V1 不再定义第二个不同值。未来 outer manifest 若需要在完整 decode 前 dispatch，只能 peek component root schema，或保存同一常量的严格镜像并交叉验证，不能独立 bump。

Definition wire/canonical mapping 的不兼容变化 bump 这个 ID。同一 bytes 的 Genesis、reducer、validator、Forecast、Plan、Observation 或未来行为意义变化由 `RulesetId` 负责；纯 CLR/assembly rename 两者都不 bump。

## 2. 完成状态

Ruleset 拥有 versioned `GameDefinitionCodec`：

- strict `PeekFormatId` 或等价 metadata-only schema inspection；
- canonical writer；
- strict lossless reader；
- canonical SHA computation。

它服务 run artifact、Save 与 replay verifier，不重新成为 Content authoring loader。Content 仍只通过 C# Provider 构造 Definition。

Runner 写出的 canonical artifact 精确命名为 `game-definition.json`，run manifest 中的 artifact reference 同步更新；当前 runtime artifact 不再生成或引用 `scenario-definition.json`。文件名迁移与 codec writer 在同一 activation 完成，避免两个 artifact authority。

## 3. 施工步骤

1. 将现有 canonical writer 和 SHA 收束进 production `GameDefinitionCodec` 的唯一实现。
2. root `schema` 只读写 `GameDefinitionCodec.Id`，增加 metadata-only peek，拒绝 missing/wrong-type/unknown ID。
3. 实现 strict reader：拒绝未知或重复 property、缺字段、错误 ValueKind、duplicate ID、unknown reference、坏 typed binding、空 prose 与矛盾 Genesis。
4. decode 后必须 `Validate → Freeze → canonical re-encode`；reader 不保留输入 collection order 或非 canonical JSON spelling。
5. 对真实 Pack A/B Definition 做 `bytes → Definition → canonical bytes` exact round-trip，SHA exact 稳定。
6. `DemoRunManifestWriter` 或后继 writer 改用 codec 写 `game-definition.json`，同步 artifact reference 和 tests。
7. old `dramaboard.scenario-definition/*`、`firstboard.duchess-letter/*` 及 `scenario-definition.json` current artifact path 全部 fail-fast/停止生成，不做隐式升级。

## 4. 非目标

- 不读 Content C# source，不提供 JSON authoring workflow。
- 不迁移旧 schema，不建立 codec registry、fallback reader 或同时支持多个 Definition 版本。
- 不实现 fact codec、Save directory transaction、digest manifest、Journal fold 或 Player checkpoint。
- 不做 `ScenarioDefinition → GameDefinition`、项目、assembly 或 namespace rename；0011 可以稍后独立执行。

## 5. 验收

- [ ] `GameDefinitionCodec.Id` 与 root `schema` 是同一个唯一常量，精确为 `dramaboard.game-definition/1`。
- [ ] metadata-only peek 在完整 Definition decode 前稳定识别/拒绝 schema。
- [ ] A/B lossless round-trip、canonical bytes 与 SHA exact 稳定。
- [ ] keyed collection reorder 和等价的非 canonical input 得到相同 canonical bytes。
- [ ] unknown/duplicate/malformed input、坏 binding 或旧 schema 在创建 Genesis 前拒绝。
- [ ] Runner 只写并引用 `game-definition.json`；不再产生 current `scenario-definition.json` artifact。
- [ ] CLR/project mechanical rename 不改变 canonical bytes、SHA、codec ID 或 RulesetId。
- [ ] 全量测试通过。

## 6. 建议提交边界

一个 codec/artifact 语义提交：strict read/write/peek、A/B round-trip 和 artifact rename 同时收敛。不要夹带 CLR/project rename、fact codec 或 Save manifest 设计。
