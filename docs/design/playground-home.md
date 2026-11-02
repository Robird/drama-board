# Playground 实例目录与启动配置

状态：已实施。home、typed 角色控制、统一宿主与当前消费者已迁移；工程与实际 CLI 证据见[实施验收](../experiments/playground-home-acceptance.md)。本次真实模型尝试遇到 transport failure，未作为在线成功。

## 1. 目标与需求账本

运行实例目录（home）将一份宿主启动设置、模型连接配置与一个世界存档放在一起。日常只指定 home，或在 home 内启动；开发时用少数命令行参数覆盖。home 不是操作系统用户目录，不是世界的领域身份，也不是全局配置中心。

| 需求与约束 | 来源与证据 | 裁决边界 |
| --- | --- | --- |
| 缩短日常启动命令，配置与存档通常放在一起，允许局部开发覆盖 | 用户本轮前的直接诉求 | 必须支持 |
| 先写方案并进行辩证简化，再按方案实施 | 用户前轮设计要求与本轮明确实施授权 | 方案与实现证据分开记录 |
| 不承担旧接口兼容包袱 | 用户此前连续实施任务的明确选择；本设计延续 | 不设旧参数兼容层；不能据此删除世界或改写历史证据 |
| 默认所有角色使用 LLM DefaultConnection；逐角色覆盖为指定 LLM Connection 或 Human Console | 用户最新明确采纳的双层映射方案 | 替换全局 Human/LLM 开关和特殊 Human 优先通道 |
| 控制绑定仅属于运行时配置，不进入 DurableGraph 持久化领域模型 | 用户本轮明确要求；第 5 节源码可行性证据 | 不增加领域字段、事件、Schema 或 GameRevision |
| 连接目录、按需凭据、共享调用预算与实际调用身份 | [多连接设计](multi-llm-connections.md)、[配置读取](../../src/CharacterControl.Completion/LlmConnectionConfiguration.cs)、[统一宿主](../../src/Playground.Cli/PlaygroundHost.cs) | 保留调用合同；旧角色连接覆盖升级为控制绑定，不保留双路由 |
| 内存运行、磁盘冷开、多分支管理、只读查询/导出与显式 fork | [CLI](../../src/Playground.Cli/Program.cs)、[统一宿主](../../src/Playground.Cli/PlaygroundHost.cs)、[管理入口](../../src/Playground.Cli/Gym/GymCommands.cs) | 保留真实用途，不为旧命令拼写保留双实现 |
| 单进程串行驱动，E/S 为游戏恢复权威，pending 不重问控制器 | 上述宿主代码与[初始化设计](game-initialization-and-revision.md) | 不增加服务、配置数据库、运行中热更新或跨进程调度 |
| home 标识、init、路径与参数覆盖细节 | 助手建议与前轮审阅裁决 | 延续可用部分；撤回“init 后默认 Human 即可玩”的旧建议 |

本文保留设计裁决与约束，实际实现证据另见验收文档；未来多人协作、云同步、多应用通用 workspace 均没有当前消费者。

## 2. 目录与使用

```text
my-playground/
├── playground.json             # 宿主设置，也是 home 标识
├── llm-connections.json         # 连接目录及 defaultConnectionId
├── character-controls.json    # 可选，角色控制绑定覆盖
└── store/                      # 默认世界存档与关联实验材料
    ├── …                       # DurableGraph 数据与 operations
    ├── gym/
    └── llm-runs/
```

一个 home 默认指向一个 store；store 已能容纳多个分支，不另造 home ID、world ID 或当前分支指针。配置仍在世界存档之外。凭据由 Completion 现有机制读取，不复制登录文件到 home。

以下 `playground` 仅为已构建 CLI 可执行文件的简写；不包含发布 .NET tool、安装到 PATH 或新建启动器的工作：

```powershell
playground init --home E:\playgrounds\hillside
# 首次运行前按下文准备 llm-connections.json
playground --home E:\playgrounds\hillside
# CWD 为该 home 时
playground
# mixed-controls.json 采用下文格式；CLI 相对路径以 CWD 为基准
playground --home E:\playgrounds\hillside --max-calls 4 --character-controls mixed-controls.json
playground --home E:\playgrounds\hillside --no-character-controls
playground --home E:\playgrounds\hillside --branches
```

`--home PATH` 优先，否则使用调用时 CWD。只识别这个目录，不搜索父目录，不读取用户级配置，不依赖进程环境变量选择 home；禁止 `Directory.SetCurrentDirectory` 改变全局路径语义。正常运行要求存在有效 `playground.json`，缺少则报错并提示 init，不隐式初始化。

`--help` 不要求有效 home。`init` 可作用于不存在或已有的目录，仅使用 CreateNew 写入一个 `playground.json`，内容为 `{ "version": 1 }`；文件已存在即拒绝。它不创建 store、不打开模型、不检查或改写已有存档。不引入多文件初始化、回滚或修复模式；单文件写入发生 I/O 失败时如实报告，不能把残留的坏文件认作有效 home。

默认绑定为 LLM，因此 init 只完成目录标识，并不代表模型配置已就绪。首次运行前，将现有[连接示例](../../config/llm-connections.example.json)复制为 home 内 `llm-connections.json`，或在 manifest 中指向已有文件；必要时调整其默认连接。需要角色覆盖时再准备第 3 节格式的 `character-controls.json` 并显式填写其路径。[角色控制示例](../../config/playground-character-controls.example.json)和[全 Human 示例](../../config/playground-all-human.example.json)采用新协议。

init 明确输出“已创建启动文件；运行前需准备默认连接配置”，并指向同一文档/示例；不内嵌第二份 provider 模板。正常启动若缺少连接配置即报错，不能退回 Console，也不能暗中选择模型。完成一次配置后，只传 home 或在其 CWD 启动即可按配置运行。

## 3. 启动配置与覆盖

典型 LLM home 配置（其余字段使用默认值）：

```json
{
  "version": 1,
  "gym": "record",
  "maxCalls": 16,
  "characterControls": "character-controls.json"
}
```

本例假设连接目录与角色控制文件已准备好。首版 manifest 的完整字段集为 `version`、`gym`、`maxCalls`、`maxFailures`、`maxContextChars`、`steps`、`store`、`llmConnections`、`characterControls`。采用严格 version 1 JSON，拒绝重复字段、未知字段、错误类型、非法枚举和非正预算，不做字符串到数字的隐式转换。`characterControls` 可为 null，其余字段若出现必须有符合类型的值；路径和 ID 不接受空白字符串。不再提供 `control`、`humanActor` 或 `characterConnections` 字段。

优先级为内置默认值 → home 配置 → 本次显式 CLI。只按顶层选项替换，不递归合并、不拼接集合、不逐字段合并连接目录。CLI 必须保留“未指定”，不能先填默认值覆盖 home 设置；显式关闭、清除须区别于未指定。重复参数、同一选项的设置与清除同时出现均报错，不按参数顺序决定胜者。

| 项目 | 内置默认值与规则 | CLI 覆盖建议 |
| --- | --- | --- |
| Gym 模式 | `gym: off`；`record` 记录轨迹；独立反馈默认可用 | `--gym off\|record`，整项替换 |
| 调用与材料预算 | 128 次调用、3 次连续协议失败、200000 字符；跨连接共享 | 沿用 `--max-calls`、`--max-failures`、`--max-context-chars` |
| 转换预算 | 10000 | 沿用 `--steps` |
| store | `store` | `--store PATH`；`--ephemeral` 显式内存运行，与显式 store 互斥 |
| 连接目录 | `llm-connections.json` | 沿用 `--llm-connections PATH` |
| 角色控制覆盖 | `characterControls: null`；省略或 null 都表示没有覆盖，不探测文件 | `--character-controls PATH`；`--no-character-controls` 清除整个覆盖文件选择 |

home 内路径相对 home；命令行路径相对调用时 CWD。绝对路径保持绝对路径，允许引用共享外部连接文件；此时复制 home 不代表包含外部依赖。嵌入连接文件中的 provider 字段与认证定位仍归 Completion，不自动进行全文件路径重写。

正常运行读取并校验所选连接目录，包括默认 `llm-connections.json`，以及被显式选择的控制覆盖文件；缺失或格式错误即失败。可选意味着不选择覆盖文件，而非选择后可静默忽略缺失；未引用的同名文件即使存在也不生效。文件在启动时各读取一次并冻结，不响应后续编辑。即使实际角色全部覆盖为 Human，也保持默认连接目录及其 ID 的结构有效性要求，但不读取未使用的凭据、不创建客户端；管理命令仍完全不读取这些文件。

### 3.1 双层角色控制映射

连接文件继续沿用 `{ "version": 1, "defaultConnectionId": "quick", "catalog": { ... } }`，由它唯一选择默认连接。角色控制文件不再声明第二个默认值，格式为：

```json
{
  "version": 1,
  "overrides": {
    "a": { "kind": "human-console" },
    "b": { "kind": "llm", "connectionId": "deliberate" }
  }
}
```

```text
Binding(actorId) = overrides.TryGet(actorId)
                ?? Llm(connectionConfiguration.DefaultConnectionId)
```

这份配置使 a 使用 Console，b 使用 deliberate，其余角色使用 quick。控制绑定是明确区分两种形状的值：`HumanConsole` 与 `Llm(ConnectionId)`。`human-console` 不允许 connectionId，`llm` 必须提供真实连接 ID；不得用特殊连接名表示 Human，不允许 null 绑定、字符串简写、通配角色或第二套 Human 列表。角色 ID、连接 ID 精确区分大小写，重复角色/未知 kind/未知字段都拒绝；所有覆盖角色都须属于本局实际角色集合，已结束但仍在世界中的角色仍可有绑定。

省略文件或使用空 overrides 表示全 LLM 默认连接；若要全 Human，就显式列出本局全部角色为 human-console。多个 Human 角色按 Kernel 既有顺序共用同一终端，不增加并行输入或远程玩家会话。新增角色若无覆盖就用默认 LLM，这正是默认规则，不按“此前是否全 Human”推测意图。

CLI 只能选择/清除控制文件，不再逐角色叠加第三层覆盖：删除 `--llm`、`--human` 与旧 `--character-connections`，也不引入前稿建议的 `--control` / `--no-human`。如需临时接管，选择一份相应的控制文件；切换文件替换整个 overrides，不合并两份映射。不存在“Human 优先”算法，因为每个角色只解析出一个最终绑定。

### 3.2 预算、记录与本次参数

Gym 只选择 `off` 或 `record`，不控制反馈启用。Human 和 LLM 默认共用独立反馈能力；持久运行写 store/feedback.jsonl，ephemeral 写 home/feedback.jsonl 且 Boundary=null。程序化入口无 store/home 时不保存。详细合同见[独立反馈](independent-feedback.md)，本切片已实施，见[实施验收](../experiments/independent-feedback-acceptance.md)。Gym 可以记录 Human 的 Context/操作，但不伪造模型请求。调用与连续协议失败预算由一个 LLM loop 在所有 LLM 角色之间共享，Human 输入不计入；steps/until 仍约束整个世界。没有自动重试、连接 failover 或错误时回退 Console。

`--content`、`--characters`、`--branch`、绝对 `--until-hour`、单次 `--llm-log`、管理查询/导出/fork 参数只放 CLI，不持久写回配置。这样不会因初始化模板、旧实验分支或已越过的模拟时限改变后续普通启动。LLM 日志仍默认每次生成独立文件；Gym 与操作日志仍跟随实际 store，不跟随未使用的默认目录。

## 4. 新局、续局与管理

首版没有 `newGame` 字段。新局沿用 hillside、3 人默认值，用 CLI `--content` / `--characters` 一次性选择其他配置；续局省略则只读取保存的定义，显式指定则按当前 `WorldHost` 行为作一致性断言，不改写世界。保留[内容测试](../../tests/Playground.Cli.Tests/ContentHostTests.cs)明确验证的顺序：不匹配在 pending 恢复与宿主日志写入之前拒绝。无需记录配置来源来区分两种初始化语义。

正常持久运行：store 不存在时创建；存在时打开，空目录/损坏目录不能当作新局重新初始化。init 不预建空 store，以符合当前 `Directory.Exists` 打开/创建边界。多分支 store 未指定 branch 时延续现有拒绝歧义规则，不记忆“上次选择”。

管理目标按下列惰性选择确定，不建立第二套管理实现：

```text
显式 --store S → 直接管理 S，不发现或读取 home
否则          → 从 --home H 或 CWD 的有效 playground.json 取得 store
最终都调用现有 GymCommands(store, ...)
```

即使同时给 `--home H --store S`，管理操作仍以 S 为准，不读取 H；管理输出不伪称已加载 home。这样一个常用运行命令加上 `--branches` 时路径覆盖关系不改变，也可在 manifest 损坏时独立检查存档。[Gym smoke 脚本](../../scripts/Smoke-HillsideGym.ps1)已有直接面向 store 的真实消费者。管理未给 store 时需要严格有效的 manifest，坏文件不猜路径或降级；只取 store，不加载模型配置、不校验运行绑定关系。

管理不启动控制器、不创建运行日志、不执行 pending 恢复。`--branches`、反馈查询、导出保持现有源存档只读保证；fork 保持显式写入语义，不把所有管理命令称为只读。管理仍拒绝混入角色控制文件、模型预算、内容等运行专用 CLI 参数；home 中合法但未使用的运行设置不触发运行行为。`--help`、init、管理、运行分别进入对应路径，不因默认绑定为 LLM 被引导进运行路径。

内存运行用于开发演练；`--ephemeral` 仍从 home 取得宿主配置，但 world 的 store=null，不能配合 branch 或依赖存档的管理/Gym。home 已配置 Gym 时需显式 `--gym off` 再使用 ephemeral，不静默丢弃用户要求的记录。统一宿主沿用现有运行审计：持久运行默认 `store/llm-runs/<run-id>.jsonl`，内存运行默认 `home/llm-runs/<run-id>.jsonl`，均可用 `--llm-log` 覆盖。目录/参数暂沿用既有命名，文件也包含宿主与提交记录；全 Human 可以有这些记录，但不能伪造模型请求/响应。ephemeral 只表示世界不落盘，并非没有任何文件写入。

程序调用仍可直接提供内存 history 与控制器依赖，无须制造 home；重构不承诺保留旧宿主静态方法的拼写，但保留这项测试/嵌入用途。CLI 正常运行只保留一种 home 解析入口，包括 ephemeral，不增加独立裸参数运行模式。显式 store 的管理入口只是已有工具所需的存档定位，不扩张成另一种运行宿主。

## 5. 软件实现边界

### 5.1 可行性与持久化隔离

源码检查结论：可行，现有依赖注入和恢复边界已经支持；需要统一宿主路由与呈现，不需要把控制绑定加入 DurableGraph。

| 已检查的实现/测试 | 证据与实施含义 |
| --- | --- |
| [PlaygroundEngine.CreateKernel](../../src/Playground/PlaygroundEngine.cs) | 控制器作为运行时参数交给 rule；无需从世界模型读取控制类型或连接 |
| [CharacterControllerRouter](../../src/Playground.Cli/WorldHost.cs) | 已按 actorId 将同一决策接口分派到控制器；可接收冻结后的完整绑定投影 |
| [业务状态](../../src/Playground/PlaygroundModels.cs)、[持久根](../../src/Playground/Persistence/PlaygroundDurableRoot.cs)、[持久事件](../../src/Playground/Persistence/PlaygroundDurableEvent.cs) | 保存世界/角色材料、游标、修订及已决定事实，没有控制绑定；保持此边界 |
| [LivingWorldHostTests](../../tests/Playground.Cli.Tests/LivingWorldHostTests.cs)、[MultiConnectionHostTests](../../tests/Playground.Cli.Tests/MultiConnectionHostTests.cs) | 已有冷开改为 Human、冷开换连接与共享预算的测试；证明现有 API 支持，非本轮重新运行的验收 |
| [恢复测试](../../tests/Playground.Cli.Tests/OperationRecoveryIntegrationTests.cs)及宿主 RecoverPending 调用 | 已发布行动按记录恢复，不重新调用控制器；绑定更换不应改变此顺序 |
| [PlaygroundHost](../../src/Playground.Cli/PlaygroundHost.cs)、[GymSession](../../src/Playground.Cli/Gym/GymSession.cs) | 实施前多处比较单个 HumanActor；本次路由、终局输出、观战材料与 run-start 已统一使用完整绑定映射 |

**持久化禁区**：不把控制 kind、ConnectionId、默认绑定、home 路径、客户端或控制器引用加入 `PlaygroundBusinessState`、角色、WorldDefinition、Context、`PlaygroundDurableRoot` 或游戏事件。不新增“控制器切换”领域事件，不更新内容指纹、Schema 或 GameRevision。角色身份、记忆、活动和行动事实仍按既有领域模型持久化；ToolCallId/OperationRecordId 等原有操作来源字段保留，它们不代表控制策略。

JSON 文件可以保存在磁盘，Gym/运行审计可以记录当次实际 actor→控制 kind/连接身份，但它们属于宿主配置或证据；冷开/分叉不从旧日志还原绑定。恢复原有 pending E 后，下一次真正需要角色决策时才使用本次绑定；若预算/退出条件先暂停，就不调用控制器。运行中不热切换，不恢复原生模型会话。

### 5.2 宿主重构

home 发现、控制绑定 JSON 与角色路由留在 Playground.Cli，不为一个应用提取通用控制插件框架。`CharacterControl.Completion` 继续负责连接目录和连接池，但应从 `LlmConnectionConfiguration` 中移除旧角色字符串覆盖及 `GetConnectionId(actorId)` 职责：目录只暴露 DefaultConnectionId、连接身份与合法定义；由宿主解析控制绑定并向 pool 请求具体 ConnectionId。池、LLM loop 的借用/所有权和预算合同保持。Atelia.Completion 不认识 human-console；Kernel、Spatial、DurableGraph 与领域模型不认识 home 或绑定文件。

建议从 `Options.Parse` 拆出显式命令行解析、home 文件读取与生效选项解析，采用普通内部 record/函数。控制绑定用区分 HumanConsole 与 Llm(ConnectionId) 的只读值表达，启动时按本局实际角色生成唯一完整只读字典；路由、呈现、启动摘要、Gym 均消费它，不各自重复“Human 优先”判断。

原 PlayConsole/LlmConsole 重复的存档开启、pending 恢复、操作对账、循环预算和退出处理已收敛为一个宿主执行流程，名称使用中性的 Playground 运行宿主概念；Console 控制器及 LLM adapter 分别保留。所有 Human 角色共用一个 Console 实例，所有 LLM 角色共用一个 loop 与连接 pool，客户端只在实际调用时创建。没有 Human 时不等待终端；全部 Human 时不创建模型客户端，不读取认证。不要为每个角色新建 loop 而重置共享调用预算。

呈现/日志同步适配：按 Human 角色集合交付各自 Context 和终局材料；只要存在 Human 角色，就继续避免将其他 LLM 的完整请求、推理或私有材料直接打印到共享玩家终端。共享终端不承诺隔离同一操作者对不同 Human 角色的记忆。Console 已在解析前记录输入，LLM 的 RecordedController 记录模型返回操作，统一宿主不能把两条入口重复包裹而记录两次。Gym/run-start 按映射记录全部角色；Human 不伪造 connection/model/protocol，LLM 请求仍按实际 trace 记录这些身份。

先识别命令及显式参数：help 不读文件；init 只创建标识；管理按第 4 节定位 store 后直接进入原管理函数；正常运行才定位 home、合并设置、规范化路径并进入宿主。init 仅接受 `--home`，管理保留各操作的参数白名单并允许 `--home`；不把运行设置的默认值伪装成显式管理参数。

正常运行的新局先在内存构造 initial，使用同一个 initial 校验已知角色与预算后再 `PlaygroundDurableHistory.Create`，不重复创建世界、不在报错后删除存档回滚。已有 store 的一致性验证、pending 恢复和决策顺序保持原合同。凭据只在实际首次模型调用才解析。

实施前 `PlayConsole.RunAsync` 和 `LlmConsole.RunAsync` 可能先创建 store，再校验预算；本次已把新局角色和预算的可知校验前移到创建边界。该保证仅限静态配置及创建前可知错误；I/O、凭据、日志写入或远端调用失败不承诺零残留，不扩展成启动事务。

运行启动摘要打印实际 home/store/branch、控制绑定、预算、Gym 模式、所选配置路径和日志位置，不打印连接配置全文或凭据。实际模型调用仍以原有 trace 身份为准；不增加完整配置快照日志或第二份恢复权威。管理命令保留当前机器可读输出，不向 `--branches` 的 JSONL stdout 前插普通文字，也不额外产生成功 stderr；直接 store 的定位规则写入帮助和文档。

## 6. 最小实施切片与验收

一次纵向切片完成 home 读取、双层控制绑定、单宿主运行接线、init/管理、创建前预算校验及示例帮助更新。不先发布通用配置库。删除旧 `--llm`、`--human`、`--character-connections`、无值 `--gym`、独立 `--feedback` 及旧角色字符串映射格式，更新当前脚本/README/测试，不重写历史实验命令。现有 store 可用 `--store` 路径覆盖接入 home，不迁移存档格式或强制搬动目录。更新 smoke 时，管理可继续直接指定 store；运行子进程需要显式 home、角色控制文件与新 Gym 参数。

验收重点：

1. init 不调用模型、不创建 store、不覆盖现有文件，并提示连接配置仍需准备；缺少默认连接时正常运行拒绝且不建 store，不回退 Human。任意 CWD 的 help 不读 home；缺失/坏格式 home 清楚报错。
2. 显式 home 与 home 内 CWD 启动选中同一实例；父目录标识不被搜索；两种相对路径基准、不同参数顺序与带空格路径均正确。
3. CLI 只覆盖显式项目，Gym 整项关闭、角色控制文件整项替换/清除有效；未知/重复字段、非法绑定形状、未知角色/连接失败；未引用同名文件不生效，已引用缺失必须失败；运行中修改文件不改变生效配置。
4. 无覆盖全默认 LLM、指定连接覆盖、单/多 Human 混合、全 Human；路由、呈现与 Gym 身份一致。全 Human 与 Human EOF 在首个模型决策前退出都不读取凭据/创建客户端；所有 LLM 角色共享预算，Human 不计入。Human 与 LLM 新局分别验证 `--until-hour 0`：无新 store、无日志、无调用；已知错误角色同样在创建前拒绝。
5. 同一 home 两次启动续接世界；保存内容权威与显式 CLI 断言保持；默认 home 可以打开 gentle/单角色世界；内存模式不产生 store，模型日志落 home 而非任意 CWD。
6. LLM home 上的管理查询不接触凭据或创建日志；显式 store 在缺失/损坏 home 时仍可管理；查询/导出前后源字节不变，JSONL 输出可按原脚本读取；fork 仍显式注册分支，多分支续跑必须选择。
7. 同一存档冷开或分叉可按新配置在 Human/不同 LLM 之间改绑；不产生控制切换领域事件，pending 恢复不调用任一控制器、不重做已记录行动，角色记忆与原操作身份保持。Schema Verify 且领域 Schema、GameRevision、内容指纹无变化；代码检查绑定类型只存在宿主配置层。
8. 现有角色隔离、操作输入只记录一次、实际协议身份、Gym 分叉与导出测试回归；多 Human 下检查私有材料与终局交付。fixture 验证全 Human Gym 记录不伪造模型调用。真实模型调用不是配置解析正确性的前提，实施时按变更风险决定是否补一次小预算冒烟。

## 7. 暂缓与重开条件

- 日志去重：仍按[后续待办](../worksets/diagnostics-feedback-backlog.md)独立延期，不合并 JSONL。[独立反馈](independent-feedback.md)已实施并验收，替换 home 初版的三态 Gym，见[实施验收](../experiments/independent-feedback-acceptance.md)。
- 全局配置、profile 继承、自动发现父目录：多个真实实例出现无法由文件路径共享解决的重复配置需求后重评。
- 热加载、运行中切模型：需要不中断角色决策的具体产品故事后重评。
- 多世界清单、home 管理器与自动分支选择：出现频繁管理多个世界的实际操作证据后重评。
- `newGame` 模板：出现反复创建相同非默认世界的消费者后重评；一次性新局参数和已有存档目前足够。
- init 自动复制连接目录：需要脱离源码包的开箱模型接入时重评；当前仅使用现有示例，不维护第二份 provider 模板。
- 可移植实验包、全量配置快照、凭据搬迁：独立交付需求出现后设计；当前目录组织不承诺封装外部依赖或旧日志中的绝对路径。
- 多进程写同一存档：沿用现有存档约束，不新增 home 锁协议或守护进程。

## 8. 辩证简化记录

本节保留 home 初稿裁决背景；其中三态 Gym 已由[独立反馈](independent-feedback.md)的 off/record 合同替换。2026-11-02 的 home 初稿使用 `dialectical-simplification`：需求质疑者、最小架构师、语义守护者独立提出论点，再交换反例质询；主线程核对源码/测试及 smoke 消费者后裁决。其后用户明确采纳双层控制映射并要求只作运行时配置，本版由主线程进行第 5 节的源码可行性核对后修订；没有声称新映射另经同一轮三人审阅。两次文档工作均未运行测试或真实模型调用，不把设计检查称为实现验收。

| 裁决 | 初稿机制 | 最终机制及不能丢失的行为 |
| --- | --- | --- |
| merge | Gym 与 feedback 两个布尔、非法组合及关闭联动 | 一个三态 Gym 模式；保留当前 feedback 蕴含记录的真实语义 |
| merge | 默认 Human → 全局 LLM 开关 → 单个 Human 覆盖 → 角色模型选择 | 用户新选择：默认 LLM(DefaultConnectionId)＋唯一角色控制覆盖；不进入持久化领域模型 |
| simplify | 自动探测可选覆盖、缺文件回退、禁用探测 | 可空显式路径；防止仅因文件增删无声改变角色模型 |
| defer | newGame 对象及来源不同的续局解释 | 仅一次性 CLI 选择；保存定义与显式续局断言继续有效 |
| simplify | init 默认写两文件与部分完成合同 | 只创建 manifest；首次模型接入使用现有连接示例 |
| keep | home/CWD 与 CLI 两种相对路径基准 | 调用位置与配置位置不同是真实场景；一次规范化，不修改 CWD |
| keep | 管理与运行分流、直接 store 管理 | 复用 GymCommands；不能因模型配置坏了就执行恢复或阻断独立存档检查 |
| simplify | 笼统“配置失败无残留” | 先校验已知新局约束再发布；不承诺 I/O/远端错误全回滚 |
| delete | 为过渡保留旧控制/Gym 拼写的潜在适配层 | 同时更新当前消费者；历史证据不改写，存档格式不变 |

前轮审阅存在两项便利性取舍：一位审阅者偏好 init 同时复制连接示例，并接受所有管理均要求 home。单文件 init 与显式 store 管理仍保留；默认 LLM 改变了初始化后的体验，因此本版明确 init 不等于可运行，准备连接后才可启动，不再声称默认 Human 开箱即玩。允许管理同时传 home/store、由显式 store 胜出。后两项仍是方案细节建议；双层映射及不进入 DurableGraph 则为用户明确裁决。

可观察的缩减：两个 Gym 设置合为一个；删除 newGame 对象及来源分流、可选文件探测；init 从两次文件创建减为一次；再删除全局控制模式与特殊 Human 字段，角色控制和模型选择合为一个绑定。第 6 节的实施切片已完成，日志去重仍按第 7 节延期；独立反馈已另行实施并验收。
