# 辩证简化记录

日期：2026-11-03。对象为本目录初稿的 README、design、implementation-plan 三篇全文；范围仅为设计修订，未运行游戏、构建或真实模型调用。

方法：使用 [dialectical-simplification](W:/CodexHome/skills/dialectical-simplification/SKILL.md)。三位 reviewer 独立只读检查同一初稿、需求账本和当前源码，分别承担 Demand skeptic、Minimal architect、Semantic defender；主代理独立核对源码和测试。第二轮交换最强反例，要求明确保留或修订。没有以投票取代证据；两轮后合同和最小机制收敛，无需第三轮。

## 1. 最终最小模型

```text
唯一通道定义（两端各有独立入口 ID/本地文案）
 + 每角色 Visits 与已确认连接
 → 已知邻接视图 → A*（h=0）→ 不可变路径值
                              ├─ 当前候选槽
                              └─ 活动导航：选中的路径值 + 进度
 → 每段仍使用普通移动的校验、风险预览、出发与到达
```

地点/入口/连接是三种知识语义，不等于三份持久集合；已见入口从静态定义与 Visits 派生。候选和活动导航有两种真实用途，但共享一种路径表示。Spatial 的位置和期限仍是唯一运动权威，导航没有自己的计时器、线程或并行阶段枚举。

## 2. 独立主张与交叉质询

| 议题 | 独立主张与最强反例 | 交叉质询后的结论 |
| --- | --- | --- |
| 分支路径 ID | Demand skeptic/Minimal architect 指出初稿“不接受外部分支句柄”与 Gym 原状态继承冲突；Semantic defender 构造同角色/同操作序号在不同分支产生同文本 ID 的例子。 | 删除来源鉴别承诺，只查当前根本角色候选；共同祖先候选在分叉后有效。同名字符串按当前根解析，不加 BranchId 或重签；三者接受。 |
| 入口拓扑 | Minimal architect 指出当前 GetPassages 已从唯一 Passage 定义派生方向；额外入口拓扑表会与 From/To 分歧。 | 双端元数据附唯一通道，派生本端视图；Semantic defender 补充反向投影应交换端点数据，保持两端身份独立。 |
| 单向规则 | 两位 reviewer 指出当前 BuildGraph 固定双向，要求生产单向道路会扩张配置和 schema。 | Semantic defender 将方向约束收窄到算法边界：按有向邻接搜索，纯夹具覆盖单向；当前内容仍双向，不新增权限玩法。 |
| 途中命令 | Minimal architect 指出“途中可查询/取消”容易被误读成随时输入。 | 仅在现有合法唤醒窗口执行；没有窗口就走到下一事件/地点。无需后台 Console reader、输入仲裁或新唤醒源。 |
| 候选持久化 | Demand skeptic 保留 `path → 退出 → 冷开 → navigate` 的真实消费者；单纯宿主缓存会失效。 | 保留一份领域候选；与活动导航共用不可变路径值，导航不能回查可变候选槽。无需 NavigationId、深复制协议或 registry。 |
| 内容泄漏 | Demand skeptic/semantic defender 分别找到地点描述明示邻接的现成例子；仅改入口 ID 不够。 | 审计并定向改写地点/入口/背景等系统文案；结构化连接显示目的地。允许猜测，不建设自然语言脱敏框架。 |
| 风险预览 | Semantic defender 指出 ActionWarning 只识别 Go；同一条危险路改用 navigate/continue 会漏提示。 | 三种出发入口共享本次单段的解析、校验、风险预览和执行，不新建整路生存预测。另两位确认这属于保留当前行为。 |
| 取消语义 | Minimal architect 在第二轮发现 ActivityCancelled 自动确认身体与通信提醒；Semantic defender 验证其可能令途中 Open Context 与待决策判据不一致。 | 导航取消需可区分的事实效果，保留提醒、位置、期限；随后普通 continue 确认并放行。不新增 Context 状态或用户命令。 |
| 恢复验证 | Semantic defender 指出“不重新寻路/发现”不能被简化成任意字段替换；队伍逐人到达不能提前给未到者知识。 | 保留既有事实边界的局部因果校验；只本人匹配段到达才授知/推进。验证记录路径连通/知识/成本，不重新求最短路。 |

初轮的两项表达已撤回：跨分支字符串来源鉴别，以及容易被理解为首版要实现生产单向配置的表述。第二轮确认取消语义无需额外状态，但必须保留与 ActivityCancelled 的效果区别。剩余主张经反例收窄，未发现需用户额外裁决的产品分歧。

## 3. 裁决表与证据

| 裁决 | 对象及成本 | 保留或删除的理由与源码依据 |
| --- | --- | --- |
| delete | 外部分支句柄来源鉴别承诺 | 无跨分支角色操作消费者；[Fork](../../../src/Playground/Persistence/PlaygroundBranchBoundary.cs)直接继承 Business。增加 BranchId 会破坏已交付候选的连续性。 |
| merge | 入口数据与唯一通道定义 | [GetPassages/BuildGraph](../../../src/Playground/PlaygroundWorldDefinition.cs)已拥有唯一拓扑；双端元数据满足匿名身份，不需要第二拓扑权威。 |
| merge | 候选/执行的路径表示；三种出发入口的预览/执行 | 两用途保留一个不可变值，活动计划不随候选替换；[ActionWarning](../../../src/Playground/PlaygroundSurvivalRules.cs)只认 Go 是必须补齐的行为接缝。 |
| simplify | 认知存储、阶段与命令窗口 | [Visits](../../../src/Playground/PlaygroundExpedition.cs)保留地点权威；已见入口派生，增加确认连接；[NeedsDecision](../../../src/Playground/PlaygroundActions.cs)继续控制调用窗口，无新异步输入系统。 |
| keep | 候选持久化、到达因果、逐站确认 | 冷开丢路径或提前授予队员知识均违反实际需求；沿用 [FactApplication](../../../src/Playground/PlaygroundFactApplication.cs) 与 [FollowingRecoveryTests](../../../tests/Playground.Tests/FollowingRecoveryTests.cs) 的结果重放边界。 |
| keep | 内容修订及导航取消的独立效果 | [地图文案](../../../src/Playground/PlaygroundExpeditionMap.cs)直接泄漏邻接；SurvivalRules.Enrich 与 FactApplication.ValidateCommunicationState 对 ActivityCancelled 自动确认提醒，不能照搬。 |
| defer | 生产单向权限、路牌/分享、动态拓扑、路径库、全程自动旅行、通用模块 | 无当前内容消费者；重开条件集中在 [设计 §8](design.md)。纯搜索有向性、静态隐藏边和当前单段出发校验仍保留。 |

这里的减少主要是去除过强承诺和消除实现歧义，并非删除已存在的生产代码。可观察的目标规模是：三个新增普通命令 `map/path/navigate`；`go` 改用入口参数，`continue/cancel` 扩展；一份候选、一份活动导航共用一种路径值，不新增挂起/恢复命令、导航身份、计时器或分支身份协议。

## 4. 与当前代码不一致的假设

1. 当前 Observation/JourneyObservation 按目的地访问记录披露终点，不等于已确认连接；[FollowingObservationTests](../../../tests/Playground.Tests/FollowingObservationTests.cs)还明确断言旧行为。此次有意改变该语义，必须同步修改夹具。
2. 当前入口 ID 是 PassageId，默认地点/入口文案还直接写对端，不能靠单个 renderer 修复。
3. 当前 continue 主要确认提醒、cancel 只取消 Activity；新增导航必须同时调整合法性、放行、PendingAdvance 与事实校验。
4. Spatial 支持入口权限，不代表 Playground 已支持生产单向道路配置；后者当前固定双向且不允许计划中的入口变化。
5. 角色知识、候选路径与导航必须可恢复；宿主日志/历史导出不是这三者的领域存储替代品。

最小安全实施起点为 [切片 A](implementation-plan.md)：唯一内容定义附双端身份、当前 go 全链路替换、本人的完整到达授予连接、冻结观察/两控制器/恢复一并闭合。后续按 B–D 逐步增加求路、导航和组合验收，不先抽象框架。
