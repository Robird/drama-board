# Playground 实例目录与启动配置

状态：经过三位独立审阅者、两轮辩证审阅修订的实施方案，尚未实现。本文命令与字段是拟议接口；当前可执行入口仍见[根 README](../../README.md)。本轮仅设计与文档，不修改运行代码。

## 1. 目标与需求账本

运行实例目录（home）将一份宿主启动设置、模型连接配置与一个世界存档放在一起。日常只指定 home，或在 home 内启动；开发时用少数命令行参数覆盖。home 不是操作系统用户目录，不是世界的领域身份，也不是全局配置中心。

| 需求与约束 | 来源与证据 | 裁决边界 |
| --- | --- | --- |
| 缩短日常启动命令，配置与存档通常放在一起，允许局部开发覆盖 | 用户本轮前的直接诉求 | 必须支持 |
| 写方案并进行辩证简化；本轮不实施代码 | 用户本轮直接要求 | 文档编辑已授权 |
| 不承担旧接口兼容包袱 | 用户此前连续实施任务的明确选择；本设计延续 | 不设旧参数兼容层；不能据此删除世界或改写历史证据 |
| 两份连接配置、默认连接与角色覆盖、Human 优先、按需凭据、共享调用预算 | [多连接设计](multi-llm-connections.md)、[配置读取](../../src/CharacterControl.Completion/LlmConnectionConfiguration.cs)、[LLM 宿主](../../src/Playground.Cli/LlmConsole.cs) | home 不改变这些运行语义 |
| 内存运行、磁盘冷开、多分支管理、只读查询/导出与显式 fork | [CLI](../../src/Playground.Cli/Program.cs)、[Human 宿主](../../src/Playground.Cli/PlayConsole.cs)、[管理入口](../../src/Playground.Cli/Gym/GymCommands.cs) | 保留真实用途，不为旧命令拼写保留双实现 |
| 单进程串行驱动，E/S 为游戏恢复权威，pending 不重问控制器 | 上述宿主代码与[初始化设计](game-initialization-and-revision.md) | 不增加服务、配置数据库、运行中热更新或跨进程调度 |
| home 标识、init、默认 Human、覆盖细节 | 上轮助手建议，本方案细化 | 设计建议，不冒充用户逐项裁决或现有实现 |

本文与上轮建议来自同一提案，不作为彼此的独立证据。拟议默认值与可见行为接受下文审阅；未来多人协作、云同步、多应用通用 workspace 均没有当前消费者。

## 2. 目录与使用

```text
my-playground/
├── playground.json             # 宿主设置，也是 home 标识
├── llm-connections.json         # 沿用现有两文件协议之一
├── character-connections.json  # 可选，沿用角色覆盖协议
└── store/                      # 默认世界存档与关联实验材料
    ├── …                       # DurableGraph 数据与 operations
    ├── gym/
    └── llm-runs/
```

一个 home 默认指向一个 store；store 已能容纳多个分支，不另造 home ID、world ID 或当前分支指针。配置仍在世界存档之外。凭据由 Completion 现有机制读取，不复制登录文件到 home。

以下 `playground` 仅为已构建 CLI 可执行文件的简写；不包含发布 .NET tool、安装到 PATH 或新建启动器的工作：

```powershell
playground init --home E:\playgrounds\hillside
playground --home E:\playgrounds\hillside
# CWD 为该 home 时
playground
# 以下假设已按下文准备连接文件，并将 home 的 control 设为 llm
playground --home E:\playgrounds\hillside --max-calls 4 --human a
playground --home E:\playgrounds\hillside --control human
playground --home E:\playgrounds\hillside --branches
```

`--home PATH` 优先，否则使用调用时 CWD。只识别这个目录，不搜索父目录，不读取用户级配置，不依赖进程环境变量选择 home；禁止 `Directory.SetCurrentDirectory` 改变全局路径语义。正常运行要求存在有效 `playground.json`，缺少则报错并提示 init，不隐式初始化。

`--help` 不要求有效 home。`init` 可作用于不存在或已有的目录，仅使用 CreateNew 写入一个 `playground.json`，内容为 `{ "version": 1 }`；文件已存在即拒绝。它不创建 store、不打开模型、不检查或改写已有存档。不引入多文件初始化、回滚或修复模式；单文件写入发生 I/O 失败时如实报告，不能把残留的坏文件认作有效 home。

初始默认 Human，init 后即可零参数进入 Console。首次接入 LLM 需一次性准备连接：将现有[连接示例](../../config/llm-connections.example.json)复制为 home 内 `llm-connections.json`，或在 manifest 中指向已有文件；将 `control` 设为 `llm`。如需按角色选择，再准备[角色覆盖](../../config/playground-character-connections.example.json)并显式填写其路径。init 的帮助指向同一文档/示例，不内嵌第二份 provider 模板。连接文件存在本身不启用 LLM；配置好后普通启动仍为零额外参数。

## 3. 启动配置与覆盖

典型 LLM home 配置（其余字段使用默认值）：

```json
{
  "version": 1,
  "control": "llm",
  "gym": "record",
  "maxCalls": 16,
  "characterConnections": "character-connections.json"
}
```

本例假设两份连接文件已准备好。首版 manifest 的完整字段集为 `version`、`control`、`humanActor`、`gym`、`maxCalls`、`maxFailures`、`maxContextChars`、`steps`、`store`、`llmConnections`、`characterConnections`。采用严格 version 1 JSON，拒绝重复字段、未知字段、错误类型、非法枚举和非正预算，不做字符串到数字的隐式转换。`humanActor`、`characterConnections` 可为 null，其余字段若出现必须有符合类型的值；路径和 ID 不接受空白字符串。

优先级为内置默认值 → home 配置 → 本次显式 CLI。只按顶层选项替换，不递归合并、不拼接集合、不逐字段合并连接目录。CLI 必须保留“未指定”，不能先填默认值覆盖 home 设置；显式关闭、清除须区别于未指定。重复参数、同一选项的设置与清除同时出现均报错，不按参数顺序决定胜者。

| 项目 | 内置默认值与规则 | CLI 覆盖建议 |
| --- | --- | --- |
| 控制方式 | `control: human`；`llm` 表示除可选 Human 角色外都用模型 | `--control human\|llm` 替代单向 `--llm` |
| Human 角色 | `humanActor: null`；只在 LLM 控制方式下使用 | `--human ID`，`--no-human` 清除；不把特殊字符串塞进角色 ID |
| Gym 模式 | `gym: off`；`record` 记录轨迹；`feedback` 同时记录并启用模型反馈工具 | `--gym off\|record\|feedback`，整项替换；删除独立 `--feedback` |
| 调用与材料预算 | 128 次调用、3 次连续协议失败、200000 字符；跨连接共享 | 沿用 `--max-calls`、`--max-failures`、`--max-context-chars` |
| 转换预算 | 10000 | 沿用 `--steps` |
| store | `store` | `--store PATH`；`--ephemeral` 显式内存运行，与显式 store 互斥 |
| 连接目录 | `llm-connections.json` | 沿用 `--llm-connections PATH` |
| 角色连接覆盖 | `characterConnections: null`；省略或 null 都表示没有覆盖，不探测文件 | 沿用 `--character-connections PATH`；`--no-character-connections` 清除 |

home 内路径相对 home；命令行路径相对调用时 CWD。绝对路径保持绝对路径，允许引用共享外部连接文件；此时复制 home 不代表包含外部依赖。嵌入连接文件中的 provider 字段与认证定位仍归 Completion，不自动进行全文件路径重写。

在 LLM 运行路径中，所选连接文件不存在或格式错误即失败，包括默认 `llm-connections.json` 和显式配置的角色覆盖。可选意味着不选择覆盖文件，而非选择后可静默忽略缺失；未引用的同名文件即使存在也不生效。需要的配置文件在启动时各读取一次，不响应后续文件编辑。

Human 模式不读取连接文件、不要求凭据，保留配置中 dormant 的模型预算/路由选择；切回 LLM 时才使用。`--control human` 能独立覆盖一个 LLM home，无须改文件或手动关闭 Gym：Human 方式下模型专用设置不生效，启动摘要说明。manifest 本身的字段、类型和值仍须有效；不因 dormant 而接受拼错的配置。显式传入模型专用 CLI 参数却最终选择 Human 则报错，沿用当前对无效参数组合的明确拒绝。只传 `--control human` 不属于此类冲突。

三个 Gym 模式在宿主边界派生为现有的 `(Gym, Feedback)`：off → `(false, false)`，record → `(true, false)`，feedback → `(true, true)`。不新增第四种状态，不需要跨字段级联；`--gym off` 一项即可关闭记录与反馈。没有模型自动重试或连接 failover。

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

管理不启动控制器、不创建模型日志、不执行 pending 恢复。`--branches`、反馈查询、导出保持现有源存档只读保证；fork 保持显式写入语义，不把所有管理命令称为只读。管理仍拒绝混入控制方式、模型预算、内容等运行专用 CLI 参数；home 中合法但未使用的运行设置不触发运行行为。`--help`、init、管理、运行分别进入对应路径，不因 home.control=llm 被引导进运行路径。

内存运行用于开发演练；`--ephemeral` 仍从 home 取得宿主配置，但 world 的 store=null，不能配合 branch 或依赖存档的管理/Gym。LLM home 已配置 Gym 时需显式 `--gym off` 再使用 ephemeral，不静默丢弃用户要求的记录。内存 LLM 的默认独立审计放在 `home/llm-runs/<run-id>.jsonl`，可用 `--llm-log` 覆盖；ephemeral 只表示世界不落盘，并非没有任何文件写入。Human 不生成模型日志。

程序调用 `PlayConsole.RunAsync(null, ...)` 等继续工作，无须制造 home；CLI 正常运行只保留一种 home 解析入口，包括 ephemeral，不增加独立裸参数运行模式。显式 store 的管理入口只是已有工具所需的存档定位，不扩张成另一种运行宿主。

## 5. 软件实现边界

实现局限于 Playground.Cli。连接解析/池仍在 CharacterControl.Completion；世界、Kernel、Spatial、DurableGraph 和 Atelia.Completion 不引入 home 概念。home 不需要持久对象、schema、ID 或注册表。

建议从 `Options.Parse` 拆出两个小职责：显式命令行解析；home 文件读取与生效选项解析。使用普通内部 record/函数即可，不预设配置接口、provider 链或依赖注入框架。所有默认值集中一处，输出一个不可变生效选项交给现有宿主，不让 PlayConsole/LlmConsole 各自重读文件或各自合并。

先识别命令及显式参数：help 不读文件；init 只创建标识；管理按第 4 节定位 store 后直接进入原管理函数；正常运行才定位 home、合并设置、规范化路径并进入宿主。init 仅接受 `--home`，管理保留各操作的参数白名单并允许 `--home`；不把运行设置的默认值伪装成显式管理参数。

正常运行的新局先在内存构造 initial，使用同一个 initial 校验已知角色与预算后再 `PlaygroundDurableHistory.Create`，不重复创建世界、不在报错后删除存档回滚。已有 store 的一致性验证、pending 恢复和决策顺序保持原合同。凭据只在实际首次模型调用才解析。

这里包含一项有证据的相邻技术债：当前 `PlayConsole.RunAsync` 和 `LlmConsole.RunAsync` 都可能先创建 store，再调用 `WorldHost.RequireBudget`；新目录配 `--until-hour 0`，初始时间 08:00，会先留下存档再拒绝。这是源码顺序推导，本轮未运行失败实验。实施时把同一预算校验前移到创建边界；保证仅限静态配置及创建前可知的新局角色/预算错误不创建 store。I/O、凭据、日志写入或远端调用失败不承诺零残留，不扩展成启动事务。

运行启动摘要打印实际 home/store/branch、控制绑定、预算、Gym 模式、所选配置路径和日志位置，不打印连接配置全文或凭据。实际模型调用仍以原有 trace 身份为准；不增加完整配置快照日志或第二份恢复权威。管理命令保留当前机器可读输出，不向 `--branches` 的 JSONL stdout 前插普通文字，也不额外产生成功 stderr；直接 store 的定位规则写入帮助和文档。

## 6. 最小实施切片与验收

一次纵向切片完成 home 读取、CLI 合并、init、运行与管理接线、创建前预算校验、示例和帮助更新。不先发布通用配置库。旧 `--llm`、无值 `--gym`、独立 `--feedback` 删除，更新当前脚本/README/参数测试，不重写历史实验命令。现有 store 可用 `--store` 路径覆盖接入 home，不迁移存档格式或强制搬动目录。更新 smoke 时，管理可继续直接指定 store；运行子进程需要显式 home 和新 Gym/控制参数。

验收重点：

1. init 不调用模型、不创建 store、不覆盖现有文件；任意 CWD 的 help 不读 home；缺失/坏格式 home 清楚报错。
2. 显式 home 与 home 内 CWD 启动选中同一实例；父目录标识不被搜索；两种相对路径基准、不同参数顺序与带空格路径均正确。
3. CLI 只覆盖显式项目，Gym 整项关闭、Human/角色覆盖清除有效；未知/重复字段失败；未引用同名角色文件不生效，已引用缺失必须失败；运行中修改文件不改变生效配置。
4. 默认 Human、全模型、混合控制、单参数临时切回 Human；未使用的连接不要求凭据；共享预算和每次调用身份保持。Human 与 LLM 新局分别验证 `--until-hour 0`：无新 store、无日志、无调用；已知错误角色同样在创建前拒绝。
5. 同一 home 两次启动续接世界；保存内容权威与显式 CLI 断言保持；默认 home 可以打开 gentle/单角色世界；内存模式不产生 store，模型日志落 home 而非任意 CWD。
6. LLM home 上的管理查询不接触凭据或创建日志；显式 store 在缺失/损坏 home 时仍可管理；查询/导出前后源字节不变，JSONL 输出可按原脚本读取；fork 仍显式注册分支，多分支续跑必须选择。
7. 现有 pending、角色隔离、实际协议身份、Gym 分叉与导出测试回归。用 fixture 覆盖路由；真实模型调用不是配置解析正确性的前提，实施时按变更风险决定是否补一次小预算冒烟。

## 7. 暂缓与重开条件

- 全局配置、profile 继承、自动发现父目录：多个真实实例出现无法由文件路径共享解决的重复配置需求后重评。
- 热加载、运行中切模型：需要不中断角色决策的具体产品故事后重评。
- 多世界清单、home 管理器与自动分支选择：出现频繁管理多个世界的实际操作证据后重评。
- `newGame` 模板：出现反复创建相同非默认世界的消费者后重评；一次性新局参数和已有存档目前足够。
- init 自动复制连接目录：需要脱离源码包的开箱模型接入时重评；当前仅使用现有示例，不维护第二份 provider 模板。
- 可移植实验包、全量配置快照、凭据搬迁：独立交付需求出现后设计；当前目录组织不承诺封装外部依赖或旧日志中的绝对路径。
- 多进程写同一存档：沿用现有存档约束，不新增 home 锁协议或守护进程。

## 8. 辩证简化记录

2026-11-02 使用 `dialectical-simplification`：需求质疑者、最小架构师、语义守护者各自完整阅读初稿与当前源码后独立提出论点，再交换最强反例交叉质询。主线程独立核对生产代码、ContentHostTests、GymCommandsTests、MultiConnectionHostTests 和 smoke 消费者后裁决；审阅者全程只读。未运行测试或真实模型调用，不把设计检查称为实现验收。

| 裁决 | 初稿机制 | 最终机制及不能丢失的行为 |
| --- | --- | --- |
| merge | Gym 与 feedback 两个布尔、非法组合及关闭联动 | 一个三态 Gym 模式；保留当前 feedback 蕴含记录的真实语义 |
| simplify | 自动探测可选覆盖、缺文件回退、禁用探测 | 可空显式路径；防止仅因文件增删无声改变角色模型 |
| defer | newGame 对象及来源不同的续局解释 | 仅一次性 CLI 选择；保存定义与显式续局断言继续有效 |
| simplify | init 默认写两文件与部分完成合同 | 只创建 manifest；首次模型接入使用现有连接示例 |
| keep | home/CWD 与 CLI 两种相对路径基准 | 调用位置与配置位置不同是真实场景；一次规范化，不修改 CWD |
| keep | 管理与运行分流、直接 store 管理 | 复用 GymCommands；不能因模型配置坏了就执行恢复或阻断独立存档检查 |
| simplify | 笼统“配置失败无残留” | 先校验已知新局约束再发布；不承诺 I/O/远端错误全回滚 |
| delete | 为过渡保留旧控制/Gym 拼写的潜在适配层 | 同时更新当前消费者；历史证据不改写，存档格式不变 |

审阅中存在两项可接受的便利性取舍，没有伪称全体一致：一位审阅者最终偏好 init 同时复制连接示例，并接受所有管理均要求 home。主线程选取单文件 init，因为初次 LLM 配置可用既有样例完成；保留显式 store 管理，因为已知 store 可直接交给现有管理函数，不需要第二套运行实现。允许管理同时传 home/store、由显式 store 胜出，避免再增加一条专用互斥规则。这些是本方案建议，不冒充用户对细节的单独裁决；没有需要等待用户才能继续写方案的悬项。

可观察的缩减：两个 Gym 设置合为一个；删除一个嵌套 newGame 对象及其来源分流；删除可选文件探测路径；init 从两次文件创建减为一次。保留的每个分流都对应现有调用或明确失败轨迹，不提取通用 home 框架。下一步是第 6 节的单个实施切片，本轮文档不代表已获代码实施指令。
