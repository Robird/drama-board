# Console 可切换布局与试玩

日期：2026-10-28。范围为 CLI 呈现、会话布局选择和只读元命令；没有改变游戏规则、存档 Schema 或修订号。接口与信息组织见[布局合同](../design/console-layouts.md)。

## 实现与验证边界

- [IPlayLayout](../../src/ResourceCycle.Cli/IPlayLayout.cs) 只接收角色观察；[PlayPresentation](../../src/ResourceCycle.Cli/PlayPresentation.cs) 保存会话偏好。`decision` 为生存默认，`classic` 为多人采集默认；切换立即重绘，终局沿用当前选择。
- [PlayInputTests](../../tests/ResourceCycle.Cli.Tests/PlayInputTests.cs) 与 [PlayPresentationTests](../../tests/ResourceCycle.Cli.Tests/PlayPresentationTests.cs) 验证仅带冒号的元命令、拒绝无前缀旧入口及带冒号的游戏行动、未知布局、同份观察重绘、跨角色会话偏好、只读查询不提交行动。没有新增具体布局的整屏快照、精确措辞或位置断言；经典布局既有测试保留。
- `dotnet test tests/ResourceCycle.Cli.Tests -c Release -warnaserror -p:DurableGraphSchemaHistoryMode=Verify --nologo`：110 项通过；相关项目与 Schema Verify 通过。清屏测试只验证时间/角色变化的触发边界及先清后渲染，不约束具体布局。
- [生存工程 smoke](../../scripts/Smoke-ResourceCycleSurvival.ps1) 改为读取新默认布局的语义信息，去掉原来的资源章节切片、标题位置与表格列依赖，并加入初始和途中警告时的布局切换、详情与帮助查询。真实存档 SHA-256 比较证明这些操作不写游戏历史；两条成功路线、失败、消费和连续/冷续局一致性仍覆盖。打磨后完整运行通过，最终证据目录 `artifacts/resource-cycle-survival-smoke/d74cfa984a7149b282b10de4df765efd/`。
- 多人采集工程 smoke 通过，证据目录 `artifacts/resource-cycle-play-smoke/cc41fdadce25438c90cf7f8dd3ceb98d/`；主线程另以实际 CLI 切换至 classic 后运行到脱水失败，确认终局沿用选择，证据在 `artifacts/console-layout-playtest/main-2910cebf04b74cb4a96d87141a804da9/`。

## 独立试玩

两位 agent 只读项目入口和玩家输出，未读源码、地图、旧攻略或彼此结果；逐屏决定行动，分别沿溪流和高处探索。不是外部人类用户研究。

| 路线 | 结果 | 原始证据 |
| --- | --- | --- |
| 溪流 | D0 22:15 获救；温暖 7、食物 28.75、饮水 32 | `artifacts/console-layout-playtest/stream/feedback.md`、`transcript.json`、`inputs.txt` |
| 高处 | D1 01:00 获救；温暖 32、食物 50、饮水 10 | `artifacts/console-layout-playtest/high/feedback.md`、`captured-output.txt`、`captured-input.txt` |

溪流记录从行动前的 `:look` 重绘开始；高处首轮 PTY 转录不完整，完整标准流来自按本人已知路线的取证复玩，不计作第二次盲玩。高处捕获器结束后的已关闭管道错误不来自游戏，终局输出已完整保存。两位均在同进程警告状态下切换布局，观察到时间、行程期限与历史警告保持；磁盘字节结论来自工程 smoke。

## 反馈与打磨

共同结论：暴露状态、温暖趋势已能在输入附近看见；补给与使用效果、资源与采集操作的局部关联有用，帮助及完整历史入口可发现。主要问题及本轮处理：

| 反馈 | 处理 |
| --- | --- |
| 在途时 continue 离输入约 29 行，末屏反而只看见受阻等待 | 输入摘要列出当前合法 continue / cancel / use 响应；途中移除等待模板，受阻详情仍可在帮助查看 |
| 帮助后看得到抵达时刻，却看不到现在几点 | 输入摘要增加当前时间、昼夜和剩余行程分钟 |
| 林屋多条路重复相同长风险 | 按完全相同的权威风险分组，每组输出一次并标明对应通路 ID，不解析或删改风险语义 |
| 新耗时行动接受后警告消失，缺少就近说明 | 待确认警告附近补充新耗时行动也确认已展示警告；use 仍不确认 |
| 庇护状态下仍泛称寒风可能提前危险 | 明确保持庇护免寒风损失，离开后需重评 |
| 预计警告/耗尽没有指明身体指标 | 留待观察提供原因；不在 UI 猜测或复制预测逻辑 |

两位试玩者在重新构建后按各自已知路线定向复查，报告为 `artifacts/console-layout-playtest/{stream,high}/recheck.md`，不计作新盲玩。溪流复查在 17:30 的途中警告停点确认：帮助后仍能就近看见 19:15 抵达、剩余 105 分钟及合法响应；用完物品后对应 use 消失，历史警告和期限保持。高处复查确认林屋三条通路的共同风险只出现一次，且与帮助中各自的风险文本一致，耗时、方向、合法性均保留。主线程读取实际输出复核，并重跑生存工程 smoke 验收。

具体布局仍可快速调整。不同风险文本中的共通长句、时间格式，以及终局“获救”叙事与结束瞬间暴露标签的表达仍有打磨空间；当前不把这些意见扩成领域规则或通用 UI 框架。

## 新观察清屏复查

主线程在 Windows PTY 实际运行：初始 15:00 的 `use item.water` 保留同轮屏幕，`wait 1` 到 16:00 后先清屏再输出决策布局；随后 `:help` 与 `:layout classic` 不清屏，`wait 24` 到 21:00 饥饿终局时先清屏再显示经典终局。原始输出块见 `artifacts/console-layout-playtest/clear-b405e69b9e404d11b73c84137ed55bc6/transcript.json`；ConPTY 将清屏表现为光标归位和逐行清除，不保证固定转义序列。另以管道输入运行 `:help / wait 1 / :exit`，确认保留 15:00 与 16:00 两轮文本且没有 ESC 控制字符。此复查不替代上面的独立路线试玩，也不声称保证所有终端的回滚历史行为。
