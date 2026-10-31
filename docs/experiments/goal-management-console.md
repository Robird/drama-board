# 目标管理 Console 工程试玩

日期：2026-10-31。操作者为 Coding Agent，以脚本驱动真实 Human Console 入口；不是实际人类体验验收，也没有调用 LLM。本轮不构建、不改生产数据，使用主线程完成 Release 构建后的程序集。合同依据：[操作](../design/goal-management/operations.md)、[容量与 Context](../design/goal-management/capacity-and-context.md)。

## 复跑与证据

预先完成仓库构建，然后使用一个全新目录：

```powershell
pwsh -NoProfile -File scripts/Smoke-ResourceCycleGoals.ps1 -OutputDirectory artifacts/goal-console/my-fresh-run
```

[脚本](../../scripts/Smoke-ResourceCycleGoals.ps1)逐次启动真实 `dotnet src/ResourceCycle.Cli/bin/Release/net10.0/DramaBoard.ResourceCycle.Cli.dll --survival --store ...`，通过 UTF-8 重定向输入提交普通命令。它不写世界对象、不覆写存档、不生成预置目标、不自动构建。每段保留 `.in.txt`、`.out.txt`、`.err.txt`；各存档的 `operations.jsonl` 保存实际收到的命令与持久回执，成功后写独立断言 `acceptance.json`。

初次证据位于 `artifacts/goal-console/20261031-engineering-01/`；焦点回执修复后的全流程复验位于 `artifacts/goal-console/20261031-polish-02/`。这些本地原始工件不纳入 Git；脚本与本报告提供复跑入口。

初次运行的 8 个进程全部退出 0，stderr 均为空。主流程 18 条、容量流程 136 条游戏提交，共 154 条；其中 5 条精确预期拒绝，其余 149 条成功。`acceptance.json` 记录 333 个断言，包括按原始提交核对每次结果的 ToolCallId、WorldVersion、DomainEvent；不以历史屏幕里出现过“拒绝”字样推断本次结果。

修复后使用全新目录完整复跑上述 8 个过程，仍是 154 条提交、149 成功与 5 条精确预期拒绝，全部进程退出 0 且 stderr 为空；加入焦点回执检查后共 334 个断言通过。

## 已观察到的行为

1. **共享目标与来源**：先创建 G1“为两人准备饮水💧”、其子 G2“确认溪流湾”，再创建 G3“寻找同伴”并连接 G3→G2。详情同时显示 L1/L2 及两份来源理由；焦点显式选取 G3→G2。中文与非 BMP 表情完整保留。
2. **更正与查询**：show G2 后，无 reason 的完成标准修改被拒绝；真正 no-op 不使查询失效。带 reason 的修改成功，并明确出现“详情已过期”。重新 show 显示旧标准与更正理由，未把修改冒充既往事实。
3. **生命周期与冷开**：pause/resume、finish/reopen 均走真实命令；保留复查线索与重开理由。进程退出后从同一存档重开仍有共享关联、Focus、修订后的标准与旧终态记录。`wait 1` 进入后续 Context 后，未结束目录仍然包含原目标与复查线索。
4. **呈现模式**：同一存档内切换 full/delta，均可发现目标区、共享来源和查询失效；旧终端文本仍然留在滚动历史，不能将其当成当前目标状态。

## 容量与显式清理

容量流程全部使用普通命令，没有注入内存夹具：

| 边界 | 真实拒绝 | 清理出口与结果 |
| --- | --- | --- |
| 12 个未结束 | 创建第 13 个被拒绝 | finish G1 后创建得到 G13，失败未消耗身份 |
| 32 个保留记录 | 保留记录满时 create 被拒绝 | forget 孤立的已结束 G1 后创建得到 G33，不复用 G1 |
| 48 条关联 | 添加第 49 条被拒绝 | unlink L1 后重新关联得到 L49，失败未消耗身份 |
| 已结束但有关联 | forget G2 被拒绝并列出阻碍的 Link ID | 显式解除 G2 的 10 条边后 forget 成功，最终 31 个保留目标、38 条关联 |

这些清理步骤在满额状态仍可执行，不需要世界时间推进。断言核对全部四条容量拒绝与那一条缺少更正理由的拒绝，没有额外意外拒绝。全部操作与长文本回放留在各过程输出及宿主日志；终端输出量不等于模型完整输入量。

## 发现的呈现问题

初版回执把“路径内的箭头”和“旧状态变为新状态的箭头”混用。暂停 G2 的输出为 `焦点：G3 → G2 → G3`，结束 G2 为 `焦点：G1 → G2 → G1`；见 `03-lifecycle.out.txt`。领域实际回退符合预期，但这串文本看起来像循环路径。

主线程已修复为明确的原/现路径。复验 `03-lifecycle.out.txt` 实际输出 `焦点：原 [G3 → G2]；现 [G3]`，脚本新增精确断言确认该暂停回执；本代理未修改生产代码。

## 验证边界

以上是工程输入、可见材料、宿主操作回执和真实 S 冷开的证据。它不覆盖 E-only 注入恢复、多人隐私、完整终局、最大正文材料或模型自主维护目标；这些不能由本脚本的成功替代。人物在容量实验中留在初始位置，不以采集/战斗等额外玩法制造目标状态。
