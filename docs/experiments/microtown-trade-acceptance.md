# Microtown 居民报价交易切片验收

日期：2026-10-31。范围为指定居民的单资产换单资产、无预留报价、原子接受、拒绝/撤回/到期、可靠终态材料与真实磁盘恢复；本片不等于整个[小镇里程碑](../codex-goals/microtown/README.md)完成。

## 实现与审查结论

- [TownTradeRules](../../src/Microtown/TownTradeRules.cs) 提供 `offer`、`accept`、`reject`、`withdraw`。创建只要求本人当前持有所给资产；接受要求双方同地、存活且严格未到期，双方各有完整支付后再分别计算最终库存/余额。
- 接受一次发布双方变化和终态，内部交换不增加外部收支。满背包可以先付后收；双方结果均必须保留当前生产完成和取消退料的空间。无法结算时不部分转移，也不自动关闭报价。
- [TownTradeState](../../src/Microtown/TownTradeState.cs) 将生命周期名额与普通消息队列分离。世界最多 24 份、每发起人最多 8 份保留报价；已有报价终态不申请新名额。双方首次纳入终态 Opening 后，参与者更晚的 Opening 才可回收；失败方仅豁免等待，不伪造交付。
- [TownEngine](../../src/Microtown/TownEngine.cs) 在失败之后、决策之前处理到期；失败/终局在同一业务转换内关闭相关报价。变化触发响应，报价持续有效本身不轮询唤醒；`continue` 保留活动期限。
- [TownObservation](../../src/Microtown/TownObservation.cs) 只投影本人参与的报价及本人的交付布尔标记。创建/接受提示不探测对方私有库存，接受明确是提交时重验的尝试；[Renderer](../../src/Microtown/TownTextRenderer.cs) 给完整双边条款、截止时刻与可复制命令。

领域、界面与独立验收按不同文件委派，另有只读结算/交付审查。主线程复核实际源码、测试和产品材料，发现并要求修正了旧报价重复标新、交付序号范围校验、重复同一 Opening 回收以及手动终态的到期边界；未以代理成功报告替代验收。

## 工程验证

实际命令及原始输出位于 `artifacts/microtown-trades/`：

```powershell
dotnet build src/Microtown.Cli/Microtown.Cli.csproj -c Release -m:1 -p:DurableGraphSchemaHistoryMode=Publish -v:q
dotnet test DramaBoard.slnx -c Release -m:1 -p:DurableGraphSchemaHistoryMode=Verify -v:q
dotnet test tests/Microtown.Tests/Microtown.Tests.csproj -c Release -m:1 -p:DurableGraphSchemaHistoryMode=Verify -v:q
dotnet build src/Microtown.Cli/Microtown.Cli.csproj -c Release -m:1 -p:DurableGraphSchemaHistoryMode=Verify -v:q
```

首轮 Publish 指出了新增 EventKind 对 Fact/Experience Schema 的连带影响；同步声明版本后生成成功，未手改生成文件。最终两次构建均为零警告、零错误。

全 solution Verify 通过 1268 项、13 个测试项目，无跳过。随后仅补强 TradeState 的手动终态时限与重复 Opening 保护，并定向重跑小镇 124 项全部通过（相对原基线新增 38 项）；没有把这次定向运行写成第二次全 solution 运行。原游戏的领域、CLI、Completion、共享 Goals/控制器/Gym 与核心均已在本轮全量回归中通过。

| 测试入口 | 证明范围 |
| --- | --- |
| [TownTradeStateTests](../../tests/Microtown.Tests/TownTradeStateTests.cs) | 生命周期容量、不可变集合、双边首交付、失败豁免、重复 Opening、交付序号与到期边界 |
| [TownTradeAcceptanceTests](../../tests/Microtown.Tests/TownTradeAcceptanceTests.cs) | 无预留、权限、接受失败无转移、满包换物、双方产出/退料四种容量边界、忙碌去重、普通消息满、到期先于决定、真实饥饿失败与终局关闭、离开后拒绝/撤回 |
| [TownTradeAcceptancePersistenceTests](../../tests/Microtown.Tests/TownTradeAcceptancePersistenceTests.cs) | 实际磁盘只提交接受 E 后中断；Open 保持原 S，RecoverPending 一次结算且不重问，第二次接受拒绝；精确 S fork 排除父未来且子接受不改变父 head |
| [TownTradeObservationTests](../../tests/Microtown.Tests/TownTradeObservationTests.cs) | 条款完整、本人材料冻结、第三方交易与对方私有资产/交付游标隔离、终态不依赖消息容量、位置/到期/busy 命令提示 |

## 实际 Console 试玩

主线程运行独立验收代理准备的 `artifacts/microtown-trades/run-console.ps1 -Released`，使用最终 Release CLI、全新 6 小时存档与 `--gym`。输入来自 `commands.txt`，为脚本操作 Human Console；无模型调用，也不宣称是实际用户游玩。

15 条命令全部成功，未重试或更改脚本。实际轮转为 `a,b,c,a,b,a,b,a,b,a,b,a,b,a,b`：

1. a 向 b 报价一份饭换 5 钱，b 接受；双方财产只变化一次。
2. 第二份报价被 b 拒绝；第三份在 b 等待期间由 a 撤回，唤醒 b，`continue` 保持原 hour 2 截止。
3. hour 2，b 向正在等待的 a 报价，期限一小时；a 响应后 `continue`，仍在 hour 3 完成等待。
4. hour 3 报价先到期，双方均看到 `Expired`。b 正在等到 hour 5，响应后 `continue` 仍保留 hour 5。
5. hour 4 轮到 c，输入结束而暂停。预设终点是 hour 6，但本次有意只验证交易路径，**没有达到游戏终点或完整经济日**。

实际存档统计：现金 `a=23,b=13,c=18`，合计 54；饭合计 3；无进出口、生产、消费或在制品变化。三人饱食均为 48，账实核对通过。接受后的参与者材料显示 a 的饭为 0、b 为 2。

冷开 `:look/:quit` 后完成 S 仍为 `6eba8d0a0e00001411000000`。a/b 私有导出包含实际报价与终态；c 导出不含任何 `trade:a:<n>` 或 `trade:b:<n>` 实例。主线程独立检查了操作回执、实际终态文本、busy 截止、最终账本及 c 导出，独立验收代理另整理 `REPORT.md`。

原始工件：`evidence/configuration.json`（程序集哈希及入口）、`console.output.txt`、`cold-look.output.txt`、`gym-events.jsonl`、`checks.json`、三角色 Markdown 导出及冷开前后分支信息；测试与构建日志在同目录上一级。工件保留在被 Git 忽略的 artifacts，长期行为证据同时由仓内测试和本文描述承载。

## 修订与限制

当前 `GameId=microtown`、S/E `GameRevision=3`。Business、EventKind、Fact、Experience Schema 为 2，Character 仍为 2；新增 TradeAsset/Offer/State/Status Schema 1，共生成八份新 Schema 历史文件。旧开发图无迁移器。

主线程实际尝试打开上一片 revision 2 的 `artifacts/microtown-social/store`：CLI 以 exit 1 明确说明不支持旧数据/Schema，前后十个存档文件的 SHA-256 全部相同，见 `old-save-rejection.log`、`old-save-check.json`。

报价通知有界且不构成长期语义记忆；已关闭记录交付后可回收，长期承诺仍由角色 Goals 保存。没有订单簿、资产托管、复杂组合报价或移动中交互。工程测试包含失败和异常边界，本次产品脚本只走成功命令与自然过期。

接下来仍需 Town owner 预测维护、当前版本三个真实 LLM 居民的完整经济日与反馈/fork 续跑，以及最终里程碑复核。新添[新增游戏接线说明](../implementation/adding-a-game.md)记录现有双消费者边界，不声称第三款游戏或通用交易模块已验证。
