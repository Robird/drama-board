# 生存逃离一期验收

状态：施工中；尚未完成一期。目标见[里程碑](../design/survival-demo-milestone.md)，当前切片见[实施方案](../implementation/survival-demo-plan.md)。本文件区分代码能力、自动化证据与玩家试玩，不以局部测试代替完整目标。

## 1. 覆盖表

| 里程碑要求 | 当前证据 | 判定 |
| --- | --- | --- |
| Console 新游戏、续局和完整胜负循环 | A/B 具备探索、补给、庇护与胜负结局 | 已有局部闭环；随机风险及整体试玩待做 |
| 两路线成功与一次失败独立试玩 | 待生存与风险规则落地后执行 | 未完成 |
| 采集/再生、移动、消费、寒冷、唤醒、随机风险 | A/B 已组合需求与庇护；随机风险待做 | 部分完成 |
| 当前/历史观察与未知世界隔离 | A 的 DTO、校验探针、历史库存与 Console 通过 | A 范围完成；后续新增信息继续验证 |
| 固定随机状态复现、移动/阈值/随机 E-only 恢复 | 移动/阈值 S-only，以及移动/警告/消费/死亡 E-only 通过；尚无随机 | 部分完成 |
| Schema、相关测试与原 Console 回归 | A/B 的 Verify、整套测试和三种 Console smoke 通过 | 当前范围完成；后续持续回归 |
| 运行说明与剩余限制清晰 | README 已说明探索、生存操作与剩余工作 | 当前范围完成 |

## 2. 当前局部证据

库存消费 API `ItemInventory.Remove(itemId, count)` 已由独立工作包实现并由主线程检查源码和测试差异。全量扣除或拒绝、扣空删除键，保留容量、ordinal 身份和不可变快照；无 Schema 变化。工作包实际运行 Inventory.Tests Release + Schema Verify，27 项通过。该结果只覆盖库存规则，不证明食物/饮水玩法已经实现。

### A 空间探索：2026-10-28

源码入口：`CycleGameFactory.CreateSurvival`、`CycleExpedition`、`CycleExpeditionMap`、`CycleEngine`、`CycleObservation`、CLI `--survival`。固定场景 `lost-mountain-v1` 共 7 地点、两出口；通路参数使用中性 `trail-N`，不以命令 ID 泄漏未知终点身份。

主线程复核单 Kernel 仲裁、空间事实与访问/结局同根、Fold/Complete 业务与缓存分界，并独立检查新增测试和真实画面。独立 reviewer 提出的异地采集、伪造逃离和不支持的通路计划边界均已补上校验及真实磁盘测试。观察投影还修复了共享食物 ID 通过全局产地名称泄漏未访问资源的问题。

实际执行：

```powershell
dotnet build DramaBoard.slnx -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
dotnet test DramaBoard.slnx -c Release --no-build --no-restore -m:1
pwsh -NoProfile -File scripts/Smoke-ResourceCycle.ps1
pwsh -NoProfile -File scripts/Smoke-ResourceCyclePlay.ps1
pwsh -NoProfile -File scripts/Smoke-ResourceCycleSurvival.ps1
```

- 最终 Verify 构建零警告/错误；ResourceCycle 17 个当前模型、28 条历史记录，新增 6 条历史。Business/Fact 为 v3、EventKind v4，新增 Expedition/Visit/Outcome v1。应用 GameRevision 为 2，旧开发存档不兼容。
- 全 solution **435 项测试通过，无跳过**：Host 2、Inventory 27、Kernel 131、ResourceCycle 125、CLI 59、Spatial 91。
- [ExpeditionTests](../../tests/ResourceCycle.Tests/ExpeditionTests.cs) 验证移动期限与互斥、同刻昼夜→到达→输入、双出口终态停止、远处 ID 探针、离场后资源记忆不更新、incremental/full 轨迹一致。直接比较空间对象图、空间事实内容和 Contacts，避免只依赖诊断字符串。
- 同一测试组使用真实 CycleDurableHistory：移动中 S-only 冷开保留期限；MoveStarted、普通 MoveArrived、最终出口 MoveArrived 的 E-only 冷恢复均只应用一次。禁止 Forecast/Plan/Controller 的夹具证明恢复未重新决定。
- [ExpeditionRecoveryValidationTests](../../tests/ResourceCycle.Tests/ExpeditionRecoveryValidationTests.cs) 将结构可还原而业务未就绪的图实际落盘：异地采集、未到出口就逃离、逃离仍有工作、当前应用不支持的通路变化计划。正常构造与 Open 都拒绝，拒绝不改写磁盘边界。
- CLI 测试证明初始未知地点不泄漏、观察/命令按可见域裁剪、旧记录带时间，获救后无下一行动提示。单人文案、空资源表和空其他角色章节已按实际输出润色，旧采集表现保留。

真实 Console 工件（忽略目录，重新运行脚本会生成新目录）：

| 脚本 | 本次成功工件 | 证明范围 |
| --- | --- | --- |
| 原批处理 | `artifacts/resource-cycle-smoke/469622afefc24903b3a41f5f8cea3f10/` | 原连续/续跑/全量对照 |
| 原多人采集 | `artifacts/resource-cycle-play-smoke/0ee4224557b048c0b13efaeabb6fe146/` | 加入/耗尽、有限采集、只读不写盘与冷开观察 |
| 新空间探索 | `artifacts/resource-cycle-survival-smoke/64ef6fde44ac43e58b832b5f5e3218a8/` | 两出口、各自续局与连续最终观察等价、只读/非法/EOF/错误模式不写盘、终局冷开不继续、远处库存仅作历史显示 |

新 smoke 的隐藏存在/不存在目标各用独立进程探测并比较唯一拒绝行；没有用多个非法命令的累计匹配数冒充探针证据。主线程检查了初始画面与 `acceptance.json`，两个出口脚本属于已知路线的工程冒烟，**不是里程碑要求的独立生存通关试玩**。

验证使用 .NET SDK 10.0.201 与相邻源码：DG `dd645c1bde973c82f95885b2fd0a395624ba6edb`、Storage `70b406cd8d1117733eee81a33e7f978399aa98b2`，两仓均干净；本片未修改上游。

当时边界：单角色探索，移动途中不可取消；Spatial 计划通路变更明确拒绝。地图定义由应用版本绑定，不声称任意地图持久化。A 当时没有食水消耗、寒冷或失败结局；这些由下述 B 加入。

### B 生存取舍：2026-10-28

源码入口：[CycleSurvival](../../src/ResourceCycle/CycleSurvival.cs)、[CycleSurvivalRules](../../src/ResourceCycle/CycleSurvivalRules.cs)、`CycleActions`、`CyclePlanning` 和 CLI `PlayView`。实现合同见[生存需求](../design/survival-needs.md)。

每角色锚定三项余量和变化率，以定点积分推导最近阈值；不增加每分钟扣值循环。身体变化与消费、昼夜、到达事实同根提交。低值已提醒和历史待确认分开，补给不会重启原空间行程。生产场景从下午开始且需要采集食物；木材尚无燃料用途，保暖通过庇护停留。

主线程独立复核实现与实际画面，修复了满值消费仍扣物品的遗漏，以及终局仍使用“预计到达”等未来动作描述的问题。死亡保留结束时的未完成活动和空间快照，UI 明示冻结记录。独立审阅未发现剩余阻塞。

自动化边界见 [SurvivalValueTests](../../tests/ResourceCycle.Tests/SurvivalValueTests.cs)、[SurvivalTests](../../tests/ResourceCycle.Tests/SurvivalTests.cs) 与 [SurvivalNeedsViewTests](../../tests/ResourceCycle.Cli.Tests/SurvivalNeedsViewTests.cs)：

- 手算分段积分、任意 tick 分割、上限饱和、长时长与阈值 ceil；严格超过警告线才重新武装。
- 途中饮水不改变 Spatial 对象与到达时刻；昼夜/庇护切换先按旧率结算，回暖仍耗食水。
- 死亡与出口相差 ±1 tick / 同刻的结果、全量与增量轨迹一致。
- 阈值附近真实 S-only 冷开；警告/消费/途中死亡 E-only 恢复不重做 Forecast/Plan/控制器，不重复扣物品或虚构到达，死亡二次冷开仍为 `Exhausted`。
- 满值消费拒绝且状态不变，接近满值仍能消费并裁剪；终局无未来行动承诺。

工程 smoke 使用生产默认规则与真实 Console 进程：

| 路线 | 实际操作与关键边界 |
| --- | --- |
| 溪流—旧桥—公路 | 采一份食物并食用、采水；17:30 途中水警告保存，重开饮水升至 70，待确认仍保留；continue 后仍按原期限 18:45 到达，21:45 获救 |
| 山脊—林屋—信号站 | 采山脊食物并食用、饮水；19:30 到庇护，等待一小时温暖 60→72，食水同时下降；途中再次确认警告，23:30 获救 |
| 原地不补给 | wait 24，先后确认两次警告但不喝水；20:37:30 脱水耗尽，背包仍保留未使用饮水 |

各路线比较连续/冷续局最终观察，终局重开不推进；途中只读/非法输入及 EOF 不改变存档字节，喝水不耗时且不自动确认。继续保留未知目标校验与异地资源历史观察验证。这是已知路线的工程验收，**仍不替代 C 之后的独立完整试玩**。

主线程在最终源码上重跑 A 所列五条命令：Schema Verify 构建零警告/错误；全 solution **480 项通过、无跳过**（Host 2、Inventory 27、Kernel 131、ResourceCycle 157、CLI 72、Spatial 91）。ResourceCycle 现有 18 个模型与 32 条历史：新增 Character v3、EventKind v5、Fact v4、Survival v1；Fact 的版本同步来自值类型 EventKind 的 Schema 变化。应用 GameRevision 为 3，保留旧 Schema 记录，但不支持旧开发游戏存档。

最终工程 smoke 工件：

| 入口 | 成功工件 |
| --- | --- |
| 原批处理 | `artifacts/resource-cycle-smoke/f1e04f2a5f85457ebd19fce394b9c8f0/` |
| 原多人采集 | `artifacts/resource-cycle-play-smoke/8a15aa9e2efe4afea37cf7d70346612c/` |
| 生存 A/B | `artifacts/resource-cycle-survival-smoke/a4481d8f940b425eaad244f3b285a9c3/` |

使用与 A 相同的 SDK/DG/Storage 源码版本；两个上游工作树仍干净，本片未改上游。当前改动文档的 165 个本地文件链接通过存在性检查，`git diff --check` 通过。

## 3. 一期仍需完成

随机环境事件及其确定性/恢复；两条路线在完整生存约束下的独立通关、失败试玩与平衡调整。A/B 工程验收不代表一期完成。二期 LLM、双角色交流和赠与尚未启动。
