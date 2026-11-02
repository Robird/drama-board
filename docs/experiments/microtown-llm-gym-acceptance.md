# 小镇 LLM / Gym 接线验收

> 历史记录：Microtown 源码、测试和 smoke 已于 2026-11-02 从主线移除，见 [Git tag 归档](../archive/microtown.md)。本文命令、实现状态和测试结果属于当时版本。

日期：2026-10-31。范围是第二个游戏接入共享控制器、连接配置与 Gym，以及一次有限真实模型反馈分叉探针。**不是小镇完整里程碑验收**：社交、忙碌通知、owner 预测维护与完整经济日模型试玩仍待实施或验证。

## 实现与边界

- [TownLlmCharacterController](https://github.com/Robird/drama-board/blob/b6fea5e94ca3ea718cee2546a2d785afa1bafa6d/src/Microtown.Completion/TownLlmCharacterController.cs) 将冻结角色观察和小镇行为指引交给共享 `LlmDecisionLoop`。同一实例串行服务三居民，共享调用预算，不保留跨角色/跨决策语义 Tail；返回未执行的原始命令和操作身份。
- [TownConsole](https://github.com/Robird/drama-board/blob/b6fea5e94ca3ea718cee2546a2d785afa1bafa6d/src/Microtown.Cli/TownConsole.cs) 默认 Human，`--llm` 可绑定三居民，`--human` 保留一位人类调试角色。先恢复 pending 和核对结果，再按需建立连接；暂停不会代选 wait。最近回执与本人活动结果仍是最小角色记忆，宿主轨迹不作为其补充记忆。
- [CompletionConnectionBinding](../../src/CharacterControl.Completion/CompletionConnectionBinding.cs) 被 Town 与原山林宿主实际使用。发现旧 catalog 加载会提前解析所有连接的环境变量后，在相邻 `atelia-completion` 提交 `ef78922` 增加选中连接读取 API：整体结构仍严格校验，仅解析被选连接的环境变量。原完整加载 API 不变；未打包或推送。
- [TownGymSession](https://github.com/Robird/drama-board/blob/b6fea5e94ca3ea718cee2546a2d785afa1bafa6d/src/Microtown.Cli/Gym/TownGymSession.cs) 复用共享日志/消息投影/导出；请求记录精确完成 S，反馈落盘再给回执。冷开从已完成 E/S 补精确结果，无提交旧输入标 Interrupted，不重放。错误游戏身份先只读拒绝，不能在拒绝前修剪日志尾行。
- [TownGymCommands](https://github.com/Robird/drama-board/blob/b6fea5e94ca3ea718cee2546a2d785afa1bafa6d/src/Microtown.Cli/Gym/TownGymCommands.cs) 提供反馈列表、单角色导出、请求/反馈 fork。列表/导出不执行 pending、不修复日志；fork 只注册分支。证据截止为源请求序号减一，分叉不恢复原生工具 Tail。
- [TownGymBoundary](https://github.com/Robird/drama-board/blob/b6fea5e94ca3ea718cee2546a2d785afa1bafa6d/src/Microtown/Persistence/TownGymBoundary.cs) 提供只读历史边界检查和当前分支已完成操作查询。排除 pending、父分叉点之后的未来和初始快照复制的旧回执；游戏 E/S 始终是效果权威。

## 工程验证

主线程运行：

```powershell
dotnet build DramaBoard.slnx -c Release -m:1 -p:DurableGraphSchemaHistoryMode=Verify -v:q
dotnet test DramaBoard.slnx -c Release -m:1 -p:DurableGraphSchemaHistoryMode=Verify -v:q
pwsh -NoProfile -File scripts/Smoke-Microtown.ps1 -OutputDirectory artifacts/microtown-gym-integration/human-console
```

构建零警告、零错误；13 个测试项目共 **1200** 项通过，无跳过。输出保存在 `artifacts/microtown-gym-integration/tests.txt`。Console 多进程烟测通过；它是脚本驱动 Human 入口，不是真人试玩。

| 验证面 | 证据 |
| --- | --- |
| 共享循环与连接 | CharacterControl.Completion.Tests 11 项；包含未选连接缺少环境变量、选中身份/模型覆盖、取消、所有权 |
| Town 模型适配 | Microtown.Completion.Tests 6 项；三角色私有目标/Tail、串行渲染、预算/取消/反馈失败、实际 Kernel 回执 |
| Town 宿主和 Gym | Microtown.Cli.Tests 15 项；真实磁盘+fixture Completion、反馈先落盘、精确 fork、pending 补回执、无 E 不重放、错误日志不变、Human EOF/终局不初始化模型 |
| Town 领域 | Microtown.Tests 53 项；含只读精确边界、拒绝后新 S、完成 E/S 查询与父子分支隔离 |
| 旧游戏和共享底层 | 同次 solution 测试覆盖 ResourceCycle 的领域、CLI、Completion，及 Goals/Gym/Kernel/Spatial/Host/Inventory |

上游负责者另运行 Completion 选中配置/注册表相关离线测试 114 项通过；主线程核对上游提交及下游实际编译/测试。此数字不计入本仓库 1200 项。

## 真实模型短探针

原始材料位于 `artifacts/microtown-llm-probe/`：`run-probe.ps1`、明确的 `experiment-note.txt`、`evidence/plan.json`、程序集哈希、各进程命令/退出码/Console 输出、完整 Gym JSONL 和逐角色 Markdown 导出。使用 `gpt-6.1-sol`，官方 auth-file provider；未读取或复制凭据内容。

实验事先限定父局 8 次、子局 4 次，实际共 **12 次**请求，没有额外模型重试。实验指令只要求 a 在首决定观察真实界面后提交一条有依据的反馈，再正常行动；不规定经济操作。父子局都因调用预算返回 2，均停在游戏时间 0。所有动作均为即时操作，因此这次探针**不证明时间推进、生产循环或市场刷新**。

父局三个居民均有成功操作，共 7 次，包括创建私有目标、吃饭和采购谷物。a 的真实反馈为“希望耗时命令显示结束时的预计饱食值”：现有材料给速率和时长，模型需自行相加。该反馈与实际界面一致，是后续材料改进线索；本轮不据此宣称需求已经解决，也不宣称模型自发使用反馈工具。

| 精确定位 | 值 |
| --- | --- |
| 父分支 | `42422d77` |
| 反馈 ID | `d1760a2e18d04a11850ab8648cdb5d6f` |
| 源请求 | `5eecacfb6c7f49e3b44b0b15301c0b72`，记录序号 8 |
| 源完成 S | `42422d770e0000d000000000` |
| 父局后续完成 S | `42422d770e00007c07000000` |
| 子分支 | `43422d77`，通过产品 `--gym-fork` 创建 |
| 继承证据截止 | 7，即源请求之前 |

子局另起进程从原 S 继续，三个居民均返回成功操作，共 3 次。父 head/完成 S 未改变，子局没有继承父局后来创建的目标或买到的谷物。主线程读取原始反馈、操作回执、父子统计和定位检查，确认“响应”确实有已提交成功结果；不是只根据进程成功或模型文本判断。

独立试玩者进一步重新导出父三角色，SHA256 逐项一致；按导出记录序号核对，子局没有截止点之后的父记录，也没有其他居民的私有记录。源请求和子局首请求的 SystemPrompt、OutputContract、Shared、Tail、ModelId 分别相同；这证明 Gym 序列化投影一致，不是 HTTP 层完整请求字节相同。子局再次提交的反馈属于新调用，不是恢复父局反馈 Tail。

## 留待后续

本切片把共享控制器/Gym 变成两个真实游戏消费者。接下来先增加有界社交材料及忙碌响应，再实现赠与/报价交易；同时处理预测维护，最终开展至少一个完整经济日及外界补货的真实模型试玩。完整里程碑条件保留在[目标](../codex-goals/microtown/README.md)，唯一动态施工表见[工作计划](../codex-goals/microtown/work-plan.md)。
