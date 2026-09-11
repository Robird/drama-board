# EventJournal + StateStore 两层持久化草稿

> 状态：用户已选双根、线性 Parent 与事件快照方向；本文是可编辑设计草稿，具体 API 和实施分片尚未冻结，产品尚未实现。
> 按结论替换正文，不追加讨论日志。入口：[项目状态](../../PROJECT-STATE.md)。

## 1. 已选方向与来源

| 来源 | 方向或边界 |
|---|---|
| 用户已选 | 事件历史为应用主轴；每步处理后的对象图直接保存，正常恢复不执行历史业务 reducing。 |
| 用户已选 | 下层 StateStore 保存领域对象图；上层 EventJournal 的 EventFrame/StateFrame 均引用 StateRevision，复用 branch/ref。 |
| 用户已选 | EventFrame 包含 LastStateRoot + CurrentEventRoot；StateFrame 包含 LastEventRoot + CurrentStateRoot。双根可用虚拟单根表达。 |
| 用户已选 | 前一个 Revision 自然是后一个的 Parent，与 Journal 历史对应；不再选择兄弟 Revision 拓扑。 |
| 用户已选 | Event 按快照建模，引用领域对象快照，不引用随后变化的 mutable 领域对象；接受加载状态时连带加载最近事件。 |
| 用户目标 | 输入输出均为强类型领域对象，框架承担 Schema、序列化、身份恢复和增量编码；不另建 JSON Schema/事件正文 codec。 |
| 本稿收敛的使用规则 | 生成事件只读旧世界；处理事件保留事件快照并产生下一状态。先按单活动分支会话、串行处理验证。 |
| 本轮授权与延期 | 本轮修订文档，不实施产品；异常/retry 是否进入同一历史、完整 Player/LLM 恢复、GC 与并发 merge 继续延期。 |

已保存 State 是历史处理结果，不默认当作可从事件重建的可删缓存。逻辑完整快照不要求物理全量保存，Base/Delta 重建由底层负责。
两层是职责划分，不意味着移除 SchemaStore、Generator 或建立新的程序集划分。

## 2. 双根作为一种使用模式

建议先用一个固定的、可声明持久化的封套表达双根，无需先给 StateStore 增加通用多根 API：

```text
PairRoot { State, Event }
JournalFrame { Kind, RevisionAddress, PairRootId, 必要 Meta }
```

PairRoot 是框架/使用模式的选择器，不是业务历史对象。Kind 只在 Journal 一处维护；State/Event 槽是普通领域对象引用，帧通过 Revision 与 PairRootId 选定完整图。

| Journal 帧 | PairRoot.State | PairRoot.Event |
|---|---|---|
| 初始 StateFrame | 初始完整状态 S0 | null |
| EventFrame E1 | LastStateRoot = S0 | CurrentEventRoot = E1 |
| StateFrame S1 | CurrentStateRoot = S1 | LastEventRoot = E1 |
| EventFrame E2 | LastStateRoot = S1 | CurrentEventRoot = E2 |

初始帧后两槽均非空；E 提交只更换 Event 槽、保留原 State；S 提交只更换 State 槽、保留导致它的同一个事件快照。
State 领域模型不必再为存储关联重复保存 LastEvent。StateRoot 指领域世界，不是上一个 PairRoot。
PairRoot 不含 PreviousPairRoot；领域快照也不得回指这个可变封套。前帧/前 Revision 通过存储地址连接，不自动纳入当前领域图的可达闭包。
每帧保存的是双根联合可达图，旧对象仅在仍被当前世界或最近事件引用时留下；不为省加载重新拆成两个独立 Load。

## 3. 一条对应的提交历史

```text
Journal:       J_S0 → J_E1 → J_S1 → J_E2 → J_S2
StateRevision: R_S0 → R_E1 → R_S1 → R_E2 → R_S2
```

每个 Journal 帧选一个 Revision；其前帧所选 Revision 就是本 Revision 的 Parent。两层地址类型仍不同，但不暴露第二种可独立选择的父关系。
前驱只表示存储历史，不是领域对象引用。沿分支顺序保存一次联合图，就有一份身份表、一份比较基线和一个分配游标。
冷重开直接从 head 帧所选 Revision 加载 PairRoot，不再分别加载“最近 S”和“待处理 E”并合并对象。
同一加载图内保留真实共享/循环引用；独立历史查看、不同分支会话的 CLR 实例相互隔离，不建全库 ObjectId→实例缓存。

建议的最小应用调用形状，名称仅为草稿：

```text
session = LoadRepoAndBranch(...)
if session.Kind == StateFrame:
    event = GenerateSnapshotEvent(session.State)  // 不修改旧世界
    session.CommitDomainEvent(event)              // 保留 State
    安排处理管线
else:
    nextState = Handle(session.State, session.Event)
    session.CommitDomainState(nextState)          // 保留同一 Event
```

处理管线的安排不是另一份权威待办：即使发布 E 后进程退出，重开时的 E-head 已足以表明待处理。
业务的已完成步数按 StateFrame/领域游标解释，不直接沿用“Journal 总帧数等于 WorldVersion”的旧 Kernel 合同。

## 4. 事件快照与共享的边界

事件被记录后，其可达领域快照的持久值和引用关系不随后续处理改变。这是领域建模合同；DG Capture 冻结存储候选，并不会自动冻结原 CLR 对象。
只把 Event.Target 字段设为 readonly 不够：若 Target.Inventory 与活动世界共用可变 List，后续 Add 仍会改变事件快照；仅复制 List 而共享可变 Item 也不够。

最小建模方式是复用不可变领域对象及子图：更新时创建变化路径上的新对象，未改变的不可变对象可以共享，不要求整世界深拷贝或一套平行 Saved/DTO 模型。
若领域世界采用可变对象，则事件需要隔离那些会被后续写入的可达部分；具体快照构造 API 由真实消费者试验选择，不先建设自动冻结/通用复制框架。
已有快照内部的共享和循环也应保留，不能因复制破坏领域含义。

```text
E1 帧：State.Alice = Alice_v0；Event.TargetSnapshot = Alice_v0，HP=10
S1 帧：State.Alice = Alice_v1，HP=7；Event.TargetSnapshot 仍为 Alice_v0，HP=10
```

E1→S1 连续会话保留同一个 Event 实例；冷重开后恢复其持久身份与引用关系，不承诺跨进程保留 CLR 实例。
新旧 Alice 的业务身份可以相同；同一 Revision 中值不同的两个快照是不同对象，不能强制共用一个 ObjectId。
删除原先要求 `ReferenceEquals(event.Target, world.Alice)` 始终成立的验收；只对本应共享的不可变子图检查实例一致。
事件快照目标不是当前可变实体的写入入口；处理器根据领域语义产生下一世界，不把对快照的修改当作更新当前世界的捷径。

这与 DramaBoard 当前替换式 reducer 方向相容：[FirstBoardReducer.UpdateActor](../../src/FirstBoard/FirstBoardDomain.cs) 创建新 Actor 后，事件保留旧 Actor 正是快照含义。
但 record 或 IReadOnlyList 本身不证明递归不可变，仍需核对集合/元素别名；DG 当前不支持 record class 声明，适配声明形状与保留不可变语义是两项不同工作。
领域主动引用大对象图会增加快照闭包；不把“连带一个事件”承诺为恒定小开销，也不把局部读取优化设为本片前置条件。

## 5. 发布与恢复的最小边界

一个 Journal branch ref 是唯一发布前沿；每次遵循：准备联合图 → Schema/StateRevision 完成规定持久化 → 追加 Journal Frame → 推进 ref → 安装本次内存基线。
ref 只指向内容已完整持久化的 Revision/根，正常推进校验前帧与 Revision Parent 的对应；不能靠文件中最大的地址选择恢复点。

| 中断位置 | 重开含义 |
|---|---|
| Revision 或 Frame 已追加，ref 尚未推进 | 分支仍在旧 head；新增物理记录不算该分支进展。 |
| E 的 ref 已发布，尚未安排/完成处理 | 加载其 PairRoot 得到旧状态和完整事件快照，处理工作仍待完成。 |
| S 的 ref 已发布，调用者尚未收到成功 | 加载其 PairRoot 得到完成状态；不能仅因上次报错再处理同一事件。 |

根存在/类型与角色匹配、初始空 Event 特例及 S→E→S 交替由上层使用模式校验，不要求 StateStore 理解业务事件语义。
Commit 错误先停止当前会话，重开判定实际 head；不承诺自动 retry、透明回滚或已实现的断电保证。故障验收范围由所选底层明确。
分叉/移动 ref 选择已有历史帧后，需从其 Revision 创建新的处理会话，不能让旧会话携原基线继续写入。

## 6. 已有基础、真实接缝与验证

| 源码依据 | 本方案如何使用／尚缺什么 |
|---|---|
| [WorldWorkspace](../../../durable-graph/src/DurableGraph.StateStore/WorldWorkspace.cs) | 固定单根、一次加载的实例表及线性基线适合 PairRoot；双根模式本身不要求新 Revision 类型。 |
| [CaptureSession](../../../durable-graph/src/DurableGraph/CaptureSession.cs)、[ObjectRevisionPlanner](../../../durable-graph/src/DurableGraph.StateStore/ObjectRevisionPlanner.cs) | 捕获 PairRoot 联合闭包，保留可达身份并计算 Removes；无需兄弟 Revision 合并、E 新 ID 导入或额外比较基线。 |
| [RevisionDecoder](../../../durable-graph/src/DurableGraph.StateStore/RevisionDecoder.cs) | 当前读取/验证全部 live 行，随后只实例化根可达图；本片接受联合加载，不新增局部读取承诺。 |
| [GraphRepository](../../../durable-graph/src/DurableGraph.StateStore/GraphRepository.cs) | 当前持有独立 publication.rbf 与单 head；需把发布/恢复入口接到 Journal 权威，不能直接叠加两次独立 Commit。 |
| [EventJournal](../../../atelia/src/EventJournal/EventJournal.cs) / [Refs](../../../atelia/src/EventJournal/EventJournal.Refs.cs) | 复用帧寻址、持久化和 branch/ref；结合引用的 Revision/根验证与会话恢复，不从 ref 已有推断整个消费者已接通。 |
| [SimulationKernel](../../src/Kernel/Simulation/SimulationKernel.cs) | 当前 facts/scratch/AppendBatch 同步边界需要适配 E/S 两阶段；Game+Spatial+Kernel 完整状态仍按[消费者闭包](durablegraph-consumer-preflight.md)验收。 |

上述为本轮源码阅读与独立交叉评审结果，尚未实现或运行测试。“根建模改动小”不代表发布改造、类型适配或写入量已有结论。

下一验证包建议只用 `PairRoot + World(Alice, Bob) + Event(TargetSnapshot)`：

1. 连续运行与 E-head/S-head 冷重开产生等价结果，Bob 不丢失；S 中 Event 快照保持处理前旧值，当前 Alice 为新值。
2. 事件含集合与元素；随后修改活动世界不改变事件快照。本应共享的不可变子图在同次加载中只实例化一次。
3. E→S 保留 Event，S→E 替换最近 Event；没有显式领域历史引用时，PairRoot 不自动保留所有历史封套/事件。
4. 从同一历史点分两支处理，确认各自 head、旧快照和工作实例隔离；按上表三个发布位置验证中断恢复。
5. 用真实生成模型保存/重开；统计模型改写量及实际 Base/Delta/Remove、文件写入，确认没有依赖旧业务 replay。

机制成立后接 FirstBoard 途中相遇场景。尚待具体化：领域快照构造与声明、Journal 接管发布的最小 API、真实包消费者及故障注入分片；不重开已选双根/Parent 方案。
此前兄弟 Revision 与可变对象历史引用候选已退出当前草稿，演变保留在 Git，不重复维护方案对照表。
