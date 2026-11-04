# DramaBoard

DramaBoard 是实验性的时空模拟引擎：**Kernel** 仲裁并提交确定性事件，**Spatial** 表达地点、通路、移动实体与相遇，**Host** 驱动 Kernel。核心不依赖 Player 或应用服务器。

当前推荐游戏入口是 **山野镇 / Hillside**，使用 `src/Playground.Cli` 项目路径。居民可以在同一世界中往返小镇与山林，采集、补给、探索、交流和结伴移动，在工坊加工口粮或木器，在市场买卖物品；抵达原来的出口不再结束角色生活。居民之间也可报价交换资金和物品。愿景与完成条件见 [living-world](docs/codex-goals/living-world/README.md)，本阶段已通过[逐项验收](docs/codex-goals/living-world/acceptance.md)，包含同一三模型历史 72h 与两次独立自由游玩；专题状态只在[工作计划](docs/codex-goals/living-world/work-plan.md)维护。

跨会话状态见 [PROJECT-STATE](PROJECT-STATE.md)，文档导航见 [docs/README](docs/README.md)，协作约定见 [AGENTS](AGENTS.md)。

## 构建与测试

需要 .NET SDK **10.0.201**，以及相邻的 `durable-graph`、`atelia-storage`、`atelia-completion` 源码目录。使用唯一 solution 入口，同一可写构建图的 dotnet 操作串行运行：

```powershell
dotnet build DramaBoard.slnx -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
dotnet test DramaBoard.slnx -c Release --no-build --no-restore -m:1

pwsh -NoProfile -File scripts/Smoke-PlaygroundHome.ps1 -OutputDirectory artifacts/home-smoke-new
```

最后一条需要 PowerShell 7，输出目录必须尚不存在。CI 在 Windows 和 Linux 的 build/test 后复用此无模型进程 smoke，验证 Human 操作、冷开、只读管理与错误退出码，并上传 manifest 和标准流文本供排错。

源码根、Generator/Schema 接线与验证边界见[源码依赖](docs/source-dependencies.md)。Solution 包含核心、Inventory/Goals/控制器/Gym、Playground 系列及相应测试，也列入相邻源码项目供 IDE/LSP 导航。Microtown 已通过 [Git tag 归档](docs/archive/microtown.md)，不再参与构建或回归。常规开发不需要先发布包、Node、浏览器或旧 Player/Server。

## 启动山野镇

```powershell
# init 只创建 version 1 manifest；连接目录在全 Human 时也必须结构有效。
dotnet run --project src/Playground.Cli -c Release -- init --home artifacts/my-hillside
Copy-Item config/llm-connections.example.json artifacts/my-hillside/llm-connections.json
# 三角色全 Human 试玩；该覆盖文件显式选择才生效：
dotnet run --project src/Playground.Cli -c Release -- --home artifacts/my-hillside --character-controls config/playground-all-human.example.json
# 同一 home 再运行即续局；省略控制覆盖则默认全 LLM：
dotnet run --project src/Playground.Cli -c Release -- --home artifacts/my-hillside --max-calls 16

dotnet run --project src/Playground.Cli -c Release -- --help
```

home 示例见 [manifest](config/playground.example.json)。home 内的路径以 home 为基准，CLI 路径以 CWD 为基准；CWD 本身是 home 时可省略 `--home`，不搜索父目录。`--ephemeral` 选择内存世界，仍要求 home 与有效连接目录。init 不创建存档、不调用模型。

新局默认从 D0 08:00 的镇中住处开始。`--characters 1|2|3` 只选择人数；恢复时显式人数必须与存档一致。居民拥有各自的身体、背包、现金、Goals、Context 和探索知识，共用时间、资源与天气。当前默认初始身体为 80/80/80，每人有三份食物、三份水和 12 钱；这些是有限初始资产，回镇不会自动补满。

新局可选 `--content hillside|gentle`，默认 hillside。gentle 减缓消耗、缩短镇营通路及口粮制作时间，并调整资源、市场和天气。续局省略 `--content` 会读取保存定义；显式选择不符则拒绝，不改写旧局。普通 C# 内容入口、指纹与支持范围见[内容定义](docs/codex-goals/living-world/content.md)。下文具体数值描述默认 hillside。构建后可用 `pwsh -NoProfile -File scripts/Smoke-HillsideContent.ps1` 验证命名变体与冷开。

自定义内容的地点、通路、入口、资源、角色、配方和物品 ID 必须是无任何空白的非空词元，保留大小写与原字符；中文和 `|` 可用。物品 ID 的精确小写 `money` 保留给现金，`Money` 仍可表示普通物品。名称、描述和内容标识不套用命令词元规则；构造、`Change` 和恢复会校验这些约束。

每个需要决策的 Human 角色由 Console 依次服务，等待输入期间世界暂停。Human Console 可以由真人或 Coding Agent 通过终端操作。默认保存到 home 下的 `store/`；`--store PATH` 可覆盖，每次成功转换自动保存。使用新路径创建新世界，已有存档不会被静默重置。旧开发修订存档不兼容，当前修订见 [PROJECT-STATE](PROJECT-STATE.md)。

`:quit`、`:exit`、EOF 或 Ctrl+C 暂停当前宿主。`--steps` 默认 10000，是本次运行提交转换数的上限；`--until-hour` 是从 D0 00:00 起的**绝对模拟小时**，省略则没有该时限。预算结束不会宣告世界终局，继续运行可设置更大的时间上限。世界只跳到实际事件，若上界没有事件，时间保留在最后完成事件的位置。所有居民死亡后宿主停止；一名居民死亡不会结束其他人的生活。

## 让模型参与

```powershell
# 默认所有角色使用连接目录的 defaultConnectionId：
dotnet run --project src/Playground.Cli -c Release -- --home artifacts/my-hillside --max-calls 128 --until-hour 32
# a=Human、b=deliberate，其余使用默认连接；续局可更换覆盖：
dotnet run --project src/Playground.Cli -c Release -- --home artifacts/my-hillside --character-controls config/playground-character-controls.example.json --max-calls 64
```

[角色控制示例](config/playground-character-controls.example.json)使用 typed 绑定：`human-console` 或 `llm` 加 `connectionId`。绑定只在宿主启动时选择，不进入世界存档。未覆盖角色使用默认 LLM；`--no-character-controls` 清除 home 中的覆盖选择。未知角色、未知连接、缺失文件与非法形状均报错。全 Human 不读取凭据或创建客户端，但连接目录仍必须结构合法；单角色新局应使用仅含 a 的覆盖文件。

配置在每次启动冻结；续局可换配置，不改变角色与世界存档。凭据与客户端只在实际调用相应连接时读取/创建，按连接缓存，由宿主释放。可复制示例为 `config/*.local.json` 后修改，这类本机文件已忽略。旧 `--model`、`--auth-file`、`--catalog`、`--connection` 已删除，没有兼容入口；模型与认证设置放进连接配置。完整格式与边界见[多模型连接设计](docs/design/multi-llm-connections.md)。

每次模型调用收到角色保留的 Context，并通过 `act` 提交操作。领域层负责校验与提交。`--max-calls` 默认 128，`--max-failures` 默认 3，`--max-context-chars` 默认 200000；调用预算、连续协议失败、取消或失败响应会暂停宿主，不替角色选择替代行动。每次运行的模型原始审计在首次调用时懒建为 `llm-runs/<RunId>.jsonl`，`--llm-log PATH` 可指定位置；全 Human 不创建模型文件。统一宿主日志内联保存人物阅读需要的材料，实际 actor、connection、model 与 protocol 不进入世界。启动参数以当前 `--help` 为准。

地图探索与逐站导航已有独立[试玩 home 与命令](docs/design/character-navigation/playtest.md)，包括 Human/LLM 角色配置、续局和反馈。机制与真实运行证据见[组合验收](docs/experiments/character-navigation-d-acceptance.md)。

支持 `--lookahead on|off`（默认 `off`），也可在 `playground.json` 写 `"lookahead": "on"`，CLI 优先。开启后在当前角色等待 LLM 响应或 Human Console 输入期间，在独立内存世界预测一个合法动作，提前请求下一位合适的 LLM 角色；实际完整输入与目标身份匹配才采用响应。所有需求、预取及工具续轮共用 `--max-calls` 物理调用预算，采用已有响应不再次扣除；未采用内容只在 raw 的 `lookahead-event` 中记录。LLM 来源只使用本次运行已就绪的连接；Human 来源允许后台准备冷连接，结束输入时会等待准备退出。该能力已纳入本地主线，保持显式启用；收益与额外调用取决于场景，见[真实模型性能与 Human 等待测量](docs/research/playground-speculative-lookahead/results/real-performance.md)。

## 操作与个人材料

使用界面显示的当前地点入口、资源和角色编号行动：

| 意图 | 命令 |
| --- | --- |
| 重新查看、帮助、退出 | `:look`、`:help`、`:quit` |
| 向开发者提交反馈 | `:feedback <text>` |
| 移动 | `go <entrance-id>`（当前地点的入口编号） |
| 个人地图与求路 | `map`、`path <目标地点ID> [from <起点地点ID>]`；只使用本人已确认连接，保存一份候选，规划不自动移动 |
| 逐站导航 | `navigate <路径ID>` 启动本人当前候选；中间地点 `continue` 出发下一段；`cancel` 只清余下导航，已开始的路段继续至终点 |
| 采集 | `gather <resource-id> <count\|all>` |
| 等待 | `wait <1–24 小时>` |
| 使用补给 | `use item.food`、`use item.water`、`use item.ration` |
| 市场买卖 | `buy <item-id> <count>`、`sell <item-id> <count>` |
| 工坊加工 | `produce <recipe-id> <batches>` |
| 确认提醒并保留当前活动；导航中间点则出发下一段 | `continue` |
| 取消采集/等待/生产 | `cancel` |
| 定向说话、赠与 | `say <actor-id> <text>`、`give <actor-id> <item-id> <count>` |
| 赠钱 | `give <actor-id> money <amount>` |
| 发起报价 | `offer <actor-id> <asset> <amount> for <asset> <amount> [hours]` |
| 接受、拒绝、撤回报价 | `accept <trade-id>`、`reject <trade-id>`、`withdraw <trade-id>` |
| 大喊 | `shout <text>` |
| 跟随/解散 | `follow <actor-id>`、`unfollow`、`dismiss-followers` |

元命令使用冒号前缀，领域动作不使用冒号。默认 decision 布局把可用行动与相关资源、路线和风险放在一起。`:layout` 列出布局，`:layout classic` / `:layout decision` 切换；`:layout classic once` 临时查看另一种排版。`:mode full|delta` 控制完整或增量输出。查看和切换布局不推进时间，不确认提醒，也不修改世界。见[布局合同](docs/design/console-layouts.md)。

采集每份单独结算，`all` 冻结接受时库存作为有限目标，不预留库存，也不跟随再生增长。资源耗尽或其他采集者加入会请求响应；`continue` 保留原活动期限，`cancel` 保留已取得物品而放弃未完成的一份。无效命令与查询不会消耗世界时间或确认提醒。

移动有真实模拟耗时，已经开始的路段不能中止；导航 cancel 只清剩余路线。身体随时间消耗，低值提醒可能在途中要求回应；取消导航或使用补给不确认提醒，`continue` 才确认并继续本段。导航每到中间地点先观察，`continue` 才尝试下一段。温暖在庇护或白天野外恢复，夜间野外下降；天气每两小时可能带来最多 10 点额外降温，庇护可防止该损失。预测包含已知昼夜变化，不包含未来寒风。干木可以加工木器出售，当前还不能生火。角色探索知识来自亲历，旧观察标注时间，其他角色的陈述不会自动成为世界确认事实。

市场每日补充有限供应与收购额度，价格、剩余额度和下次补充时间在当地显示。买卖数量与生产批次为 1–1000。工坊每批用两份食物制作两份口粮（1 小时），或两份干木制作一件木器（2 小时）；口粮恢复 55 饱食。开工先托管原料，完成交付产物；取消或死亡退回未完成活动的原料。生产中处理通信、补给或赠与不改变原期限，接收物品还须为完成产物和取消退料分别留足空间。赠钱和物品赠与都使用通信额度。实际两次往返与账目见[经济验收](docs/experiments/living-world-economy-acceptance.md)；构建 CLI 后可运行 `pwsh -NoProfile -File scripts/Smoke-Hillside.ps1` 复验。

跟随需要同地且没有耗时活动；领队普通 `go` 带所有实际跟随者一起出发，到达仍保持关系。跟随者不能自行开始移动、采集、生产或等待，个人身体提醒仍需自己处理。到工作地点后可以解散，结束工作再集合；没有自动等待未加入同伴的机制。见[跟随设计](docs/design/character-following.md)。

报价里的资产可用 `money` 或物品 ID；期限为 1–24 小时，省略为 6 小时。例如 `offer b item.water 1 for money 2 4` 表示用一份水换两钱，四小时后到期。发起与接受须双方同地，拒绝或撤回可在远处自己的决策窗口进行；途中仍不能交易。报价不预留资产，接受时双方重新核对完整支付额与背包容量，一次成交；可尝试不保证成功。终态保留至实际交付后回收。交易不改变原生产/等待期限、跟随或既有警告。见[交易验收](docs/experiments/living-world-trades-acceptance.md)，构建后可运行 `pwsh -NoProfile -File scripts/Smoke-Hillside.ps1 -Scenario Trade` 复验。

说话、赠与限同地活跃角色；大喊送达本地与一跳声音连接地点的活跃角色，途中角色听不到。声音连接独立于移动通路，目前为 camp ↔ stream、ridge ↔ cabin。说话、赠物、赠钱、大喊与发起报价共用每角色每模拟时刻四次成功发送额度；接受、拒绝和撤回不消耗发送额度。无人听见的大喊也成功，不返回听众名单。通信唤醒不取消活动、不改变期限或跟随关系。独立闲置角色须安排耗时行动或加入跟随才能放行时间。见[大喊设计](docs/design/spatial-relations-and-shout.md)。

每名角色拥有独立的[目标记录](docs/design/goal-management/README.md)。例如 `create-goal {"title":"准备饮水","criteria":"出发前携带两份饮水","focus":true}`，`show-goal {}` 列出保留目标；界面列出其余编辑、关联、聚焦与结束命令。目标不会代执行活动，也不伪造完成事实。`handoff <text>` 为下一个 Context 留一次性便条，`handoff --clear` 清除；长期意图适合记录为目标。

## 持久化与观察

一个 DurableGraph 根保存业务子图与 Kernel 游标。派生预测在冷开时重建，在业务事实提交后增量维护。E 已提交、S 尚未完成的中断恢复只应用保存的事实及纯派生完成，不重新询问控制器或重新执行规划。机制见[运行时预测与恢复](docs/design/runtime-derived-predictions.md)。

当前游戏修订为 25；入口身份、个人连接知识、候选路径与活动导航改变了角色命令和持久材料，旧开发存档不迁移，请使用新的 store。完整有效内容及 SHA-256 指纹随 S 保存，E 绑定规划前态内容；冷开和分叉验证身份，运行中禁止替换定义。改变代码中的默认内容只影响新建世界；同修订旧局仍使用保存定义。

除带外 `:feedback` 外，每个非空 Console 操作提交拥有隐式 ToolCallId。`store/host/events.jsonl` 是所有分支、所有运行共用的有序宿主日志，保存操作接收与结果，包括查询与拒绝。冷开只对账当前分支自己的操作，依据已提交世界事实补齐遗漏结果，不重做动作。世界 E/S 继续是游戏恢复权威；宿主日志不提供跨文件事务或模型原生会话恢复。

`--gym off|record` 只控制详细人物材料：持久运行默认 `record`，`off` 仍保存运行与操作事实；ephemeral 默认 `off`，主日志仅在内存，显式 `record` 拒绝。`--gym-export FILE --actor ID` 从主日志导出 Observation、reasoning（Completion 的 PlainText，可为空）、普通 response、操作与游戏回应/后续经历；Human 没有 reasoning/response。普通导出不读取原始模型审计，raw 移走后仍可阅读主体。请求分叉只继承该请求之前的精确日志前缀。详见[角色 history 与统一宿主日志](docs/design/character-history-and-diagnostics.md)及 [Gym 使用说明](docs/design/character-gym/usage.md)。

独立反馈合同见[设计](docs/design/independent-feedback.md)：Human `:feedback <text>` 与 LLM `feedback({text})` 默认共用宿主保存，正文上限默认 4000 字符。持久局写 `store/feedback.jsonl`，ephemeral 写 `home/feedback.jsonl` 且不可分叉；成功回执在刷盘后返回。`--feedbacks` / `--fork-feedback ID` 不依赖主日志健康或模型配置，主日志仅记成功反馈 ID 的关联。

旧 `operations.jsonl`、`branch-operations/`、`gym/events.jsonl` 不迁移、不混写。检测到旧宿主格式且没有有效新主日志时，运行会在打开世界前拒绝；保留旧 store，使用新的 store 路径开始，home 和非敏感配置可继续使用。领域 Schema 与 GameRevision 不因这次日志重构改变。历史 Gym 验收保留其[原始版本语境](docs/experiments/living-world-gym-acceptance.md)。

## 原型与历史证据

Microtown 的生产、现金、有限市场与居民报价已融入山野镇；旧源码、三个测试项目及专属 smoke 脚本已从主线移除，由 [Microtown 归档](docs/archive/microtown.md)中的固定 Git tag 保留。历史设计与实验记录继续保留，旧证据不能替代融合验收。新增世界玩法与玩家入口统一在山野镇推进。

应用项目、namespace 与类型已统一为 `Playground` / `Playground*`，表达开发期机制实验应用的用途；山野镇 / Hillside 保留为综合场景名。依据见[开发实践参照](docs/research/playground-development-practices.md)，持久身份重建与验收见[重命名说明](docs/implementation/playground-rename.md)。

ResourceCycle（现 Playground）早期批处理、单地点采集与逃生玩法的命令模式已退出当前 CLI。历史 Smoke 脚本若包含旧参数，不作为当前启动入口；复验使用上文 `Smoke-Hillside.ps1` 和 `Smoke-HillsideContent.ps1`。历史实现与验证仍可查阅：

- [昼夜资源设计](docs/design/resource-cycle-demo.md)、[原始验收](docs/experiments/resource-cycle-acceptance.md)、[运行时缓存验收](docs/experiments/resource-cycle-runtime-cache-acceptance.md)。
- [多人采集设计](docs/design/resource-cycle-multiplayer-gathering.md)、[多人采集验收](docs/experiments/resource-cycle-multiplayer-acceptance.md)。
- [生存里程碑](docs/design/survival-demo-milestone.md)、[逃生试玩验收](docs/experiments/survival-demo-acceptance.md)、[真实模型旅程](docs/experiments/survival-llm-acceptance.md)。旧文档中的出口退休与启动指令属于当时版本。
- [目标管理验收](docs/experiments/goal-management-acceptance.md)、[通信验收](docs/experiments/survival-communication-acceptance.md)、[跟随验收](docs/experiments/character-following-acceptance.md)、[大喊验收](docs/experiments/spatial-shout-acceptance.md)。旧项目另行保留的真人合作体验项不被 Agent 操作 Console 冒充完成。

[Inventory](src/Inventory/ItemInventory.cs) 提供独立于时空核心的不可变物品数量和可选容量；游戏负责将跨模块效果一次提交，见[分层与组合](docs/design/layering-and-composition.md)。[SpacetimeHostScenarioTests](tests/Spatial.Tests/Acceptance/SpacetimeHostScenarioTests.cs) 独立验证两实体相遇、掉头、到达、确定性仲裁与重放；游戏磁盘 adapter 并非任意 Spatial 游戏的通用存档宿主。

旧 Player/Protocol、角色规划、Server/WebUI 与专用测试已冻结于 [Player 归档](docs/archive/player-runtime.md)，FirstBoard 见[独立归档](docs/archive/firstboard-llm.md)。如需重现旧应用，使用归档记录的基线和独立 worktree。
