# 设计决策：对象图初始化与游戏状态修订

状态：工厂初始化、应用层整体修订及 State/pending E 联合处理是用户已采纳的方向。本文的接口与编排是首片设计草案，尚未接入运行；当前 ResourceCycle 没有 `GameRevision` 字段或游戏修订管道。现有 XML 文档只标明职责边界，不构成迁移实现。

## 1. 新游戏由工厂构造

新游戏工厂接收场景、角色设置及必要时的生成种子，组装初始领域对象图；校验业务约束并重建派生预测后，由持久化入口发布初始 S。工厂直接构造当前游戏修订的状态，不从最早版本逐步升级。

现有 [CycleWorld.Create / CreatePlayable](../../src/ResourceCycle/CycleModels.cs) 已是此模式的简单实例。随着场景生成增多，再将生成逻辑移出运行时 wrapper；目前不要求建立统一生成器接口。

- 地点、资源、角色等生成器可以是普通函数，各自构造子图，由上层连接引用、校验身份唯一性和引用完整性。
- 每次新游戏的可变状态独立；外部传入子图的共享/转移约定应明确，不能默认工厂会深复制。配置文件可以成为可选输入，不是必经表示。
- 随机生成使用显式种子及受控随机源；需要复现时记录生成器版本和参数。仅有相同种子不保证跨生成器版本得到相同世界。
- 生成结果作为对象图落盘，继续游戏不重新生成已有部分。以后扩建世界也不能覆盖玩家造成的变化。

## 2. 三个不同的版本与职责

| 概念 | 职责 | 当前状态 |
| --- | --- | --- |
| DurableGraph Schema 版本 | 将旧持久表示还原为可供应用处理的类型；State 与 Event 各自都可能需要 Schema Upgrade。 | 已有 DG 能力；`UpgradeContext` 不提供跨图访问和对象 ID 分配。 |
| `GameRevision` | 表示领域对象图已经完成哪些游戏语义修订，决定下一步修订函数。 | 已选定整体递增整数方向，尚未加入存档根。 |
| `WorldVersion` | 标识模拟谱系与已完成的发生项转换次数。 | 现有 [WorldVersion](../../src/Kernel/Simulation/WorldVersion.cs)，不能兼作游戏修订号。 |

Schema 升级解决“能否还原”，游戏修订解决“还原出的世界应如何变化”，Rebuild 只负责派生预测与索引。业务修订必须显式处理正在进行的活动：例如周期缩短时保留原到期时刻、完成比例还是重置进度，不能靠重建预测暗中选择。

首片采用单一 `GameRevision` 和明确的相邻修订步骤。没有 Schema 改动也可以增加游戏修订号；普通程序发布也不必每次增加它。独立可选内容包确实出现时，再评估分模块版本或已应用修订 ID 集合，不同时引入两套重复进度。

“Schema 无关的进度”指其语义独立于 Schema 版本；字段本身仍正常持久化。新增该字段时，支持范围内的历史根需要明确映射到旧游戏修订号，不能自动标为最新版。未知、更高或缺少升级路径的版本应明确拒绝。

## 3. 修订的输入是 State 与可选 pending E

**应用层游戏修订过程同时负责 State 和 pending E；不要求在进入修订过程之前先用旧规则完成 E。** 两者必须作为一致的一对处理，允许一个修订步骤采用以下策略：

| 策略 | 行为与责任 |
| --- | --- |
| 透传 | 明确确认 E 的语义在修订后的 State 上仍成立，保留待完成事件。没有 Schema Upgrade 不代表语义必然兼容。 |
| 转换 | 协调修改 State 与 E 的内存表示，例如同步修改物品 ID、槽位键、引用或替换式 fact 的子图。 |
| 结算 | 根据该修订定义的语义，将 pending E 的结果纳入 State，明确返回“已结算”。可先按旧语义完成再升级，也可直接计算等价的新版结果。 |
| 拒绝 | 不支持这种历史状态或事件时停止，不写入任何迁移结果。 |

E 不是重新决策的请求。修订不调用 CharacterController、Plan、LLM 或外部业务副作用，不重新抽取已记录的随机结果，也不把旧 E 交回调度器重新仲裁。若新规则移除了某种动作，需要明确的结算/补偿政策；不能以 `PendingEvent = null` 静默丢弃已记录的工作。

修订处理的是加载后的工作副本。原始磁盘 E 保留在历史中，内存里的转换不会重写旧提交。首片建议保留原 E 的 CauseKey、TargetInstant 和对应游标边界；改变历史身份或时间轴属于单独设计的历史重写，不混进游戏修订。

例如旧 E 保存了“角色背包装入一件旧 ID 物品”的整个背包替换值，仅升级 S 中的背包是不够的：随后 Fold 旧 E 会把旧 ID 写回来。联合输入让这两个位置能在同一步中协调处理。

## 4. 首片接口草案：先固定语义，再固定类型

以下名字和形状仅供下一实施切片使用，**不是当前可调用 API**。保持 ResourceCycle 应用层类型；在第二个实例证明共性前不提取通用引擎框架。

```csharp
// 草案：State 根未来携带 GameRevision；此处不另外复制一份版本权威。
public sealed record CycleRevisionInput(
    CycleDurableRoot State,
    OccurrenceEvent<CycleFact>? PendingEvent);

// 通过结果种类区别“仍有待完成工作”与“已结算边界”。
public abstract record CycleRevisionResult {
    public sealed record Pending(
        CycleDurableRoot State, OccurrenceEvent<CycleFact> Event, string Explanation)
        : CycleRevisionResult;
    public sealed record Settled(CycleDurableRoot State, string Explanation)
        : CycleRevisionResult;
}

public interface ICycleGameRevision {
    int FromRevision { get; }
    int ToRevision { get; }

    /// <summary>Revises the restored State and pending Event together.</summary>
    /// <remarks>No publication, planning or controller calls. Preserve input snapshots.
    /// Return an explicit pending/settled result; reject unsupported cases by throwing.</remarks>
    CycleRevisionResult Apply(CycleRevisionInput input);
}
```

结果的 State 必须带目标修订号；编排器检查版本连续、游标和 pending 状态转换，不能只相信声明。`Explanation` 解释事件透传、转换、结算或补偿的政策，用于发布摘要；没有 E 的输入不得凭空产生 pending E。输出 `Settled` 后，后续步骤只处理 State，不能再次结算原 E。

游标检查随结果种类进行：`Pending` 保持输入游标和事件身份/目标时点；从有 E 的输入转为 `Settled` 时恰好 Advance 一次；无 E 的步骤保持游标不变。编排器应积累各步骤的处置说明，而不只保留最后一步。

接口接收的是 DG 已能还原的当前 CLR 表示，但允许仍带旧业务语义。需要保留哪些旧字段/DTO/事件绑定，由实际支持的修订决定；不能删掉唯一的旧数据解码途径，再指望应用层修订恢复它。

## 5. 打开与发布编排草案

建议将打开过程分为“还原工作副本”和“交付可运行世界”，让当前规则的完整业务校验与 Rebuild 在修订完成后执行：

1. 打开独占工作副本，读取最近 S、head 和可选 pending E；完成 DG 还原。检查历史形状、根身份和原 E 与游标的边界关系，保存原 head 作为本次来源。
2. 根据 S 的 `GameRevision` 选择修订链。当前协议只有紧跟 S 的一个 pending E，因此 E 的游戏语义版本继承配对 S，不另存重复版本权威。
3. 顺序处理 State/E 对。步骤只能替换私有候选图，不发布、不交给正在运行的 Kernel。步骤之间检查所声明的目标版本和边界；不提前套用最终版业务约束去否决合法的旧世界。
4. 修订链完成后，如果仍有透传/转换后的 pending E，按目标语义完成它。原 E 最多结算一次；无 E 时游标保持不变，有 E 时最终游标应等于原游标对原 CauseKey/TargetInstant 的一次 Advance。游戏修订本身不伪造一次玩法发生项。
5. 对最终业务图校验、Rebuild，并检查游标/时间一致。准备携带目标 `GameRevision` 的根及修订摘要。
6. 一次发布最终 S，再安装运行时世界。摘要记录来源 head、起止修订号和 pending E 的处置说明，便于解释跨版本边界。

此首片选择把 pending 工作结算完再发布，因此落盘形状只有 `旧 S → 新 S` 或 `旧 S → 旧 E → 新 S`。转换后的 E 可以只存在于本次工作副本，不插入第二个 E，也不发布“State 已升级而 pending E 尚未升级”的半成品。原 E 到新版 S 的解释由显式修订政策承担。

同版本且没有修订需求时保留现有打开/显式 RecoverPending 路径，不为普通读取增加提交。跨修订历史的任意重放也不等于使用当前 Fold 重放全部旧 E：后续若提供此能力，必须选择修订后的 S 作为起点或使用相应版本语义。

失败前未发布的候选图全部丢弃；若提交结果不确定，停用工作副本并重开检查真实 head。业务变化、游标、修订号同根发布，使进程中断后可以从旧根重新计算或直接认出已完成的新根；不要求每一步在已修改的同一内存上重复执行仍幂等。禁止对故障会话透明重试。

## 6. 当前源码接缝与下一切片验收

- [CycleDurableHistory.Open](../../src/ResourceCycle/Persistence/CycleDurableHistory.cs) 目前立即 Validate/Rebuild，之后才暴露 pending E；需要拆开早期还原与就绪世界交付。
- `CycleDurableRoot` 构造函数也会调用当前业务 Validate。实施时需区分“还原/修订候选的结构检查”与“可运行根的完整校验”，避免接口草案要求的中间修订结果被构造函数提前拒绝；不能只移动 Open 中的一次 Validate。
- 当前 `CycleDurableHistory.CommitState` 必须完成 pending E。DG 的 `WorkingCopy.CommitState(nextState)` 本身允许没有前置 E，故缺口在应用 adapter 的独立修订发布入口；不放宽正常 Kernel 提交合同。
- [SimulationKernel.RecoverPending](../../src/Kernel/Simulation/SimulationKernel.cs) 假定 State/E 已与配置的 Fold/Complete/Validate 语义兼容。它负责正常恢复，不负责识别游戏版本或挑选修订策略。
- `CycleDurableRoot` 尚无游戏修订字段；本轮仅记录决策、草案及现有代码边界，未增加旧存档支持承诺。

下一实施切片先实现工厂/根版本/应用层编排的最小闭环，并用一个真实跨图修订验证，而非仅添加无人调用的接口。至少覆盖：

1. 新游戏直接为当前修订，两次工厂调用无意外共享；保存并冷开等价。
2. S-only 修订与 pending E 透传、转换、显式结算；背包替换 fact 不把旧 ID 写回；控制器/Plan 调用为零。
3. 多步修订中，E 在某一步结算后不重复应用；游标只推进一次；已有采集进度遵循指定政策。
4. 不支持版本、步骤失败、最终校验失败均不发布；当前版本重开不重复修订。
5. 真实磁盘上验证提交前中断与提交成功但返回失败后的重开；原历史可追溯，不出现半升级 State/E 组合。

文档保存动机、政策和实施边界；未来实际接口/XML doc 保存调用契约，编排器和测试保证顺序及恢复不变量。共性成熟后再决定是否提取引擎层。
