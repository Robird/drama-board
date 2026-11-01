# 语义空间连接与大喊

> 状态：已实施（2026-11-01）。设计经过两轮辩证简化审查；代码、自动测试与实际 Console 证据见[实现验收](../experiments/spatial-shout-acceptance.md)及[召集试玩](../experiments/spatial-shout-console.md)。游戏修订为 18，旧开发存档不兼容。
>
> 最小模型：Spatial 增加带 64-bit 能力标记的无向地点连接，查询本地点及匹配连接的一跳实体；ResourceCycle 在一次转换中向范围内其他活动角色交付大喊，保留每个角色的活动、信息边界和恢复合同。

## 1. 需求账本与证据边界

| 编号 | 要求与来源 | 当前消费者 / 约束 |
| --- | --- | --- |
| U1 | 用户：通过游戏原型积累可复用功能；事件分类、传播、筛选、订阅将是重要的长期子系统。 | 当前实际消费者为 ResourceCycle 的召集大喊；长期方向不等于本次必须建设完整框架。 |
| U2 | 用户：新增一种节点间连接，附带 64-bit Flags，先只定义声音传播一位，其他位保留。 | Flags 表示连接能力，不表示事件类型；语言、铃声、爆炸声可共用声音位。 |
| U3 | 用户：采用显式语义关联，不用坐标、距离推导；查询本节点及一跳的实体集合。 | 不递归传播，不根据移动通道自动生成关系。 |
| U4 | 用户：实体枚举、实体 Flags、Delegate 过滤均是候选，允许先不提供过滤；先正确再优化。 | 本版选择不提供 Spatial 实体过滤器，接收规则留在游戏层。 |
| U5 | 用户认可上一轮建议：无向连接、全部位匹配、零查询掩码拒绝、途中不纳入节点范围、一次确定接收者后统一提交。 | 已实施为首版规则。 |
| C1 | 设计前基线：[GraphDefinition](../../src/Spatial/Definitions/GraphDefinition.cs)、[SpatialQueries](../../src/Spatial/Queries/SpatialQueries.cs)。 | 当时只有地点、移动通道、同地/同路查询；本次加入独立关系与一跳查询，继续严格区分 AtPlace 与 Traversing。 |
| C2 | 当前代码：[CycleCommunicationRules](../../src/ResourceCycle/CycleCommunicationRules.cs)、[交流测试](../../tests/ResourceCycle.Tests/CommunicationRulesTests.cs)。 | say/give 仅对同地单一目标；每角色每模型时刻共用 4 次发送额度；非空话语最多 2000 Unicode scalar。 |
| C3 | 设计前基线：[CycleFactApplication](../../src/ResourceCycle/CycleFactApplication.cs)、[CycleObservedEvents](../../src/ResourceCycle/CycleObservedEvents.cs)。 | say/give 仍要求恰好两名角色变化；本次用独立 SpeechShouted 分支承载广播。 |
| C4 | 当前代码与测试：[交流恢复](../../tests/ResourceCycle.Tests/CommunicationRecoveryTests.cs)、[Context](../../src/ResourceCycle/CharacterContext.cs)。 | 串行模拟、单根业务状态；E 与 S 分别发布，pending E 恢复不重问控制器或重做 Plan；角色材料已冻结。 |
| C5 | 用户前序兼容决定与[当前状态](../../PROJECT-STATE.md)：无旧开发存档兼容要求。 | 允许必要 Schema / GameRevision 变更；不建设旧数据迁移与通用消息兼容层。 |
| C6 | 当前代码：[CycleExpedition.Validate](../../src/ResourceCycle/CycleExpedition.cs)。 | 本实例空间实体与活动角色一一对应；本次不放宽世界校验来加入非角色实体。Spatial 本身不受此限制。 |
| D1 | 本文的设计选择：大喊沿用署名交流、全文可听清、瞬时动作与现有发送额度。 | 首版是已知同伴之间的召集；不建立陌生声音识别、语言理解、响度或衰减模型。 |
| D2 | 本文的内容选择：为现有生存地图显式配置少量声音连接。 | 已采用 §3.2 的两条连接；调整时遵守游戏修订边界。 |

证据优先级：用户当前决定 > 当前源码、测试和真实消费者 > 具体失败轨迹 > 相邻设计文档 > 假设未来玩法。同一提案衍生的文档不算独立证据。C1/C3 保留设计前缺口的语境，当前实现以源码及验收为准。

## 2. 首版产品行为

新增 `shout <text>`，Human Console 与 LLM 使用同一 parser、校验与执行路径。

- 仅在启用 Spatial 的生存实例中，活动角色处于 `AtPlaceLocation` 且已有可操作决策窗口时可发声；不会额外给忙碌角色打开任意插话入口。
- 覆盖发声地点，以及通过声音连接直接相邻的地点；不经过邻居继续传播。
- 接收者为范围内除自己外的活动角色；途中、死亡或已离场角色不接收。Spatial 不区分实体种类，ResourceCycle 则继续维持其空间实体与活动角色一一对应的现有合同。
- 每名接收者获得一次带发声者署名的完整原文。声音不会自动共享地图、远处库存、对方活动、私有 Context 或 Goals；话语仍是角色陈述，不是世界验证的事实。
- 发送成功只扣一次额度，与 0、1、多个听众无关；无人接收也成功。和 say/give 共用当前时刻 4 次额度，沿用 2000 Unicode scalar 限制。
- 大喊不耗模型时间、不创建 Activity、不结束当前 Context、不自动确认发送者已有提醒；服务轮转遵循其他成功瞬时操作。
- 收到大喊置 `AttentionPending`，保留原活动、到期时刻、已有警告和跟随关系。暂停时间推进以请求响应不等于取消采集；是否回答、完成工作、返回或跟随由接收者决定。
- 发声者只获知“已大喊”与自己的原文，不获知实际听众名单、数量或接收确认；不会因无人听到改变回执。
- `say <actor-id> <text>` 保持同地指定目标的既有语义，不自动变成旁人可旁听；give 仍只对同地单人。一次大喊不是若干次 say。

首版署名表示已知角色的游戏身份，不推出位置与可交互性。例如在营地听见阿白的声音，不能因此执行远程 `follow b` 或 `give b ...`。陌生人身份与声源方向是后续感知需求，不在本次引入模糊识别系统。

## 3. Spatial 的语义连接

新增 [PlaceRelationDefinition](../../src/Spatial/Definitions/PlaceRelationDefinition.cs)，构造参数为两个 `PlaceId` 与 `ulong flags`，规范化为 `EndpointA`、`EndpointB`、`Flags`，作为 `GraphDefinition.Relations` 的独立集合。

### 3.1 定义合同

1. 无向，无长度、旅行耗时或进入权限；与 `PassageDefinition` 并列，二者互不自动推导。
2. 两端须为已定义且不同的 Place；按现有 PlaceId 排序规范化端点对；同一对地点只能定义一条，反向重复也拒绝。多种能力在同一 Flags 上组合。
3. 不新增 RelationId：静态内容中无平行关系身份或运行时引用需求，规范化端点对足以定位。
4. 存储类型为 `ulong`；首个命名能力 `Sound = 1UL << 0`。其余 63 位不枚举含义，不使用“已知位掩码”清除或拒绝它们；最高位必须完整保留。
5. 连接允许 `Flags == 0`，其含义为没有当前可用能力，不匹配任何合法查询掩码。查询的零掩码另行拒绝，避免意外选中所有边。
6. 图定义和关系集合不可变，构造时规范化；定义重排不改变查询结果。首版不增加动态门状态、关系 patch、事件、订阅注册或持久运行时索引。

`GraphDefinition.Create` 的第三个参数为可选的 `IEnumerable<PlaceRelationDefinition>? relations = null`，省略表示空关系集合；没有双实现兼容适配器。声音位定义为 `PlaceRelationCapabilities.Sound`。

### 3.2 地图内容

[CycleExpeditionMap](../../src/ResourceCycle/CycleExpeditionMap.cs) 单独声明声音关系，不从 Passages 或 DurationTicks 推导。当前内容：`camp ↔ stream`、`ridge ↔ cabin` 的 Flags 为 Sound；其他跨地点组合默认不传播。该表是关卡内容选择，不是根据物理距离作出的事实判断。

Spatial 测试使用专门的 A—B—C 图覆盖一跳边界，并验证无移动通道也可存在声音关系、有移动通道也可没有声音关系。生存内容测试只核对选定关系表；不为制造测试拓扑而增加关卡关系或地图注入框架。

已有地图长度仍服务移动；本次不重构其旅行参数，不引入坐标、米数、衰减、可见性几何或多跳搜索。

## 4. 一跳实体查询

绑定图定义的 `SpatialQueries` 实例新增查询：

```csharp
IReadOnlyList<EntityId> GetEntitiesInOneHop(
    GraphSpatialState state,
    PlaceId origin,
    ulong requiredFlags);
```

合同如下：

- `origin` 必须存在，`requiredFlags != 0`；无实体或无匹配连接都属于正常结果。
- 纳入本节点，以及所有与本节点直接相连且满足 `(relation.Flags & requiredFlags) == requiredFlags` 的节点。只匹配相邻边，不递归。
- 只返回上述节点内的 `AtPlaceLocation` 实体；不按角色类型、活跃状态、阵营或事件兴趣过滤。
- 包含起点内的全部实体；API 不接受 sender，不负责排除发声者。
- 结果实体唯一，按现有 EntityId 比较器排序。结果立即物化为只读集合，不依赖之后变化的世界，不暴露可写内部数组。
- 查询不写状态、推进时间、消费预测或产生通知；没有 `ModelTime` 参数，因为本版只检查地点归属，途中位置不参与。
- 返回客观世界信息，不能直接作为 Console / LLM 可见名单。

### 4.1 构造集合与过滤的选择

首版算法：扫描关系并筛选 Flags，构建覆盖地点集合；扫描实体一次，按 AtPlace 归属筛入结果。[GraphSpatialState](../../src/Spatial/State/GraphSpatialState.cs) 已规范化实体顺序，[状态校验](../../src/Spatial/State/GraphSpatialStateValidator.cs)要求 ID 唯一有序，直接保序收集即可，不再排序或构建第二个实体去重集合。该算法为 O(R + E)，包含已有状态校验的成本应另行计入；不为每个邻居重复扫描所有实体。

明确选择先返回完整的空间候选集合，再由应用筛选接收角色。这样只有一个物化查询 API，能够独立验证其集合语义；现有规模没有实测性能证据支持增加 Delegate、枚举策略或实体兴趣位。

以下都不在规范接口中：延迟 iterator、`predicate` 重载、公共可变 HashSet、角色种类枚举、实体订阅 Flags、节点实体索引。这些是有实际消费者或性能证据后可讨论的替代方案，不是本次的预留参数。

## 5. 游戏内接收与交付

### 5.1 接收规则

`CycleCommunicationRules` 内部使用纯函数 `GetShoutRecipients(business, senderId)`：调用空间查询，映射到 `CycleBusinessState.Characters`，保留其他活动角色，保持 ID 顺序。Check、Plan 与事实校验共用此函数，不各写一套范围策略。事实的 ChangedCharacters 按既有业务角色顺序记录（该顺序不要求按 ID 排序），校验核对完整集合及记录顺序。这个本地步骤足够，不新增 `IEventSubscriber`、事件总线或订阅持久状态。

“空间覆盖”“允许接收”“形成哪些个人材料”“是否请求决策”是不同职责，但首版不为每个职责建立独立接口或服务。所有活动角色都接收语言；需要耳聋、语言不通、选择性提醒等真实玩法时，再从此处分离规则。

不得对其中某人无法表示状态变化的情况静默跳过；现有计数/DecisionGeneration 容量前置校验应覆盖完整参与者，失败时整次不扣额、不部分送达，也不返回有身份的失败听众列表。

### 5.2 一次规划，一次业务转换

```text
同一已提交世界：校验发声者、正文和额度
  → 查询范围并确定接收者
  → 构造发送者与所有接收者的状态替换
  → 构造各角色的冻结经历与 Context 变化
  → 按现有 Kernel 流程发布一个 E，再发布一个 S
```

不在接收循环里执行控制器、产生嵌套喊话或提交子操作。只有发声者带本次 ToolCallId / OperationRecordId，接收者不产生伪造工具调用。一次行为一个额度、一个提交结果；参与者状态在同一业务根内变化。

采用业务事实种类 `SpeechShouted`；复用 `CycleFact.ActorId`、`SpeechText`，`TargetActorId` 为 null。`ChangedCharacters` 必须包含发送者及每名接收者，即使对方已 `AttentionPending` 也保留其接收变化；每个参与者只变化一次。

接收者身份从这条事实的 `ChangedCharacters` 去掉 ActorId 得到，不再添加一份持久 `RecipientIds` 名单。`ObservedEvents` 和 `InteractionChanges` 是个人材料与交付结果，必须与该参与者集合一致，不能成为第二个独立路由权威。

### 5.3 材料与信息边界

现有 `CycleObservedCommunication` 强制单接收者且 Observer 必须是双方之一。不能为大喊填入假目标、给发送者复制所有听众的单聊材料，或者把隐含收件人名单写到 Result 中。

不增加专用大喊材料类型或可空载荷字段。复用现有 `CycleObservedEvent`，以新增的 `SpeechShouted` Kind 固定字段含义：

| 现有字段 | 大喊中的唯一含义 |
| --- | --- |
| `ObserverId` | 这份个人材料的归属角色 |
| `SubjectId` / `SubjectName` | 发声者 ID / 发生时冻结的姓名 |
| `Detail` | 话语原文，不是机器原因标签 |
| `Communication` | 必须为 null；继续仅服务既有 say/give |

发送者 `ObserverId == SubjectId`，保存一次自己的发声经历；每名接收者保存一次听见经历。Kind 是字段语义的判别依据，不从原文猜测事件分类，也不建立泛型消息 Envelope 或继承树。

该复用必须同时完成三项约束：事实感知校验逐字段对照原事实的署名与正文；`CharacterExperience.ValidateSequence` 对 S 冷开的该 Kind 检查非空署名、合法正文与 `Communication == null`；`CycleUpdateRenderer.EventDescription` 在通用 Detail 翻译之前按 Kind 分支，将姓名、ID、原文全部通过 `DataLiteral` 引用，注明“角色话语，未经世界证实”。例如大喊 `ColdWind` 或 `Terminal` 必须显示原文，不能被翻译为天气或终局提示。

沿用既有 Context 交付：Open 时附加个人经历，Closed 时保留待交付经历并在下一 Opening 交付；不改写已经冻结的 Opening。渲染器只读自身材料，不能按当前世界重新查询当时听众或声音来源位置。

沿用 [CycleInteractionRules.LocalEvent](../../src/ResourceCycle/CycleInteractionRules.cs) 的逐观察者局部编号，不能将全局 EventOrdinal 的间隙带入个人材料。现有复制与相等比较已覆盖上述三个字段；不得为了减少代码跳过原文比较。

发送者与接收者都不获得其他听众信息；接收者可以知道首版规则允许披露的发声者署名与原文。不得新增 CycleVisit、远处资源观察或可交互目标。文本引用安全与“角色陈述不等于世界证实”沿用现有交流呈现规则。

## 6. 事实校验、预测与持久化

### 6.1 校验

现有[事实校验](../../src/ResourceCycle/CycleFactApplication.cs)中“通信恰好修改两人”的合同继续用于 say/give，为大喊新增明确分支，不能统一放宽成任意多人。

大喊分支检查原始命令匹配、正文、预算、参与者去重、发送者扣额一次、接收者 AttentionPending、每人 DecisionGeneration 变化，以及 Activity、跟随、背包、警告、结局等无关状态不变。空听众时仍须有发送者状态与自己的经历。

校验必须在同修订的 before 状态上调用同一个纯接收者选择函数，检查合法接收者集合与 `ChangedCharacters` 去掉发送者后的集合完全相等，拒绝漏送和越界；不能重新构造替换、修正听众、重做 Plan 或调用控制器。最终应用的是 E 已记录的变化；非法记录拒绝而不是重新路由。

现有[感知校验](../../src/ResourceCycle/CycleObservedEventValidation.cs)、[Context](../../src/ResourceCycle/CharacterContext.cs)与交付校验须覆盖新 Kind 的材料约束，保证每名参与者恰有一次相关经历且只有自身效果，不能只验证字段非空。

审查发现 [CycleInteractionValidation.ValidateDelivery](../../src/ResourceCycle/CycleInteractionValidation.cs) 原来遗漏 `Communication` 比较，本次已补齐 `delivered.Communication == source.Communication`，使用 record 的值相等；[交付完整性测试](../../tests/ResourceCycle.Tests/CommunicationDeliveryIntegrityTests.cs)覆盖 say/give 在 Open/Closed 两种路径的合法载荷替换拒绝。

E → 个人 Context 的比较须保留完整署名与原文，只允许 EventOrdinal 按个人重编号。S-only 冷开验证材料自足形状及内部一致性，不声称能在缺少历史 before/E 时重新证明历史署名真实性。

### 6.2 预测与唤醒

把新事实接入 [CyclePredictionMaintenance](../../src/ResourceCycle/CyclePredictionMaintenance.cs) 的决策相关失效维护，让新增 AttentionPending 能被服务；以增量结果与 Rebuild 对照验证。保留已有“同刻自然完成先于输入”的规则。

当某角色到达时刻与喊话输入同刻，先完成现有到达处理，再按喊话时的实际 AtPlace 状态选取听众；不能仅因 `ArrivalDue == now` 就在到达事实应用前将 Traversing 角色当成已在端点。

这不是声音波前预测，不新增定时传播、延迟投递、持续范围成员缓存或控制器后台监听。接收后实际何时被服务仍由现有串行调度决定。

### 6.3 恢复边界

- 正常步骤继续使用 Plan → Fold → Complete → Validate → E/S 发布；E/S 是两个磁盘边界，不称为一次跨记录磁盘事务。
- 在 E 已发布、S 尚未发布时中断，恢复应用已记录的所有参与者变化和个人材料，不重新询问模型，也不再次计费扣额。
- 声音关系是不可变地图内容，不新增运行时持久根。ResourceCycle 的 ScenarioId / GameRevision 绑定该内容；语义变更时提升 GameRevision，不能在相同修订下随意改表后继续读旧档。
- 按实际 DurableType 变化更新 Schema 版本与历史，包含新增枚举值影响的容器类型；不提前规定未经生成器核对的版本清单。
- 旧开发档不迁移；纯图消费者空关系集合的源码接线不构成旧存档支持承诺。

## 7. 实施范围与验收

### 7.1 已实施边界

1. Spatial：关系定义、GraphDefinition 组装校验、一个一跳查询及单元测试。不要改动移动、导航、交会算法。
2. ResourceCycle：显式地图关系、shout 命令、多人事实及严格校验、冻结材料、Context 交付与预测维护；同步 Schema / GameRevision。
3. Console / Completion：共享帮助、发声回执、收到大喊的材料与提示；不生成听众名单；完成磁盘恢复、三角色实际入口试玩。

这是一个功能的依赖顺序，不是先建设通用事件平台再接入游戏。Microtown 不强制迁移；只处理 GraphDefinition 签名造成的必要空集合接线。

关键接线点：命令涉及 `CycleActions` / `CycleCommandParser` / `CyclePlanning`；事实涉及 `CycleCommunicationRules` / `CycleFactApplication`；材料涉及 `CycleObservedEvents` / `CycleObservedEventValidation` / `CharacterExperience.ValidateSequence` / `CycleInteractionValidation`。呈现同时检查 `CycleOperationResults`、`CommunicationView`、`CycleUpdateRenderer`、`ContextHistoryPlayView` 的收到通信原因和 Completion 提示，避免只改帮助而遗漏真实交付。`LocalEvent` 与 `CharacterContextEquality` 已覆盖复用字段，验证保真即可，无需新增字段分支。

### 7.2 验收矩阵

| 层次 | 必须覆盖的行为 |
| --- | --- |
| 定义 | 坏端点、自连接、正反向重复拒绝；未知位与 bit 63 保留；零 Flags 不传播；定义重排等价。 |
| 查询 | 本地含自己；无关系仍返回本地；双向一跳，A—B—C 不到 C；全部位匹配；零查询掩码拒绝；无关移动边不参与；途中排除；包含不具有游戏角色身份的普通空间实体；集合唯一、顺序确定、无状态变化。 |
| 接收 | 0/1/2 听众；本地与邻居并存；自己、终止角色排除；不把移动途中投影成端点；无人听见仍成功。不要为测试非角色过滤而放宽 CycleExpedition 的实体对应校验。 |
| 操作 | Human/LLM 共用 parser；正文空格和 Unicode 边界；无目标参数；say/give 不改语义；3 种交流共用额度，拒绝不扣额、多人只扣一次。 |
| 活动与跟随 | 听到喊话保留采集和 Due、跟随关系、其他提醒；忙碌角色获得响应后自主决定；发送不确认旧提醒；无新增自动集合/移动；同刻先到达再喊话的领域集成测试。 |
| 材料 | 发送者回执与听众人数无关；每人一次经历，已 AttentionPending 仍保留新正文；局部编号无听众间隙；无其他听众泄漏、无新增地图知识；Open/Closed/下一 Opening 正确交付；`ColdWind`、`Terminal`、引号、换行、Markdown 均作为原文安全显示，冷开后相同。 |
| 畸形事实 | 多发、漏发、重复对象、双扣额度、私自取消活动、改背包或跟随、伪造听众材料均被拒绝；事实原文 X / 交付正文 Y 拒绝；say/give 的 Communication 单独被替换也拒绝；不能把 say/give 偷换成多人。 |
| 恢复 | E-only 中断和正常 S 冷开；禁止 Plan/控制器，所有听众与正文恢复一致、额度只扣一次；增量预测等于 Rebuild；再次恢复无重复交付。 |
| 实际入口 | 三角色分处本地/邻地，大喊召集，听众先完成活动再返回跟随；另有途中角色未收到的对照；二跳边界由 Spatial 夹具验收，无需为此改生存地图；Console 两种布局与 LLM 看到相同个人材料。 |

上述矩阵的实际测试、Schema Verify 和入口证据集中于[实现验收](../experiments/spatial-shout-acceptance.md)。本次使用离线 Completion fixture 验证真实宿主接线，没有在线模型调用；脚本操作 Human 入口不等于真实人类体验验收。

## 8. 长期事件子系统的边界与重开条件

明确保留的方向是事件分类、传播、筛选与订阅，但不要把本版的声音位当成该系统的事件分类枚举。

| 职责 | 当前最小实现 | 重开条件 |
| --- | --- | --- |
| 连接能力 | `ulong Flags`，仅命名 Sound | 新玩法需要另一种跨地点关系，才赋予新位含义。 |
| 事件分类 | 游戏已有 Fact / ObservedEvent 种类加大喊 | 至少出现真实的跨规则匹配需求，例如语言与铃声共享声音传播，却需要不同兴趣和提示策略。 |
| 实体过滤 | Spatial 不筛，游戏挑活动角色 | 出现非角色接收者或实测成本，再比较枚举、实体 Flags、Delegate 与索引，不预选框架。 |
| 订阅 / 兴趣 | 活动角色固定接收语言，无注册表 | 实体可自主改变关注种类，或跨事件统一的持久关注需求出现。此时明确兴趣状态权威与冷开重建。 |
| 感知 | 本版完整署名话语，个人冻结材料 | 陌生声音、隐蔽身份、不同语言、模糊声源或认知差异实际进入玩法。 |
| 动态传播 | 静态无向一跳 | 门窗、屏障、单向广播、多跳传播成为实际内容需求。 |
| 持续效果 | 本版不提供 | 光环、火场等玩法需要进入/离开/到期结算；不由一次喊话的听众表冒充持续成员关系。 |

本设计不新增消息队列、订阅数据库、并发分发、监听器生命周期、统一事件信封或新的持久事件身份。将来事件的语义分类、空间传播能力、实体接收兴趣仍应分别表达，不共用一个万能 Flags。

## 9. 辩证简化审查

2026-11-01 完成三名独立评审（需求怀疑者、最小架构师、语义守卫）的独立论证与交叉质询，主线程独立核对代码后修订。第二轮已对最小机制与必要不变量收敛，没有为制造一致意见追加第三轮。以下是证据裁决，不是运行验收结果。

| 裁决 | 对象 | 证据与最终选择 |
| --- | --- | --- |
| simplify | 专用 `CycleObservedShout` | 既有 SubjectId/SubjectName/Detail 全链可承载三值；删除初稿拟增的 1 个持久类型与 1 个可空字段。保留按 Kind 严格校验和原文渲染，避免 `ColdWind` 误译。 |
| simplify | 结果二次排序 | GraphSpatialState 已保证实体唯一有序；直接保序收集，不削弱确定顺序合同。 |
| merge | 接收选择规则 | Check、Plan、Validate 共用一个纯函数；多份算法会使实时与恢复的合法范围漂移。 |
| keep | 完整范围及交付校验 | A 范围内有 B/C，记录只含 A/B 时，仅校验已列对象会漏检 C；必须核对完整合法集合。补齐已有 Communication 的交付比较。 |
| keep | 完整参与者、每句原文、单事实 | B 已 pending 后再次收到更正仍须记入；拆成多次 say 则会多扣额度，并可能在故障时只送达部分角色。 |
| defer | 通用过滤、订阅与事件平台 | 用户允许先不筛；现有活动角色查询足够。按 §8 的真实消费者条件重开，不放空接口。 |
| delete | 重复听众名单、新事件身份 | 原事实参与者、操作身份、既有个人序号已足够；额外身份与 RecipientIds 无当前职责。 |
| simplify | 地图与验收耦合 | 初稿建议的两条声音边均已有移动通道且不形成二跳链；能力独立性与二跳用 Spatial 专门夹具证明，不扭曲可玩地图来满足测试。 |

交叉质询中，语义守卫确认专用材料类型并非必要，但原文、署名、局部编号与冷开形状校验不可删；其余评审接受这一约束。没有遗留架构争议阻止按 §7 实施。署名全文与初始地图表仍明确是本文的首版设计/内容选择，不冒充引擎客观定律；后续可根据试玩调整并遵守游戏修订边界。
