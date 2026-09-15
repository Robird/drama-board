# 001 · EventHistory API 首轮使用反馈

> 原反馈基线：2026-09-11 / DurableGraph `1cace42`。
> 当前状态：2026-09-12 静态复核至 `f68388f`；DB-065 已在 `79be2c2` 落地，A/B/C 及 E 的文档反馈已处理。DramaBoard 实际游戏消费仍待接入验证，本轮未重跑测试或包实验。

## 已处理的反馈

| 条目 | 上游结果与入口 |
|---|---|
| 001-A · 默认参数起步 | [README](../../../../durable-graph/README.md) 的最短调用省略保存策略，并解释每次显式参数只影响当次调用。 |
| 001-B · Commit XML 与恢复 | [EventHistorySession](../../../../durable-graph/src/DurableGraph.StateStore/EventHistorySession.cs) 的方法合同、实际 XML 包配置和[恢复消费者](../../../../durable-graph/experiments/PackageConsumerProbe/EventHistoryRecoveryConsumer/README.md) 已落地。恢复只完成 pending E；S-head 不自动生成事件，不把没有 pending 当成外部请求已完成。 |
| 001-C · 含引用的快照 | 同一恢复消费者展示独立集合快照、热/冷与历史观察；只读字段不冻结可变数组/元素，原地处理不得改旧事件。 |
| 001-E · 枚举成本说明 | README 已说明 ReadFrames/ReadEvents 正序整链物化，Reverse/Take 不减少读取，严格 Open 的历史检查另计。 |

上游实现与其测试/真实包验收记录集中在 [DB-065](../../../../durable-graph/docs/design-branches/0065-event-history-consumer-contract-slice.md)。本仓库之前实际运行的 `1cace42` 验证范围在[FirstBoard 归档](../../archive/firstboard-llm.md)；不能将其算作新版复跑证据。

## 仍待真实使用触发

| 条目 | 当前限制、触发与最小反馈材料 |
|---|---|
| 001-D · 跨重开定位 | GraphFrame handle 只属于签发它的这次 repository 打开，诊断地址不等于可重开解析的公共书签。出现 RL 样本、叙事引用或历史书签消费者时，再提供必须跨重开保持的引用及最小定位操作；不预建通用地址协议。 |
| 001-E · 局部/倒序浏览 API | 需要最近 N 条、范围或分页且整链物化成为实际问题时，分别测量 Open、枚举和 typed 恢复成本，再反馈读取方向/范围要求。 |

命名分支只有在应用保持其不移动时才能固定历史点，不当成不可变书签。D/E 均不阻塞活跃核心，也没有因文档改进而变成已实现 API。

原始场景与提案保存在 Git：`063e3e7:docs/feedback/durablegraph/001-eventhistory-api.md`。后续同类实际摩擦继续在本文件更新，不追加逐轮日志。
