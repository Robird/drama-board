# 001 · EventHistory API 首轮使用反馈

> 提出方：DramaBoard；核对日期：2026-09-11；上游提交：DurableGraph `1cace42`。
> 状态：上游已形成 DB-065 Proposed 方案；消费者评审认可本片覆盖范围，待实施验收。DramaBoard 尚未接入，本文不把评审观察写成真实游戏接入中已发生的故障。

## 1. 结论与证据范围

当前 EventHistory 外观适合开始真实接入。`CreateBranch` 交付已保存的 S0，`Resume` 处理 E/S 两种 head，`CommitDomainEvent` / `CommitDomainState` 接受领域对象，`ReadEvent` / `ReadState` 独立读取，应用不需要维护 ObjectId 或两份发布 head。这些设计应保留；同类型 State 根替换尤其适合 DramaBoard 的 scratch-fold 更新方式。

首轮反馈来自公开 README、API 源码及实际运行的上游测试/真实包实验；完整验证范围与本地依赖条件见[消费者合同 §6](../../research/event-journal-state-store-draft.md#6-接入边界与最小验证)。下列问题的原始接口核对基线仍为 `1cace42`。

上游回应为 [DB-065](../../../../durable-graph/docs/design-branches/0065-event-history-consumer-contract-slice.md)（文档提交 `613f759`，实现基线 `297619b`，Proposed）。消费者设计评审结论：A/B/C 和 E 的文档部分得到充分覆盖，未发现需要退回修改的阻断问题；D 与 E 的局部浏览 API 延期符合原反馈的候选定位。这里认可的是方案与范围，不是实现已经完成；此次仅审查文档，没有重跑测试。

| 条目 | 证据性质 | 当前处理 |
|---|---|---|
| 001-A · 起步示例使用默认保存策略 | 已确认 API 已提供，README 未展示最简调用 | DB-065 §4.1 / A1 纳入方案，待实施验收；补明当次覆盖不变成会话默认值。 |
| 001-B · Commit 的调用处文档与恢复示例 | 已确认合同分散，方法 XML doc 不完整 | DB-065 §4.2–4.3 / B1–B4 纳入方案，待实施验收；包含实际 XML 包产物。 |
| 001-C · 展示含引用的事件快照建模 | 已确认起步示例未覆盖，误用风险待消费者验证 | DB-065 §4.4 / C1–C2 纳入方案，待实施验收；应用隔离快照，原地处理也须热/冷等价。 |
| 001-D · 跨重开的历史记录定位 | 已确认无公开定位入口，具体使用工作量待验证 | 延期；上游路线图保留书签/RL 样本/叙事引用的重开条件。 |
| 001-E · 最近事件与局部倒序浏览 | 已确认整链物化，长轨迹成本未测量 | 文档纳入 DB-065 / E1，待实施验收；局部 API 与测量待真实浏览流程触发。 |

DB-065 的两个补充值得保留：调用处 XML 必须进入实际 nupkg 和恢复目录；恢复入口只完成已有 PendingEvent，S-head 不自动生成新 E。S-head 本身不能证明某个外部请求已完成，外部请求重投仍归宿主协议。快照示例用独立 `string[]` 保存来源 List 的观察值，避免为演示正确性复制整个 State；可变元素的进一步隔离由文档明确。

一项非阻断的验收细化建议：C1 将来源集合增删/替换明确安排在 CommitDomainEvent 返回后，立即检查热 PendingEvent 的快照内容，再分别核对冷 Resume 和历史 ReadEvent。这样直接见证应用快照隔离，不会只验证落盘 DTO 保持旧值；不需要改变方案或新增模型层。

D/E 的延期条件以[上游路线图 §4](../../../../durable-graph/docs/DurableGraph-research-roadmap.md#4-明确延后及重访条件)为回应依据。命名分支只有在应用保持其不移动时才能固定历史点，不将其视为不可变书签；局部枚举也不能代替严格 Open 的成本测量。实施完成后再按原条目的验收更新解决状态。

## 2. 001-A：让最短接入路径使用已有默认参数

**场景与现状。** 首次接入时只想声明模型、保存和重开。[README](../../../../durable-graph/README.md) 的最短示例先创建 `ReadAmplificationBaseBudgetParameters(3, 5)`，再传给 CreateBranch 和每次 Commit。但 [Repository](../../../../durable-graph/src/DurableGraph.StateStore/EventHistoryRepository.cs) 已在参数省略时使用同样的默认策略，两个 Commit 也已有可选参数。

**影响。** 示例把一个本可稍后再学的存储性能概念放进了初次接入的必经路径。这里没有缺少配置 API，也不需要新增 options 层来解决。

**建议。** 最短示例省略策略参数；保留单独一段说明默认行为及按次覆盖方式。需要调优的消费者再阅读 Base/Delta 策略。对应现有调用形状如下，仅展示调用片段：

```csharp
using var session = repository.CreateBranch("main", initialState, models);
session.CommitDomainEvent(domainEvent);
session.CommitDomainState();
// 替换式 reducer 可改用 session.CommitDomainState(nextState)。
```

**验收。** 按上游 README 的真实 PackageReference 验证路径运行省略参数的完整示例，行为保持一致；文档不把当前默认数值承诺为永久格式或业务语义。

## 3. 001-B：在 Commit 调用处交代成功、失败与恢复

**场景与现状。** 宿主需要围绕 E/S 写一个可恢复循环。[Session](../../../../durable-graph/src/DurableGraph.StateStore/EventHistorySession.cs) 的类型 remarks 已说明快照与 Dispose 边界，但 CommitDomainEvent、两个 CommitDomainState 重载缺少各自的 summary/remarks。完整失败合同分布在 README、[GraphCommitException](../../../../durable-graph/src/DurableGraph.StateStore/GraphCommitException.cs) 和测试中。仅在编辑器查看方法签名，难以确定失败后应保留哪些对象、何时重开。

**建议。** 补充方法 XML doc，并提供一个最短恢复示例，明确以下可观察行为：

- E 发布只表示存在待处理事件。S 仅在 ref 发布后尝试安装 State 候选；正常返回时 `session.State` 已切换并清空 PendingEvent。若发布后安装或交付失败，Outcome 可以是 Published，旧 session 不保证反映已发布 S，须重开判定。
- `Outcome` 表达持久发布结果，`IsFaulted` 表达当前实例还能否使用；`NotPublished` 不等于“实例一定可复用”。这两个维度有用，应保留。
- Unknown/Published 不透明重试；faulted 时关闭并重开，按所选 branch 的持久 head / Resume 判断。E 已发布则交付待处理 E；S 已发布则交付完成结果，不能再应用同一 E。
- 保存异常不撤销应用已经作出的内存修改；发生在写入前的参数、捕获或业务回调异常也不保证全部包装成 GraphCommitException。

**验收。** 示例分别对应“E 已发布、S 未完成”和“S 已发布、调用方未收到成功”的已有回归，并展示重开后重新取得对象；不对已修改的旧 State 盲目重复处理。证据见 [EventHistoryRepositoryTests](../../../../durable-graph/tests/DurableGraph.StateStore.Tests/EventHistoryRepositoryTests.cs) 的 `KnownPrepublicationFailureDoesNotInstallCandidateAndColdResumeSeesPendingEvent`、`PublishedStateFailureFaultsWriterAndReopenMustNotReplayPendingEvent`，以及[真实 ref 故障测试](../../../../durable-graph/tests/DurableGraph.StateStore.Tests/EventHistoryPublicationFailureTests.cs)。

这项建议主要补调用处可发现性和用法示例，不要求增加自动 retry、回滚或透明业务执行框架。

## 4. 001-C：增加一个真正需要快照语义的起步示例

**场景与现状。** DramaBoard 的事件可能记录角色当时的观察，而世界里的角色随后继续变化。README 的 `DamageEvent` 只有 Amount，能说明流程，却不能示范最容易误用的引用边界。文字和 Session remarks 已明确持久 DTO 冻结不会冻结 CLR 对象；[包消费者](../../../../durable-graph/experiments/PackageConsumerProbe/EventHistoryConsumer/README.md) 也已有更完整的历史与引用见证。

**建议。** 在最短示例之后增加一个小的 `ActorSnapshot` 场景：事件保存业务身份、当时 HP 与少量观察数据；处理器通过业务身份找到当前实体。示范以下结果：

```text
E1.TargetSnapshot.Hp == 10
S1.Alice.Hp == 7
```

示例应覆盖一个集合或引用元素，说明 readonly 字段/外壳并不隔离可变成员；展示如何用受支持的领域类型保存所需快照并保持只读，以及避免回指整个 World。采用不可变替换式领域对象时，可以保留旧对象作为快照，无需强制维护另一套 Saved/DTO 类型。

**验收。** 示例自身隔离随后会被写入的对象，使热处理不改变 PendingEvent 的快照内容，并与冷 Resume 得到相同业务结果；后续提交后重新读取 E 仍得到记录时的值，独立浏览不需要加载无关世界。这里验收的是示例的快照建模，不能依赖框架自动拆开热路径的可变别名；尚无证据要求通用 Snapshot/Clone API 或自动冻结机制。

## 5. 001-D：历史记录需要怎样的跨重开定位方式

**候选场景。** 保存一条事件的调试书签，或在 RL 样本/叙事引用中指向某次记录；下次打开仓库后回到同一事件。它不是“恢复当前 branch head”，因为 branch 可能继续前进或被 Move。

**已确认的边界。** [GraphFrame](../../../../durable-graph/src/DurableGraph.StateStore/GraphFrame.cs) 只在签发它的那次打开中有效，Journal `Address` / `Parent` 为 internal；公开的 RevisionAddress 被注明为 diagnostic，RootId 也不是持久 frame locator。[Repository](../../../../durable-graph/src/DurableGraph.StateStore/EventHistoryRepository.cs) 没有按持久定位值重新取得受检 handle 的公开入口。现有办法是保留命名分支来固定历史点，或重新枚举并用领域自有标识寻找；它们可以满足部分场景，但不等同于事件引用。

**建议讨论。** 首先明确长期定位历史记录的推荐方式。如果真实接入确实需要书签，再提供最小的“取得可保存定位值 → 重开后解析为本次有效 handle”入口。保留当前 handle 的来源校验；明确定位值的仓库作用域、记录可读条件及无法解析时的错误。不要求现在决定 locator 类型、编码或全局仓库身份设计，也不建议直接把 diagnostic RevisionAddress 当作受支持书签。

**后续验收。** 记录 E1 的定位值，继续追加历史、关闭重开、移动原分支后仍能按约定定位 E1；错误仓库/无效定位值遵循明确合同。若定位方式依赖命名 ref，应写明这种依赖。DramaBoard 还没有落地该使用场景，当前不列为首个保存/续局切片的阻碍。

## 6. 001-E：只取最近 N 条事件，不应被示例掩盖整链成本

**场景与现状。** 事件回顾通常先显示最近一段，RL 轨迹检查也可能只取某个区间。[ReadEvents](../../../../durable-graph/src/DurableGraph.StateStore/EventHistoryRepository.cs) 当前先由 ReadFrames 构造正序整链，再筛选成数组；没有方向、起点、数量或分页参数。调用 `ReadEvents(branch).Reverse().Take(n)` 可以得到正确顺序，但不会使底层只枚举 n 条。

**建议。** 近期给返回值/方法 XML doc 写明顺序、整链物化和 orphan 排除规则。真实轨迹证明确需局部浏览后，再选择一个支持起点、方向和数量限制的简单入口；不要只把返回类型改为 IEnumerable 就宣称已经按需读取。

**后续验收。** 固定所浏览的历史 head，读取尾部 N 条或连续两页，顺序正确、无重复/遗漏且不加载无关 State；同时分别量化打开仓库、枚举 frame 和恢复事件对象的工作量。底层仍严格验证全历史，因此局部枚举优化本身不能证明冷打开成本下降。当前未测长轨迹，不提出未经测量的时间/内存阈值，也不建议削弱完整性校验来换取表面性能。

## 7. 随真实接入再反馈的事项

现有 record class、接口集合不能直接作为持久模型，会产生适配工作；记录实际改写量后再判断是否需要上游扩充支持范围。Snapshot 构造、跨库模型登记和 Upgrade 声明也应以真实领域模型衡量，不从小示例推断总体低工作量。

`ReadPair` 保持可选的只读快照便利入口即可；写入/续局仍使用独立 E/S。单 writer/单活动 session、fork/Move 前关闭 session 的约束与当前串行 Kernel 相容。可玩 fork 的新 lineage、完整续局状态和 E 相对 Plan/校验的位置由 DramaBoard 决定，见[消费者目标对照](../../research/event-journal-state-store-draft.md#7-dramaboard-消费者目标对照)。

后续收到上游结论时，直接在对应条目更新采纳、延期或解决状态及证据；本文件不追加逐轮往来记录。
