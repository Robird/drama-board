# 预测对象：方向与实施入口

> 历史命名说明：本文保留当时的 ResourceCycle / Cycle 类型、命令与验证记录。当前应用已重命名为 Playground；现行入口与持久身份边界见[重命名说明](../../docs/implementation/playground-rename.md)。旧名称源码链接固定到重命名前的 Git 版本。

> 2026-10-27。早期草稿已经由[局部预测重构](local-prediction-refactor.md)及后续[运行时缓存方案](runtime-derived-predictions.md)取代；此页仅保留方向与导航，不维护第二份切片清单。

当前空间实现：运行中局部维护，纯派生预测冷开重建，不再持久化 Contacts；见[缓存验收](../experiments/runtime-derived-predictions-acceptance.md)。下一步协调 ResourceCycle 第二消费者，保留应用优先级与角色唤醒。

用户已定方向是在扩大世界之前，让子系统维护预测过程与输出，通过事件筛出受影响范围，再重算这部分预测。第一版允许粗粒度或恒 true；没有旧存档兼容负担。

后续讨论已将空间首片具体为：运动变化触发所在通道重新发现；不能仅通知已存在的接触预测，因为此前不存在的 BC 也可能因 B 改向而变为有效。通道本身可承载零到多项输出的过程职责，不要求为每个人对建立持久对象。

当前实施提案和证据集中在：

- [局部预测重构方案](local-prediction-refactor.md)：需求来源、状态与事实、唯一完成入口、时间校验、实施切片及验收。
- [辩证审阅裁决](../research/local-prediction-design-review.md)：三方独立审阅与交叉质询后的保留、化简和延期项。
- [新组合发现综述](../research/prediction-discovery/README.md)：EDMD、broad-phase、KDS、混合系统及当前空间语义的约束。

长期目标仍是 Kernel/Spatial 与 ResourceCycle 的共同执行契约。首阶段完成空间单根预测与恢复闭环，随后协调另一个会话的 ResourceCycle 分支，接入相同维护时机；不能把仅空间局部优化等同于跨系统统一已经完成。

现有 floor-contact、ceil-arrival 与同 tick 稳定仲裁作为首片对照基线，未来可以单独重设计。更细的变化段配对、邻域证书、时间索引和通用过程注册机制，按新方案的重开条件推进，不默认纳入当前实施。
