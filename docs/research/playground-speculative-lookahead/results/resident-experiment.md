# 连续宿主对照：实验入口与预声明

2026-11-03。正式结果尚待执行及独立验收；本文件先固定比较口径，后续填入全部样本，不按结果挑选有利 profile。

## 已完成的预检

[PlaygroundLookahead](../../../../experiments/PlaygroundLookahead/README.md) 直接运行实际 PlaygroundHost。`p3-self-check-1` 和 `p3-self-check-2` 位于 `artifacts/playground-speculative-lookahead/`，均完成同进程预热及四类场景的各一组配对，每 run 4 个成功提交操作；两次自检各有 8 个正式 sample，四组完整实际输入、操作、事实及最终状态对照均通过。

5ms 服务时间的首轮自检中，预取开启后的工作墙钟均更慢，属于明确保留的短服务负结果，不作为 1 秒服务的性能估计。第二轮完善了全部物理服务、废弃服务及日志成本记录。小自检的高耦合四操作仍在一个 tick，不代表正式连续多时刻验收。

`p3-online-self-check-1` 仅使用离线 factory：两策略实际持久 Host 各两个物理请求，验证在线 runner 的预算装饰、排空和审计接线；另验证额度、唯一 outcome、批次不可重用、错误 grant 和不完整账本尾。不是在线模型结果。

## 正式矩阵

预声明命令（必须新目录）：

```powershell
dotnet experiments/PlaygroundLookahead/bin/Release/net10.0/DramaBoard.PlaygroundLookahead.dll --output artifacts/playground-speculative-lookahead/p3-resident-1000 --pairs 5 --operations 16 --delay-ms 1000
```

- 低/高耦合 × 独立/共享服务容量，共四类；各五组配对，开关顺序逐组轮换，共 40 个测量 run。每类另外进行开、关各一次四操作预热，共八个预热 run。
- 低耦合 camp/cabin，以完整可见材料选择合法 wait 1 或 continue。高耦合 camp/stream 有声音连接，每角色当前 Context 首次操作大喊，之后选择合法 wait/continue；正式 run 强制至少两个操作模型时刻，并确认低耦合反复创建/采用、高耦合确有废弃。
- 每 run 独立内存 history，共享不可变初态；通过只读 `StopRequested` 在实际 Host 已完成提交与记录的边界正常暂停，恰好完成 16 个成功角色操作。不以 MaxCalls 或不足长度前缀作公平对照。
- 固定响应由完整请求确定，不以物理调用全局序号选脚本。实际有效请求逐字比较，包含所有模型可见身份，不删除或替换字符串。仅在事实/状态的显式 `OperationRecordId` 字段按已核对的 actor/context/operation sequence 双射归一化，原值与每处替换路径保留。
- 独立服务允许请求分别推进且响应取消；共享单槽在排队时允许取消，但一旦开始服务就不响应取消，避免用廉价取消掩盖无用推理成本。因此 profile 间同时改变容量和服务取消策略，不能把差异全归因于容量；各 profile 内的 on/off 条件一致。

## 成本与证据范围

工作墙钟从第一次真实控制器服务进入到最后有效操作的 inner CommitState 返回；启动准备、Host 首服务前、最后提交后排空、整个 Host 时长分别保存。`ServiceCompleted` 测量真实可服务后到返回决定，包括渲染和工具续轮，不是纯网络 RTT。停止时全部在途任务仍需排空；窗口外计算仍计入物理请求及废弃成本。

每 run 保存完整 `actual-inputs.json`、`operations.json`、`events.json`、`final.json`、生产 raw、fixture timeline、服务计时、覆盖门槛及 `physical-service.json`。后者按物理 ordinal 和完整请求核对关联，区分需求、采用、废弃与停机时未采用，并保留实际已服务时间。

日志分别测生产 raw、HostJournal append 与 fixture timeline；纯 inner E/S 提交另测。此矩阵的 E/S 与 HostJournal 在内存中，不能据此宣称持久存档成本相同。推演、比较、日志、service 和 provider 的计时有嵌套/并发，不能相加充当总墙钟。工作结束时的最终诊断和排空留在尾段；各累计 scope 的精确范围随工件保存。

## 在线批次预声明

本专题独立账本为 `artifacts/playground-speculative-lookahead/online-budget.jsonl`，最初预留 16 次：off 新局 6、on 新局 6、on 续局 4，最大本地物理在途 2。Grant 与协议在 `p3-online-plan/`；每批调用前刷盘记账，结束后对账，失败与远端未知结果不返还额度、不自动重试。前期研究的八次调用不作为本阶段结果。

使用本机 `http://127.0.0.1:8491/`、`responses` 路径、`deepseek-v4.1-flash`，无 API key。真实模型输出可能改变实际工作量，因此这部分验证连接、完整材料、采用、提交和 usage，不假装与固定响应矩阵有相同轨迹，也不直接宣称公平加速。
