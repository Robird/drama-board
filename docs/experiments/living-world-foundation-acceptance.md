# 山野镇首片：连续世界与宿主验收

> 历史命名说明：本文保留当时的 ResourceCycle / Cycle 类型、命令与验证记录。当前应用已重命名为 Playground；现行入口与持久身份边界见[重命名说明](../../docs/implementation/playground-rename.md)。旧名称源码链接固定到重命名前的 Git 版本。

2026-11-01，Windows / .NET SDK 10.0.201。来源为基线 `fd8e3ea` 上、本文件所属提交的首片工作树；GameRevision 19，ScenarioId `hillside-v1`。这是融合的第一片，不是 C1–C8 总验收。

## 已验证的变化

- ResourceCycle 是唯一生活世界根；新增住处、市场、工坊与镇口—营地捷径。默认三居民、有限补给、身体需求和天气开启；两个旧出口都是可返回地点，不再退休角色。
- 产品入口显示山野镇 / Hillside。默认共用 Human Console，`--llm` 控制全部居民，`--human ID` 指定一个 Console 角色；启动/冷开可改变绑定。
- `--steps` / `--until-hour` 只暂停宿主。恢复已发布 pending 优先于预算，恢复不重问控制器；再次冷开不重复结算。
- 混合终端不打印模型角色的 Context、Goals、handoff、原始操作及回执；完整记录仍在独立审计/Gym，合法大喊仍进入 Human 角色材料。

## 工程证据

所有 dotnet 操作串行，Release、`-m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify`。

| 范围 | 结果 | 证据 |
| --- | --- | --- |
| CLI 构建与 Schema Verify | 通过，零警告/错误 | 本地 `artifacts/living-world-foundation/build-cli.log`；随后 CLI 测试构建包含隐私修复 |
| ResourceCycle.Tests | 492 通过，零失败/跳过 | `artifacts/living-world-foundation/domain-final.log` 及 `domain-final/*.trx` |
| ResourceCycle.Cli.Tests | 原完整组 368 通过；之后新增 pending 预算交叉用例单独 1 通过 | `cli-final.log`、`cli-final/*.trx`、`pending-budget-final.log`、`pending-budget-final/*.trx` |
| ResourceCycle.Completion.Tests | 45 通过，零失败/跳过，离线 fixture | `completion-tests.log`、`completion-results/*.trx`；过滤 LiveE2E / LocalE2E |

新增领域证据见 [HillsideFoundationTests](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Tests/HillsideFoundationTests.cs)：普通命令完成两次镇山往返，中间磁盘冷开，保留身份、身体、补给、探索和材料；每步比较 Rebuild 预测。两次都开启需求与天气。旧出口测试改为活着抵达/返回；原死亡冻结、隐私、E-only、天气和同刻边界覆盖保留。

新增宿主证据见 [LivingWorldHostTests](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Cli.Tests/LivingWorldHostTests.cs)：真实宿主配离线 Completion fixture，验证三角色绑定、选择 b、冷开重绑定、预算和隐私。pending 用例通过普通采水构造只发布 E 的中断窗口，事件时刻在 Until 之后，核对水/井库存、游标、预测与 Gym 恢复记录，零模型调用；第二次冷开完成提交 ID 不变。

最初两组测试各有一条旧文案断言失败，修正为新背景或实际世界/人数校验后通过。新增 pending 用例首次编译触发 xUnit2029，按分析器改为 DoesNotContain 后通过；没有通过跳过测试掩盖失败。

## 实际入口：Agent 操作 Human Console

这是带目标的入口验收，**不是自由游玩，也没有真实 LLM 调用或真人参与**。直接运行 Release DLL，经终端逐步输入普通命令；未修改存档或补发资源。

```powershell
dotnet src/ResourceCycle.Cli/bin/Release/net10.0/DramaBoard.ResourceCycle.Cli.dll --characters 1 --store artifacts/living-world-foundation/console-world --steps 200
```

操作顺序：`go market-home` → `go market-road` → `go road-camp` → `go trail-1` → `gather berries 1` → `gather water 1` → `use item.water` → `use item.food` → 按原路返回 → `:quit`。

观察到 D0 08:00 从住处出发，10:30 到溪流，11:15 完成两种采集，13:45 返回住处且仍活跃。最终食物 86.25、水分 79；各采一份并用一份，背包食物/饮水均回到初始三份，未触发回镇补满机制。

第二个实际进程省略人数、打开同一存档，输入 `:look`、`:quit`；输出确认 D0 13:45、住处与上次回合材料已恢复，退出码 0。

本地原始工件（不包含凭据）：

| 文件（均在 `artifacts/living-world-foundation/` 下） | SHA-256 |
| --- | --- |
| `console-world/operations.jsonl`，包含两进程的 15 次原始输入及结果 | `365E73295EA21197AFF31D2102933BDD08E42584450A183727EE92E5A38952D6` |
| `console-cold-open.log` | `464B2315FEC9FD7A87C1A13183BE7181A69F6F231AE052EFEFAA5651A44D941E` |
| `console-partial-transcript.txt`，08:30 起 PTY 输出，保留终端折行痕迹 | `7DF90F8FA98CD1E5F8127CA4263727892D4BFF9FFDE26B6E970933CF536F6049` |

## 边界

生产、现金、市场、居民报价交易、完整内容指纹/变体、工程更名与旧产品归档尚未完成。没有三日真实多模型历史，没有融合后的实际混合会话，也没有两次自由游玩。以上必须继续完成，详见[工作计划](../codex-goals/living-world/work-plan.md)。旧原型的成功不替代新世界证据；本轮未运行整仓 solution 测试或 Linux 验证。
