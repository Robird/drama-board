# 从 Mud 借鉴玩家体验：DramaBoard DemoApp 可行性调查

> 2026-10-27。调查基线：DramaBoard `7594237`、durable-graph-mud `66e12a7`；LLM 储备参照 atelia-console-agent `3c013fe`。
> 本文是源码调查和候选实施顺序，不是已经批准的施工契约。没有移植代码、解冻旧应用或重新运行这些仓库的构建与测试。

后续决策：用户选择就地增强 ResourceCycle，当前[单地点采集片](../design/resource-cycle-play.md)已实施；扩大世界前优先进行[预测对象核心重构](../design/prediction-objects-roadmap.md)。下文保留调查当时的能力分类与候选路线，不代表后续顺序仍待选择。

## 结论

**现在已经可以建设一个具有 Mud 阅读和操作体验的小型 DemoApp，不需要先完成空间重构、通用事件总线或 LLM Player。** 最有价值的第一步是：一个角色、一个地点、现有两种植物、少量物品，支持观察、等待、采集、背包、完整 Markdown、差分及冷开续玩。两地点移动可以作为紧接其后的切片。

目前缺少的主要是应用层闭环：角色状态、行动规则、请求输入的时机、玩家可见观察，以及连接这些部分的 Session。DramaBoard 已有确定性事件推进和可恢复发布的基础；它尚未提供开箱即用的 RPG Session。不能将“可以实现”写成“已经实现”。

真正需要认真设计的部分有四项：

1. **行动生命周期**：Mud 一次输入同步结算；DramaBoard 的长行动应允许环境事件在开始与完成之间发生。
2. **观察与事件窗口**：一个世界 occurrence 不等于一个玩家回合，“最近发生”不能机械按最后几个 occurrence 截取。
3. **子系统组合**：Spatial、ResourceCycle 的规则和事实绑定各自世界类型，还不是可直接拼接的通用组件。
4. **持久化所有权**：当前单根恢复可作为模式复用；多分支体验及持久 LLM 子状态需要新的应用宿主整合，不能叠放几个各自提交的 Session。

## 1. 范围、分类和证据

本文的“移植”指选择性复用体验、内容和独立代码，在 DramaBoard 新建应用；不要求还原整部《雾川》，不迁移 Mud 存档，也不保留原动作时间语义。旧 FirstBoard/Player 仍以归档为准。

采用三种判断：

- **可直接复用**：已有独立代码或明确的局部算法，仅需项目接线或小规模类型整理。
- **应用层可实现**：核心没有已发现的阻塞，但领域规则、快照、宿主或适配器还需要编写和验收。
- **存在集成/设计障碍**：直接搬现有代码会违反状态、时间或提交契约；需要先裁决具体语义或改造接口。这不等于整体不可行。

四个并行调查分别覆盖文本/动作、玩法/时空、GUI/Session/历史、LLM/多人；主线程复核 Kernel 的等待与发布顺序、世界组合、Mud 的呈现与结算代码，以及 AgentSession 的根对象假设。下文链接指向当前源码；跨仓链接依赖相邻仓库布局。

现有实现依据：

- [DramaBoard 当前 solution](../../DramaBoard.slnx)仅含 Kernel、Spatial、Host、ResourceCycle 库/CLI 及测试；没有活跃的通用 RPG DemoApp。
- [ResourceCycle 验收](../experiments/resource-cycle-acceptance.md)记载上一轮的跨进程、E-only 恢复和增量/全量对照。本轮引用其证据范围，不宣称重新运行通过。
- Mud 的[项目状态](../../../durable-graph-mud/PROJECT-STATE.md)用于导航，具体能力以本文链接的源码为依据。

## 2. 玩家体验能力地图

| 玩家体验 | Mud 的现状 | DramaBoard 的可行性与缺口 |
| --- | --- | --- |
| 场景、状态、背包、人物和出口的完整文本观察 | 不可变 `GameView`，按语义分节 | **应用层可实现**。需要新世界到玩家观察的投影；不能把诊断输出直接当角色感知 |
| 参数化动作、候选域、不可执行原因 | Describe / Query / Check / Execute 已分开 | **契约可复用，规则需重写**。新行动应共享校验与执行前检查；查询不能推进世界 |
| 动作组合的紧凑 Markdown | 二维矩阵多表达择短 | **Mud.Text 可直接引用**；上层文档/动作类型需迁出或重建。当前仍计算完整参数乘积 |
| full/delta 及运行时切换 | 块键、撤销块、游标、重同步 | **局部移植可行**。需要稳定观察版本和每个阅读上下文独立游标 |
| 当前状态与“最近发生”的注号关联 | 结构化事件、效果、状态键，最近完整行动历史 | **算法可借鉴，事件采集需重做**。要定义时间窗口、因果身份和可见域 |
| 连续对白/小说式正文 | `TurnNarrative` 与当前观察分离 | **应用层可实现**。先少量固定对白即可，不依赖 LLM 或复杂任务引擎 |
| GUI 三栏、可复制文字、FIFO 正文 | WPF 外壳已有实现 | **布局和交互技巧可复用**；窗口与 Mud Session 耦合，异步会话接线需改写 |
| 昼夜、资源刷新、暂停生长 | Mud 为动作后时间结算；Drama 已有新预测原型 | **已有新模式可用**。真实采集、物品产出与角色状态尚未实现 |
| 角色、物品、背包、装备 | 持久领域模型与内容表 | **应用层可实现**。数量、槽位、角色 ID、子图不变量需要新建，无须先改 Kernel |
| 两地点移动、在途、到达 | Mud 为房间跳转；Drama 有 Graph Spatial | **当前 Spatial 可支撑**。需将规则/事实接入统一 DemoWorld，并定义玩家可见的在途体验 |
| 制作、交易、NPC 对话、任务 | 规则和内容已存在 | **可以分批借鉴**。同步简单规则容易；长行动、日程、截止时间须重新映射 |
| 战斗、死亡、打断 | Mud 单玩家同步轮流结算 | **设计障碍较大**。受击/行动完成/死亡同刻顺序、打断与资源退款不能靠原系统顺序继承 |
| 关闭后续玩、pending E 补完 | 两边均有各自实现 | **单根模式已有证据**；新根/新事实仍要注册、验证及测试，不是原 adapter 换个名称即可 |
| 命名存档、回溯、分叉、切线 | Mud Session 已封装 DG 能力 | **底层有能力，Demo 产品链路未接**；ResourceCycle 明确拒绝 forks |
| LLM Player 与行编辑笔记 | Mud 中是研究方案 | **不是已有可迁产品**。模型/工具储备存在；持久子 Session 与游戏单根整合需额外工作 |
| 2–3 Player 共存与轮流请求输入 | Mud 没有；Drama 活跃应用也没有 | **世界建模可行，调度语义待定**。同刻先后、共享资源竞争和每玩家观察窗口是主要工作 |

## 3. 文本和动作：最值得先搬的部分

### 3.1 可复用边界

[Mud.Text.csproj](../../../durable-graph-mud/src/Mud.Text/Mud.Text.csproj)没有游戏或 DG 的项目引用；`BoolMatrix`、`MatrixLabels`、`IBoolMatrixFormatter` 和 Markdown 转义可以先以相邻源码引用复用。没有必要为了首个 Demo 先创建新的公共包。

[ActionContracts](../../../durable-graph-mud/src/Mud.Engine/Actions/ActionContracts.cs)与[GameView](../../../durable-graph-mud/src/Mud.Engine/Presentation/GameView.cs)提供了值得保留的边界：

- UI 收到脱离持久对象图的不可变值，不拿可写 World 或规则委托。
- 描述动作入口、查询参数候选、校验完整请求、执行分别承担明确职责。
- 候选值存在不表示任意组合合法；只有 Ready 可执行，受阻原因保留。
- 观察和动作目录来自同一版本；执行时再次验证，旧按钮/旧命令可以明确拒绝。
- 所有事实均能文本表达；GUI 不另造规则或隐藏必要信息。

但这些类型目前位于 `Mud.Engine`，且 `GameView` 引用了 Session 的 `ActionChoice`；[Engine 项目](../../../durable-graph-mud/src/Mud.Engine/Mud.Engine.csproj)还引用 `Mud.Domain`。**单独看 DTO 简单，不等于已有干净的独立呈现程序集。** 建议先在 Demo 中落实最小等价契约，选择性移入所需渲染代码；确有第二个消费者时，再与 Mud 共同提取。不要为了引用一个渲染器而让新应用依赖完整 Mud 世界和 Session。

### 3.2 组合压缩、delta 与 full 的限制

[ActionDocumentBuilder](../../../durable-graph-mud/src/Mud.Engine/Presentation/ActionDocument.cs)仍然列出完整笛卡尔积并批量检查；[ActionMarkdownRenderer](../../../durable-graph-mud/src/Mud.Engine/Presentation/ActionMarkdownRenderer.cs)比较文本表达。它解决输出冗余，**没有解决所有组合的计算成本**。小型 Demo 完全可以沿用；若以后 NPC × 物品 × 地点激增，再研究规则提供的结构化关系，不能先把现有 formatter 当作性能保证。

[MarkdownObservationCursor](../../../durable-graph-mud/src/Mud.Engine/Presentation/MarkdownObservationCursor.cs)的块替换、撤销和全量回退算法可复用。它暂存上一份完整观察，不保存游戏状态，也不确认输出已经送达；截断、丢失、切换角色、切线或更换 LLM 上下文后需要重同步。运行时模式切换属于呈现状态，不应产生世界 occurrence。

新应用可以用 [WorldVersion](../../src/Kernel/Simulation/WorldVersion.cs) 的 lineage/count 作为世界版本来源，但还要携带观察者身份；多人情况下仅“同一个世界版本”不足以共享观察缓存。Kernel transition count 不应在 UI 中冒称玩家回合数。

## 4. 事件、状态与注意力提示：需先确定含义

Mud 的[仪表板渲染器](../../../durable-graph-mud/src/Mud.Engine/Presentation/MarkdownDashboardRenderer.cs)把最近一次行动的效果反查到当前状态行；[EventRecorder](../../../durable-graph-mud/src/Mud.Engine/Rules/EventRecorder.cs)通过规则主动声明的作用域和前后状态快照收集效果，避免子作用域变化重复归因。这个设计可以保留，但不能直接把 `CycleFact` 当成 Mud 的 `ObservedEvent`。

这里至少有三层不同的数据：

| 层次 | 当前实例 | 新 Demo 的用途 |
| --- | --- | --- |
| 权威结算事实 | `OccurrenceEvent<CycleFact>` 内的具体替换值 | 恢复时纯 Fold，不再次执行玩家/业务规划 |
| 预测依赖通知 | `CycleBinding` 的 kind/target/owner | 筛选需重算的预测者；不是玩家故事，也不是完整效果记录 |
| 玩家可见经历 | 当前 ResourceCycle 尚无相应领域模型 | 叙事、状态注号、最近历史、未来 LLM 记忆来源 |

新应用可以在规划一次变化时同时产出实际世界效果与可见经历，再纳入同一个发布根；不必要求 Kernel 理解中文文本或装备/任务语义。但可见经历应按已提交结果展示，重放不得重复追加。

需要特别避免两类错误：

- **时间经过被归因给最后一个事件**。`CyclePlant.ProgressAt(time)` 是随时间投影的值；从 16 点观察到 17 点，某植物的进度增长不一定由 17 点恰好发生的另一株销毁所引起。记录“期间生长”和“销毁清零”时应分清来源，不能只比较两个 UI 快照就生成因果声明。
- **看见变化被当成对象变化**。走进房间后看见 NPC，不表示 NPC 刚出生；离开视野不表示对象被删除。观察变化与世界效果需要不同文案，后台预测/脚本详情也不能默认向角色公开。

建议首个版本将“最近发生”定义为**自上一次请求该玩家决策以来，已提交且对该玩家可见的事件窗口**，窗口中允许多个环境事件和行动事件；首次进入/冷开有明确的重同步规则。窗口身份和条目身份应稳定，不能用数组下标充当事件 ID。历史可保留有限个完整窗口；若单窗口可能过大，须明确合并/摘要规则，不能静默丢失关键效果。

这是建议，尚未实现。多人后每位玩家的决策频率不同，观察窗口也可能不同；不应把窗口边界硬编码成全世界共享的“回合”。

## 5. 交互与时间：能等待输入，但要有行动生命周期

### 5.1 当前 Kernel 已支持的接点

[IOccurrenceRule.PlanSelectedAsync](../../src/Kernel/Simulation/IOccurrenceRule.cs)可以异步规划。[SimulationKernel](../../src/Kernel/Simulation/SimulationKernel.cs)选中候选后等待完整 draft，scratch-fold 并验证，再发布 E/S；同一个 Kernel 禁止并发 step。由此可以在选中的“请求决策”规则内等待 Human 输入，逻辑时间在等待期间不前进。

因此，[SimulationHost](../../src/Host/SimulationHost.cs)尚无 `AwaitingPlayer` 状态，**不构成必须先改 Kernel 的技术阻塞**。应用可以提供自己的交互循环/决策入口；第一个 Console 无需通用 Player 调度框架。

但这条接点有边界：

- 规划收到的是旧的已提交 world，候选的 Due 不代表 world 已经推进到该时刻。不能把未来候选时间直接贴在旧观察上。
- 建议先提交行动完成/就绪状态，然后在当前已提交时刻请求决策；需要未来唤醒时，先有相应世界 occurrence。不要在首版做复杂的未来时刻观察投影。
- 非法输入和查询留在决策交互内，不返回可提交的 draft，不消费时间或 RNG。
- 取消发生在 E 之前可以不产生世界结果；E 已记录后必须完成或冷开补完，恢复不会再次请求 Player。
- 等待过程中不能让另一个 Session 改同一 WorkingCopy，也不能重入 Kernel 处理游戏命令。若选择输入前显式停在完成边界，应由同一个应用 owner 统筹，而不是开第二条提交链。
- 决策请求应有持久 request-id/generation；提交后消费旧请求，以新身份构造下一候选键。反复使用固定的 `player-ready` 键会触发 Kernel 的立即重复保护。
- 即时动作可以成立，但不能无限提交同刻的零耗时合法动作：Kernel 有 `MaxTransitionsPerModelTime` 上限。对白、换装等需要明确耗时或循环边界；查询及非法输入仍不产生 occurrence。

### 5.2 不能照搬的 Mud 时间语义

Mud [GameEngine.ApplyCore/AdvanceTime](../../../durable-graph-mud/src/Mud.Engine/Rules/GameEngine.cs)先执行动作、累计 Spend，再推进时间并依次通知各系统。这与“开始采集 → 中途天黑/受击 → 完成或取消”的预测世界不同。

最小 Demo 可以选用简单明确的规则：

- 即时采集：当前检查成熟，成功时扣除资源并增加背包；不假装存在可打断的采集过程。
- 等待：创建明确的等待完成时刻，让昼夜/生长事件在此之前照常发生，完成后重新请求输入。
- 后续再增加耗时采集：持久行动状态记录目标、完成预测和 generation；明确开始时还是完成时扣体力、占用资源，以及取消是否退款。

同刻语义必须写成业务规则。现有 [OccurrenceScheduler](../../src/Kernel/Scheduling/OccurrenceScheduler.cs)的确定性排序按 Due、种子排名、候选键决胜，不自动保证“天黑先于行动”或“行动结果先于再次输入”。[CycleEngine](../../src/ResourceCycle/CycleEngine.cs)在应用内部筛选最早时刻和成熟→光照→销毁优先级；把新规则加成另一个 rule 并不会自动继承这个跨系统优先级。小 Demo 可以在应用组合层统一筛候选，不必马上增加全引擎优先级框架。

`StepAsync(notAfter)` 的上限只是候选执行边界，没有事件时不会将世界时间自动推进到该上限；因此“等待一小时”需要前述完成事件，不能仅调用一次带未来上限的 Step 就宣布时间已到。

## 6. 世界组合、移动和玩法

### 6.1 角色与背包没有发现基础设施硬阻塞

可在新 DemoWorld 下放置独立的角色、背包、资源、空间等子图，以稳定 ID 相互引用，通过一笔业务变化生成相关事实。采集成功同时改变植物与背包，应在同一世界发布边界校验数量守恒；“拆成子图”不意味着各子图各自提交。

[CycleDurableRoot](../../src/ResourceCycle/Persistence/CycleDurableRoot.cs)已经展示 world/cursor 单根；[CycleFact 和 Fold](../../src/ResourceCycle/CycleEngine.cs)展示只替换变化部分、保留其他不可变子图。新增类、生成定义与 schema、根注册和组合校验属于正常应用开发。

不过 ResourceCycle 的 `CycleRule` 为私有类，`CycleFact`、`CycleWorld` 都面向当前两种植物和脚本；它是可验证的模式参考，**还不是可把任意世界挂进去的资源系统插件**。可按新实例需要局部提取业务函数或重写小块代码，不建议先泛化所有预测者/订阅生命周期。

### 6.2 当前 Spatial 足够做两地点体验

[SpatialPlanner](../../src/Spatial/Planning/SpatialPlanner.cs)、[SpatialQueries](../../src/Spatial/Queries/SpatialQueries.cs)和[SpatialOccurrenceRule](../../src/Spatial/Simulation/SpatialOccurrenceRule.cs)已有移动规划、位置查询和到达预测。两地点往返并不需要先把空间全部改造成 ResourceCycle 的显式预测表。

但 `SpatialOccurrenceRule` 的泛型参数是 `GraphSpatialState / SpatialOccurrenceData / GraphSpatialFact`，不能直接与 `CycleWorld / CyclePrediction / CycleFact` 放入同一个 Kernel。新应用需要组合世界、候选和事实包装，将规划下沉到相应子系统、把结果折回新根；并保持全局候选键唯一和同刻仲裁一致。

推荐先只用“地点—通道—到达”，暂不启用途中接触、追逐和攻击。若第一片只做单地点，直接延后 Spatial 接线也成立。无论采用哪条路径，都不要在新世界另保留一个具有独立时间权威的 Mud `RoomId + Spend` 引擎。

### 6.3 玩法的相对难度

- **优先且局部**：背包、即时采集、物品使用、简单装备、固定 NPC 对话、一次性交付任务。借鉴 Mud 的规则与文本，改写为新状态/事实形式。
- **可做但依赖生命周期**：耗时制作、NPC 日程、商店补货、种植、租金与摊位结算。应围绕预测到期、失效条件和当前状态重算，不照抄全系统 `OnTimeAdvanced`。
- **最后验证**：战斗、死亡恢复、移动中交互、打断、多人争抢。需要决定占用/消耗/结算点，同刻效果是否顺序敏感，以及取消之后哪些预测失效。

Mud 的 [Systems](../../../durable-graph-mud/src/Mud.Engine/Systems) 和 [Content](../../../durable-graph-mud/src/Mud.Engine/Content)可作为业务样例与内容素材。静态对白和名称比较容易搬；任务 flags、默认装备槽、房间/资源 ID、商品配方引用构成互相依赖的内容图，不能假设复制某张表就会独立工作。首个 Demo 自建小内容集比整体搬目录更容易验收。

## 7. GUI、存档和 LLM 的实际边界

### 7.1 GUI 复用布局，替换会话接线

Mud 的 [MainWindow](../../../durable-graph-mud/src/Mud.Desktop/MainWindow.xaml)、[TextPresentation](../../../durable-graph-mud/src/Mud.Desktop/TextPresentation.cs)和[SelectableText](../../../durable-graph-mud/src/Mud.Desktop/SelectableText.cs)可以提供三栏、独立滚动、正文 FIFO、拖选复制的现成参考。无需为了第一版重做阅读交互设计。

该外壳使用 WPF，目标为 Windows；这不限制纯文本 Demo，但不能据此宣称 GUI 已具备跨平台能力。

但窗口代码调用具体 `GameSession`，GUI 仍使用 `GameView.Actions` 展开的动作列表和旧 Step 路径；它没有已经完成的参数组合多级菜单。新 UI 应消费新 Session 的不可变快照，按钮只提交请求并等待重新校验。[SessionWorker](../../../durable-graph-mud/src/Mud.Desktop/SessionWorker.cs)目前是串行同步 `Func<T>` 队列，异步决策等待需要相应调整，不能原样套用就假定异步操作也串行。

建议先通过 Console 定型观察/操作边界，再接 WPF。GUI 的美化不应成为时间语义正确性的前置，也不应恢复旧归档 WebUI 作为隐含依赖。

### 7.2 单根续玩可行；历史分支尚需新 adapter

[CycleDurableHistory](../../src/ResourceCycle/Persistence/CycleDurableHistory.cs)可以创建/打开单根分支、记录 E/S、检查 pending、在失败后废弃实例。它明确拒绝多分支和 fork，且根与事实类型写死为 ResourceCycle。可复用流程和测试思路，新游戏仍需自己的根/注册/adapter。

Mud [GameSession](../../../durable-graph-mud/src/Mud.Engine/Session/GameSession.cs)封装了 tag、历史查找、fork、切线与恢复；DG 的这些原语不需要从零开发。但是产品层还要完成：

- 分支身份与 `KernelCursor.LineageId` 的映射，保证旧观察不能用于新线。
- 在完整 S 上建立回溯点，明确 pending E 的处理；新线重建 Kernel，不能悄悄挪动仍在运行的 history。
- 切线时重置 delta、正文接续与决策状态。
- 明确“回到上一次玩家决策”还是“回退一个世界 occurrence”；两者不是同一位置。

[SimulationFork](../../src/Kernel/Simulation/SimulationFork.cs)只提供内存 journal 前缀分叉，并不等于已有磁盘游戏存档菜单。第一片可只做关闭/重开续玩，命名存档与回溯作为后续局部功能。

### 7.3 LLM 与多人可以后接，但不能把储备当成已完成集成

Mud [LLM 研究文档](../../../durable-graph-mud/research/llm-player.md)中的独立回合上下文、笔记 `rewrite_lines`、多 Player 等目前不是 Mud 可搬走的程序。相邻 AgentCore 已有 [AgentLoop](../../../atelia-console-agent/src/AgentCore/AgentLoop.cs)、模型调用与工具基础；但 [AgentSession](../../../atelia-console-agent/src/AgentCore/AgentSession.cs)要求 `WorkingCopy.State` 是 `AgentState`，自行提交 AgentEvent/State，历史也按 AgentEvent 读取。

因此 **不能把这个 AgentSession 原样嵌入 DemoWorld 然后与 Kernel 各自提交**。将 Player 状态作为游戏子图的方向可行，实际需要把根和提交所有权交给应用宿主，并适配 Agent 状态机/历史入口。

早期可先复用模型与工具抽象，在已选中的决策步骤内运行内存 tool-loop；局部决策结束时把必要结果、笔记等并入同一世界 draft。工具只查询、修改暂存笔记或提交一份行动意图，不直接修改权威世界；接受行动后关闭再次行动入口，不能直接沿用通用 AgentLoop 的终止条件。此方案在发布前中断可能重跑整个本次决策，不承诺保存到每条工具调用；只有用户接受这种粗粒度恢复语义时才适用。若要求完整模型响应一旦收到并保存就不再调用模型，或中途独立保存笔记，则应增设显式持久决策阶段，不能在悬挂的 Kernel step 内另行提交。

跨仓源码接线也需单独联编：AgentCore 还引用相邻 Completion/Tools 项目，Console Agent 的入口属性不应假定会随一个 ProjectReference 自动全部传入 DramaBoard。本轮未联编该组合，也未调用实际模型。

多人第一版可以串行请求决策，等待时整个逻辑世界暂停，无需并发运行多个模型。仍要先决定同刻 Player 的顺序、是否轮流享有先手、资源是否预留、每人的可见经历窗口。增加角色字典并不会自动得到这些语义。

## 8. 建议切片与验收

以下是便于后续选择的路线，不是本轮新增待完成任务。

| 切片 | 可见交付 | 主要验收 |
| --- | --- | --- |
| A：单地点可玩 Console | 一个角色、背包、两种植物；观察/等待/即时采集；全量 Markdown；关闭后续玩 | 等待跨昼夜正确；采集守恒；非法请求/查询不改世界；等待输入不推进时间；E-only 恢复不重复奖励 |
| B：呈现体验 | 参数化动作、阻塞原因、delta/full 切换、最近事件与状态注号 | Markdown 可解码出当前合法集合；delta 重建等于完整块；同一场景反复观察不消费历史；时间流逝不误归因 |
| C：两地点与耗时行动 | 接入 Spatial；移动/到达；再选一种可暂停或取消的行动 | 中途环境事件先后正确；同刻成熟/采集/光照结果有明确规则；冷开与连续运行一致 |
| D：一个社交闭环或 GUI | 一个 NPC 和一项交付任务，或三栏 WPF 阅读入口 | 可仅凭 UI 完成目标；旧按钮不误执行；正文选择/复制/FIFO 一致；两种 UI 使用同一事实 |
| E：按需求扩展 | LLM/笔记、多 Player、存档分支、战斗分别立项 | 逐项增加恢复、竞争或因果显示测试，不把所有路线捆成一个前置框架 |

建议第一片不搬整套 Mud Catalog、不启用复杂战斗、不做长期 Plan、不设计通用事件总线，也不要求先升级 Spatial 的预测表示。这些都不是“单角色看清状态并完成一次采集”的必要条件。

若本轮后续只想先验证阅读体验，还可以选一个更小的前导实验：在 ResourceCycle 上做 `next / look / mode` 交互观察壳，展示最近 occurrence 和状态标记，无需角色或背包。这里的 `next` 是控制仿真推进的调试命令，不应冒称玩家在世界中完成了采集；它验证文本接线，不能替代切片 A 的玩法闭环。

后续实现时最值得保留的自动化策略：连续运行与冷开轨迹对照、关键 E/S 中断点恢复、查询无副作用、UI 合法集合独立解码、相同事件时刻的手写期望，以及只读 UI 的盲玩验收。能编译只能证明接线完成，不能证明时间、因果与阅读体验正确。

## 9. 尚需选择的问题

这些问题应随对应切片裁决，不应先做成一个巨大引擎设计：

1. 第一项采集是否即时；如耗时，在开始还是完成时占用资源/扣体力？
2. 环境变化、行动完成和请求决策同刻时的顺序是什么？
3. 玩家可见哪些预测信息：大致成熟时间、精确时间，还是仅当前阶段？诊断与观察要分开。
4. 最近事件窗口是否在行动完成时关闭；重开/首次观察如何处理未读经历？
5. 两地点之后需要途中交互吗？没有该需求就先不启用 contact 系统。
6. LLM 首版允许哪种中断重做粒度？据此决定仅用内存 loop，还是先改 AgentCore 的子状态与提交边界。

本轮没有发现要求先整体改写 Kernel、DurableGraph 或 Spatial 才能开始小型可玩 Demo 的证据。主要障碍可分片落在应用层；应通过一个完整的小体验验证这些接缝，再据实际重复提取引擎能力。
