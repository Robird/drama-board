# 决策回合的信息继承：设计与重构方案

状态：保留 2026-10-28 信息继承切片的设计与验收依据（当时 GameRevision 5）。2026-10-29 起，原固定 Continuity/Incoming 已由[角色上下文 Model](character-context-model.md)的同根交互状态和经历材料替换；本文旧模型形状、源码名称及施工步骤不再表示当前 API。放行结束旧回合、handoff 一次性交付与原 ToolCallId 配对仍沿用；拒绝操作已改为持久 Context 条目并提交 E/S，本文“不提交拒绝”的旧合同不再适用。原切片的[设计审查](../research/decision-context-handoff-review.md)和[实施验收](../experiments/decision-context-handoff-acceptance.md)保留历史证据；LLM、端点 ID 映射及完整工具会话恢复仍未实施。

后续方向：[增量 UI 目标](incremental-ui-milestone.md)的切片 A 已扩展回合内事件/效果呈现；包含零耗时事件的有界最近历史仍待 B 实施。本文件保留本切片已落地的有限继承范围，新目标不改变放行后结束旧控制会话的流程。

## 1. 目标与裁决

Console 展示更新（2026-10-29）：本文原定的清屏已替换为带角色/ContextSequence 的“LLM 上下文开始/结束”标记，保留滚动历史；重定向使用纯文本标记。启动说明与元命令位于开始之前，中途管理回复明确带外；即时反馈从已提交 Items 追加，放行结算只进入下一 Opening。完整纯上下文与最新状态预览分开，`:look` 不重建消费游标。本文以下清屏和旧呈现步骤保留当时设计语境。当前展示合同见[Console 布局](console-layouts.md)，领域回合与信息继承语义不变。

每次唤醒建立新的角色决策上下文，以“当前观察 + 游戏记录的上次行动及结果 + 可选的角色交接文字”保持连续性。产生时间推进的旧会话在放行模拟时结束，不等待看到结果，也不提供动作后收尾阶段。新会话可以先反思上次行动，再执行零耗时操作或选择下一次行动。

决策回合（Decision turn）是角色局部交互上下文的生命周期，不是 Kernel 的时间单位、一次控制器方法调用或一项 Activity 的生命周期。一个回合可以有多次零耗时领域提交；同一活动可以跨多个回合。角色交接文字（Handoff text）保存意图、预期、假设和待办，不是游戏确认的事实，也不自动升级为长期记忆。

本切片要验证少量显式信息能否支撑连续决策，不承诺它与保留完整旧会话等效。先用 Console 验证机械流程；接入 LLM 后再比较“仅当前观察 / 加结构化结果 / 再加交接文字”。

每次控制器产生的操作都在入口固定一个工具调用标识（ToolCallId），对应结果引用同一个标识。LLM 来源保留 Completion 交付的 ID，Console 来源隐式生成；端点兼容格式的稳定双向映射归兄弟项目 Completion，不由 DramaBoard 处理。该合同覆盖零耗时操作，不限于上次放行行动。

明确不做：动作前/等待/动作后三阶段会话、显式 `:end`、旧会话续接、完整 LLM 会话/推理块恢复、LLM 端点接入与格式映射、长期笔记检索、增量文本渲染、Agent 全轨迹导出及其可靠投递框架。§4.4 的最小操作记录是此次 ID 保存要求新增的范围，不等于完整会话持久化；游戏效果仍只由 E/S 决定。

## 2. 重构前的实现依据

下表保留施工前的基线与改动原因；当前入口与已落地边界见 §11，不将旧 API 形状当作当前实现。

| 当前入口 | 事实与改动原因 |
| --- | --- |
| [CycleActions](../../src/ResourceCycle/CycleActions.cs) | `ChooseCommandAsync` 返回一条 `CycleCommand`；`NeedsDecision` 依据活动、移动和待确认感知，不存在回合对象。 |
| [CyclePlanning](../../src/ResourceCycle/CyclePlanning.cs) | `go / wait / gather` 在当前模型时刻接受并启动过程，时间由后续发生项推进；`use / cancel` 是即时效果。 |
| [CycleEngine](../../src/ResourceCycle/CycleEngine.cs) | 请求决策在 Plan 中调用控制器；同刻自然事件和活动结算先于输入，已有按角色轮转规则。 |
| [CycleCharacter](../../src/ResourceCycle/CycleCharacter.cs) | `DecisionGeneration` 是预测请求版本；`LastResult` 是会被后续操作覆盖的文本，不能承担稳定的跨回合因果记录。 |
| [CycleObservation](../../src/ResourceCycle/CycleObservation.cs) | 运行时角色观察和纯命令校验，不公开完整世界；目前没有结构化的上次行动结果包。 |
| [PlayConsole](../../src/ResourceCycle.Cli/PlayConsole.cs) | 按观察时间变化或切换角色清屏；首次展示不清屏；这只是现有启发式，不能代表新回合身份。 |
| [CycleDurableHistory](../../src/ResourceCycle/Persistence/CycleDurableHistory.cs) | 单根 E/S 发布，pending E 恢复不重做 Plan 或控制器；本方案沿用该路径。 |

## 3. 交互合同

### 3.1 回合开始与结束

1. 新游戏为角色创建第一个决策上下文标识；首次展示清屏并全量输出 Doc UI，没有上次结果。
2. 查询、非法输入、布局切换留在当前回合，不提交世界效果；仍以 §4.4 的操作记录保存自己的 ID 与结果。合法零耗时游戏操作按原路径提交，然后在同一回合追加结果与全量观察。
3. 接受“放行模拟”的行动时，随行动事实记录交接数据，旧回合结束。此时不清屏；提交成功后才把运行时局部会话视为已结束。
4. 模拟按既有规则推进；到达下一次可服务的决策边界，游戏封存角色可知的行动结果，开启新上下文。Console 此时清屏，展示当前观察和继承信息。
5. EOF、`:exit`、进程中断不是游戏行动，不隐式等待、确认警告、结束回合或消耗补给。重新打开从已提交领域边界恢复；不承诺恢复未提交输入和临时对话。

新上下文不要求模型时间变大：同刻的后续感知也可能形成新决策边界。清屏依据显式上下文身份，不比较时间。切换角色可为隐私重绘清屏，但不增加角色回合编号；重定向输出继续保留全文。

### 3.2 放行模拟与零耗时命令

不能用命令提交前后的时间差分类：所有命令接受本身都发生在当前时刻。沿用业务语义，不添加第三种“结束回合但不行动”的操作。

| 操作 | 回合效果 |
| --- | --- |
| 合法 `go / wait / gather` | 记录具体接受的行动，结束当前回合并放行模拟。 |
| `continue` | 若确认后角色仍有持续活动/移动且不再需要决策，视为恢复模拟，结束回合；若角色空闲、只是确认身体警告，留在当前回合。 |
| `use / cancel` | 留在当前回合；不自动放行模拟，不覆盖继承的上次结果。 |
| 新增交接文字编辑 | 零耗时领域操作，留在当前回合。 |
| 元命令、拒绝的命令 | 不改变回合和领域状态。 |

分类在行动规划并完成生存规则补全后由应用层确定；控制器和布局不各自复制规则。第一片保留 `continue` 既有警告确认语义，不为了分类另加 `ack`。新行动仍受已有互斥、移动不可取消和终局冻结约束。

活动未完成的唤醒必须明确写成“本段推进因警告/感知返回，活动仍在进行”；不能把 `continue` 伪装成重新创建活动或改变原到期时刻。

### 3.3 示例

```text
回合 1，15:00：清屏 → 当前观察
  handoff 抵达后先检查保暖，溪谷路线尚未排除。
  use item.water       → 同轮追加完整观察
  go passage.xxx       → 保存行动及交接文字，回合 1 结束
模拟：天气、需求结算；16:00 身体警告
回合 2，16:00：清屏
  当前观察 + 上次沿路出发、途中警告返回、仍在途中 + 交接文字
  use item.food        → 同轮追加；上次结果保持不变
  continue             → 保存本次继续活动，回合 2 结束
模拟：抵达
回合 3：清屏 → 当前观察 + 继续活动后抵达的结果
```

## 4. 最小持久状态与信息内容

### 4.1 数据归属

每角色保存一个 `CycleDecisionContinuity`，挂在 `CycleCharacter` 下，随现有 Business 根发布；不另建领域存储、不修改 Kernel 时间模型。形状：

```text
CycleDecisionContinuity
  ContextSequence             当前/最近一次上下文的角色内递增编号
  Incoming                    当前回合固定继承的上次结果，可空
  DraftHandoff                本回合准备交给下次的文字，可空
  Pending                     已放行但尚未交付下次上下文的行动记录，可空
```

约束：实际调用控制器交付观察时 Pending 为空；已结束旧回合、等待下次交付时 Pending 非空。`NeedsDecision == true && Pending != null` 是合法的中间持久状态，表示已有唤醒条件、但尚未执行交付边界。只表示一次交接是否交付，不保存旧会话执行栈或动作后阶段。`ContextSequence` 不复用 `DecisionGeneration`、Activity ID、世界版本或端点调用 ID。以 `(ActorId, ContextSequence)` 识别上下文；存档外的日志另加存档身份。

`Incoming` 是封存结果；本轮补给、取消、编辑交接文字都不能重算或覆盖它。接受下一次放行行动后旧 Incoming 从当前图移除，以新 Pending 替换；不存在跨回合无限增长的链表。活动自身继续保存在原领域模型。

Pending/Incoming 都记录源上下文编号、角色身份与放行操作的 ToolCallId/操作记录引用，便于验收关联和避免错误串用；新游戏编号从 1 开始。它们只保留相关来源，不复制整个操作记录流。实现时所有 `CycleCharacter` 构造、Change、WithSurvival、Notify 路径必须保留或显式替换连续性对象，遵守现有不可变快照约定，不能在已发布对象上就地编辑草稿或累计字段。

### 4.2 Pending 与封存结果

保存具体接受的行动描述，不持久化完整控制器请求对象或 Markdown：行动种类、实际参数、角色、接受时刻、接受时可知的位置和活动关联键；`gather all` 记录已冻结的目标数量，`continue` 关联既有等待/采集 ID 或移动段身份。不能用接受以后才揭晓的目的地名字回填旧时刻的知识。

结果包包含：

- 行动记录及接受时的有限基线：身体需求值、背包物品数量、已知位置与活动进度。
- 本段期间固定白名单的结构化汇总：自身采集所得、已感知天气次数及实际保暖损失、昼夜变化次数；不保存逐事件列表、整个 `CycleFact`、角色对象或 World 引用。
- 交付时刻、唤醒原因集合（允许同刻多个原因）、交付时身体/背包/位置与活动进度，以及活动仍在进行/已结束/终局状态。
- 提交行动时附带的交接文字，明确标记为角色的主观记录。

身体/背包差值表达“这段时间的净变化”，不声称所有变化都是所选行动单独造成。原因只从可见的已记录事实得到，不从文本、净差值或未来预测反推。需要解释寒风时，保留已感知天气事件的效果；不能只剩前后两个数值。

摘要采用确定性规则，不调用 LLM。第一版只实现下表的固定字段；不建设近期事件队列、省略计数、可配置订阅或通用日志摘要器。采集只累计本段关联活动的物品与数量；天气分类使用当前有限枚举。计数/累计量使用非负 long，溢出饱和并附 `IsSaturated`，展示为下界；身份编号使用 checked 递增、达到上限拒绝新的放行行动，不绕回重复。身体端点保存原领域精度。固定类别及有界背包键确保数据不随运行时长线性增长；新玩法需要新信息时再显式扩充。

首片收集白名单如下。读取顺序为行动/自然规划 → 生存补全 → 地点观察 → 连续性补全；连续性补全读取最终具体效果和规划分支已知的原因，写入同一事实。这里没有现成的通用“角色事件过滤器”，不把 `CyclePlanning.Observe` 当成它。

| 事实/分支 | 可写入的角色及摘要 | 是否形成返回原因 |
| --- | --- | --- |
| 自身 `GatherYielded` | 该角色关联活动的累计物品所得 | 仅最终 Activity 为空时记录目标完成或不能继续安排的结束原因；中间产出本身不新增唤醒，若耗尽资源并产生既有通知，仍按通知规则唤醒。 |
| 自身 `GatherFinished` | 记录本次活动结束及规划时的细因 | `ActivityStopped`，细因见下文。 |
| 自身 `WaitCompleted / MoveArrived` | 等待完成或已知抵达信息、关联活动结果 | `ActivityCompleted / Arrival`；到出口另含终局。 |
| `SurvivalWarning` | 仅对应角色的警告指标集合 | `SurvivalWarning`；同刻可合并多个指标。 |
| `WeatherResolved` | 仅该事实对应角色的天气次数、实际已结算的保暖损失；庇护免损失如实记零 | 普通天气本身不唤醒；若死亡则含 `Terminal`。 |
| `LightChanged` | 已启用且原事实已替换身体状态的角色，记录昼夜变化次数 | 本身不唤醒；后续身体警告另记。 |
| 已记录给本角色的 `CycleNotice` | 仅原事实已通知的角色，收集 `CollectorJoined / ResourceDepleted` 原因；不镜像他人私有行动 | 按现有 NeedsDecision 语义唤醒；保留现有通知的去重身份。 |
| `SurvivalFailed` 或逃离/死亡结果 | 终局状态与已记录原因 | `Terminal`，封存结果，不再开可行动回合。 |
| 资源再生、远处库存及其他不在表中的变化 | 不收录过程摘要；当前可见状态仍由正常观察显示 | 不新增唤醒。 |

收集只在 Pending 非空时进行；不反向修改 Incoming。对 `GatherFinished` 的资源空、物品数量上限、容量上限，以及成功产出后因时间无法表示而停止，现有 `Result` 文本不足以提供类型化细因。实施时在 `CompleteGather` 对应分支直接写有限原因值（`TargetReached / ResourceEmpty / ItemLimit / CapacityLimit / TimeLimit`）到连续性结果；无需扩展所有事件为通用原因体系，严禁解析中文 Result。

白名单只附加更新原事实已经合法替换的角色；保持资源再生的零角色效果、单角色事件的数量约束。连续性补全不得新增身体、活动、库存或空间效果。以后若增加观察者摘要，须另行定义附加角色范围，不能宽泛放松 `ValidateFact`。

### 4.3 交接文字

第一片提供 `handoff <文字>` 和 `handoff --clear`，属于角色操作，不带冒号；查看内容由 Doc UI / `:layout <名称> once` 提供。编辑后沿 E/S 保存 DraftHandoff，不能藏在 Console 控制器字段中。上限为 2,000 个 Unicode 标量值，超长拒绝，不静默截断；Console 只接收单行。

接受放行行动时把 DraftHandoff 复制到 Pending 并清空草稿；下一回合默认草稿为空，不自动把收到的文字继续传下去。没有新文字就传空，避免已过期计划永久继承。若要延续由控制器显式重写。Incoming 中的文字是只读历史，不随新草稿修改。

编辑文字不确认警告、不改变活动/身体基线、不触发随机抽样。实现时不能直接套用当前“除 ItemUsed 外所有角色决定确认身体警告”的规则；必须明确将交接编辑排除。文字只是数据，不能当成系统指令、游戏事实或可执行命令。

领域合法性同时要求连续性功能已启用、旅程未结束、角色已有决策机会；途中警告暂停时允许编辑，须扩展当前仅 `Continue / Use` 的途中白名单。功能未启用时在 `CycleActions.Check` 拒绝，不能仅靠布局隐藏。编辑不会让原本正在自动模拟的角色主动获得决策机会。帮助、命令校验与两种布局使用同一启用条件。

长期笔记和召回不是 DraftHandoff 的扩容版本，本切片不提供；以后按独立需求设计。

### 4.4 操作 ID、结果配对与 Completion 边界

#### 已定职责

ToolCallId 是一次控制器操作的关联标识，按不透明字符串处理；不是回合、活动、模型请求、消息块或世界发生项的 ID，也不独自提供幂等执行保证。一次 Activity 可经历 `go / continue / use` 等多次不同 ID 的操作；一个放行操作也可能造成多个后续自然发生项。

- **LLM 来源**：在 Completion 向适配器交付工具调用时捕获 `RawToolCall.ToolCallId`，先记入操作记录，再做参数/领域校验。不能到执行成功或生成结果时再编号。正常端点使用原 ID；若 Completion 已进行了兼容映射，DramaBoard 保存它交付的稳定 ID，不再改写。
- **Console 来源**：每条提交的非空操作输入，在解析和校验前隐式生成一次 ID，例如 `call_` 加无分隔符 GUID；人无需输入，普通界面无需展示。相同文字再次主动输入是新操作，取得新 ID；渲染、重绘、结果构造和恢复同一记录均不重新编号。
- **结果配对**：结果保存/引用原操作的 ToolCallId。当前 Completion 的 `ToolResult.ToolCallId` 就是此引用，不另造一个不同的“tool-result-id”来配对。若端点还有结果消息自身的 item/message ID，那是另一层身份，留给 Completion；不得覆盖关联 ID。
- **Completion 职责**：必要时在捕获端点输出时把端点 ID 映射为兼容 ID，在向目标端点提交历史时稳定映射为该端点格式；同一目标端点和同一段历史不能每次临时换号。映射必须保留足够作用域以消解重号、保持一对一可逆关系，并同时改写 call/result 引用。跨端点映射不保证各端点原始 ID 相同；映射信息的保存与恢复由 Completion 的调用边界负责，不成为游戏 Schema 的端点格式表。

“推理引擎生成”在协议边界上指模型服务交付的调用 ID，不假定一定由模型 token 自行生成。流式分片的临时索引不是最终 ID；适配器以 Completion 确认的调用身份为准，不能把每个参数增量当成新操作。缺失最终 ID 时由 Completion 明确补齐或报协议错误，DramaBoard 不猜测原模型调用身份。

#### 操作封套与记录范围

控制器边界返回 `CycleControllerOperation(ToolCallId, Command, OperationRecordId)`，不将 ID 填进每种玩法命令参数；命令合法性查询仍只读。宿主记录同时携带 ActorId、可选的上下文身份、来源调用批次及原操作内容，用 OperationRecordId 定位具体发生的那一次；ToolCallId 不假定在所有端点、所有会话间全局唯一。调用批次/操作记录身份由宿主生成并保存，不占用游戏随机流，不用裸 ToolCallId 做全局业务主键。

同一调用批次中重复 ID、同一已登记操作引用却改变工具名或参数，作为协议冲突拒绝，不能覆盖旧记录或重新执行。传输重送/恢复已登记操作时沿用原记录和结果；来自一次新的模型生成的调用，哪怕参数相同，也不是旧调用的重送。跨批次重号由宿主记录作用域区分；若要把这些历史拼到同一端点请求，格式与冲突映射由 Completion 处理，DramaBoard 不给旧 ToolCallId 擅加前缀。

所有通过控制器入口提交的操作都登记：合法游戏动作、handoff、查询、被拒绝的操作，以及 Console 的 `:help / :look / :layout / :layout <名称> once / :exit`。纯布局渲染函数、自动广播、天气等自然事件和 ContextOpened 不产生虚构的控制器调用。空行忽略；EOF 是宿主生命周期事件，不伪造人的 tool-call。记录元命令不改变其“不推进模型时间、不确认通知、不改变回合”的语义。

登记/执行顺序：

1. **接收**：固定 ID、操作作用域与原始输入，写最小 `Received` 记录；包括稍后会被拒绝的输入。内存玩法写内存记录；`--store` 模式在解释执行前持久追加到宿主拥有的操作记录流。写入失败不执行该操作。此记录只说明收到了请求，不代表游戏已接受。
2. **执行与领域发布**：合法游戏操作把原 ID 和操作记录引用随具体行动事实写入 E，再发布 S；Pending/Incoming 复制来源关联。Game 侧不引用 Completion 的运行时消息对象。查询/元命令/校验失败只产生操作结果，不为记 ID 而制造游戏发生项、推进 DecisionGeneration 或占用同刻转换预算。
3. **结果**：记录成功、拒绝或中断状态及同一 ToolCallId；游戏操作关联已完成的 E/S 边界，纯查询关联所读世界版本。需要再次投影同一工具消息时使用当时保存的工具名、输入与结果，不用新观察重写旧结果。不要求记录模型推理文本或整屏历史。

这是保存操作身份所需的最小宿主记录流，不把每次纯查询写入角色 Business，也不扩展成完整 LLM 会话日志。`--store` 在存档目录内保存 UTF-8 `operations.jsonl`，通过独占写入、顺序号与 SHA-256 前向校验链验证追加完整性；未以换行结束的尾部可在重开时截断，完整坏行拒绝加载。每条追加 flush 后才返回，不新增多存储原子事务。无存档模式不承诺进程退出后的保留。

操作记录与游戏 E/S 的故障关系必须明确：

- Received 已保存、领域 E 尚未发布：恢复保留原 ID，并标记未执行/中断；首片不自动重跑这条输入，也不假定外部 LLM 会再次生成相同 ID。
- 领域 E 已发布：以现有 pending E 恢复为准，恢复原 ID 和具体效果，不重新调用控制器。不能因操作记录暂缺结果就重做行动。
- S 已完成、操作结果尚未写入：通过行动 E 中的记录引用补出“已提交”状态和记录的语义结果；不能伪造当时尚未保存的逐字 UI 文本。需要精确回灌但缺少原文本时，明确无法恢复该消息，不声称缓存可保持。
- 查询/元命令结果未保存且没有领域 E：保留为中断；不在新世界上重查后冒充原结果。操作记录成功但结果尚未交付控制器时，返回保存的同 ID 结果。

宿主记录只用于身份、配对及审计，游戏事实的执行权仍在 E/S；接收记录不是可任意再次执行的队列。其最小结构与写入恢复是本次新增实施内容，不能继续按“日志全部延期”跳过。已提交操作的 ID 必须保留在记录/事件中；旧回合退出当前对象图时不重编号。

#### 耗时行动与 prompt cache

放行行动的即时工具结果表示“行动已接受、模拟将继续”，引用本次 ToolCallId；下一回合的 Incoming 是该行动后果的角色观察，可带来源 ID，但默认不伪造成同一调用的第二条最终 tool-result。`continue` 自己拥有新的调用 ID，并另外关联原活动。这样仍保留本方案“旧会话不等待时间推进后结果”的选择。

复用 ID 能避免因重新编号而不必要地修改历史，但不是 prompt-cache 命中的充分条件。还须保持已保留前缀内的内容、块顺序、工具定义与序列化等条件稳定；跨模型/端点也不承诺共享缓存。新回合主动只继承摘要，本来就重构了上下文，不能宣称仅凭 ID 稳定就能复用被丢弃的旧工具历史。

依据：当前兄弟仓库 [RawToolCall / ToolResult](../../../atelia-completion/src/Completion.Abstractions/RawToolCall.cs) 都保存 ToolCallId，[ActionMessage / ToolResultsMessage](../../../atelia-completion/src/Completion.Abstractions/IHistoryMessage.cs) 保持调用/结果结构；[Completion 转换层](../../../atelia-completion/src/Completion) 负责端点消息格式。本次只核对其边界，不宣称已经存在满足所有跨端点场景的稳定双向映射，也不修改兄弟仓库。外部协议对照见 [Claude 工具调用配对](https://platform.claude.com/docs/en/agents-and-tools/tool-use/handle-tool-calls) 与 [prompt caching](https://platform.claude.com/docs/en/build-with-claude/prompt-caching)。

## 5. 规划、发布与恢复

### 5.1 保留逐事件提交，不在控制器内部推进 Kernel

保留控制器逐条返回意图的边界，但按 §4.4 将返回值扩为携带 ToolCallId 的操作封套，并扩展 `CycleCommandKind` 支持交接编辑；不能继续只返回会丢失来源 ID 的裸命令。不为整回合创建可执行 Kernel 的控制器接口。零耗时操作提交后，宿主以同一个上下文身份再次请求操作。未来 LLM 适配器可据此组织局部 tool-loop，本片不实现端点会话管理器。

每次接受放行行动，应用层同时规划行动效果与新的 Pending；白名单内的发生项同时规划 Pending 的固定摘要更新。E 已包含具体的新业务状态，Fold 只应用记录，Complete 只维护派生预测，观察和布局均只读。不能在渲染、控制器查询或冷开加载时顺便生成/消费交接状态。

纯交接编辑与 ContextOpened 使用独立事实分支，绕过通用 `CycleSurvivalRules.Enrich` 和 `CyclePlanning.Observe`，不刷新身体锚点、确认通知或重写地点记忆。新增事实校验限制变化为连续性对象与请求代数；交接编辑沿合法角色命令的既有 LastServedActorId 更新规则，ContextOpened 不改服务游标。世界时间、活动、库存、空间、身体、通知、LastResult 及其他业务字段保持原值。编辑成功的显示反馈来自该事实的 Result/当前草稿，不覆盖角色的上次玩法结果。

### 5.2 交付边界

为保证 Incoming 不受本轮零耗时操作污染，在 RequestDecision 已获选且 Pending 非空时，其 Plan 分支直接返回同刻的“上下文已开启”事实（建议事实名 `DecisionContextOpened`）：封存 Pending，写 Incoming，清 Pending，递增 ContextSequence。该步不调用控制器。下一次 RequestDecision 才交付这个已提交的上下文。不添加独立预测 kind、owner、交付队列或新仲裁等级。

此边界保留同刻自然事件与活动完成先于输入的排序，避免在第一条警告出现时过早冻结、遗漏同刻其他结果。新增一次 E/S 的成本换取稳定的继承结果和简单恢复；不是额外的玩家操作，不确认通知，不改变服务轮转游标。必须推进请求代数以避免重复候选 key，并加入预测依赖和事实校验。

特别注意现有预测依赖：生存预测 key 使用 `DecisionGeneration`，但其 `Affected` 仅比较 Survival 引用。交接编辑/ContextOpened 保持身体引用却递增请求代数时，必须让生存 owner 的失效判断也比较 `DecisionGeneration`；不新增独立生存版本。生存的现有全事件订阅可沿用，attention 订阅须加入两种新事实。用保持 Survival 引用的用例核验 incremental/full 对照与冷开，不能靠无意义地替换身体对象掩盖依赖缺口。

新游戏的第一个上下文在工厂中创建，不需要空的上次行动。终局不会再调控制器：产生终局的事实同时封存 Pending 为终局结果，供最终界面读取，不递增成可行动回合。

### 5.3 失败窗口

| 中断点 | 恢复合同 |
| --- | --- |
| 尚未完整收到操作/Received 未持久保存 | 没有已接受的游戏动作；未捕获输入可丢失，不保证原模型输出重现。 |
| Received 已保存、领域 E 尚未发布 | 原 ID 保留为未执行/中断，不自动重做；不能把接收当作业务接受。 |
| 交接编辑 E 已发布、S 未完成 | 按记录恢复草稿，不再次输入或调用模型。 |
| 放行动作 E 已发布、S 未完成 | 恢复同一行动、ToolCallId 和 Pending，不重复消耗、启动或交接。 |
| Pending 正在累积结果 | 从已保存世界与摘要继续既有模拟；不重放控制器，不回滚活动。 |
| ContextOpened E 已发布、S 未完成 | 恢复同一个 Incoming 和上下文编号；不再次封存或跳号。 |
| 新上下文已发布、控制器还没看到 | 重建 Doc UI 交付同一个上下文，不再开一轮。 |
| 同轮已执行若干零耗时操作 | 恢复最新世界、Incoming 与 DraftHandoff；局部对话原文不恢复，不重复那些领域效果。 |

E 与 S 仍是两个磁盘边界；“同时保存”指一个发生项的具体事实与对应状态，不声称跨 E/S 原子事务。宿主操作记录也不与 E/S 组成原子事务；不依据侧写记录中有无结果来决定是否重新执行游戏动作，按 §4.4 处理记录缺口。

新回合建新结果容器，不清空并复用旧回合历史。理由是生命周期清晰；DG base/delta 的空间收益必须实测，不作为本方案成立的条件。历史 E/S 仍可能保留旧对象，退出当前根图不等于立即回收历史磁盘记录。

## 6. 角色可见性与多角色范围

连续性数据按角色隔离。事件筛选复用/提取应用层的感知规则，不能从 Console 的公共广播文本、完整世界差分或别人的交接数据生成。未访问地点的库存、隐藏路线终点、他人的私有背包与笔记不可进入结果。天气和身体数据只记该角色可知的部分。

第一片在 `--survival` 验证产品体验，同时保持 `--play` 和无角色批处理工作。共享模型的连续性字段可空：未启用的场景沿用原路径；不修改多人抢资源顺序。ContextOpened 只在启用的角色上发生，不改变 LastServedActorId；全量多角色会话策略留给二期。跨角色读取隔离仍用领域测试验证，不能因为当前只启用单角色就省略数据归属。

操作 ID 合同不受连续性开关限制：所有 CharacterController 操作（包括 `--play`）都带 ID；未启用决策回合编号的模式可省略记录中的 ContextSequence，仍用宿主操作作用域关联。批处理没有控制器操作时不伪造 ID。仅新增来源关联和宿主记录，不改变 `--play` 的服务轮转及活动规则。

## 7. 呈现合同

两种 layout 都能读取同一个结构化结果投影。界面区分当前状态、上次行动的历史结果、角色交接文字及本轮草稿；历史时间、仍在途/活动结束和导致唤醒的原因必须可辨。不规定章节顺序、逐字文案和行号，不引入整屏快照测试。

清屏逻辑由 Console 宿主/控制器消费上下文身份，不放进 layout。首次交付与新身份清屏；同身份补给、交接编辑、查询、布局切换追加全量观察。冷开时可以清屏重绘同一身份，但不伪造一次领域唤醒；说明“局部交互文本未恢复，已提交状态仍在”。终局展示封存结果后退出。

启动帮助必须进入首次清屏后的可见内容；`:help` 保持完整元命令及 layout 名称列表。CLI 不新增 `:end`；终端回滚历史和真实 LLM 上下文隔离仍是不同问题。

## 8. 已实施的重构边界

| 切片 | 主要文件/职责 | 验收出口 |
| --- | --- | --- |
| A：领域数据与规划 | 新增连续性/行动结果值模型、操作封套及事实来源 ID；`CycleCharacter`、`CycleActions`、`CyclePlanning`、`CycleSurvivalRules`、`CycleFact`、`CycleFactApplication` | 放行分类、草稿生命周期、固定摘要、操作/结果配对、可见性与事实形状合同；不改玩法数值。 |
| B：交付与持久恢复 | `CycleEngine`、`CyclePredictionMaintenance`、工厂、持久模型注册、GameRevision、Schema 历史 | 同刻排序、ContextOpened 恰好一个已提交身份；真实 S-only / pending E 恢复与 oracle 对照。 |
| C：操作入口、观察与 Console | `CycleObservation`、`PlayInput`、`PlayConsole`、最小宿主操作记录、两种 layout 与帮助 | Console 隐式 ID、接收/结果保存、记录缺口与领域提交对照、手动交接、按身份清屏；不锁排版。 |
| D：验收与状态收敛 | 针对性测试、既有 smoke、独立 Console 试玩、更新状态与合同 | 机械连续性及无回归；明确没有 LLM 效果证据。 |

A/B 是同一最小可运行领域切片的施工拆分，不能发布只有数据字段却没有恢复合同的半成品。建议不动 Kernel/Spatial/DurableGraph 核心，不引入通用消息总线、组件框架或持久工具运行时。新语义升级应用 GameRevision 和受影响 Schema；沿用当前开发存档无兼容要求的政策，实施说明要求新存档，不顺带建设旧存档迁移。

第一个纵向验收点先打通 `wait → ContextOpened → 固定 Incoming → use 后保持不变`，含 pending E 恢复；再加入交接编辑、天气/采集摘要与 CLI。模型通过现有 `DurableDefinitions.Register` 生成注册路径接入，不新建注册框架。A/B 中途若跨多个提交推进，须明确未完成施工状态，不把中间测试绿灯当作完整交付。

新增类和字段以语义职责为准；既有符号无需为词根统一批量改名。若实施时决定重命名公共声明，先提供 IDE symbol rename 映射，再单独做语义改造。

## 9. 验收矩阵

1. `use / cancel / handoff` 不更换上下文；合法放行行动恰好结束当前上下文；拒绝输入、纯查询与 layout 切换不提交游戏 E/S，但保留宿主操作 ID/结果记录。
2. `continue` 分别覆盖空闲确认、途中警告、采集中感知；保留原活动 ID、到期时刻与已得成果。途中 `handoff → continue` 可用且编辑不确认警告；未启用和终局均拒绝交接编辑。
3. 正常完成、警告提前唤醒、同刻多个原因、同刻唤醒、死亡和逃离：结果归属明确，不虚构活动完成，不遗漏不可淘汰原因。
4. 本轮补给或取消后 Incoming 保持不变；上次净变化与当前数值可区分；下一次放行后旧结果不再挂在当前状态图上。
5. 无交接文字、覆盖、清空、Unicode 边界、超长拒绝、收到旧文字但未续写：按合同保存，不自动无限继承。
6. 固定摘要有界且可解释；检查饱和标记、同刻多原因、可见性过滤与隐藏地理不泄漏。活动结束原因覆盖目标完成、资源空、容量/数量限制、时间无法继续安排；不依赖 Result 文案。
7. 真实磁盘重点覆盖交接编辑、放行动作、ContextOpened、终局封存的 pending E 恢复及同轮最新状态的 S-only 冷开；覆盖保持 Survival 引用但代数变化的预测重建。恢复不调用控制器、不重采天气、不重复消费，正常/replay/incremental/full 结果一致。未返回/未发布输入的丢失边界沿既有取消测试，分类、上限、可见性主要用内存测试，不为重复覆盖每段文字机械增加磁盘夹具。
8. 批处理与未启用的 `--play` 不新增交付发生项或改变行动/仲裁轨迹；启用角色之间没有 Incoming/DraftHandoff 串用。
9. Console 自动测试只断言清屏触发、命令路由和语义字段齐全；人工/代理实际体验检查跨屏后能否解释“我为什么在这里、刚才发生了什么、活动结束了吗”。
10. LLM 测试桩提供的 ID 原样经过接收、事实、结果、Pending/Incoming 和冷开；Console 无需用户填 ID，重复主动输入取得不同 ID，同一已登记操作的重绘/恢复不换 ID。失败和纯查询也配对；元命令不增加游戏发生项。相同批次重号/同记录不同参数被拒绝，跨批次相同文本 ID 不误当同一业务操作。
11. 最小宿主记录验证 Received 写失败不执行、Received 后中断不自动重跑、pending E 恢复后结果补记、S 后缺结果不重复行动、查询结果丢失不伪造旧回答。固定 ID 的测试不等于端点映射或缓存命中验收；后两者由 Completion 接入时另验。

验证命令沿现有 solution、Release 和 Schema Verify 入口。工程测试与 Console 试玩不宣称证明 LLM 决策效果；后续模型实验保持世界种子与任务条件一致，比较重复探索、计划遗忘、结果误读、有效反思及上下文成本，避免凭单次成功裁决。

## 10. 延期与重开条件

- 少量交接仍频繁遗漏关键背景：先增加可展开的角色历史或调整摘要，再评估原会话动作后续接；不自动恢复三阶段设计。
- 需要断线后继续精确的 LLM tool-loop：在已保存的操作 ID/结果之上补齐完整消息及推理块等会话协议；端点 ID 兼容映射仍归 Completion，不把 ToolCallId 塞进领域活动 ID。
- 需要长期经验、人物目标与主动反思：独立设计角色记忆及工具，不把 2,000 字交接槽位当长期数据库。
- 需要完整 Agent 行为轨迹：在本片的最小操作记录之外设计消息/推理等完整轨迹及导出；本片不保留完整旧会话对象图。
- 同时启用多角色连续性：先明确零耗时轮转、上下文在他人行动后的刷新政策，再开放产品入口。

## 11. 当前实现入口

| 职责 | 实现 |
| --- | --- |
| 角色连续性与固定值模型（原切片） | 原 `CycleDecisionContinuity` 仅包含本次 Pending 或固定 Incoming、草稿及身份；现由 [CharacterInteractionState](../../src/ResourceCycle/CharacterContext.cs) 替换。 |
| 规划与封存（原切片） | 原 `CycleContinuityRules` 已由 [CycleInteractionRules](../../src/ResourceCycle/CycleInteractionRules.cs) 替换；[CycleEngine](../../src/ResourceCycle/CycleEngine.cs) 仍在 RequestDecision 分支先提交 ContextOpened，再服务控制器。 |
| 来源身份与事实校验 | [CycleActions](../../src/ResourceCycle/CycleActions.cs)、[CycleFactApplication](../../src/ResourceCycle/CycleFactApplication.cs)，元数据事实与跨图字段比较独立于呈现。 |
| 宿主记录与修复 | [CycleOperationJournal](../../src/ResourceCycle.Cli/CycleOperationJournal.cs)；[ReadCommittedOperations](../../src/ResourceCycle/Persistence/CycleDurableHistory.cs) 从已完成 S 覆盖的 E 中查询缺结果记录，不把 pending 当已完成。允许从非零模拟版本的快照建新仓库，只扫描本仓库已有历史。 |
| 展示（现行接线） | [PlayConsole](../../src/ResourceCycle.Cli/PlayConsole.cs)、[ContextHistoryPlayView](../../src/ResourceCycle.Cli/ContextHistoryPlayView.cs)；历史改读冻结 Opening 和经历，完整呈现边界仍按新方案实施。 |

本片保留完整 Doc UI 追加输出；没有增量渲染、`:end` 或动作后收尾阶段。试玩只证明 Console 流程、可理解性与持久恢复，不证明真实 LLM 决策效果、端点映射或 prompt-cache 命中。

相关合同：[无状态控制器](stateless-character-controller.md)、[Console 布局](console-layouts.md)、[术语表](../glossary.md)、[E/S 发布](durablegraph-occurrence-persistence.md)、[游戏修订](game-initialization-and-revision.md)。
