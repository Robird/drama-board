# 小镇 Console、LLM 与 Gym

一个串行宿主依次服务三个居民，世界在需要行动时暂停。默认由同一个 Human Console 控制所有居民；`--llm` 让三个居民共用一个无跨决策语义记忆的 LLM 控制器，`--llm --human a` 则由人类控制 a、模型控制 b/c。当前是工程实验入口，居民对话与赠与尚未实现。

```powershell
dotnet run --project src/Microtown.Cli -c Release -- --store artifacts/my-town --hours 48
# 冷开同一个存档；不要再传 --hours
dotnet run --project src/Microtown.Cli -c Release -- --store artifacts/my-town
```

无 `--store` 时仅保存在内存。新档 `--hours` 为 1..720，默认 48；存档已有实验终点，冷开不能覆盖。`--max-operations` 默认 200，统计本次进程所有居民的提交操作，包含拒绝；预算用尽暂停，下次启动可继续。`:look` 重显当前材料，`:quit` 或 EOF 暂停，不产生一次游戏操作。Ctrl+C 取消当前运行。多个分支时用 `--branch <ID>` 明确选择。

每次交付的材料包括角色姓名/ID、时间、本人生活/库存/目标、最近回执、附近居民、规则与当前合法动作。成功的即时命令轮转服务其他空闲居民，拒绝留在同一角色；耗时行动开始后需等完成才能再次决策。输入示例：

```text
buy grain 4
go market-workshop
produce meal 2
eat
go market-workshop
sell meal 1
wait 4
```

这些是命令形状示例，并非多角色可直接逐行执行的完整脚本；看清当前服务的角色与地点。每条通路可双向使用，`go market-workshop` 在工坊表示返回市场。

回执与最近本人经历属于游戏状态，冷开继续显示；pending E 先由 Kernel 恢复，不重复询问。末尾 `[Host statistics]` 是开发者全知统计，与角色材料分开；其时间区间是新游戏开始至当前状态（包括 fork 继承的前缀），不是本次进程运行区间。资金、物品与饱食分开列出，失败居民仍计入最差生活值。

## 模型运行与配置

```powershell
# 使用本机 Codex 登录；三个居民都由模型控制，开启审计与反馈
dotnet run --project src/Microtown.Cli -c Release -- --store artifacts/town-llm --hours 48 --llm --feedback
# 冷开，切换为 a 由 Human 控制，另两位使用选定 catalog 连接
dotnet run --project src/Microtown.Cli -c Release -- --store artifacts/town-llm --llm --human a --gym --catalog E:/private/completion.json --connection chosen
```

连接由 [CompletionConnectionBinding](../CharacterControl.Completion/README.md) 装配：无 catalog 时默认 Codex subscription、模型 `gpt-6.1-sol`，可用 `--auth-file PATH` 指定 auth 来源；`--catalog PATH` 必须配 `--connection ID`，不能同时传 `--auth-file`。`--model MODEL` 可覆盖模型。严格校验整个 catalog，但仅解析所选连接的环境配置、创建其客户端。连接在恢复 pending 和核对 Gym 结果后、第一次真正需要模型时才建立；只查看存档或处理已经发布的 E 不会调用模型。

`--human` 仅在 `--llm` 下接受 a、b 或 c，省略表示所有居民由模型控制。每次模型调用打印角色 ID、request ID 与实际观察材料，输出操作时标注 `[Model proposed: actor]`；只有随后 `[Result: actor] Accepted/Rejected` 才是游戏结论。材料只有本人私有状态和可感知信息；最近回执与本人经历并非完整 Context 历史。跨决策长期计划可写入 Goals，不能依靠上一轮模型对话仍在上下文中。

| 参数 | 默认 | 含义 |
| --- | --- | --- |
| `--max-operations N` | 200 | 本次进程所有居民的提交操作上限，含拒绝 |
| `--max-calls N` | 128 | 所有 LLM 居民共用的模型调用预算，含反馈后续调用 |
| `--max-failures N` | 3 | 连续协议失败上限 |
| `--max-context-chars N` | 200000 | UTF-16 材料与局部工具历史限制；超限暂停，不截断 |

预算属于宿主进程，冷开重新计数。达到模型预算、协议或材料限制会显式暂停，不代替居民选择 `wait` 等操作。模型未形成可发布 E 时，中断后可能再次调用；已发布 E 由正常恢复完成，不重问控制器。不恢复原生工具 Tail，也不自动重试 provider 错误。

`--experiment-note FILE` 仅用于 `--llm`，附加指令上限 8000 字符，每次观察末尾明确标为开发者实验介入。它不是世界事实或角色记忆；使用它诱导反馈或行为时，不应把结果描述为模型自发发现。开启 Gym 后实验指令也会进入实验记录。

## Gym 回看、反馈与分叉

`--gym` 为 Human 或 LLM 运行记录宿主证据；`--feedback` 要求 `--llm`，同时启用 Gym 与模型的 `report-feedback` 工具。两者都要求 `--store`。日志位于 `<store>/gym/events.jsonl`，包含实际模型请求材料、原生响应的可导出投影、反馈、操作及精确决策前态。反馈落盘后才返回工具回执；反馈描述是角色报告，不是已核实的世界真相。

```powershell
dotnet run --project src/Microtown.Cli -c Release -- --store artifacts/town-llm --branches
dotnet run --project src/Microtown.Cli -c Release -- --store artifacts/town-llm --gym-feedbacks --actor b
# EXPORT.md 必须不存在；多分支时补 --branch ID
dotnet run --project src/Microtown.Cli -c Release -- --store artifacts/town-llm --gym-export EXPORT.md --actor b
dotnet run --project src/Microtown.Cli -c Release -- --store artifacts/town-llm --gym-fork FEEDBACK_ID --fork-label alternative
# 也可直接用实际模型请求 ID 定位
dotnet run --project src/Microtown.Cli -c Release -- --store artifacts/town-llm --gym-fork-request REQUEST_ID --fork-label alternative-request
```

这些是进程级管理命令，不是居民的游戏动作，也不是 Console 内的冒号命令。反馈列表省略 `--branch` 时跨分支列出，`--actor` 可选；导出要求 `--actor`，多分支必须选定一个。列表/导出只读，不恢复 pending、不调用模型、不修复日志尾部；导出不会覆盖已有文件。

分叉通过唯一 feedback/request ID 定位并校验该次请求的**确切已完成 S 决策前态**，不是从当前最新状态复制。只注册新分支，不立即运行；随后用输出的 branch ID 冷开：

```powershell
dotnet run --project src/Microtown.Cli -c Release -- --store artifacts/town-llm --branch CHILD_ID --llm --feedback
```

新分支继承该前态的整个世界和此前证据前缀，不继承触发分叉的模型请求及其后续未来。反馈工具循环中的某个请求也只定位游戏 S 前态；分叉不恢复该请求的原生 Tail。当前串行运行分支，不支持多个进程同时操作同一存档。若提示分支已注册但证据写入失败，先查看 `--branches`，不要盲目重复 fork。

Gym 是开发者审计材料，不是自动交给居民的记忆。日志中的 `perception` 仅表示边界上可见的材料，不证明角色实际看过；`request` 才记录模型调用材料。游戏 E/S 决定效果是否发生，冷开核对结果不从日志重放输入。日志含角色私有材料与模型输出；它不记录连接凭据，发布实验记录前仍应检查自己提供的实验指令等内容。

## 退出状态与验证边界

| 退出码 | 含义 |
| --- | --- |
| 0 | 达到实验终点，或 Human `:quit`/EOF、操作/转换预算暂停；管理命令成功也返回 0 |
| 1 | 参数、存档、连接配置或未归类的宿主/client 失败；不自动重试 |
| 2 | `LlmControllerPausedException`：调用预算/协议/材料限制，已识别的传输或请求失败、未完成响应、反馈保存失败等显式暂停 |
| 130 | 运行中取消，例如 Ctrl+C |

暂停不等于通关；需结合 `[Host]` 原因与统计判断。存档保留已经完成的状态；无 `--store` 的运行无法冷开续局。

构建后可运行实际多进程 Console 探针：

```powershell
pwsh -NoProfile -File scripts/Smoke-Microtown.ps1
```

探针保留原始输入/输出及 acceptance.json，覆盖拒绝、预算暂停、冷开、三居民生产消费、市场更新、有限终点和结束后再开；它是脚本驱动 Human 入口，不是实际模型试玩。
