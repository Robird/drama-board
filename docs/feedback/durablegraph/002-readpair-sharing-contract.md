# 002 · ReadPair 共享读取的消费者边界

> 核对日期：2026-09-11；GraphReader 共享实现来自 `1f5c9d0`，非泛型公开重载来自 `297619b`。
> 审查时上游 HEAD 为 `613f759`，README/公开方法文档另有 DB-065 未提交施工；本文没有修改上游文件。
> 状态：已整理，待上游评估。本轮为源码、已有测试及文档的静态审查，没有运行新复现或重跑测试。

## 1. 适用结论

当前非泛型 `ReadPair(first, second, models)` 与泛型校验重载适合消费者使用：结果按输入顺序对应，允许任意 E/S 组合；已知类型可直接校验，未知实际类型可模式匹配。保留独立 ReadEvent/ReadState 和可写 Resume，无需为 DramaBoard 增加另一套 pair 类型或强制联合读取。

未发现共享算法的错版本/错引用问题。依据是 [GraphReader](../../../../durable-graph/src/DurableGraph.StateStore/GraphReader.cs) 的 FindSharedClosure 与两表分配/填充，以及 [RevisionReadSession](../../../../durable-graph/src/DurableGraph.StateStore/RevisionReadSession.cs) 的缓存范围：

- 同 ObjectId 还必须同实际 head、同 current model/layout 与完整当前状态；不是按数值 ID 或内容相等跨版本合并。
- owner 自身没变但 child 变化时，不能共享会沿反向引用传播到祖先和环。稳定闭环可整体共享；共享节点的所有持久引用目标都共享。
- 两个完整实例表在 Hydrate 前建立，共享节点只填充一次；非共享节点仍按各自 Revision 的引用环境恢复。
- 缓存命中不免除每份 Revision 的引用验证；Normalize/Upgrade 分别执行，可写 Resume 分别分配可变对象。

证据入口：[SharedGraphReaderTests](../../../../durable-graph/tests/DurableGraph.StateStore.Tests/SharedGraphReaderTests.cs)、[RevisionReadSessionTests](../../../../durable-graph/tests/DurableGraph.StateStore.Tests/RevisionReadSessionTests.cs)、[SharedEventHistoryTests](../../../../durable-graph/tests/DurableGraph.StateStore.Tests/SharedEventHistoryTests.cs)。上游已运行的证据由 [DB-064](../../../../durable-graph/docs/design-branches/0064-shared-revision-decoding-design.md) 维护；本文不将静态复核写成新的测试通过结论。

## 2. 002-A：共享判定给只读操作增加了编码回调

**已确认的事实。** GraphReader.HasSameCurrentState 对共享候选调用两份 current preparation 的 Validate/PrepareBase，再比较完整 Base body。普通单图 Read 路径不会调用 PrepareBase；其 VisitReferences/Hydrate 中已有的 preparation.Validate 也不能代替 Base 编码成功。该成本是 DB-064 明确选择的实现方案，当前正文比较也确有必要：同 head、同 layout 不保证手工 Normalize 没有改值。

**待明确的消费者合同。** [CapturedStatePreparation](../../../../durable-graph/src/DurableGraph/CapturedStatePreparation.cs) 把 Base 编码委托与 [StateModelBinding](../../../../durable-graph/src/DurableGraph/StateModelBinding.cs) 的读取/归一化/恢复能力组合登记。编码异常会从 ReadPair 传播；公开 README/ReadPair 文档虽说明回调副作用不回滚，但未明确只读共享会调用编码器。

以下是源码路径可推导的最小复现方案，尚未执行，不能称为已发生的下游故障：

1. 用正常模型保存一份 State，关闭 writer。
2. 新读取目录保留正常 reader、Normalize、Allocate、Hydrate，但同 current DTO 的 Base-preparer 明确抛异常。
3. 单独 ReadState 可以走完恢复路径；对同一 frame 调 ReadPair 则进入共享候选比较并触发该编码异常。

这种模型是否属于上游支持的“可读但当前不可写”能力范围，需要上游明确；不能仅凭人为抛错就判定产品缺陷。不过两条读取路径依赖的回调集合不同，是当前实现事实。

**建议。** 先补充该差异的回归和公开合同。消费者更希望共享作为优化不额外要求写入可用性；若采用这一合同，可在内部接入完整 current StateEquals，或在明确缺少安全比较能力时保守不共享。具体实现由上游选择，不要求新公开 comparer/策略接口，也不建议用 blanket catch 吞掉 Schema、Upgrade、I/O 或任意模型错误。

**验收。** 明确哪些模型被支持，并分别验证普通读取、paired 读取及失败行为；无比较能力时若选择不共享，仍须各自正确恢复。若继续要求 Base-preparer 可用，则公开说明这一前提与异常传播，而不是仅称为“解码复用”。完整比较不能退化为只看 ObjectId/head/layout 或不完整字段。

## 3. 002-B：Transient 重建也可能改写共享实例

**使用场景。** README 要求恢复后由应用重建 Transient。历史查看器可能对两份 World 分别调用 RebuildTransient；如果一个共享 Actor 的 Transient 字段保存所属 World、查询上下文或视图专属缓存，第二次重建就会覆盖第一份图也能看到的字段。即使持久字段完全不变，也存在这种可见写入。

这是需要澄清的用法边界，不是现有只读合同下的共享算法错误。FindSharedClosure 证明的是持久引用闭包可共享，不证明调用方之后建立的 Transient 反向引用也可跨视图共用。

**建议。** 在 ReadPair remarks 与 Transient 说明之间加明确连接：

- 不应分别向可能共享的节点写入两份世界各自的 owner/context/cache；“只读”也涵盖会改变观察结果的 Transient 写入。
- 视图专属索引/缓存放在各视图外部；不要把 ReadPair 返回的普通 CLR 对象当作可独立编辑的两份恢复图。需要修改世界和续写时，通过 Resume 取得工作区。
- 若文档允许在只读材料上做派生缓存初始化，应限定为与视图无关、只由共享持久子图决定且可重复的初始化，并说明并发使用另受同步约束；不要笼统授予所有 RebuildTransient 都安全的承诺。

**验收建议。** 用两个不同 World 引用同版本 Actor 的小例子说明：共享节点不保存 per-view owner；外部视图索引保持分别关联正确世界。消费者测试检查各视图查询结果，不把特定跨图 ReferenceEquals 写成 API 保证。

当前没有证据要求增加自动 Transient hook、通用 Clone 或共享开关。先明确用法，真实 DramaBoard 模型若仍难以使用，再提供最小复现讨论能力扩展。

## 4. 性能与后续优先级

ReadPair 可以减少 exact 解码与最终保留的实例，但当前还会分别 Normalize，并为候选编码两份临时 Base body。DB-064 已把这些成本与闭包图算法的 O(V+E) 分开，也记录了样例中 paired 读取更慢的情况；不必为这项已知取舍再建一轮泛化优化工程。

建议公开使用说明链接该成本边界，实际接入时分别记录延迟、托管分配和保留实例。领域事件已建成独立小快照时，与 State 的实际持久闭包重叠未必很大，收益应按真实模型测量。

优先级：002-A 先厘清 API 依赖合同；002-B 可先补使用文档。两项均未在 DramaBoard 真实接入中复现，目前不作为首个保存/恢复切片的阻碍；正确性上的共享闭包判据和冷 Resume 隔离应保持。
