# 角色跟随：真实 Console 进程验收

> 历史命名说明：本文保留当时的 ResourceCycle / Cycle 类型、命令与验证记录。当前应用已重命名为 Playground；现行入口与持久身份边界见[重命名说明](../../docs/implementation/playground-rename.md)。旧名称源码链接固定到重命名前的 Git 版本。

2026-11-01。独立验收代理操作真实 Human Console 入口；不是用户本人试玩，不涉及真实 LLM 调用。本记录验证[角色跟随设计](../design/character-following.md)的三人产品闭环，模型试玩和领域恢复测试由各自证据覆盖。

## 运行来源与证据

运行基线为 `b0ac5919aee9219a580156b4ec1ebe12373b3394` 加当时未提交的实现工作树，随后实现收录于 `ebdecebc4dfa8a6c1aa9ad0eff080e6a5d95d32d`。使用主线程已构建的 Release DLL；收尾构建的下列两个 DLL 哈希与试玩时一致。验收不调用 build/run/test，不修改实现，不重置存档。二进制 SHA-256：

| 文件 | SHA-256 |
| --- | --- |
| `DramaBoard.ResourceCycle.Cli.dll` | `7d93ce78dc6ff14e6e03cd907c37eb348c34d38ea9111152ce8375cafae33e68` |
| `DramaBoard.ResourceCycle.dll` | `d42cb7d49ec40345bd77d83d5c2576c4b3ffb49a0b521c483e28214502de6119` |

原始本地证据在 `artifacts/character-following-console-20261101/`（不提交存档/整段角色材料）：每阶段保留 UTF-8 `*.input.txt`、`*.out.txt`、`*.err.txt` 和新操作配对 `*.operations.json`；`three-person/operations.jsonl` 是完整宿主操作日志。`acceptance.json` 汇总断言和二进制哈希；`readonly-before.json` 保存最终只读恢复前的全部游戏文件哈希。

实际启动命令：

```powershell
dotnet src/ResourceCycle.Cli/bin/Release/net10.0/DramaBoard.ResourceCycle.Cli.dll `
  --survival --characters 3 --store artifacts/character-following-console-20261101/three-person
```

第一次创建后，每阶段均退出进程再打开同一存档，并省略 `--characters`。阶段输入由本地 `run-stage.ps1` 写入真实进程标准输入； stdout/stderr 异步捕获，30 秒上限。表中角色归属取自新增操作收据，不从历史 Context 回显推断。

## 实际轨迹

| 阶段 | 输入及实际结果 |
| --- | --- |
| 01 集合，15:00 | a、b 各 `follow c` 成功，c 获得输入；两布局显示直属队员 a/b。 |
| 02 第一段 | c `go trail-1`，三人从营地抵达溪流湾；16:00 食物警告唤醒 a。 |
| 03 权限与第二段 | a 的 `go trail-1`、`gather water 1`、`wait 1` 均持久拒绝；a/b 分别 `continue`；c 普通 `go trail-1`，17:00 三人返回营地。 |
| 04 第三段 | c 再次 `go trail-1`，17:30 饮水警告在途中唤醒 a；原定抵达仍为18:00。 |
| 05 途中响应 | a 的 `unfollow`、`dismiss-followers` 均拒绝；a/b/c 各使用1份水，各 `continue`，18:00 三人抵达溪流湾，关系保留。 |
| 06 分头工作 | c `dismiss-followers` 成功，a/b/c 各开始 `gather berries 1`，都有自己的30分钟期限；加入采集通知唤醒 a。 |
| 07 完成活动 | a 未完成采集时 `follow c` 被拒绝；a/b 分别确认采集加入通知，时间推进至18:30，三人各取得1份食物。 |
| 08 重新集合 | c/a/b 分别成功 `use item.food`，证实各自背包已收到采集成果；c `say a 准备出发，请跟随我。`；a `follow c` 同时确认待响应交流，b `follow c`，两人休眠，c 获得输入。 |
| 09 一起离开 | c `go trail-1`；19:30 三人抵达营地，c 的当前观察显示 a/b 仍在跟随。该段没有队员输入。 |
| 10 人数冲突 | 显式 `--characters 2` 打开三人存档退出码1，错误明确要求沿用实际3人。 |
| 11 只读恢复 | 省略人数成功恢复“3 人山林求生”，仍为19:30营地、c领队/a/b队员；执行 `:mode full`、`:layout decision once`、`:layout classic once`、`:look`、`:exit`，所有游戏文件哈希保持不变。 |

阶段03的第一次权限拒绝使用 classic 布局；其余使用默认 decision/delta，阶段11另检查 full。布局元命令的新配对收据均成功，展示包含关系身份、限制及解散/脱队入口；本次没有固定行号/排版快照断言。

## 独立核验与边界

- 共32条新游戏操作：26成功、6次有意拒绝。每条均有且只有一条配对结果，ToolCallId一致，且保存 WorldVersion、DomainEvent；不以旧 Context 中的相似文字判成功。
- 四条成功 `go` 全部属于领队c，队员没有虚构的 `go` 操作；四次成功 `follow c` 分别来自 a/b 的初次加入与重新加入。只有现有身体警告/采集通知唤醒队员，普通同行不要求逐段输入。
- 三条 `gather berries 1` 分别属于a/b/c；后续三条成功 `use item.food` 分别消费各自成果，没有借用共享背包。
- 冷开覆盖原地跟随、途中待确认警告、解散后采集、重新跟随后及最终到达；已有行程与关系继续有效。最终只读恢复核对完整游戏文件哈希，排除会合法追加的宿主 `operations.jsonl`。
- 最终状态由新的 `:look` 成功结果和当前同地角色展示确认；原始滚动输出包含冻结 Opening 与历史条目，不能把它们当作最终位置。
- 本次未覆盖二人完整路线、LLM领队、E-only中间恢复、逐实体到达间断点、死亡清理或完整获救。由其他独立测试/试玩补齐，不以本记录声称全部验收完成。

本次未发现阻断闭环的功能缺陷。当前可见操作明确列出跟随约束；真人只能在自己的决策窗口输入，和设计约定一致。
