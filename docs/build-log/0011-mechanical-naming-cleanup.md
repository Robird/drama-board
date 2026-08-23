# Build Log 0011：非阻塞命名清理（deferred leaf）

> 状态：**Deferred non-blocking mechanical cleanup**
>
> 可选前置：[Build Log 0009](0009-content-neutral-runner.md)
>
> 本文件不是 0012、Player checkpoint、Objective persistence、Save 或 resume 的 gate。
>
> 范围：只记录两个可独立执行的机械 rename；任何 wire/artifact contract 变化归其语义 slice。

## 1. 为什么不再作为 gate

CLR type、namespace、assembly 和 folder name 不参与 Game Definition hash、Ruleset compatibility、fact wire、Player checkpoint 或 Save identity。把全仓 rename 放在两条 persistence 分支之前，只会让 project references、friend assemblies、Content build output 与大量 tests 的机械冲突阻塞无关语义工作。

延期不等于永久放弃。触发点是：

- 首个对外 API/格式冻结之前；或
- 引入第二 Ruleset 之前；或
- 旧名已持续妨碍新代码理解且当前语义分支已稳定。

即使长期延期，任何 codec/composition ID 都必须是显式稳定字符串，不能使用 CLR full name、assembly-qualified name、namespace 或项目路径。

## 2. Leaf A：public/application vocabulary

```text
ScenarioDefinition     → GameDefinition
ScenarioInstance       → GameInstance
FirstBoardScenario     → FirstBoardRuleset
DemoOptions            → RunnerOptions
DemoLlmComposition     → LlmPlayerComposition
DemoRunManifestWriter  → RunManifestWriter
BoardIds               → FirstBoardKinds       # 此时 author IDs 已在 Content project
```

这是一份纯 C# symbol/file rename。若 0012 先实施，`GameDefinitionCodec` 可以暂时读写仍名为 `ScenarioDefinition` 的 sealed graph；该不协调不是 codec 的语义 blocker。

## 3. Leaf B：project/folder/assembly/namespace

```text
src/FirstBoard                     → src/Rulesets.FirstBoard
src/FirstBoard.Demo                → src/FirstBoard.Runner
tests/FirstBoard.Tests             → tests/Rulesets.FirstBoard.Tests
tests/FirstBoard.Persistence.Tests → tests/Rulesets.FirstBoard.Persistence.Tests
tests/FirstBoard.Demo.Tests        → tests/FirstBoard.Runner.Tests
```

同步两个 `.slnx`、Content ProjectReferences、assembly names、root namespaces、`InternalsVisibleTo` 与测试 using。Ruleset assembly identity 改变后，A/B Content Modules 必须全部重新 build；当前不承诺旧 Content binary compatibility。

Leaf A/B 可以任意顺序执行，也可以只执行其中一个。每个 leaf 单独提交、单独全量 build/test，不与 schema、Ruleset、CLI、Save 或 resume 修改混合。

## 4. 不属于本 mechanical slice 的名称

```text
scenario-definition.json → game-definition.json
```

这是 Runner artifact 与 manifest reference 的 contract 变化，不是纯 CLR rename；归 0012 的 Game Definition codec activation。`dramaboard.game-definition/1` 等 wire identity 同样只由 codec/Ruleset 语义文档拥有，rename 不得改变或 bump 它们。

## 5. 验收

- [ ] rename 前后 A/B canonical Definition bytes、SHA、RulesetId 与所有 codec/composition IDs exact 相同。
- [ ] rename 前后的 scripted captures 与 production-pipeline smoke 语义等价。
- [ ] 每个 leaf 的 diff 只含目标 mechanical rename 和必要 reference 修复。
- [ ] project leaf 完成后，两个 solutions、Content projects、friend assemblies 与 tests 无旧 project path/namespace。
- [ ] 全量 build/test 通过。
- [ ] 任一 leaf 未执行都不阻塞 0012 或后续 Save/Player 工作。
