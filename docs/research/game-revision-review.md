# 游戏初始化与修订：交叉审阅裁决

> 历史命名说明：本文保留当时的 ResourceCycle / Cycle 类型、命令与验证记录。当前应用已重命名为 Playground；现行入口与持久身份边界见[重命名说明](../../docs/implementation/playground-rename.md)。旧名称源码链接固定到重命名前的 Git 版本。

范围：对[设计稿](../design/game-initialization-and-revision.md)执行 dialectical-simplification。三位独立评审分别担任需求怀疑者、最小架构设计者、语义守护者；第一轮独立调查，第二轮交换最强反例，由主线程核对源码裁决。第二轮已收敛，无需第三轮。本轮仅完善文档，未实施迁移、未运行测试；下述测试均为阅读所得证据，不能当作新管道验收。

后续状态：首片已实施，运行证据见[游戏修订验收](../experiments/game-revision-acceptance.md)。下文保留实施前的审阅语境，不作为当前未完成清单。

## 需求账本与证据来源

| 来源 | 必须保留的要求或事实 |
| --- | --- |
| 用户明确决策 | 普通工厂构造初始领域图；S/E 各自携带同一编号体系的 GameRevision；加载升级内存表示，pending 留给正常执行。 |
| 用户范围 | 没有既有旧存档兼容包袱；建立机制不等于永久支持所有历史。 |
| 当前 Kernel/adapter | 单会话独占，Plan/控制器在 E 前；E 为已决定的有序事实；E/S 分开发布，S 成功后安装，故障重开，拒绝 S/E/E。 |
| 实际消费者 | 唯一当前应用 ResourceCycle；Schema/业务修订号及迁移管道未实现。Spatial 不自动加入本片。 |
| 未裁决内容 | 未来具体平衡修订如何处置活动进度、哪些历史版本值得保留；无需求时不补通用政策。 |

PROJECT-STATE、术语表与设计稿来自同一讨论，不能互相充当独立证明。证据优先级为用户决定、当前代码/测试、具体失败轨迹，再到设计推论。

当前实现依据：

- [IOccurrenceHistory](../../src/Kernel/Journal/IOccurrenceHistory.cs) 的单拥有者、E/S、稳定实例与失败合同。
- [CycleDurableHistory](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/src/ResourceCycle/Persistence/CycleDurableHistory.cs) 的原始根读取、LoadPending、CommitEvent/CommitState；[Kernel](../../src/Kernel/Simulation/SimulationKernel.cs) 的正常规划与 RecoverPending。
- [CycleFact](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/src/ResourceCycle/CycleFact.cs) 存角色/资源替换子图；[CycleEngine.Fold/ValidateFact](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/src/ResourceCycle/CycleEngine.cs) 与 [ReplaceChildren](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/src/ResourceCycle/CycleModels.cs) 按领域 ID 应用它们。
- 相邻 DG 的 [WorkingCopy](../../../durable-graph/src/DurableGraph.Persistence/WorkingCopy.cs) 明确 Event 以局部对象身份独立捕获，CommitEvent 不推进 State 保存基线，CommitState 可发布新根。
- [CyclePersistenceTests](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Tests/CyclePersistenceTests.cs) 的 E-only、安装时机测试；[MultiplayerRecoveryTests](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Tests/MultiplayerRecoveryTests.cs) 的采集/感知恢复、控制器输入中断；[CycleStorageShapeTests](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Tests/CycleStorageShapeTests.cs) 的真实图形检查。它们证明目前合同的覆盖意图，不证明跨游戏版本升级已实现。

## 裁决与最小模型

最小模型保持为：应用 State 根和 Event 包装各自存版本，一个应用内普通升级函数，adapter 暴露升级后的就绪视图，现有正常恢复负责执行和发布。

| 裁决 | 发现与改动 | 不能删除的语义 / 不引入的机制 |
| --- | --- | --- |
| keep / clarify | State 转换不依赖 pending 的有无/内容，受支持旧步骤的语义与领域 ID 映射保持稳定。 | 保住原 E 的规划前态；不要求 CLR/DG 对象身份跨进程一致。 |
| simplify | 多跳混合版本先对齐，再按相邻修订推进；Event 转换只读取该步对应的 State/映射。 | 保留版本上下文；不建通用版本图、阶段接口或持久映射表。 |
| keep | 跨图关联用已有领域 ID，保留版本化 Event 包装与磁盘基线/内存视图区别。 | 两图身份独立；不建新全局身份或 checkpoint 系统。 |
| simplify | 加载只做结构、版本、边界与修订特定检查，真实效果交给 Fold/Complete/Validate。 | 不预执行 E，不以新命令门槛重新裁决已记录事实。 |
| delete / defer | 删除草案的两个同构 Input/Result record 和可替换 Pipeline 接口，改普通函数与具名 tuple。 | 草案类型减少 3 个；多消费者/实现替换有实际需要时再提接口。 |
| simplify | 用真实磁盘测试夹具模拟跨图、多跳；生产先有当前版本，不为演示捏造规则修订。 | 不减少故障窗口覆盖；生产历史支持范围由真实需求决定。 |

## 决定性反例

**前态会因 pending 改变。** `U(S1, 无E)=X`，Plan 基于 X 生成 E2 并发布；S 未发布时中断。若重开 `U(S1,E2)=Y`，即使两次各自确定、版本都为 2，E2 也可能引用 X 的资源 ID，或覆盖 Y 中不同的进度。仅做版本/终态合法性检查发现不了所有这种变化。最小修复是 State 单向决定映射，Event 消费它；不需要通用可交换性证明。

**后续程序改变旧步骤。** 首次 1→2 映射 `old → a` 后发布 E2；后来程序把同一步改为 `old → b`。重开旧 S1/E2 时，a 失去对应物。修复是保持所支持路径的转换含义，变化走新修订；若修复缺陷确实无法兼容，明确拒绝受影响边界，不能仅增加 TargetRevision 掩盖旧步骤变义。

**多跳读取错误版本的前态。** v2 的 E2 根据 S2 决定了角色替换值，磁盘仍为 S1/E2。v3 打开应先重现 S2，再用 2→3 的原/新 State 上下文转换 E2。若把 S 一路升完才把 S3 冒充事件转换所需的 S2，新增字段或映射可能被覆盖或误算。可等价优化，但不能省略该语义边界。

**把两图的对象身份当业务身份。** State 与 Event 都可能有相同数字的 DG ObjectId，但代表不同对象；热运行偶然共享 CLR 引用也不会成为冷开的跨图保证。直接用这些键合并映射可能套错角色/背包。使用现有领域 ID 足够，无需额外身份层。

## 交叉质询后的修订

- 需求怀疑者接受“同源数据确定性”还不足以表达 pending 存在前后的前态稳定性；保留这一约束，不把它当过强确定性删掉。
- 最小架构设计者将“联合一致性”收窄为具体转换合同与边界检查，接受不增加提前 Fold 的验证器。
- 语义守护者接受普通函数替代接口，并将多跳对齐限定于首片可产生的 `S_revision ≤ E_revision`，不要求泛化到所有版本组合。
- 三方均接受不制造生产兼容史、保持现有 Kernel 语义；主线程核对了 DG 的独立 Event 图和 ResourceCycle 的替换事实代码。

## 下一最小切片与未决边界

当前版两处修订号与持久事件包装 → 普通升级函数接入 adapter → 夹具验证跨图 ID 转换及 `S1-only → E2落盘 → 目标v3重开` → 正常恢复后实际 S 冷开。原有失败返回与不重做控制器合同继续保留；这是一条纵向验收链，不是四个独立框架项目。

没有必须向用户追加裁决的问题。State 修订确需依赖 pending、逆序版本配对、持久替代 E、可交互过程型 E，均没有当前消费者；出现具体业务要求时重开。上述限制是首片可继续历史的合同，不是宣称所有理论迁移都只能如此。
