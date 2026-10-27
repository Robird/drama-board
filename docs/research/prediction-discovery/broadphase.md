# 新组合发现：碰撞检测 broad-phase 的可借鉴经验

> 2026-10-27。综述与 DramaBoard 适配建议，不是已批准 API。读取了当前 PROJECT-STATE、预测对象草稿与 SpatialContactOccurrenceRule；查阅原始论文和官方文档/源码，未运行性能实验或修改产品代码。Box2D 示例明确区分 2.4.1 与 3.1.1，PhysX 手册使用 5.1.0；链接 main 的接口属于调研当日快照。

## 1. 对应关系：发现关系与维护关系是两份工作

用户的直觉成立：物理引擎的 broad-phase（粗筛）负责找出可能互动的对象对，narrow-phase（精算）才判断是否碰撞、何时碰撞。I-COLLIDE 已采用包围盒剪枝与对象对精算两层结构，并利用连续时刻变化较小的 coherence 减少工作。这是“增量发现新组合”的成熟先例，不只是加速某个已知对象对的求交公式。[I-COLLIDE 原论文，1995](https://www.cs.princeton.edu/courses/archive/spr01/cs598b/papers/cohen95.pdf)

对 DramaBoard 的建议映射是：

| 物理模拟概念 | 当前图空间的对应物 | 需要保留的区别 |
| --- | --- | --- |
| broad-phase proxy | 角色当前运动段在某通道中的成员记录 | 可以用 PassageId，不要求建立欧氏坐标 |
| potential pair | 同一通道、符合业务筛选的人对 | 只是有资格检查，不表示一定相遇 |
| narrow-phase | PassageContactCalculator | 沿用现有精确运动数学和取整语义 |
| cached interaction | 可维护的人对过程或通道预测者内的输出 | 可以暂时没有 Due |
| proxy change | 加入、离开、掉头、轨迹参数或过滤条件变化 | 不仅是坐标变化 |

当前 [SpatialContactOccurrenceRule](../../../src/Spatial/Simulation/SpatialContactOccurrenceRule.cs) 每次 Forecast 都按通道分组并两两求交。因此我们已经有最粗的空间分区；首个收益点是只重算变化通道，而不是先替换求交算法。

## 2. 成熟实现如何增量发现，以及谁负责退出

**Box2D 3.1.1：变化对象驱动查询。** 创建或移动 proxy 后登记到 `moveArray`，`moveSet` 去重；更新时只处理该批对象，`pairSet` 防止重复创建现有关系。双方同时变化时还需抑制双向重复发现。这说明“新对象没有旧预测可监听”的解决方式，是让一个持续存在的空间索引接收成员变化并主动查询。[固定版本 broad_phase.c](https://raw.githubusercontent.com/erincatto/box2d/v3.1.1/src/broad_phase.c)

**创建与退出不一定由同一组件报告。** Box2D 2.4.1 的 `UpdatePairs` 只报告潜在新增 pair，销毁 proxy 后的关系清理交给调用方；其 contact manager 另行检查旧关系是否仍有包围盒重叠，必要时销毁。这不是一个通用的 Enter/Leave 事件总线。[b2_broad_phase.h](https://raw.githubusercontent.com/erincatto/box2d/v2.4.1/include/box2d/b2_broad_phase.h)、[b2_contact_manager.cpp](https://raw.githubusercontent.com/erincatto/box2d/v2.4.1/src/dynamics/b2_contact_manager.cpp)

**PhysX 给出了很接近我们需要的批次 API。** `PxBroadPhaseUpdateData` 输入 created/updated/removed 对象及其 bounds/filter，`PxBroadPhaseResults` 输出 created/deleted pairs。更新以批次提交，结果只含新增和丢失关系。可以借鉴它的职责划分，而不引入 PhysX 本身。[PxBroadPhase.h](https://raw.githubusercontent.com/NVIDIA-Omniverse/PhysX/main/physx/include/PxBroadPhase.h)

**Bullet 的 pair cache 展示了清理需求。** 它提供按单个对象移除其全部重叠 pair、单独移除 pair、查找 pair 和碰撞过滤等操作。映射到我们这里，实体删除或运动段替换必须能清理相关关系，不能只删除一个到期队列项。[btOverlappingPairCache.h](https://raw.githubusercontent.com/bulletphysics/bullet3/master/src/BulletCollision/BroadphaseCollision/btOverlappingPairCache.h)

据此有一个容易遗漏的设计点：**只有 AddedPairs/RemovedPairs 不够。** 假设 A、B 一直同处一条通道，A 改变速度，粗筛关系没有增删，但相遇时间已改变。我们的维护层还必须把“端点发生变化”的已有关系加入 dirty 集合。若运动段 generation 属于 pair 身份，可以表示为旧段关系撤销、新段关系创建；其他不改变身份的参数变化仍需显式失效通知。这是本报告的适配推论。

## 3. 几种索引路线的取舍与性能边界

| 路线 | 依赖的局部性 | 适合 DramaBoard 的位置 | 主要代价或退化 |
| --- | --- | --- | --- |
| 通道桶 + 变化通道全配对 | 互动仅发生在相同通道 | 推荐第一步，几乎不改变空间语义 | 通道内 m 人仍需 O(m²) 候选检查 |
| 通道桶 + 变化成员配对 | 一次只改变少数运动段 | 推荐下一步；新成员查询桶，退出清理关联关系 | 需要反向关联索引、批次去重；多人同时变化仍退化 |
| 一维 sweep-and-prune | 区间端点顺序变化不大 | 长通道内有大量局部活动时可试 | 大跳跃、大批加入/移除、长区间重叠会削弱收益 |
| dynamic AABB tree | 可用包围盒排除大片空间 | 真正二维/三维活动或通道内保守区间索引 | 树维护成本、包围盒过大导致大量假阳性 |
| 空间网格/哈希 | 对象尺寸和局部密度适合划格 | 自由空间大量近似同尺度对象 | 大物体跨很多格、密集单格、格宽选择与去重 |

上述选择没有统一赢家。PhysX 官方说明 SAP 在大量休眠对象时有优势，但全体移动及大量增删会变差；分区路线的权衡不同。其 MBP 超出已定义区域会停止碰撞检测，提醒我们“覆盖区域不足”是正确性问题，不只是性能问题。[PhysX 5.1.0 broad-phase 文档](https://nvidia-omniverse.github.io/PhysX/physx/5.1.0/docs/RigidBodyCollision.html)

动态树以包围盒层级跳过不相交子树，适用于区域查询；Box2D 也允许独立于刚体模拟使用动态树。[Box2D Dynamic Tree](https://box2d.org/documentation/group__tree.html) 2.4.1 的 fat AABB 允许小幅运动不立即重插树；这是一种减少索引维护的宽松包络，其空间余量不是任意长未来的安全证明。[b2_dynamic_tree.cpp](https://raw.githubusercontent.com/erincatto/box2d/v2.4.1/src/collision/b2_dynamic_tree.cpp)

空间哈希把潜在无限网格映射到有限桶；原论文同时研究 hash、表大小和 cell size 的影响。它并没有消除局部拥挤，也不能把哈希冲突当成真实邻接关系。[Teschner 等，Optimized Spatial Hashing，2003](https://cg.informatik.uni-freiburg.de/publications/2003_VMV_collisionDetectionHashing.pdf)

本项目的复杂度推论：若 N 个对象分别分布在小通道，一次变化只触及一个大小 m 的桶，则发现成本可依赖 m 而非 N。若世界真实需要显式维护 K 个相互作用关系，输出这些关系至少要承担 Ω(K) 工作；拥挤场景 K 可为 Θ(N²)。因此可以追求“成本随局部变化和真实关系增长”，不能承诺对任意世界都近似线性。若只求下一个事件而不显式列出所有 pair，这个输出下界不直接适用，但需要另一套有完整性证明的选择算法。

## 4. 最大适配陷阱：离散粗筛不能直接支持任意时间跳跃

物理引擎常在每个时间步更新 proxy。两个点此刻相距很远，下一采样时刻又已互换位置，仅检查两个时刻的包围盒会漏掉中途相遇。Box2D 的 TOI 接口针对给定运动 sweep 求碰撞时刻；官方文档也明确某些旋转碰撞可能漏掉，因此不能把“有 CCD”理解为任意运动方程下的完备保证。[Box2D Time of impact](https://box2d.org/documentation/md_collision.html)

对于 DramaBoard 的离散事件时间推进，有两条可替代路线，以下都是适配建议：

**路线 A：完整已知运动段包络。** 角色开始一段直线通道运动时，将其整个剩余路径视为保守覆盖范围，与同通道其他有效运动段发现关系，再精算相遇时间。只要段内运动方程不变，就无需按经过时间更新 proxy；掉头、到达、退出等事件负责结束该段。更粗的实现直接让同通道全部成员成为候选，最容易证明无漏项，代价是可能产生许多最终不相遇的检查。

**路线 B：有限有效域 + 到期刷新。** 每个 proxy 只保证在 `[now, H]` 内保守覆盖轨迹，可以缩小包络；但必须确保时间推进不会越过 H 而没有刷新发现。即使目前一个碰撞输出也没有，也要安排域失效/跨分区边界的维护时刻。否则“没有预测所以直接跳到很远的业务事件”会漏掉期间刚形成的关系。这条路线可减少候选，但增加维护事件、有效域协调和同刻边界规则。

两条路线都要满足：对每个可能先发生的接触，至少存在一个覆盖它的候选 pair，或者在它之前必然有一次重新发现。长运动段可能让 A 的包络退化成整条通道；B 则以更多维护事件换更紧的范围。首版建议 A，不默认引入固定小步长。

“远处的人不用更新”也需要明确原因：在当前世界模型里，双方被不同通道或不相交的有效包络排除，且所有能破坏这个排除条件的变化都能触发更新。若未来加入全局广播、传送、远程魔法，就必须给这些领域效果另设依赖范围；不能从几何距离独立性推导整个业务世界都独立。

当前 contact 的 floor 和 arrival 的 ceil 是已有离散时间语义。若引入 H，必须在同一模型时间域里定义刷新先后及边界覆盖，不能拿连续几何时间的“刚好相等”直接决定是否可以安全跳过。这部分尚需专门设计与边界测试。

## 5. 建议的 API 形状：稳定职责，索引可替换

以下是职责草图，**不建议现在同时实现全部层次**。首版通道级预测者可以包住发现和求交，后续保持外部契约不变再细分。

```text
MembershipChangeBatch:
    AddedSegments / ChangedSegments / RemovedSegments
    AffectedScopes

Discovery(scope, finalBusinessState, batch):
    完整候选集合                         // 首版，易于核验
    或 AddedPairs / RemovedPairs          // 有规模证据后再增量化

PredictionMaintenance:
    新 pair → 建立过程并首次预测
    丢失 pair → 撤销过程及其输出
    端点变化但仍存在的 pair → dirty
    已消费输出的存活 owner → dirty
    dirty 去重 → 根据同一最终业务状态重算
```

建议约束：

- discovery 的 scope 是业务能证明互动局部性的范围，例如 PassageId。范围的创建也要发现已有成员，不能只等下一次成员移动。
- pair key 按两个端点稳定规范化，并包括当前运动段 generation；索引位置、插入顺序或重算次数不能成为业务身份。
- “尚无相遇输出”的关系仍需要被重新检查的入口。可保存 pair 过程，也可由持续存在的 scope/membership 索引承担；不要求所有负结果都持久化一个 DG 对象。
- 索引是可从完成状态重建的运行数据。预测注册和输出若持久化，则与业务变化在同一完整转换内发布；恢复只 Fold 已记录事实，不重新跑发现并改变历史。
- 先给出完整候选集合并做差集，足以验证生命周期；之后换 changed-member 查询、树或哈希不应改变相遇业务结果。
- 当前不把发现做成任意重入的通用事件总线。业务完成 scratch 变更后统一维护，避免边改 membership 边计算混合状态。

这里借鉴的是批次变更和关系生命周期。物理引擎内部接触缓存通常服务浮点求解、逐帧迭代及性能；它们不会自动提供 DramaBoard 所需的稳定键、单根发布、E-only 恢复或精确重放。

## 6. 推荐试验与必须防住的漏检

建议先做“通道级发现过程 + 完整运动段 + dirty 通道全配对”，以现有全量 Forecast 为独立参考；然后只换成“变化成员配对”，测量真实收益后再决定是否需要更细空间索引。两版必须保持候选集合、取整和赢家轨迹一致，不能只比最终位置。

| 试验 | 必须观察的结果 |
| --- | --- |
| 原先无人相遇，第三人加入 | 创建此前不存在的关系与到期输出 |
| 同一批加入两人、顺序置换 | 两人之间的关系不漏、不重复，身份不变 |
| 已有 pair 不增删，但一人改速/掉头 | 更新相遇时刻；旧段输出清除 |
| 人员移除、重新进入、复用实体 ID | 不继承旧运动段消费状态或悬挂输出 |
| 当前相距远，却在长时间跳跃中相遇 | 路线 A 覆盖；路线 B 在覆盖失效前刷新 |
| 空间范围中新建过滤规则/变更互动资格 | 原本不存在的关系有重新发现入口 |
| 新范围覆盖已有静止成员 | 不依赖它们再次移动才能被索引 |
| 同刻到达、contact、有效域失效 | 与明确裁决的离散时间顺序一致 |
| 大量互不相关通道只改一条 | 其他通道无精算；另测索引/验证/持久化总成本 |
| 单通道高度拥挤 | 正确暴露候选数膨胀，不把它误报为算法失效 |
| 冷开、E-only 恢复 | 已发布预测与轨迹一致，无重复发现导致业务重复 |

本轮没有证据证明动态树或 SAP 比通道桶更适合当前规模。最值得先继承的成果是：**保守范围负责完整性，变化集合控制工作量，候选关系与真实互动分层，创建和回收都有明确责任人。**
