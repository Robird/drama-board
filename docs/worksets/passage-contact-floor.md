# Passage 接触时间向下取整

用户已采纳：contact 采用数学 floor，arrival 保持 ceil。权威语义见[Graph Spatial §3.5](../design/graph-spatial-world.md#35-passage-contact目标法则与第二竖切)；本文件保存本片验收边界与证据。

## 实施边界

- 保留精确交点、共同有效窗口、严格内部与正 tau 检查；负数按数学 floor，整数交点不变。
- Contact 是提前开放的交互机会，回应可以改变运动；不引入实体尺寸或 fraction DTO。
- 保留 `AnchorTime < at` 的 Reverse 条件；出生／重锚同刻参与者只能 Continue。新运动代数的有效 contact 可同刻再出现，不增加粗粒度 suppression。
- 到达仍 ceil；接触和同刻回应严格先于参与者到达。无关到达、entry mutation、其他 contact 继续由原 Kernel 仲裁。
- FirstBoard 规则 `/2 → /3`，拒绝旧规则 S/E-head 续局；不改 Schema、不转换旧存档、不改上游 DurableGraph。

## 验收映射

| 要求 | 实现与证据入口 |
|---|---|
| 正负数 floor、整数、出生刻、配对消费 | [PassageContactCalculator](../../src/Spatial/Internal/PassageContactCalculator.cs)、[Spatial contact tests](../../tests/Spatial.Tests/Contacts/PassageContactTests.cs) |
| 提前接触、Continue-only、同刻新代数接触 | [FirstBoard encounter tests](../../tests/FirstBoard.Tests/PassageEncounterHostTests.cs) |
| 规则绑定拒绝旧版本 | [ScenarioDefinition](../../src/FirstBoard/ScenarioDefinition.cs)、[定义测试](../../tests/FirstBoard.Tests/ScenarioDefinitionTests.cs)、真实旧程序写出的存档 |
| pending 仅处理已保存事实、冷开结果一致 | [分数交点持久化回归](../../tests/FirstBoard.Persistence.Tests/ContactFloorPersistenceTests.cs) |

## 验证结果

2026-09-12，Windows / .NET 10：

- `dotnet build DramaBoard.Local.slnx -m:1 -nr:false -warnaserror -p:DurableGraphSchemaHistoryMode=Verify`：零警告、零错误；64 份 Schema history 的路径、数量与 SHA256 保持不变。
- `dotnet test DramaBoard.Local.slnx --no-build --no-restore -m:1 -nr:false`：11 个程序集，527 项通过、零失败、零跳过。包含新分数交点的真实 E-head 恢复，以及原独立进程恢复回归。
- 用改动前保留的实际 process consumer 分别写出 `/2` S-head 和 E-head，新程序 `open` 均明确报告不支持 `firstboard.duchess-letter/2`；拒绝前后所有存档文件的路径和 SHA256 一致。
- 独立复审无未解决问题；`git diff --check`、受影响本地链接与锚点检查通过。未运行远端 CI、Linux 或真实 LLM 服务。

一次性旧程序、存档和日志保留在忽略目录 `artifacts/contact-floor-20260912-164123/`；最终结果为 `final-build.log` / `final-tests.log`。`legacy-create-rejected.log` / `legacy-pending-rejected.log` 记录旧规则拒绝。旧分支测试保留人工构造过时 pending 的 cleanup 见证，不再把它描述为新规则下的自然 arrival 竞争。
