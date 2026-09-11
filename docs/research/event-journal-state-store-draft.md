# EventJournal + StateStore 两层持久化草稿

> 状态：用户明确要求独立浏览事件；当前方向为 Event/State 独立图、交错提交与快照语义，撤销强制 PairRoot/联合加载。
> 本文是可编辑设计草稿，API 与底层实现尚未冻结，产品尚未实现。按结论替换正文，不追加讨论日志。入口：[项目状态](../../PROJECT-STATE.md)。

## 1. 消费者合同与来源

| 来源 | 当前方向或边界 |
|---|---|
| 用户要求 | 事件历史为应用主轴；每步处理后的对象图直接保存，正常恢复不执行历史业务 reducing。 |
| 用户要求 | 下层 StateStore 保存强类型领域图，上层 EventJournal 的 EventFrame/StateFrame 引用相应图并提供 branch/ref。 |
| 用户新增的明确用例 | 可以遍历和读取事件，不为每个事件反序列化完整 State；事件与状态应能独立读取。 |
| 用户当前方向 | Event/State 以独立根交错提交；Event 引用领域 Snapshot，避免引用大批活动可变实例。通过文档/XML doc 说明建模合同。 |
| 保留的使用规则 | 生成事件只读旧世界；处理器读取事件快照并更新领域状态。先按单活动分支会话、串行处理验证。 |
| 本轮边界 | 修订方案，不实施产品；异常/retry 账本、完整 Player/LLM 恢复、GC 与并发 merge 继续延期。 |

已保存 State 是历史处理结果，不默认当作可删缓存。逻辑完整图不要求物理全量写入，Base/Delta 由底层负责。
两层是职责划分，不意味着删除 SchemaStore/Generator 或为领域事件另建 JSON Schema/正文 codec。

## 2. 独立根与一条逻辑历史

```text
Journal: S0 → E1 → S1 → E2 → S2

EventFrame: Kind=Event, RevisionAddress, EventRootId, 必要 Meta
StateFrame: Kind=State, RevisionAddress, StateRootId, 必要 Meta
```

每次 CommitDomainEvent 提交事件根及其可达快照闭包；每次 CommitDomainState 提交完整领域状态根。调用方不必提供另一根或构造 PairRoot。
E1 的直接前帧是其处理基态 S0；S1 的直接前帧是产生它的 E1。按所选分支的逻辑链解释，不能按物理追加顺序或仓库任意“最新状态”配对。
初始记录是没有前事件的 S0，随后按 E/S 交替；这个使用协议不要求 StateStore 理解业务事件种类。

State 不必为关联而嵌入 LastEvent；Event 不必嵌入整个 LastState。两者关系由 Journal 地址链表达，不强迫领域图保留历史对象。
领域确实需要引用某个事件/状态快照时仍可显式建模，并承担相应可达闭包；不禁止合法的领域引用。

这里固定的是 Journal 的逻辑顺序。StateRevision 的增量 Parent、成员集、捕获基线和复用方式留给 DurableGraph 设计，不从接口独立推断必须是兄弟或线性 Revision。
PairRoot 不再是默认合同或独立浏览的前提；特定消费者可以显式组合对象图，但不能强迫所有读者都这样加载。

## 3. 读取与续局是不同用法

| 概念入口（命名未冻结） | 对外合同 |
|---|---|
| 遍历帧/筛选 EventFrame | 沿指定分支正序或倒序浏览元数据，不自动载入世界。 |
| ReadEvent(eventFrame) | 返回该次记录的强类型事件及快照闭包，不反序列化无关的 State 对象。 |
| ReadState(stateFrame) | 返回该次记录的完整领域状态，不因基础设施关联自动加载前事件。 |
| Resume(branch) | head 是 S 则加载 S；head 是 E 则取得 E 与其对应前置 S，便于继续处理。两图不要求共享 CLR 实例表。 |

Resume 可以是一项便利 API；它不将联合加载变成 ReadEvent 的前置条件。正常循环仍可表达为：

```text
session = Resume(branch)
if session.HeadKind == StateFrame:
    event = GenerateSnapshotEvent(session.State)
    session.CommitDomainEvent(event)
    安排处理管线
else:
    nextState = Handle(session.State, session.PendingEvent)
    session.CommitDomainState(nextState)
```

E-head 已表达待处理工作，进程退出后无需依赖之前的调度通知。S 完成后才推进领域版本/逻辑时刻并重新 Forecast。
只浏览事件不要求恢复可写世界，也不应借此调用 Player 或执行历史业务 reducer。

独立读取并不承诺零历史 I/O：允许读取必要 Journal/Schema/索引及重建事件闭包所需的旧 Base/Delta。
要求是不反序列化与所选事件无关的世界，而非禁止访问任何存放过 State 的文件字节。事件主动引用整个世界时，该世界已属于其闭包，文档不能凭名称将它变小。

## 4. 快照语义与身份

此前共同加载用于维持 `event.Target` 与当前实体的共享可变身份；采用事件快照后，该要求撤销。
Event 的目标表示记录时的观察/领域快照；State 的对象表示所读状态版本。两次加载得到不同实例、乃至不同版本的值，本身都不构成错误。

```text
ReadEvent(E1).TargetSnapshot.HP = 10
ReadState(S1).Alice.HP = 7
```

同一个事件或状态图内部原有共享/循环仍须正确恢复；跨 ReadEvent/ReadState 不承诺 ReferenceEquals，也不能仅凭存储 ObjectId 认定业务上是同一个当前实体。
处理器若需定位当前对象，应使用领域已有的身份或其他明确规则；事件快照是读取材料，不通过修改 event.Target 来间接修改当前世界。
相同业务身份的旧快照与当前实体可以并存，框架不自动将两者合并或重定向。

事件被记录后，领域代码将其及可达快照视为只读，不随后续处理改变。Snapshot 命名、readonly 外壳或 IReadOnlyList 接口不自动隔离可变集合/元素；DG Capture 也不冻结原 CLR 对象。
不可变领域子图可以共享；如果活动世界有可变别名，需在建模时隔离会被写入的可达部分。小闭包是建模目标，不要求整世界深拷贝、平行 Saved/DTO 模型或自动冻结框架。
避免快照回指活动世界容器、会话选择器或完整历史链。已有不可变替换式 reducer 可让事件继续保留旧对象，不要求改成稳定可变实体。

公开文档与 XML doc 至少说明以下内容（待上游 API 定稿时落到实际方法）：

```xml
<remarks>
事件及其引用表示记录时的领域快照；请避免引用会继续变化的对象或无关的大对象图。
读取事件会恢复其可达闭包，不保证与独立读取的状态共享 CLR 实例。
若需查看当时的完整世界，请沿事件关联的 StateFrame 另行加载。
</remarks>
```

这是一份明确的使用合同，不声称 XML 注释本身能自动检查别名、冻结对象或限制闭包大小。

## 5. 发布与恢复的最小边界

Journal branch ref 是唯一发布前沿。每次准备本次图 → Schema/Revision 完成规定持久化 → 追加引用该图的 Frame → 推进 ref → 接受本次保存结果。
追加图不等于发布；ref 只能指向内容完整持久化且根/类型符合帧角色的记录。各图如何维护比较基线由下层处理。

| 中断位置 | 重开含义 |
|---|---|
| 图或 Frame 已追加，ref 未推进 | 仍按旧 head 恢复，新增物理记录不算该分支进展。 |
| E 已发布，S 尚未完成 | 精确读取这条 E 及其同分支前置 S；两图独立实例化，事件待处理。 |
| S 已发布，调用方未收到成功 | 读取 S 即为已完成结果；不能仅因上次报错再处理同一事件。 |

Commit 错误先停止并重开判定 head；不承诺自动 retry、透明回滚或未经验证的断电保证。
分叉/移动 ref 后创建与所选历史点对应的新会话，工作实例隔离；不能以旧会话的基线继续写入。

## 6. 接入边界与最小验证

对外独立读写已经消除了跨两份图合并 CLR 身份的正确性要求，但不证明当前入口可以直接交替换根。
此前核对的 [WorldWorkspace](../../../durable-graph/src/DurableGraph.StateStore/WorldWorkspace.cs)、[CaptureSession](../../../durable-graph/src/DurableGraph/CaptureSession.cs) 与 [ObjectRevisionPlanner](../../../durable-graph/src/DurableGraph.StateStore/ObjectRevisionPlanner.cs) 仍有固定根、候选绑定替换和完整成员集 Remove 语义；内部复用/比较基线由 DG 细化，不转嫁为消费者维护 ObjectId 的义务。
[RevisionDecoder](../../../durable-graph/src/DurableGraph.StateStore/RevisionDecoder.cs) 当前全 live 行解码的路径不能仅改名就宣称满足独立浏览；需用实际读取验证独立事件成员集或按根读取方案。
[GraphRepository](../../../durable-graph/src/DurableGraph.StateStore/GraphRepository.cs) 原 publication 与 [EventJournal](../../../atelia/src/EventJournal/EventJournal.cs) / [Refs](../../../atelia/src/EventJournal/EventJournal.Refs.cs) 的发布职责仍需接通，不能叠成两个权威 head。
上述为实现边界提示，本轮未实施或跑测试，不在此冻结底层 Parent/新类型/编码方案。

最小见证使用 `World(Alice, Bob) + Event(AliceSnapshot)`，事件不引用 World/Bob：

1. 构造交错 E/S 历史，打开只读历史并仅浏览 E；从打开到遍历，无关 World/Bob 的 typed 解码、Allocate、Hydrate 均不发生，而事件快照正确。仅“打开时已解码整个世界，遍历时不实例化它”不算通过。
2. 只读 S 不自动加载前 E；读取任意 E 仍保留旧值，后续当前 Alice 为新值。集合/元素变化不能暗改事件快照。
3. E-head 冷重开与连续处理等价，选择正确同分支前 S，Bob 不丢失；不检查跨图 ReferenceEquals，只检查各自图内原有共享/循环。
4. 分支选择、历史隔离与三个发布中断位置按上表验证；在不执行历史业务 reducer/Player 的情况下读取结果。
5. 用真实生成模型/包消费验证，并分别记录事件浏览读取量与保存写入量；不把性能结论从接口形状推导出来。

## 7. DramaBoard 消费者目标对照

本节只对照 [Kernel 目标设计](../design/simulation-kernel.md) 与 [Graph Spatial 目标设计](../design/graph-spatial-world.md)，不计现有代码迁移成本，也不选择 DG 内部的比较/编码优化。
评审结论以“独立浏览事件”也得到满足为前提；仅证明完整续局可行，不足以证明历史浏览的易用性与读取成本。补足该合同后，主要需求仍落在通用强类型图、历史地址与发布上，无需增加领域专用存储机制。

| 目标设计需求 | 消费者使用方式 |
|---|---|
| 完整世界、跨域原子性（003 §3、§7；008 §4.4） | State 保存 Game+Spatial+Kernel 完整边界；一个 E/S 对对应一次 Occurrence，多条 facts 不拆成可观察的部分世界。 |
| 确定性时间与仲裁（003 §2、§4、§9） | State 保留逻辑时刻、版本、seed/规则所需状态；E 记录已选 cause 及领域事件内容。下一次只从完成状态 Forecast。 |
| Lazy traversal 与接触进展（008 §2.5–2.6） | 保存 anchor/time/speed/generation、entry overrides、scheduled patches、consumed contacts；位置/路线/关系继续由领域查询推导，不生成逐 tick 存储协议。 |
| 恢复、历史与分叉（003 §8；008 §6.3） | 正常恢复直接 Load 已存图，历史沿 Journal 查询；DramaBoard 的可玩 fork 选完整 StateFrame，创建新 lineage 并继承世界/count/instant/seed。 |
| 独立事件历史消费（用户本轮明确要求） | 沿 Journal 选择 E，只读取事件快照闭包；需要某一时点世界时再显式 Load 对应 S。 |
| 内容与信息边界（008 §5.4、§6.2） | 保存所用不可变 Definition 或能精确恢复它的绑定；完整持久图属于宿主，Player 仍只收到合法投影，不直接获得事件/世界根。 |

这里的 E 应由已选 winner 的可信领域流程产生，不是每个 Forecast candidate、每条子 fact 或未经校验的 Player proposal。
已选 cause 的 CandidateKey 需要保留；全量候选集合、临时 owner map 与 Forecast/query cache 不需要保存。领域边界字段可放在领域根/事件对象中，不要求通用 Journal 或 StateStore 理解它们。

两份目标文档尚含旧存储约定，后续应定向修订，不能称为逐字兼容：

- 003 §5/§7 的单帧发布改为 E/S 阶段；只有 S 完成才推进 WorldVersion/LogicalInstant、发布世界与重新 Forecast。E 已发布而 S 未完成时保持待处理状态，失败“零已完成 transition”不再等于“物理 Journal 零新增”。
- 003 §8.1、008 §6.3 的正常恢复从 facts fold 改为加载状态；运行时领域状态更新与可选 conformance 验证仍可保留，不要求恢复重新调用 Player。
- 003 §8.3 的完整边界 fork 继续由应用限制在 S；存储能从 E 分叉不自动授予游戏中途分叉语义。新 lineage 是领域分支身份，不能直接照搬旧 State 中的完整 WorldVersion。

本节是消费者契约评审，未改上述目标设计正文。E 相对 Plan/校验的精确位置和接缝命名在 DramaBoard 后续设计中收敛；这些是领域流程映射，不是缺少新的存储原语。
