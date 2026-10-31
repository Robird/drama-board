# 小镇 LLM CharacterController

`TownLlmCharacterController` 是 [共享 Completion 循环](../CharacterControl.Completion/README.md) 的薄游戏适配器。它只接受冻结的 `TownObservation`，用 `TownTextRenderer` 生成本人材料、附加小镇行为指引，返回 `TownControllerOperation`；不接受完整 `TownWorld`，也不直接执行游戏操作。

构造函数借用 `ICompletionClient`，接受模型名、`LlmControllerOptions`、共享 `LlmControllerTrace` 回调、可选持久反馈 sink 和可选观察渲染函数。`ChooseCommandAsync(observation, cancellationToken)` 保留 provider 的 `ToolCallId` 与共享循环分配的 `OperationRecordId`。宿主随后通过游戏 parser、校验与 E/S 发布确认成功或拒绝；模型提交不等于动作成功。

同一个实例可以依次服务 a、b、c，`CallsMade` 是共享调用预算，`FeedbackEnabled` 表示已配置反馈 sink。并发调用直接拒绝，串行保护覆盖渲染与工具循环；已取消请求在渲染前退出。非开放决策、已失败居民及结束世界均不能送入模型。反馈 sink 必须持久记录后才返回回执，原生 assistant/tool Tail 只用于当前服务内的后续调用，不带入另一个居民或下一次决策。

默认材料包括本人库存、钱、饱食、Goals、最近操作回执及最近本人经历，同地居民的公开活动，以及路线、配方和有时效边界的市场信息。其他居民的私有 Goals、库存与钱不会进入观察。最近回执/经历不是完整交互历史；长期意图目前由各角色自己的 Goals 保存，控制器不私藏跨决策记忆。

行为指引对应当前已实现的吃饭、生产、买卖、移动、等待和目标管理。当前没有居民对话、赠与、讨价还价、活动取消或远程交易。自由文本不会成为对话或世界事实。合法耗时操作也可能使居民在完成前饿死，指引要求结合饱食预测规划。

[Console 宿主](../Microtown.Cli/README.md) 负责连接配置、角色绑定、发布后的决策调用、预算、Gym 与退出处理。宿主可通过自定义渲染函数添加开发者实验指令；它属于显式实验介入，不是角色自然形成的目标、记忆或世界事实。

[测试](../../tests/Microtown.Completion.Tests/TownLlmCharacterControllerTests.cs) 使用真实共享循环与 fake completion，覆盖三居民材料/Tail 隔离、反馈、预算、取消、串行渲染、操作身份及游戏拒绝的下一次呈现。这些测试不等于真实模型试玩或人机协作验收。
