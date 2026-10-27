# 0026 Dynamic-Programmer 切换验收记录

状态：[施工工单](../implementation/dynamic-programmer-build/README.md) R1–R8 全部完成；2026-09-19，Windows 实跑。证据在 `artifacts/0026/`，被 Git 忽略、可按下述命令重建。

## 本轮基线（R8 删旧与守卫）

- R8 在 R7 提交（`099e80f` 实现、`78ea4cc` 文档）后的干净工作树执行。
- 删除旧 Intent 单发路径：`IPlayerDriver` 与三个 driver、`DecisionRequest/Observation/PlayerDecision/Intent/ProtocolKinds`、`PlayerDecisionValidator` 及其旧测试（Player/Protocol/Decision.Validation 共 15 个文件）；`StableIdentifiers.cs` 修剪为仅保留 `StableIdentifier` 机制（`ActivationId`/`PassageHandle` 复用，R1 已把 `FrozenList` 迁出 `DecisionRequest.cs`）。
- 守卫：`rg -n "IPlayerDriver|PlayerDecision|DecisionRequest|DecisionId|ActionKind|FactKind|Observation|ObservedExit|AvailableAction|Intent|WebHumanDriver|DecisionInput" src tests` 仅剩 3 处现行设计名——`OccurrenceView.FactKinds` 属性与 `FreePlayFactKind` 方法（视图合同与 union 分派，非旧 Protocol `FactKind` 类型）；词边界判读下旧类型零命中。工单原文的 `FactKind` 模式按此源码事实修正。
- 偏离记录：`StableIdentifierTests.cs` 未按工单"改写"而是直接删除——R1 的 `ProgrammingIdentifierTests` 已用 `PassageHandle`/`ActivationId` 断言同一机制（等值语义、空白、257 超长、256 边界、控制字符），改写只会造成重复测试。

## 验证矩阵（全部通过）

- `dotnet build DramaBoard.Local.slnx -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify`：零警告、零错误。
- `dotnet test DramaBoard.slnx` 与 `dotnet test DramaBoard.Local.slnx`：各 8 个项目全绿，共 304 项（Protocol 36、Decision.Validation 12、Player 6、Host 2、Kernel 118、Spatial 58、Player.Agency 8、Server 64）。`ApplicationBoundaryTests`（8+8 项目、Server 六引用）未修改而通过。
- `pwsh -File scripts/Publish-Server.ps1`（npm ci、tsc/vite build、发布 `-warnaserror -p:DurableGraphSchemaHistoryMode=Verify`）通过；`npm --prefix src/WebUI run test:e2e` 1 项 Chromium 用例通过（6.5 秒），发布 apphost 自临时目录启动并由测试清理。
- 本轮只实跑 Windows（Node v24.11.1、npm 11.6.2）。CI 已配置 Ubuntu/Windows 的 Node、前端、.NET、发布及 Chromium 步骤；未触发远端 CI，不声称 Linux 已通过。

## 与机制文档 §7.2 行为表的对应

| 行 | 证据 |
| --- | --- |
| 自动执行、改写未来、三个边界、默认边界、正常待机、保留安排、受阻与重试、思考与因果 | kernel 级 `FreePlayProcessRuleTests`（10 用例，含 2500 ms 中途世界事件与同刻预算 fault） |
| 写权限、输入与故障、信息连续性（会话与 HTTP 级） | `FreePlaySessionTests`（11 用例）、`HttpBoundaryTests`（6 用例，含解析严格性、禁字段、并发单赢家与正向多指令程序） |
| 信息连续性（刷新）与产品面 | e2e [movement.spec.ts](../../src/WebUI/e2e/movement.spec.ts)：快捷移动、Think 笔记追加、清空并待机、刷新保持 activation/工作区/待执行程序 |

## 随本切换定稿的已知语义

1. **同刻预算是确定性 fault**：`MaxTransitionsPerModelTime=100` 耗尽时 `LogicalInstantRules.Propose` 抛错、Step 零提交；会话以 faulted 呈现、保留最后合法世界、无孤儿 pending。切换后人类可在受阻地点经数十次原样重交受阻程序触达——每次重交都是新的明确授权，不是无进展循环；kernel 级 `SameTickBudgetExhaustionIsDeterministicFault` 覆盖，不做提交前计数拒绝。
2. **KnownPassages 只披露静态已知位**：请求材料的方向位取已知图 `InitialEntryAccess`，不读世界实时 override——远方动态入口状态不属合法披露；动态关闭由开始时 planner 拒绝并以 `MoveBlocked` 原因表达。重开条件见[工单 README 裁决表](../implementation/dynamic-programmer-build/README.md)。

## 启动与边界

仓库根执行 `pwsh -File scripts/Publish-Server.ps1`，运行 `artifacts/server/DramaBoard.Server.exe`，入口 `http://127.0.0.1:5080/player` 与 `/dev`；完整命令见[根 README](../../../../README.md)。本批不含 LLM Programmer、持久化续局闭包、主动重新唤醒、多角色与途中接管，重开条件见 [PROJECT-STATE](../../../../PROJECT-STATE.md)。
