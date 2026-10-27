# 局部预测方案：辩证审阅裁决

> 2026-10-27。对[局部预测重构方案](../design/local-prediction-refactor.md)执行 dialectical-simplification。三位审阅者分别承担需求质疑、最小架构、语义防守；先独立阅读同一需求账本与源码，再交叉质询。主线程核对源证据并修订文档。两轮后关键不变量与机制已收敛，无需第三轮；未修改或测试产品代码。

## 最小模型

通道是常驻的发现范围，运动变化产生临时 dirty 通道集合。整批业务事实应用后，每个 dirty 通道重新求交一次，把结果与业务变化放进同一完整 E/S 转换。到达与入口日程继续读取既有 Due，不复制预测表。恢复应用保存结果，不重新发现。

新增的持久状态仅针对接触输出及其发布记录；通道过程无需独立对象注册表。一个 Kernel `Complete` 委托覆盖所有发生源；已有完整状态校验增加 ModelTime 参数，使用既有 cursor/批次时间。

## 需求与证据边界

用户明确要求：局部预测、新组合发现、无兼容负担、为实施准备文档。当前代码提供：顺序 occurrence、E→S 完成边界、恢复不规划、逐 fact 业务校验、空间稳定仲裁与 ResourceCycle 应用优先级。空间时间语义在本片保持是设计选择，不是永久兼容要求。完整需求来源见方案第 1 节。

没有任意不可信 E 导入、多线程维护或跨提交 dirty 恢复的当前需求。资料综述与旧路线图来自同一讨论，不算多份独立支持证据。

## 裁决表

| 位置/主张 | 来源、消费者与具体失败 | 最小替代与成本 | 裁决 |
| --- | --- | --- | --- |
| §3/§7：validator 能拒绝缺失维护 | 新入场 B 与 A 相遇，但 Contacts 仍为空；结构校验无条目可否定。消费者是预测完整性验收 | 唯一 Complete 接线加独立全量 oracle；不新增批封印、持久 dirty 或每步全量求解 | **simplify**：撤销普遍运行时检漏承诺 |
| §5：Kernel hook 或组合器二选一 | [SpacetimeHostScenarioTests](../../tests/Spatial.Tests/Acceptance/SpacetimeHostScenarioTests.cs) 已有独立掉头 rule；只包 contact rule 会漏它的运动变化 | 选唯一 Kernel Complete 委托，工厂必装；其他消费者显式 identity | **simplify**：删除第二种架构备选与单方法接口包装 |
| §3：Due 不早于当前时间 | 当前 [Kernel](../../src/Kernel/Simulation/SimulationKernel.cs)、[Replay](../../src/Kernel/Simulation/SimulationReplay.cs) 只传 world 给 validator；闭包可能读到旧 cursor，回放更没有该安装状态 | 现有 validate 统一增加 ModelTime 参数；初态和每个完成边界传已知时间 | **simplify**：统一上下文传递，不增加时钟或校验阶段 |
| §2：每种过程必须另建持久对象 | arrival/schedule 的 Due 已在 [SpatialOccurrenceRule](../../src/Spatial/Simulation/SpatialOccurrenceRule.cs) 所读业务记录中；新增 registry 没有当前消费者 | 用业务身份解释 owner；仅 Contacts 新增输出存储 | **defer** 通用过程注册表；**merge** 身份与已有业务记录 |
| §3：组合根/事实包装是否多余 | [GraphSpatialReducer.Apply](../../src/Spatial/Facts/GraphSpatialReducer.cs) 每条 fact 前后校验；若业务子图同时强校验预测，Reverse 后旧 generation 会在替换前误报 | Business 与 Contacts 两子图，同根批末校验；包装只区分业务与预测结果 | **keep**：有真实中间状态边界 |
| §3/§7：把 Contacts 改成纯缓存、取消恢复片 | E-only 若没有记录预测结果，就必须重新预测或留下陈旧状态；[模型资格测试](../../tests/Spatial.Tests/Acceptance/DurableModelQualificationTests.cs) 明确不是 history adapter | 保留接触输出与整通道替换事实；测试用单根 history，暂不做通用存档产品 | **keep**：持久状态与 S2 有当前故障模型依据 |
| §6：S3 只改 Forecast 名称 | [CycleEngine](../../src/ResourceCycle/CycleEngine.cs) 维护藏在当前赢家 rule 中；只改名无法保证将来别的发生源也维护 | 真正迁到同一个 Complete 时机；保留业务事实形状、私有通知和应用优先级 | **simplify** 共用执行契约；**defer** 泛型过程/通知框架 |
| 可选空间索引、邻域证书、对级负记录 | 当前通道全配对已覆盖新关系，尚无密集通道测量 | 保留重开条件和独立参考，不进入首片 | **defer**：当前无新增机制的必要证据 |

以上裁决对当前源码证据的信心高；具体 DG 类型和字段命名、后续多人 Cycle 的消费映射仍须实施时验证。未对性能作测量推断。

## 两轮争议如何解决

第一轮三方都发现结构校验与漏项检测的冲突。语义防守给出最强恢复反例：E 有业务变化但漏了新增 BC 输出，Replay 可以得到结构合法而不完整的 S。第二轮核对真实信任模型后，撤回“必须新增批级证明”的可能要求：E 来源是受控规划完成过程，代码漏生成由独立 oracle 验收；不把任意伪造记录当作新的产品协议。

时间校验比较了两个方案：统一传时间，或在 Complete、冷开工厂、替换 fact Fold 分别检查并依靠下次仲裁兜底。后者改签名少，但一个不含替换的回放末批或 pending 仍可能完成带过期项的根，直到下一次 Step 才拒绝。统一传参覆盖同一条完整边界法则，概念更少；主线程也核实四个 Kernel 入口本就持有所需时间。Replay/Conformance 的空历史也需初态校验。

最小架构审阅保留了组合根：直接内联预测表虽然少一个容器，却会迫使原业务 reducer 区分半成品与完成状态。这里减少类型数量不等于减少实际职责。

## 可观察的收敛结果

- 完成步骤从两种候选落点收敛为一个；单方法接口改为既有风格的委托。
- 完整校验只扩展既有时间参数，不增加第二个持久时钟或多处独立时间防线。
- 到达与日程没有新增权威表；owner/输出身份复用通道 ID 与 ContactKey。
- 不增加通用过程注册、持久 dirty、漏项封印或对级负结果。
- S1/S2 构成最小空间实施闭环；S3 才完成两个真实消费者的共同执行契约，S4 细配对优化保持可选。

这些是文档与概念层面的收敛，不是已减少的代码行数或测得的性能提升。没有剩余问题需要用户选择产品语义；若未来改变 floor/同 tick 规则，另开明确设计切片。
