# ResourceCycle 多角色有限采集验收

> 2026-10-27。对应[多人采集规则](../design/resource-cycle-multiplayer-gathering.md)。本记录区分当前实现、实际执行证据和延期能力；旧单角色实验的测试结果不作为本轮证据。

## 实现边界

- 同一 Console 控制阿青、阿白、阿岚三人，采药等级分别 0/1/2；太阳花、月光草初始各 3/5 份。基础采集周期一小时，经技能加速分别为 60/48/40 分钟。
- 资源存量和再生进度独立。昼夜只控制再生，满仓不囤积进度，未满仓的采集不清再生进度。
- `gather <resource> <n|all>` 创建有限目标；每周期结算一份，达标或首次无货结束。`all` 固定下令时数量，不预约库存。
- 活动与注意力分开：他人加入、资源由有货变零都记录感知，推进到下一时刻前请求响应。`continue` 保留活动身份与 Due，`cancel` 保留已得产出、取消未完成周期。
- 同刻先自然事件，再所有活动完成，再请求输入。角色服务轮转独立于抢资源仲裁；没有平均分配承诺。
- 资源、角色、背包、通知与游标单根发布；多角色通知与引发它们的业务变化同一 E/S。缓存及路由不落盘，冷开重建；pending 恢复不再调用 Player 或 Plan。
- 正式玩家视图只显示自己的技能、活动截止时刻和背包。调试视图单独显示预测/订阅。历史结果标为“该次结算后余量”，避免与当前库存混淆。

Kernel、Spatial、Host 和 DurableGraph 源码没有因本片修改；沿用相邻源码引用。DurableGraph 生成器产生本片的新 schema 记录；没有手写生成代码或提供旧存档迁移。

## 自动验收

执行命令：

```powershell
dotnet build src/ResourceCycle/ResourceCycle.csproj -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Publish
dotnet build DramaBoard.slnx -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
dotnet test DramaBoard.slnx -c Release --no-build --no-restore -m:1
pwsh -NoProfile -File scripts/Smoke-ResourceCyclePlay.ps1
pwsh -NoProfile -File scripts/Smoke-ResourceCycle.ps1
```

首轮完整构建及 Schema Verify 零警告、零错误；302 项测试通过、无失败/跳过：ResourceCycle 78、Kernel 131、Spatial 91、Host 2。随后内部订阅收窄及试玩文字修改的最终复验结果见下节，不能用首轮结果代替改后验证。

**最终复验**：订阅按事件种类与身份收窄、历史结果和 CLI 文案修正后，再次执行 solution Release 构建与 Schema Verify，零警告/错误；全部 302 项测试再次通过，无失败/跳过；两份 smoke 再次通过。主线程抽查改后真实耗尽画面，历史结果与当前库存分开标注，重复句号已消除，等待受阻参数压缩为 `1–24`。

关键自动证据：

| 合同 | 测试入口与区分力 |
| --- | --- |
| 再生与采摘独立 | [CycleBehaviorTests](../../tests/ResourceCycle.Tests/CycleBehaviorTests.cs)、[PlayableCycleTests](../../tests/ResourceCycle.Tests/PlayableCycleTests.cs)：昼夜保留进度、满仓停产、未满仓采摘不重置、补货/切光/采集同刻顺序 |
| 有限批次与通知 | PlayableCycleTests：1/n/all、快速再生不扩大目标、提前耗尽唤醒、本人采空提醒、continue 保持 Activity 与预测引用、取消换行动 |
| 竞争及失效候选 | [MultiplayerEdgeTests](../../tests/ResourceCycle.Tests/MultiplayerEdgeTests.cs)：同 Due 最后一份只给一人，感知确认和无关完成不改变胜负，取消重启后旧候选被拒绝 |
| 完成与冷开等价 | [CycleTestSupport](../../tests/ResourceCycle.Tests/CycleTestSupport.cs) 独立业务公式 oracle；PlayableCycleTests 对照 100 个增量/全量边界、50 个真实磁盘逐 S 冷开边界和请求顺序 |
| 多角色 E-only 恢复 | [MultiplayerRecoveryTests](../../tests/ResourceCycle.Tests/MultiplayerRecoveryTests.cs)：加入、耗尽、确认的真实磁盘 pending；恢复使用禁止 Forecast/Plan 的探针；未提交确认冷开仍保留感知 |
| 发布及纯派生边界 | [CyclePersistenceTests](../../tests/ResourceCycle.Tests/CyclePersistenceTests.cs)、[CycleCompletionTests](../../tests/ResourceCycle.Tests/CycleCompletionTests.cs)、[CycleStorageShapeTests](../../tests/ResourceCycle.Tests/CycleStorageShapeTests.cs)：E/S 失败、旧快照不可变、多事实折叠一致、实际存档无缓存/路由类型 |
| 数值及消息边界 | [GatheringBoundaryTests](../../tests/ResourceCycle.Tests/GatheringBoundaryTests.cs)：大时长百分比不因中间乘积溢出、满背包初始拒绝/途中停止、通知聚合、启动新活动消费旧感知；MultiplayerEdgeTests 验证续周期溢出保留本次产出 |

## Console 运行与试玩

[可玩 smoke](../../scripts/Smoke-ResourceCyclePlay.ps1) 实际启动多个 CLI 进程并检查输出及存档文件哈希，首轮全部通过：

- A/B 同采太阳花，C 等待；B 于 D0 17:36 取走最后一份，世界当时即请求感知响应，A/B 原截止时刻保持。
- 在待响应处退出、冷开；look、非法命令、EOF 不改变存档字节。连续运行和分段续玩最终观察完全一致。
- 继续路线：A 于 18:00、B 于 18:24 无货结束；取消路线：B 改采月光草，18:24 拥有太阳花 2、月光草 1。
- 三人等待跨越昼夜的连续/冷开对照，以及批处理/可玩存档误用拒绝均通过。
- 正式其他角色栏目不泄漏技能、未来 Due 或背包。

[批处理 smoke](../../scripts/Smoke-ResourceCycle.ps1) 同样实际启动多个进程：连续、全量重算、分段续跑的事件轨迹和最终诊断相同；首轮轨迹 29 个事件，重开已完成存档不重复事件。

额外 UI 试玩使用另一条实际输入路线：三人同点采集，A 一屏收到 B/C 两条加入感知；A 取消重开、C 取消再选 all，会按真实新活动再次提醒。D0 17:00 三人各取得 1 份后 A 取空；C 此时被唤醒但 Due 仍为 17:20。保存、冷开后分别继续/取消，A 保留所得转等待，C/B 分别于 17:20/17:36 无货结束。该轮发现并修复了历史结果“当前余量”与真实当前库存混淆的文案，另消除重复句号、压缩 wait 参数范围并补明耗尽先提醒。

另一位只读审查代理实际运行三个独立 Console 进程，从 D0 16:00 玩到 D1 08:40：A 采太阳花 all，B 采两份月光草，C 异步等待后两次采月光草 all。在 D0 17:36 与 D1 08:00 两次退出冷开，原活动按既有截止时刻完成；最终 A 的太阳花累计 4，C 的月光草累计 4。还观察到太阳花夜间保留 120/180 分钟再生进度、次日 07:00 补货，以及白天可采已有月光草；没有发现必须修改的问题。两轮额外试玩早于最终文字润色，改后输出由最终 smoke 重新验证。

本地证据（生成目录，不纳入 Git）：

- `artifacts/multiplayer-test-results/`：首轮各测试程序集 TRX。
- `artifacts/multiplayer-play-smoke-first/`、`artifacts/multiplayer-batch-smoke-first/`：输入路径、逐进程输出、`acceptance.json` 及存档。
- `artifacts/multiplayer-playtest-ui/7a0d9813b4b747f790c12bef1a34b2ea/`：额外三人竞争、查询字节不变、取消重入及冷开试玩。
- `artifacts/multiplayer-independent/42cf8764a5c34efd824af2906f2911ca/`：独立跨资源/跨昼夜试玩、两次冷开及输入日志。
- `artifacts/multiplayer-test-results-final/`、`artifacts/multiplayer-play-smoke-final/`、`artifacts/multiplayer-batch-smoke-final/`：最终代码的 TRX、进程输出与通过记录。

## 后续能力边界

当前仅有采药等级和单项确定产出；未实现概率表、技能成长、多个地点、战斗、笔记工具、即时使用物品或 LLM API。角色仍只能在被调度到的交互机会输入，没有后台手动打断线程。宝箱/唯一草药需要“不再生”或“目标失效”的显式规则，首片没有伪装成可玩的宝箱。

真实重新加入可再次提醒；只去重重复交付并聚合待响应消息，没有防恶意加入/退出的节流策略。Kernel 同 tick 上限仍是故障护栏。界面通过真实重定向输入进程验证；未把这等同于所有终端的 Ctrl+C/窗口关闭行为验证。

后续根据实际试玩决定提醒策略或另一个玩法，不以完成本片为由自动抽取通用 RuleSuite。历史单角色 schema 文件保留为 schema 历史，不代表旧存档读写兼容承诺。
