# Human Console 元命令边界重构

状态：已实施。2026-10-28 在增量 UI 切片 A 基础上分离纯行动解析、共享游戏视图与 Human Console 管理能力；原切片验证范围见[实施验收](../experiments/console-command-boundary-acceptance.md)。后续[角色上下文重构](character-context-model.md)已将行动 parser 移至 Playground，控制器返回原始 submission；完整角色材料改由持久 Context 同源渲染。§2 与 §5 保留原切片范围和验证意图，其余现行职责以下述源码为准。

## 1. 目标与现状

元命令（Console meta command）管理真人的 Console 会话；角色行动（Command）表达角色在世界中要做什么。未来 LLM 可以共享游戏观察、事件和 Markdown 排版，但不应因复用文本而获得 Console 的退出、布局切换等能力。

当前职责边界如下：

| 现有入口 | 当前职责 |
| --- | --- |
| [PlaygroundCommand / IPlaygroundCharacterController](../../src/Playground/PlaygroundActions.cs) | 领域命令没有 Exit、Layout 等；控制器返回带原始行动文本与操作身份的 submission，由游戏解析、校验并提交成功或拒绝。 |
| [PlaygroundCommandParser](../../src/Playground/PlaygroundCommandParser.cs) / [ConsoleMetaCommands](../../src/Playground.Cli/ConsoleMetaCommands.cs) | 前者位于共享应用层，只解析游戏行动；后者解析 Console 冒号元命令，目录同时控制提示与解析。 |
| [ConsoleCharacterController](../../src/Playground.Cli/PlayConsole.cs) | 唯一元命令执行入口；`:exit` 配对日志结果后抛出 Console 专用异常，外层 Console 运行循环捕获并返回。没有调用 `Environment.Exit`。EOF 也结束 Console。 |
| [PlayPresentation](../../src/Playground.Cli/PlayPresentation.cs) / [PlayPresentationSession](../../src/Playground.Cli/PlayPresentationSession.cs) | 前者保存布局/模式偏好及实际选项，后者组合 Console 带外信息、上下文标记和输入提示，并管理临时条目消费游标。 |
| [CharacterContextRenderer](../../src/Playground.Cli/CharacterContextRenderer.cs)、[IPlayLayout](../../src/Playground.Cli/IPlayLayout.cs)、[PlayView](../../src/Playground.Cli/PlayView.cs)、[ContextHistoryPlayView](../../src/Playground.Cli/ContextHistoryPlayView.cs) | 从 Opening 和当前保留 Items 生成完整角色材料，另提供最新状态预览与行动帮助；不生成元命令或宿主交互指引。 |

独立 LLM 控制器通过原始行动 submission 接口接入时，`:exit` 只会成为游戏语法拒绝，不会退出进程。将模型文本直接接入 `HumanDefault` Console 输入流则会拥有整套 Console 能力。此边界避免共享解析器和视图夹带管理能力，不建设同进程恶意代码隔离。

## 2. 原切片范围和不变量

- 只调整 Playground.Cli 的输入、提示与会话组合，以及相关测试和文档；保留两个场景、两种布局和 full/delta 行为。
- 领域观察、`PlaygroundCommand`、控制器返回封套、E/S、ToolCallId 与恢复语义不变；无需修改 Schema 或 GameRevision。
- 本轮不接入 LLM、不提取通用 Controller/工具框架、不新增包、不实现角色控制器路由或通用宿主生命周期协议。
- 不以终端、重定向、输出是否为 Markdown 判断能力。人可以用重定向脚本试玩；Console 也可以用于旁观 LLM，输出介质不代表控制权限。
- `handoff` 修改角色交接状态，仍属共享游戏行动；不因零耗时移入元命令。LLM 结束 tool-loop 不等于退出 Console 或停止进程，具体 LLM 工具合同留到接入时设计。
- 当前元命令仅允许冒号前缀；不恢复无冒号别名。输入大小写、空白、数值和 handoff 正文规则保留。

## 3. 最小职责划分

| 组件 | 重构后的职责 | 明确不承担 |
| --- | --- | --- |
| `PlaygroundCommandParser` | 纯文本行动解析，产出 `PlaygroundCommand` 或语法失败（`null`） | 元命令、退出、日志、世界合法性检查 |
| `ConsoleMetaCommands` | 有限内建元命令目录、解析，以及同源帮助文字 | 世界规则、持久状态、通用插件加载 |
| `ConsoleCharacterController` | 接收并登记输入，路由元命令；可查询行动风险，但只返回原始行动 submission，由游戏裁决 | 把 Console 管理操作提交给领域 |
| `IPlayLayout` 与游戏视图 | 只读地组织观察、事件、效果、行动条件和游戏规则 | 提示真人输入、宿主会话关闭、退出、元命令广告 |
| `PlayPresentationSession` | 组合带外回复、提示符、上下文开始/结束标记及消费游标 | 根据输出位置猜测控制器类型 |
| `PlayPresentation` | 布局/模式偏好、可选布局和模式数据 | 维护另一份独立的元命令语法与启用开关 |

纯行动解析器位于 Playground；Console 目录、呈现和宿主副作用仍在 CLI 程序集内。后续按第二个消费者的需要决定是否提取共享呈现程序集。

### 3.1 解析与输入路由

每次读取沿用现有顺序：取消检查 → 读取 → 再次取消检查 → EOF/空白处理 → 保存 Received 与隐式 ToolCallId → 解析/执行或拒绝 → 配对 Result。保留读取后的取消检查，避免等待期间已取消却继续登记和分派刚读到的命令；这不构成任意时刻取消与日志写入的原子保证。行动合法性由引擎使用角色限定的 `context.Check` 判断；Console 不预先截获游戏结果；行动风险来自保存的角色材料。

去除前导空白后，以 `:` 开头的输入只交给元命令目录；未知、禁用或参数错误均在 Console 输入循环内拒绝，不回退为游戏行动。其余输入原样返回共享游戏入口，由行动解析器处理。`handoff :exit` 的冒号属于正文，必须保存为交接文字，不能再次分派。

目录返回有限的 `ConsoleMetaCommand`（命令种类和参数），执行继续使用 Controller 内的明确分派；不为每条命令创建 handler 接口、服务容器或可加载插件。原 `PlayInput` 混合结果已拆除，不保留第二条可绕过目录执行元命令的路径。

### 3.2 运行时配置：显式组合，不向布局传播布尔量

目录提供 `HumanDefault` 和 `None` 两个不可变配置：

- 正常 `--play` / `--survival` 由 Console 组合入口明确选用 `HumanDefault`，现有真人体验保持。
- 构造会话/Controller 时可由内部参数传入 `None`：不提示、不解析为合法元命令、不执行任何元命令；`:exit` 与 `:quit` 都只是被拒绝的输入。
- Controller 分派和 Console 提示使用同一份目录实例；不设置彼此独立的 `ShowMetaHelp` 与 `AllowMetaCommands`。
- 配置在会话构造时确定；这已经满足运行时依赖注入需求。本轮不实现会话中途热切换、任意子集或新的 CLI 启动参数；出现实际用途时再增补。
- `None` 只关闭元命令，不把 Human Controller 变成 LLM Controller：EOF、Ctrl+C 和宿主取消仍是宿主生命周期行为。未来 LLM 不复用 Console 读取循环或其 EOF 约定。

目录的每条内建定义含规范名称、别名、参数形状和说明，解析与帮助引用同一条定义。`:quit` 是 `:exit` 的别名，关闭时一起关闭；目录内有限解析分支即可，不建立通用语法 DSL。

`:layout` 的合法名称来自实际布局集合，`:mode` 的合法值来自场景实际支持的模式。`--play` 只列出 full 为合法值，可以说明 delta 暂不支持，但不能把它列为可执行选项。列出、切换、帮助和错误提示使用同一数据来源。

### 3.3 文档内容与 Console 附加内容

游戏视图继续包含身份、当前/历史状态、警告、行动语法、受阻原因、已知风险及交接规则。它们可以直接用于后续 Doc UI。行动帮助与布局可以继续有各自的排版；无需合并为第二套文档树。

从共享视图移走了 `:help` / `:look` 等提示、退出与 EOF 说明、布局/模式管理指令、输入提示符。`IPlayLayout.RenderPrompt` 已删除，提示符由 Console 会话生成；原 `RenderHelp` 改为游戏行动/规则帮助 `RenderActionHelp`。`PlayView.RenderCommands` 同样只保留行动和规则内容。输入提示与会话关闭等宿主说明由 Console 会话组合，不扩充领域观察来携带 UI 能力。

Console 根据目录生成开始标记之前的元命令提示、`:help` 的管理部分和元命令拒绝后的指引。启动信息、管理回复、最新状态预览及会话关闭说明均包在带外块中；输入提示符明确标为 Console 输入。`None` 下不出现不存在能力的提示。即时反馈不显示“正在处理结算”，输入机会只由进入控制器读取循环决定。

目录定义会话能力，不代表此刻可以输入。终局 Context 只呈现带开始/结束标记的冻结材料，不追加可交互指引；EOF/exit 只输出带外会话关闭，不能伪造领域 Context 已放行。Closed 之后的世界结算不进入旧正文；按可见性记录的经历在下一 Opening 首次交付。开发者诊断使用独立的 [PlaygroundContextDiagnostics](../../src/Playground.Cli/PlaygroundContextDiagnostics.cs) 提交后回调，不是角色正文的备用输入。

`:help` 组合元命令目录、实际布局/模式选项和当前布局的行动帮助。`:layout <名称> once`、`:look` 及布局/模式切换只预览 Context 的最新保存观察，不重新观察世界、不推进条目游标、不重放全部 Items，也不新增上下文边界。完整纯上下文由 `CharacterContextRenderer.RenderFull` 另行提供：Opening + 当前保留 Items，带范围标记，无宿主管理内容、输入回显或诊断。普通终端的带外回复与已裁剪拒绝仍可留在滚动历史中，不能用滚动文本替代 Model。

这里只排除程序注入的 Console 操作指引。角色名称、地点描述、handoff 文字等数据可以合法包含 `:exit`；原始输入与交接文字按数据引用并转义换行/控制字符，保留内容语义，不得用全局字符串替换清洗 Markdown，也不能用“整份输出不得包含冒号命令子串”作为验收。

目标管理的[待实现操作](goal-management/operations.md)使用普通游戏提交而非冒号元命令，`show-goal` 也记录交互 E/S；它与本地 `:look` 的副作用合同不同。其[专属材料投影](goal-management/capacity-and-context.md)会用短回执替代长编辑原文的重复展示，但原始输入/身份仍保存。上文“无输入回显”排除的是宿主终端回显，不禁止角色材料按规定显示操作记录。

## 4. 副作用与交付合同

元命令执行保持现有有限副作用：

| 类别 | 保留行为 |
| --- | --- |
| `:help` / `:layout <名称> once` / 列出布局和模式 | 返回独立查询答复，不改领域状态或条目消费游标 |
| `:look` / 成功切换布局或模式 | 在带外块中预览最新保存状态，不新增上下文边界，也不改变条目消费游标 |
| 元命令拒绝、未知选项、禁用命令 | 配对拒绝结果，留在当前输入循环，不推进时间、不确认消息、不改变会话偏好或消费游标 |
| `:exit` / `:quit` | 先保存配对答复，再结束当前 Console 运行；由外层所有者返回，不在解析器、布局或领域调用进程退出 |
| EOF / 纯空白 | EOF 结束 Console，纯空白继续读取；二者均不虚构操作 ID |

每条非空提交只生成一次身份，包含查询、拒绝与元命令。成功及拒绝的行动都沿 E/S 提交路径完成结果；本地元命令由 Controller 完成，不重复完成。重构不能把日志接收移到解析成功之后，也不能新增恢复时重做动作的逻辑。日志写出失败沿用现有失败处理，不在本轮重建日志与 UI 的事务协议。

事件顺序、真实效果与去重当前值由同一已提交 Item 提供。`PlayPresentationSession` 只在 writer/Flush 成功后推进消费游标；输出失败停止宿主，不重新执行已提交动作。完整重绘不依赖该游标，任何时候都可由保存的 Context 生成。装饰性提示不进入 `PlaygroundViewSnapshot` 或 `PlaygroundViewUpdate` 的游戏语义键。

最新状态预览先组合答复正文，再通过 `ShowCurrent` 的 `beforeWrite` 回调记录操作结果，最后由 `Reply` 添加带外标记并写出。日志记录的是答复正文，不包含宿主包围标记；日志先于写出不保证用户实际看见。操作日志与游戏 E/S 各有提交边界，冷开补录不重做动作。

## 5. 实施分片与验证合同

1. 提取纯行动解析器和有限元命令目录，迁移现有解析测试；保持原行动语法和冒号唯一形式。
2. Controller 与会话显式共享目录，落实 `HumanDefault` / `None`，保留日志和结束行为。
3. 移走布局、完整观察、交接视图中的 Console 指引；收敛帮助和提示符职责，从真实注册数据生成目录提示。同步调整现有调用点和测试布局，不保留无意义的兼容包装。
4. 更新[布局合同](console-layouts.md)、[控制器边界](stateless-character-controller.md)和术语映射，以代码事实标记方案完成。实际运行两个场景，验证原 UI 与禁用路径。

必要的行为验收：

- 两个解析器职责分明：行动解析器不能产生任何元命令；冒号形式不回退为行动；非法参数、无冒号别名、大小写规则保留。`handoff :exit` 和带冒号的普通正文无副作用。
- `HumanDefault` 覆盖全部既有元命令及 `:quit` 别名；帮助中每个承诺的名称/模式都能被识别，当前不支持的值不会被列为合法值。
- `None` 的集成测试覆盖全部内建名称、别名及带合法参数的切换形式，包括 `:layout classic` / `:mode full`（选择与初始偏好不同的值），后跟一个合法行动；确认循环继续、每条拒绝同 ID 配对、布局/模式与消费游标未因禁用输入改变、最终返回后续的行动 submission，领域层另行校验。不要以随后的 EOF 退出误判禁用失败。
- 用无 Console 指令的数据夹具分别渲染 classic/decision、全量/增量、帮助、交接和终局内容，确认没有程序生成的元命令提示；另用带 `:exit` 正文的夹具确认作为数据呈现而不执行。
- Console 启用/禁用提示与解析同源；正常模式的帮助仍可发现全部 layout 名称与适用模式。只检验能力与必要语义，不断言章节位置、整屏快照或精确措辞。
- 覆盖终局提交与冷开终局，确认只输出一次冻结记录且没有可继续交互的元命令提示；保留读取后取消检查的行为覆盖。
- 维持既有查询不改消费游标、预览不新增上下文边界、写出失败不推进消费游标、退出不发布 E/S、接收失败不分派等关键边界。复用或调整现有测试，不机械复制。
- 工程验证运行 CLI 相关测试及受影响的操作恢复测试，并完成 solution 构建；纯 CLI 重构不以重跑全部领域测试为默认要求。若实际修改超出此边界，按影响范围补充。
- 用真实 CLI 试玩 `--survival` 的帮助、切换、查询、handoff 和退出，再抽查 `--play` 的 full-only 帮助；`None` 无产品入口，使用集成测试驱动相同 Controller 即可，不为试玩另造启动参数。独立试玩证据归入实施验收。

## 6. 与后续工作的关系

本重构可独立于[增量 UI B](incremental-ui-milestone.md)实施；本身不决定跨回合事件窗口；当前经历身份、交付和恢复合同由角色上下文方案承载。未来 LLM 通过同一原始行动 submission 入口提交，需要只读查询时复用游戏内容并定义自己的工具入口，不获得 Console 目录；不把禁用元命令的 Console Controller 当作完成 LLM 适配。

本轮不要求通用 `IHumanInterface`、`ICommandRegistry`、`IPresentationHost` 或宿主控制命令协议。只有第二种实际交互实现需要共享依赖，或产品提出部分命令开放/中途切换时，才重新评估这些扩展。
