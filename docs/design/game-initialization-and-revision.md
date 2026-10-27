# 设计决策：对象图初始化与游戏状态修订

状态：首片已接入 ResourceCycle 的真实创建、保存与恢复路径。普通工厂构造新游戏，State/Event 各自记录 `GameRevision`；加载还原并协调升级内存表示，再交给正常执行流程。生产目前只接受修订 1；跨版本语义通过测试夹具验证，不构成对旧存档的兼容承诺。升级本身不完成 pending 工作。实施边界与验证见[验收记录](../experiments/game-revision-acceptance.md)。

三方交叉审阅收敛的约束与源码依据见[审阅裁决](../research/game-revision-review.md)。

## 1. 新游戏由工厂构造

新游戏工厂接收场景、角色设置及必要时的生成种子，组装初始领域对象图；校验业务约束并重建派生预测后，由持久化入口发布初始 S。工厂直接构造当前游戏修订的状态，不从最早版本逐步升级。

[CycleGameFactory.CreateBatch / CreatePlayable](../../src/ResourceCycle/CycleGameFactory.cs) 集中组装场景并返回 `(World, Cursor)`，供内存运行及磁盘新游戏使用；[CycleWorld.Create / CreatePlayable](../../src/ResourceCycle/CycleModels.cs) 保留为薄委托。工厂不发布存档，不建立统一生成器接口。

- 地点、资源、角色等生成器可以是普通函数，各自构造子图，由上层连接引用、校验身份唯一性和引用完整性。
- 每次新游戏的可变状态独立；外部传入子图的共享/转移约定应明确，不能默认工厂会深复制。配置文件可以成为可选输入，不是必经表示。
- 随机生成使用显式种子及受控随机源；需要复现时记录生成器版本和参数。仅有相同种子不保证跨生成器版本得到相同世界。
- 生成结果作为对象图落盘，继续游戏不重新生成已有部分。以后扩建世界也不能覆盖玩家造成的变化。

## 2. 三个不同的版本与职责

| 概念 | 职责 | 当前状态 |
| --- | --- | --- |
| DurableGraph Schema 版本 | 将旧持久表示还原为可供应用处理的类型；State 与 Event 各自都可能需要 Schema Upgrade。 | 已有 DG 能力；`UpgradeContext` 不提供跨图访问和对象 ID 分配。 |
| `GameRevision` | 表示该份 State 或 Event 的游戏语义修订版本，决定其内存升级路径。 | `CycleDurableRoot` / `CycleDurableEvent` 各自保存；`CycleGameRevision.Current` 为生产当前修订。 |
| `WorldVersion` | 标识模拟谱系与已完成的发生项转换次数。 | 现有 [WorldVersion](../../src/Kernel/Simulation/WorldVersion.cs)，不能兼作游戏修订号。 |

Schema 升级解决“能否还原”，游戏修订解决“还原出的世界应如何变化”，Rebuild 只负责派生预测与索引。业务修订必须显式处理正在进行的活动：例如周期缩短时保留原到期时刻、完成比例还是重置进度，不能靠重建预测暗中选择。

首片采用统一的游戏修订编号和明确的相邻修订步骤。S/E 的字段分别描述自身，不能从配对 S 推断 E 的版本，也不另建一套独立的 Event 修订编号。没有 Schema 改动也可以增加游戏修订号；普通程序发布也不必每次增加它。独立可选内容包确实出现时，再评估分模块版本或已应用修订 ID 集合。

“Schema 无关的进度”指其语义独立于 Schema 版本；字段本身仍正常持久化。新增该字段时，支持范围内的历史根需要明确映射到旧游戏修订号，不能自动标为最新版。未知、更高或缺少升级路径的版本应明确拒绝。

## 3. 升级表示，执行仍由正常流程负责

**加载时分别识别 S/E 的版本，协调升级其内存表示，验证一致后交给正常执行流程。升级完成不等于事件执行完成。** 首片升级管道保持 pending 的有无不变，不推进 Kernel 游标、不应用 E 的业务效果、不发布存档，也不调用控制器、Plan、LLM 或外部业务副作用。

事件可以透传或转换；透传也要明确确认语义兼容，并把内存中的版本标为目标修订。无法支持时拒绝。新规则移除某种动作时，应定义可由正常执行流程处理的转换结果，不能静默删除 E 或重新抽取已记录的随机结果。

S/E 各自带版本不意味着总能独立转换。例如旧 E 保存了“背包装入旧 ID 物品”的整个替换背包，仅升级 S 会被之后 Fold 旧 E 覆盖。首片采用单向依赖：**State 转换及业务 ID 映射只由源 S 和固定修订步骤决定，不依赖 pending E 的有无或内容；Event 转换可以读取对应步骤的原/新 S 与映射，但不能反向改变 S。** 保持输入快照不变，目标版本相同不能替代这些约束。

跨图匹配使用角色、资源、物品等已有领域 ID。DG Event 独立保存完整对象图，使用图内局部身份；不能用 CLR 引用相等或 DG ObjectId 作为跨 S/E 映射键。这里不引入新的全局身份系统。

当前 [OccurrenceEvent](../../src/Kernel/Journal/OccurrenceEvent.cs) 记录的是 Plan 完成后的确定事实，现有 RecoverPending 不调用控制器；使角色进入待决策状态也不等于当场询问控制器。未来若扩展为可能继续交互的过程记录，需另行定义执行/恢复协议；加载升级与执行分离的决策不自动改变当前 Kernel 的合同。

不能用当前新命令的合法性重新裁决已发布事实。例如采集门槛提高，不自动撤销旧 E 已决定的物品归属；如确需改变已记录数值，应由具体修订明确转换政策。

升级保持原 E 的 CauseKey、TargetInstant、配对边界及 S 的游标；原始磁盘记录不变。改变历史身份或时间轴是另外的历史重写任务。

## 4. 首片应用接口

应用层持久根 [CycleDurableRoot](../../src/ResourceCycle/Persistence/CycleDurableRoot.cs) 保存 `GameRevision`，独立事件包装 [CycleDurableEvent](../../src/ResourceCycle/Persistence/CycleDurableEvent.cs) 保存自己的修订号及 `OccurrenceEvent<CycleFact>`。Root Schema 为 v2，Event Schema 为 v1；Kernel 不承担应用修订号。旧无版本根和原始 occurrence 存档不在支持范围内。

```csharp
// CycleGameRevision 中的 internal 普通函数；生产目标使用 Current。
/// <summary>Upgrades restored representations without executing pending work.</summary>
/// <remarks>Preserve input snapshots, cursor, event identity and pending presence.
/// State conversion is independent of pending Event presence or content.
/// No publication, planning or controller calls; reject unsupported boundaries.</remarks>
internal static (CycleDurableRoot State, CycleDurableEvent? PendingEvent) UpgradeLoadedBoundary(
    CycleDurableRoot state, CycleDurableEvent? pending, int targetRevision,
    Func<CycleDurableRoot, CycleDurableRoot>? upgradeState = null,
    Func<CycleDurableRoot, CycleDurableRoot, CycleDurableEvent, CycleDurableEvent>? upgradeEvent = null);
```

普通函数按下节顺序调用相邻转换；内部 `Open` 重载让测试夹具为同一加载和发布路径提供目标修订及转换函数，公开 `Open` 固定当前修订且不提供虚构历史步骤。后续 E/S 写入会话的目标修订。不建立同构的 Input/Result 类型、可替换管道接口、通用版本图或持久映射表。第二个真实消费者或实现替换需求出现后再提取接口。元数据与边界检查由编排负责，具体转换的正确性由修订代码及对应测试保证，不增加一套通用业务验证器。

只承诺支持明确范围内的历史版本，不要求永久支持所有版本。DG 必须先能还原旧表示：保留必要的旧 Schema/DTO/绑定和转换数据，不能删掉解码途径后依赖应用修订补救。历史无版本字段的 S/E 需明确赋予旧版本，不能自动当作当前版。

在声明支持的范围内，已发布修订步骤的语义与业务 ID 映射必须稳定；允许等价代码重构，有语义变化则增加修订。修复旧步骤也需考虑已经由其结果产生的 E，不能直接改旧映射仍宣称兼容；必要时明确拒绝受影响的历史边界。

## 5. 加载与后续发布编排

1. 打开工作副本，读取最近 S、head 和可选 pending E，完成 DG 还原；检查历史形状及原 E 与游标的边界关系，保留物理来源提交身份。
2. 分别读取 S/E 的 `GameRevision`。有 E 时先将 S 升到 E 的版本，然后逐相邻修订一起推进：每步先转换 S，再用该步原/新 S 与映射转换 E。没有 E 则仅执行同一套 State 步骤。此时不提前套用最终版业务约束去否决合法旧数据。
3. 检查版本、形状、pending 有无、事件身份/游标及具体修订必要的映射与引用条件，校验当前 S 并重建其预测；不提前 Fold 来证明 E 可执行，也不复制 Fold/Complete 的业务验证。将就绪 State 与仍 pending 的 E 交给正常运行流程；升级入口到此结束，不写盘。事件执行结果仍由正常 Fold → Complete → Validate 检查。
4. 当前 ResourceCycle 继续通过显式 `RecoverPending` 完成 E；没有 E 则正常 Step。后续产生的 E 和 S 各自写入当前 `GameRevision`。只有正常执行完成后才推进游标、发布对应 S。

首片支持正常协议可产生、且有明确升级路径的 `S_revision ≤ E_revision ≤ 当前修订`；S 比 E 更新的输入没有当前消费者，明确拒绝，待真实需求出现后再定义。对齐可以采用等价实现，不要求保留所有中间快照或建立阶段框架。

加载不发布替代 E，也不为升级提前发布 S。退出发生在正常发布之前时，下次从原始磁盘边界重新升级；State 的业务内容与 ID/映射必须稳定重算，不能依赖墙钟、外部调用或无种子的随机数。不要求 CLR 引用或 DG ObjectId 跨次相同，也无需在同一个已修改对象上重复应用步骤仍幂等。

**S/E 版本不同是必须覆盖的正常恢复情况。** 例如打开旧 S 后只升级内存，随后正常 Plan 发布新版 E，但尚未发布 S 就中断，磁盘是 `S_v1 → E_v2`。重开应把 S 升到 v2，并保留已为 v2 的 E，不能重复转换 E，也不能只因版本不同就拒绝。版本相同同样不能替代配对关系与语义一致性检查。

关键反例是 `U(S1, 无E) = X` 产生 E2 后崩溃，重开若 `U(S1, E2) = Y`，即使两次各自确定，E2 也已失去原规划前态。因此 State 升级必须独立于 pending。若此时程序已到 v3，应先重现 S2，再用 2→3 的对应 State 上下文转换 E2，不能让事件升级误读已经完成全部转换的 S3。

adapter 需要区分物理保存基线与升级后的运行视图：`State`/`PendingEvent` 向 Kernel 暴露升级结果，正常完成后相对原 head 提交新 S。旧版 E 无需再次 CommitEvent。发布失败或结果不确定时仍停用会话并重开检查真实 head，禁止基于旧内存透明重试。

当前范围不做事件持久化替代。以后若需要，须另行定义同一逻辑事件的新表示、配对 S 与中断恢复，不能靠追加 E 破坏当前应用的 E/S 形状。历史任意重放也不能直接用当前 Fold 解释所有旧 E；需经过对应表示升级及语义检查，或从合适的新版 S 起步。

## 6. 实施落点与验收合同

- [CycleDurableHistory.Open](../../src/ResourceCycle/Persistence/CycleDurableHistory.cs) 先还原 S/E 并检查历史边界，调用升级函数后才完整 Validate/Rebuild；加载不提前 Fold 事件。
- `CycleDurableRoot` 普通构造仍完整 Validate；内部候选入口仅检查修订号、非空业务根、游标和世界时间对齐。候选交付运行及发布前必须完整验证。
- adapter 的 `_readyRoot` / `_pending` 保存就绪视图，`State`、`Cursor`、`PendingEvent` 及正常提交使用同一语义边界；运行时 wrapper 与就绪根共享 Business。`WorkingCopy` 保留物理基线，只有提交成功才安装新根及 wrapper。
- [SimulationKernel.RecoverPending](../../src/Kernel/Simulation/SimulationKernel.cs) 继续只处理兼容的输入，不识别游戏版本或挑选修订策略。`CommitState` 继续完成 pending E；本方案不要求新增独立升级发布入口。
- 公共入口只接受当前游戏修订；首次真实业务修订出现时再提供步骤并声明支持范围，不增加旧无版本存档兼容承诺。

首片用测试夹具模拟跨图 ID 改名和 v1/v2/v3，不为展示管道捏造生产规则修订或兼容历史。以下合同持续约束后续真实修订；具体已运行证据见[验收记录](../experiments/game-revision-acceptance.md)：

1. 新游戏及新事件直接使用当前修订；工厂无意外共享，保存并冷开等价。
2. S-only 与 S/E 加载升级均不发布、不推进游标；pending 保留，控制器/Plan 不调用；透传、转换及背包替换 fact 不写回旧 ID。
3. `S1-only → 内存S2 → E2落盘 → 中断 → 目标v3重开` 的对齐路径；重现原S2前态，跨图用领域ID，记录事实不按新命令门槛重新裁决；正常恢复后效果仅应用一次，游标仅推进一次。
4. 不支持版本、解码/步骤/配对校验失败不发布；加载后退出再开可稳定重算；当前版无需转换。
5. 真实磁盘覆盖旧 S/旧 E、旧 S/新 E、当前 S/E，以及新版 E 已发布而 S 未发布的中断窗口；模拟 S 发布成功但返回失败后重开，既不丢失效果也不重复执行。

文档保存动机和边界，接口/XML doc 保存调用契约，编排器和测试保证协调、执行与发布的职责分离。
