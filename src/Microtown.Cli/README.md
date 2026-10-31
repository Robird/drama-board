# 小镇 Console、LLM 与 Gym

一个串行宿主依次服务三个居民，世界在需要行动时暂停。默认由同一个 Human Console 控制所有居民；`--llm` 让三个居民共用一个无跨决策语义记忆的 LLM 控制器，`--llm --human a` 则由人类控制 a、模型控制 b/c。居民可以同地说话、赠送物品或钱，也可提出双边报价并由对方明确接受；等待/生产期间可被社交与报价通知唤醒。

```powershell
dotnet run --project src/Microtown.Cli -c Release -- --store artifacts/my-town --hours 48
# 冷开同一个存档；不要再传 --hours
dotnet run --project src/Microtown.Cli -c Release -- --store artifacts/my-town
```

无 `--store` 时仅保存在内存。新档 `--hours` 为 1..720，默认 48；存档已有实验终点，冷开不能覆盖。`--max-operations` 默认 200，统计本次进程所有居民的提交操作，包含拒绝；预算用尽暂停，下次启动可继续。`:look` 重显当前材料，`:quit` 或 EOF 暂停，不产生一次游戏操作。Ctrl+C 取消当前运行。多个分支时用 `--branch <ID>` 明确选择。

当前 GameRevision 为 3，Business/EventKind/Fact/Experience Schema 为 2，Character 仍为 Schema 2；旧开发存档没有迁移路径，不能继续游玩或按当前规则分叉。拒绝可能先发生于 DG 对象还原，而不是应用修订号检查。旧档的 `--branches` 与 Gym 反馈列表/文本导出不必还原角色对象，仍可查看历史证据。

每次交付的材料包括角色姓名/ID、时间、本人生活/库存/目标、最近回执及交互记录、附近居民、规则与当前动作。成功的即时命令轮转服务其他空闲或待响应居民，拒绝留在同一角色。耗时行动通常等待完成；等待/生产期间收到说话或赠与也会获得响应机会。输入示例：

```text
buy grain 4
go market-workshop
produce meal 2
eat
go market-workshop
sell meal 1
wait 4
say b 我准备去工坊做饭，你需要一份吗？
give b meal 1
give b money 3
offer b meal 1 money 5 4
accept trade:a:1
reject trade:a:1
withdraw trade:a:1
continue
cancel
```

这些是命令形状示例，并非多角色可直接逐行执行的完整脚本；看清当前服务的角色与地点。每条通路可双向使用，`go market-workshop` 在工坊表示返回市场。

说话/赠与要求对方存活、同地点且不是自己。话语最多 512 个 Unicode 标量，物品每次 1..1000 份，钱为正整数。赠与菜单显示本人能提供的上限，对方接纳条件仍在提交时检查；不会通过菜单泄露其库存、钱或容量。赠与一次结算双方余额，显示旧记录不会再次转移。

忙碌响应期间可说话、赠与、吃饭、编辑目标及在市场买卖，原活动仍保持。即时成功后轮转，待响应状态不自动清除；`continue` 释放响应并保留原活动到期时间，`cancel` 结束等待或生产，生产原料一次退回但已经消耗的饱食不退。移动不支持途中社交响应或取消。入库需为当前生产的未来完工、取消退料分别保留接纳空间；即使当前背包能放下，也可能因此拒绝赠物。

个人交互窗口完整保留最多 32 条待交付、最近 opening 的最多 32 条新项，以及更早 16 条。界面说明已裁剪多少旧记录；发送队列满时整次操作拒绝，不悄悄丢未读。新项标记只表示进入已发布决策材料，不证明阅读或回应；`:look`、重复渲染、冷开不会消费记录。居民话语按文本转义展示，属于角色陈述而非游戏规则；重要约定请记入 Goals。

报价语法为 `offer <resident> <give-asset> <give-count> <want-asset> <want-count> <hours>`：例如 `offer b meal 1 money 5 4` 表示本人愿给 b 一份饭，换取 5 钱，4 小时有效。两腿各一种物品或 `money`；物品 1..1000 份，钱为正整数，期限 1..24 整小时。创建需本人持有完整付款，但不会锁住资产。接收者用 `accept <offer-id>` 接受、`reject <offer-id>` 拒绝，提出者用 `withdraw <offer-id>` 撤回；上面示例 ID 要替换为界面实际显示值，三种命令是备选操作，不是连续脚本。

创建与接受要求双方存活且同地，接受必须严格早于到期时刻；拒绝/撤回不要求仍同地。接受时重查双方支付及完整接收状态，一次转移两腿，失败无部分交易；同资产交换也要求足额支付，不只看净额。UI 的 `may attempt` 只说明本人和公开条件允许尝试，不证明对方还有钱、货或容量。报价操作不耗时，busy 响应内原活动保持；`continue` 不接受/拒绝报价，取消活动不撤回报价。

“Your resident trade offers” 独立显示本人参与的有效报价与尚未回收终态，不依赖聊天历史。最多保留全镇 24 条、每提出者 8 条，含终态；满时仅阻止新报价，不阻止既有报价关闭/结算。终态需纳入双方决策材料后才可在后续 opening 回收，失败居民免除交付；不是阅读回执。普通聊天队列满也不会吞掉报价终态通知。已接受的记录再次显示不代表再次付款；长期约定仍请写入 Goals。

本人饱食预览显示不再进食时的耗尽时刻、路线/当前活动完成风险，以及多批生产的时长与饱食公式。它假设没有进食或其他干预，负预测意味着完成前已失败，不是实际负饱食；同刻饥饿失败优先于生产完成。超过实验终点也会提示。合法数量上限不是生存建议。

回执与最近本人经历属于游戏状态，冷开继续显示；pending E 先由 Kernel 恢复，不重复询问。末尾 `[Host statistics]` 是开发者全知统计，与角色材料分开；其时间区间是新游戏开始至当前状态（包括 fork 继承的前缀），不是本次进程运行区间。资金、物品与饱食分开列出，失败居民仍计入最差生活值。

## 模型运行与配置

```powershell
# 使用本机 Codex 登录；三个居民都由模型控制，开启审计与反馈
dotnet run --project src/Microtown.Cli -c Release -- --store artifacts/town-llm --hours 48 --llm --feedback
# 冷开，切换为 a 由 Human 控制，另两位使用选定 catalog 连接
dotnet run --project src/Microtown.Cli -c Release -- --store artifacts/town-llm --llm --human a --gym --catalog E:/private/completion.json --connection chosen
```

连接由 [CompletionConnectionBinding](../CharacterControl.Completion/README.md) 装配：无 catalog 时默认 Codex subscription、模型 `gpt-6.1-sol`，可用 `--auth-file PATH` 指定 auth 来源；`--catalog PATH` 必须配 `--connection ID`，不能同时传 `--auth-file`。`--model MODEL` 可覆盖模型。严格校验整个 catalog，但仅解析所选连接的环境配置、创建其客户端。连接在恢复 pending 和核对 Gym 结果后、第一次真正需要模型时才建立；只查看存档或处理已经发布的 E 不会调用模型。

`--human` 仅在 `--llm` 下接受 a、b 或 c，省略表示所有居民由模型控制。每次模型调用打印角色 ID、request ID 与实际观察材料，输出操作时标注 `[Model proposed: actor]`；只有随后 `[Result: actor] Accepted/Rejected` 才是游戏结论。材料只有本人私有状态和可感知信息；有界交互窗口、最近回执与本人经历并非完整 Context 历史。跨决策长期计划可写入 Goals，不能依靠上一轮模型对话仍在上下文中。

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

本轮社交、忙碌继续/取消、冷开及饱食预览的验证范围见[社交切片验收](../../docs/experiments/microtown-social-acceptance.md)。

报价规则与本版存档的证据见[报价交易验收](../../docs/experiments/microtown-trade-acceptance.md)。三个真实 LLM 居民已跑至预定 30h，覆盖市场刷新、双商品生产/外销及消费；另完成反馈定位分叉和有引导的居民成交，见[试玩报告](../../docs/experiments/microtown-playtest.md)与[总验收](../../docs/experiments/microtown-acceptance.md)。两段子局因预算暂停，不能按 exit 0 当作游戏结束。
