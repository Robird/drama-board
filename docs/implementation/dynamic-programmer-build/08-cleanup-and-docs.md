# R8 · 删除旧路径、守卫与文档收口

状态：未开始。依赖：R7。产物：全仓库源码、测试与文档。

## 目标

R7 之后旧决策路径只剩零引用的死代码。本轮删除它们、跑完整验证矩阵，并把"已实现"事实回写到项目记忆与设计文档状态行。

## 前置阅读

- [AGENTS.md](../../../AGENTS.md)（项目记忆维护规则）
- 本目录 [README](README.md) 状态表

## 删除清单

先 `rg -l` 确认零生产引用再删；若某类型仍被引用，保留并在回传中说明：

- `src/Player`：`IPlayerDriver.cs`、`NullPlayerDriver.cs`、`RandomPlayerDriver.cs`、`ScriptedPlayerDriver.cs`；`tests/Player.Tests/PlayerDriverTests.cs`（保留 R2 的 DynamicProgrammer 测试）。
- `src/Protocol`：`DecisionRequest.cs`、`Observation.cs`、`PlayerDecision.cs`、`Intent.cs`；`ProtocolKinds.cs` 与 `StableIdentifiers.cs` 中失去引用的类型（`ActionKind/ActionKinds/FactKind/DecisionId` 等——`ActivationId/PassageHandle` 复用的 `StableIdentifier` 机制保留）。
- `tests/Protocol.Tests`：删除 `DtoJsonRoundTripTests.cs` 与 `IntentJsonTests.cs`（旧类型 JSON 测试）；`StableIdentifierTests.cs` 改写为经 `PassageHandle/ActivationId` 断言同一机制。注：`FrozenList` 已在 R1 迁出 `DecisionRequest.cs`，删除该文件不影响新契约。
- `src/Decision.Validation`：`PlayerDecisionValidator.cs`；`tests/Decision.Validation.Tests` 中对应旧测试（保留 R2 新校验器测试）。
- `src/Server/Web/DecisionInput.cs`（R7 已被 `ProgrammingInput` 替代）。
- 零引用的 `using`/注释残留。

## 守卫与验证

```text
rg -n "IPlayerDriver|PlayerDecision|DecisionRequest|DecisionId|ActionKind|FactKind|Observation|ObservedExit|AvailableAction|Intent|WebHumanDriver|DecisionInput" src tests
→ 零命中（`ActionKind` 同时覆盖 `ActionKinds`；大小写敏感，不误伤小写普通词）

dotnet build DramaBoard.Local.slnx -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
dotnet test DramaBoard.slnx
dotnet test DramaBoard.Local.slnx
npm --prefix src/WebUI run build
pwsh -File scripts/Publish-Server.ps1
npm --prefix src/WebUI run test:e2e
```

`ApplicationBoundaryTests`（8+8 项目、Server 六引用）必须不修改而通过。本地不具备 Playwright/Chromium 时按 0025 先例注明未运行项，由 CI 覆盖。

## 文档收口

- `docs/build-log/0026-dynamic-programmer-cutover.md`：简短验收记录（执行了哪些命令、结果、与 §7.2 行为表的对应关系、平台边界），沿用 0025 风格。另记录两项已知语义：同刻预算超限为确定性 fault 且切换后人类可经数十次同刻重交受阻程序触达；KnownPassages 披露静态已知位（远方动态状态不披露，重开条件见工单 README 裁决表）。
- [机制模型](../../design/player-runtime/dynamic-programmer.md)：头部状态行与 §8 改写为"已实施"，指向 0026 与本工单目录；§7 仍标注为"首批验证配置"而非产品定律。
- [运行时理念](../../design/player-runtime/README.md) §8：实现差距段更新。
- `docs/README.md` Current work 与 `PROJECT-STATE.md` 当前焦点：按 AGENTS 规则改写——删除施工待办、保留一句结论与证据链接，不追加过程记录。
- 本目录 README：状态表全部勾选，加一行完成日期。

## 禁止事项

- 不借清理轮重构无关文件、不批量重排格式。
- 不把"首批验证配置"（固定 1000ms 思考耗时、同步响应、单角色）倒写成产品定律写入设计文档。
