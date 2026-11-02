# DramaBoard 术语表

本文统一当前核心与应用实例的概念、中文称呼和代码映射。规则细节仍由相应设计说明，实际能力以源码和测试为准；归档文档保留其历史语义。

使用约定：新文档首次出现概念时使用“中文名（EnglishName）”；引用具体 API 时保留准确标识符。概念不要求各有一个同名类。本表中的“概念名”不表示已经存在公共接口，也不构成未来框架的施工计划。修改概念含义或引入易混淆命名时，同步更新本表和受影响的当前文档；不批量改写历史记录。

## 游戏与共享代码

见[分层与组合](design/layering-and-composition.md)及实施中的[连续生活世界](codex-goals/living-world/README.md)。以下是职责用语，不表示已有统一框架类型。

| 推荐术语 | 含义与边界 | 当前代码映射 / 状态 |
| --- | --- | --- |
| 实验应用（Playground） | 用于组合、试验和验证世界模拟机制的开发期可交互游戏原型；不追求商业游戏的游戏性，不预设通用游戏框架。 | 应用项目与类型已按此名称统一；实施及验收见[重命名说明](implementation/playground-rename.md)。见[实践参照与命名交接](research/playground-development-practices.md)。 |
| 运行实例目录（home） | 将宿主启动设置、连接配置与默认世界存档组织在一起的目录；不是操作系统用户目录，也不构成世界身份或恢复权威。 | [方案](design/playground-home.md)由 Playground.Cli 实施；manifest 标识 home，仅选择当前目录或显式路径。 |
| 山野镇（Hillside） | 融合小镇与山林的连续生活游戏；唯一生活世界根，抵达镇口不会终止角色。 | 由 Playground 项目和 Playground 类型承载；已融合生产、现金、市场、赠钱与居民报价；完成状态见专题工作计划。 |
| 具体游戏（Game） | 拥有规则组合、世界根、初始化、内容、结束条件和修订政策的应用；可与其他游戏共享机制。 | Playground 拥有山野镇的领域根和存档适配；独立 Microtown 实现已[归档](archive/microtown.md)，不预设统一根接口。 |
| 场景（Scenario） | 一款游戏内选定的地图、人物、资源、初始状态和局部参数组合；不等于一套独立游戏规则。 | `PlaygroundGameFactory` 提供当前场景构造；新场景无需自动新建程序集。 |
| 共享领域模块（Shared domain module） | 一项可复用能力的状态与局部规则；游戏组合协调跨模块变化并统一发布。 | Inventory 为现有实例；Goals 等后续提取按实际消费者验证，不把模块等同 ECS 组件。 |

## 角色、控制与交互

| 推荐术语 | 含义与边界 | 当前代码映射 / 状态 |
| --- | --- | --- |
| 角色（Character） | 世界内承受规则效果的对象，拥有技能、背包和活动。不能用 Player 指代角色状态。 | [PlaygroundCharacter](../src/Playground/PlaygroundCharacter.cs) |
| 跟随（Following） | 队员主动授权领队带自己移动的持续关系；期间禁止自己的移动与其他耗时活动，到点可显式解除；不等于采集/等待 Activity。领队和队员由关系派生。 | `PlaygroundCharacter.FollowingLeaderId`；`follow` / `unfollow` / `dismiss-followers`；游戏内 `PlaygroundFollowingRules` 与唯一 `PlaygroundActions.NeedsDecision`。见[跟随合同](design/character-following.md)。 |
| 角色控制器（CharacterController），亦称 Player | 为被服务的角色读取观察、选择并提交命令的决策提供者。可由人类、LLM 或脚本驱动；一个控制器可以服务多个角色，不预设一对一。此处 Controller 指决策职责，不指运动控制组件。 | 当前应用接口为 [IPlaygroundCharacterController](../src/Playground/PlaygroundActions.cs)，尚未提取通用引擎接口。 |
| Console 控制器（Human Console，旧称 HumanPlayer） | 通过终端取得角色决策；操作者可以是真人或使用终端的 Coding Agent。操作者、控制器和被控角色是三个不同概念；与游戏内直接调用模型的 LLM 控制器区分。 | [PlayConsole](../src/Playground.Cli/PlayConsole.cs) 中的 `ConsoleCharacterController`；同一入口可服务多个角色，生存场景当前支持 1–3 人。当前[融合世界](codex-goals/living-world/README.md)沿用此区分，支持指定 Human 角色与模型角色同局。 |
| LLM 控制器（LlmPlayer） | 通过 LLM 调用或局部 tool-loop 产生角色决策。 | [LlmCharacterController](../src/Playground.Completion/LlmCharacterController.cs) 接 Completion；统一 `PlaygroundHost` 负责连接、共享预算和旁观输出，未覆盖角色默认由 LLM 控制。 |
| 模型连接（LLM connection） | 宿主侧可复用的 provider、model、protocol 与认证配置；ID 区分不同模型或同模型参数，不是角色身份。 | `LlmConnectionConfiguration` 冻结配置；`LlmConnectionPool` 按连接缓存并拥有客户端；见[多模型连接](design/multi-llm-connections.md)。 |
| 角色控制绑定（Character control binding） | 每个角色的运行时控制选择：Human Console 或 LLM(ConnectionId)；默认 LLM(DefaultConnectionId)，逐角色覆盖一次，不再另算 Human 优先。配置与审计不构成 DurableGraph 领域状态。 | `CharacterControlConfiguration` 解析并冻结，`PlaygroundHost` 统一路由；见[运行时绑定](design/playground-home.md#31-双层角色控制映射)。 |
| 游戏管理上下文的独立决策调用 | 每次以当前角色保存的完整 Context 材料构造新的模型请求，交回一个操作，游戏提交后再按需调用；同一决策回合可包含多次调用。 | [生存二期 2A](implementation/survival-llm-controller.md)已接入；这是调用策略，不是原生工具会话续接。 |
| 角色决策规约（Character decision policy） | LLM 固定提示词中的稳定检查与行为指引；依据角色材料和本回合反馈选择行动，不固定价值排序或执行阶段，不保证输出确定性。 | [LlmCharacterController](../src/Playground.Completion/LlmCharacterController.cs) 按能力选择 `GoalGuidance` / `HandoffGuidance`，共享 `LlmDecisionLoop` 提供工具协议；[配套设计](design/goal-management/decision-policy.md)。已实现，代码校验仍是强约束权威。 |
| 原生模型工具会话 | 按模型协议续接 assistant 消息、工具调用及配对结果，可能包含原生推理续接材料；与角色语义 Context 分开。 | 共享 `LlmDecisionLoop` 在单次决策内保留反馈所需原生 Tail；跨决策清空，不支持冷开恢复原生会话。 |
| 行动主体（Actor） | 某条命令、事实或规则中的“谁在行动”这一关系角色，不另立一种 Character 类别。 | Playground 的 `actorId` / `ActorId` 指向 `PlaygroundCharacter.Id`；不因此要求所有 Spatial 实体都是角色。 |
| 观察（Observation） | 某个角色当时可知的信息投影；不等同于完整世界对象或完整上下文。具体游戏可同时投影本人已保存的近期记录。 | [PlaygroundObservation](../src/Playground/PlaygroundObservation.Material.cs) 保存于 Opening/Items，无世界、Context 或委托引用；只提供本角色可知材料。 |
| 行程观察（Journey observation） | 角色正在行走的路线知识：出发地、通路与方向线索、已知目的地；未探索目的地不公开身份。 | [PlaygroundJourneyObservation](../src/Playground/PlaygroundJourneyObservation.cs) 冻结于 `PlaygroundObservation.Journey`，与 `ArrivalDue` 同时存在；抵达后清除，途中终局保留。 |
| 游戏背景材料（Game background） | 角色可知的目标与规则；不含宿主启动信息、管理命令或排版。 | [PlaygroundBackground](../src/Playground/PlaygroundBackground.cs) 从纯观察选择并冻结材料，随 Opening 持久化。完整渲染与增量均使用保存的 Opening.Background，不按当前观察重新选择规则。 |
| Console 布局（Play layout） | 对同一角色观察的文本组织方式；可运行时切换，属于会话偏好，不是角色记忆或游戏状态。 | [IPlayLayout](../src/Playground.Cli/IPlayLayout.cs)、[PlayPresentation](../src/Playground.Cli/PlayPresentation.cs)；[布局与元命令](design/console-layouts.md)。 |
| Console 元命令（Console meta command） | Console 操作者管理会话的操作，以冒号开头；不属于角色的领域行动。显示在终端上不代表游戏内 LLM 控制器拥有这项能力。 | [ConsoleMetaCommands](../src/Playground.Cli/ConsoleMetaCommands.cs) 的 `HumanDefault` / `None` 同时控制提示与解析，`ConsoleCharacterController` 执行；纯行动由 [PlaygroundCommandParser](../src/Playground/PlaygroundCommandParser.cs) 解析。见[边界合同](design/console-command-boundary.md)。 |
| 增量观察（Incremental observation） | 在同一阅读上下文内，按序追加事件及其效果，尾部统一汇总受影响状态的最新值并明确失效信息；从已提交 Context Item 及相邻观察绘制；完整材料可直接由 Model 重绘，消费游标仅为 Console 优化。不是游戏状态的增量持久化。 | 生存模式默认 delta；[PlaygroundViewSnapshot](../src/Playground.Cli/PlaygroundViewSnapshot.cs) / [PlaygroundViewUpdate](../src/Playground.Cli/PlaygroundViewUpdate.cs) 与 [PlayPresentationSession](../src/Playground.Cli/PlayPresentationSession.cs)，见[切片 A 合同](design/incremental-ui-slice-a.md)。 |
| 可见事件与效果（Observed event / effect） | 给角色的有序过程材料；效果保存有语义身份的前后值，解释结算但不再次执行结算。当前值、历史效果和观察范围变化分别表达。 | [PlaygroundObservedEvent / PlaygroundObservedEffect](../src/Playground/PlaygroundObservedEvent.cs) 随事实 E 保存，角色经历复用同一语义；不再独立累计 Incoming 总量。最近 n 回合窗口仍待 B 实施。 |
| 可见状态键和值（State key / visible value） | 与布局和中文名称无关的状态身份，以及数值、布尔、文字、已知不存在、不可见等有限值；自有物品用尽是 0，不等于离开观察范围。 | `PlaygroundStateKey` / `PlaygroundVisibleValue`，效果与当前观察共用；数值按 `Units / Scale` 表达。 |
| 速率贡献分解（Rate breakdown） | 同量纲的已求值持续贡献及其派生总值；公开来源键支持按属性或来源组织，不等于一次性效果或 Buff 实例。 | [RateBreakdown / RateContribution](../src/Playground/RateBreakdown.cs) 保存来源键、名称与贡献；`PlaygroundSurvival` 和冻结观察共用此形状，总值只读派生。见[设计](design/attribute-rate-explanations.md)。 |
| 属性阈值预测（Attribute threshold forecast） | 在明确假设下，单项数值到达警戒线与耗尽的预计时刻；不是通知发送时间，也不承诺角色能存活至每项预测时刻。 | [PlaygroundAttributeForecast](../src/Playground/PlaygroundAttributeForecast.cs) 由冻结 `PlaygroundSurvivalObservation` 按属性保存；历史警告确认独立。见[设计](design/attribute-threshold-forecasts.md)。 |
| 行动结果预览（Action projection） | 根据本人冻结状态、公开耗时和速率，估算无额外干预时的结果；不是已决定事实，也不改变合法性或保证成功。 | 归档小镇 [TownTextRenderer](https://github.com/Robird/drama-board/blob/b6fea5e94ca3ea718cee2546a2d785afa1bafa6d/src/Microtown/TownTextRenderer.cs) 显示不进食的耗尽时刻、行程/活动及多批生产饱食预览；零以下表示在完成前已失败，不是保存负值。见[社交切片验收](experiments/microtown-social-acceptance.md)。 |
| Console 消费游标（Delivery cursor） | 记录某角色某上下文已成功绘制的最后条目，仅优化追加输出，不决定角色可知信息。 | `PlayPresentationSession` 在写出与 Flush 成功后推进；预览不改变游标，完整材料直接从 Context 重绘。输入提示仅在实际调用控制器时出现。 |
| 决策回合（Decision turn） | 角色一次局部交互上下文的生命周期，可包含多次零耗时操作；接受放行模拟的行动后结束，下次唤醒建立新上下文。不同于一次方法调用、一次发生项或整项活动。 | `CharacterContext.ContextSequence` 与 Open/Closed/Terminal；`DecisionContextOpened` 冻结新的 Opening，终局在终局提交内新建只读上下文。 |
| 角色上下文 Model（Character context Model） | 按角色/决策回合保存的初始材料与有序交互，供不同控制器消费；与完整模型协议会话、世界状态和 Console 输出分别建模。 | [CharacterContext](../src/Playground/CharacterContext.cs) 位于 Business.InteractionStates；Opening + 当前保留 Items 是呈现真相；[CharacterContextRenderer](../src/Playground.Cli/CharacterContextRenderer.cs) 同源提供完整纯输出与增量，最新状态预览不等于完整 Context；已裁剪拒绝可仍留在终端滚动文字中。 |
| 上下文条目（Context Item） | 角色可见语义交互或感知材料；内容不可改写，身份由角色/回合/单调 ItemSequence 决定；顺序表示纳入上下文的顺序，事件仍保留发生时刻。 | `CharacterContextItem`，包含反馈与完整更新观察；允许仅观察变化、没有事件文案。纯拒绝没有观察/经历，只保留本角色本轮最近 3 次已提交操作中的拒绝；成功与感知条目不因此删除。 |
| 待交付经历（Undelivered experience） | 角色按规则可感知、尚未纳入下一次决策上下文的结构化材料；不表示网络消息已投递或人已阅读。 | `CharacterInteractionState.UndeliveredExperiences` 保存 `CharacterExperience`；ExperienceSequence 按角色分配，Closed/未开始时累计，新 Opening 消费。 |
| 角色交接文字（Handoff text） | 角色给下次上下文的意图、预期、假设与待办；不是游戏确认的事实，也不是完整对话或长期记忆。 | `CharacterInteractionState.DraftHandoff` 与 `Opening.PreviousAdvance.Handoff` 分开；下一次放行记录并清空草稿，不自动续传。 |
| 署名通信（Signed communication） | 活跃角色间的定向说话、赠与或范围大喊；话语内容是角色陈述，不自动成为规则、已探索地图或世界真相。 | `say` / `give` 限同地单一目标；山林另有 `shout`，以 `SpeechShouted` 的 SubjectId/SubjectName/Detail 表达署名原文，和 [PlaygroundObservedCommunication](../src/Playground/PlaygroundObservedEvent.cs) 一样通过既有 Context 交付；山野镇还支持同地赠钱。 |
| 通信额度与待响应（Communication budget / attention） | 每角色每模型时刻有限发送；接收可合并为一个待响应标记，但逐条经历保留。确认保留原活动期限，不返还同刻额度。 | [PlaygroundCommunicationState](../src/Playground/PlaygroundCommunicationRules.cs) 的 `BudgetAt / SentCount / AttentionPending`；当前每时刻 4 次，时间前进按需恢复有效额度。 |
| 有界个人交互记录（Bounded interaction history） | 本人收发的有限语义记录，区别于最近操作回执、活动结果和宿主审计日志；同一交互两边共用身份，各用本地序号。 | 归档小镇 [TownInteractionState / TownInteractionEntry](https://github.com/Robird/drama-board/blob/b6fea5e94ca3ea718cee2546a2d785afa1bafa6d/src/Microtown/TownInteractionState.cs)：最多待交付 32 条、本次 opening 新项 32 条、此前 16 条；未读不裁剪，队列满拒绝发送。不是完整长期记忆。 |
| 材料交付高水位（Delivered-through sequence） | 已纳入发布的决策材料的最高本地记录序号，不证明控制器已阅读或回答；与 Console 输出消费游标分开。 | 归档小镇的 `TownInteractionState.DeliveredThrough` 在 `DecisionOpened` 推进，`OpeningAfter` 标记该次新项下界；渲染不修改两者。 |
| 忙碌响应（Busy response） | 活动仍进行时，为待响应通知提供决策机会；唤醒不取消活动，交付材料也不等于完成响应。 | 归档小镇 `TownInteractionState.NeedsResponse` 与交付游标独立。即时成功操作轮转但保留标记；`continue` 清标记且保留活动期限，`cancel` 结束等待/生产并一次退料。途中移动不开放该窗口。 |
| 居民报价（Resident trade offer） | 指定双方、每边一种资产的交换条款；未接受前不转移或预留资产，接受时重验并一次结算。 | 山野镇 [PlaygroundTradeOffer / PlaygroundTradeRules](../src/Playground/PlaygroundTradeRules.cs)；`offer`、`accept`、`reject`、`withdraw`，到期/参与者死亡也会关闭。报价身份包含角色、Context、操作序号；区别于口头承诺和可尝试提示。 |
| 报价版本交付（Offer-version delivery） | 某一报价的有效或终态版本首次纳入参与者 Opening；不代表同意、阅读或完成响应。 | [PlaygroundTradeState](../src/Playground/PlaygroundTradeState.cs) 保存双方已交付版本/Context 序号；最多 24 份、每发起人 8 份。双方终态交付后可在后续 Opening 回收，并同步另一方仍打开的材料；死亡方豁免未来交付，不伪造已读。内部游标不进入角色观察。 |
| 工具调用标识（ToolCallId） | 单次控制器操作的稳定关联 ID，结果引用同一个值；不等于 Activity、回合、消息 item ID 或全局幂等键。 | `PlaygroundControllerOperation`、行动事实与 `PlaygroundOperationJournal`；Console 隐式生成，LLM 保留 Completion 交付值，端点映射归 Completion，见[操作身份合同](design/decision-context-handoff.md#44-操作-id结果配对与-completion-边界)。 |
| 原始操作提交（Submission） | 控制器产生的行动文本和已捕获身份；语法或合法性失败也进入同一提交边界。 | `PlaygroundControllerOperation(ToolCallId, RawInput, OperationRecordId)`；共享 `PlaygroundCommandParser` 产生命令，`OperationRejected` 产生纯拒绝条目；Console 元命令不通过此入口。 |
| 命令 / 行动意图（Command） | 请求做什么及其参数；可能被校验拒绝，尚不是已发生事实。日常可称“行动”，讨论协议时优先用 Command。 | [PlaygroundCommand、PlaygroundActions.Check](../src/Playground/PlaygroundActions.cs) |
| 耗时活动（Activity） | 角色已启动、跨模型时间持续的过程；移动与等待/采集互斥。 | 等待/采集由 `PlaygroundActivity` 表示；移动由 Spatial 的 `TraversingLocation` 表示，不复制到另一份 Activity 计时器。 |
| 感知通知（Notice） | 角色收到、等待控制器响应的业务信息，例如他人加入或资源耗尽；不是世界所有事件的副本。 | `PlaygroundNotice`、`PlaygroundCharacter.Notices`；会持久化。 |
| 决策请求 / 待决策（Decision request） | 当前应给予角色控制器交互机会。可因空闲或有待响应感知产生。 | `NeedsDecision`、`PlaygroundPredictionKind.RequestDecision`；没有独立持久请求对象。 |
| 唤醒（Wake-up） | 服务决策请求：展示角色材料并等待操作提交；拒绝保持原服务机会。不是操作系统线程唤醒，也不意味着取消 Activity。 | `IPlaygroundCharacterController.ChooseCommandAsync`；等待输入时暂停模型时间。 |
| 交棒 / 服务轮转（Decision rotation） | 多个角色待决策时，按确定顺序选择下一服务者；不保证资源公平分配，也不保证无限拒绝或即时操作下时间推进。 | `PlaygroundBusinessState.LastServedActorId`；拒绝和 Console 纯查询不交棒，归档小镇即时成功轮转空闲或待响应居民。 |
| 个人终局（Character ending） | 角色获救或死亡的权威记录，停止本人的活动、需求和决策；保存结束时刻、原因与历史位置。 | `PlaygroundCharacter.Ending` / `PlaygroundCharacterEnding`；同时退休 Spatial 实体，其他人可继续。 |
| 整局总结（Expedition summary） | 从所有个人终局派生的宿主结果；不是局内角色自动知道的信息。 | `PlaygroundWorld.ExpeditionSummary`；Exploring / AllEscaped / PartiallyEscaped / NoneEscaped，不单独持久化。 |

**“多人”需要说明计数对象。** 山野镇支持 1–3 个角色，默认共用 Human Console；`--llm` 控制全体，`--human ID` 选择一个 Console 角色。角色记忆分别保存在游戏图中，不能由角色数推导出同样数量的控制器实例。需要精确表达时，用“多角色”或“多控制器”，不单写“多 Player”。

**唤醒不等于打断。** `continue` 确认感知、保留活动及原定到期时刻；`cancel` 取消当前活动、保留已获成果。收到感知和活动继续存在可以同时成立。

**生存警告有两种标记。** `PlaygroundSurvival.WarnedMask` 表示当前低值区间已经提醒，`PendingMask` 表示尚待确认的历史提醒。确认只清后者；余量严格恢复到警告线以上才允许再次提醒。途中补给保留原空间期限，也不自动确认历史警告。具体规则见[生存需求](design/survival-needs.md)。

**控制器不私藏角色的跨决策记忆。** 已采纳[共享无状态控制器决策](design/stateless-character-controller.md)：游戏维护角色状态并构造动态上下文，控制器可共享运行设施，本次工具交互使用独立局部会话。三层状态归属、可见性及恢复边界以该文档为准；角色观察和纯命令校验已接入，LLM 适配器使用独立 Context 请求；完整原生工具会话恢复仍未实施。

## 角色实验环境（一期已实现）

见[专题入口](design/character-gym/README.md)与[使用说明](design/character-gym/usage.md)。概念不要求各有同名类型；宿主证据不是角色记忆。

| 推荐术语 | 含义与边界 | 当前代码映射 / 状态 |
| --- | --- | --- |
| 角色实验环境（Character Gym） | 支持角色轨迹回看与请求边界分叉续跑的宿主实验能力；独立反馈不依赖其录制，不等于训练平台或自动优化器。 | [GymSession](../src/Playground.Cli/Gym/GymSession.cs)、`GymCommands`；`--gym off|record`、`--gym-export` 与 `--gym-fork-request`。 |
| 运行记录（Run） | 一段宿主执行及其配置/审计身份；同一分支可跨多个 Run，一个 Run 可服务多个角色。 | `GymSession.RunId`、[GymJournal](../src/CharacterGym/GymJournal.cs) 的 run-start/end；每次模型请求另有 RequestId。 |
| 角色轨迹（Character trajectory） | 按角色组织的实际材料、控制器输出、提交结果与感知过程，可跨运行衔接；不是当前 Context 的别名。 | [GymTraceExporter](../src/CharacterGym/GymTraceExporter.cs) 按角色及分支截点继承祖先前缀，并报告证据缺口。 |
| 人物历史阅读视图（Character history，方案） | 统一宿主时间线的逐角色阅读投影：观察、可读 reasoning、普通输出、操作和已交付经历；Human 无 reasoning/response，不是新增领域记忆。 | [实施方案](design/character-history-and-diagnostics.md)拟由 CLI 导出 Markdown，主体不依赖 raw 模型文件；尚未实施、不回灌角色。 |
| 统一宿主日志（Host journal，方案） | 单 store 内有序记录操作事实、运行诊断与可选人物材料；操作恢复只读当前分支自己的记录，history 可选择祖先前缀。 | 同一[方案](design/character-history-and-diagnostics.md)拟以 host/events.jsonl 替代 operations/Gym 文件；世界 repository 继续是游戏恢复权威。 |
| 需求反馈（Feedback report） | Human/LLM 提交的非空自由文本，由宿主附加提出时的角色材料与可用决定前边界；不是游戏操作回执、世界事实或已批准需求。 | [LlmFeedbackSubmission](../src/CharacterControl.Completion/LlmFeedbackSubmission.cs)、`feedback({text})` / `:feedback <text>`；[独立合同](design/independent-feedback.md)规定刷盘后返回 FeedbackId，正文不进入角色状态。 |
| 重跑起点（Rerun anchor） | 可持久定位的已完成 S 决策边界；关联请求但不等同于某次模型调用。 | [PlaygroundDecisionBoundary](../src/Playground/Persistence/PlaygroundBranchBoundary.cs)，以分支/commit/角色及游标校验，不能只用模型时间或 Context 序号定位。 |

## 角色目标管理（已实现）

当前[里程碑](design/goal-management/README.md)采用以下概念，源码映射如下；实际验收证据集中见[工程验收](experiments/goal-management-acceptance.md)与[试玩记录](experiments/goal-management-playtest.md)，实现存在不等于模型效果已验收。

| 推荐术语 | 含义与边界 | 当前代码映射 / 状态 |
| --- | --- | --- |
| 目标（Goal） | 角色明确采纳的目的及完成标准、进展和续接信息；标准区分结果/过程、历史成果/当前条件。角色判断不等于世界事实；不等于 coding agent 的外部工作 Goal。 | [Goal](../src/Goals/GoalModel.cs)；上述语义区别不对应新增类型枚举。 |
| 目标来源关联（GoalLink） | 父目标使用子目标的独立关系，保留该来源的理由；多个父目标可显式引用同一子目标，同父子对最多一条边。 | `GoalLink`；不按同文去重，不自动推断依赖调度或资源分配。 |
| 焦点 / 焦点路径（Focus / Focus path） | 当前优先处理的目标及选定来源路径；末端 Open，祖先可保留其他状态的来源背景；独立于物理活动。 | `GoalSet.Focus`；[GoalRules](../src/Goals/GoalRules.cs) 处理显式路径及回退，没有隐藏的历史焦点栈。 |
| 未结束目标（Unfinished goal） | Open 与 Paused 的合称，跨 Context 保留可发现入口。 | `GoalStatus` 的 Open / Paused / Completed / Abandoned；`Goal.IsUnfinished`。 |
| 目标挂起（Goal pause） | 暂时不推进，保留原因与恢复线索；不取消 Activity，不递归暂停共享子目标。 | `pause-goal` / `GoalOperationKind.Pause`；不另设 suspend 状态。 |
| 复查线索（Revisit cue） | 提示哪些关键假设或变化值得重新考虑的短文本；Open/Paused 均可携带，包含挂起时的恢复线索用途。 | `Goal.Revisit`；不自动订阅事件、唤醒、恢复目标或变更 Activity。 |
| 目标重开（Goal reopen） | 将已结束目标显式改回 Open，保留身份和有限的旧判断；用于更正误判或重新采纳放弃的目的。 | `reopen-goal` / `GoalRevision`；成果正常消耗后再次需要，默认另建目标。 |
| 目标遗忘（Goal forget） | 显式移除已结束且无任何关联的当前目标记录。 | `forget-goal`；不删除历史 E/S 或旧冻结观察，不复用 ID。 |

长期目标管理与一次性 handoff 各有用途；具体完成标准、材料限制及共享关系规则以专题文档为准。

## 时间、预测与调度

| 推荐术语 | 含义与边界 | 当前代码映射 |
| --- | --- | --- |
| 模型时间（Model time） | 游戏世界时间轴；与人类思考、网络调用所用的墙钟时间分开。 | [ModelTime](../src/Kernel/Time/ModelTime.cs)，一个 tick 为 1 毫秒；不是 .NET `TimeSpan` 的 tick 单位。 |
| 模型时长（Model duration） | 两个模型时刻之间的时长。 | [ModelDuration](../src/Kernel/Time/ModelDuration.cs) |
| 逻辑时点（Logical instant） | 模型时间加同一时刻内的提交因果序号；用于区分模型时间相同的多次转换。 | [LogicalInstant](../src/Kernel/Time/LogicalInstant.cs)：`ModelTime + CausalOrdinal` |
| 到期时刻（Due） | 过程或候选预计应结算的模型时刻。它不保证结算成功；活动可能取消，采集可能无货。 | [CandidateDue](../src/Kernel/Scheduling/CandidateDue.cs)、`PlaygroundActivity.Due`、`TraversingLocation.ArrivalDue`；是否持久化取决于所属概念。 |
| 预测（Prediction） | 基于当前业务状态推导的未来发生可能；可失效、重算、冷开重建。当前纯派生预测是运行时缓存。 | [PlaygroundPrediction / PlaygroundForecasts](../src/Playground/PlaygroundForecasts.cs)、[ContactPrediction / SpatialContactPredictions](../src/Spatial/State/SpatialSimulationState.cs) |
| 候选（Occurrence candidate） | 提交给 Kernel 仲裁的一次可能发生项，携带稳定身份、到期时刻和规则私有数据。是预测对调度器的投影，不是事实。 | [OccurrenceCandidate](../src/Kernel/Scheduling/OccurrenceCandidate.cs)、`CandidateKey` |
| 候选枚举（Forecast） | 规则读取当前世界、产出候选的操作。不要把方法名 Forecast 理解成每次必须全量重算预测。 | [IOccurrenceRule.Forecast](../src/Kernel/Simulation/IOccurrenceRule.cs) |
| 发生项（Occurrence） | Kernel 一次选择并处理的单位；可能是自然过程到期，也可能是请求角色决策。一次发生项可产生多个业务事实。 | 候选经 Plan 形成转换；记录形式为 `OccurrenceEvent<TFact>`。明确“候选”或“已记录”阶段，避免统称已发生事件。 |
| 调度与仲裁（Scheduling / Arbitration） | 从规则提交的候选中选择下一项；同 Due 按世界 seed、稳定 key 确定性破平局。 | [OccurrenceScheduler](../src/Kernel/Scheduling/OccurrenceScheduler.cs)；Playground 的自然事件、活动完成、输入优先级由应用筛选实现。 |
| 随机机会（Random opportunity） | 已安排的抽样机会，候选本身不包含结果；被选中规划时才决定实际影响。机会序号与 seed 随业务状态保存，独立于调度破平局的 seed。 | `PlaygroundEnvironment.NextDue/Sequence/Seed`；`PlaygroundEnvironmentRules.Plan` 复用 [DeterministicRandom](../src/Kernel/Random/DeterministicRandom.cs)，结果随 Fact 记录，见[天气合同](design/survival-weather.md)。 |
| 预测所属单元（Prediction owner） | 负责某类预测的应用单元；更新时可以暂时没有候选。不是物品所有权。 | `PlaygroundPrediction.OwnerId`、`PlaygroundBinding.OwnerId`，例如 `actor:a:activity`。没有通用 Owner 接口。 |
| 依赖绑定（Binding） | 把事实种类和目标映射到可能受影响的预测单元，先粗筛再检查是否需要重算。 | `PlaygroundBinding`；当前运行时数据，不是持久化 delegate 或通用事件总线。 |
| 待重算集合（Dirty set） | 本次转换中可能失效的单元/空间范围的去重集合；在最终业务态上更新。 | Playground 的 owner 集合、Spatial 的 passage 集合；实现层概念。 |
| 全量重建（Rebuild） | 从业务真相和确定性规则重新构造完整派生缓存，用于初始状态、冷开及对照验证。 | `PlaygroundEngine.Rebuild`、`SpatialSimulation.Rebuild` |

“倒计时条（Countdown）”只作为过程的展示比喻：记录进度锚点或已决定的到期时刻，并维护预测，不逐 tick 递减计数。目前没有统一 `LocalTimeline` / `Countdown` 类型。

“回合（Turn）”不是 Kernel 的基础时间单位。新[信息继承方案](design/decision-context-handoff.md)使用上表的“决策回合”；讨论旧代码或其他机制时仍须明确是一次控制器调用、一次采集周期，还是一次发生项提交。世界并不等待所有角色各行动一次才统一推进。

## 事实、转换与恢复

| 推荐术语 | 含义与边界 | 当前代码映射 |
| --- | --- | --- |
| 转换规划（Plan） | 为选中的候选确定完整、有序的业务事实；尚未发布世界状态。可包含询问控制器，恢复时不能重新询问。此 Plan 不是控制器的长期目标树。 | `IOccurrenceRule.PlanSelectedAsync`、[TransitionDraft](../src/Kernel/Simulation/TransitionDraft.cs)、`PlaygroundPlanning` |
| 业务事实（Fact） | 已确定的具体效果数据，可依次应用到业务状态；规划草稿中的 Fact 在 E 发布前还不是已记录历史。 | [PlaygroundFact](../src/Playground/PlaygroundFact.cs)、[GraphSpatialFact](../src/Spatial/Facts/GraphSpatialFact.cs) |
| 事实折叠（Fold） | 按顺序应用业务事实，得到新的业务态；不重新决定事实。 | `PlaygroundEngine.Fold`、`GraphSpatialReducer` |
| 转换补全（Complete） | Fold 后纯粹维护派生缓存，返回准备好的运行时世界；不增加业务事实、不调用控制器。不是“角色活动完成”的同义词。 | Kernel 的 complete 回调、`PlaygroundEngine.Complete`、`SpatialSimulation.Complete` |
| 状态转换（Transition） | 从一个已完成边界推进到下一个边界，包含该发生项的有序事实、业务态和缓存准备、校验及发布。多个子对象的变化可属于同一次转换。 | [SimulationKernel](../src/Kernel/Simulation/SimulationKernel.cs) |
| 业务状态（Business state） | 必须保存的世界真相和已作出的决定；包括活动参数、感知、消耗记录等，不包含纯派生预测。 | `PlaygroundBusinessState`、`GraphSpatialState` |
| 运行时世界（Runtime world） | Kernel 操作的完整就绪视图，组合业务状态与派生缓存。不能把整个 wrapper 都当作存档根。 | `PlaygroundWorld`、`SpatialSimulationState` |
| 事件记录（Event，E） | 一次发生项的原因 key、目标逻辑时点和有序业务事实的记录。不是一条 UI 消息。 | [OccurrenceEvent](../src/Kernel/Journal/OccurrenceEvent.cs) |
| 状态发布（State publication，S） | 发布该边界的业务状态与 Kernel 游标。E/S 是两个发布边界，不能称为一次不可分割的磁盘事务。 | [PlaygroundDurableRoot](../src/Playground/Persistence/PlaygroundDurableRoot.cs)、`PlaygroundDurableHistory` |
| Kernel 游标（Kernel cursor） | 标明已完成的世界版本、逻辑时点与原因，和业务状态一起保存。不同于 Console 的角色服务游标。 | [KernelCursor](../src/Kernel/Simulation/KernelCursor.cs) |
| 待完成事件（Pending E） | E 已发布，而对应 S 尚未完成发布的记录。兼容语义下按记录 Fold、Complete、Validate，再发布 S；跨游戏修订由应用协调转换 State/E，不重新仲裁、Plan 或询问控制器。 | [E/S 合同](design/durablegraph-occurrence-persistence.md)、[游戏修订决策](design/game-initialization-and-revision.md)；加载仍保留 pending，正常恢复负责完成。 |
| 单根发布（Single-root publication） | 在同一个 S 根下保存业务对象图及游标，使背包、资源、角色活动等共同组成一致边界。 | 当前 Playground 的 durable root；不表示 .NET Task、调用栈或外部 LLM 调用也自动持久化。 |

单写“事件”时容易混淆：说明是在讲候选发生项、业务事实、E 记录、依赖绑定所匹配的事实种类，还是给角色的感知通知。它们可以相关，但不是同一个对象。

## 初始化与版本修订

| 推荐术语 | 含义与边界 | 当前代码映射 / 状态 |
| --- | --- | --- |
| 新游戏工厂（New-game factory） | 组装当前规则下的初始领域对象图及起始游标；可以组合程序化生成器，不负责升级旧世界。 | 山野镇 `PlaygroundGameFactory.Create(definition)` / `CreateLivingWorld`；旧领域夹具保留 `CreateBatch` / `CreatePlayable`。发布由 `PlaygroundDurableHistory.Create` 负责。 |
| 有效世界定义（Effective world definition） | 已归一化初始覆盖参数的不可变完整内容，包含地图、居民、资源、经济与身体/天气。随世界保存，运行中不能替换。 | `PlaygroundWorldDefinition`；`PlaygroundWorldDefinitions.Named` 提供预设，`Change` 建立新定义；派生图缓存不持久化。 |
| 内容身份（Content identity） | ContentId 与规范 SHA-256 指纹共同标识有效内容；同名内容字段变化也改变身份。独立于模拟位置与算法修订。 | S 保存定义与指纹，`PlaygroundDurableEvent` 保存规划前态身份；冷开、发布和分叉核验，见[内容合同](codex-goals/living-world/content.md)。 |
| 游戏修订号（GameRevision） | State/Event 各自表示的游戏语义版本，使用同一编号体系；独立于 Schema 版本和模拟转换次数。 | 山野镇由 `PlaygroundGameRevision.Current` 定义，S/E 均携带修订；生产只支持当前版本，旧开发档不迁移。历史小镇版本见[归档](archive/microtown.md)。 |
| 游戏状态修订（Game revision） | 加载时协调升级 State 与可选 pending E 的内存表示；不执行事件、不推进游标、不写盘，pending 工作留给正常流程。 | `PlaygroundGameRevision.UpgradeLoadedBoundary`；[实施合同](design/game-initialization-and-revision.md)，生产只接受当前版本，跨版本政策由夹具验证。 |
| 模拟世界版本（WorldVersion） | 模拟谱系及已完成转换次数，标识模拟位置；不表示游戏内容或规则修订。 | [WorldVersion](../src/Kernel/Simulation/WorldVersion.cs) |


## 空间

| 推荐术语 | 含义与边界 | 当前代码映射 |
| --- | --- | --- |
| 空间实体（Spatial entity） | 拥有客观位置的实体；空间核心不要求其具备角色、背包或控制器。 | [SpatialEntity](../src/Spatial/State/GraphSpatialState.cs)、`EntityId` |
| 地点（Place） | 可区分的稳定语义地点，构成空间图的节点。不是 UI 面板或叙事场景对象。 | `PlaceId`、[GraphDefinition](../src/Spatial/Definitions/GraphDefinition.cs) |
| 通道（Passage） | 两个地点间有长度、有身份的连接，可分别控制从两端进入的权限。 | `PassageDefinition`、`PassageId`、`PassageEntryAccess` |
| 语义连接（Place relation） | 地点间独立于移动通道的无向关联，携带 64-bit 能力 Flags；首版只定义声音传播位，不表示事件种类。 | [PlaceRelationDefinition / PlaceRelationCapabilities](../src/Spatial/Definitions/PlaceRelationDefinition.cs)，由 `GraphDefinition.Relations` 保存。 |
| 一跳实体范围 | 起点及通过能力匹配连接直接相邻的地点内实体；不递归、不含途中实体、不筛选事件兴趣。 | [SpatialQueries.GetEntitiesInOneHop](../src/Spatial/Queries/SpatialQueries.cs)；游戏另行筛选接收角色，见[语义连接与大喊](design/spatial-relations-and-shout.md)。 |
| 客观位置（Spatial location） | 实体当前唯一的空间位置：在地点，或处于通道运动段。不同于角色对地图的知识。 | [SpatialLocation](../src/Spatial/State/SpatialLocation.cs)：`AtPlaceLocation` / `TraversingLocation` |
| 运动段（Movement segment） | 用通道、锚点位置/时间、目标端点、速度快照和到达时刻描述的当前运动；不逐帧更新坐标。 | `TraversingLocation`、`SpatialEntity.MovementGeneration` |
| 路线（Route） | 从起点到目标的一组通道行程，不意味着已启动或已完成移动。 | [RouteLeg、RouteFound](../src/Spatial/Navigation/RouteModels.cs)、`SpatialNavigator` |
| 相遇 / 追及（Passage contact） | 当前两条通道运动段的交会，包含迎面相遇与同向追及。不是自动战斗、碰撞伤害或角色感知。 | [PassageContactKind、PassageContactKey](../src/Spatial/Contacts/PassageContact.cs)；预测为 `ContactPrediction`。 |
| 已消费交会（Consumed contact） | 指定当前运动段组合的交会已经处理，用来避免重复触发；这是发生过的历史，不能仅靠几何重建。 | `GraphSpatialState.ConsumedContacts`，与运行时 Contacts 预测缓存区分。 |
| 到达（Arrival） | 结束通道运动段、进入目标地点的空间转换。与通道内交会不同。 | `TraversingLocation.ArrivalDue`、空间事实与规则；到达向上取整，交会采用现有 floor 语义，详见[交会时刻说明](worksets/passage-contact-floor.md)。 |

山野镇默认场景使用 Spatial 的地点/通路/移动，并校验采集目标实际位于角色地点。移动中没有第二份游戏位置或到达期限。原采集与逃生 CLI 模式已撤下；旧初始化工厂暂作为领域测试夹具保留。

探索子图 `PlaygroundExpedition` 保存场景身份、Spatial 业务状态、结局和按角色/地点的最近观察 `PlaygroundVisit`。历史观察附时间，不等于远处资源的实时真相；观察记忆属于游戏状态，运行时 `PlaygroundObservation` 是其投影。

## 采集实例与层次边界

| 推荐术语 | 含义与边界 | 当前代码映射 / 状态 |
| --- | --- | --- |
| 资源点（Resource node） | 储存可采份额、按条件再生并定义产出的世界对象；不等同于产出物品。 | [PlaygroundResource](../src/Playground/PlaygroundResource.cs)：`Id` 与 `ItemId` 分开。 |
| 库存 / 余量（Stock）与容量（Capacity） | 资源点当前可被采走的份数与最大份数。 | `PlaygroundResource.Stock` / `Capacity` |
| 再生（Regeneration） | 累积有效生长时间后补充资源点库存；与角色采集过程独立。 | `RegenerationTicks`、`ActiveSince`、`AccumulatedTicks` |
| 采集批次（Gathering batch） | 一次接受的有限成功目标，内部可以连续执行多个周期；`all` 固定为接受时库存快照，不是预留。 | Gather 种类的 `PlaygroundActivity`：`TargetCount`、`CompletedCount`、`SelectedAll` |
| 采集周期（Gathering cycle） | 尝试取得一份资源的耗时过程；到期成功才扣一份库存并给一件物品，失败可结束批次。 | `PlaygroundActivity.DurationTicks`、`Due`、`CycleIndex`；成功事件 `GatherYielded`。 |
| 物品库存（Item inventory）与背包（Backpack） | 物品 ID 到数量的不可变快照；背包是角色对它的一种用途，独立于资源点余量。 | `DramaBoard.Inventory.ItemInventory`；角色属性仍为 `PlaygroundCharacter.Backpack`。 |
| 总件数容量（Unit capacity） | 所有物品数量之和的可选上限，不是物品种类数或资源点容量；null 表示无玩法限制，0 表示零容量。 | `ItemInventory.UnitCapacity` 持久化；`TotalCount` 派生；默认 demo 不设置上限。 |
| 库存接纳（Inventory admission） | 当前快照能否接纳单份物品的局部查询；不预留，成功/失败如何改变活动由游戏决定。 | `ItemInventory.CheckAdd` / `InventoryAdmission`；单项表示上限优先于总容量上限。 |
| 活动结算空间（Activity settlement capacity） | 在当前库存之外，分别保证活动完成和取消退料两种互斥结果可表示、可接纳；不是把两种未来结果相加预留。 | 山野镇 `PlaygroundEconomyRules.CanSettleActivity` 配合购买/赠物接纳、交易双方最终库存、生产开始及世界校验使用；在制品由 `PlaygroundActivity.Production` 订单快照保存。 |
| 内核（Kernel） | 通用发生项调度、转换与发布协议。 | `src/Kernel` |
| 空间层（Spatial） | 客观图空间、运动及交会规则。 | `src/Spatial` |
| 宿主（Host） | 驱动 Kernel 运行的程序层职责，不等同于角色控制器。 | [SimulationHost](../src/Host/SimulationHost.cs)、应用 Console 入口 |
| 规则包（Rule suite） | 可组合的领域规则集合，如采集、战斗等；提取需具体用例证据，不强制等待多个完整游戏。不是 Kernel 的 `SimulationRules` 配置对象。 | 当前规则在 Playground 应用层，尚无通用 RuleSuite 框架；方向见[分层设计](design/layering-and-composition.md)。 |

采集规则详见[多人有限批次采集](design/resource-cycle-multiplayer-gathering.md)。新增术语优先解释真实代码/业务边界，不为凑齐名词而建立抽象类型。

## 模块与游戏组合

| 推荐术语 | 含义与边界 | 当前代码映射 / 状态 |
| --- | --- | --- |
| 领域模块（Domain module） | 具有明确状态、局部规则及协作合同的一项领域能力；不要求独立程序集、统一组件基类或独立提交。 | Spatial、Inventory 已独立；Playground 的资源、活动和跨模块采集编排仍归游戏。 |
| 组件（Component） | 模块内可组合的对象或能力的宽泛称呼；使用时说明具体职责，不默认指 ECS 或某个 `IComponent`。 | 尚无通用组件协议；设计讨论优先使用具体领域名称。 |
| 游戏组合（Game composition） | 选择并连接模块，协调跨模块行为、世界根、初始化及整体修订的职责。 | 当前分布在 `PlaygroundGameFactory`、`PlaygroundPlanning`、`PlaygroundEngine` 和游戏持久化入口，不对应一个新接口。 |
| 扩展内容 / 模组（Mod） | 内容、规则或完整游戏的一种来源，按其职责接入，并非固定的最上层。 | 方向为源码引用、共同编译与显式接线；尚无 mod 加载器或稳定扩展 API。 |

职责与依赖决策以[分层与组合设计](design/layering-and-composition.md)为准；文件/程序集调整次序见[重构方案](implementation/modularization-plan.md)。
