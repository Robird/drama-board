# 源码基线与真实 Playground 实验接缝

2026-11-03。调查基线为 DramaBoard `83dcc9e`、相邻 Completion `d1dd88d`。本报告完成源码复核和可实施接缝设计；**尚未实现这些接缝、运行构建/实验或进行在线调用**。其他工作包正在建立的实验工程不作为本报告的运行证据。

任务与验收归[研究入口](../README.md)，实验计量归[实验协议](../experiment-protocol.md)。当前用户已允许按需真实调用，不再采用原方案 24 次的研究总硬上限；具体批次仍应先定义用途、次数和停止条件，由主线程集中协调，不能由子代理各自无限调用。

## 结论：先用请求 gate，避免复制真实请求构造逻辑

最小路径是研究侧的 **ICompletionClient gate + 实际控制器 + 单一真实 Kernel**：

1. 从真实完成 S 为两个角色各自准备预计 Context。
2. 每个预计 Context 使用独立的普通 `LlmCharacterController`，复用真实 renderer 和控制器内的私有 Guidance。研究 gate 在 `StreamCompletionAsync` 收到该控制器实际构造的 `CompletionRequest`。
3. 协调者并行发出请求、保存响应，但 gate **不向预计控制器返回响应**。预计控制器停在一次 await；因此不能提前解释工具、保存 feedback 或生成角色操作。
4. 真实 Kernel 按既有顺序打开 Context、选中角色。真实控制器经需求侧 gate 产生实际请求，协调者比较预计请求与实际请求。
5. 相等则将已保存结果交给**真实控制器**，仍由真实控制器执行原有 Completed/协议检查、feedback 循环和操作生成，再由 Kernel 重新校验和发布 E/S。不相等则废弃候选，为实际请求调用模型。
6. 取消并排空仅用于构造预计请求的控制器任务；它永远不获得候选响应。所有物理请求也要结束或记明未知，才能释放底层 client。

这里的 gate 是实验侧实现的现有公开接口，不是增加 Completion 公共 API。这个方案不要求抽出或复制 `GuidanceFor`、系统提示、工具 schema 或整个请求 builder，也不要求修改当前顺序控制器的 guard。

## 当前源码事实

| 入口 | 复核到的事实及约束 |
| --- | --- |
| [SimulationKernel](../../../../src/Kernel/Simulation/SimulationKernel.cs) `StepCoreAsync`、`BeginOperation` | await Plan 后才 Fold/Complete/Validate、发布 E/S；同 lineage 禁止并发 Step。E 发布后必须完成 S，pending 恢复不重新 Plan/问模型。 |
| [PlaygroundEngine](../../../../src/Playground/PlaygroundEngine.cs) `PlaygroundRule.Forecast/PlanSelectedAsync` | 同 tick 有种类优先级；角色服务按 LastServedActorId 轮转。Context 未开时先提交 `DecisionContextOpened`，后续 Step 才调用控制器。 |
| [PlaygroundInteractionRules](../../../../src/Playground/PlaygroundInteractionRules.cs) `OpenContext` | 推进 DecisionGeneration、维护报价交付/回收，并可能更新其他开启中的观察；不能直接在真实根提前开启。 |
| [PlaygroundTradeState](../../../../src/Playground/PlaygroundTradeState.cs) `OpenContext` | 返回新交易状态，不等于无领域效果；某角色开窗可能改变另一角色材料。 |
| [PlaygroundEngine](../../../../src/Playground/PlaygroundEngine.cs) `Fold/Complete/Validate` | 已有纯的业务应用、派生预测维护和完整校验路径；可用于独立 scratch，不需要保存假 S。 |
| [PlaygroundDecisionContext](../../../../src/Playground/PlaygroundObservation.cs) `Create` | Observation 取保存的 Context；Check/Warning 闭包捕获构造时 world。真实采用必须重新使用实际世界的 Context/校验。 |
| [CharacterContextRenderer](../../../../src/Playground.Cli/CharacterContextRenderer.cs) `RenderFull`、[DecisionPlayLayout](../../../../src/Playground.Cli/DecisionPlayLayout.cs) | 当前完整模型材料入口，均为 CLI internal；预计与实际应使用同一份代码。 |
| [LlmCharacterController](../../../../src/Playground.Completion/LlmCharacterController.cs) `ChooseCommandAsync` | 内部以 renderer、私有 `GuidanceFor` 构造 `LlmDecisionInput`，然后进入共享工具循环；单实例要求顺序调用。 |
| [LlmDecisionLoop](../../../../src/CharacterControl.Completion/LlmDecisionLoop.cs) `ChooseCommandAsync` | 在调用 client 前已经构成真实请求；接收结果后才执行 Completed/协议判断、反馈或生成 OperationRecordId。gate 可以在这个既有 await 处截住。 |
| [PlaygroundBranchBoundary](../../../../src/Playground/Persistence/PlaygroundBranchBoundary.cs) `DescribeDecisionBoundary` | 真实边界只接受当前下一个角色且 Context 已开启。预计 Context 没有真实 StateCommitId，不能伪装成普通决定前边界。 |
| [LlmConnectionPool](../../../../src/CharacterControl.Completion/LlmConnectionPool.cs) `GetAsync` | gate 保护按连接懒创建/借用，不围住实际 Completion 调用。配置相同的角色可能借用同一 client。 |

另直接检查了相邻 Completion 的 `src/Completion.Abstractions/ICompletionClient.cs`：当前接口明确允许同一实例收到重叠调用，要求隔离 request、observer、parser、result 和 cancellation，且 dispose 应在调用全部结束后。实现允许内部限流/串行，所以接口并发合同不等于真实端点吞吐保证。

`CompletionRequest` 当前只保存 ModelId、PromptPrefix 和 TailMessages。`CompletionInvocationOptions` 是另一个运行偏好合同，默认可请求 PlainText；连接也会提供有效选项。该 API 明确没有调用方输出 token 上限，实验不得发明本地输出截断字段。若本次统一使用默认 invocation options，manifest 应显式记录这一点。

## 从完成 S 构造预计 Context

首版限于**同一 ModelTime 上已需要决策的角色**。协调者先检查真实 Kernel 没有 pending E，并等待 `NextDecisionActor(world, cursor)` 返回角色，避开本应先执行的同 tick 自然事件。它只说明当前存在决策前沿，不证明其他角色的预计输入安全。

每个角色从同一来源 world **独立**准备，不能把“预计甲开窗、预计乙开窗”的序列当作已经发生：

```text
Preview(sourceWorld, actorId):
    要求 actor 活跃且 NeedsDecision(sourceWorld, actorId)
    若已有 Open Context：返回 Create(sourceWorld, actorId)
    fact = PlaygroundInteractionRules.OpenContext(sourceWorld, actorId)
    instant = (sourceWorld.Time, scratch-local ordinal 0)
    folded = PlaygroundEngine.Fold(sourceWorld, instant, fact)
    scratch = PlaygroundEngine.Complete(sourceWorld, instant, [fact], folded)
    PlaygroundEngine.Validate(scratch, sourceWorld.Time)
    返回 PlaygroundDecisionContext.Create(scratch, actorId)
```

scratch-local ordinal 只满足纯应用函数的时间参数，不是 Kernel cursor，也不是预计的真实提交次序。OpenContext 不包含空间移动事实；若未来实现开始读取这个 ordinal，首版接缝必须重新审核，不能继续假定它与请求无关。

这个过程**会形成假设性的领域 fact 和 scratch 状态，但不会形成已发生的世界事实**：不调用任何 History.Commit、不安装 scratch、不推进真实 cursor、不投递真实经历、不写普通 operation-received，不调用反馈或 provider。最终只把 projected Context 及其来源/假设交给研究协调者，临时 fact 不是恢复材料。

当前业务路径以新对象替换保存的子图。实现时仍需用来源内容/引用与真正存档字节的前后对照验证不变性，不能仅凭命名为 Preview 就假定无副作用。准备工作在协调者线程完成，worker 不访问真实 world 或其委托。

真实执行甲后，乙的 Context 可能因为甲开窗清理报价、甲发言或移动而变化。这是合法的预测失配，实际请求比较应废弃旧候选；不会把“纯 scratch 可构造”升级为安全窗口证明。

首版不跨 ModelTime 直接改 Time 字段造未来观察。跨时刻预计输入需要之后研究明确机械事件投影与不确定外部输入，另有测试后再扩展。

## 请求 gate 的两种用途与副作用隔离

### 预计请求捕获

预计控制器使用真实 `CharacterContextRenderer.RenderFull(state, new DecisionPlayLayout())`，模型/连接与真实角色绑定相同，options 和反馈工具可用性也相同。若真实控制器提供 feedback，预计控制器应提供**永远不得被调用的 fail-closed sentinel**，不能传 null 改变提示或工具集合，也不能借用真实保存回调。

gate 发布请求后返回一个未完成的 `Task<CompletionResult>`。协调者得到实际请求后，可以在底层 client 上并行发送；模型响应保存到候选记录中，始终不完成这个预计 gate。采用/丢弃时用专属 CTS 取消预计控制器并观察任务结束。这样不依赖异常中断的“抓包技巧”，也没有伪造 feedback 成功结果。

不得把取消预计控制器产生的本地 cancelled 当作底层 provider 结果。底层可能已经 Completed；候选可以 discarded，同时物理请求是 completed 或 unknown。gate 的 cancellation registration 和 TaskCompletionSource 必须正确释放，异步 continuation 不在持锁路径内执行。

### 实际需求与结果交付

研究包装控制器接到真实 Kernel 的 Context 后，使用普通实际 `LlmCharacterController`。需求 gate 收到新构成的请求时，协调者检查：

- 同一实验分支、角色控制配置与候选代次；预计目标仍对应这个实际角色服务。
- ModelId、SystemPrompt、输出合同、Shared 和 Tail 的结构化原值相同；连接/协议/有效配置也相同。
- 原生 reasoning 或工具回执如参与 Tail，保留其身份。初版只提前第一条请求，正常 Tail 续调用走需求路径。
- 源 S 只是来源，不要求全局版本仍相同；实际决定前边界在此时重新捕获。

相等才允许将缓存结果交给**实际需求 gate**。非 Completed、协议错误或反馈工具输出仍由真实循环按现有合同处理，不直接转换成成功 act。后续 feedback/纠正请求照常构造并发出，不使用未知的假回执提前推理。

不相等时废弃候选，然后只为当前实际请求发起新的调用。模型返回快慢不决定真实角色次序。需求控制器返回操作后，Kernel 使用实际 `decision.Check`、交易全量检查及原有规划/发布路径；不使用 scratch 中捕获的 Check/Warning。

本实验中的预备控制器与实际控制器都会有本地 CallsMade，但**物理调用数只能在底层调用入口统一计数**。缓存交付不是第二次 provider 调用，预计控制器的局部计数也不能当第二笔费用。物理预算、最大在途与实际控制器自己的 service/protocol 限制分别记录。

## 本仓最小改动与工程归属

建议原型继续放在 `experiments/PredictiveTimeParallelism/`，引用现有 Playground.Cli 项目即可取得同一源码图，不另建模拟 Kernel。

| 文件/区域 | 最小拟议变更 |
| --- | --- |
| `src/Playground/AssemblyInfo.cs` | 仅向本仓实验程序集 `DramaBoard.PredictiveTimeParallelism` 开放 internal，以调用已有 OpenContext 和 NextDecisionActor。 |
| `src/Playground.Cli/Playground.Cli.csproj` | 同样的本仓 IVT，使实验复用 renderer/DecisionPlayLayout；不把呈现类型改 public。 |
| 实验 `.csproj` | 加入对 Playground.Cli 的 ProjectReference。AssemblyName 由根 Directory.Build.props 决定；若以后显式改名，应同步 friend 名。 |
| 实验 `PlaygroundPreview` helper | 封装前述纯 scratch 准备，只返回预计输入与来源假设；不修改领域生产算法。 |
| 实验 request gate / coordinator | 捕获真实请求、统一 dispatch、缓存/比较/取消/排空、物理调用账本与候选生命周期；不拥有底层借用 client 的提前释放权。 |
| 实验实际应用 runner | 单 Kernel、原有控制器与 parser；配对初态/存档、采用校验与轨迹比较。 |

这条路径可只新增两个**仓内** friend 声明，不需要改私有 Guidance、不需要 shared loop 新 internal builder，也不需要向相邻 Completion/DurableGraph 增加 IVT 或扩大 public API。若实现 gate 后发现重复构造开销显著，再用证据决定是否提取共享纯 builder，不作为首版前置工作。

不改默认 CLI 启动、玩法、角色人数、Schema、持久化或默认控制器策略。独立 runner 使用实际 Playground 材料/命令/Kernel，足以检验应用接缝；它不自动证明完整生产宿主日志/Console 已支持并行。后续如接入现有 PlaygroundHost，仍须对宿主 writer、暂停与诊断逐项验证。

## 日志与身份

预计请求不得调用普通 HostSession.Trace 的真实边界捕获。实验时间线记录 SourceStateCommitId、CandidateId、预计 actor/context 和物理 RequestId；实际采用记录新的真实边界、实际请求身份和对应 CandidateId。诊断 Sequence 不冒充 Kernel 逻辑次序。

预计 controller 的局部 trace 可以不接普通日志。物理请求/结果由协调者串行记录，实际角色操作仍由唯一世界 E/S 证明。缓存交付需标明来源，不能重复累计 usage，也不能把一次物理响应写成两次在线成功。研究统计文件是诊断工件，不是第二份恢复日志。

当前 `LlmDecisionLoop` 会随机生成宿主 OperationRecordId。离线配对比较要么增加最小的可控身份来源，要么在验证器中建立受约束的一一对应：按同一 actor/context/operation 位置配对新生操作身份，并检查它在事实和 Context 中所有引用保持一致。只允许声明过的宿主新生身份对应，不更改实际请求、世界实体或模型可见的工具回执身份。不能直接要求两个随机 GUID 的字节相同，也不能粗略删掉所有 ID 后比较。

## 最小测试与实际验证顺序

以下均为待实施检查，不是本报告已运行结果：

1. **预计输入忠实性**：两个角色同 tick、初始无 Context；分别 preview，真实 Kernel 实际开启后捕获请求，验证独立情况下逐字段一致。先为单角色验证 builder 路径，再验证远处双角色。
2. **source 不变**：preview 前后真实 world 内容/引用、cursor、StateJournal 分支 head 和文件字节不变；scratch 的交易交付变化与真实开启效果相同，但不进入真实世界。
3. **预计阶段绝无工具副作用**：底层返回 feedback 或 act 后，预计 controller 仍未得到结果；sentinel/反馈 journal/操作 Receive 次数均为零。实际采用后 feedback 才保存，续调用含真实回执。
4. **并行及顺序**：TaskCompletionSource 控制两个物理请求均已启动，再逆序完成；真实提交顺序与 serial 相同。独立取消一个请求不取消另一请求，所有调用结束后才 dispose。
5. **失配反例**：同地 a 的行动、shout、OpenContext 报价维护改变 b 的实际输入；不能因为 b 的旧命令仍合法就采用。需求路径重新调用，完整轨迹与固定响应 serial 对照一致。
6. **错误与停止**：过期候选失败不污染实际循环的连续协议失败计数；实际采用的错误按原合同暂停。预算竞态、取消/排空、停止后不采用、真实 E 已发布后的 pending 冷开均验证。
7. **实存档与真实服务**：新建实验 home/store，保存可复用 prelude 的完成 S；对照独立副本，记录所有依赖 commit/dirty 状态和内容指纹。先固定响应通过，再固定真实输入小批量探测端点，然后短应用 serial/候选运行。真实随机响应轨迹不同只报告条件化现象，不能称为等价加速比。

2–3 个真实角色已经足够：可以用普通控制器脚本把 a/b 从 home 移到不同地点，再安排同 tick 的实际决策。选地点时检查声音连接而不是只看地点 ID，例如 home/market 仍可能有声音关系。也可显式使用实验内容定义设定不同出生点；该变体必须固定内容指纹，且 serial/候选从同一变体开始，不能伪称默认出生内容。

真实模型若产生 handoff/goal/feedback 或跨角色交互， runner 应继续走原有实际循环、校验失配或按预定有限工作结束，不预设模型一定返回 wait。在线配置、实际响应、usage/未知项、拒绝与废弃均留证，不用旧日志冒充本研究调用。

## 本报告尚未证明什么

未运行任何代码，故尚不能宣称 scratch 构造在所有业务状态都通过验证、gate 已无泄漏、provider 的实际吞吐有收益，或 C2/C6 已完成。此接缝只提供可执行的 R0→R2/R4 路线：先用真实源码构造/比较请求，再用实际串行世界证明采用行为；有限时域影响估计、安全窗口与规则约束仍由其他研究实验独立给证据。
