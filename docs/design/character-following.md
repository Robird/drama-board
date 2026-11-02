# Playground 角色跟随设计

状态：已实施；工程验证与试玩证据见[实施验收](../experiments/character-following-acceptance.md)。2026-11-01。

本文保留经辩证审阅收敛的功能与实现合同。用户认可“主动集合—持续同行—到点解散—分头工作—重新集合”的方向，并明确没有旧存档或 API 兼容负担；当前 Playground 已按此合同接通领域、Context、持久化与 Console/LLM 入口，GameRevision 为 17。

## 1. 需求账本与证据边界

| 编号 | 必须保留的要求 | 来源与当前消费者 |
| --- | --- | --- |
| R1 | 两人或三人，领队照常移动；队员主动跟随，期间不能自己移动或开始其他耗时动作 | 用户当前故事；Playground 生存角色 |
| R2 | 抵达工作地点后显式结束跟随，分头工作；完成或取消活动后主动重新加入 | 用户故事与已认可建议 |
| R3 | Console 与 LLM 使用相同玩法规则；跟随不能靠控制器反复提交 go 实现 | 用户要求控制器无关；现有共享 parser / Check / Plan 路径 |
| R4 | 直属、无环关系；同地加入；到达保留关系；允许补给与原有即时操作；普通同行不反复唤醒 | 用户认可的上一轮建议；本轮明确其操作细节 |
| R5 | 同行出发全部成功或全部不发生；个人身体、背包、发现与终局独立；所有同刻到达完成后才能输入 | 用户认可的建议；现有单根 E/S 与确定性仲裁 |
| R6 | 途中不可取消移动；关系失效不取消已开始路段；角色材料不自动共享私有知识 | 现有 `PlaygroundActions`、`PlaygroundPlanning`、`PlaygroundObservation`；已认可边界 |
| R7 | 保存、冷开和 pending E 恢复保持跟随事实，不重做控制器调用或计划 | 现有 `PlaygroundDurableHistory`、`PlaygroundEngine` 合同 |
| R8 | 没有兼容包袱；首版游戏内实现，不引入通用队伍、网络或异步输入框架 | 用户明确决定；当前只有 Playground 消费本功能 |

真实运行模型：单个宿主串行服务角色；等待控制器输入时模型时间暂停；即时操作可在同一模型时刻轮转；Kernel 按预测选择 occurrence。发布为独立 E/S，Fold 应用记录事实，Complete 维护派生预测，成功保存后安装。不是多人联网实时竞态模型。

当前证据入口（代码优先于既有设计）：

- [PlaygroundActions](../../src/Playground/PlaygroundActions.cs)：动作校验、`NeedsDecision`、途中限制。
- [PlaygroundPlanning](../../src/Playground/PlaygroundPlanning.cs)、[PlaygroundFollowingRules](../../src/Playground/PlaygroundFollowingRules.cs)：全员原子出发、逐实体到达、关系校验与终局清理。
- [PlaygroundEngine](../../src/Playground/PlaygroundEngine.cs)、[预测类别](../../src/Playground/PlaygroundModels.cs)：同刻死亡优先，全部空间到达先于天气、活动完成、警告和输入；不改变现有顺序。
- [PlaygroundFactApplication](../../src/Playground/PlaygroundFactApplication.cs)：动作身份和事实形状验证。
- [PlaygroundInteractionRules](../../src/Playground/PlaygroundInteractionRules.cs)、[验证](../../src/Playground/PlaygroundInteractionValidation.cs)、[CharacterContext](../../src/Playground/CharacterContext.cs)：关闭 Context、PendingAdvance 与重新唤醒。
- [PlaygroundGameFactory](../../src/Playground/PlaygroundGameFactory.cs)、[PlaygroundExpedition](../../src/Playground/PlaygroundExpedition.cs)：支持 1–3 个生存角色；三角色采集 demo 仍是独立的无空间场景。
- [FollowingRulesTests](../../tests/Playground.Tests/FollowingRulesTests.cs)、[FollowingMovementTests](../../tests/Playground.Tests/FollowingMovementTests.cs)、[FollowingContextTests](../../tests/Playground.Tests/FollowingContextTests.cs)、[FollowingRecoveryTests](../../tests/Playground.Tests/FollowingRecoveryTests.cs)：规则、同行、决策和真实磁盘恢复边界。实际执行结果集中在实施验收。

邻接设计仅用于理解既有合同：[角色 Context](character-context-model.md)、[控制器边界](stateless-character-controller.md)、[E/S](durablegraph-occurrence-persistence.md)、[分层与组合](layering-and-composition.md)。它们不是本需求的独立证明。

## 2. 功能合同

### 2.1 角色关系与操作

跟随（Following）是持续的移动授权关系，不占用采集/等待 Activity，没有预设截止时间。首版只在 Playground 生存探索场景启用：以存在 Expedition 为能力边界，纯采集 `--play` 和 Microtown 不提供这些命令。

| 命令 | 接受条件 | 结果 |
| --- | --- | --- |
| `follow <actor>` | 自己和目标活跃且同地点；自己没有 Activity、没有跟随别人、也没有队员；目标没有跟随别人 | 自己加入目标；本次零耗时操作放行模拟，结束自己的决策回合 |
| `unfollow` | 自己正在跟随，且处于地点内 | 解除自己的关系，恢复自主行动；本次操作不放行时间 |
| `dismiss-followers` | 自己有至少一名队员，且处于地点内 | 一次解除全部直属队员；队员恢复决策机会；自己保留原活动与决策机会 |
| `go <passage>` | 自己没有跟随别人；自己与全部队员均可沿该通路出发 | 沿用普通移动命令，原子提交全部成员出发 |

禁止自跟随、链式跟随和环；转跟其他领队需先 `unfollow`。重复 follow、无关系时 unfollow、无队员时 dismiss 均明确拒绝，不引入幂等操作协议。非法/远处角色 ID 返回不透露隐藏状态的统一拒绝。

领队可正在原地采集或等待；队员可以提前加入等待。加入不替领队取消活动，不强制唤醒忙碌领队；变化作为经历记录，领队下次观察能看到队员。拥有队员的角色不能去跟随别人，需先解散。

加入即表示“当前已完成自己的耗时活动，并授权领队带自己离开”。不另存 Ready、Party、Roster、预期人数、邀请、批准或领队角色类型。未加入的同地角色仍自主行动，领队可自行选择等待或先走。

### 2.2 跟随期间的权限

- 拒绝独立 go、gather、wait；这些操作先要求 unfollow。
- `cancel` 仍只取消已有采集/等待，不作为脱队别名。
- 查看材料、目标管理、handoff、使用补给继续遵守现有规则。
- say / give 继续要求同地，不因为同行而扩展到路中通讯。
- 任一命令仍需当前决策机会；“允许操作”不代表新增随时打断宿主的输入通道。
- 身体警告与待回应交流保持原来的唤醒语义。`continue` 确认后，若仍在跟随且没有待回应事项，则重新放行模拟。

领队移动不得清掉队员尚未确认的警告或交流；有待回应事项的队员仍会得到决策机会。成员没有 Activity 是出发条件，不把“没有任何提醒”再定义成一个准备状态。

提醒确认必须按操作所有者明确处理，不能把“参与同一事实”当作“本人已确认”：

| 操作 | 发起者已有 notices / 交流 / 身体提醒 | 其他人的提醒 | Context |
| --- | --- | --- | --- |
| follow | 沿用主动放行动作的本人确认语义，全部确认 | 保留 | 关闭 |
| unfollow / dismiss-followers | 全部保留 | 全部保留 | 保持打开 |
| 领队 go | 仅确认领队本人 | 保留所有队员提醒 | 只关闭领队的回合 |
| continue | 沿用本人确认规则 | 保留 | 无剩余决策需求时关闭 |

两条必要轨迹：A say B 后，B follow 应直接休眠，不能因同一条旧交流立刻重开回合；A 正在采集并因身体警告醒来，dismiss 后仍需保留警告和操作机会，直到 A 自己 continue。Cancel 会结束 Activity，dismiss 不结束，不能因此类比两者的确认行为。

### 2.3 集合、同行、分头工作

```text
B: follow A                  B 结束当前回合，原地等待领队
C: follow A                  A 现在有 B、C 两名队员
A: go passage-1              A、B、C 同时出发
（三人到达；跟随关系保留）
A: go passage-2              不需要逐人重新确认
（到达采集地）
A: dismiss-followers        B、C 恢复自主决策
A / B / C: gather ...       各自独立工作
B / C: 完成或 cancel 后 follow A
A: go ...                   一起离开
```

到达保留关系，普通到达不唤醒队员。领队到达后正常决策，可继续走、工作、交流或解散。忘记解散时，队员仍在等待领队，界面必须明确显示，而不是假装队员已经开始工作。

没有预期队伍名单，首版不承诺“领队一定等全员”；领队只带实际已跟随者。观察同时呈现同地角色当前可见活动及谁已经加入。

### 2.4 自主退出与异常

地点内，队员在自己的决策窗口可 unfollow；领队可 dismiss-followers，也可通过现有 say 唤醒队员。首版没有真人后台按键、任意时刻插话或 LLM 定时轮询。

途中拒绝 follow / unfollow / dismiss-followers；保持本段移动不可取消。到达后才可正常改变关系。

- 队员死亡：在个人终局事实中清理其关系，其他成员继续；不得留下指向终局角色的活动关系。
- 领队死亡：在同一事实中解除全部关系并记录原因。仍在途中的队员保留原路线、速度、到达时刻，到达后自主行动；原有身体警告仍可途中唤醒。
- 到达出口：每个人独立按现有规则获救。解除关系不改变其他人的到达与个人结局。
- 关系解除事件进入受影响角色的经历；原地且空闲者自然需要决策，在途者不因解除而获得取消移动能力。

### 2.5 可见材料

自己的观察至少包含：正在跟随的领队身份、自己的直属队员身份，以及同行状态对应的命令限制。途中使用自己的 Journey / ArrivalDue；到达前不因领队知道目的地而共享其地图记忆。

成员身份可沿已建立关系呈现，但不由此暴露其他人的背包、身体数值、Goals、私有 Context 或未发现资源。领队没有自动获得全队风险仪表盘；队员自己的警告仍交给本人。

Console 两种布局、full/delta、纯 Context 输出与 LLM 材料都消费冻结观察；渲染器不查世界、不改变关系、不确认感知。命令与状态始终可发现，不测试固定排版位置。

## 3. 软件实现设计

### 3.1 最小权威状态

给 `PlaygroundCharacter` 增加可空的 `FollowingLeaderId`。领队的队员集合通过扫描角色派生；当前 2–3 人无需索引。任何 Change / WithSurvival / End / 恢复路径都必须显式保留或清理该字段。

不增加 Party 实体、关系 ID、版本时钟、队伍 Activity 或持久成员列表。关系变化沿用角色 `DecisionGeneration` 使预测失效；位置与运动身份仍属于 Spatial。

世界验证：有关系者活跃、无 Activity；目标存在且活跃、不是自己、目标无上级；自己不得同时拥有队员。平常双方同地，途中双方同路线、同起止时刻与速度。各实体 MovementGeneration 是各自的历史身份，不要求它们数值相等。逐实体到达引起的短暂混合状态见 3.3，不能简单要求所有发布边界都具有完全相等的位置类型。

游戏内 `PlaygroundFollowingRules` 共享成员枚举和纯关系谓词。`PlaygroundActions.Check` 仍为公开合法性入口，`PlaygroundPlanning` 仍为事实规划入口，验证器检查记录的转换，不增加第二套状态机、执行器或队伍服务。

### 3.2 出发：一个决定、多人的事实

`go` 的执行对象是领队和其当前直属队员，按既有 Characters 顺序稳定枚举。

1. 在同一个 before world 上检查成员身份、关系、无 Activity、同地点、通路与数值边界；对所有人检查 Spatial 可否开始移动。
2. 准备完整的 `TraversalStartedFact` 序列和成员变化；全部成功才构造一个 `PlaygroundFact.MoveStarted`，包含所有 spatial facts / changed characters。
3. 仅领队具有此次 go 的 ToolCallId / OperationRecordId；队员的移动是这个决定产生的领域效果，不伪造队员操作。
4. 每个实际移动者记录自己的出发经历、环境/需求速率变化和可见材料。队员原来 PendingAdvance 不被改写成领队的 go；其待确认事项保持不变。
5. Fold 只应用已记录的多角色变化，不能扫描“当前队员”重新扩展动作。验证记录中的参与者恰好匹配 before 的领队关系，不允许漏带或夹带角色。

`PlaygroundFact` 沿用多角色 / 多空间事实容器；`PlaygroundFactApplication` 校验精确成员集合，`PlaygroundObservedEvents`、Observe 的出发快照和交互验证区分个人操作与实际移动。`PlaygroundSurvivalRules.Enrich` 枚举实际 changed characters 计算速率，并按 2.2 的确认白名单处理提醒。

观察规划局部区分三个判定，不新增持久身份：`subjectSelf` 表示事实主体是本人；`ownOperation = subjectSelf && OperationRecordId != null` 表示本人的主动操作；`movedSelf` 从已记录出发/到达 SpatialFacts 的实体 ID 派生。回执、草稿消费、主动确认与 Context 闭合只属于 ownOperation；个人移动经历和出发快照属于 movedSelf；自然到达、收获和死亡仍按各自事件主体解释。不能全局替换现有 own，也不能把所有 changed characters 当发令者。

### 3.3 到达：优先保留逐实体事件

现有 `PlaygroundRule.Forecast` 已使同一时刻的 Spatial occurrence 先于警告/输入；首选复用逐实体到达，不创建队伍到达调度器或把多条自然事实强行合成一个新事件。

必须满足：第一人抵达至最后一人抵达之间，不允许任何控制器插入 go / unfollow 等操作；S/E 恢复可以落在其中任一已有提交边界，并继续完成剩余到达。

关系空间校验只读取 Business 与当前模型时间，对每个领队及直属成员组成的集合检查三种形状：

1. 全部 AtPlace：在同一地点。
2. 全部 Traversing：相同通路、方向、起止时刻、运动锚点与速度。
3. AtPlace / Traversing 混合：所有仍在途者保持同一行程，ArrivalDue 等于当前模型时间，全部已到者位于该行程目标地点。

不保存新的“到达中”标记，不在关系校验器中查询或重建预测。`PlaygroundEngine.Rebuild` 本来就先验证 Business 再重建预测；[SpatialOccurrenceRule](../../src/Spatial/Simulation/SpatialOccurrenceRule.cs) 从每个 TraversingLocation 直接派生 arrival，预测与时间有效性由现有 Spatial/Engine 负责。剩余 arrival 有效且先于输入是组合验收要求，不是第二份到达权威。

同刻死亡优先级保持不变。到出口时先完成个人终局者可以触发关系解除；其余人的已启动行程仍独立存在，继续完成。不能要求一组人永远共同存活或共同终局。

实施验收必须用三人逐步 Step、每步 Validate、增量/全量预测对照和中间边界恢复验证上述选择；如果有实际破坏反例，修正游戏层组合，不先改 Kernel 仲裁。

### 3.4 决策与 Context 生命周期

以 `PlaygroundActions.NeedsDecision` 为唯一决策需求谓词，已删除 `PlaygroundCharacter.NeedsDecision` 这个不含空间状态的同名局部属性；局部条件并入前者，[PlaygroundDiagnostics](../../src/Playground/PlaygroundDiagnostics.cs) 同样使用统一入口，无兼容转发。

该谓词对活跃角色的规则改为：

```text
存在待回应交流 / 通知 / 身体警告
OR（不在移动 AND 没有 Activity AND 没有 FollowingLeaderId）
```

保持原有通知对忙碌角色的唤醒。follow 成功关闭自己的 Context，消费 handoff 草稿，建立 PendingAdvance；普通随行事件只累计个人经历。关系解除使空闲队员自然重新需要决策，不增加 FollowAttentionPending。

`PendingAdvance` 保留 ActivityId 与 MovementGeneration 的历史活动/行程因果；原有二者恰有一个非空的条件扩展为以下有限形状：

| 原命令 | ActivityId | MovementGeneration | 接受事实的额外条件 |
| --- | --- | --- | --- |
| Wait / Gather | 有 | 空 | 对应本次活动 |
| Go | 空 | 有 | 对应本人本次行程 |
| Follow | 空 | 空 | 本次成功建立同地跟随关系 |
| Continue | 有 | 空 | 继续原活动 |
| Continue | 空 | 有 | 继续原行程，可同时有跟随关系 |
| Continue | 空 | 空 | 接受时正在原地跟随，且无活动/移动 |

结构验证只允许表中的命令/字段形状；`ValidateAdvance` 在接受事实的 before/after 上检查真实关联，Follow 必须从原有 Continue-only 分支分离。后续 PendingAdvance 是历史原操作，不能拿当前关系来否定它：Opening 消费时关系可能已解除。无需新增 FollowId、FollowingGeneration 或额外的长期证明记录，也不借本功能删除原活动/运动关联字段。

`CreateContext` / `ValidateOpening` 将“仍在跟随”计入 Ongoing。`continue` 在原地跟随时可以重新休眠；无待回应事项时不能凭空重复提交。闭合条件、Advance 构造、结构验证、事实验证和结果描述必须同改，不能只改 NeedsDecision。

领队 go 时若队员已有打开的警告/交流 Context，保留它并追加观察变化，不伪造其主动放行；按原调度服务，处理后 continue。具体轨迹：B 因交流 Opening → B 做一个即时操作并轮转 → A go → B 的同一 Open Context 收到出发经历与新位置 → B 按途中规则补给或 continue。宿主没有并发中的 B 控制器调用，不需新增输入竞争机制；也不承诺领队等 B 回应才移动。

### 3.5 事实、可见性与预测维护

新增三种明确命令及相应关系变化事实类别：FollowStarted、FollowEnded、FollowersDismissed（具体命名实施时统一）；死亡/获救触发的清理附于现有终局事实，不另造延后清理任务。

关系变化以角色替换作为唯一权威数据。仅在规划时构造已录制经历；关系接收者根据 before/after 关系差异及关联领队确定。尤其 A 死亡解除 B 的关系时，B 收到“领队终局导致跟随结束”，不能沿现有按 fact.Kind 转换观察类别的方式误记为“B 死亡”。关系清理适用于连续生存耗尽、天气死亡和出口获救的所有终局路径，附在原终局事实内，保留每人的真实原因。

终局清理、多人出发的接收者与 effects 必须经 `PlaygroundObservedEventValidation` 验证。界面状态键加入跟随身份/队员集合，Observation equality、visible endpoint changes、Context 校验同时覆盖。新的 Follow target 同时接入 parser/command/fact 参数校验，不沿用“只有 say/give 才可携 TargetActorId”的旧限制。

受影响角色的 attention、survival、activity 等 owner 使用现有 dirty 维护；不要仅按 `fact.ActorId` 使领队失效而漏掉队员。新事件加入相关 bindings。以 Rebuild/oracle 验证增量结果，无新持久预测缓存。

### 3.6 两人与三人入口

本需求包含三人，不以二人 demo 代替验收。扩展 `CreateSurvival(characterCount)` 与 expedition 验证到 1–3，明确创建 a/b/c（复用采集 demo 已有角色名），禁止当前二元表达式生成重复 b。

增加 `--survival --characters 1|2|3` 入口；新局默认 1。`--duo` 只在参数解析处归一到显式 count=2，不在内部继续传播 duo 布尔模式；与 --survival 或 --characters 同用时拒绝歧义组合。--characters 只适用于 --survival。恢复时，未显式给人数就使用存档角色数；显式数量与存档不符要报错。

具体检查 [Program](../../src/Playground.Cli/Program.cs)、[PlayConsole](../../src/Playground.Cli/PlayConsole.cs)、[PlaygroundHost](../../src/Playground.Cli/PlaygroundHost.cs)、[宿主接线](../../src/Playground.Cli/PlayConsole.cs) 与 Gym 场景信息，包含两次模式校验、退出/续局提示和实际绑定审计；不能只放开工厂人数。不增加通用 roster 配置。

多人默认共用 Human Console；带 `--llm` 时保持 a 为 Human，其他角色使用现有角色路由绑定 LLM；单人 --survival --llm 继续由 LLM 控制 a。绑定从实际 Characters 构造，不能漏掉 c；继续复用无状态控制器与既有宿主调用预算，不按队员人数隐式增加预算。允许 LLM 或 Human 任一角色担当领队；不按控制器类型写玩法分支。

### 3.7 持久化和兼容性

沿用现有单根 E/S；新增字段、命令/事件枚举、观察及 PendingAdvance 语义需要更新 Durable schema 与 GameRevision。没有旧开发存档兼容要求：提升当前修订并拒绝旧修订，不写迁移、双读或 fallback。

实现时同步更新 `PlaygroundDurableModels` / 事件序列化与复制路径、操作恢复、Gym 导出所依赖的现有 material，不建立单独的队伍日志。E 已提交但 S 未完成时只重放已记录事实，不重新访问控制器或发起新的 LLM 调用。

## 4. 最小实施切片与验收

一次贯通 Playground，不先搭共享框架：

1. 领域与决策闭环：关系字段、三命令、动作限制、多人出发、到达边界、终局清理、Context/PendingAdvance 与预测校验；使用现有 scripted controller 完整跑集合到再集合。
2. 同一闭环持久化与呈现：修订/schema、冷开/pending 恢复、冻结观察、Console/LLM 命令材料、三人入口。
3. 独立验收：真实 Console 操作与有界真实 LLM 试玩；不把模型返回成功、脚本代操 Human 或旧日志当作本次真人体验验收。

前两步是同一可交付功能的实现顺序，不是允许留下半套接线的独立平台阶段。

| 验收组 | 必须证明的行为 |
| --- | --- |
| 加入/退出 | 同地、自愿、无活动；目标可以忙碌；自环/链/转跟/重复拒绝；解散与主动退出恢复动作 |
| 权限 | Console 与 LLM 都拒绝跟随者 go/gather/wait；补给、交流、目标遵循原条件；途中不可退出 |
| 同行原子性 | 任一成员出发失败时全员保持原状；成功时同路线/同速/同时；只有领队操作身份，无虚构队员 go |
| 决策节奏 | 收到 say 后 follow 直接休眠；多段路不逐段调用队员控制器；提醒→补给/回复→continue→休眠；忙碌领队因警告醒来→dismiss 仍可操作→continue 放行；已有 Open 的队员被带走后按新位置操作 |
| 到达/终局 | 三人所有同刻到达先于输入；逐人到达中间状态可校验/冷开；领队途中死亡不取消剩余路段；同刻死亡优先；出口个人终局 |
| 个人观察 | 各自产生移动经历与访问记录；未知目的地不泄露；无私有背包/Goals/身体值共享；两布局与 LLM 材料一致 |
| 持久化 | 原地跟随、途中、逐实体到达间、解散后冷开；E-only/pending 重放不调用控制器；关系/Context/操作结果因果一致 |
| 预测 | 上述每个边界增量与 Rebuild/oracle 等价；队员 owner 不漏失效；无同刻无限转换 |
| 产品闭环 | 二人和三人分别集合→至少两段同行→解散采集→取消/完成→再集合离开；至少一次 LLM 领队和一次 Human 领队 |

构建测试沿用根 SDK、串行 dotnet 图及 Schema Verify。新增验收文档应保存实际命令、源 commit 与证据范围；本文是设计，不预报测试通过。

## 5. 明确延期

| 延期项 | 重开条件 |
| --- | --- |
| Party / 队伍名单 / Ready 与全员出发屏障 | 实际玩法要求识别“尚未回来的固定队友”并强制等待 |
| 邀请、领队批准、踢单人、转交领队 | 自愿跟随与全体解散无法满足实际社交玩法 |
| 途中脱队、掉头、追赶、不同速度 | 空间交互需求明确，先定义新的移动语义 |
| 真人随时插话 / LLM 轮询 / 自动补给 | 现有决策窗口的退出或安全体验出现实际不足 |
| 共享地图、风险面板、背包与规划 | 有独立的信息分享与授权需求 |
| Microtown 或跨游戏 Follow 模块 | 第二个真实游戏消费相同行为后再提取 |

## 6. 审阅裁决

使用 dialectical-simplification：三位独立审阅者分别担任需求怀疑者、最小架构师、语义守护者；均完整阅读同一初稿和需求账本，再核对当前源码。首轮独立主张、次轮交叉质询，只有“解除关系是否确认本人提醒”进入第三轮具体轨迹裁决。审阅者全程只读，由主线程独立核对源码并修改本文；没有运行功能测试或真实模型。

| 裁决 | 议题与最小结论 | 决定性证据 / 删除失败 |
| --- | --- | --- |
| merge / delete | 决策需求统一到 PlaygroundActions，移除角色同名局部属性 | 原诊断读局部属性，不知道空间/跟随；直接消费者只有 Actions、Diagnostics |
| simplify | 关系只做 Business 形状验证，arrival 仍由 Spatial 派生 | Rebuild 先验证 Business；读取运行时预测将倒置依赖；窄混合边界仍须保留 |
| simplify | 明确操作拥有者、实际移动者及关系接收者，无新持久名单 | 队员不能获得领队 ToolCallId，领队死亡不能记成队员死亡；自然事件也不能因无操作 ID 被漏掉 |
| simplify | Follow 确认本人；unfollow/dismiss 保留所有提醒 | 最小架构师最初类比 Cancel，交叉质询后撤回：忙碌领队 dismiss 清唯一警告会留下无法继续服务的 Open Context |
| keep | 单一 FollowingLeaderId、三命令、原 PendingAdvance 加有限双空形状 | 单凭最后命令无法表示长期关系；用当前关系验证历史 pending 会在解散后失败 |
| keep | 多人原子出发、逐人到达及同刻先于输入、终局同事实清理 | 分开提交出发会走散；严格同位置拒绝合法中间 S；延后清理可留下指向终局领队的关系 |
| keep | 三人生存入口和完整路由 | 工厂现有二元 ID 生成会重复 b；现有 a/b 路由无法服务 c |
| defer | Party / Ready / FollowId / 额外到达阶段 / 通用模块 | 当前无消费者；具体重开条件见第 5 节 |

最终模型保留 **1 个新增权威关系字段、3 个新增命令**；合并 **2 个同名决策谓词为 1 个入口**，不新增关系身份、持久队员表或到达调度器。审阅没有删除个人移动、操作身份和恢复因果，具体失败轨迹保留在功能合同和验收表中。

没有剩余必须由用户裁决的产品分歧。退出受决策窗口限制、有提醒仍可被领队带走、未加入者不会被强制等待，均已在本文明示；如果后续体验改变这些要求，应重新设计对应产品合同。第 4 节保留实施顺序与验收要求，当前完成证据及体验边界见实施验收。
