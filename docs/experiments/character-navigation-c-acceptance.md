# 角色地图与导航：切片 C 验收

日期：2026-11-03。范围为 [导航设计](../design/character-navigation/design.md)的逐段执行、跟随组合、提醒与恢复。A/B 的入口知识和求路合同继续保留；完整 Goal 的预取隔离与真实 LLM 组合验收仍待 D。

## 实现结果

`navigate <路径ID>` 在实际起点启动本人当前候选。路径 ID 已失效、属于他人、起点不符、有耗时活动或正在跟随时拒绝；不会自动换路线或移动到起点。零段在真实目标位置给完成回执，不放行时间、不创建导航。

Expedition 保存 `PlaygroundNavigation(Path, CompletedSteps)`；活动路线持有选中的不可变 PlaygroundPath。候选替换不会修改活动路线，位置和期限继续由 Spatial 唯一维护，没有新增导航计时器、线程或宿主缓存。

普通 go、navigate 首段、导航中间点 continue 共用单段入口解析、成员校验、风险预览与出发规划。到中间点先交付观察并等确认；继续失败保留全队位置、导航和提醒，不自动改道。期间 map/path 及允许的即时交互不隐式移动；其他独立耗时动作要求先取消导航。

`cancel` 有活动导航时只清余下计划，保留已开始的路段、期限、队伍关系和待确认提醒；无导航时继续沿用普通活动取消。途中取消只在现有决策窗口操作，随后补给不确认旧警告，continue 才确认并完成当前段；到点不再接续。

本人匹配的完整到达才记录一步进度，终点清除活动导航。领队每段使用原子队伍出发，跟随者不继承导航或路线知识；同刻逐人到达的中间状态可恢复，控制器必须等全队结算。死亡只清本人导航/候选；领队死亡解除跟随，幸存者只完成当前段。

主要源码：[Navigation/Rules](../../src/Playground/PlaygroundNavigation.cs)、[Planning](../../src/Playground/PlaygroundPlanning.cs)、[InteractionRules](../../src/Playground/PlaygroundInteractionRules.cs)、[NavigationView](../../src/Playground.Cli/NavigationView.cs)。当前游戏修订 **25**，新实验使用新的 home/store；没有旧开发存档兼容承诺。

## 双视角审阅

工程审阅发现旧 `AttentionAcknowledged` / `ActivityCancelled` 事实可能绕过导航语义清除提醒，已加本地因果守卫：在点 continue 必须出发、途中 continue 只能确认、有导航 cancel 必须清导航。正常入口和恢复校验保持同一合同，不通过 Fold 重跑寻路或控制器。

体验审阅抽查实际 Console 后修正了历史与当前语境：Opening 只说明“交付时”的导航/跟随/活动状态，不能在取消后的历史区仍发“本次 continue 出发”的指令。通信、身体警告和交易帮助也不再用旧固定期限说明覆盖导航中间点含义。候选和活动路线各自显示 ID、终点、进度及可操作入口；两布局、delta、冻结 Context/history 与 LLM 指导同源。

## 自动验证

| 验证 | 本次结果 |
| --- | --- |
| Release 全 solution、warnings-as-errors、Schema Verify | 0 warning、0 error；`c-build-final.log`。新 schema 历史由 Publish 工具生成后 Verify。 |
| 全 solution 测试 | 10 个程序集、1671 项全部通过，0 失败/跳过；`c-tests-final.log` 与 `c-tests-final/*.trx`。 |
| 实际 Console 两条轨迹 | 11＋14 个普通操作全部成功，续局与 history 导出通过；最终目录的 `verification.json` 保存语义断言和 CLI SHA-256。 |

原始证据位于本地忽略目录 `artifacts/character-navigation/`。首次构建遇到一处新测试的 xUnit 分析器错误；首次全测发现五处测试误假设队员到达顺序、两处旧文案断言。修正为真实首个到达边界、按位置断言本人知识、禁止控制器介入剩余到达，未修改生产仲裁来迎合测试；最终上述完整构建和全测重新运行通过。

```powershell
dotnet build DramaBoard.slnx -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
dotnet test DramaBoard.slnx -c Release --no-build --no-restore -m:1 --logger trx --results-directory artifacts/character-navigation/c-tests-final
```

- [NavigationTests](../../tests/Playground.Tests/NavigationTests.cs)：逐站确认、候选替换保持活动路线、途中取消/补给/确认、零段与错起点、跟随隐私、三种出发风险预览相同、成员不能出发时全队与提醒守恒、旧/他人路径 ID 拒绝、领队/队员死亡。
- [NavigationRecoveryTests](../../tests/Playground.Tests/NavigationRecoveryTests.cs)：启动、半队到达、待确认、下一段出发、途中取消五类边界各自 S 与 E-only/pending；实际关库重开、禁止控制器、恢复幂等。半队恢复先结算剩余到达，取消恢复后只完成当前段。错误进度、其他角色/队员到达推进、空间错位和旧事实绕过均有明确拒绝。
- [ActiveNavigationMaterialTests](../../tests/Playground.Cli.Tests/ActiveNavigationMaterialTests.cs)：实际探索后启动多段导航，中间查询替换、继续抵达、取消和零段材料；两布局/full/delta/Context，以及取消后的历史语境。

## 实际 Console 证据

两条轨迹均使用新 home、gentle 内容、单角色 a=Human Console、Gym record。只通过角色命令产生世界状态，不修改存档、不注入角色属性；模型调用为 **0**，这是代理工程试玩，不是实际人类自由试玩。

第一条先走家→工坊→市场，求回家的两段路线。navigate 抵达工坊后停下，查询去市场的新候选，再 continue；实际仍沿原活动路线回家。第二次导航在工坊 cancel，然后查询并启动本地零段，续局与 history 导出正常。共 11 个普通角色操作，证明中间点确认、候选/活动分离、取消和零段不自动出发。

第二条先探索家→工坊→市场→镇口→营地→溪边→狭谷，再返回营地并 wait 3；此时 18:15、水分 28.75。求营地→溪边→狭谷的两段路线并 navigate，19:00 在第一段途中因水分到 25 唤醒。执行：

```text
:look
cancel
:look
use item.water
:look
continue
:quit
```

cancel 后导航清除，路段仍约定 19:15 抵达；补水到 70 后历史警告仍待确认。continue 确认后抵达溪边，未继续去狭谷；冷开仍无活动导航。共 14 个普通操作，保留每次输入、输出、结构化回执及人物 history。

最终产物目录为 `c-console-final` 和 `c-warning-console-final`，各自保存逐进程 stdin/stdout/stderr、`operations.json`、`verification.json`、`history-a.md` 与独立 home/store。一般导航轨迹验证候选替换后原路线仍回家、取消后停工坊、零段不移动及历史不发当前指令；警告轨迹验证取消/补给后仍待确认、19:15 到溪边且不续走。首次轨迹保留于 `c-console-01` / `c-warning-console-01`，供审阅过程追溯。

## Goal 剩余工作

本片支持 N5 执行侧和 N6–N10 的导航合同，但不是全 Goal 完成证据。D 仍需实际 lookahead 分支发现知识/求路/推进导航后丢弃、正式响应只提交一次与输入匹配审计；真实 LLM 使用完整新命令；最终 N1–N12 逐项证据审计，以及可直接试玩的独立 home/命令。代码存在、自动测试通过和代理 Console 运行不替代这些要求。
