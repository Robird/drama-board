# 决策回合继承与操作身份：实施验收

> 历史命名说明：本文保留当时的 ResourceCycle / Cycle 类型、命令与验证记录。当前应用已重命名为 Playground；现行入口与持久身份边界见[重命名说明](../../docs/implementation/playground-rename.md)。旧名称源码链接固定到重命名前的 Git 版本。

2026-10-28。对应[设计与重构合同](../design/decision-context-handoff.md)。本片已实现并通过下列工程、恢复及独立 Console 试玩验证；没有真实 LLM 效果、端点 ID 映射或 prompt-cache 命中证据。

## 交付范围

- 生存模式持久保存 ContextSequence、固定 Incoming、本轮 DraftHandoff 和放行后的 Pending；同一活动可以跨上下文，零耗时操作不改写上次结果。
- `handoff <文字>` / `handoff --clear` 属于角色命令；交接最多 2,000 个 Unicode 标量，只传到下一回合一次。元命令仅使用冒号前缀，`:help` 枚举布局。
- 控制器返回 `CycleControllerOperation`；Console 在解析前隐式生成 ID，动作事实与结果保留同一个 ToolCallId。宿主 `operations.jsonl` 覆盖查询、拒绝和元命令；游戏效果仍由 E/S 决定。
- ContextOpened 使用已有请求仲裁，不新增预测 owner；同刻完成/警告先结算，终局直接封存，不虚构新决策回合。所有新容器随现有 Business 根发布。
- 首次与新上下文清屏，同轮追加完整 UI。两种布局共享结构化历史投影，不增加位置快照或逐行排版测试。
- GameRevision 5；旧开发存档与旧裸命令控制器 API 不兼容。Kernel、Spatial、Inventory 及兄弟 Completion 项目未改动。

## 工程证据

主线程执行 Release、warnings-as-errors 与 Schema Verify 的完整 solution 构建，零警告/错误。完整测试运行 594 项通过（Host 2、Inventory 27、Kernel 131、ResourceCycle 206、CLI 137、Spatial 91）。随后新增的两项轨迹测试通过，覆盖采集中途继续及包含天气/警告/死亡的 incremental/full 逐事件对照；本片共 596 个不同测试用例通过。最后呈现与物品名称投影修订后，重新构建并运行 CLI 全部 137 项，全部通过。

```powershell
dotnet build DramaBoard.slnx -c Release --no-restore -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
dotnet test DramaBoard.slnx -c Release --no-build --no-restore -m:1
dotnet test tests/ResourceCycle.Tests -c Release --no-build --no-restore --filter FullyQualifiedName~DecisionContinuityTrajectoryTests
dotnet test tests/ResourceCycle.Cli.Tests -c Release --no-restore -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
```

三个真实进程 smoke 均通过。查询的宿主记录允许增长，脚本比较领域 E/S 文件的字节不变，不再错误地要求整个目录不变。

| Smoke | 本轮工件 |
| --- | --- |
| 批处理、增量/全量与冷开 | 7483e26dcaf04afc9b2ab311dc33badc（历史本地工件：`artifacts/resource-cycle-smoke/7483e26dcaf04afc9b2ab311dc33badc/`，当前检出不含该工件） |
| 多人采集 | 7dddfe6d01cf468f8b7e689052c9bcda（历史本地工件：`artifacts/resource-cycle-play-smoke/7dddfe6d01cf468f8b7e689052c9bcda/`，当前检出不含该工件） |
| 生存、天气、两出口与死亡 | ef89a2a7c2764eaeb84f9529cb5acb2d（历史本地工件：`artifacts/resource-cycle-survival-smoke/ef89a2a7c2764eaeb84f9529cb5acb2d/`，当前检出不含该工件） |

关键测试入口：

- [DecisionContinuityTests](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Tests/DecisionContinuityTests.cs)：同刻完成与警告、途中交接不确认警告、Unicode 边界和角色隔离。
- [DecisionContinuityBoundaryTests](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Tests/DecisionContinuityBoundaryTests.cs)：同刻新回合、未启用模式原调度、编号上限仍允许空闲确认。
- [DecisionOutcomeSummaryTests](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Tests/DecisionOutcomeSummaryTests.cs)：五类采集结束原因、all 目标冻结、天气实损/庇护零损失、昼夜计数、饱和及未知目的地。
- [DecisionContinuityTrajectoryTests](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Tests/DecisionContinuityTrajectoryTests.cs)：采集先得 1 份、继续后再得 3 份，累计 4 份且活动身份不变；生存完整终局逐事件对照。
- [DecisionContinuityPersistenceTests](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Tests/DecisionContinuityPersistenceTests.cs)：真实 S-only 冷开及 handoff、放行、ContextOpened、死亡四类 pending E 恢复，不调用控制器、不重复消费。
- [CommittedOperationRecoveryTests](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Tests/CommittedOperationRecoveryTests.cs)：已完成历史查询、pending 排除、跨页、跨批同文本 ID，以及非零模拟版本的仓库根。
- [OperationRecoveryIntegrationTests](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Cli.Tests/OperationRecoveryIntegrationTests.cs)：真实 Journal 与 DG 的 E-only/S 后缺结果恢复，只补同 ID 语义结果；丢失查询结果标中断，不重查冒充原回答。
- [OperationJournalTests](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Cli.Tests/OperationJournalTests.cs) 与 [PlayControllerOperationTests](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Cli.Tests/PlayControllerOperationTests.cs)：Received 写入/flush 失败不执行、结果配对、批次冲突、精确结果保留、尾部截断/完整坏行拒绝、单写者、EOF 与显式退出。
- [PlayPresentationTests](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Cli.Tests/PlayPresentationTests.cs) 与 [ContinuityViewTests](https://github.com/Robird/drama-board/blob/c1de4afc96957ff02976a98ee40e3cec1eb8b440/tests/ResourceCycle.Cli.Tests/ContinuityViewTests.cs)：上下文身份清屏与语义信息齐全，不固定排版位置。

实现期两路独立只读审查检查领域流程与操作恢复；发现的“历史扫描把仓库根误认为模拟版本 0”已修复并加测试。审查本身不替代上述运行验证。

## 独立试玩与修订

读屏试玩者未读玩法源码/地图，自主走营地→溪流湾→废弃林屋→巡山信号站，在第 1 天 09:30 获救，温暖 93、食物 42.5、饮水 32。途中 21:00 温暖警告后补水：当前饮水 42→87，历史仍为 62→42；覆盖草稿、切布局、退出重开均保留回合 5、21:30 抵达期限及两份不同时间的状态。继续后交接只传一次，后续未写则显示无。证据：逐步输入、完整输出、存档与反馈（历史本地工件：`artifacts/decision-context-handoff/playtest/independent-20261028/feedback.md`，当前检出不含该工件）。

另一位试玩者使用真实 Windows PTY，验证 handoff/use/help/layout/details 不清屏；wait/continue 先输出接受结果与发生项，再以 ESC 光标/擦行序列清屏进入新上下文。等待期间警告保留原期限，最终饥饿终局显示封存结果且退出。证据含启动输出及实际控制符：原始试玩（历史本地工件：`artifacts/decision-context-handoff/terminal/pty-agent-20261028/feedback.md`，当前检出不含该工件）、修复后新进程复验（历史本地工件：`artifacts/decision-context-handoff/terminal/pty-agent-20261028-retest/feedback.md`，当前检出不含该工件）。接受提示与清屏之间没有人为停顿，证据证明顺序，不保证人眼有充足时间阅读旧屏。

据试玩完成四项修订：

1. 等待完成后仍保留活动身份，不能据此显示“仍未结束”；完成状态只来自 ActivityState，旧矛盾已由真实 PTY 新进程复验消失。
2. 冷开明确“之前的控制台对话未重放”，并说明行动结果、交接和草稿已恢复。
3. 终局使用“终局结果”标题，避免把保持源回合编号误读为漏增编号。
4. 当前物品用完后，历史物品仍保留显示名称；只补入本角色历史快照中的已知物品，不暴露远处库存。

主线程又运行中文 handoff→wait→use→exit 并检查磁盘 4 对 ID、固定历史及物品名称；重开已获救存档验证最终标题与恢复文案。原始输入、输出和存档见最终现场核验（历史本地工件：`artifacts/decision-context-handoff/main-verification-2ddee3498400499f8249b79f4244e437/`，当前检出不含该工件）。

## 限制与后续条件

- 80×24 终端完整 UI 超过一屏，需上翻读取摘要；固定规则与全量重复输出仍偏冗长。后续按体验打磨，增量渲染仍是独立切片。
- 本工具的 PTY 中文输入出现过 `@` 替换；重定向中文提交、落盘与冷开正常。现有证据不能将其归因产品编码；PTY 复验以 ASCII 文本验证清屏，未冒称中文终端输入已验收。
- 记录流的故障注入与文件恢复不等于掉电资格认证。E、S、宿主记录不是跨存储原子事务；缺原文时只能补语义结果并标 `ExactResult=false`。
- 工件在被忽略的 `artifacts/` 下，保留于当前工作区；Git 中保留合同、测试与本记录。此为代理试玩，不是外部人类用户研究或真实 LLM 效果比较。
