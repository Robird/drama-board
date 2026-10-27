# 新组合发现：DramaBoard 当前语义与算法约束

> 2026-10-27。本地源码适配调查；不修改产品代码，不运行构建或测试。本文件区分已实现语义、数学推演与待选方案。外部算法综述见同目录其他研究包；此处不声称已经实现某种物理引擎算法。

## 结论

当前空间模型已经提供一个很好的局部性边界：**不同 passage 的运动段不会产生 contact**。首先把全世界每步两两扫描改成“运动段变化只更新所属通道”，即可减少大量无关计算，无须先解决通用空间索引。

但“一维只看相邻者”不能直接替换当前 contact Forecast。当前业务输出是提前开放的交互机会，时间向下取整，同 tick 的候选以 seed/key 仲裁；它不是按精确交点依次发生的刚体碰撞。相邻换序算法可以作为内部发现技术研究，必须补上同 tick 完整性、已消费交点与索引维护等证明。

第一版最易验证的设计仍是：通道级发现 owner 常驻，运动段加入/替换触发通道局部重新发现；精确求交沿用现有数学；然后逐渐把通道全量扫描缩成“变化段与本通道其他段”的增量 join。是否引入邻接证书应由密集通道实测和语义证明决定。

## 一、当前实现事实

### 运动段和接触的权威

- [TraversingLocation](../../../src/Spatial/State/SpatialLocation.cs) 保存 `AnchorTime`、`AnchorOffset`、`SpeedSnapshot`、`TargetPlaceId` 和 `ArrivalDue`。运动数学实现入口是 [SpatialMath](../../../src/Spatial/Internal/SpatialMath.cs) 与 [PassageContactCalculator](../../../src/Spatial/Internal/PassageContactCalculator.cs)。
- 每段运动恒速、锚定于整数 tick 与整数 offset。开始运动和掉头增加 `MovementGeneration`；到达保留代数但不再处于 TraversingLocation。
- [SpatialContactOccurrenceRule.Forecast](../../../src/Spatial/Simulation/SpatialContactOccurrenceRule.cs) 按 passage 分组，枚举该组所有无序实体对；过滤 `ConsumedContacts` 后调用精确求交，返回当前段对的所有未来 contact。
- 精确求交使用 `BigInteger`，共同有效窗口起点为两个 AnchorTime 的最大值。要求相对速度非零、交点严格晚于共同起点、严格早于双方物理退出，且位置严格处于通道内部。同速同位、共同起点的重合、端点相遇不产生 contact。
- [PassageContactKey](../../../src/Spatial/Contacts/PassageContact.cs) 是 passage 加两个规范排序的 `(EntityId, MovementGeneration)`，不是“两个角色曾经相遇”的永久身份。
- [GraphSpatialReducer.ApplyContact](../../../src/Spatial/Facts/GraphSpatialReducer.cs) 仅追加当前段对的消费记录，**不改变位置、速度或方向**。后续是否打招呼、战斗、掉头属于其他业务行为。

### 时间与仲裁

- contact 为精确交点的数学 floor，arrival 为物理退出的 ceil。对于严格内部交点，contact tick 严格早于参与者双方的 arrival tick，包括负时间情形。
- [OccurrenceScheduler](../../../src/Kernel/Scheduling/OccurrenceScheduler.cs) 按整数 Due、HMAC(seed, Due, canonical key)、规范 key bytes 决胜；同 tick 不按精确交点先后排序。精确分数没有进入 CandidateDue。
- [LogicalInstantRules](../../../src/Kernel/Simulation/LogicalInstantRules.cs) 为同 tick 的连续 occurrence 增加 CausalOrdinal；ordinal 是提交顺序，不是额外物理时间。任何事件结束后都重新选赢家。
- [ForecastRound](../../../src/Kernel/Simulation/ForecastRound.cs) 允许 Due 等于当前时间，拒绝早于当前时间；新段可在创建 tick 产生新的 contact。不能将刷新条件写成 `due > now`。
- [SpatialPlanner.TryReverseTraversal](../../../src/Spatial/Planning/SpatialPlanner.cs) 要求 `AnchorTime < at < ArrivalDue` 且位置严格内部。同 tick 第一次掉头可以创建新 contact；新段不能在自己的锚定 tick 再掉头。

### 发布与恢复

[SimulationKernel](../../../src/Kernel/Simulation/SimulationKernel.cs) 先完整 scratch-fold/validate，发布 E，再发布 S，S 成功才安装当前世界。pending 恢复只 Fold 保存的 facts，不重新 Forecast/Plan。预测和发现生命周期的权威变化必须进入同一完整转换；运行时索引可以重建，但不能在 S 之后才补写预测真相。

当前 validator 检查实体、通道、日程、消费记录的完整性；尚不存在通用持久发现过程表或输出表。草稿中的通道 owner 是拟议设计，不能当作已实现接口。

## 二、哪些变化会产生新组合

下表基于 [SpatialPlanner](../../../src/Spatial/Planning/SpatialPlanner.cs)、[GraphSpatialReducer](../../../src/Spatial/Facts/GraphSpatialReducer.cs) 与 [SpatialOccurrenceRule](../../../src/Spatial/Simulation/SpatialOccurrenceRule.cs)，不是一张要求新增所有事件类型的 API 清单。

| 已有事实/变化 | 当前 contact 集合影响 | 新机制必须做什么 |
| --- | --- | --- |
| EntityPlaced | 只放到 Place，无运动段，不产生 contact | 无须扫描通道；未来开始运动时才发现 |
| TraversalStarted | 新运动段加入 passage，可与任何当前同通道段形成新对 | 找到通道成员；发现新人相关关系；注册到达输出 |
| TraversalReversed | 同一角色旧代数结束、新代数开始；可能把原来不相遇的一对变成相遇 | 删除旧段输出/消费项；发现新段与当前同通道所有相关段，不仅更新已有相遇对 |
| TraversalArrived | 段退出，没有其他运动改变 | 撤销涉及此段的输出和消费项；邻接索引还要连接退出者两侧的新邻居 |
| EntityRemoved | 同到达，可能移除在途实体 | 同上；删除不能只处理有输出的段 |
| PassageContactOccurred | 只使一个当前段对不再可输出 | 删除该业务输出；其他段对预测保持有效；索引维护不能误以为双方运动结束 |
| PassageEntryAccessChanged | 入口权限改变，不修改已开始运动 | 当前段 contact/arrival 无须重算；未来 Start/Reverse 用新权限 |
| PassageEntryChangeScheduled/Applied | 新建/消费入口日程，Apply 在到期状态上应用 patch | 维护该日程输出；即便 access 无净变化也要消费日程 |

“到达/删除会创造新邻接”是**内部算法关系**的新增，不代表两个剩余运动段的物理交点方程因此改变。设计通知时应分别表达业务事实和发现索引需要的维护操作。

当前没有独立加速、瞬移、改拓扑、改通道长度等业务操作，不能据此保证未来它们可免费纳入。若新增，会成为新的发现范围和版本边界。

## 三、邻接方案的可复现反例

取 passage `p`，长度 100，三个实体 `a,b,c`，当前代数均为 1，所有运动朝同一端：

| 实体 | 出发 tick | 速度 | tick 1 的位置 |
| --- | ---: | ---: | ---: |
| a | 1 | 10 | 0 |
| b | 0 | 2 | 2 |
| c | 0 | 3 | 3 |

这可由现有 Start API 构造。b、c 在共同锚定时刻重合，但 tau=0 被排除，之后不相遇。a 加入后：

```text
ab: 10(t-1) = 2t  => t = 5/4
ac: 10(t-1) = 3t  => t = 10/7
floor(5/4) = floor(10/7) = 1
```

当前几何顺序是 a,b,c，ac 非邻接，但当前 Forecast 同时输出 ab、ac。以 `WorldSeed=0` 和现有 JSON canonical key 编码，scheduler rank 为：

```text
ab key = ["graph-spatial/contact","p","a",1,"b",1]
ab rank = 54d2fe02eb0a7baa780d43b8d151d8bc701919fd9946db7c3e6fbfe598bc9990
ac key = ["graph-spatial/contact","p","a",1,"c",1]
ac rank = 37a7a1a86ab5c6d3cd5d44019d621888e692e05fccf80213cd114ca2f2c80565
```

所以当前完整候选集合先选择 **ac**。只提交相邻 ab 既改变候选集合，也改变此 seed 下的赢家。本文用独立 Python 脚本按源码的 HMAC/大端字段编码验证了上述 rank 与有理数；没有运行产品测试，不把它记作现有自动化测试覆盖。

可复现的独立探针（Python 3，仅标准库）：

```python
import hmac, json, struct
from fractions import Fraction

for pair in ("ab", "ac"):
    key = json.dumps(
        ["graph-spatial/contact", "p", pair[0], 1, pair[1], 1],
        separators=(",", ":"),
    ).encode()
    message = (
        b"DramaBoard.Kernel.OccurrenceScheduler.v1\0"
        + struct.pack(">q", 1) + struct.pack(">i", len(key)) + key
    )
    rank = hmac.digest(struct.pack(">Q", 0), message, "sha256")
    print(pair, key.decode(), rank.hex())
print("ab", 1 + Fraction(2, 8), "ac", 1 + Fraction(3, 7))
```

还有两个陷阱：

1. 消费 ab 时世界仍在整数 tick 1，真实位置仍为 0,2,3；ApplyContact 不移动实体。若重新按当前位置找邻居，ac 仍不可见。没有其他机制保证它在 tick 1 被发现。
2. 即使按假想连续时间先交换 a,b，再发现 ac，也只能说明发现遍历可达；若已经发布 ab，就无法恢复本来应该先 ac 的业务顺序。应先完成该 tick 必要候选的发现，再交给现行仲裁。

这不证明邻接技术无用，只排除了直接替换。若采用内部精确时间扫描，必须说明如何枚举最早 tick 的全部合法业务候选，如何处理同精确时刻多体相交和同位置同速组，以及业务回应改变运动后如何丢弃失效的预推演结果。

## 四、四层职责与可选 API 形状

以下是建议，没有裁决公共类型名。

| 层 | 输入/输出 | 不应承担的职责 |
| --- | --- | --- |
| 候选关系发现 | 业务运动段、局部域成员及变化 → 保守段对/候选关系集合 | 不把“以前没相遇”理解为以后无需关注；不要求每个候选都真有 contact |
| 精确预测 | 当前段对、通道定义 → 零或一个精确验证后的业务输出 | 不选择业务回应；不以重算次数创造新身份 |
| 已输出预测维护 | owner/段版本/消费记录、事实批次 → 保留/撤销/替换输出 | 不依靠最后执行时抛错来弥补漏取消；不丢掉零输出过程的发现入口 |
| 业务响应 | 选中的 contact 与世界 → 消费、回应及新运动事实 | 不直接操作调度器堆或内部邻接证书 |

关键区别是：**发现一个关系、发现它有交点、交点业务机会已消费、几何关系已结束是四件事。** 若采用换序证书，已经消费的交互机会仍可能对应一个未来物理换序；几何索引不能因为业务输出已消费而永久不再维护该换序。

一个较保守的生命周期草图：

```text
通道发现过程（空通道也有创建入口）
  接收段加入/移除/替换及必要的消费通知
  维护局部成员、发现/撤销段对
  对需要检查的关系调用现有精确求交
  输出以原 candidate key 标识的当前未消费 contact

每次完成的业务转换
  先应用整批业务事实
  再基于最终 scratch 状态完成发现、失效、重算
  最后发布业务与预测的同一完成状态
```

通道过程可以输出多项，亦可只负责创建 pair 过程。前者首版维护面小；后者细粒度但需要 pair 注册/销毁/索引和零输出关系策略。没有必要为了“对象化”先把所有 n² 潜在对持久化。

## 五、分层优化的可证明边界

### 保守局部方案

1. 通道有自己的发现 owner，成员变化时重算该通道所有段对。成本从每次全世界 `Σ n_p²` 求交降为受影响通道的求交；这仍不保证总程序时间相同幅度减少。
2. 再缩为变化段与同通道其他段的 join：新建或替换 A，只需计算 `(A, other)`；移除 A 清理这些关系；contact 消费只删该 pair。其他段对的数学输入没有变化，因此原预测保持不变。
3. 局部成员索引、entity→pair 输出反向索引可从权威状态重建；如果未来采用惰性失效堆，需要定义过期项回收，不能按运行时长无限积累。

第二步依赖当前恒速、固定 passage 定义和 pair-local contact 语义，是基于源码的推演。若业务改变了多个人的运动，则对所有受影响段求并集；若引入影响整条通道的力场，应提升到通道范围。

### 邻接证书或更强空间索引

至少需要同时满足以下条件，才可声称替换后保持当前语义：

- 每个可能成为下一赢家的真实 contact 都能在选择前发现；只证明“最终会发现”不够。
- 同 tick 的完整候选集合或具有等价赢家证明的保守集合已经就绪。当前完整 Forecast 返回所有未来输出；若改成按时间窗惰性输出，还要明确改写这一契约，不能沿用“全候选集合相等”的验收口径。
- 完整处理入场、掉头、离场、同位、多体同刻相交；内部证书的精确时间顺序不得替代业务 seed/key 顺序。
- 消费 contact、运动段 generation 与索引关系分别有寿命；恢复后重建索引不能恢复已消费业务机会。
- 内部维护事件是否占用 Kernel occurrence/ordinal 必须裁决。若仅为算法维护而增加可见 occurrence，会改变世界 cursor、日志和同刻预算；初版更适合把它作为受控的派生索引工作。

当前如果 n 个段实际产生 Θ(n²) 个应展示/处理的交互机会，至少输出这些机会本身就有对应成本。局部算法应消除无关检查，不能承诺任意世界都接近线性。

## 六、实施前应补的独立验收

已有测试源码的明确锚点：

- [PassageContactTests](../../../tests/Spatial.Tests/Contacts/PassageContactTests.cs)：floor/负时间、出生 tick、同 tick 掉头后新代数接触、tau=0/同速/端点排除、消费清理、乱序输入稳定。
- [SpatialPlannerReducerTests](../../../tests/Spatial.Tests/Planning/SpatialPlannerReducerTests.cs)：严格内部掉头、只在新段创建时检查入口、到期 partial patch/no-op 日程消费。
- [SpatialContactKernelAcceptanceTests](../../../tests/Spatial.Tests/Acceptance/SpatialContactKernelAcceptanceTests.cs)：contact-only Kernel 提交、消费与回放。

建议新增，不表示已完成：

1. 本文三人反例：验证 seed 0 时非邻接 ac 先赢，随后 ab 仍能消费。
2. 空通道→一人→第三人入场；过去零输出 pair 因掉头变为有效；邻居移除暴露新内部邻接。
3. 全量参考从运动业务状态独立计算，不读取被测输出表/dirty 集；比较候选身份、Due、kind、赢家和最终轨迹。
4. 多体同刻、同位置同速组、一个 tick 内多次不同 generation 的接触；明确不能用“每人每 tick 只一次”抑制合法机会。
5. 无关通道事件不调用本通道精确求交；保存/冷开与 E-only 恢复不重新规划已记录结果。
6. 性能分开测：粗筛候选数、精确求交数、路由/索引维护、候选仲裁、不可变集合与持久化开销。重算减少不等于整体加速已证实。

本调查只新增本文件，未更改当前空间语义、公共 API、代码或 schema。
