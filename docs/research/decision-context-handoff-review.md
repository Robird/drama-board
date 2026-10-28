# 决策上下文信息继承：方案审查裁决

日期：2026-10-28。对象为[设计与重构方案](../design/decision-context-handoff.md)。本轮只修改文档；下述源码核对支持实施方案，不代表实现、测试或 LLM 效果验收。

适用范围：本记录对应首次三路审查。之后用户新增的 ToolCallId 保存与 Completion 映射职责已写入主方案 §4.4；该增量由主线程核对 Completion 源码与协议后修订，未经过本记录所述三路审查。本记录中的“完整日志延期”不否定后来明确纳入的最小宿主操作记录。

## 审查组织与结论

主线程先提交方案初稿，再由三个独立 subagent 分别只读审查：

- `handoff_flow_review`：回合边界、零耗时命令、同刻请求与角色轮转。
- `handoff_recovery_review`：Plan/Fold/Complete、E/S、事实形状与预测失效。
- `handoff_scope_review`：复杂度、首片范围、CLI 可用性与验收成本。

主线程核对具体源码反例，统一裁决并修改方案及导航。三路均认可放弃动作后阶段后能明显缩小范围；未发现需要改通用 Kernel 或建设完整会话恢复的理由。

## 保留与删除

| 问题 | 裁决与依据 |
| --- | --- |
| 是否还需要一次 ContextOpened 提交 | 保留。在 [SimulationKernel](../../src/Kernel/Simulation/SimulationKernel.cs) 中，控制器返回后才发布 E；若要求首次展示前封存 Incoming，且查询不写状态，就需要显式领域边界。利用已有 RequestDecision 的 Plan 分支，不增预测 owner、队列或仲裁等级。 |
| 是否保存完整回合或近期事件列表 | 删除初稿的最近 16 条事件和省略计数，只保留有限端点、返回原因及固定白名单汇总。流程审查提出“保留列表、删除累计”，范围审查提出相反简化；主线程选固定汇总，优先解释采集所得和天气损失，暂不承诺事件先后序列。以后按实际误判证据重开历史窗口。 |
| 是否保存 DraftHandoff | 保留。角色准备传递的信息不藏在 Console 实例里，编辑可独立提交；未续写的旧交接不会永久自动继承。长期笔记与端点会话恢复继续延期。 |
| 是否逐一为每个叙述窗口增加磁盘夹具 | 不机械扩展。真实磁盘聚焦新增的编辑、放行动作、交付与终局封存；分类、上限及可见性用内存验证，沿用既有取消/输入未提交合同。 |

## 已收敛的实施缺口

1. **途中交接合法性与启用范围。** [CycleActions.Check](../../src/ResourceCycle/CycleActions.cs) 当前只允许途中执行 Continue/Use。方案补充：已获决策机会且启用连续性的角色可编辑交接；未启用和终局在领域层拒绝，而非只隐藏 UI。
2. **生存预测的隐含依赖。** [CyclePredictionMaintenance](../../src/ResourceCycle/CyclePredictionMaintenance.cs) 的生存预测 key 包含 DecisionGeneration，失效判断却只看 Survival 引用。新控制事实保留身体引用时暴露这一缺口；方案要求同时比较代数并补 attention 绑定，不靠复制身体对象维持偶然正确。
3. **事实形状不能任意放宽。** [CycleFactApplication](../../src/ResourceCycle/CycleFactApplication.cs) 限制再生事件无角色替换、多种事件只替换一个角色。首片摘要不收集资源再生过程，只附加修改原事实已合法替换的角色；未来旁观者摘要须重新裁决。
4. **活动停止原因的类型化来源。** [CyclePlanning.CompleteGather](../../src/ResourceCycle/CyclePlanning.cs) 将多种停止细因写入 Result 文本。方案改为在原规划分支直接生成有限原因值写入摘要，不解析文案、不建立通用原因框架。
5. **仅控制状态变化的副作用边界。** 交接编辑/ContextOpened 绕开 [CycleSurvivalRules.Enrich](../../src/ResourceCycle/CycleSurvivalRules.cs) 与地点 Observe；不确认警告、重锚身体或刷新地图记忆。服务游标、请求代数与允许替换的字段分别写明。
6. **Pending 与 NeedsDecision 可同时存在。** 警告已提交、交付尚未提交是合法中间状态。约束改为“实际交给控制器时 Pending 为空”，不误写成所有 NeedsDecision 状态都必须为空。

## 实施前后的证据边界

修订后由持久恢复审查者复核，原问题已收敛，未发现新的阻断项；补充澄清了采集中间产出仍可能因资源耗尽通知而唤醒，未另建通知规则。

本轮源码核对覆盖控制器返回、规划补全、事实校验、请求与生存预测、单根 E/S 恢复；没有运行代码测试，也没有实际清屏或 LLM 试玩。主线程检查了集成 diff、相关文档本地链接与空白格式；后续实施仍须完成主方案的纵向恢复用例、原玩法回归和独立 Console 试玩。

主方案中的 2,000 Unicode 标量值、有限汇总字段及切片划分是本轮建议的实施默认值，不是用户逐项确认的产品承诺；可随实施证据调整，但应维护“有界、角色可见、主客观分离、未承诺恢复完整会话”的合同。
