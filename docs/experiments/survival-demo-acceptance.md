# 生存逃离一期验收

状态：一期验收完成；二期尚未启动。目标见[里程碑](../design/survival-demo-milestone.md)，组合合同见[实施方案](../implementation/survival-demo-plan.md)。本文件区分代码能力、自动化证据与玩家试玩，不以局部测试代替完整目标。

## 1. 覆盖表

| 里程碑要求 | 当前证据 | 判定 |
| --- | --- | --- |
| Console 新游戏、续局和完整胜负循环 | README 启动与续局命令；A/B/C 工程闭环及 D 真实 Console 独立试玩 | 完成 |
| 两路线成功与一次失败独立试玩 | D 两名独立试玩者分别公路、信号站获救；自然失温及故意脱水失败 | 完成；保留原始输入输出与证据边界 |
| 采集/再生、移动、消费、寒冷、唤醒、随机风险 | 两路线自然覆盖除再生外的机制；SurvivalRegenerationTests 补生产规则下再生及 S-only 冷开 | 完成 |
| 当前/历史观察与未知世界隔离 | DTO、未知身份探针、离场库存记忆、种子隐藏；D 干木提示仅局部可见 | 完成；相关测试最终回归通过 |
| 固定随机状态复现、移动/阈值/随机 E-only 恢复 | 移动/阈值/天气 S-only，以及移动/警告/消费/死亡/天气 E-only；D 真实 Console 重开与再生冷开 | 完成；无需重做 Plan、抽样或消费 |
| Schema、相关测试与原 Console 回归 | D 最终 Verify、511 项测试、三种 Console smoke | 完成，详情见 D |
| 运行说明与剩余限制清晰 | README、三份试玩报告、本页局限与 PROJECT-STATE | 完成；二期未启动 |

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

### C 随机天气：2026-10-28

源码入口：[CycleEnvironment](../../src/ResourceCycle/CycleEnvironment.cs)、`CycleExpedition.Environment`、`CyclePredictionMaintenance`、`CycleFactApplication` 与 [天气 UI 测试](../../tests/ResourceCycle.Cli.Tests/SurvivalWeatherViewTests.cs)。[天气合同](../design/survival-weather.md)明确到达先于天气、连续耗尽最先、天气致死同事实，以及随机只在选中 Plan 时计算。

复用 Kernel `DeterministicRandom`，持久 seed/sequence/NextDue 与最近个人影响。每两小时二元结果，寒风使暴露者损失 10 点温暖，庇护免损；平静或庇护窗口仍推进序号。最近记录保存当时的实际损失和庇护状态，不按新位置重算。独立审阅发现并修正了普通时刻 `NextDue=null` 会静默停天气的漏洞，现在仅允许时间或序号耗尽时停止预测。

[WeatherTests](../../tests/ResourceCycle.Tests/WeatherTests.cs) 的实际运行证据包括：

- 固定 seed 0 的四窗向量为寒风/平静/平静/寒风，seed 1 首窗平静；庇护与暴露抽到相同结果，效果不同。
- 观察、重建、校验与非法命令不推进随机地址；同一输入的增量/full 轨迹、cursor 和空间一致。
- 庇护/出口到达与天气相差 ±1 tick / 同刻；连续耗尽抢先；天气→活动完成→警告→输入的顺序。
- 天气窗口前一 tick 与恰好到期的 S-only 冷开保持同一下一机会。
- 真实 E-only 截断用 seed 0：非致死 29→19、致死 9→0。恢复禁止 Forecast/Plan/控制器，序号恰推进一次，实际损失不重复，二次冷开一致；致死仍 `Exhausted`。平静和受庇护的记录有正常 Kernel 测试，不将它们冒充已单独做过 E-only 截断。
- 原始 DG 发布的普通时刻无 NextDue 状态能还原 Schema，但正常 Open 拒绝；合法时间/序号耗尽边界允许没有下一窗口。

主线程实际检查新开画面与终局画面：未知地点不泄漏，初始不同 seed 的观察相同，窗口提示与已发生结果分开；行动的确定性估计注明不包含未来寒风；终局不再提示下一窗口。实时播报使用已提交事实，冷开完整观察从状态恢复最近记录。

生产默认 seed `20261028` 的工程脚本现在实际经历 17:00 寒风、19:00 平静、21:00 寒风、23:00 平静：

| 路线 | C 实测变化与结局 |
| --- | --- |
| 溪流—旧桥—公路 | 17 点暴露损失 10；21 点再次受风后温暖 22，行程仍定于 21:45；确认后获救 |
| 山脊—林屋—信号站 | 19:30 到林屋时温暖 50，停留改为两小时；21 点同一阵寒风被庇护挡住，21:30 温暖 74，次日 00:30 获救 |
| 原地不补给 | 天气仍开启，忽略食水警告后在 20:37:30 脱水失败 |

脚本比较天气结果后的只读冷开、警告/行程保持、两路线与失败的连续/续局最终观察及存档 hash。主线程重跑最终版本的三组 smoke：

| 入口 | 成功工件 |
| --- | --- |
| 原批处理 | `artifacts/resource-cycle-smoke/c32171e42a0149e9b96e86dfaa1fa962/` |
| 原多人采集 | `artifacts/resource-cycle-play-smoke/0cdbc11c0ad549efa5d697e414425fd0/` |
| 生存 A/B/C | `artifacts/resource-cycle-survival-smoke/f3b0091a564b4b4a8fa4adda4bcf8982/` |

最终 Schema Verify 构建零警告/错误；全 solution **503 项通过、无跳过**（Host 2、Inventory 27、Kernel 131、ResourceCycle 175、CLI 77、Spatial 91）。ResourceCycle 19 个当前模型、36 条历史；新增 Environment v1、Expedition v2、EventKind v6、Fact v5。应用 GameRevision 为 4。SDK/DG/Storage 与前片一致，上游仍干净，未作修改。

### D 独立试玩与体验收敛：2026-10-28

三个独立工作包只读 AGENTS、PROJECT-STATE 和实际 Console 输出，未预读源码、地图、工程 smoke 或彼此报告。两个成功试玩者分别选择起点不同方向，逐次看画面后输入；第三人专门验证故意失败。使用 C 完成后的 Release 二进制与独立新存档，不把已知路线脚本计入独立通关。

| 试玩 | 选择、结局与恢复证据 |
| --- | --- |
| [高处窄径](survival-playtest-ridge.md) | 首局成功；采两份食物，使用初始水，在林屋采木半小时并等待一小时回暖，D1 00:30 在信号站获救。途中未确认食物警告时退出重开，原到达时刻不变。 |
| [溪水方向](survival-playtest-stream.md) | 首局采三食三水，夜行遇寒风，D0 22:50 失温失败；第二局只采一食、使用初始水并及时赶路，D0 21:30 在公路获救。两局都有途中警告冷开，第二局还验证补水后历史警告与原行程保持。 |
| [故意不补给](survival-playtest-failure.md) | 原地 wait 24，故意忽略两次有合法补给/取消机会的警告，D0 20:37:30 脱水，符合画面预告；终局两次冷开文本相同，不再进入命令循环。此例不作为普通玩家失误率。 |

原始输入输出副本随各报告纳入 Git。存档、驱动和工程 smoke 工件留在本地 artifacts；报告中的记录器退出异常明确归属外部 driver，没有误记为产品失败。主线程核对原始命令、两次成功终局、失温及脱水结局和重开记录；两条成功策略确有不同，不以同一脚本的重跑凑数。

本轮据实际困惑做三项窄修正：

- 身体摘要和行动风险共用已知昼夜分段估计，不再让白天当前变化率被误当成整晚预算。条件是保持当前暴露/庇护、不补给、不计未来随机天气；移动按途中暴露估计，不预知目的地庇护。
- 干木说明只在可见资源或自有背包中出现，明确当前不能生火取暖；保暖仍依靠庇护停留，不新增燃料系统。
- 低温历史警告与食水补给区分，途中明确不能取消、食水不能补温；当前能否回暖取决于暴露和庇护。

没有改变资源数值、地图、天气概率或胜负规则。GameRevision 仍为 4，Schema 无变化。

定向补证见 [SurvivalRegenerationTests](../../tests/ResourceCycle.Tests/SurvivalRegenerationTests.cs)：生产需求与天气均开启，8 点出发至溪流、两次采集、消费、等待；9:30 首次减少库存，10 点第二份不重置再生时钟，经过 10/12 点天气后 12:30 自然恢复一份。断言资源 4→5、角色食物仍为已采两份减已吃一份；真实 S-only 冷开比较完整状态、Spatial 和 cursor，继续至 13 点等待完成与连续路径一致。

[SurvivalForecastTests](../../tests/ResourceCycle.Tests/SurvivalForecastTests.cs) 比较日落估计与真实无天气 Kernel 死亡，检查残夜→白天→完整夜、庇护下食水耗尽、零温暖不复活，以及重复查询不改变身体、预测或天气状态。CLI 回归检查估计条件与干木/低温提示的可见性。

主线程实际运行 A 所列 Verify 构建、全 solution 测试和三组 smoke。构建零警告/错误；**511 项测试通过，无跳过**（Host 2、Inventory 27、Kernel 131、ResourceCycle 181、CLI 79、Spatial 91），含上述新增组合及查询测试。ResourceCycle 仍为 19 个当前模型、36 条历史。SDK 与 DG/Storage 源码版本与 C 一致，上游工作树干净、未修改。

| 最终回归入口 | 成功工件 |
| --- | --- |
| 原批处理 | `artifacts/resource-cycle-smoke/9200459b6ef643b582d4fb2c72cae571/` |
| 原多人采集 | `artifacts/resource-cycle-play-smoke/4b8a3487a31140f999dfcfaed598f851/` |
| 生存 | `artifacts/resource-cycle-survival-smoke/b300c193ef3942c7ba4d1fdecc1c3755/` |

随后两位试玩者以已知路线做修正后复查，明确不再计入盲测：林屋采木前已显示用途限制；溪流路线 17:30 的身体摘要改为 23:40，18:30 摘要与行动风险同为 23:40，21 点低温提示明确无物品补温手段。两人再次获救，最终数值与各自原策略一致，详见各报告末尾。独立 reviewer 对估计上界、信息可见性及组合测试完成静态复核，主线程结合真实测试和 Console 结果验收。

生存 smoke 只证明工程闭环；其 JSON 范围说明由 `remainingMilestoneEvidence` 更名为 `notCoveredByThisSmoke`，避免把“该脚本未覆盖”误写成“一期仍未完成”。上列运行工件保留旧字段原貌，独立试玩完成状态以本页及原始记录为准。

## 3. 剩余限制与后续方向

- 两位独立试玩者都是 Coding Agent，经普通 Console 输入输出扮演玩家；尚无人类外部用户研究。成功记录覆盖默认种子的一条天气序列，不代表所有种子或所有策略可通关。
- 完整 Markdown 每步重复，较长等待的风险说明会占多行；这是已记录的后续 UI 改进机会，不妨碍本次独立理解与通关。
- 一期没有途中掉头、移动取消、携带保暖或生火系统；干木暂无消费用途。冒险进入长路段后，低温警告可能只能帮助理解风险，无法现场挽救；通路开始前已显示耗时、昼夜估计和额外寒风风险。
- 无 GUI、真实 LLM/API、双角色生存、对话、赠与或笔记。这些属于二期，尚未启动；当前恢复证据不涵盖外部调用恢复。
