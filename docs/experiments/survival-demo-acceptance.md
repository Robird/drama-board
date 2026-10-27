# 生存逃离一期验收

状态：施工中；尚未完成一期。目标见[里程碑](../design/survival-demo-milestone.md)，当前切片见[实施方案](../implementation/survival-demo-plan.md)。本文件区分代码能力、自动化证据与玩家试玩，不以局部测试代替完整目标。

## 1. 覆盖表

| 里程碑要求 | 当前证据 | 判定 |
| --- | --- | --- |
| Console 新游戏、续局和完整胜负循环 | A 已有探索、采集与获救；尚无生存失败 | 部分完成 |
| 两路线成功与一次失败独立试玩 | 待生存与风险规则落地后执行 | 未完成 |
| 采集/再生、移动、消费、寒冷、唤醒、随机风险 | A 移动与原采集组合已验证；需求和风险待做 | 部分完成 |
| 当前/历史观察与未知世界隔离 | A 的 DTO、校验探针、历史库存与 Console 通过 | A 范围完成；后续新增信息继续验证 |
| 固定随机状态复现、移动/阈值/随机 E-only 恢复 | 移动中 S-only、出发/到达/出口 E-only 通过；尚无生存与随机 | 部分完成 |
| Schema、相关测试与原 Console 回归 | A 的 Verify、整套测试和三种 Console smoke 通过 | A 范围完成；后续持续回归 |
| 运行说明与剩余限制清晰 | README 已增加探索入口，本文记录限制 | A 范围完成 |

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

边界：单角色探索，移动途中不可取消；Spatial 计划通路变更明确拒绝。地图定义由应用版本绑定，不声称任意地图持久化。没有食水消耗、寒冷、随机风险、失败结局或 LLM；这些仍是一期/二期后续工作。

## 3. 一期仍需完成

食物、水、寒冷、危险响应、随机环境事件及恢复；两条路线在生存约束下的独立通关、失败试玩与平衡调整。当前空间探索成功退出即使验收通过，也只证明切片 A，不宣称一期已经具备完整生存体验。二期 LLM、双角色交流和赠与尚未启动。
