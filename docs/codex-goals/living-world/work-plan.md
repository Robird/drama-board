# 当前工作计划

2026-11-01：**本阶段已完成 C1–C8**，GameRevision 22。当前状态在此维护，逐项证据与启动/续局交接见[阶段验收](acceptance.md)。没有尚未完成的必需项或等待用户裁决的阻塞。

## 交付状态

| 条件 | 最终依据 |
| --- | --- |
| C1 连续世界 | 山野镇唯一推荐入口，同根镇山往返；Microtown 已从主线移除，历史实现与测试由 [Git tag](../../archive/microtown.md)保留。内部 Cycle 命名不需机械迁移。 |
| C2 可重复生活 | 修订 22 复验两次经济往返与交易账；三日真实模型局又产生采集、生产、消费和市场流转，身体需求/天气开启，无隐藏补给。 |
| C3 角色与控制 | 共享 Human、三模型、Human＋两模型均有证据；启动/冷开可改绑定，保留角色身份与个人材料。 |
| C4 互动 | 跟随、大喊、赠与、报价与忙碌响应有当前回归；真实混合自由会话自然结伴、分开并重逢，拒绝清楚且无隐式取消。 |
| C5 恢复与观察 | 正常/pending、竞争/拒绝、Schema Verify、内容绑定及实际 Gym 导出/管理分叉/两次冷开通过；源分支不变、证据前缀与角色隐私正确。 |
| C6 内容定义 | 不可变有效定义、指纹与实例缓存隔离；合法变体普通循环、正常/pending/分叉恢复和不符拒绝不改源档均通过。 |
| C7 实际生活 | 同一三模型历史累计 72h，264 次真实 Completed、三人存活且守恒；两次独立自由会话完成，其中一次 Human Console＋两真实模型。均明确 Agent 非真人。 |
| C8 交接 | 使用说明、历史产品状态、实现边界与逐项验收索引完成；受影响回归通过，无已知严重阻断。 |

工程证据：[连续世界](../../experiments/living-world-foundation-acceptance.md)、[经济](../../experiments/living-world-economy-acceptance.md)、[交易](../../experiments/living-world-trades-acceptance.md)、[修订 22 内容与完整回归](../../experiments/living-world-content-acceptance.md)、[Gym 实际验收](../../experiments/living-world-gym-acceptance.md)。

运行与体验：[三日同历史](../../experiments/living-world-llm-playtest.md)、[单人自由游玩](../../experiments/living-world-freeplay-console.md)、[混合自由游玩](../../experiments/living-world-freeplay-mixed.md)。所有实验进程均已停止；恢复时应先核对存档和启动参数，不重复创建或覆盖验收现场。

## 后续边界

不自动进入新功能迭代。若用户希望继续，优先讨论 Human Console 重复帮助的阅读负担；场景交互、长期记忆和社会动机另行选择目标。两场自由游玩的体验线索不包装成已确认严重缺陷，也不据此无限延长本阶段。旧生存原型的人类合作验收独立保留；本阶段没有必须由用户现场试玩才能关闭的隐藏依赖。
