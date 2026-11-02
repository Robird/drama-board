# 派生预测改为运行时缓存

> 2026-10-27，Kernel/Spatial 已实施，见[验收记录](../experiments/runtime-derived-predictions-acceptance.md)。基础真相与已作出的决定持久化，纯派生预测在加载后重建。本文接替[上一轮方案](local-prediction-refactor.md)中预测落盘、预测替换 facts 及恢复禁止重算的选择；局部发现算法、时间语义和 E/S 发布边界继续保留。

## 1. 目标与当前事实

把两个独立决策分开：**运行中增量维护**，**冷开时全量重建**。不回到每次读取候选都全量求交，也不要求未来预测属于持久世界真相。

重构前基线为 `21379a1`：Spatial 的 `Business + Contacts` 同根持久化，Complete 在业务 facts 后追加 `ContactPredictionsReplaced`；E/S 冷开与全量参考曾验收，见[原验收记录](../experiments/local-prediction-acceptance.md)。本次调整保留局部维护与测试，只改变派生数据的存储和完成边界。没有旧存档兼容要求，不设计双格式或迁移器。

迁移涉及的接缝：

- [SpatialSimulation](../../src/Spatial/Simulation/SpatialSimulation.cs) 通过 Rebuild 全量发现，Complete 保留逐 fact 旧/新范围收集与局部维护。
- [SimulationKernel](../../src/Kernel/Simulation/SimulationKernel.cs) 的 Complete 在业务 Fold 后返回准备好的运行时世界。
- [IOccurrenceHistory](../../src/Kernel/Journal/IOccurrenceHistory.cs) 要求 State 返回已安装实例；不能把 State getter 改为每次创建 wrapper。
- Kernel/Spatial 首片仅机械适配 Playground；随后单角色实例已迁移，见第 9 节。多角色化不构成缓存拆分的技术前置条件。

## 2. 哪些保存，哪些重建

| 信息 | 本片归属 | 原因 |
| --- | --- | --- |
| 实体身份、位置/运动段、movement generation | 持久业务状态 | 描述当前真实运动及其身份 |
| ConsumedContacts | 持久业务状态 | 已经发生过的消费，单凭当前几何不能重建 |
| 明确安排的入口日程、已经作出的选择/随机结果 | 持久业务状态或 E | 不能在恢复时重新决定 |
| KernelCursor、pending E 中的业务事实 | 持久 | 确定已完成边界及待完成决定 |
| Contacts 中的 ContactKey/Due/Kind | 运行时派生缓存 | 可由业务状态、空间定义、确定性代码重建 |
| OccurrenceCandidate、成员/反向索引、dirty 集合 | 运行时 | 前者是投影，后两者是维护辅助 |

本片暂保留运动段既有 `ArrivalDue`，不把所有字段名含 Due 的数据一起删除。其冗余性与平衡更新语义见第 6 节。当前 `PassageContactKey` 仍用于持久 ConsumedContacts 和业务 contact fact，不能随预测模型一起删除其 durable 定义。

## 3. 最小状态与 API 形状

继续利用现有 `TWorld` 表示**运行时完整世界**，不新增 Kernel 第四个泛型，不把可变缓存塞进持久 Business，也不引入全局字典。

```text
运行时：SpatialSimulationState（普通不可变 CLR 对象）
          Business: GraphSpatialState（durable）
          Contacts: SpatialContactPredictions（普通不可变 CLR 对象）

磁盘 S：发布根
          Business: GraphSpatialState
          Cursor: KernelCursor

磁盘 E：OccurrenceEvent<GraphSpatialFact>
          已选 cause、target instant、有序业务 facts
```

删除 Contacts、ContactPrediction、SpatialSimulationState 的 durable 标记；删除 `SpatialSimulationFact` 包装层和 `ContactPredictionsReplaced`。两空间 rule 使用 `<SpatialSimulationState, SpatialOccurrenceData, GraphSpatialFact>`。缓存结构继续保持快照不可变，读取只投影候选。

Complete 采用以下单一接缝，**不保留两条完成流水线**：

```csharp
Func<TWorld, LogicalInstant, IReadOnlyList<TFact>, TWorld, TWorld> complete
// Complete(before, instant, recordedBusinessFacts, foldedBusinessWorld)
//     -> preparedRuntimeWorld
```

Kernel 先顺序 Fold 业务事实，再调用 Complete 准备派生信息，然后按目标时间 Validate。Complete 不增加/改变业务 facts，不改业务决定，不调用 Player、随机重抽或外部服务。没有派生状态的消费者显式返回最后一个参数。

Spatial 的中间 Fold 可保留旧 Contacts，作为尚未完成的 scratch；任何候选读取和完整校验只在 Complete 之后发生。Complete 从传入的最终业务态产生新 Contacts 并返回新 wrapper。为收集旧/新 passage，可继续重折业务 facts；首片接受与当前实现同级的额外 Fold，不为省一次 Fold 先扩展 Kernel 的通知协议。核对最终业务态保持不变；逐 fact 分类未知类型必须拒绝，不能只比较净结果。

`Rebuild(business, boundaryTime)` 显式从全部 passage 创建就绪 wrapper，供 genesis、冷开和测试使用。增量 Complete 与全量 Rebuild 共用精确数学，但 oracle 的配对发现继续独立。缓存不在运行中自动全量修补漏项，漏项仍由测试捕捉。

## 4. 发布、恢复与引用身份

正常推进：

```text
读取缓存投影的 candidates → 选 winner → Plan 得到业务 facts
→ 顺序 Fold → Complete 准备新缓存 → Validate(目标时间)
→ 发布 E（只有业务 facts）→ 发布 S（只有 Business + cursor）
→ 安装准备好的运行时 wrapper
```

Complete/校验在 E 前失败：旧世界、缓存、历史不变。E/S 发布失败：沿用 fault/reopen，不尝试复用不确定发布结果。不得把新缓存提前写入旧 wrapper 或公共 session 字段。

持久 history adapter 保存一个稳定的运行时 `_state`：

1. Open 时加载业务根与 cursor，按 cursor 时间 Rebuild 一次并校验，然后提供给 Kernel；不得每次读取 State 都重建。
2. CommitState 只序列化传入 wrapper 的 Business 与 cursor；成功后在 adapter 中安装**传入的同一 wrapper**，再正常返回。Kernel 仍执行现有 history 对齐检查。
3. adapter 是本会话独占；不能把某一规则配置下的缓存借给另一个配置的 session。

pending 恢复：先把旧 S 重建为就绪运行时世界，再 Fold 保存的业务 E，调用纯 Complete，验证并发布 S。**恢复允许重复派生计算，仍禁止重新仲裁 pending、Plan、询问 Player 或重做外部副作用。** 不为省一次旧 S 重建引入“未就绪世界”状态机。

Replay 每批同样 Fold → Complete → Validate；其初始输入必须已经 Rebuild。空前缀仍按 genesisTime 验证。SchedulerConformance 从重建/维护的预测选赢家，再折叠保存的业务 facts；不调用 Plan。Fork 转交相同完成/校验逻辑。E 已保存的 cause/target instant 不被新预测改写。

这会修改上一版“恢复 Complete 零次”的测试：新约束是**规划与 Player 零次，派生维护可以执行且无副作用**。Complete 与 Rebuild 的调用次数只用于证明入口合同，不能把纯缓存计算再次绑定成不可重做的业务行为。

## 5. 重建正确性

同一业务状态、空间定义与规则版本下，全量 Rebuild 和增量维护必须得到相同的 ContactKey、Due、Kind 以及候选键/赢家。定义与规则在一个 session 内固定；本片不提供配置热替换 API。

- 已消费的接触不能重生；继续保留 ConsumedContacts 及旧段退出时的回收。
- 未消费且 Due 等于边界时间的接触必须保留，不能用 `Due <= now` 全部删掉。
- 出现 Due 早于完成边界仍拒绝，不为掩盖错误静默丢弃或夹到 now。
- B 转向、新人入场、空预测通道、同批多次运动变化继续通过 dirty passage 发现；不依赖旧预测查找过程。
- 缓存与对应 Business 一起以运行时 wrapper 安装；旧 wrapper 保持可读，不产生跨快照污染。
- 存档不记录预测缓存类型或替换事实。内存 history 可以保存运行时 wrapper，它不是磁盘存储格式。

## 6. 平衡性调整：本片提供什么，不自动决定什么

不保存缓存减少派生格式的兼容负担，并让重建自然使用新的预测实现；**它不会自动改变仍保存在业务状态中的参数快照**。

当前 [TraversingLocation](../../src/Spatial/State/SpatialLocation.cs) 保存 `SpeedSnapshot` 和 `ArrivalDue`，`ArrivalDue` 可由 anchor、目标、速度和 passage 长度算出，但现在同时进入验证与候选键。修改“默认移动速度”不会改变已开始的段。当前 [PlaygroundPlant](../../src/Playground/PlaygroundModels.cs) 保存 `RequiredTicks`，所以修改创建植物的默认值也不会改变已有植物。

建议参数更新另按下列具体业务问题处理，**不随缓存移除隐式实施**：

| 需求 | 应显式决定/实现的事情 |
| --- | --- |
| 新动作/新生长周期采用新平衡值 | 读取新版定义；明确已有过程继续旧参数 |
| 在途角色立即采用新速度 | 先按旧运动求边界时刻的真实位置，再重新锚定、设置新速度、推进 generation，触发预测失效；直接改旧段速度会追溯改变过去位置 |
| 已开始的植物采用新刷新间隔 | 明确保留已累计有效时间还是完成比例，以及新门槛已经达到时如何完成；可能需要在当前时刻产生合法业务转换 |
| 去掉 ArrivalDue 等可推导字段 | 独立调整业务模型、验证与候选键；用时序测试证明不改变既有段语义 |

空间定义/算法版本改变后，旧 pending fact 的合法性复核可能失败。缓存不落盘不能保证任意版本互通。首片只承诺相同规则下断点恢复；若要把平衡更新作用于已有存档，应在完成 S 边界实施明确的业务参数调整，并定义 pending 先完成还是拒绝升级。暂不建设规则版本注册表、迁移框架或自动热更新。

## 7. 实施顺序与委派边界

以下为本片的实施分工。后续迁移 Playground 时重新读取 PROJECT-STATE、工作树与应用现状，确认另一会话变更；旧方案的 S3 不再按“预测结果继续落盘”推进。

1. **Kernel 接缝**：把 Complete 移至 Fold 后并返回运行时世界；normal/pending/replay/conformance/fork 统一调用；Host、测试与 Playground 先机械接线。验证 E/S 失败、取消、重入、同 tick 和带时间校验不退化。
2. **Spatial 运行时模型**：保留局部发现，移除持久预测及替换事实；提供 Rebuild/Complete/Validate，候选只读；迁移所有真实调用点。同步删除旧测试适配中多余包装。
3. **持久化闭环**：测试 adapter 的磁盘根仅保存 Business/cursor，稳定缓存运行时 wrapper；S 冷开和 E-only 恢复实际重建。更新生成定义与 schema，禁止为通过旧读档测试保留双格式。
4. **验收与文档**：独立审查业务/缓存边界；更新原方案和原验收记录的适用范围，PROJECT-STATE 改为新的下一步。

Kernel 与 Spatial 可在签名确认后并行；持久化支撑与现有测试迁移分配独立文件；主线程负责集成构建、Schema Verify、最终验证。必要的上游修改以真实阻塞为依据，当前没有发现必须改 DurableGraph 的证据。

Playground 后续片采用同一“运行时 wrapper + durable Business”原则，把预测维护移出 Plan，移除 E/S 中的派生 Forecasts。Bindings 是否也是代码/业务对象可重建的索引应单独分类：固定路由可重建，显式动态订阅若承载业务决定则保存决定。用户已选择先在单角色版本完成迁移，再独立扩展多角色；原协调要求针对同文件并行编辑，不是架构依赖。必须保留 Due/Kind 优先级、已消费 owner 的重算、无输出过程的订阅和角色唤醒。

本片完成边界是 **Kernel + Spatial 缓存/恢复闭环及其他消费者机械适配**；不夹带运动参数热更新、资源进度语义重设计、通用缓存框架或持久预测可选开关。之后再做 Playground 的实际迁移与候选读取命名收敛。

## 8. 验收清单

1. 原局部预测轨迹与独立全量 oracle 继续通过，包含 B 反转新 BC、无关通道不求交、多代数、消费回收及非邻接 same-tick 赢家。
2. 正常增量结果等于从同一完成 Business 全量 Rebuild；固定种子多步轨迹在各边界执行对照。候选读取不触发全量重算。
3. 实际 DG S 冷开恢复相同业务状态、重建相同候选，继续产生相同事件轨迹。保存对象图及 E 的类型/字段证明不存在 Contacts 或预测替换，而非只检查内存属性为空。
4. E-only 冷开重放业务决定，允许 Rebuild/Complete，Plan/Player/外部调用使用抛错探针证明未执行；cause/时刻保持。新人产生的新接触仍能恢复。
5. Complete 抛错、校验失败、E/S 发布失败时，旧运行时根及其缓存不变；S 成功前不安装新根；adapter State 引用稳定。
6. 空 Replay/Fork、每批完成时间、pending 时间校验不退化；同 tick 已消费接触不再出现，未消费接触不能被清掉。
7. 调整派生计算但保持业务事实语义有效的定向测试，证明冷开用当前算法重建而非读取旧缓存；不要把它称为任意平衡版本兼容。SpeedSnapshot/RequiredTicks 的既有语义有明确说明。
8. 运行 solution Release 构建、Schema Verify 与全套测试；记录本轮实际结果，不能沿用基线的通过数量作为新证据。

实施与验证结果见[缓存验收记录](../experiments/runtime-derived-predictions-acceptance.md)。第 7 项采用缺失/陈旧运行时缓存重新落盘并冷开的定向实验，证明读取当前业务重新发现；没有为测试增设产品算法注入接口，也不声称验证了任意跨算法版本兼容。

## 9. ResourceCycle 单角色迁移

> 本节保留当时的迁移记录与旧类型名；当前命名见[Playground 重命名说明](../implementation/playground-rename.md)。

本后续片不等待多角色化，仍保留批处理与单角色采集两种入口。实现与证据见[ResourceCycle 缓存验收](../experiments/resource-cycle-runtime-cache-acceptance.md)。

- `CycleBusinessState` 保存 Time、Light、Plants、Script 与可选 Character；角色和背包继续作为独立业务子图。`CycleDurableRoot` 只保存 Business 与 Cursor。
- `CycleWorld` 是普通不可变运行时包装，持有 Business 与 Forecasts；固定 bindings 及其索引由当前业务身份和代码生成，不是动态订阅决定。`CyclePrediction`、`CycleForecasts`、`CycleBinding` 不再注册为 durable 类型。
- `CycleFact` 仅保存已决定的业务子图变化。Plan 不再生成预测表；Fold 应用业务，Complete 按有序 facts 收集受影响 owner、合并 dirty 后从最终业务状态重算。已消费 owner 必须包含在内，无当前输出的植物仍保留订阅。
- `Rebuild(business, boundaryTime)` 负责初始世界与冷开；运行中候选读取仍只投影缓存。按 Due 再 Kind 的应用优先级保留，同刻先成熟、光照、销毁，再等待完成，最后决策。
- History 打开时重建一次；State 返回稳定运行时实例。S 落盘成功后才安装传入 wrapper。pending 恢复只折叠已记录业务并完成派生缓存，不再询问 Player，也不重新选择行动。
- 验收同时比较增量、全量维护、从业务重建与独立业务公式 oracle；真实磁盘 S/E-only 恢复继续相同轨迹。缓存重算计数允许包含 pending Complete，不再把“恢复零派生计算”视为合同。

本片不引入多角色调度、动态订阅、参数热更新、通用缓存框架或旧档兼容。`RequiredTicks`、等待截止时间等业务决定仍保存，删除预测不会隐式改变已开始的过程。
