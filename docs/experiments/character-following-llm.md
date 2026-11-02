# 角色跟随：真实模型协作预演

> 历史命名说明：本文保留当时的 ResourceCycle / Cycle 类型、命令与验证记录。当前应用已重命名为 Playground；现行入口与持久身份边界见[重命名说明](../../docs/implementation/playground-rename.md)。旧名称源码链接固定到重命名前的 Git 版本。

2026-11-01。对应[角色跟随设计](../design/character-following.md)。两人场景已分别完成 Human 入口领队与真实 LLM 领队的“集合 → 至少两段同行 → 解散采集 → 再集合离开”闭环。**Human Console 的 a 由 coding agent 操作，不是用户本人；本报告不替代真人体验验收，也不宣称通关或模型自主探索能力。**

## 执行范围与来源

仅运行主线程已构建的 Release CLI，未在试玩中修改生产规则、伪造模型回复或替模型提交行动。每局复制完整构建输出到独立 artifact 目录，避免 Windows 进程锁影响主线程构建。认证由现有 Codex Responses provider 读取 `W:/CodexHome/auth.json`，未读取、复制或打印凭据。模型为 `gpt-6-luna` 与 `gpt-6.1-sol`。

成功样本启动时源码 HEAD 为 `ebdecebc4dfa8a6c1aa9ad0eff080e6a5d95d32d`。早期 probe、首个失败样本及准备存档发生在该提交前，其 `transport.json` 记录的 HEAD 为 `b0ac591...`，不能用这个旧 HEAD 代替当时的施工产物。实际所有样本的下列程序集哈希相同，且与本次完成实现的构建产物一致：

| 程序集 | SHA-256 |
| --- | --- |
| DramaBoard.ResourceCycle.Cli.dll | `7d93ce78dc6ff14e6e03cd907c37eb348c34d38ea9111152ce8375cafae33e68` |
| DramaBoard.ResourceCycle.dll | `d42cb7d49ec40345bd77d83d5c2576c4b3ffb49a0b521c483e28214502de6119` |
| DramaBoard.ResourceCycle.Completion.dll | `d5dd06366421c3cc1fb948326fff76990a90633cb13b5da54d41f6d40b54f3dd` |

原始工件根为 `artifacts/character-following-llm-20261101/`，不纳入 Git。每局保留 `transport.json`（实际参数、UTC 起止、HEAD、程序集哈希、退出码）、`audit.jsonl`、`human-input.txt`、`stdout.txt`、`stderr.txt`、`store/` 和独立提取的 `acceptance.json`；准备存档没有模型 audit。`run.py` 只是转发真实 Console 输入、复制产物和保存输出，没有游戏行动策略或模型回复替换。模型输入/输出原始审计只留本地，本文仅报告行动和领域结果。

实际 CLI 参数形状如下；模型预算分别为 1、40 或 30，不因角色数增加：

```text
dotnet <本局复制的CLI.dll> --survival --characters 2 --llm
  --model <gpt-6-luna或gpt-6.1-sol> --auth-file W:/CodexHome/auth.json
  --store <本局store> --max-calls <预算> --llm-log <本局audit.jsonl>
```

## 受控准备条件

正式两种领队样本均复制 `seed/store`，是独立实验分支，不是同一存档并发运行。准备过程全用普通共享 Human Console，未直接编辑存档、关闭身体消耗或修改地图：a 跟随 b，从营地沿 `trail-1` 到溪流；确认警告并解散；双方先各采一份浆果、食用并饮水，再各采两份浆果和两份水。18:00 时两人位于溪流湾，已无跟随关系与耗时活动，食物约 50、饮水约 66，背包各有两份食物和两份水。过程及实际回执见 `seed/human-input.txt` 和 `seed/store/operations.jsonl`。

准备阶段还经 b 的普通 Console 输入创建一条公开说明的私有目标，用于明确实验任务；没有写入任何角色的交接草稿或预先固定谁当领队：

```json
{"title":"与阿青完成一次同行协作","criteria":"集合后连续同行至少两段路，到资源点解除跟随各采一份，完成后再集合一起离开。不要自行离队。","focus":true}
```

随后各局由 a 通过真实 `say b ...` 提出领队身份、短路线和采水分工。模型仍需自己选择 `follow`、`go`、`dismiss-followers`、`gather` 等行动；目标和对话是人为引导，不声称模型自行发明合作计划。

## 实际样本

| 工件子目录 | 模型 / 调用数 | 结果 |
| --- | --- | --- |
| `probe` | luna / 1 | 认证与真实调用成功，b 实际 `follow a`，正常暂停 |
| `human-leader` | luna / 4 | 默认新局，b 跟随 a 到山脊并独立处理提醒；解散后 b 自行 `go trail-5`，未遵守采集约定，正常暂停保留失败样本 |
| `prepared-human-leader` | luna / 1 | a 的 `say` 已保存；模型调用报告 `transport-failure`，宿主暂停，退出码 2，未形成行动 |
| `prepared-human-leader-r2` | luna / 7 | 显式开启新宿主继续前一已保存状态，完成 Human 领队闭环；退出码 0 |
| `prepared-llm-leader` | luna / 9 | b 真实带 a 完成两段，但随后继续第三段，没有按计划解散；正常暂停保留失败样本 |
| `prepared-sol-leader` | sol / 8 | 从同一 seed 开始，完成 LLM 领队闭环，重新出发后途中正常暂停；退出码 0 |

共 30 次模型调用记录，包含 1 次 transport failure；没有库内自动重试。`prepared-human-leader-r2` 是记录在案的人工续局，复制的是前次已经保存的 `say` 状态，没有再次执行该命令。失败样本没有计作闭环通过；两个成功样本均没有游戏拒绝或模型协议错误。

### Human 入口 a 领队，luna 控制 b

`prepared-human-leader-r2` 的原始请求/响应各 7 条，领域提交 37 条。b 的实际动作依次为 `continue`、`follow a`、`use item.water`、`gather water 1`、`continue`、`follow a`、`continue`。

- 18:00：b 主动 follow a；a 沿 `trail-1` 从溪流湾出发。
- 19:00：a、b 都抵达营地；a 再次沿 `trail-1` 出发。
- 20:00：两人都回到溪流湾。**18:00 首次出发到 20:00 双方到达之间没有 b 的模型调用**，队员没有逐段提交移动或继续。
- a 通过 say 重申采水分工后 dismiss；b 与 a 各自 `gather water 1`。b 因另一采集者加入被唤醒，选择 continue 保留活动。20:15 双方分别得到 GatherYielded。
- a 通过 say 要求重新集合，b 实际 follow a；a 再次 go trail-1。21:00 两人分别收到温暖警告，b 选择 continue，a 也确认；21:15 两人都抵达营地，随后 a `:exit` 保存暂停。

三次 MoveStarted 的操作主体都仅为 a；每段分别记录 a、b 的 MoveArrived，未出现虚构的 b go 操作。

### LLM b 领队，sol 控制 b

`prepared-sol-leader` 的原始请求/响应各 8 条，领域提交 30 条。b 的实际动作依次为 say 确认领队、update-goal、go trail-1、go trail-1、dismiss-followers、gather water 1、say 请重新跟随、go trail-3。

- a 明确要求两段往返后立即解散；b 用 say 确认等待 a，a 实际 follow b。
- b 更新 G1 保存路线和分工；18:00 从溪流去营地，19:00 从营地返回溪流。20:00 双方都到达后，b 实际 dismiss-followers；a 在这两段途中没有提交操作。
- a、b 各采一份水，20:15 分别结算。b 主动 say 要求 a 完成后重新 follow，a 实际 follow b。
- b 20:15 选择 go trail-3，两人共同离开溪流。此处模型自主选择了其他通路，没有沿 a 建议的 trail-1；“再集合共同离开”功能成立，不能据此宣称它完全遵循路线建议。
- 21:00 途中温暖警告使 a 得到操作窗口，a 使用 `:exit` 正常暂停。**第三段尚未到达，不宣称逃离或安全完成后续路线。**

三次 MoveStarted 的操作主体都仅为 b；此前两段均有双方独立到达事件。a 的两次 follow 均由自己的 Console 操作产生，领队没有强制加入队员。

## 核对与证据边界

已独立解析原始 JSONL 并断言两个成功样本的真实调用数、模型角色始终为 b、三次领队出发、前两段各有双方到达、两次队员 FollowStarted、一次领队解散、20:15 双方各自收获，以及 `human-session-ended` 正常暂停。Human 领队样本还核对了两段同行期间零模型调用。回执主体、路线和当前材料互相印证；仅退出码 0 不算通过。

| 原始审计 | SHA-256 |
| --- | --- |
| `prepared-human-leader-r2/audit.jsonl` | `77a5a60ce2b4bcc23d394a12c3cdfbd17e61e76812e39372c838c014a6fd7cad` |
| `prepared-sol-leader/audit.jsonl` | `c17c5f1bc9a112685f1ac7751371493bd8f51a528719c8c75a3a2b0de276e118` |

本次证明真实模型和代理操作的 Human 入口均能担任领队，领域机制无需按控制器类型分支；同时实际展示了 luna 在分工记忆、路线计数与补给选择上的失败。成功样本经过补给和目标准备，并有明确 say 指导，不能推导无引导模型的普遍可靠性。三人、冷开/E-only 等工程合同由对应离线测试和 Console 验收另行证明，本次在线模型仅运行两人场景。
