# 小镇 LLM CharacterController

`TownLlmCharacterController` 是 [共享 Completion 循环](../CharacterControl.Completion/README.md) 的薄游戏适配器。它只接受冻结的 `TownObservation`，用 `TownTextRenderer` 生成本人材料、附加小镇行为指引，返回 `TownControllerOperation`；不接受完整 `TownWorld`，也不直接执行游戏操作。

构造函数借用 `ICompletionClient`，接受模型名、`LlmControllerOptions`、共享 `LlmControllerTrace` 回调、可选持久反馈 sink 和可选观察渲染函数。`ChooseCommandAsync(observation, cancellationToken)` 保留 provider 的 `ToolCallId` 与共享循环分配的 `OperationRecordId`。宿主随后通过游戏 parser、校验与 E/S 发布确认成功或拒绝；模型提交不等于动作成功。

同一个实例可以依次服务 a、b、c，`CallsMade` 是共享调用预算，`FeedbackEnabled` 表示已配置反馈 sink。并发调用直接拒绝，串行保护覆盖渲染与工具循环；已取消请求在渲染前退出。非开放决策、已失败居民及结束世界均不能送入模型。反馈 sink 必须持久记录后才返回回执，原生 assistant/tool Tail 只用于当前服务内的后续调用，不带入另一个居民或下一次决策。

默认材料包括本人库存、钱、饱食、Goals、最近操作回执、最近本人经历及有界的本人收发交互，同地居民的公开活动，以及路线、配方和有时效边界的市场信息。其他居民的私有 Goals、库存与钱不会进入观察。交互按当前决策标出新近交付，居民言语明确标为发言而非世界规则，文本转义以免伪装成新的 UI 章节。有限记录不是完整交互历史；长期意图与已接受的约定由各角色自己的 Goals 保存，控制器不私藏跨决策记忆。

行为指引对应吃饭、生产、买卖、移动、等待、目标管理，以及同地 `say`、物品/钱 `give`。自由文本不会送达为对话，只有成功的 `say` 会留下双方记录。赠与范围只表达本人能提出的数量；接受时仍检查目标条件，不利用动作列表暴露对方余量或容量。

显式交换使用 `offer <resident> <give-asset> <give-count> <want-asset> <want-count> <hours>`，接收者 `accept/reject <offer-id>`，提出者 `withdraw <offer-id>`。报价不预留资产，期限 1..24 整小时；接受需在到期前同地，重验双方并原子结算。本人参与的有效报价完整呈现，终态用独立有界记录保留至纳入双方决策材料后再回收，失败者免交付；不依赖短聊天历史。UI 仅做本人/公开条件校验，将接受标为尝试，不泄露对方库存、余额、容量或交付游标。没有自动撮合、库存托管或远程交易。

等待/生产途中收到社交通知可开启响应，期间即时操作不取消活动或改变原定完工时间；`continue` 释放响应并继续，`cancel` 停止活动，生产退回全部预留原料，不产生部分产出。移动不能取消。合法耗时操作仍可能使居民在完成前饿死；UI 给出按当前饱食速率、不额外进食或干预的完工预览，并提醒饿死与完工同刻时失败优先。模型不能依赖其他居民及时打断自己来进食。

报价通知同样可以开启忙碌响应，即时交易保留原活动。`continue` 不代表接受或拒绝报价，取消活动也不会撤回报价；必须使用对应的交易命令。反复展示的已接受终态表示交易已经发生，不应再次送出同样资产。

[Console 宿主](../Microtown.Cli/README.md) 负责连接配置、角色绑定、发布后的决策调用、预算、Gym 与退出处理。宿主可通过自定义渲染函数添加开发者实验指令；它属于显式实验介入，不是角色自然形成的目标、记忆或世界事实。

[测试](../../tests/Microtown.Completion.Tests/TownLlmCharacterControllerTests.cs) 使用真实共享循环与 fake completion，覆盖三居民材料/Tail 隔离、反馈、预算、取消、串行渲染、操作身份及游戏拒绝的下一次呈现。这些测试不等于真实模型试玩或人机协作验收。

本版领域为 GameRevision 3，Business/EventKind/Fact/Experience Schema 2；存档边界见[领域说明](../Microtown/README.md)。规则和恢复证据见[报价交易验收](../../docs/experiments/microtown-trade-acceptance.md)；当前真实三居民 30h 经济日、精确反馈分叉和有引导的成交见[试玩报告](../../docs/experiments/microtown-playtest.md)。没有真实模型的全命令覆盖或自发合作结论。
