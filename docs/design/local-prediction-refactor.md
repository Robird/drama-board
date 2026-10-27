# 局部预测重构方案

> 2026-10-27。经两轮辩证审阅后，S1/S2 空间首阶段已实施；入口与验证边界见[验收记录](../experiments/local-prediction-acceptance.md)。S3 ResourceCycle 共同维护接缝与 API 收敛仍待协调其多角色分支，S4 为可选优化。以下保留设计职责与验收契约；背景见[算法综述](../research/prediction-discovery/README.md)，取舍见[审阅裁决](../research/local-prediction-design-review.md)。

## 1. 需求账本与边界

| 要求 | 来源与当前消费者 | 本方案处理 |
| --- | --- | --- |
| 子系统维护预测过程，事件后仅重算受影响范围；第一版允许恒 true | 用户明确方向；Spatial、ResourceCycle | 先落实空间通道过程，统一读取与完成转换的接缝，再依据两个消费者提取共性 |
| 变化 B 后发现 BA、BC，包括此前无输出的 BC | 用户最新例子；现有分段匀速 passage | 由运动变化找通道，首版通道全配对，不依赖已有接触集合发现新人 |
| 过程可无输出但继续响应变化 | 用户方向、暂停植物与空通道的具体失败例 | 过程身份来自业务对象/静态定义，不能从输出表反推存活过程 |
| 没有旧存档兼容负担 | 用户明确 | 可修改 API/schema；不建立迁移器或长期双生产路径 |
| 暂不改变空间数学与业务时序 | 当前代码/测试；本方案的保守选择，非永久用户禁令 | 保留 floor-contact、ceil-arrival、候选键字节、同 tick 仲裁、严格内部掉头 |
| 完整转换先验证，E→S，S 成功才安装；pending 恢复不规划 | 当前 SimulationKernel 与测试 | 新预测结果进入 E facts；恢复 Fold 保存的结果 |
| 顺序执行，一次只推进一个 occurrence | 当前 Kernel 的 in-flight 防护 | 不引入并行求解、重入事件总线或跨提交 dirty 状态 |
| 不干扰并行的 ResourceCycle 多角色施工 | 用户分叉安排 | 首片不改其领域模型；共同 API 变更在后续集成片协调 |

本轮足以选择实现路线，无须用户再决定内部数据结构。精确时间改造、业务同刻批次、动态空间拓扑、连续加速度、多线程、分布式执行均不纳入。现有资料是证据，不把同一提案衍生的多份文档当作独立需求。

## 2. 最小运行模型

```text
读取当前已知到期项 → 仲裁赢家 → 业务规划得到有序 facts
→ 在 scratch 中顺序应用业务 facts，收集受影响范围
→ 最终 scratch 上重算 dirty 通道，更新接触输出
→ 把业务 facts 与预测替换结果组成完整 draft
→ Kernel scratch-fold/validate → E → S → 安装
```

保留两个概念：**预测过程**和它的**零到多项输出**。它们不要求两张通用持久表：

| 过程 | 存活依据 | 输出来源 |
| --- | --- | --- |
| 通道接触过程 | GraphDefinition 中的 passage；即使空通道也有发现入口 | 新增持久接触预测集合，按 passage 分区 |
| 实体到达过程 | 当前 TraversingLocation 与 MovementGeneration | 既有 ArrivalDue；只读投影为 candidate |
| 入口日程过程 | 当前 ScheduledPassageEntryChange | 既有 Due/patch；只读投影为 candidate |

“过程”在首片是业务 owner 的职责称呼，不要求新建到达/日程过程类型或注册动作。不为每个人对建立过程，不持久保存“不相遇”的负结果，不复制 arrival/schedule 的权威 Due。通道 ID 就是接触 owner 身份，PassageContactKey 就是输出身份；不再新增 OwnerId、SlotKey、OutputId 三套等价字符串。代码行为由静态类型/分派实现，无需持久 Delegate。

当前完整通道与恒速段已经覆盖所有潜在关系，故不需要邻域期限或额外维护 occurrence。每次成员/运动变化重新发现，足以保持未来接触集合完整。后续若缩小空间/时间覆盖范围，必须另行证明在任何新交互可被选中前已重新发现。

## 3. 状态、事实与不变量

建议新增组合根 `SpatialSimulationState`：

```text
SpatialSimulationState
  Business: GraphSpatialState
  Contacts: SpatialContactPredictions

ContactPrediction = { ContactKey, Due, Kind }
```

保留 GraphSpatialState 为业务子图，Contacts 为同根下独立预测子图。组合根是完成边界的容器，不新增世界 ID。GraphDefinition 沿用当前加载时传入的不可变内容约束，不在此片改变存储归属。ContactKey 内已有 passage 和双方 generation；CandidateKey 用既有算法生成，不另存一份字节身份。已发布子图及 facts 保持快照语义，维护不能原地修改 before。

`SpatialSimulationFact` 区分业务 fact 包装与 `ContactPredictionsReplaced`。后者保存需要替换的 passage 及其完整新输出；空集合明确清空，未列出的 passage 保持。一次完整 draft 最多包含一个末尾预测替换 fact，没有变化时可省略。不给业务 planner 写预测 fact 的权限。

这里的替换结果是发布记录，不是第二份长期权威；S 内 Contacts 是完成状态中的权威预测。模型验证与重算验证分开：

- 业务 reducer 继续逐 fact 检查业务不变量，不检查 Contacts；不能因为临时旧预测失效而拒绝正常业务 Fold。
- 完整组合根检查键唯一、端点同通道存活且代数匹配、未消费、合法 Kind、Due 不早于边界模型时间等结构条件；时间参数接线见第 5 节。完整性通过独立参考验证，不在每次完成校验中重新全量求交。
- PlanSelected 仍复核被选输出与业务真相，拒绝 stale；这不能代替维护与完整性验收。
- 事实 Fold 不运行发现/维护，但可以进行已有的确定性合法性复核，包括 contact 精确求交。

结构校验不能普遍发现缺项：B 入场后遗漏 AB，空 Contacts 仍可结构合法。正常发布依靠唯一完成入口与测试保证维护覆盖；故意漏维护的负例由独立参考发现，不承诺运行时检查任意丢失的预测。当前没有接受任意不可信 E 的产品要求，不新增持久 dirty 标志、批封印或覆盖证明来检测手工伪造记录。

初始化与冷开必须区分：新游戏在创建 genesis 前全量建立 Contacts，并按 genesisTime 检查所有候选不过期；已有在途初态若产生过期输出应拒绝，不能静默丢弃。已有新格式 S 冷开只加载 Contacts，并在需要索引时重建派生索引，不能偷偷重新生成业务输出。改变 GraphDefinition 或预测算法语义后重开新世界；旧存档不支持不等于可以默默改写已有 S。

## 4. 变化范围如何得到

首版直接从已应用的有序 GraphSpatialFact 投影变化范围，不新增持久通知日志。折叠每条 fact 时都能看到该条的 before/after，因此删除、替换和同批多次变化不会丢旧通道信息。dirty 是临时去重集合。

| fact | dirty 通道/处理 |
| --- | --- |
| TraversalStarted | 新段所在 passage |
| TraversalReversed | 旧段与新段 passage 的并集（当前两者相同） |
| TraversalArrived、EntityRemoved | 旧段 passage；无在途段则无 contact 影响 |
| PassageContactOccurred | 所在 passage；首版完整替换时过滤 ConsumedContacts |
| EntityPlaced | 在房间内放置，无 contact 影响 |
| 入口权限变化、入口日程创建/应用 | 不影响已有运动段，不重算 contact；日程变化由原业务记录反映 |

对未知空间 fact 不静默忽略：新增 fact 时须显式分类为无接触影响或给出范围，未识别类型拒绝规划完成。业务 facts 顺序保留；不得按前后净差异抹去消费、generation 等真实进展。

`ShouldRecompute(passage, notices)` 首版等价于该 passage 是否在 dirty 中；若需要全量对照则绕过全部粗筛，把所有通道纳入。无须先遍历所有持久预测再问它们是否失效。按 passage 的路由与最后的细判断可由一个方法实现。

完成整批业务 Fold 后，每个 dirty passage 只重算一次，读同一最终状态。枚举当前所有无序段对，过滤已消费，调用现有 PassageContactCalculator；新输出规范排序。B 改向后旧 BA 消失，新 BA/BC 按新段求交；AC 在通道粗粒度版本会被重复计算但语义不变。

没有运动变化只消费一个 contact 时，首版允许重算通道；后续可以直接删该输出。任何新 contact 的 Due 可以等于当前 tick，禁止擅自加一 tick 或按每角色每 tick 一次限流。

## 5. 规划完成的唯一入口

仅修改 SpatialContactOccurrenceRule.PlanSelectedAsync 不够：角色动作、入口规则或其他领域的赢家也可能改变 Spatial。必须确保所有进入同一个模拟根的业务 draft 经过维护，且只执行一次。

在 Kernel 的业务 Plan 返回后、现有 FoldAndValidate 之前增加一个纯同步 `Complete` 委托，与现有 Fold/Validate 的依赖注入方式一致；不新增单方法接口：

```text
Func<TWorld, LogicalInstant, TransitionDraft<TFact>, TransitionDraft<TFact>> complete
Complete(before, targetInstant, businessDraft) -> completeDraft
```

委托不认识 Spatial，构造时显式提供；尚未迁移的 ResourceCycle 和不需维护的测试显式传 identity，而不是以可选 null 隐藏遗漏。Spatial 工厂必须配置其维护实现，由组合层实施有序业务 Fold、范围收集、维护及末尾 fact 生成。Complete 不改变业务决定、cause、时刻和业务 facts 的顺序，只补入预测结果；不调用 Player、不产生外部副作用、不自行 Commit。输入已含空间预测维护 fact 则拒绝，避免双完成。抛错在 E 之前结束，不安装 scratch。

Kernel 仍从 before 对完整 draft FoldAndValidate 一次，作为将来恢复会执行的真实路径。预规划 scratch 与验证 scratch 不构成两份权威；为减一次 Fold 改写提交协议不在首片范围。RecoverPending、普通 Replay、SchedulerConformance 不调用 Complete。后者仍检查已记录预测上的仲裁合规，不等于预测完整性验证。

把既有 `Action<TWorld> validate` 收敛为 `Action<TWorld, ModelTime> validate`，不新增时间字段或另一条校验流水线：

| 入口 | 传入时间 |
| --- | --- |
| Kernel 从 history 打开完成 S | history.Cursor.CurrentModelTime |
| 正常提交、RecoverPending 的完整 Fold 后 | targetInstant.ModelTime |
| Replay、SchedulerConformance 初态（包括空历史） | genesisTime |
| Replay、SchedulerConformance 每批 Fold 后 | batch.Instant.ModelTime |
| SimulationFork | 转交相同校验给 Replay |

业务子图原有校验可忽略新增参数；空间组合根据此验证所有到期项（含 arrival/schedule 投影）不过期。禁止闭包读取尚未安装的新 cursor，禁止在中间业务 fact 后验证整个组合根。这个签名变更涉及 Kernel 的四个实现入口及 Host/Spatial/ResourceCycle 组装和测试，可机械迁移，不改变另一会话的角色业务逻辑。

空间组合器工厂配置 Complete、根 Fold/Validate 及候选投影。任何新发生源加入该工厂都受同一维护约束；不得只给某几个 rule 套装饰器。直接使用低层业务 reducer 仅可构造 scratch/测试输入，不得直接发布为完成模拟状态。初始状态也必须走工厂的 genesis 路径。

首片采用上述 Kernel 唯一完成接缝，不保留另一套组合器完成入口。当前 [SpacetimeHostScenarioTests](../../tests/Spatial.Tests/Acceptance/SpacetimeHostScenarioTests.cs) 已把到达、接触与自定义掉头发生源并列接入同一个 Kernel，跨来源覆盖不是假想需求。

## 6. 候选读取与共性抽取

首个空间切片仍可用现有 IOccurrenceRule 接线：其 Forecast 变成 Contacts 与已有 arrival/schedule 的纯投影，不做全配对求交。接触 payload 可并入现有 SpatialOccurrenceData 的分支，使空间工厂统一候选类型；不能因合并 payload 而改变 key 编码。规则执行选择后的业务规划仍存在，不能把“预测不重算”误写成“执行不校验”。

候选必须保持当前完整集合和既有 key 编码；调度仍线性扫描所有候选。旧全配对逻辑移为测试参考，不能留下另一个生产缓存与新输出表相互修补。

第二个消费者集成时，将 Forecast 统一改名为 ReadCandidates 或等价明确命名，删除过渡命名；调用点含 ForecastRound、回放调度合规检查、测试与 ResourceCycle。ResourceCycle 当前先按 Due/Kind 过滤，具有应用层优先级，不能把“纯读取”误解释为它也必须暴露所有候选给 Kernel，从而改变光照/成熟/角色决策的同刻顺序。

共同机制的最小目标是：候选只读投影、任意赢家的业务批次只经过一次 Complete、恢复不经过 Complete；领域 dirty、通知和存储形状可以不同。S3 必须把 CycleRule 内现有预测维护块移到相同完成接缝，不仅是给 Forecast 改名。

当前 CycleFact 已有可空 Forecasts：Plan 保留业务决定与 Player 调用，返回业务字段；Complete 基于有序事实与最终 scratch 维护预测，把结果写入最终事实的 Forecasts，删除 Plan 内旧维护块。不要求改变持久事实形状。原 `dirty.Add(prediction.OwnerId)` 的消费语义不可丢：当前可由事件种类/目标及变更的角色子图确定原生产者（如销毁脚本→script、成熟→plant、角色行动→actor），未知种类显式处理。应以集成时的多角色代码为准验证映射，不照搬此处单角色快照。两系统都应证明任意发生源只完成一次、恢复完成零次；泛型过程接口不作为验收门槛。

## 7. 实施切片与停止点

| 切片 | 交付与允许改动 | 验收/停止点 |
| --- | --- | --- |
| S1：空间完整纵向切片 | Kernel Complete/带时间 Validate 接缝、Spatial 组合根/接触输出/末尾 fact、通道级维护、所有空间候选读取与 genesis；ResourceCycle 仅机械更新接线 | B 改向发现 BC、新人入场、arrival/日程与contact完整轨迹；全量参考相等；内存 pending 恢复；可持久类型冷开验证。不只提交空框架 |
| S2：恢复闭环与负例验收 | 在测试支撑中以同一根记录空间世界、cursor、pending；复用 DG Repository 和既有 Kernel 接口，不建设通用存档产品 | S 冷开续跑与 E-only 中断续跑一致；恢复不调用 Complete；运行时拒绝错误 generation/过期 Due，漏生成由 oracle 发现 |
| S3：第二消费者与 API 收敛 | 协调 ResourceCycle 分支，迁移共同读取/维护约定，删旧生产 Forecast 计算入口 | 光照优先级、暂停恢复、角色唤醒及持久化轨迹保持；决定共享工具是否真能替代重复维护代码 |
| S4：有证据的局部配对 | 可选，把 dirty 通道全配对改为变化段 × 最终通道成员 | 与参考候选/轨迹相同；无关段对不求交；同时报告求交数与实际 CPU/分配，不承诺仅凭次数加速 |

S1、S2 是本次重构首阶段的完成边界；S3 是达成跨系统统一的后续必需集成片，不能把仅 Spatial 完成宣传成全部目标完成。S4 不是首阶段门槛。每片保持可构建，迁移现有测试真实调用点；允许破坏旧 API，不为旧签名长期保留 adapter。

## 8. 独立验收清单

1. **B 改向**：旧 BA 输出撤销、此前不存在的 BC 被发现；其他通道候选不变且不调用精确求交。
2. **生命周期**：空通道/新人、进出、同批两人加入、移除/掉头、多代数、消费后不重建；没有输出也能响应后续变化。
3. **时序**：非邻接同 tick 赢家反例、负时间 floor、同 tick 新段输出、arrival ceil、入口 no-op 日程消费、现有严格内部掉头约束。
4. **独立参考**：从最终业务状态全量枚举，不读取被测 dirty 或 Contacts；比较 ContactKey、Due、Kind、候选key和赢家。可共用精确数学，但配对/范围发现必须独立；数学边界由已有单测验证。
5. **跨规则来源**：非 contact 发生源改变运动也维护预测；一笔 draft 多条业务 fact 只维护一次；业务顺序不被重排。
6. **恢复**：完整 S 冷开、E-only 重开、维护抛错在 E 前、S 失败后 fault/reopen；对 Complete/Player 装抛异常探针证明恢复不调用它们。区分模型持久化资格与实际同根恢复。普通 Replay、空前缀及 pending 的带时间边界校验都应有负例，不能等下一次 Step 才拒绝过期状态。
7. **有界性**：移除旧段后旧输出可回收；不持久化 dirty、指标或永增的失效队列；按活跃段和真实接触量增长，不按经历回合无限积累。

完整性不能靠 validator 每轮全量求交来“保证”，那会把扫描成本搬个位置。测试/显式诊断保留全量 oracle；生产结构校验与选中项数学复核各守边界。现有 validator 的扫描、候选读取、不可变集合复制也可能成为瓶颈，本片只声称减少重复预测，不声称整体亚线性。

代码实施时执行影响范围的 Kernel/Spatial 测试、DurableGraph schema 更新与 Verify；涉及共同接口时再跑 ResourceCycle/Host 及 solution。文档阶段只检查引用与一致性，读过测试不记作本轮运行通过。

## 9. 延期项与重开条件

- 变化段 join：通道拥挤或通道重算实测显著时；输入必须包含旧/新段及同批变化，不能只传当前实体 ID。
- 成员/反向索引：扫描成员或清理输出成为实测成本时；从权威根重建，不能成为第二个独立状态机。
- 堆/时间索引：选最早事件成为瓶颈时；先定义失效项回收，再替换线性仲裁。
- KDS、格网、邻域期限：需要排除同通道大量不相关段时；单独裁决时间覆盖与同 tick 语义。
- 统一持久过程注册表、通用订阅总线：出现业务对象/定义不能表达的动态生命周期及两个真实消费者时再论证。

## 10. 代码证据入口

- [SimulationKernel](../../src/Kernel/Simulation/SimulationKernel.cs)：Plan→Complete→FoldAndValidate→E→S、pending 恢复与并发保护。
- [GraphSpatialReducer](../../src/Spatial/Facts/GraphSpatialReducer.cs)：逐事实业务校验、contact 消费与旧段清理。
- [SpatialSimulation](../../src/Spatial/Simulation/SpatialSimulation.cs)：已实现 genesis 全量发现、通道 dirty 维护、组合根 Fold/Validate 与工厂接线。
- [SpatialContactOccurrenceRule](../../src/Spatial/Simulation/SpatialContactOccurrenceRule.cs)：已改为持久 Contacts 投影，保留稳定 candidate key 与选中项复核。
- [SpatialOccurrenceRule](../../src/Spatial/Simulation/SpatialOccurrenceRule.cs)：arrival/schedule 已有 Due 的投影与消费。
- [CycleEngine](../../src/ResourceCycle/CycleEngine.cs)：持久输出、应用优先级、批内维护；属于第二消费者证据，不是空间设计的独立需求来源。
- [空间模型持久化资格测试](../../tests/Spatial.Tests/Acceptance/DurableModelQualificationTests.cs)：保留低层类型资格；实际单根恢复证据另见 [SpatialPredictionPersistenceTests](../../tests/Spatial.Tests/Persistence/SpatialPredictionPersistenceTests.cs)。
- [源码约束与反例](../research/prediction-discovery/dramaboard-constraints.md)：时间量化、同刻候选与旧/新关系寿命。
