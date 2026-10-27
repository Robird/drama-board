# 002 · ReadPair 共享读取的消费者边界

> 原审查：2026-09-11，共享实现 `1f5c9d0` / 重载 `297619b`，当时未运行新复现。
> 当前状态：2026-09-12 静态复核 DurableGraph `f68388f`；两项反馈均已落地，本轮未重跑测试。DramaBoard 尚未使用 ReadPair 验证真实模型。

> 历史适用范围（2026-10-27）：下文上游链接固定到 `f68388f`，由本地 Git 对象确认。上游 DB-098 已退役 `ReadPair` 及专用跨图共享规划；本文解释当时的共享约束，不要求当前独立读取保留这些 API 或别名语义。当前升级另见[包来源说明](../../worksets/durablegraph-package-source.md)，不迁移归档 FirstBoard adapter，也不以历史证据宣称新版恢复已验收。

## 处理结果

| 条目 | 已实施结果 |
|---|---|
| 002-A · 共享判定依赖 Base 编码 | [GraphReader.HasSameCurrentState](https://github.com/Atelia-org/durable-graph/blob/f68388f88ba09354e9fa90420dc2cf22b146b6cf/src/DurableGraph.StateStore/GraphReader.cs) 改用可选的 ProvesSameState，不再为共享候选准备 Base/Delta payload；无法证明相等则不共享。普通读取可能仍为 Dictionary key 校验做规范编码，这与已移除的 payload 编码依赖不同。 |
| 002-B · Transient 的共享可见写入 | [README](https://github.com/Atelia-org/durable-graph/blob/f68388f88ba09354e9fa90420dc2cf22b146b6cf/README.md) 与 [ReadPair XML](https://github.com/Atelia-org/durable-graph/blob/f68388f88ba09354e9fa90420dc2cf22b146b6cf/src/DurableGraph.StateStore/EventHistoryRepository.cs) 已明确：视图上下文放在各自图外；两份独立 ReadState/ReadEvent 可以各自初始化 Transient，持久成员仍按快照使用；需要修改并保存时使用 Resume。 |

实现与上游验收见 [DB-066](https://github.com/Atelia-org/durable-graph/blob/f68388f88ba09354e9fa90420dc2cf22b146b6cf/docs/design-branches/0066-readpair-comparison-and-transient-contract-slice.md)。最初的人工抛错 preparer 只是未执行的静态复现建议，不改写成 DramaBoard 已发生的故障。

## 当时的消费者约束

- ReadPair 保留输入顺序，允许任意 E/S 组合；同 ObjectId 不足以证明同版本，同 head 也不足以证明当前完整引用闭包可共享。
- 不依赖跨图 ReferenceEquals 判断业务身份/版本；不把共享结果当成独立可编辑世界。
- 每个视图持有自己的上下文/索引，不能用全局 Actor→context 表代替它。
- 独立小事件未必与世界有很多可共享内容。按实际模型测量读取延迟、分配和保留实例，不从共享机制推导一般性能收益。

ReadPair 不是活跃核心的前置依赖；独立 E/S 与 Resume 的 FirstBoard 消费验证已归档。原始算法审查和建议保存在 Git：`063e3e7:docs/feedback/durablegraph/002-readpair-sharing-contract.md`。
