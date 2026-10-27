# ResourceCycle 原型与试运行

> 本文保留首片实际验收。预测/绑定持久化与恢复零重算的结论属于旧实现，当前已改为运行时缓存，见[后续验收](resource-cycle-runtime-cache-acceptance.md)。本页测试数量与计量不代表当前构建。

日期：2026-10-27。范围为应用层预测失效实验，无角色、物品、空间重构或模型 API 调用。设计见[资源周期](../design/resource-cycle-demo.md)，本轮规则见[施工契约](../implementation/resource-cycle-prototype.md)。

## 交付与关键裁决

- [ResourceCycle](../../src/ResourceCycle/CycleEngine.cs)：昼夜、植物、脚本销毁；声明事件依赖，用事件类型/目标索引粗筛、状态变化细筛、dirty 去重后重算。成熟植物可收到光照通知但跳过重算；已消费预测的 owner 必须重算以撤销旧结果。
- [模型](../../src/ResourceCycle/CycleModels.cs)：持久预测与订阅、数据化行为种类/目标/代次；暂停预测者没有到期结果但仍保持订阅。未变子对象共享，不逐 tick 递减、不深拷贝世界。
- [持久化](../../src/ResourceCycle/Persistence/CycleDurableHistory.cs)：单根包含完整世界与 KernelCursor，E 保存具体变化，S 替换完整根；pending 恢复不广播、不重新预测。只支持一个根分支，拒绝 S/E/E 尾部和错误游标；提交异常后须丢弃当前会话重开。
- [Console](../../src/ResourceCycle.Cli/Program.cs)：事件流与状态诊断、进程内计数、保存/继续、步数/时间上界、文件快照、全量重算对照。
- 同刻采用成熟→光照→销毁；昼夜暂停保留进度，销毁清零重长。这些是本实例选用的业务语义，不是新引擎全局协议。
- Kernel、Spatial、Host 和上游 DG/Storage 代码均未修改。新模型按当前 DG 规范使用显式存储字段与持久枚举声明；生成并验收应用的 12 份 schema history。

## 构建与自动验证

Release solution 构建使用 `-m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify`，零警告/错误。完整 solution 测试 198 项通过、零跳过：原核心 176 项，新实例 22 项。日志为 `artifacts/resource-cycle-build.log`、`artifacts/resource-cycle-tests.log`，TRX 在 `artifacts/resource-cycle-tests/`。

[业务测试](../../tests/ResourceCycle.Tests/CycleBehaviorTests.cs) 独立固定以下预期，而非仅比较同一实现：

- 无脚本从 16:00 开始，月光草当日 22:00 成熟，太阳花经过暂停后次日 07:00 成熟。
- 17:00 销毁未成熟太阳花后，次日 08:00 成熟，旧 19:00/20:00 预测不会执行。
- 15:00 开始、18:00 安排销毁时，同刻依次成熟、日落、销毁；下一次成熟为次日 09:00。
- 针对太阳花的销毁不通知/重算月光草；已成熟植物对光照变化通过细筛跳过重算。
- 四组开始时刻和脚本对照增量/全量模式，完整事实序列、持久字段与 Cursor 一致；已发布快照不被后来事件原位修改。

[持久化测试](../../tests/ResourceCycle.Tests/CyclePersistenceTests.cs) 在每个完成 S 边界关闭/重开并继续；另在八个位置真正发布 E 后注入异常再重开，验证 pending 只应用一次、恢复的预测/路由计数为零、继续轨迹等同正常执行。错误 S 游标被拒绝后，已发布 E 仍可恢复。此为协议阶段中断验证，不是断电/磁盘损坏测试。

## 真实进程试运行与计量

[Smoke-ResourceCycle.ps1](../../scripts/Smoke-ResourceCycle.ps1) 启动五个 Console 进程，分别执行增量连续、全量连续、存档前五步、重开续跑、完成后再开。比较完整可见事件序列与最终诊断文件；完成后再次打开没有重复事件。

默认 D0 16:00 至绝对小时 88 的脚本产生 17 次转换，最后事件在 D3 06:00；上界内没有后续事件时不凭空推进时钟。五次销毁脚本已消费，两个植物成熟。

| 进程内工作量 | 增量 | 全量对照 |
| --- | ---: | ---: |
| dirty 预测重算 | 26 | 68 |
| 执行前预测校验 | 17 | 17 |
| 失效通知路由 | 34 | 0（绕过依赖筛选） |

增量少做 42 次 dirty 重算，约 62%。这不计初始世界构建，也不是总运行时间或总工作量加速；校验、集合操作、持久化开销另在。两条路径没有因重算次数不同改变预测身份或结果。

最终多进程工件在 `artifacts/resource-cycle-final-smoke/`，包含日志、状态 Markdown 和 `acceptance.json`。首次功能验收工件在 `artifacts/resource-cycle-acceptance/`；后续只对诊断文字润色，不改变业务结果。

## 独立审查与试读

独立审查检查持久预测权威、依赖路由、同刻语义、纯 fold 和恢复边界。修正了计量口径：执行前 stale guard 的 Predict 调用另计 `PredictionChecks`，不混同 dirty 重算节省。

第二轮独立直接运行 CLI，观察 18:00 暂停、冷开、23:00 销毁、次日 06:00 恢复预测，并试运行非法参数与无事件上界。工件在 `artifacts/resource-cycle-review-20261027/`。据试读增加三点文字：明确植物生长光照条件，说明预测受当前条件约束，以及计数/最近原因仅属于本进程。

## 能力边界与下一步

本实例证明依赖驱动的重算和持久预测可以配合运作；它没有实现通用业务事件总线。光照业务仍显式更新植物，固定绑定只路由预测失效；动态增删订阅、任意处理器及递归事件反应尚未设计。

目前仅两个植物；候选扫描、预测字典重建、部分集合替换和完整验证仍有线性成本。不能据此宣称大世界性能已达标。若后续增加大量预测者，再度量候选选择、索引维护与持久化成本，决定是否需要运行时优先队列或更细增量结构。

后续应先检视这个模式的业务编写体验，再选择增加预测者类型或动态生命周期；空间和 Player 接入均不自动作为剩余施工项。当前 schema/规则没有旧存档迁移承诺；未做远端 CI、Linux 或真实模型调用验收。
