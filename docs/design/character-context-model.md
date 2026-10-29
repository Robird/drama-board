# 角色上下文 Model 与信息交付重构

状态：2026-10-29 已实施，合同与证据见[验收记录](../experiments/character-context-model-acceptance.md)。Human Console 与纯输出已共用持久 Context；未来 LLM 适配器仍未实施。设计调查基线为 `0912377`；本文承接已归档的[问题工作单](../archive/legacy/controller-context-visibility.md)。

## 1. 目标与明确不做的事

建立由游戏维护、按角色隔离、可持久化的决策上下文。Human Console 与未来 LLM CharacterController 消费同一语义材料；背景、初始观察、有序交互反馈和跨回合经历不再依赖某次终端输出或控制器实例字段。

- 世界规则以命令、事件、状态和时间为依据，不读写提示词、Console 布局或模型消息。
- 感知规则决定角色获得什么信息；交互规则决定这些信息进入哪个决策上下文、何时允许行动。
- 成功放行模拟后，旧上下文不再接收执行结果；下一上下文继承实际经历与 handoff，没有动作后收尾阶段或 `:end`。
- 事实、感知和持久交互状态通过现有 E/S 边界提交；恢复不重新询问控制器或重新抽样感知。
- Console 带外管理、开发者诊断与角色材料有明确边界。支持从同一 Model 单独生成可检查的角色上下文，不通过截取混合 Console 字节恢复它。

本轮不实现 LLM 端点、完整模型会话恢复、长期记忆检索、通用消息总线/权限框架、声音传播/爆炸玩法或范围查询优化。不要求旧存档兼容，不保留被新模型替代的 API 形状。生存和 `--play` 中所有受控角色都使用新 Model；`--play` 仍只支持 full，但自身采集效果、加入/耗尽通知及可见活动变化接入同一材料路径，不保留逐提交直出的旧适配器。此范围不增加生存多人玩法或新的公开事件规则。

## 2. 现有实现与可复用基础

| 当前入口 | 事实 | 重构方向 |
| --- | --- | --- |
| [CycleCharacter](../../src/ResourceCycle/CycleCharacter.cs) / [CharacterInteractionState](../../src/ResourceCycle/CharacterContext.cs) | 物理角色与交互状态已分开；Business.InteractionStates 保存 Opening、Items 和待交付经历 | 成功与拒绝共用操作身份及提交边界；旧 Continuity 不再作为第二套权威 |
| [CycleFact](../../src/ResourceCycle/CycleFact.cs) / [CycleObservedEvent](../../src/ResourceCycle/CycleObservedEvent.cs) | E 保存角色可感知事件与实际效果，含观察者、时段和局部序号 | 复用事件/效果语义，加入角色持久经历及来源绑定 |
| [CycleObservation](../../src/ResourceCycle/CycleObservation.Material.cs) | 纯可见观察及嵌套材料直接保存在 Opening/成功 Items；拒绝不保存重复观察，CycleDecisionContext 读取已保存最新观察并另持校验委托 | 后续所有呈现只读保存材料，避免 Context → Observation → Context 引用环 |
| [CycleEngine](../../src/ResourceCycle/CycleEngine.cs) | 已串联业务、生存、访问观察、连续性和解释材料；控制器在 Plan 中被调用 | 组合层编排这些步骤；领域子规则不承担上下文组织 |
| [PlayConsole](../../src/ResourceCycle.Cli/PlayConsole.cs) / [PlayPresentationSession](../../src/ResourceCycle.Cli/PlayPresentationSession.cs) | Console 从已提交 Context 消费新材料，管理反馈位于带外块；世界事实只进入可选诊断 sink | 完整与增量经同一渲染器，消费游标不成为角色知识权威 |
| [CycleOperationJournal](../../src/ResourceCycle.Cli/CycleOperationJournal.cs) | 解析前登记身份，日志与 E/S 分别提交，冷开补配结果 | 保留接收与核对边界；它不成为角色 Context 的权威副本 |

`CycleNotice` 的待确认通知和 `CycleVisit` 的历史观察仍有独立玩法含义，不因新增 Context 删除。Context 是它们在一次决策中的材料记录，不反向承担警告确认、世界库存或地点记忆的权威。

启用 Context 不等于启用所有角色工具。保持场景原有行动能力，例如普通采集不因有交互状态就自动开启生存场景的 handoff；命令目录与校验应读取明确的场景能力，不再借“Continuity 是否为空”推断功能开关。

## 3. 分层与原子边界

```text
命令 / 时间 / 世界规则
        ↓
结构化世界事实、状态变化
        ├── 提交后 → 开发者诊断输出
        ↓
角色感知投影（接收者、可知内容、实际效果）
        ↓
角色交互规则（当前追加 / 等待交付 / 开启与关闭）
        ↓
同一转换的 E/S 发布
        ↓
Context Model → 共用渲染 → Human Console / 未来 LLM
```

图中的层次是职责，不要求每层新建程序集、接口或独立服务。第一版留在 ResourceCycle 应用组合内。世界效果与感知/上下文替换先规划并校验，再记录到 E，Fold 安装替换值，S 成功后才能交付。诊断输出不参与决定感知，也不作为恢复输入。

持久布局选择 **Business 根下按 actorId 索引的 InteractionStates**，逻辑上由角色拥有，但不再嵌入物理 `CycleCharacter`。事实分别记录物理 `ChangedCharacters` 与 `InteractionChanges`；前者沿用具体玩法的效果约束，后者只安装按角色归属的交互替换。这样资源再生可以有零个物理角色变化、多个交互接收者，不必放宽原事件的物理形状。根表验证受控角色一对一归属，无跨角色条目、无重复替换；不新建独立发布根或异步写入器。

规划顺序固定为：业务/空间/生存及既有通知 → 候选业务状态 → 各角色感知与可见观察 → 交互替换 → E。投影读取候选业务状态和必要的交互标量（如草稿、状态），不回指完整 Context；Fold/Complete 校验并安装已记录替换，不再次运行这条规划链。删除 `CycleCharacter.Continuity` 后，核心玩法字段仍只有一个写入来源。

爆炸伤害和机关触发是世界规则；听见爆炸、看见火光、识别施放者是感知规则；是否打断活动、请求决策是玩法/交互规则。它们可共用空间查询，但“追加 Context Item”不能代替伤害或机关执行。当前先使用自身、同地点和明确指定接收者的规则；距离、遮挡、传播时延及索引优化以后独立设计。

## 4. 最小持久模型

建议逻辑结构如下；实施时按 DurableGraph 已支持的类型形状落地，不采用任意 `object`、动态字典或序列化模型 SDK 消息。

```text
Business.InteractionStates[actorId] : CharacterInteractionState
  CurrentContext?（首次服务前可为空；编号从 1 开始）
    ContextSequence
    Status: Open / Closed / Terminal
    Opening（冻结初始材料）
    Items[]（有序语义材料；旧纯拒绝按保留规则移除）
    NextItemSequence（单调分配，不因移除而复用）
    LastOperationSequence（本轮已提交角色操作数，初始为 0）
  PendingAdvance?（放行行动、接受时可知描述、handoff、时刻、活动关联）
  UndeliveredExperiences[]（关闭后至下次唤醒的角色经历）
  DraftHandoff?
  NextExperienceSequence
```

`CurrentContext` 只保留当前/刚关闭的一轮；新开时使用新容器替换旧对象，不在同一个列表上 Clear。没有永久累积的 `List<Context>`。旧对象可随历史 S/E 保留，但产品不承诺任意旧上下文浏览或旧模型会话恢复。上图是逻辑字段，不要求为每个缩进新建包装类。

首次服务前可以收集经历，但不提前冻结 Opening。Open 状态没有 PendingAdvance，待交付集合为空；Closed 状态保留唯一 PendingAdvance，并收集尚未交付经历；Terminal 清空 PendingAdvance/待交付集合并冻结全部最终材料。宿主退出不改变这些领域状态。CurrentContext 为 null 表示尚未开始，不额外持久化另一份阶段枚举。

### 4.1 Opening：冻结起始材料

Opening 包含：角色身份与回合号、游戏内背景/目标与规则材料、当时已知知识、上次放行的简要说明及首次交付经历、handoff、起始完整可见观察。冻结观察是数据快照，不是对当前世界的活引用；不保存世界对象图、校验委托、Console 提示或 Continuity 自引用。

静态背景可以采用受 GameRevision 约束的材料键，渲染器按当前支持的版本解析；动态数值、已知地点、角色文字与观察时刻必须冻结。第一版不建设跨游戏版本旧上下文渲染兼容。不能把“你在山林中迷途”的角色目标只存于一次性 StartupText。

完整观察与有序事件用途不同：观察回答当时能知道什么，事件回答发生了什么。不要从起始状态差分杜撰事件原因，也不要要求只靠事件重建全部世界状态。

### 4.2 Item：以一次语义反馈为追加单位

第一版只需少量明确种类：

- **交互记录**：一次角色输入、操作身份、解析/校验结果及角色可见反馈。成功零耗时操作携带该次事件/效果与更新后的可见观察；拒绝携带可见理由，世界观察不变。
- **感知记录**：没有对应当前工具调用的公开/自身事件及必要观察更新。保留自然事件身份，不编造 ToolCallId。允许事件列表为空、仅含观察更新；其他角色开始采集等可见变化不一定已有独立事件文案，不能因此让 Context 中的当前状态过时。

一次交互将请求与结果成组保存，避免在 Context 中再造 Received/Pending/Succeeded 的中间状态机。接收但尚未提交的操作仍由既有操作记录负责。为降低第一版复杂度，成功更新先保存完整可见观察快照，渲染时用相邻快照与事件选择增量尾部；不同时持久化完整快照和另一份状态 delta 权威。后续只有存储证据证明必要时才优化快照表示。

已进入 Model 的条目内容不可改写；补给后不修改旧观察、旧效果或 Opening。集合允许按 §4.4 移除过期纯拒绝条目，其他材料按既定范围保留。当前值取本轮最近观察快照；纯拒绝不持有唯一的观察更新，清理不会改变当前值。切布局、full/delta、重新显示不生成 Item。

对于 Open 接收者，每次相关世界提交都比较其上一份已记录可见观察与提交后的投影：事件或可见语义有变化才追加一条反馈，完全相同则不追加。自己的成功交互将事件与观察合并在同一 Item，不能再重复追加一条感知 Item。未开始/Closed 时只收集实际经历，直到 Opening 才获取完整当前观察，不保存整段中间观察快照。

操作原始文字属于角色自身输入，可以保存但必须按数据渲染。正文中的 `:exit` 不获得元命令权限。模型私有推理、端点消息 role、响应 item-id 不进入本 Model。ToolCallId 继续是不透明关联值，不是 Item 或事件编号。

### 4.3 身份、顺序与来源

- Context 身份为角色 ID + ContextSequence；同一角色每次新决策递增，模型时间不必增加。
- Item 身份为 Context 身份 + 单调分配的 ItemSequence；顺序是纳入该上下文的顺序。删除条目后允许序号有空洞，不重编号、不用当前数组下标或 Count 分配身份。
- 角色操作另有回合内 OperationSequence；每次已提交的成功或拒绝操作递增一次，交互记录保存该值。它与包含自然感知的 ItemSequence 分开，也不是 ToolCallId。放行操作的序号保存在 PendingAdvance 中，不必为计数向旧 Context 追加接受反馈。
- 经历使用角色局部递增 ExperienceSequence，在规划时分配并随 E 记录；不新增 Kernel 全局 ID 服务。分支若以后支持，需要把谱系纳入外部身份，不在本片预建分支协议。
- 世界 E 中的 EventOrdinal 可包含其他接收者的事件位置；进入 CharacterExperience 时改为本接收者在该事实内的局部顺序，避免通过序号间隙透露其他角色事件。ExperienceSequence 才是经历的持久身份，诊断仍从世界 E 建立来源关联。
- 发生时间/区间保留于经历；首次纳入上下文由其所在 Opening 或 Item 明确。历史重显沿用经历身份，不能重新编号或伪称首次发生。
- 开发者来源 `(LineageId, TransitionCount, FactOrdinal, EventOrdinal)` 可在提交后与角色经历 ID 建立诊断关联；它不是角色排序、持久化追加或恢复所必需的输入。隐藏事件关联不暴露给角色。

这替代“必须在 Plan 获得提交游标，才能持久化角色历史”的前置要求。恢复安装 E 中记录的序号及集合，不能重分配序号、重跑感知或 append 同一经历两次。

### 4.4 纯拒绝的短期保留

用户已选定：**当前 Context 只保留最近 3 次角色操作中的纯拒绝记录，更早的纯拒绝请求与反馈成组移除。** 成功记录、实际经历、Opening 与待交付经历不受此规则裁剪；这不是只保留最后 3 个 Item，也不是保留最近 3 次失败。

- 窗口按当前角色、当前 Context 的已提交操作计数，成功和拒绝都计入，包含本次操作。Console 元命令（包括错误元命令）、空输入、重绘、自然事件、其他角色操作及尚未提交/已中断的请求不计入。
- 最新操作序号为 `n` 时，保留纯拒绝记录中 `OperationSequence >= n - 2` 的条目，移除更早的纯拒绝；不足 3 次时全部保留。每次角色操作提交都执行相同规则，成功放行时也整理即将关闭的旧 Context。
- 例如连续 4 次纯拒绝后，当前 Context 保留第 2、3、4 次；第 1 次拒绝后接两次成功，第 1 次暂时仍保留，提交第 4 次操作时才移除。期间穿插多少观察更新或元命令都不影响这个窗口。
- “纯拒绝”是解析/参数/合法性校验未通过、没有执行世界效果的操作结论。尝试失败但实际消耗体力、发出声音或改变世界的事件不属于纯拒绝。若拒绝同时提供需要保留的角色知识，将该知识作为独立的可见感知材料记录，不让唯一副本随试错条目删除。
- 序号更新、追加本次结果和移除旧纯拒绝都随本次同一 E/S 转换记录；不额外提交一次“清理事件”。条目内容、存活 Item ID、原 ToolCallId 均不变。Fold/恢复安装已记录的保留结果，不根据后来输入再次执行清理策略。
- 移除的是活动 Context 中的整组请求/反馈，不删除 `operations.jsonl` 或历史 E/S，不影响已有结果配对和故障补录；也不能留下指向已移除条目的必需引用。新 Context 的操作窗口重新从 0 开始，旧失败不会因审计补录重新进入当前窗口。

这条规则直接缩短当前 Model 的材料，而不只在某个 layout 中隐藏错误。无须判断后一次成功是否“解决了”此前错误，也不增加成功次数计数器或摘要器。它限制纯拒绝条目数量，不限制每条输入长度、成功记录或整个对象图的总大小。

## 5. 生命周期与交付

| 情况 | 持久变化 | 控制器获得什么 |
| --- | --- | --- |
| 首次决策 | 建立 Open Context 与 Opening | 背景、知识、当前观察、行动，没有上次结果 |
| 成功 use / handoff / cancel | 同一转换追加交互 Item，更新玩法状态；Context 仍 Open | 同轮追加反馈；handoff 仍属主观记录 |
| 解析/合法性拒绝 | 追加拒绝交互 Item；不推进模型时间、不确认通知、不改物理世界 | 可见拒绝理由，同轮可继续输入 |
| continue 只确认空闲警告 | 同轮 Item | 不关闭 Context |
| 成功 go / wait / gather 或恢复模拟的 continue | 关闭 Context，记录 PendingAdvance；接受效果起进入 UndeliveredExperiences | 旧上下文不接收接受/执行效果 |
| 世界继续变化，包括其他角色的零耗时操作 | 按每个接收者自己的状态：Open 追加感知 Item；Closed/未开始收集待交付经历；Terminal 不再更新 | 不向已关闭上下文追加；仍 Open 的角色获得可知更新，与谁发起此次提交无关 |
| 下一次可决策 | 在同刻必要结算之后，新建 Context；消费待交付经历、handoff，冻结 Opening，清空 PendingAdvance/草稿 | 上次行动、首次获知的过程与当前完整观察 |
| 死亡/逃离 | 建立 Terminal 只读材料，纳入未交付经历与冻结终局观察；不产生新可行动请求 | 显示结果，无工具循环 |

终局上下文使用新的 ContextSequence 标识只读终局材料，Status 区分其不是可行动决策；若无进行中的放行也能封存当时已知结果。它不自动调用未来 LLM。精确构造点在既有终局提交中，不能等一个永远不会发生的 RequestDecision。

终局事实的路由优先于普通 Open/Closed 分流：该事实产生的角色可感知经历，与此前待交付经历共同进入新 Terminal Opening，不先追加到即将被替换的旧 Open Context。“Terminal 不再更新”仅适用于已经完成终局冻结后的后续事实，不得过滤促成本次终局的材料。

放行判断复用领域现有的“接受后是否恢复模拟”语义，不由命令名字、Advance 阶段或时间差各自猜测。旧 Context 关闭须随接受事实成功发布；失败、取消或尚未提交不能显示成已执行。

同一事实的自身放行效果先按关闭后的状态路由到待交付集合，其他接收者按各自状态处理。经历按事实顺序、事实内事件顺序分配身份；一次多个接收者更新不能依赖 Dictionary 枚举顺序决定其各自经历的顺序。

最后的耗时请求由旧控制器产生，这个事实不能从模型原响应或 Console 输入回显中抹去。将它保留在 PendingAdvance/操作记录中，下一轮以历史叙述交付；不在旧 Context Items 追加接受反馈，也不在新上下文伪装成新发出的工具调用。结果配对仍使用原 ToolCallId，是否向具体模型端点回传最后的 tool-result 留给 LLM 适配器，不能因此重开旧决策循环。

“首次交付”在这里表示材料被纳入控制器可读 Model，不宣称人已阅读或模型服务已确认。输出成功游标仅为消费端状态，不回写为角色知识权威。

## 6. 操作入口、拒绝与持久恢复

Human 与未来 LLM 共用纯行动字符串入口，第一版不要求每个行动一个工具。Console 先单独分派冒号元命令；其他非空输入在解析前固定 ToolCallId 和 OperationRecordId，然后由共享入口解析、按角色校验并提交。

`ChooseCommandAsync` 返回 `CycleControllerOperation(ToolCallId, RawInput, OperationRecordId)`；游戏组合层使用共享 [CycleCommandParser](../../src/ResourceCycle/CycleCommandParser.cs) 解析，经角色可见性限定的校验后提交成功或 `OperationRejected`，再请求本轮下一次输入。Console 管理命令仍在自己的输入循环中，不返回给游戏；其他控制器直接提交 `:exit` 只会得到游戏语法拒绝。成功 Item、拒绝 Item、放行 PendingAdvance 均保留精确原始输入，规范化命令只用于脚本/目录显示，不改写输入。

拒绝使用明确的交互记录事实：模型时间不变，世界业务字段不变，只更新当前角色交互子图并递增 DecisionGeneration，使已消费的 RequestDecision 不重用旧 key；LastServedActorId、活动/期限、通知、身体警告和 ContextSequence 保持。保留 LastServedActorId 使同刻下次仍轮到原角色；合法零耗时操作的既有仲裁规则不顺手修改。

将“携带操作身份”“成功服务轮转”“确认警告”分开判断。FactApplication 分别校验操作身份与成功服务游标；拒绝使用独立规划路径，跳过成功行动的身体、访问观察与事件感知 enrichment。SurvivalRules 仍借成功服务游标判断既有确认规则。纯记录可见拒绝不是活动，也不是身体/库存事件。

DecisionGeneration 暂保留现有调度用途；拒绝、开轮等消费 RequestDecision 的记录更新相应候选版本并标 dirty，被动接收感知不自动轮转、确认警告或改身体预测版本。不能以整张 InteractionState 的替换次数代替 DecisionGeneration。涉及唤醒的通知仍按既有玩法规则处理。

所有角色反馈从已提交语义记录得到（零耗时/拒绝在 Context Item，放行的接受结论在推进记录），再完成操作日志 Result 和实际输出；对已关闭旧上下文不交付放行结果。E 必须携带可恢复的成功/拒绝结论和反馈材料，正常完成与 ReadCommittedOperations 使用同一结果投影。`CycleFact.OperationDisposition` 和 `CycleCommittedOperation.Disposition` 明确保留成功/拒绝；`CompleteOperations / Reconcile` 使用同一映射，不能把持久化的拒绝补录成成功。

先有游戏 E/S、后有日志/屏幕；日志故障不能回滚已提交游戏或重新执行命令。冷开根据 E 补齐匹配结果，未进入 E 的 Received 标为未完成，不假称已执行。Console 元命令仍使用宿主日志，不往角色图写 Item。恢复生成的文字不冒称上次实际写出的原始字节，语义结果与实际终端投递是两个层次。

本片不新增角色查询命令。未来查询若影响模型上下文，采用同样的交互记录路径；`:look` 与 layout 预览属于人类检查，不是这类角色查询。

## 7. 可见性与跨回合摘要收敛

感知先确定接收者，再生成该接收者可知的内容。筛选覆盖正文、结构化键、名称、因果、风险和元数据；不把完整 CycleFact 或可展开世界引用放进 Item，再期待渲染时删掉秘密。

第一版保留现有玩法可见性：自身物品/活动/身体，当前可见地点与通路，合法公开环境变化，已有规则明确发送的通知。一次其他角色的秘密提交导致自己身体随时间变化时，可以记录自身消耗，不能把秘密动作作为可见原因。

Incoming 不再独立累积一份天气次数、背包差异与身体变化。新的上次推进说明保留行动身份、区间、返回原因、活动关联/是否继续与 handoff，并冻结接受时可知的最小行动描述（原命令、起点及当时可见的对象名称/通路线索）。同一 `trail-1` 在新地点可能表示不同线索，不能用新菜单反解释旧命令；未知目的地只能由后续实际经历补充。无需为此保留第二份完整 Before 快照。

过程效果使用同一经历材料；需要总量时从记录材料派生显示，不长期保存第二套总计。保留净变化展示必须标明区间，不能代替净零过程。删除原摘要字段前，逐类核对天气、昼夜、采集停止等原有信息已有经历材料或被保留为返回原因，不以化简名义丢信息。

本片默认继承上次放行至本次唤醒的全部待交付经历；更早回合及旧回合零耗时交互不自动续传。增量 B 提出的最近 n 回合完整历史属于后续保留策略：本模型提供身份与材料，不承诺已实施完整 n 回合窗口。不能将最近一次放行区间宣称为上一整个回合。

当前 Context 的纯拒绝采用 §4.4 的最近 3 次角色操作窗口；成功记录、实际经历和待交付集合不因此裁剪。单回合大量成功操作或大量感知仍可能增长，第一版不声称硬内存有界。上线 LLM 长循环或多角色事件广播前，仍需单独裁决其他材料的容量/封存策略；超限不得造成世界提交成功而感知丢失。该限制不阻碍当前有限 Human Demo 验证。

另有必须显式接受的运行限制：当前 `CycleEngine` 使用每模型时刻最多 1024 次转换的 Kernel 护栏，零耗时成功操作原本已消耗预算，持久拒绝现在也消耗。首版保留此护栏；耗尽时停止宿主并明确报告，不能自动推进时间、丢 Item 或静默重置。冷开不会重置持久因果序号，耗尽后也无法继续接收下一条放行命令；因此本片不保证任意长同轮 tool-loop。临界与冷开行为由 [CharacterContextRejectionPersistenceTests](../../tests/ResourceCycle.Tests/CharacterContextRejectionPersistenceTests.cs) 覆盖；消除此限制是接入长循环前独立裁决，不能隐含地改 Kernel 时序协议。

按保留规则移除旧拒绝，不撤销已经完成的转换，也不返还上述预算；上下文长度与调度预算分别处理。

## 8. Human Console、纯上下文输出与诊断

两种消费模式共享一个语义渲染入口：

完整角色上下文由 Opening 与当前保留的有序 Items 决定，显示内容不依赖此前向终端写过什么。一次瞬时操作提交后，界面逻辑上显示更新后的完整 Model：GUI 可以重绘全部材料，也可以按条目身份更新；Console 追加新反馈只是绘制层优化，不负责保存信息、判断感知或决定交付归属。不要求先生成完整文本再做字符串差分；增量可以直接由已提交的新条目及相邻观察快照生成。关闭增量绘制后，仍必须能从 Model 完整呈现本轮角色材料。

- **正常交互 Console**：角色正文与管理反馈都可见，但管理块明确标为带外。开场宿主信息和元命令帮助放在“LLM 上下文开始”之前。中途管理回复用明确带外块标记，不制造假的 Context 结束/开始。
- **纯上下文导出/预览**：从指定角色已提交的 Context Model 渲染完整材料，只包含角色内容和范围标记，不含提示符、输入回显、元命令、ANSI 颜色或诊断。第一版可先提供内部可调用渲染入口及测试工件，无需新增导出命令。

因此普通终端两个标记之间不承诺每个字节都是模型输入；诊断 LLM 可见范围应使用纯输出。新上下文开始/结束标记带角色及 ContextSequence。正常放行输出结束标记后不再刷结算增量；新上下文开始时输出首次交付材料。`use` 等即时反馈不再显示误导性的“正在处理结算”阶段切换。

纯输出入口为 `CharacterContextRenderer.RenderFull`，成对标记包围指定 Context 的材料快照，即使它仍是 Open 也不改变其领域状态。正常 Console 则在已提交关闭时显示结束标记；切回仍 Open 的角色仅以带外说明恢复显示其保存材料，不伪造关闭/新开。完整呈现包含 Opening 和保留 Items；仅渲染 `LatestObservation` 是当前状态预览，不能替代整轮上下文。

`:look`、layout 切换/once、mode 切换不新增 Item、不改变回合；它们只读已提交 Model 中最近的可见观察，不重新从 live world 偷补未记录信息。管理回执与预览包在带外块中，未来 LLM 不调用这些 Console 能力。重绘当前状态与重放本轮完整 Context 是两个明确的内部渲染操作，不强迫每次 look 重放整个交互列表。

管理预览不推进 Item 消费游标或改写增量基线；正常输出成功并 Flush 后才推进消费位置，写出失败仍可从 Model 重放。行动风险也来自保存的观察/目录，不在输入时另调运行时预测向正文注入未记录的反馈。

纯上下文预览与未来 LLM 后续输入使用清理后的 Model；Human 终端已经打印的旧失败无需擦除，滚动文本仍不是当前 Model 的逐字副本。消费端不得把曾见过的失败永久缓存并重新混入后续输入。移除早期条目会改变后续请求的共同前缀，可能降低缓存复用；先采用已选定的保留语义，接入 LLM 后再测量，不在本片增加批量压缩策略，也不声称恢复完整端点消息历史。

终局只读材料带开始/结束标记但不显示可输入；EOF/exit 正常结束展示时用带外“会话关闭”，不能写成领域 Context 已放行。冷开同一 Open Context 可重绘其保存材料，回合号不递增；意外中断可能留下不完整终端文本，恢复依赖 Model。

开发者诊断独立于正常角色输出。第一版可提供显式 TextWriter/回调接收已提交世界事实、角色投影关联及上下文变更，不默认为玩家打印全世界日志。记录提交身份和结构化种类/必要字段，不把 `CycleFact.ToString()` 宣称为完整轨迹。诊断写出故障不得重新执行已提交事件；应停止当前宿主交互并报告，冷开依 E/S 恢复。完整耐久诊断日志、轮转及历史导出后续独立实施。

## 9. 实施分片

按可独立检查的边界实施，避免先造新模型却长期维持两个连续性权威。

共享观察与背景直接进入持久 Context，具体集合在构造/init 时复制且公开只读，不另建持久 DTO。Business.InteractionStates 与 Fact.InteractionChanges 已替换 Character.Continuity；合法操作、首次开轮、关闭后待交付、下一 Opening 和终局通过同一 E/S 边界发布。GameRevision 9；Business Schema 4、Character Schema 5、Fact Schema 9、PendingAdvance Schema 2，不兼容旧开发存档。

证据集中在 [CharacterContextLifecycleTests](../../tests/ResourceCycle.Tests/CharacterContextLifecycleTests.cs) 的合法路径、角色隔离、仅快照更新及错误端点拒绝，以及 [CharacterContextPersistenceTests](../../tests/ResourceCycle.Tests/CharacterContextPersistenceTests.cs) 的真实 S 冷开和瞬时/放行/开轮/终局 E-only 恢复。恢复比较独立 E/S 图的语义前缀并安装记录对象，不重跑观察、背景、感知或编号分配；直接业务端点检查也不重新预测风险菜单。共享材料冷开和全部角色事件的基础证据仍分别见 [VisibleMaterialPersistenceTests](../../tests/ResourceCycle.Tests/VisibleMaterialPersistenceTests.cs)、[AllActorObservedEventTests](../../tests/ResourceCycle.Tests/AllActorObservedEventTests.cs)。

原始 submission、共享 parser、持久拒绝/窗口及日志状态配对已经接通；拒绝只改变自身交互材料和请求版本。证据见 [拒绝与保留窗口](../../tests/ResourceCycle.Tests/CharacterContextRejectionTests.cs)、[S/E-only 拒绝恢复与同刻预算](../../tests/ResourceCycle.Tests/CharacterContextRejectionPersistenceTests.cs)及[宿主结果修复](../../tests/ResourceCycle.Cli.Tests/OperationRecoveryIntegrationTests.cs)。恢复安装已记录条目及窗口，不重做合法性判断；纯语法解析仅检查成功事实与保存输入的一致性。

呈现统一由 [CharacterContextRenderer](../../src/ResourceCycle.Cli/CharacterContextRenderer.cs) 消费 Opening/Items，Console 不再从 Fact 直出角色信息；[呈现会话](../../src/ResourceCycle.Cli/PlayPresentationSession.cs)只维护输出游标和带外管理。[诊断 sink](../../src/ResourceCycle.Cli/CycleContextDiagnostics.cs)在提交后提供强类型世界材料与经历关联。下表保留实施边界，最终源码、测试和独立试玩证据统一见[验收记录](../experiments/character-context-model-acceptance.md)。终端滚动历史不等于当前完整 Model。

| 分片 | 交付与范围 | 验收门槛 |
| --- | --- | --- |
| 1. 拆出共享可见材料 | 从 CycleObservation 拆出不含连续性/委托的观察数据；建立背景材料入口；整理世界→感知→交互组合顺序；保留现有行为 | 隐藏地点/私人物品不可达；两种布局信息仍齐全；核心规则不依赖 Console；不预先堆持久空类型 |
| 2. 持久 Context 垂直闭环 | 新模型、角色经历身份、统一 submission、拒绝记录、放行/待交付/新轮/终局；替换原连续性权威；全量上下文渲染；操作恢复接线 | use→handoff→go→抵达/警告→新轮；S 冷开与 E-only 恢复一致；拒绝同轮；两角色隔离；最后放行不向旧 Model 追加反馈 |
| 3. Console 交付与验收 | 接通 Model 消费、增量/full、带外块、开始/结束、纯输出及诊断 sink；删除旧逐提交直出路径 | 实录对照、layout/once 不污染、终局/退出/输出失败；独立 Human 试玩，检查每轮纯上下文自足 |

分片 2 是最小一致的领域替换，分为两个内部检查点：2A 先证明合法操作、生命周期、投影与 S/E 恢复；2B 再接入 raw submission、共享 parser、持久拒绝及其保留窗口、日志状态及拒绝仲裁。两者一起验收，不把 2A 宣称为完整 Context 交付，也不在上线状态中留下“恢复以后再补”的中间合同。施工前核对具体 Schema 注册、结构化 observation 的可持久化字段；新 GameRevision / Schema 依据最终字段统一更新，无旧存档兼容义务。

预计触及 ResourceCycle 的角色/连续性、观察、事件、规划/校验、模型注册与持久化；CLI 的操作入口、日志配对、呈现会话及共享渲染；相应测试。Kernel 和 Spatial 算法不因本片改变。若实现发现需要修改其提交协议或引入独立跨根事务，先回到本设计寻求更小方案。

## 10. 验收不变量与文档整合

- 同一角色、同一 Context，无论由 Human、纯输出测试消费者或未来 LLM 适配器读取，其语义输入一致；界面格式可不同。
- 世界变化与应记录的角色经历共同提交；关闭诊断或更换 Controller 不改变结果、感知或持久 Context。
- 事件原因来自实际记录，历史值不可被当前补给覆盖；接收者和排序稳定；恢复不重新做感知判断、不重复编号或追加。
- 第一次拒绝、成功零耗时、确认后恢复模拟、提前警告、终局、宿主退出分别有明确路径；操作 Result 引用原 ToolCallId。
- 私有动作、不可见对象和隐藏因果不会通过正文、属性键、错误或诊断来源泄漏。
- 两个角色均 Open 时，一方公开活动变化更新另一方的 Model；没有事件文案也不丢可见状态更新。正常显示、look 与纯输出同源。资源再生可只改变多个角色的交互子图，而不伪造身体或活动效果。
- 拒绝保持原服务机会和待确认警告；拒绝 E 已写/S 待恢复、S 已写/日志未配对两种故障窗口，恢复后仍是原 ID 的拒绝而非成功；同刻预算耗尽不篡改已提交状态。
- 纯拒绝窗口覆盖连续 4 次失败、失败与成功交错，以及元命令/自然事件/其他角色操作穿插：只按本角色本轮最近 3 次已提交操作淘汰纯拒绝，成功与实际经历不被删除。存活条目与工具身份不变，删除后继续分配的序号不复用。
- 清理随同一次转换提交，S 冷开与 pending E 恢复得到相同保留集合和计数；故障日志补录不把已清理拒绝重新放回 Context。审计仍可追溯，当前观察不变，1024 转换预算不返还；独立知识材料及有实际效果的失败不被误删。
- 已提交但尚未写出、写出后进程退出、pending E 恢复均能从保存的语义材料重新显示；不承诺恢复模型私有推理或外部调用恰好一次。
- 测试语义归属、顺序、效果、恢复和隔离，不锁死整屏快照、章节位置或缩进；真实试玩保留原始输出和纯上下文材料。

现行合同同步于[信息继承](decision-context-handoff.md)、[控制器方向](stateless-character-controller.md)、[Console 边界](console-command-boundary.md)、[布局](console-layouts.md)与[增量阶段目标](incremental-ui-milestone.md)；问题工作单已归档。旧验收记录保留其历史范围，不作为新实现的运行证据。

## 11. 本轮设计审查收敛

三路 subagent 分别审查模型化简与分片、发布恢复与调度、可见性与 Console 消费；主线程核对代码依据并集成。此次没有运行实现测试，也不以设计审查代替后续工程验收。

| 审查发现 | 最终处理 |
| --- | --- |
| 广播感知需要更新原事实未替换的角色，直接扩充 ChangedCharacters 会破坏物理形状约束 | 独立根内 InteractionStates 与事实 InteractionChanges；仍保持单根提交，不增加事务协议 |
| 其他角色可能仍有 Open Context，且无事件文案不代表观察没变 | 按接收者状态路由；Open 记录必要观察更新，所有查看只读已提交 Model |
| 拒绝进入 E/S 可能轮转、确认警告、重复候选或恢复成 Succeeded | 拆开操作身份/轮转/确认，更新候选版本，持久成功/拒绝结论；明确 1024 同刻预算限制 |
| 旧命令可能在新地点被解释为不同路线，摘要与经历容易重复 | 冻结最小接受时可知描述；保留返回原因，实际效果仅保留一套来源 |
| 新模型与 submission 同时改造造成单片过大 | 分片 2 内分 2A 合法路径/恢复与 2B 拒绝/日志检查点，共同交付验收 |

保留 CurrentContext、PendingAdvance、待交付集合的职责区分；它们分别保存已纳入材料、推进关联和未交付经历。没有为了减少字段强行合并生命周期，也没有引入通用事件总线、全局事件 ID 服务或第二份持久状态 delta。
