# 渐进模块化验收

日期：2026-10-27。对应[重构方案](../implementation/modularization-plan.md) A–D；逻辑边界、容量变体和 Inventory 程序集提取均已验收。

## 已落地的职责边界

- [ItemInventory](../../src/Inventory/ItemInventory.cs) 独立承载物品数量、可选总容量与 `CheckAdd` 单份接纳查询。开始与结算两处检查保留，`Add` 自行检查并产生新快照；容量拒绝后的资源/活动/通知仍由采集编排决定。字段与 Schema 演进见下文，GameRevision 不变。
- [PlayView](../../src/ResourceCycle.Cli/PlayView.cs) 提供纯 Markdown 观察、命令列表和公共变化；当时的 `PlayInput` 解析完整命令语法（后按 [Console 边界合同](../design/console-command-boundary.md)拆分）。[PlayConsole](../../src/ResourceCycle.Cli/PlayConsole.cs) 保留驱动、IO、EOF、重试与 `CycleActions.Check`。B 提取时保持原渲染；C 仅新增所属角色的有限容量行，默认无限画面保持。
- CLI 测试独立引用 CLI；领域测试继续只引用领域库。没有新增公共展示框架或信息隔离 API。
- [CycleFactApplication](../../src/ResourceCycle/CycleFactApplication.cs) 只对业务态应用记录替换并检查事实形状/时间；[CyclePredictionMaintenance](../../src/ResourceCycle/CyclePredictionMaintenance.cs) 从业务态独立处理固定绑定、dirty 与派生预测。两者不依赖 World/controller、不反调 Engine；Engine 保留公开入口、候选政策和 Complete 顺序编排。没有新增中间状态完整校验。
- [Inventory](../../src/Inventory/Inventory.csproj) 只依赖 DG 运行库与生成工具；[Inventory.Tests](../../tests/Inventory.Tests/Inventory.Tests.csproj) 只引用 Inventory。公开的接纳、单份 Add 和 Validate 满足实际消费者，没有新友元或通用组件基类。[游戏注册入口](../../src/ResourceCycle/Persistence/CycleDurableModels.cs) 显式组合 Kernel、Inventory 和游戏定义，游戏继续拥有根、事实、工厂、修订和磁盘提交。

## 行为与恢复证据

| 合同 | 证据 |
| --- | --- |
| 单份接纳、上限边界、不同物品独立、快照隔离 | [ItemInventoryTests](../../tests/Inventory.Tests/ItemInventoryTests.cs)，包含旧 Items 视图及构造入参不受后续操作影响 |
| 开始满包拒绝；取得最后一份后下个周期拒收且不扣资源 | [GatheringBoundaryTests](../../tests/ResourceCycle.Tests/GatheringBoundaryTests.cs) 原有集成用例 |
| 效果按记录恢复，不重新执行控制器/Plan | [MultiplayerRecoveryTests](../../tests/ResourceCycle.Tests/MultiplayerRecoveryTests.cs) 等现有真实 E-only 冷开用例 |
| 大小写/空白/参数数量/整数边界；语法通过不等于业务合法 | 当时的 `PlayInputTests`；现行动部分见 [CycleCommandParserTests](../../tests/ResourceCycle.Cli.Tests/CycleCommandParserTests.cs) |
| 他人私有字段变化不改变本角色完整观察；自己的视图仍包含私有信息 | [PlayViewTests](../../tests/ResourceCycle.Cli.Tests/PlayViewTests.cs) 的差异世界比较 |
| 历史加入/耗尽与当前采集/余量同时表达；渲染不确认通知 | 同一视图测试；不变性比较使用 `DescribeState()`，而非无内容的默认 ToString |
| 无 World/controller 的记录事实应用、attention 更新和错误事实拒绝 | [CycleMaintenanceBoundaryTests](../../tests/ResourceCycle.Tests/CycleMaintenanceBoundaryTests.cs) |
| 批内改变后恢复原引用仍逐事实收集 dirty，最终每 owner 只重算一次 | 同一边界测试；同时验证相同预测/缓存实例复用 |
| 无输出 owner 保留订阅、同刻顺序、增量/全量完整轨迹一致 | [CycleBehaviorTests](../../tests/ResourceCycle.Tests/CycleBehaviorTests.cs) 与未修改的独立公式 oracle；预测校验未悄然改为全量重算 |
| 跨物品共享总容量、0/null/负容量、总量超过 int、拒收不能绕过 | [ItemInventoryTests](../../tests/Inventory.Tests/ItemInventoryTests.cs) 的容量与输入边界用例 |
| 容量 2、目标 3，仅取得/扣资源 2 份；已有其他物品占用容量 | [CapacityGatheringTests](../../tests/ResourceCycle.Tests/CapacityGatheringTests.cs)，使用长再生周期排除补货干扰，并检查失败时刻与无新通知 |
| 满包替换的真实 S-only/E-only 冷开，容量保持、收益不重复 | 同一测试使用 ForbiddenPlanning/ForbiddenController，比较连续与恢复的状态/游标/事件，并再冷开确认 |
| 有限容量仅所属角色可见，默认无限视图不变 | [PlayViewTests](../../tests/ResourceCycle.Cli.Tests/PlayViewTests.cs) 的容量差异世界比较 |

## 容量 Schema 与修订支持范围

Backpack 保留逻辑身份 `DramaBoard.ResourceCycle.Backpack`，Schema 升到 v3；field 1 仍为数量字典，field 2 新增 nullable long 容量。TotalCount 与接纳结果不持久化。Publish 仅新增一个历史文件，v1/v2 未删改，随后 Verify 通过。

GameRevision 保留 1，因为默认和原可表达无限状态的规则未变；这不等于 Schema 兼容。未新增 DG 相邻升级 provider，包含 v1/v2 背包的旧开发存档不受支持。主线程在构建新模型前用旧 CLI 创建实际 v2 存档，再用新 CLI 打开：捕获 InvalidDataException 后退出码 1，只输出 `ResourceCycle: An adjacent owner upgrade capability is missing.`，存档所有文件 SHA256 不变。该探针发现并修正了 CLI 漏捕获 InvalidDataException 的问题。

旧格式探针工件：`artifacts/capacity-schema-v2/747c1f61d17841f2972d08268070da65/`。它验证的是受控拒绝，不是旧存档升级。新 v3 的完整 S/E 恢复由上述实际磁盘测试覆盖。

## 程序集提取与跨二进制回读

D 使用 Roslyn `Renamer.RenameSymbolAsync` 改声明与绑定引用，禁用字符串/注释替换；再显式移动文件、命名空间和项目接线。CLR 类型现为 `DramaBoard.Inventory.ItemInventory`，角色属性仍称 Backpack。迁移工具与当时的 Schema 哈希留在忽略目录 `artifacts/inventory-symbol-rename/`，工具不成为产品依赖。

三个 Schema 文件由 ResourceCycle 移入 Inventory，分别与迁移前 Git blob 完全一致。生成定义检查确认：Inventory 独占 Backpack 的 owner 定义并提供三版模板；ResourceCycle 只引用同一逻辑身份，没有重复生成或注册。Schema Verify 检查 Inventory 的 1 个当前模型/3 份历史、ResourceCycle 的 14 个当前模型/22 份历史。最终运行时依赖闭包也未带入 Kernel、Persistence 或游戏程序集；库存测试同样不依赖游戏。

主线程在构建 D 前复制 C 的真实 v3 多人存档，记录旧二进制的冷开观察和继续一次等待的输出。D 冷开该存档后完整观察相同，查询/退出前后所有存档文件哈希不变；继续同一操作的输出也相同（仅归一化两个副本的存档路径）。该对照没有用新二进制重新创建所谓旧存档。相关输出、哈希与依赖审计：`artifacts/inventory-extraction/`。有限容量与 pending E 的真实冷开仍由容量恢复测试覆盖。

## 实际运行

在 Windows、SDK 10.0.201、相邻 DG/Storage 源码工作图执行；DG 为 `dd645c1bde973c82f95885b2fd0a395624ba6edb`，Storage 为 `70b406cd8d1117733eee81a33e7f978399aa98b2`，两仓工作树均干净：

```powershell
dotnet build DramaBoard.slnx -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
dotnet test DramaBoard.slnx -c Release --no-build --no-restore -m:1
pwsh -NoProfile -File scripts/Smoke-ResourceCycle.ps1
pwsh -NoProfile -File scripts/Smoke-ResourceCyclePlay.ps1
```

solution 构建零警告/错误，Schema Verify 通过；399 项测试全部通过，无失败/跳过（Host 2、Inventory 18、Kernel 131、ResourceCycle 110、CLI 47、Spatial 91）。库存测试从游戏项目迁入新项目，总数未变；这次运行覆盖 D 的程序集形状与已有 CLI 异常处理。

批处理 smoke 的连续、全量重算、磁盘续局三条轨迹均为相同 29 个事件；终态快照 SHA256 为 `EA199EED12637CB399D9A5A61686E77880E5E3CD65B11A69481F0B21B20326EB`，与上一片一致，再次重开没有重复事件。工件：`artifacts/resource-cycle-smoke/f736ec8b01c542e3b5d18e4387d55e0e/`。

真实 Console smoke 通过：查询/非法输入/EOF/错误入口保持存档字节不变；加入与耗尽唤醒在途角色；有限竞争结束、cancel 后改活动、跨夜运行及磁盘续局成立。连续运行与恢复后的观察相同，竞争终态 D1 04:00。最新工件：`artifacts/resource-cycle-play-smoke/d7ce98d939af454e9b5e45db81085a9c/`。工件不提交，可用上方脚本重新生成。

## 证据边界

按 A–D 逐项复核后，当前重构范围没有未完成的必做项：局部规则、应用职责、约束变体和物理提取均有相应证据。资源/活动的进一步通用化、持久文本全面结构化、空间组合和 mod 运行框架不属于这次验收范围。

容量变体已经验证，但不是第二个完整游戏；默认 demo 未启用有限容量。Console 仍以完整可信世界为输入；不泄露测试约束当前文本，并不构成控制器信息安全边界。测试运行于 Windows，不冒充已执行跨平台 CI 或真实断电测试。
