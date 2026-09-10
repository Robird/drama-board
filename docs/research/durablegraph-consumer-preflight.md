# DurableGraph 消费者前置研究

> 状态：提案待裁决；2026-09-11 静态核对 DramaBoard `bd73e64`、DurableGraph `2fbcca4`。
> 本轮仅阅读源码、测试与当前项目状态，未运行构建/测试，未接入持久化。入口：[项目状态](../../PROJECT-STATE.md)。

## 建议先回答的问题

首个切片建议采用现有 FirstBoard 的 **Alice/Bob 同 Passage 相遇 → Game 打开 encounter → Alice 继续/反向 → 后续到达**。
在相遇已打开、响应尚未执行的完整 Occurrence 边界保存，正常关闭后重开，再执行同一个确定性响应。
这比只保存坐标更能暴露跨模块恢复错误：漏存 `ConsumedContacts` 会重复产生接触，漏存 `PendingEncounter` 会丢失玩法回应。
这是候选范围，不表示模型、提交边界或实现已获批准。

直接沿用 [PassageEncounterHostTests](../../tests/FirstBoard.Tests/PassageEncounterHostTests.cs) 的
`CreateTravelingPrefix`、`SingleDriver_ContinueRunsPlayableCoreAndBuildsLocalRequestWithoutKnowledgeGetter`、
`Reverse_CommitsGameThenSpatial_ClearsGoalAndReanchorsMovement`。
前缀经真实 reducer 创建并验证，接触时间为 150000 ticks；不新造独立小世界。
首轮主线选 Continue，Reverse 作为同一前缀的加强验收，入口变化沿已有关闭反向入口用例补充。

**边界排除**：fork/rewind、完整 Player closure、LLM 调用/执行栈恢复不进入这份首个候选的验收。
用户尚未裁决前两项的优先级；这里的缩小范围不是用户决定放弃它们。
确定性 driver 应仅由恢复后的 request 决定响应，避免测试自身队列游标变成未保存状态。
已属于 `FirstBoardGameState` 的 `KnownFacts`、DecisionSequence 等仍要保存，不能借排除 Player closure 将其删掉。

## 必须共同恢复的状态

| 范围 | 最小完整闭包及依据 |
|---|---|
| Game | `WorldSeed`、`NextPersistentId`、`Now`、全部 Actors/Objects、知识全文、活动、目标、两项机关标志、`PendingEncounter`；见 [FirstBoardDomain](../../src/FirstBoard/FirstBoardDomain.cs)。 |
| Spatial | Entities 的 ID/MovementGeneration 与完整 AtPlace/Traversing 状态、entry overrides、scheduled changes、consumed contacts；见 [GraphSpatialState](../../src/Spatial/State/GraphSpatialState.cs)、[SpatialLocation](../../src/Spatial/State/SpatialLocation.cs)。不能只持久化按当前时间算出的坐标。 |
| Kernel 边界 | `WorldVersion` 的 lineage/count、`LastCommittedInstant` 的 ModelTime/CausalOrdinal、有效 genesis/rules 配置；重开后同一模型时间的下一次仲裁必须继续原序号。候选键通常从世界与规则重算，已消费状态不能丢。 |
| Content 身份 | 固定场景 definition 的 canonical bytes/hash 与 seed，重开校验后重建 Graph；利用 [ScenarioDefinition / ScenarioInstance](../../src/FirstBoard/ScenarioDefinition.cs) 的 Freeze、canonical JSON、DefinitionSha256。首轮可由固定测试 artifact 提供原文，不能按同名“最新默认场景”静默替代。 |

[CreateKernel](../../src/FirstBoard/FirstBoardScenario.cs) 已接收恢复后的 world/version/last instant，但它仍要求 Journal 全量批次数与 version 对齐。
[SimulationKernel](../../src/Kernel/Simulation/SimulationKernel.cs) 的 `ValidateCommittedBoundary` / `EnsureJournalAligned`
明确检查 count/head，`StepAsync` 则 scratch-fold → Validate → AppendBatch → 安装世界与游标。
所以“Load 世界，然后传入空 InMemoryJournal”不能被描述成现成续局方案；正常重开也绕不过这条接缝。

## 两个最小的直接领域模型候选

两者都让 DG 生成领域状态，不另建完整 `SavedActor ↔ BoardActor` 等平行模型，也不把全世界 JSON blob 当作直接模型接入。
两者都需要确定新的发布接缝；替换 IJournalSink 的落盘代码本身不足以提交 scratch world。

| 候选 | 最小形状与 reducer 衔接 | 代价 |
|---|---|---|
| A：固定会话根，保留不可变世界替换（推荐先试） | 新建一个稳定的 durable session root，持有 `CurrentWorld` 与 Kernel 边界字段。将当前领域闭包中的 record class 原位改为受支持 partial class，显式持久字段及只读查询；小值可保留/改成 partial record struct。集合以具体数组/List 字段存储，公开只读视图。reducer 仍构造隔离的下一世界，验证后仅替换根的 CurrentWorld 引用。 | 要替换 `with` 与 record 自动相等性的用法、显式检查集合不被泄漏修改；恢复绕过构造器，交付前须运行完整领域校验/重建 transient。每次替换的对象/集合可能获得新 ObjectId、产生 Base/Remove，不能预称为细粒度增量。 |
| B：固定根与稳定可变领域实体 | 同样直接调整领域声明，但 Actor/SpatialEntity/容器在成功提交后保持实例，按完整 Occurrence 更新字段；业务 ID 与 DG ObjectId 分开。现有事件、规则与玩法断言尽量复用。 | 若在原世界上逐 fact 调用可变 reducer，会破坏当前 scratch 原子性；必须先做隔离 draft/副本验证，再受控应用及提交，并定义失败后停止/重开。这比 A 多一项事务式应用机制和规则改造，不能以“加 DurableType”概括。 |

推荐 A 的理由是保留已验证的纯 fold 和失败隔离，先测真实消费者的模型改写量；固定外壳解决的是 GraphSession 根约束，
它不要求所有子对象可变。若对象替换导致实际写入成本不可接受，再以量测证据选择局部稳定身份；不要预先改造全库为 B。
实施前仍需列出完整持久闭包清单，显式包含 `SpatialLocation` 抽象 record 及 `AtPlaceLocation`、`TraversingLocation` 两个派生类型，以及 `GraphSpatialState` 的具体集合；不能仅核查 FirstBoardWorld/Game/Actor。这是待做核查，不在此展开 Schema 设计。
业务标识如 Actor.Id、MovementGeneration 仍保持原语义，不能用跨仓库无业务含义的 DG ObjectId 替代。

建议的 DG 路径提交契约（仍待裁决）：Kernel 交付验证后的 scratch world、下一 version/instant 给一个原子发布入口；
入口更新固定根并调用 `GraphSession.Commit`，成功后 Kernel 才安装该边界。DG 的已发布 root 是此路径重开的权威。
现有 EventJournal 路径保留原合同；DG 路径可保留内存 facts 供展示/oracle，但恢复不能再依赖它的全量 `Batches.Count`。
这需要显式的 checkpoint 恢复/提交接缝，不应顺手放宽所有 Journal 校验，也不应把 EventJournal 与 DG 两次独立提交当作一次原子提交。
若首次可用存档要求保留全量可查询事件历史，就必须另行决定其同提交存储形状，首轮不暗中实现第二条权威日志。
Commit 失败不会撤销已改的领域字段；首轮宿主可统一停止并 dispose/reopen，不能让“内存看似已前进”继续驱动游戏。

## 上游事实、反馈与待实验项

本轮重新读取 [DG 产品状态](../../../durable-graph/src/PROJECT-STATE.md) 与 [README](../../../durable-graph/README.md)，
并核对以下公开入口/测试；“存在测试”不表示本轮运行通过。

| 分类 | 本轮证据与结论 |
|---|---|
| 已有产品入口 | [GraphRepository](../../../durable-graph/src/DurableGraph.StateStore/GraphRepository.cs) 的 CreateNew/OpenExisting/Create/Load；[GraphSession](../../../durable-graph/src/DurableGraph.StateStore/GraphSession.cs) 的只读 World、Commit。单 head/单活动 session，根要求 DurableBase；固定根之外的对象引用可变化。 |
| 已有保存/续写机制 | [GraphRepositoryTests](../../../durable-graph/tests/DurableGraph.StateStore.Tests/GraphRepositoryTests.cs) 的 ThreeCommitsRetainInstancesAndAdvanceExactParentThenReopenFromPublishedWorldId、UnchangedCommitAdvancesRevisionWithoutRewritingObjects、LoadedUpgradeRewriteIsClearedOnlyBySuccessfulCommitAndLaterWritesUseDelta；不是缺少基本保存/重开 API。 |
| 确证模型摩擦 | [SG Ancestry](../../../durable-graph/src/DurableGraph.Generator/DurableSchemaGenerator.Ancestry.cs) 拒绝 record class；普通 class 自动属性也不能等同显式支持的字段。当前 [GraphSpatialState](../../src/Spatial/State/GraphSpatialState.cs) 的只读包装容器不能照抄为持久字段。接口/List 子类拒绝见 [ListGeneratedStateTests](../../../durable-graph/tests/DurableGraph.Tests/ListGeneratedStateTests.cs)。 |
| 已有能力，先适配再反馈 | 当前支持 record struct、Nullable、数组、List、实验性 Dictionary，以及跨程序集 nominal/inline/继承；实际模型例见 [RecordConsumer](../../../durable-graph/experiments/PackageConsumerProbe/RecordConsumer/Model.cs)、[InheritanceLibraryConsumer](../../../durable-graph/experiments/PackageConsumerProbe/InheritanceLibraryConsumer/AppModel/Model.cs)。不能再以“值类型、集合或跨库完全不支持”为阻塞理由。 |
| 确证宿主接缝缺口 | DramaBoard 尚无上述世界+游标的 DG 原子发布/checkpoint 恢复路径；现有 AppendBatch 只接收 batch，不接收 scratch world。归属先在 DramaBoard 设计，不是已经证明 DG 必须增加事务 API。 |
| 确证上游范围限制 | GraphRepository 公共产品入口未提供 branch/Reset/根替换；底层显式 Parent 能力不等于可编辑历史会话。只有用户要求首轮 fork/rewind 后，它才成为本切片阻塞条件。 |
| 需要真实实验 | 现有领域闭包的声明改写量、跨项目登记/history 使用成本、只读视图与恢复校验、对象替换产生的 Base/Remove 数量、Commit 分配与文件写入量。支持特性的单项测试不能证明 DramaBoard 组合已接通。 |

反馈上游时给出最小领域声明、诊断/异常、实际 API 调用、预期业务行为，区分缺功能与消费者建模选择。
每个声明 durable 模型的项目应按 DG README 直接引用 Runtime 包并保存 `.dgschema`；仅 Runtime ProjectReference 不能代替包的 SG/history 接入验证。

## 验收与最小独立工作

1. 连续运行对照与保存/正常关闭/新进程重开/继续运行，从同一个 traveling prefix 出发；在打开 encounter 前、打开后、响应后分点保存。
   比较后续完整 facts 顺序、CandidateKey、LogicalInstant、DecisionId、可用回应及最终 Game+Spatial；接触不重复、回应不丢失、反向后的 anchor/generation 正确。
2. 复用 `WorldSnapshot` 快速定位差异，但补充 `NextPersistentId`、KnownFacts.Text 的结构断言：当前 [WorldSnapshot](../../src/FirstBoard/FirstBoardScenario.cs) 没有覆盖它们。
   对全部 Spatial 集合、pending contact key/kind、Kernel 边界和 content hash 做完整比较；保留 [FirstBoardReducer.Validate](../../src/FirstBoard/FirstBoardDomain.cs) 的领域校验。
3. 完整 Occurrence 的前/后边界不能混合；复用 [TravelGoalAtomicityAndReplayTests](../../tests/FirstBoard.Tests/TravelGoalAtomicityAndReplayTests.cs) 的 fold/append 失败经验。
   DG 已知 NotPublished/Unknown/Published 分类及重开行为见 GraphRepositoryTests；消费者至少验证失败后停止，重开按真实 head 恢复，不透明重试。
4. 基础路径成立后，用两次独立构建验证一次真实领域升版：可在 PendingEncounter 增加显式的回应策略版本字段，V1→V2 补旧行为值；
   这是建议的迁移探针，须先确认该字段的业务意义。保留 history，旧库加载、继续、再存、再次重开后行为仍与旧语义对照一致。
5. 记录手工改写的类型/字段/登记/升级代码，分别统计对象 Base/Delta/Remove、实际文件字节、Commit 时间与分配；首轮不给“增量一定更小”的门槛。

**首个可独立实施任务**：仅在 FirstBoard 测试中提取共用的 encounter prefix 与确定性 request→response driver，
形成一个完整 committed-boundary oracle：含上表字段、batch key/instant、下一 request，并加上上述快照遗漏字段的比较。
验收是原有 Continue/Reverse 用例和新增 oracle 在内存连续运行下行为不变；无需 DG、MCP、生产模型迁移或 Journal 权威裁决。
随后再批准 A/B 与提交接缝，实施真实包编译闭包和保存/重开；本提案不授权这些后续修改。
