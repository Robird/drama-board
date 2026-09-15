# DurableGraph 消费者前置研究：后继入口

> 状态：早期方案已被后续能力与调研替代；不再作为施工计划。
> 2026-09-12 将仍有效的场景、完整状态闭包与验收要求收敛到下列文档。DramaBoard 尚未接入。

现在从[真实接入近期计划](../worksets/durablegraph-first-integration.md)继续；具体 E/S 时点、完整 State 与失败恢复见[Occurrence 持久化提案](../design/durablegraph-occurrence-persistence.md)。两者是本轮推荐，待用户采纳。

仍保留的研究成果：

- 使用真实 Alice/Bob Passage encounter，冷重开后 Continue/Reverse，不另造玩具世界。
- 保存完整 Game + Spatial + Kernel；补足 WorldSnapshot 漏掉的 NextPersistentId、KnownFacts.Text，且恢复包含因果游标与精确内容绑定。
- 模型直接服务领域，不维护 SavedActor/SavedSpatial 平行模型；真实包、跨程序集登记与 `.dgschema` 必须验证。
- 不能只换 IJournalSink：完整 State 发布、pending E 处理和完成通知必须接通。

不再有效的早期前提：旧 GraphRepository/GraphSession、缺少 branch/根替换、为固定根限制建立稳定外壳、只保存图但缺统一事件历史。DurableGraph 的 EventHistory 已替代这些入口。

首片暂保留纯 fold 是为了复用已有 scratch、失败隔离与呈现语义，不是因为库要求不可变，也不是沿用早期 A/B 方案的固定根限制。是否转向局部可变实体，以真实代码维护成本、保存成本和失败合同再评估。

本文件不继续复制任务表、模型闭包或 API 状态。原研究全文保存在 Git，可查看：

```powershell
git show 063e3e7:docs/research/durablegraph-consumer-preflight.md
```

通用消费者合同见[独立 E/S 草稿](event-journal-state-store-draft.md)；上游问题与解决状态见[反馈目录](../feedback/durablegraph/README.md)。
