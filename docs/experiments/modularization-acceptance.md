# 渐进模块化验收

日期：2026-10-27。对应[重构方案](../implementation/modularization-plan.md) A/B；容量探针和物理拆分尚未完成。

## 已落地的职责边界

- [CycleBackpack](../../src/ResourceCycle/CycleBackpack.cs) 独立承载物品数量与 `CanAdd` 单份接纳查询。开始与结算两处检查保留，`Add` 产生新快照；容量拒绝后的资源/活动/通知仍由采集编排决定。持久类型身份、字段与 GameRevision 不变。
- [PlayView](../../src/ResourceCycle.Cli/PlayView.cs) 提供纯 Markdown 观察、命令列表和公共变化；[PlayInput](../../src/ResourceCycle.Cli/PlayInput.cs) 解析完整命令语法。[PlayConsole](../../src/ResourceCycle.Cli/PlayConsole.cs) 保留驱动、IO、EOF、重试与 `CycleActions.Check`。渲染方法体除访问修饰符外与原实现一致。
- CLI 测试独立引用 CLI；领域测试继续只引用领域库。没有新增生产程序集、公共展示框架或信息隔离 API。
- [CycleFactApplication](../../src/ResourceCycle/CycleFactApplication.cs) 只对业务态应用记录替换并检查事实形状/时间；[CyclePredictionMaintenance](../../src/ResourceCycle/CyclePredictionMaintenance.cs) 从业务态独立处理固定绑定、dirty 与派生预测。两者不依赖 World/controller、不反调 Engine；Engine 保留公开入口、候选政策和 Complete 顺序编排。没有新增中间状态完整校验。

## 行为与恢复证据

| 合同 | 证据 |
| --- | --- |
| 单份接纳、上限边界、不同物品独立、快照隔离 | [BackpackTests](../../tests/ResourceCycle.Tests/BackpackTests.cs)，包含旧 Items 视图及构造入参不受后续操作影响 |
| 开始满包拒绝；取得最后一份后下个周期拒收且不扣资源 | [GatheringBoundaryTests](../../tests/ResourceCycle.Tests/GatheringBoundaryTests.cs) 原有集成用例 |
| 效果按记录恢复，不重新执行控制器/Plan | [MultiplayerRecoveryTests](../../tests/ResourceCycle.Tests/MultiplayerRecoveryTests.cs) 等现有真实 E-only 冷开用例 |
| 大小写/空白/参数数量/整数边界；语法通过不等于业务合法 | [PlayInputTests](../../tests/ResourceCycle.Cli.Tests/PlayInputTests.cs) |
| 他人私有字段变化不改变本角色完整观察；自己的视图仍包含私有信息 | [PlayViewTests](../../tests/ResourceCycle.Cli.Tests/PlayViewTests.cs) 的差异世界比较 |
| 历史加入/耗尽与当前采集/余量同时表达；渲染不确认通知 | 同一视图测试；不变性比较使用 `DescribeState()`，而非无内容的默认 ToString |
| 无 World/controller 的记录事实应用、attention 更新和错误事实拒绝 | [CycleMaintenanceBoundaryTests](../../tests/ResourceCycle.Tests/CycleMaintenanceBoundaryTests.cs) |
| 批内改变后恢复原引用仍逐事实收集 dirty，最终每 owner 只重算一次 | 同一边界测试；同时验证相同预测/缓存实例复用 |
| 无输出 owner 保留订阅、同刻顺序、增量/全量完整轨迹一致 | [CycleBehaviorTests](../../tests/ResourceCycle.Tests/CycleBehaviorTests.cs) 与未修改的独立公式 oracle；预测校验未悄然改为全量重算 |

## 实际运行

在 Windows、SDK 10.0.201、相邻 DG/Storage 源码工作图执行：

```powershell
dotnet build DramaBoard.slnx -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
dotnet test DramaBoard.slnx -c Release --no-build --no-restore -m:1
pwsh -NoProfile -File scripts/Smoke-ResourceCycle.ps1
pwsh -NoProfile -File scripts/Smoke-ResourceCyclePlay.ps1
```

solution 构建零警告/错误，Schema Verify 通过；380 项测试全部通过，无失败/跳过（Host 2、Kernel 131、ResourceCycle 110、CLI 46、Spatial 91）。最终全量复验包含四项新的维护职责测试和完整状态不变性断言。

批处理 smoke 的连续、全量重算、磁盘续局三条轨迹均为相同 29 个事件；终态快照 SHA256 为 `EA199EED12637CB399D9A5A61686E77880E5E3CD65B11A69481F0B21B20326EB`，再次重开没有重复事件。工件：`artifacts/resource-cycle-smoke/628a3862262a4827935d5f931c12f7c3/`。

真实 Console smoke 通过：查询/非法输入/EOF/错误入口保持存档字节不变；加入与耗尽唤醒在途角色；有限竞争结束、cancel 后改活动、跨夜运行及磁盘续局成立。连续运行与恢复后的观察相同，竞争终态 D1 04:00。最新工件：`artifacts/resource-cycle-play-smoke/811b62453f7a4e39a35883be17708773/`。工件不提交，可用上方脚本重新生成。

## 证据边界

本轮分离预测职责但没有改变算法、容量玩法、Schema 或持久历史文本；容量变体/程序集提取尚未验收。Console 仍以完整可信世界为输入；不泄露测试约束当前文本，并不构成控制器信息安全边界。测试运行于 Windows，不冒充已执行跨平台 CI 或真实断电测试。
