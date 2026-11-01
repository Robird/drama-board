# DramaBoard

DramaBoard 是实验性的时空模拟引擎：**Kernel** 仲裁并提交确定性事件，**Spatial** 表达地点、通路、移动实体与相遇，**Host** 驱动 Kernel。核心不依赖 Player 或应用服务器。

当前推荐游戏入口是 **山野镇 / Hillside**，暂沿用 `src/ResourceCycle.Cli` 项目路径。居民可以在同一世界中往返小镇与山林，采集、补给、探索、交流和结伴移动，在工坊加工口粮或木器，在市场买卖物品；抵达原来的出口不再结束角色生活。居民之间也可报价交换资金和物品。愿景与完成条件见 [living-world](docs/codex-goals/living-world/README.md)，施工进度只在[专题工作计划](docs/codex-goals/living-world/work-plan.md)维护。

跨会话状态见 [PROJECT-STATE](PROJECT-STATE.md)，文档导航见 [docs/README](docs/README.md)，协作约定见 [AGENTS](AGENTS.md)。

## 构建与测试

需要 .NET SDK **10.0.201**，以及相邻的 `durable-graph`、`atelia-storage`、`atelia-completion` 源码目录。使用唯一 solution 入口，同一可写构建图的 dotnet 操作串行运行：

```powershell
dotnet build DramaBoard.slnx -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
dotnet test DramaBoard.slnx -c Release --no-build --no-restore -m:1
```

源码根、Generator/Schema 接线与验证边界见[源码依赖](docs/source-dependencies.md)。Solution 包含核心、Inventory/Goals/控制器/Gym、新世界采用的 ResourceCycle 项目、作为迁移参照的 Microtown 及相应测试，也列入相邻源码项目供 IDE/LSP 导航。常规开发不需要先发布包、Node、浏览器或旧 Player/Server。

## 启动山野镇

```powershell
# 默认三名居民 a、b、c，共用 Human Console 轮流行动：
dotnet run --project src/ResourceCycle.Cli -c Release -- --store artifacts/my-hillside

# 恢复同一存档；省略人数则沿用存档：
dotnet run --project src/ResourceCycle.Cli -c Release -- --store artifacts/my-hillside

# 单人新局，最多提交 200 个转换、模拟不超过绝对小时 32：
dotnet run --project src/ResourceCycle.Cli -c Release -- --characters 1 --store artifacts/my-solo-hillside --steps 200 --until-hour 32

dotnet run --project src/ResourceCycle.Cli -c Release -- --help
```

新局默认从 D0 08:00 的镇中住处开始。`--characters 1|2|3` 只选择人数；恢复时显式人数必须与存档一致。居民拥有各自的身体、背包、现金、Goals、Context 和探索知识，共用时间、资源与天气。当前默认初始身体为 80/80/80，每人有三份食物、三份水和 12 钱；这些是有限初始资产，回镇不会自动补满。

每个需要决策的角色由 Console 依次服务，等待输入期间世界暂停。Human Console 可以由真人或 Coding Agent 通过终端操作。无 `--store` 时只在内存中运行；指定存档后每次成功转换自动保存。使用新路径创建新世界，已有存档不会被静默重置。旧开发修订存档不兼容，当前修订见 [PROJECT-STATE](PROJECT-STATE.md)。

`:quit`、`:exit`、EOF 或 Ctrl+C 暂停当前宿主。`--steps` 默认 10000，是本次运行提交转换数的上限；`--until-hour` 是从 D0 00:00 起的**绝对模拟小时**，省略则没有该时限。预算结束不会宣告世界终局，继续运行可设置更大的时间上限。世界只跳到实际事件，若上界没有事件，时间保留在最后完成事件的位置。所有居民死亡后宿主停止；一名居民死亡不会结束其他人的生活。

## 让模型参与

```powershell
# 三名居民全由模型控制，所有角色共用本次调用预算：
dotnet run --project src/ResourceCycle.Cli -c Release -- --llm --model gpt-6.1-sol --store artifacts/my-llm-hillside --max-calls 128 --until-hour 32

# Human Console 控制 b，模型控制 a 与 c；a/b/c 均可被选择：
dotnet run --project src/ResourceCycle.Cli -c Release -- --llm --human b --model gpt-6.1-sol --store artifacts/my-mixed-hillside --max-calls 64

# 恢复同一存档时改变绑定：
dotnet run --project src/ResourceCycle.Cli -c Release -- --llm --human a --model gpt-6.1-sol --store artifacts/my-mixed-hillside --max-calls 64
```

`--llm` 默认控制所有角色；`--human ID` 必须与 `--llm` 同用，且 ID 必须属于当前存档。不带 `--llm` 时所有角色交给共享 Human Console。绑定在启动/恢复边界选择，保留原角色身份、背包、目标和个人材料；没有运行中热切换。模型控制器与 Human 使用同一领域行动及冻结角色材料。全模型观战输出属于宿主观察证据，不是任一角色的额外知识。

默认连接使用 Completion 的 Codex Responses 客户端及标准认证文件发现；`--auth-file PATH` 可指定文件。V3 连接目录需同时提供 `--catalog PATH --connection ID`，并使用目录内的认证配置。模型配置与凭据均在宿主边界读取，不保存在游戏状态中。

每次模型调用收到角色保留的 Context，并通过 `act` 提交操作。领域层负责校验与提交。`--max-calls` 默认 128，`--max-failures` 默认 3，`--max-context-chars` 默认 200000；调用预算、连续协议失败、取消或失败响应会暂停宿主，不替角色选择替代行动。每次运行有独立 JSONL 审计，`--llm-log PATH` 可指定位置；恢复时重新提供模型/连接与控制绑定参数。连接机制的历史说明见[LLM 接线文档](docs/implementation/survival-llm-controller.md)，启动参数以当前 `--help` 为准。

## 操作与个人材料

使用界面显示的当前地点通路、资源和角色编号行动：

| 意图 | 命令 |
| --- | --- |
| 重新查看、帮助、退出 | `:look`、`:help`、`:quit` |
| 移动 | `go <passage-id>` |
| 采集 | `gather <resource-id> <count\|all>` |
| 等待 | `wait <1–24 小时>` |
| 使用补给 | `use item.food`、`use item.water`、`use item.ration` |
| 市场买卖 | `buy <item-id> <count>`、`sell <item-id> <count>` |
| 工坊加工 | `produce <recipe-id> <batches>` |
| 确认提醒并保留活动 | `continue` |
| 取消采集/等待/生产 | `cancel` |
| 定向说话、赠与 | `say <actor-id> <text>`、`give <actor-id> <item-id> <count>` |
| 赠钱 | `give <actor-id> money <amount>` |
| 发起报价 | `offer <actor-id> <asset> <amount> for <asset> <amount> [hours]` |
| 接受、拒绝、撤回报价 | `accept <trade-id>`、`reject <trade-id>`、`withdraw <trade-id>` |
| 大喊 | `shout <text>` |
| 跟随/解散 | `follow <actor-id>`、`unfollow`、`dismiss-followers` |

元命令使用冒号前缀，领域动作不使用冒号。默认 decision 布局把可用行动与相关资源、路线和风险放在一起。`:layout` 列出布局，`:layout classic` / `:layout decision` 切换；`:layout classic once` 临时查看另一种排版。`:mode full|delta` 控制完整或增量输出。查看和切换布局不推进时间，不确认提醒，也不修改世界。见[布局合同](docs/design/console-layouts.md)。

采集每份单独结算，`all` 冻结接受时库存作为有限目标，不预留库存，也不跟随再生增长。资源耗尽或其他采集者加入会请求响应；`continue` 保留原活动期限，`cancel` 保留已取得物品而放弃未完成的一份。无效命令与查询不会消耗世界时间或确认提醒。

移动有真实模拟耗时，途中不能取消。身体随时间消耗，低值提醒可能在途中要求回应；使用补给后 `continue` 可继续原行程。温暖在庇护或白天野外恢复，夜间野外下降；天气每两小时可能带来最多 10 点额外降温，庇护可防止该损失。预测包含已知昼夜变化，不包含未来寒风。干木可以加工木器出售，当前还不能生火。角色探索知识来自亲历，旧观察标注时间，其他角色的陈述不会自动成为世界确认事实。

市场每日补充有限供应与收购额度，价格、剩余额度和下次补充时间在当地显示。买卖数量与生产批次为 1–1000。工坊每批用两份食物制作两份口粮（1 小时），或两份干木制作一件木器（2 小时）；口粮恢复 55 饱食。开工先托管原料，完成交付产物；取消或死亡退回未完成活动的原料。生产中处理通信、补给或赠与不改变原期限，接收物品还须为完成产物和取消退料分别留足空间。赠钱和物品赠与都使用通信额度。实际两次往返与账目见[经济验收](docs/experiments/living-world-economy-acceptance.md)；构建 CLI 后可运行 `pwsh -NoProfile -File scripts/Smoke-Hillside.ps1` 复验。

跟随需要同地且没有耗时活动；领队普通 `go` 带所有实际跟随者一起出发，到达仍保持关系。跟随者不能自行开始移动、采集、生产或等待，个人身体提醒仍需自己处理。到工作地点后可以解散，结束工作再集合；没有自动等待未加入同伴的机制。见[跟随设计](docs/design/character-following.md)。

报价里的资产可用 `money` 或物品 ID；期限为 1–24 小时，省略为 6 小时。例如 `offer b item.water 1 for money 2 4` 表示用一份水换两钱，四小时后到期。发起与接受须双方同地，拒绝或撤回可在远处自己的决策窗口进行；途中仍不能交易。报价不预留资产，接受时双方重新核对完整支付额与背包容量，一次成交；可尝试不保证成功。终态保留至实际交付后回收。交易不改变原生产/等待期限、跟随或既有警告。见[交易验收](docs/experiments/living-world-trades-acceptance.md)，构建后可运行 `pwsh -NoProfile -File scripts/Smoke-Hillside.ps1 -Scenario Trade` 复验。

说话、赠与限同地活跃角色；大喊送达本地与一跳声音连接地点的活跃角色，途中角色听不到。声音连接独立于移动通路，目前为 camp ↔ stream、ridge ↔ cabin。说话、赠物、赠钱、大喊与发起报价共用每角色每模拟时刻四次成功发送额度；接受、拒绝和撤回不消耗发送额度。无人听见的大喊也成功，不返回听众名单。通信唤醒不取消活动、不改变期限或跟随关系。独立闲置角色须安排耗时行动或加入跟随才能放行时间。见[大喊设计](docs/design/spatial-relations-and-shout.md)。

每名角色拥有独立的[目标记录](docs/design/goal-management/README.md)。例如 `create-goal {"title":"准备饮水","criteria":"出发前携带两份饮水","focus":true}`，`show-goal {}` 列出保留目标；界面列出其余编辑、关联、聚焦与结束命令。目标不会代执行活动，也不伪造完成事实。`handoff <text>` 为下一个 Context 留一次性便条，`handoff --clear` 清除；长期意图适合记录为目标。

## 持久化与观察

一个 DurableGraph 根保存业务子图与 Kernel 游标。派生预测在冷开时重建，在业务事实提交后增量维护。E 已提交、S 尚未完成的中断恢复只应用保存的事实及纯派生完成，不重新询问控制器或重新执行规划。机制见[运行时预测与恢复](docs/design/runtime-derived-predictions.md)。

每个非空 Console 提交拥有隐式 ToolCallId。存档旁 `operations.jsonl` 保存宿主收到的操作和结果，包括查询与拒绝；它不是游戏状态权威。冷开依据已提交事实补齐遗漏结果，不重做动作。模型原生会话恢复不属于这份保证。

Gym 入口沿用 `--gym` / `--feedback`（需 `--llm --store`），管理命令包括 `--branches`、`--gym-feedbacks`、`--gym-export` 和分叉命令；详见 `--help` 与 [Gym 使用说明](docs/design/character-gym/usage.md)。旧原型的 Gym 验收不自动证明融合世界的 C5，当前覆盖以专题工作计划中的新证据为准。

## 原型与历史证据

Microtown 当前是经济融合的源码与行为参照，不再作为另一条推荐产品路线推进。其生产、现金、有限外部市场与居民报价交易见 [Microtown](src/Microtown/README.md)、[里程碑验收](docs/experiments/microtown-acceptance.md)、[真实模型试玩](docs/experiments/microtown-playtest.md)及[持久化交互证据](docs/experiments/microtown-persistence-acceptance.md)。新世界已接入生产、现金、市场、赠钱与居民报价；旧证据不能替代融合验收。

ResourceCycle 早期批处理、单地点采集与逃生玩法的命令模式已退出当前 CLI。原 Smoke 脚本包含旧参数，目前不作为推荐新世界运行方式。历史实现与验证仍可查阅：

- [昼夜资源设计](docs/design/resource-cycle-demo.md)、[原始验收](docs/experiments/resource-cycle-acceptance.md)、[运行时缓存验收](docs/experiments/resource-cycle-runtime-cache-acceptance.md)。
- [多人采集设计](docs/design/resource-cycle-multiplayer-gathering.md)、[多人采集验收](docs/experiments/resource-cycle-multiplayer-acceptance.md)。
- [生存里程碑](docs/design/survival-demo-milestone.md)、[逃生试玩验收](docs/experiments/survival-demo-acceptance.md)、[真实模型旅程](docs/experiments/survival-llm-acceptance.md)。旧文档中的出口退休与启动指令属于当时版本。
- [目标管理验收](docs/experiments/goal-management-acceptance.md)、[通信验收](docs/experiments/survival-communication-acceptance.md)、[跟随验收](docs/experiments/character-following-acceptance.md)、[大喊验收](docs/experiments/spatial-shout-acceptance.md)。旧项目另行保留的真人合作体验项不被 Agent 操作 Console 冒充完成。

[Inventory](src/Inventory/ItemInventory.cs) 提供独立于时空核心的不可变物品数量和可选容量；游戏负责将跨模块效果一次提交，见[分层与组合](docs/design/layering-and-composition.md)。[SpacetimeHostScenarioTests](tests/Spatial.Tests/Acceptance/SpacetimeHostScenarioTests.cs) 独立验证两实体相遇、掉头、到达、确定性仲裁与重放；游戏磁盘 adapter 并非任意 Spatial 游戏的通用存档宿主。

旧 Player/Protocol、角色规划、Server/WebUI 与专用测试已冻结于 [Player 归档](docs/archive/player-runtime.md)，FirstBoard 见[独立归档](docs/archive/firstboard-llm.md)。如需重现旧应用，使用归档记录的基线和独立 worktree。
