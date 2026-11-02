# 小镇 owner 预测维护验收

> 历史记录：Microtown 源码、测试和 smoke 已于 2026-11-02 从主线移除，见 [Git tag 归档](../archive/microtown.md)。本文命令、实现状态和测试结果属于当时版本。

本片从 `b23071f` 的逐次全量 Forecast 改为运行时 owner 维护。它不新增玩法或存档字段；小镇 GameRevision 仍为 3。完整经济日真实模型验收继续由[工作计划](../codex-goals/microtown/work-plan.md)跟踪，不能用本片离线证据替代。

## 实现边界

- [TownForecasts / TownPredictionMaintenance](https://github.com/Robird/drama-board/blob/b6fea5e94ca3ea718cee2546a2d785afa1bafa6d/src/Microtown/TownForecasts.cs) 保存不可变 owner 输出：居民饥饿失败与活动完成、市场刷新、有效报价到期、实验终点、全局决策。`LastRecomputedOwners` 只描述本次准备，不累计日志。
- [TownEngine](https://github.com/Robird/drama-board/blob/b6fea5e94ca3ea718cee2546a2d785afa1bafa6d/src/Microtown/TownEngine.cs) 在新局/冷开 Rebuild，在正常推进和 pending 恢复的 Complete 中准备缓存；Fold 保留旧缓存但来源标记使它不能直接作为就绪世界。S 成功后才安装，旧 wrapper 不变。
- [候选投影](https://github.com/Robird/drama-board/blob/b6fea5e94ca3ea718cee2546a2d785afa1bafa6d/src/Microtown/TownEngine.Forecasts.cs) 使用当前 Generation 构造 key，组合 Spatial 到达与已有接触缓存。`ForecastOracle` 保留原全量算法；正常 Forecast/Validate 不调用它。
- 自然食物耗损不改变绝对失败截止，市场库存变化不改变刷新时刻，报价交付标记不改变到期时刻。只有相关输入变化才重算；全局决策保守检查以覆盖跨角色唤醒与轮转。

这是局部预测维护，不是性能基准或次线性复杂度证明；仍会枚举有限角色/报价来检查依赖、投影候选。没有新增通用事件总线、owner 接口或持久化缓存。

## 独立测试证据

[TownPredictionMaintenanceTests](https://github.com/Robird/drama-board/blob/b6fea5e94ca3ea718cee2546a2d785afa1bafa6d/tests/Microtown.Tests/TownPredictionMaintenanceTests.cs) 的 11 个案例逐边界比较完整 owner 集合，并在重新构造空间缓存的 wrapper 上调用全量 oracle，比较原始候选序列。每次真实 Kernel Step 前，用 oracle 经 `OccurrenceScheduler` 计算预期 winner，再核对提交的 CauseKey 和 LogicalInstant。

显式案例覆盖：无关库存/目标/拒绝复用；进食重新预测；报价交付与其他报价终结不重算本报价；小于一个 tick 的食物储备 1/2/4 的取整与 clamp；连续同刻失败；多报价到期、市场、活动与终点优先级；到达与活动/终点竞争；对向接触在 1.375h 消费、随后 1.4h 的无关居民失败；新决策使用当前时间；Fold-only 状态被拒绝。

[TownPredictionPersistenceTests](https://github.com/Robird/drama-board/blob/b6fea5e94ca3ea718cee2546a2d785afa1bafa6d/tests/Microtown.Tests/TownPredictionPersistenceTests.cs) 的 7 个案例使用真实 DG：

- 26h 场景每个完成 S 都关开，比较未中断运行的业务、游标、完整缓存、候选与事实轨迹；覆盖生产、移动、到期、进食、市场更新及终点。
- 分别把生产开始、生产完成、报价到期、移动开始和进食留作真实 E-only；加载保持前态，恢复禁止调用控制器，重复恢复不产生效果，结果与未中断边界相同。
- 从精确决定前 S fork；父局生产和子局等待独立变化，冷开仍保持各自 head/业务/缓存，旧快照不被修改。

[原持久化测试](https://github.com/Robird/drama-board/blob/b6fea5e94ca3ea718cee2546a2d785afa1bafa6d/tests/Microtown.Tests/TownPersistenceTests.cs) 另将两个提交失败窗口改在真正新增活动预测的 `wait` 操作：在 S 发布前或成功后抛出 I/O 错误，Kernel 均不安装准备中的世界；旧缓存引用及内容不变，冷开依磁盘事实恢复，与完整重建一致。

首轮新测试有 4 个失败，原因是测试误把同类同刻仲裁写成角色/报价 ID 顺序；实际 Kernel 使用既定确定性仲裁。已修为 oracle 选 winner、结果集合唯一覆盖及未消费预测保留，未修改引擎来迎合错误假设。初次失败与诊断日志保留，见下节证据目录。

冷开脚本的命令队列由测试宿主持有，这些案例证明游戏恢复与预测重建，不证明控制器会话或原生模型 Tail 持久化。独立审阅核对了上述测试与实际生产接线；主线程负责运行及集成结论。

## 执行记录

2026-10-31，主线程实际运行以下命令，原始材料保留在 `artifacts/microtown-predictions/`：

```powershell
dotnet build src/Microtown.Cli/Microtown.Cli.csproj -c Release -m:1 -p:DurableGraphSchemaHistoryMode=Verify -v:q
dotnet test DramaBoard.slnx -c Release -m:1 -p:DurableGraphSchemaHistoryMode=Verify -v:q
pwsh -NoProfile -File scripts/Smoke-Microtown.ps1 -OutputDirectory artifacts/microtown-predictions/console
```

- CLI 构建零警告、零错误；未修改任何持久 Schema。
- 最终 solution 13 个测试项目共 **1,289 通过、0 失败、0 跳过**；其中 Microtown 142 项，新增 18 项并补强原有两个发布失败案例。日志 `tests-solution.log` 是最终结果，`tests-town.log` / `tests-diagnostics.log` 保留前述修正前的失败证据。
- 产品 Human Console 三进程探针通过：先拒绝超量购买、执行三人采购并预算暂停；冷开继续移动/生产/消费/等待，到 **25h 有限终点**，hour 24 市场刷新一次；结束后再冷开不激活控制器、不重复产出。
- Console 终态三人饱食均为 25、均存活、每人现金 10；全镇现金 30 = 初始 54 − 进口支出 24。12 grain 进口、6 grain 配方消耗、6 meal 产出、3 meal 食用，加初始 3 meal 后剩余 6 meal，账实一致。此次 Console 没生产 woodware 或外销，相关循环由既有 48h 规则测试及新增 26h 冷开轨迹覆盖，不能冒充真实模型覆盖。

Console 的 `.in.txt` / `.out.txt` / `.err.txt`、`acceptance.json` 与真实存档均保留在 `console/`。独立核验直接读取三组原始输入输出，确认冷开后的 12 条操作全部成功，结束再开统计及完成 S `625994110e00001013000000` 均相同；结果保存在 `console/REPORT.md` 和 `independent-check.json`。本片没有真实模型调用；下一轮另按事先声明的时长与预算运行三居民完整经济日、反馈定位 fork 和冷开行动。
