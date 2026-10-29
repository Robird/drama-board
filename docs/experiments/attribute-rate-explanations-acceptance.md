# 属性速率解释实施验收

状态：2026-10-29 已完成工程与独立 Console 试玩验收。范围对应[属性速率解释设计](../design/attribute-rate-explanations.md)：共享领域数据、持久角色观察、Context 交付、两种布局及增量输出；不改变现有生存数值，不建立 Buff 生命周期或通用属性执行引擎。

## 实现合同

| 合同 | 实现与证据入口 |
| --- | --- |
| 一份来源贡献，唯一派生总值 | [RateBreakdown / RateContribution](../../src/ResourceCycle/RateBreakdown.cs) 复制并规范排序，拒绝重复键、非法名称与溢出；同一角色跨属性的同键来源名称必须一致。[模型测试](../../tests/ResourceCycle.Tests/RateBreakdownTests.cs)覆盖零和、不可变集合及结构比较。 |
| 解释参与真实积分，先旧后新 | [CycleSurvival](../../src/ResourceCycle/CycleSurvival.cs) 持久保存三项分解，低层构造与 WithRates 要求显式来源；初始化工厂、昼夜、庇护与预测使用[同一规则](../../src/ResourceCycle/CycleSurvivalRules.cs)。既有[积分测试](../../tests/ResourceCycle.Tests/SurvivalValueTests.cs)、[预测测试](../../tests/ResourceCycle.Tests/SurvivalForecastTests.cs)与[事件测试](../../tests/ResourceCycle.Tests/ObservedEventTests.cs)继续约束数值、限幅及边界顺序。 |
| 同总值换原因必须交付，排列等价不交付 | [上下文测试](../../tests/ResourceCycle.Tests/RateExplanationContextTests.cs)覆盖无数值事件的原因更新、遗漏交付拒绝、伪造旧解释拒绝、交接命令偷改身体拒绝；事实比较、观察比较与必要交付守卫均使用同一结构比较。 |
| 冻结历史与 S/E 恢复 | [Context 持久测试](../../tests/ResourceCycle.Tests/CharacterContextPersistenceTests.cs)加入多来源夹具；旧 Opening 保留旧原因，pending E 安装记录的新原因，不调用控制器。Opening/Items 的结构校验不按当前环境重新求值。 |
| 所有布局与增量共用材料 | [CycleAttributeView](../../src/ResourceCycle.Cli/CycleAttributeView.cs)只读取保存观察；[呈现测试](../../tests/ResourceCycle.Cli.Tests/AttributeRateViewTests.cs)覆盖 decision/classic、同总值换来源、旧 Opening 与最新状态、多来源分组消费者、来源名称字面量。没有整屏位置快照。 |
| 受影响属性一次重绘，限幅依赖完整 | snapshot 按现有 rate key 索引瞬时解释，update 同时选择 value/rate，renderer 合并为一行当前值与完整构成；呈现测试覆盖 99→100、100→90、100 不变但 +6→−12，以及仅范围变化。 |
| 一次性效果与持续速率分开 | 补给、寒风仍通过原事件/效果表达；模型测试验证这些操作保持贡献，既有天气测试约束实际扣减。终局展示冻结解释并说明不再推进。 |

## 实施裁决

公开 API 为 `RateContribution(SourceKey, SourceLabel, Amount)`、`RateBreakdown.Contributions/Total/SameAs`。`CycleSurvival` 和 `CycleSurvivalObservation` 使用 `FoodRate/WaterRate/WarmthRate`；原 `*PerHour` 仅为派生只读访问器。物理低层构造没有可写整数或缺失来源 fallback；`Create` 是明确的初始户外场景工厂。

当前量纲仍为整数点/小时。三属性共用的 `Minimum/Maximum` 由领域常量填入冻结观察；限幅提示从保存值、边界与速率方向派生，不额外持久化有效速率。共享结构位于 ResourceCycle，待第二个真实消费者后再决定提取包。

`GameRevision` 为 10，Survival / SurvivalObservation Schema 为 2，新增两个模型各为 1。生成的模型注册包含它们；不提供旧开发存档兼容路径。

## 验证范围

领域 Release 测试 301 项、CLI Release 测试 257 项全部通过，两次均启用 `DurableGraphSchemaHistoryMode=Verify`。主线程随后运行整个 solution 的 Release 构建（`--no-restore -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify`），零警告、零错误。日志分别保留在本地 `artifacts/rate-domain-tests.log`、`artifacts/rate-solution-build.log`；CLI 测试由负责呈现的子代理运行，主线程核对交付和关键测试源码。本轮没有重跑未受影响的 Kernel/Spatial/Inventory 测试。

独立复核检查了五处语义入口及持久验证；补齐低层构造显式来源、真实磁盘畸形历史拒绝与多贡献恢复证据后关闭复核意见。畸形数据夹具覆盖贡献乱序、重复键、总和溢出和同键跨属性名称冲突，当前物理状态仍合法，确认冷开确实检查历史观察。

独立代理使用真实 Release CLI 完成四个进程会话，退出码均为 0，stderr 均为空。主线程查看了原始输出、实际输入序列及文件散列对比：

- 15:00 营地通过两布局显示白天野外 +6 与食水代谢 −5/−8；delta 饮水先给一次性 +45，再显示持续代谢 −8。
- 经溪流湾采集并补给后进入林屋，经历保留 18:00 从 +6 切为 −12、20:00 抵达后切为庇护 +12；当前解释与历史实际变化分开。
- 冷开林屋存档，只运行 `once/layout/mode/look/exit`；除宿主 `operations.jsonl` 外，所有存档文件 SHA256 完全相同。
- full 模式食物补给 +35 后代谢仍为 −5；离开林屋后在夜间唤醒，温暖先由持续散热 44→32，再被寒风扣为 22；当前只列夜间散热 −12。再次冷开保持相同解释。

本地原始工件位于 `artifacts/rate-playtest/`：输入、stdout、stderr、退出码、交互脚本、`report.md` 与 `preview-file-check.json`。这些忽略目录不是仓库永久证据，复验合同保留在测试中。试玩使用 UTF-8 管道，没有验证 Windows Terminal 颜色；多贡献、同总值换原因、pending E-only 和限幅轨迹由语义测试覆盖，未在正常玩法中虚构新效果。

当前实现不宣称已验证实际 LLM 端点、GUI、部分可见原因或非加法规则。
