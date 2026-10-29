# Human Console 元命令边界验收

日期：2026-10-28。对应[实施合同](../design/console-command-boundary.md)，范围仅为 CLI 输入、呈现、会话组合及测试；领域、E/S、Schema、GameRevision 与操作恢复协议未修改。

## 实施结果

- [CycleCommandParser](../../src/ResourceCycle/CycleCommandParser.cs) 只产生领域行动，混合 `PlayInput` 已删除；冒号输入只走 [ConsoleMetaCommands](../../src/ResourceCycle.Cli/ConsoleMetaCommands.cs)。
- `HumanDefault` / `None` 是会话构造时显式注入的不可变目录，同一实例控制解析与提示；没有新增 CLI 参数、热切换或 LLM 适配器。
- [IPlayLayout](../../src/ResourceCycle.Cli/IPlayLayout.cs) 仅保留游戏观察、更新及 `RenderActionHelp`。提示符、阶段说明和元命令提示由 [PlayPresentationSession](../../src/ResourceCycle.Cli/PlayPresentationSession.cs) 组合；终局不广告后续输入。
- 布局/模式帮助来自实际注册选项；`--play` 仅列 full。重同步先组合完整答复，再记录日志并写出，保留成功 Flush 后安装基线的合同。

## 工程与独立审查

- `dotnet build DramaBoard.slnx -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify`：成功，0 警告、0 错误，Schema Verify 通过。
- `dotnet test tests/ResourceCycle.Cli.Tests/ResourceCycle.Cli.Tests.csproj -c Release --no-build --no-restore`：209 项通过，无失败/跳过，包括实际持久化操作恢复用例。本次未重跑全体领域测试。
- [解析与目录测试](../../tests/ResourceCycle.Cli.Tests/ConsoleMetaCommandsTests.cs) 验证冒号唯一形式、全部命令/别名、非法参数、实际选项可发现性；[行动解析测试](../../tests/ResourceCycle.Cli.Tests/CycleCommandParserTests.cs)保留语法与领域合法性分离。
- [Controller 集成测试](../../tests/ResourceCycle.Cli.Tests/PlayControllerOperationTests.cs) 验证 None 下全部命令及带参数切换被同 ID 拒绝、偏好与基线不变、最终返回 `handoff :exit`；另覆盖读取后取消、精确重同步答复、查询/拒绝/退出不发布 E/S。
- [共享视图边界测试](../../tests/ResourceCycle.Cli.Tests/SharedGameViewBoundaryTests.cs) 验证两布局的完整/增量/行动帮助与终局无 Console 指引，含 `:exit` 的正文保留，目录提示同源及完整日志回调。既有 session 测试继续覆盖清屏、查询、Flush 失败与基线恢复；没有新增具体排版位置或整屏快照合同。
- 独立审查者对照方案、源码与测试，未发现实质问题。主线程复核解析分派、日志时序、终局与写出边界，并独立完成上述构建和测试。

## 真实 CLI 试玩

独立试玩使用已构建 Release 程序，通过重定向输入输出启动真实进程。6 个主要样本退出码均为 0，stderr 为空；原始材料保留在本机 `artifacts/console-command-boundary-playtest/`（不纳入 Git）。主线程抽查帮助、错误答复、交接正文及两种终局全文。

| 样本及证据文件 | 可观察结果 |
| --- | --- |
| `survival-matrix-input.txt` / `survival-matrix-output.txt` | 帮助列出 decision/classic 和 full/delta；两布局与模式切换、look/details、无冒号旧命令与非法参数拒绝正常。`handoff :exit` 原样跨回合继承，仍能补给和移动，最后显式退出。 |
| `quit-alias-output.txt` | `:quit` 正常结束当前 Console。 |
| `play-full-only-output.txt` | 帮助只广告 full，delta 拒绝后仍可查询/切换布局及退出。 |
| `natural-death-output.txt` | decision/delta 下自然脱水死亡；事件后一次完整冻结记录，无元命令广告或输入提示符。 |
| `terminal-cold-open-output.txt` | 同一持久存档冷开只输出一次冻结全文，随后返回。 |
| `full-classic-death-output.txt` | classic/full 下实际补给、等待和确认警告，推进中不宣称可输入；自然终局一次冻结全文。 |

本次未发现阻断缺陷或明显 UX 退步；未跑 PTY 或获救路线，不重新声明跨平台清屏或完整玩法验收。None 没有产品启动入口，以相同 Controller 的集成测试验证。增量 UI B 的历史容量、来源绑定和裁剪仍按原阶段目标待设计。
