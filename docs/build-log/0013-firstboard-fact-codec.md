# Build Log 0013：FirstBoard Fact production codec

> 状态：**Planned implementation slice**
>
> 依赖：[Build Log 0005](0005-game-definition-and-ruleset-bindings.md)
>
> 范围：只把 persistence tests 内的 FirstBoard fact payload codec 提升为 production strict closed-union codec。Journal batch envelope 仍属于 `Journal.Atelia`，不在本 slice 中形成第二份 authority。

## 1. 施工步骤

1. 将 FirstBoard fact payload envelope、kind mapping 与 required JSON converters 移入 Ruleset production code，由单一 `FirstBoardFactCodec` 实现拥有。
2. closed union 覆盖每个当前 Game / Spatial fact shape；不使用 CLR type name、reflection discovery 或 fallback polymorphism。
3. strict reader 精确验证 root / payload property set：unknown、duplicate、missing property，错误 value kind，unknown fact kind，坏 identifier 与 invalid payload 均 fail-fast。
4. 冻结唯一 `FirstBoardFactCodec.Id = "dramaboard.rulesets.firstboard.fact-json/1"`，逐 shape 做 canonical bytes exact round-trip。当前 test-only `firstboard-host-fact-json/5` 不是已发布兼容 epoch，不保留 reader 或 migration。
5. 通过 Journal batch 组合测试证明 `LogicalInstant + CandidateKey + Facts[]` round-trip；`Facts[]` 必须保持原始顺序，不得按 kind / ID 重排，也不得 flatten 为可见 fact prefix。
6. 删除测试项目中的第二份 codec implementation；persistence tests 只消费 production codec。
7. 写明版本纪律：fact wire / restore 不兼容时 bump payload codec；fact meaning、reducer / validator 或未来行为不兼容时 bump `RulesetId`。Journal batch envelope shape 的版本不归本 codec 所有。

## 2. 非目标

- 不实现 Journal package、digest、old codec migration 或 polymorphic type-name JSON。
- 不允许 Content Module 提供 codec。
- 不在 fact codec 中重排 batch 或 facts；canonicalization 只影响单个 fact payload 的 wire representation。

## 3. 验收

- [ ] 所有 current fact shapes exact round-trip。
- [ ] production payload codec ID 精确为 `dramaboard.rulesets.firstboard.fact-json/1`；旧 test-only `/5` 不被 production reader 接受。
- [ ] unknown / duplicate / missing property、unknown kind 与 malformed payload 在 fold 前拒绝。
- [ ] multi-fact batch round-trip 后 facts 数量、原始顺序与 bytes 逐项 exact；交换两个 facts 的 mutant test 必须失败。
- [ ] Runner / Ruleset 只接受编译期声明的 `RulesetId` 与 payload codec ID。
- [ ] 测试代码不再拥有 codec authority；全量测试通过。
