# 设计决策：共享无状态角色控制器与游戏维护的动态上下文

状态：用户已采纳的架构方向；控制器符号重命名已完成，不表示 LLM、角色记忆或新的上下文 API 已实现。

## 决策与动机

CharacterController（亦称 Player）负责为指定角色产生决策，**不拥有角色跨次决策的隐式记忆**。同一个控制器实例可以先后服务多个 Character；角色的连续性由游戏维护的经历、目标、笔记、人格设定等显式状态提供，不依赖某个控制器实例或某条长期聊天会话。

每次角色需要决策时，游戏构造该角色的动态上下文，通过 Doc UI 和可用工具交给控制器。对 LLM，可以每次创建新的调用上下文，在本次局部 tool-loop 中探索信息并产生合法行动。重要经验应写回显式状态，再用于后续上下文构造。

这样可以复用控制器及 API 客户端，减少每角色初始化和生命周期管理，也能替换模型实现而保留角色记忆。代价是游戏必须承担记忆保留、召回、上下文组织及权限边界的质量；“无状态”本身不保证决策质量，也不保证 Doc UI 已经足够完整。

控制器共享不意味着多个角色共享知识、人格或目标，也不要求整个游戏只能有一个控制器。不同角色如何绑定不同配置或实现，留到实际接入时确定。

## 三层状态归属

| 层次 | 内容 | 归属与约束 |
| --- | --- | --- |
| 跨决策的角色状态 | 经历、目标、笔记、关系认知、人格设定，以及其他影响后续行为的信息 | 游戏维护，按角色隔离；可采用世界发布根下独立的子对象图。控制器不能把唯一副本藏在实例字段或服务端聊天会话中。具体 schema 尚未设计。 |
| 本次决策的局部会话 | 当前工具调用历史、临时工作上下文、尚未提交的行动选择 | 归本次调用所有，不泄漏到下一个角色或下一次决策。需要中途恢复时，再把必要会话状态显式保存；不要求一开始就细粒度持久化。 |
| 可共享的运行设施 | API 客户端、模型配置、连接池等 | 可以由控制器实例共享，但不得夹带隐式角色聊天历史。配置应作为明确输入或依赖；控制器替换不应带走角色唯一的记忆。 |

这里的“无状态”指不私藏跨决策的角色语义状态，不禁止局部变量、客户端资源、测试计数器或显式会话对象。对人类控制器，这是软件状态归属约定，不声称能清除操作者本人的记忆。

## 两个必要边界

### 角色知识与世界真相分开

游戏可依据世界真相构造准确的感知和因果关联，但交给控制器的是该角色按规则可知的信息。未被感知的事件、其他角色的私有笔记和隐藏状态不能因为控制器共享而自动可见。

角色自己的猜测、错误记忆或不确定判断也属于角色状态；不能在上下文构造时悄悄用全知真相替换。若标注事件与状态的因果联系，联系本身也需符合已选定的感知规则。

Doc UI 与工具必须遵守同一可见性边界，不能只裁剪文本，却允许展开工具读取全部世界。当前已收敛为指定角色的 `CycleDecisionContext`：观察结果与只读命令校验；后续工具协议仍按实际需求设计。

### 无状态不等于确定性或无副作用

LLM 调用仍是外部操作，重试可能产生不同结果。尚未记录的完整决定或不完整输出遇到中断时，可能需要重新调用；本决策不提供外部调用恰好一次保证。

已有 E/S 合同继续适用：决定一旦进入已发布的 E，恢复只应用已记录事实、补全派生缓存、校验并发布 S，不重新仲裁、Plan 或询问控制器。控制器无状态不会改变这个边界。

未来工具若编辑笔记或产生其他持久效果，应通过游戏的显式状态更新与发布路径，不把副作用藏在控制器内部。具体工具提交粒度及中断恢复协议，随工具切片设计，不因本决策自动确定。

## 一次决策的概念流程

1. 世界产生某角色的决策请求；当前 ResourceCycle 在服务输入期间暂停模型时间。
2. 游戏根据角色身份、当前可见状态、相关经历、目标和笔记构造上下文，并绑定该角色可用的工具。
3. 共享控制器执行本次局部交互，返回行动意图。查询、非法输入不等于接受行动。
4. 游戏校验并按规则形成具体事实，沿现有转换与发布协议提交。
5. 本次临时会话结束；需要保留的信息应已进入显式状态，后续决策重新构造上下文。

以上是职责划分，不是冻结的新 API 签名。行动结果后追加一次记笔记机会仍是可选后续设计。共享控制器也不自动意味着并发安全：当前 Console 串行服务，未来若并行调用，局部会话隔离、工具写入顺序和发布仲裁需要单独验证。

## 当前实现与后续范围

- [ICycleCharacterController](../../src/ResourceCycle/CycleActions.cs) 的 `ChooseCommandAsync` 接收 [CycleDecisionContext](../../src/ResourceCycle/CycleObservation.cs)，返回 `CycleCommand`。上下文包含角色观察与纯 `Check`，不公开完整世界；一个实例仍可服务多个角色。该 API 边界不等于同进程恶意代码隔离。
- [ConsoleCharacterController](../../src/ResourceCycle.Cli/PlayConsole.cs) 没有实例字段，单个实例轮流服务三个角色；每次调用渲染该角色界面并等待合法输入。
- [CycleEngine](../../src/ResourceCycle/CycleEngine.cs) 的请求调度与角色 Activity 独立；唤醒不取消活动。共享控制器沿用现有服务轮转，不修改抢资源仲裁。
- 当前没有 LLM 接入、持久笔记、角色经历召回或独立 DecisionContext 类型。归档 Player 实现是技术储备，不因本决策自动恢复使用。

后续实现可用以下行为验收本决策：A/B 角色交替使用同一控制器不串记忆；替换控制器实例后显式角色记忆仍可用；文本与工具都不泄漏未授权信息；中断后的 pending E 恢复不再次调用控制器。更换 LLM 后得到完全相同的行动不属于验收要求。

## 已完成的 IDE Symbol Rename 映射

以下保留用户已完成的旧→新符号映射。当前代码使用右列名称；保留 `Cycle` 前缀，接口仍属应用实例。本次改名没有同时修改参数类型、状态归属或行为。

| 声明位置 | 旧标识符 | 当前标识符 |
| --- | --- | --- |
| `src/ResourceCycle/CycleActions.cs` 接口 | `ICycleDecisionSource` | `ICycleCharacterController` |
| 上述接口方法及其实现 | `ChooseAsync` | `ChooseCommandAsync` |
| `src/ResourceCycle.Cli/PlayConsole.cs` 嵌套类 | `ConsoleDecisionSource` | `ConsoleCharacterController` |
| `src/ResourceCycle/CycleEngine.cs` 的 `CreateKernel` 参数 | `decisionSource` | `characterController` |
| 同文件 `CycleRule` 主构造参数 | `decisionSource` | `characterController` |
| `tests/ResourceCycle.Tests/CycleTestSupport.cs` 嵌套类 | `Source` | `StubCharacterController` |
| `tests/ResourceCycle.Tests/CycleStorageShapeTests.cs` 嵌套类 | `WaitSource` | `WaitingCharacterController` |
| `tests/ResourceCycle.Tests/CyclePersistenceTests.cs` 嵌套类 | `WaitSource` | `WaitingCharacterController` |
| `tests/ResourceCycle.Tests/PlayableCycleTests.cs` 嵌套类 | `GatedSource` | `GatedCharacterController` |
| `tests/ResourceCycle.Tests/MultiplayerRecoveryTests.cs` 嵌套类 | `GatedContinue` | `GatedContinueCharacterController` |

两个 `WaitSource` 和两个 `decisionSource` 是独立声明，均已分别改名。`actorId`、`NeedsDecision`、`DecisionGeneration` 等保留其准确含义，无需为统一词根而改名。

异常字符串、接口注释、[术语表](../glossary.md)和当前设计中的 API 映射已同步；旧实验验收中的当时名称保持历史语境。后续语义改造仍应独立设计、实施和验证，不能把这次符号重命名当作无状态架构已经完整落地的证明。

相关合同：[术语表](../glossary.md)、[多人采集规则](resource-cycle-multiplayer-gathering.md)、[E/S 发布](durablegraph-occurrence-persistence.md)、[缓存与恢复](runtime-derived-predictions.md)。
