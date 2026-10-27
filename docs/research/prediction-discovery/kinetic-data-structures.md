# Kinetic Data Structures：用证书发现新的空间关系

> 2026-10-27。专题研究及本仓库适用性分析；不是已经批准的接口或实现。阅读了当前 Spatial 源码和公开第一手资料，未构建或运行仓库测试。CGAL 引用固定为 4.1 历史文档，用于研究机制，不表示建议依赖这个旧版本。

## 1. 最值得继承的是“维护发现所需的证明”

Kinetic Data Structures（KDS）维护随连续运动变化的离散结构。核心是 **certificate（证书）**：一组成立时足以保证结构正确的条件。将坐标替换成时间函数后，条件有了预计失效时刻；到时修复结构、撤销旧证书并建立新证书。对象可以在线改变轨迹，不必预知整个未来。[Basch、Guibas、Hershberger 原论文](https://graphics.stanford.edu/courses/cs268-11-spring/notes/kinetic.pdf)、[CGAL KDS 概览 §71.2](https://doc.cgal.org/Manual/4.1/doc_html/cgal_manual/Kinetic_data_structures/Chapter_main.html)

这与本项目的对应关系是研究者推论：**现有人对预测负责回答“这两人何时相遇”；发现结构的证书负责保证“有可能成为下一对相遇者的人，不会被漏掉”。** 没有当前业务候选，不意味着发现结构可以消失。空通道仍需要接收成员加入通知；非空通道可能需要在无人改变轨迹时，自行处理次序变化。

这一点比按事件筛选 dirty 更进一步。dirty 机制解决谁需要重新计算，证书还提供“即便没有外部业务输入，什么时候必须重新检查”的依据。

## 2. 一维排序：新组合如何从局部修复中出现

经典例子维护运动点的排序及相邻关系。相邻点的次序保持不变，就能保证整列有序；交换发生后只修复附近的相邻关系。插入对象需要找到插入位置、拆掉跨过该位置的证书，再建立两侧证书；删除与改轨迹也有对应入口。[CGAL Kinetic Framework §72.3.1](https://doc.cgal.org/Manual/4.1/doc_html/cgal_manual/Kinetic_framework/Chapter_main.html)

下面是据此推演的简化示例，假设精确时间、连续轨迹、没有三点同刻相遇：

```text
当前顺序：A — B — C
证书：A 在 B 左侧；B 在 C 左侧

A 追上 B → 顺序变为 B — A — C
撤销旧局部证书，创建 B—A、A—C 证书
原本没有的 A—C 组合由结构变化发现
```

此时无需扫描所有点对。最早的两点穿越如果不是当前相邻点，中间必有点先被其中一方穿越，或者存在同刻退化；这解释了相邻证书为什么有效，也标出了证明的前提。同向追及并不破坏这个理由；“只有对向移动才需要预测”反而会漏项。

更高维的对应物可以是维护邻近图、三角剖分或分离结构的证书；不能把一维的“两邻居”数量原样搬到二维、三维。作者的研究展示了通过维护稀疏候选边捕获最近点对的思路；最近点对与报告所有接触仍是不同查询。[作者的 KDS 演示论文](https://graphics.stanford.edu/~comba/papers/socg.pdf)

## 3. 内部事件、业务事件与四项指标

原论文区分：**external event** 改变被维护的目标结构，**internal event** 只为维护内部证明而发生。这里的 external 不是“来自玩家或外部进程”。同一个排序交换，对于“维护排序”是目标变化，对于“维护最近点对”却可能只是辅助维护。[原论文 §1](https://graphics.stanford.edu/courses/cs268-11-spring/notes/kinetic.pdf)

| KDS 指标 | 含义 | 建议在 DramaBoard 测量什么 |
| --- | --- | --- |
| Responsiveness | 单次证书失效的修复成本 | 每次事件检查/重算/新建/撤销多少记录，以及 CPU、分配 |
| Efficiency | 总维护事件相对目标结构变化的数量 | 发现维护触发数与真正接触输出数，不能只看 dirty 数 |
| Locality | 单对象关联多少证书 | 一人改轨迹涉及多少维护记录 |
| Compactness | 证书总规模是否接近线性 | 持久记录、队列和索引大小，是否随历史无限增长 |

这些指标来自原论文；表中项目测量方式是本次建议。经典 efficiency 通常比较允许运动集合上的最坏情形，不保证每条实际轨迹都几乎零浪费；Guibas 进一步区分 weak/strong efficiency。[Guibas 综述](https://graphics.stanford.edu/courses/cs268-16-fall/Notes/g-kds.pdf)

对游戏而言，“修复通道排序”可以是不可见的维护工作，“给两个角色开放交互机会”才是玩家相关事件。两者可能共用调度基础设施，但不宜共用业务含义和消费记录。若未来存在只有维护事实的 occurrence，其持久化、日志可见性及同刻优先级需显式设计；本研究没有裁决一定要独立发布它。

## 4. 现有 contact 不能直接换成“只预测相邻对”

当前 [SpatialContactOccurrenceRule](../../../src/Spatial/Simulation/SpatialContactOccurrenceRule.cs) 对同通道当前运动段两两求交；[PassageContactCalculator](../../../src/Spatial/Internal/PassageContactCalculator.cs) 使用精确整数运算，保留严格内部及正相对时间检查，再把交点时间 **floor** 为业务 Due。contact 消费只追加当前段对记录，不改变位置或速度；见 [GraphSpatialReducer](../../../src/Spatial/Facts/GraphSpatialReducer.cs)。[OccurrenceScheduler](../../../src/Kernel/Scheduling/OccurrenceScheduler.cs) 对相同 Due 按 seed/key 排名，而不按未量化的精确交点时间排序。

以下是本次独立推演的反例，不是已有测试：令当前时刻为 0、通道足够长，三个有效运动段在内部满足：

```text
A(t) = 10 + 10t
B(t) = 12 +  6t
C(t) = 15 +  2t

当前相邻：AB、BC
精确交点：AB = 0.5；AC = 0.625；BC = 0.75
业务 Due：三对都是 0
```

最初非相邻的 AC 已经是当前全量算法的合法候选。只向仲裁器提供 AB、BC，会遗漏可能应最先执行的 AC。按当前 HMAC 输入格式做独立 PowerShell 计算，通道 `p`、实体 `a/b/c`、generation 均为 0、seed 为 0 时，AC 的 rank 确实最小。该计算验证排序反例，不等于运行过完整合法世界构造或 Kernel 测试。

还有四个边界不能略过：

- **消费后继续穿越**：AB 的交互在 0 已被消费，但物理次序到 0.5 才改变。若消费即删除 AB 的几何证书，A 何时成为 C 邻居就无人维护；若在 0 立即交换，则排序已不是真实当前排序。
- **三点同位**：若改为 `C(t)=14+2t`，三对在 0.5 同位。邻接链只有两条边，但当前业务会给三个不同段对提供机会。必须展开退化组或有等价的全对发现机制。
- **起始同位不等于未来相遇**：当前计算器排除共同窗口起点上的 `tau<=0`，相同速度也无唯一交点。按排序 tie-break 随意生成 contact 会改变既定规则。
- **业务响应改变未来**：提前出现的 contact 可以触发掉头，令原预测的精确交点根本不发生。几何探索可基于当前轨迹假设，但每个真实业务转换之后都必须撤销受影响的推演结果。

因此，穿透本身不是 KDS 的障碍；**提前开放交互、量化时间、同刻业务仲裁与几何维护事件混用**才是直接套用的障碍。

这里把现行规则作为分析基线，不把它当作兼容性包袱。用户已明确允许重设计；若另行决定使用精确交点顺序、统一 tick 批次，或改变提前交互语义，就应在新契约下重新评估相邻证书方案。不能从“不能直接替换现有算法”推导“KDS 不适合本项目”。

可研究的保守方案是：先在不改变世界的工作区内，完整发现某个 tick 所涵盖的所有几何穿越，再把对应业务候选交给现有仲裁；或用该时间窗的 swept 区间做保守候选筛选，再精确求交。两者都还需处理新段、消费、到达边界与业务打断，尚未证明或实现。首版通道级全量配对仍是可理解、可靠的基线。

## 5. 可借鉴的 API 形状与数值边界

CGAL 把活动对象增删改通知、证书生成、时间队列、结构修复和独立 audit 分开；其 `ActiveObjectsTable` 的通知覆盖 insert/erase/set，而不只是已经存在的证书。[CGAL KDS 架构 §71.3](https://doc.cgal.org/Manual/4.1/doc_html/cgal_manual/Kinetic_data_structures/Chapter_main.html)

对 DramaBoard 的候选形状如下；这是设计建议，不是搬用 CGAL 的 C++ API：

```text
DiscoveryScope(PassageId)
    OnMembersChanged(added, removed, changedSegments)
    OnCertificateDue(certificateId, dependencyVersions)
    Repair(finalScratchWorld)
      → certificate replacements + discovered contact candidates
    AuditAgainstFullReference(world)

Certificate
    Id, OwnerScope, Kind, DependentSegmentIdsAndGenerations,
    FailureTimeOrValidityHorizon, ProofData
```

重点是 scope 的生命周期比单个 contact 更长；证书引用具体运动代数；新增成员由 scope 接收。运行时类型注册解释 Kind，持久数据不包含 delegate。索引可以重建，完成状态及已发布预测仍需满足现有单根恢复约束。内部证书是否必须持久化，应依据恢复语义与重建成本再裁决，不能由“CGAL 用事件队列”推导出来。

数值方面，CGAL 的精确模型比较代数根，并检查根之后的符号来区分真正失效与瞬间为零后仍有效；它也用事件间隔内的独立静态重建审计结构。[CGAL Framework §72.1.1、§72.2.1](https://doc.cgal.org/Manual/4.1/doc_html/cgal_manual/Kinetic_framework/Chapter_main.html)

本项目目前是分段匀速、BigInteger 有理求交，通常无需引入通用多项式求根。但如果维护顺序需要精确交点，而业务继续使用整数 Due，就需要明确区分内部比较时间与业务时间；仅把全部根 floor 后入同一队列，不能继承经典 KDS 的正确性证明。同刻稳定排序只保证确定性，不自动保证退化处理正确。

## 6. 对后续分片的建议与验收

1. **先统一过程生命周期与通道发现入口。** 保留每通道完整配对，不把相邻算法作为第一版门槛。
2. **建立独立全量参考。** 从业务运动段计算 contact，比较候选集合、赢家与整个事实轨迹；不能仅检查增量表内部自洽。
3. **做隔离的精确时间 KDS 小实验。** 无退化先验证相邻交换，随后加入三点同位、插入、删除、同向追及与改轨迹。明确这是算法实验，还未符合 floor 业务语义。
4. **再选择 floor 兼容策略。** tick 内完整发现或其他保守筛选必须包含本报告反例；每次业务响应后复核候选失效。
5. **测量四项指标及总成本。** 增加规模、局部密度、速度差、打断频率；稀疏局部世界与所有对象挤在一条通道上应分开报告。

性能不能只以总对象数描述。若 n 个角色确实在同一时刻相遇，业务坚持为每对开放机会，输出本身就有 n(n−1)/2 项；这个下界来自需求，而非索引不好。KDS 的价值是在可证明无关时少做工作，不保证任意世界都线性运行。

资料获取范围：CGAL 历史官方章节已阅读全文；原论文、Guibas 综述及作者演示稿使用搜索工具返回的相关正文片段，原 PDF 直接打开超时。未把无法取得的页面内容当作已读证据，未调查这些旧实现今天的维护或编译状况。
