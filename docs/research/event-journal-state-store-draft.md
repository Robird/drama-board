# EventJournal + StateStore 两层持久化草稿

> 状态：共同讨论中的可编辑草稿，不是已批准设计或实现说明。随结论替换正文，不追加逐轮纪要。
> 用户于 2026-09-11 提出两层方案并要求以本文作为讨论载体。入口：[项目状态](../../PROJECT-STATE.md)。

## 1. 问题与需求来源

| 来源 | 当前要求或边界 |
|---|---|
| 用户当前设想 | 事件历史为应用主轴；每次处理后的对象图直接保存；正常恢复不执行历史业务 reducing。 |
| 用户当前设想 | 下层 StateStore，上层 EventJournal；核心帧仅 DomainEvent/DomainState，两者都引用 StateRevision。 |
| 用户当前设想 | 输入输出都是强类型领域对象；事件直接引用旧世界对象；框架承担 Schema、序列化、引用身份及增量编码。 |
| 用户当前设想 | 复用 EventJournal 的 branch/ref；领域 EventInstance 可同时被结果状态引用。 |
| 用户已说明的延期 | 异常/retry 是否记录到同一事件历史可以后议；本轮讨论，不实施产品。 |
| 当前 DramaBoard 事实 | Kernel scratch-fold、验证、AppendBatch 后安装世界；新设计可以改此接缝，但 Game+Spatial+Kernel 边界须完整恢复。 |
| 评审范围建议 | 先按一个活动分支会话、串行处理分析；不预设多 writer、通用并发 merge、GC 或历史兼容框架。 |

本轮不是要求在当前 GraphRepository 外面保留全部旧限制；源码限制用于评估改造成本，不作为新产品需求。

## 2. 用户候选的主体

下层继续使用 StateStore，外观可按需调整。上层 EventJournal 的两类帧结构高度相似，少量 Kind/Meta 区分语义，主体都指向 StateStore 中的一次对象图保存。

- **DomainEvent**：保存以强类型 EventInstance 为根的图。产生事件的基态是上一个 DomainState，事件可以直接引用其中任意受支持对象。
  调用方提交事件根，由框架处理增量保存；可以单独加载事件根，只实例化其可达对象。
- **DomainState**：保存处理后的领域世界，包含产生它的 EventInstance。用户希望连续处理时能保持同一个事件实例以及共享引用。
  用户描述其增量基态仍为上一个 DomainState。
- **Journal branch/ref**：提供历史选择和分支。分支 head 为 DomainEvent 表示该事件尚待处理；head 为 DomainState 表示可产生下一事件。

用户给出的调用流程，按语义整理如下，尚非 API 设计：

```text
LoadRepoAndBranch
取得最新 DomainState 与 branch head
if head.Kind == DomainEvent:
    用 EventInstance 更新领域状态
    CommitDomainState(domainState, eventInstance)
else if head.Kind == DomainState:
    由当前领域状态产生 EventInstance
    CommitDomainEvent(eventInstance)
    安排事件处理管线
```

根状态初始化可以表现为第一条 DomainState；其具体 API 与元数据尚未讨论。

## 3. 临时术语与两个 Parent

| 本文简称 | 所处层与含义 |
|---|---|
| `event` / EventInstance | 内存中的领域对象，例如某种 EventClass；不是 Journal Frame。 |
| `R_E` / `R_S` | StateStore 中事件图/世界图的 StateRevision；是否共用现有类型仍可按证据裁决。 |
| `J_E` / `J_S` | EventJournal 中 DomainEvent/DomainState 帧，指向相应 Revision 与选定根。 |

候选帧的最小逻辑内容为 `Kind + RevisionAddress + RootObjectId + 必要 Meta`。根地址需能支持恢复，字段位置尚未冻结。

为了检查现有 branch/ref，主线程当前将 **Journal Parent** 理解为连续历史：

```text
J_S0 → J_E1 → J_S1 → J_E2 → J_S2
```

用户关于“相对于上一个 DomainState”的描述，则暂解释为 **StateRevision 增量 Parent**：

```text
R_S0 ─┬→ R_E1
      └→ R_S1 ─┬→ R_E2
               └→ R_S2
```

这是待确认的解释，不应把两个 Parent 自动合并。评审需要比较兄弟 Revision 与线性 Revision 的实际代价，不预设必须修改用户提案。

## 4. 当前证据入口

- [WorldWorkspace](../../../durable-graph/src/DurableGraph.StateStore/WorldWorkspace.cs)：固定 World 根；每次 Load 创建自己的实例表，先处理完整 Revision 再实例化可达图；保存与安装维护身份和比较基线。
- [LoadedRevisionPlanner](../../../durable-graph/src/DurableGraph.StateStore/LoadedRevisionPlanner.cs) / [ObjectRevisionPlanner](../../../durable-graph/src/DurableGraph.StateStore/ObjectRevisionPlanner.cs)：exact Parent 校验；完整 post-live 集合决定 Removes。
- [RevisionDecoder](../../../durable-graph/src/DurableGraph.StateStore/RevisionDecoder.cs)：当前遍历全部 live 行读取/验证，不等于按根仅读取可达字节。
- [CaptureSession](../../../durable-graph/src/DurableGraph/CaptureSession.cs)：实例与 ObjectId、分配游标、候选接受。
- [GraphRepository](../../../durable-graph/src/DurableGraph.StateStore/GraphRepository.cs)：当前单 head publication 入口；不是新两层方案已经可直接复用的高层 façade。
- [EventJournal](../../../atelia/src/EventJournal/EventJournal.cs) / [Refs](../../../atelia/src/EventJournal/EventJournal.Refs.cs)：帧追加、持久化、ref 推进、分支和祖先链。
- [SimulationKernel](../../src/Kernel/Simulation/SimulationKernel.cs)：当前领域提交边界；不是新方案的不可变限制。

以上是静态阅读入口，本轮没有运行实现验证。

## 5. 已核实的约束与最小机制

以下来自三个独立评审角色及一次交叉质询，主线程核对源码；是设计评审结论，不是运行验收。

| 项目 | 裁决与证据 |
|---|---|
| 两类帧和一个 Journal ref | **保留**。ref 指向 E 表示待处理，指向 S 表示已完成；最近 S 可从链导出，不另建已处理 head。 |
| 第三种 manifest、独立事件正文 codec | **合并/延期**。E/S 帧已承担 Revision/根选择；领域 event 使用 DG 生成能力，框架只需统一引用封套。Schema 登记仍是下层能力，两层不等于删除 SchemaStore。 |
| 发布顺序 | **保留**。Schema/Revision 完成规定持久化 → Journal Frame 追加 → ref 发布 → 安装内存比较基线。未被 ref 选中的追加记录不算分支进展。GraphRepository 的独立 publication head 需重构，不能叠成第二权威。 |
| 两种 Parent 的职责 | **区分，拓扑待决**。Refs.AdvanceRef 要求新帧 Parent 等于旧 head；StateRevision Parent 则选择增量比较/恢复基态，两者不必相同。 |
| 单独捕获 E 的 membership | **允许**。ObjectRevisionPlanner 会从 R_E 中 Remove 非事件可达的旧对象，但不修改 R_S0；兄弟方案仍可从 R_S0 保存完整新世界。不能把局部 Remove 误称为丢失旧存档。 |
| S/E 共用身份 | **保留框架内机制**。WorldWorkspace.Load 每次创建新实例表；分开 Load 会产生两份 Alice。需一个受控处理视图，消费者无需自行合并对象。 |
| 按根只实例化 | **已有基础，读取优化另议**。WorldWorkspace 只 Allocate 可达对象；RevisionDecoder 当前仍读取/验证全部 live 行。不能声称当前读取量只与事件闭包相关。 |

“同一实例”的建议边界是**一次处理视图**。独立查看不同历史版本必须隔离；不能建立全库 ObjectId→单一 CLR 对象的缓存。
兄弟方案还需保留 R_S0 的绑定并导入 R_E1 的新对象 ID/分配游标：CaptureSession.Accept 当前会用候选可达绑定替换旧表，event-only Accept 不能直接成为世界的下一身份表。
例如旧 S 最大 ID 为 10，E 已分配 11；冷恢复不能再从旧 S 的 11 开始给另一个新对象编号。

事件普通引用按所属快照解析的建议语义：R_E1 中 `event.Target.HP=10`，处理后 R_S1 中同一事件身份的 `Target.HP=7`。
这不修改 R_E1；同一事件身份不保证其引用闭包在所有版本中值相同。需要固定的观察值可作为事件字段，但不强制改成手工 ID。
事件回指 World 会扩大可达闭包，这是普通引用的真实含义；不因假设中的大闭包提前增加新引用框架。

DramaBoard 当前 `FirstBoardReducer.UpdateActor` 使用 `with` 替换 Actor。若事件仍引用旧 Alice，新世界装入新 Alice 后引用不会自动重定向。
因此领域模型还需选择稳定实体原地更新，或明确事件保留旧对象版本；共同加载只解决恢复身份，不能替代这个运行时选择。

## 6. 评审裁决与最小验证

**当前结论**：两层主体有可行路径；保持应用层小循环的主要工作落在框架的处理视图、根选择、身份与发布接缝。不能称为现有 GraphRepository 的零成本包装。

**待决一：StateRevision Parent 与成员集。**

| 候选 | 最小机制 | 代价 |
|---|---|---|
| A，保留用户原描述：R_E1、R_S1 同父 R_S0 | R_E1 只存 event 闭包；框架保留 S0 基线/身份，冷恢复时将同源 S0+E1 联合实例化，S1 继续比较 S0。 | 两来源需验证共享对象兼容并导入 E 新 ID；E 在 S1 相对 S0 是新对象，当前规划器需再写 Base。 |
| B，评审提出的线性备选：R_S0→R_E1→R_S1 | R_E1 保留完整世界与 event 的成员集，帧主要根仍是 event；恢复从同一 Revision 联合选择 World/Event 根，共用一份表与比较基线。 | 需支持保留世界根/联合捕获；E 的 Revision 目录更大，当前全 live 解码成本存在；改变了用户提出的 S 增量 Parent。 |

B 在框架身份管理上更简单，A 更贴合原描述且 E 的成员集更小。此处尚不替用户选；保留成员集不等于每次重写全部对象。

**待决二：生成事件是否可以修改旧世界？**

建议先限定生成 E 只读旧世界持久字段，可创建任意受支持的新事件对象图；处理阶段再更新世界。这是建议，非用户已定法则。
如果允许生成时修改 Alice 和 Bob，而 E 只引用 Alice，简单“继承 S0 目录再覆盖 E 闭包”会得到 Alice 新/Bob 旧的混合世界。
A 此时不能仅以 ID 去重合并；B 也需捕获同一时刻完整 World+Event，并明确以这个中间世界继续处理，而非静默改变基态。

**待决三：同实例引用与现有不可变领域模型。** 先选上节的引用版本语义，再决定 DramaBoard 保留哪些替换式 reducer、哪些对象需要稳定身份。

最小可执行验证建议：`World(Alice, Bob) + Event(Target = Alice)`，事件另带一个新建对象。
验证连续处理与 E 后冷重开等价，`ReferenceEquals(event.Target, world.Alice)` 成立，Bob 不丢失、新 ID 不冲突；原事件快照与处理后快照各自保留正确值。
从同一历史点建两个 Journal 分支并分别处理，验证历史选择与实例隔离；在 Revision 追加后、Frame 追加后、ref 发布后中断，验证恢复只按发布的 head。
这组机制成立后再接真实相遇场景；异常/retry 账本、完整 LLM 执行恢复、分页优化与 GC 继续延期。

历史背景与完整 DramaBoard 状态闭包见[消费者前置研究](durablegraph-consumer-preflight.md)。旧候选的固定会话根、Artifact 独立格式和额外 manifest 均不约束本文。
