# ResourceCycle：运行时预测缓存验收

2026-10-27。按[缓存方案第 9 节](../design/runtime-derived-predictions.md#9-resourcecycle-单角色迁移)完成单角色迁移，保留批处理和单角色等待/即时采集玩法。多角色化独立推进，没有旧存档兼容负担。

## 状态与完成边界

- [CycleBusinessState / CycleWorld](../../src/ResourceCycle/CycleModels.cs)：持久 Business 包含时间、光照、植物、脚本、可选角色；角色和背包仍为独立业务子图。World 是普通不可变运行时包装，包含 Business 和 Forecasts。
- 预测、固定订阅、订阅索引都能由业务身份和代码重建，不落盘。暂停植物即使没有成熟预测，订阅仍存在。没有新增动态订阅机制。
- [CycleEngine](../../src/ResourceCycle/CycleEngine.cs)：Plan 只生成业务 facts；Fold 应用业务并暂留旧缓存；Complete 顺序读取 facts 收集 dirty、补入已消费 owner，针对最终业务状态每个 owner 重算一次。Full 模式绕过路由、独立枚举全部 owner。
- Complete 核对最终时间和业务子图引用，保留调用方传入的 folded.Business；不改变已记录事实，不调用 Player。Validate 检查业务、边界时间和已有缓存项的身份/结构，不通过完整重预测掩盖维护遗漏。
- [CycleDurableHistory](../../src/ResourceCycle/Persistence/CycleDurableHistory.cs)：S 只保存 Business/Cursor，E 只保存已决定的业务变化。冷开 Rebuild 一次后 State 返回稳定实例；S 成功后才安装传入的同一 wrapper。pending 恢复执行 Fold → Complete → Validate → S，不重新仲裁或规划。

同刻仍按成熟、光照、脚本销毁、等待完成、决策的顺序执行；只有相同 Due/Kind 内才使用 Kernel 仲裁。等待截止、RequiredTicks、generation、背包和最后行动结果仍是业务真相，不因删除预测而隐式改变。

## 自动化证据

| 合同 | 可复核证据 |
| --- | --- |
| 预测完整性、增量与重建等价 | [CycleTestSupport](../../tests/ResourceCycle.Tests/CycleTestSupport.cs) 从业务直接枚举独立公式 oracle，不调用 Owners/Predict 或路由；每个完成边界比较缓存及 Rebuild。另有缺缓存负例证明可检出遗漏 |
| 暂停、销毁、同刻优先级与局部重算 | [CycleBehaviorTests](../../tests/ResourceCycle.Tests/CycleBehaviorTests.cs) 保留手算预期和多组完整 Full/Incremental 轨迹对照；无输出植物可被日出/日落唤醒，无关植物不重算 |
| Complete 与快照不变 | [CycleCompletionTests](../../tests/ResourceCycle.Tests/CycleCompletionTests.cs) 覆盖同批多 facts、dirty 去重、保存 folded.Business 引用、未知事实拒绝、额外时间/光照/角色变化拒绝、due-now 保留、过期预测拒绝；候选读取不重算或替换缓存 |
| 单角色完整轨迹与决策 | [PlayableCycleTests](../../tests/ResourceCycle.Tests/PlayableCycleTests.cs) 每个 S 都 fresh open 后继续，逐事件比较状态、游标、事实及输入次数；等待途中不问新动作，等待完成/采集后再次决策 |
| E-only 恢复 | [CyclePersistenceTests](../../tests/ResourceCycle.Tests/CyclePersistenceTests.cs) 保留八处批处理 E-only 中断；PlayableCycleTests 覆盖等待开始、等待完成、采集 E-only。纯 Complete 可重复，Player/Plan 零次；另以 Forecast/Plan 抛错探针验证 pending 不重新调度 |
| 发布引用边界 | CyclePersistenceTests 检查 E 前后和 S 前仍为旧 wrapper；S 成功返回后为传入同实例。S 前注入失败、非法 S cursor 后重开，pending 均可恢复，旧快照不变 |
| 真正不落盘 | [CycleStorageShapeTests](../../tests/ResourceCycle.Tests/CycleStorageShapeTests.cs) 绕过 adapter，直接加载磁盘 S/E，检查 durable 字段、可达对象图及生成模型目录：不含 World/Forecasts/Prediction/Binding/PredictionKind |
| 冷开只信业务真相 | 同上：缺失/过期预测和空路由在真实保存、冷开后丢弃并重建；真正业务成熟时刻已经过期的存档在 Open 被拒绝 |

恢复计量口径随之调整：`PredictionChecks` 仍为 Plan 的执行前检查；`Recalculations`/路由计数包含当前 Kernel 的 pending Complete，不包括 genesis 或冷开 Rebuild。它们是诊断，不是持久世界状态。

## 本轮执行

在 Windows / .NET SDK 10.0.201 上串行构建并验证：

```powershell
dotnet build DramaBoard.slnx -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
dotnet test DramaBoard.slnx -c Release --no-build --no-restore -m:1
pwsh -NoProfile -File scripts/Smoke-ResourceCycle.ps1 -OutputDirectory artifacts/resource-cycle-cache-batch-smoke
pwsh -NoProfile -File scripts/Smoke-ResourceCyclePlay.ps1 -OutputDirectory artifacts/resource-cycle-cache-play-smoke
```

最终全套 **276 项通过，零失败/跳过**（ResourceCycle 52、Kernel 131、Spatial 91、Host 2）。Release 与 Schema Verify 零警告/错误；ResourceCycle 当前 10 个 schema，旧 World/预测/订阅及原 Fact/Root schema 已移除，新 BusinessState、业务 Fact 和 Root 由生成工具产生，不提供双格式读取。

批处理 smoke 对比连续、Full、分段冷开和最终再次打开的完整事件/最终诊断。采集 smoke 使用实际 CLI 子进程，验证两次进程间续玩与连续执行等价：从 16:00 等待 6 小时、采月光草，再等待 9 小时、采两种植物，最终太阳花 ×1、月光草 ×2；只读/非法命令、EOF 与模式误开不改变存档。

独立只读审查检查了核心与持久化合同，未发现额外阻塞。主线程修正集成中发现的业务根默认引用相等误用，并以成功/拒绝两类测试验证。日志位于忽略目录 `artifacts/resource-cycle-cache-*.log`，两份 smoke 各有 `acceptance.json` 与逐进程输出。

## 边界

- 本片不包含多角色轮转、资源竞争、角色增删、空间或 LLM 输入。当前 owner/固定订阅来自同一世界内固定身份，扩展生命周期时须增加相关路由维护与轨迹验收。
- 发布故障探针覆盖协议边界，不模拟真实断电或部分磁盘写入。陈旧缓存实验不代表任意算法版本或旧 pending 的跨版本兼容。
- 独立 oracle 验证完整性；生产 Validate 没有全量重建兜底。没有据计数宣称整体 CPU 或大世界复杂度收益。
- 未修改 Kernel、Spatial 或上游/兄弟仓库源码；本轮没有引入通用缓存框架或平衡参数热更新。
