# 山野镇居民报价验收

> 历史命名说明：本文保留当时的 ResourceCycle / Cycle 类型、命令与验证记录。当前应用已重命名为 Playground；现行入口与持久身份边界见[重命名说明](../../docs/implementation/playground-rename.md)。旧名称源码链接固定到重命名前的 Git 版本。

2026-11-01，基于 `44ddf70` 后的本提交工作树，Windows / SDK 10.0.201 / Release，GameRevision 21。本片补齐 [C2/C4](../codex-goals/living-world/README.md) 的居民交易，未完成整个 Goal；剩余内容绑定、产品归档、Gym 与真实模型运行见[工作计划](../codex-goals/living-world/work-plan.md)。

## 实现范围

`offer`、`accept`、`reject`、`withdraw` 支持资金与物品两腿交换。报价不预留资产，接受重新检查双方完整支付额、余额溢出与生产完成/取消容量；一次事实记录全部资产与终态，不改变原活动、截止、跟随和提醒。只有发起报价消耗发送额度。

报价 ID 包含角色、Context、操作序号；固定 trades owner 跟踪最早期限，同刻到期先于输入。自然死亡与天气同时多人死亡都关闭相关报价，幸存参与者收到合并的交易终止材料，不获得远方死亡地点或身体信息。

只有参与者能看到条款；内部交付标记不进入角色材料。可见候选检查不探测对方私产，实际提交才全量检查。终态在实际 Opening 交付后保留至后续回收；若回收改变另一方仍打开的材料，同一事实追加非操作观察项，不伪造操作、经历或决策代次。

源码入口：[交易状态](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/src/ResourceCycle/CycleTradeState.cs)、[规则](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/src/ResourceCycle/CycleTradeRules.cs)、[独立事实校验](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/src/ResourceCycle/CycleTradeValidation.cs)、[材料交付](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/src/ResourceCycle/CycleInteractionRules.cs)、[Console](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/src/ResourceCycle.Cli/TradeView.cs)。完整合同见[经济设计](../codex-goals/living-world/economy.md)。

## 工程验证与独立审查

串行运行同一构建图；Publish 新增 12 个 Schema 历史记录，后续 Verify 核对 67 个当前 Schema / 175 个历史记录。构建 0 警告、0 错误。

| 最终检查 | 结果 |
| --- | --- |
| ResourceCycle.Tests 完整运行 | 521 通过，0 失败/跳过 |
| ResourceCycle.Cli.Tests 完整运行 | 396 通过，0 失败/跳过 |
| ResourceCycle.Completion.Tests，排除 LiveE2E/LocalE2E | 45 通过，0 失败/跳过；离线证据 |

新领域测试 16 项来自[独立验收](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Tests/HillsideTradeAcceptanceTests.cs)与[规则/反例回归](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Tests/HillsideTradeTests.cs)：普通操作成交、E-only 不重 Plan/控制器、重复提交、旁观者拒绝、无预留与完整支付、生产双结局容量、到期优先、远方死亡、跨 Context 身份、伪造条款/交付/额外资产、途中限制、完整终态回收、天气同时两人死亡和批量到期。每步对照增量预测与 Rebuild，独立核对资产账。

新 CLI 测试 12 项覆盖双布局条款/方向/终态/私有资产、真实宿主配离线 Completion fixture 的报价与接受、生产中接受、精确冻结材料冷开。它们不代表真实模型合作。

独立审查与首次测试实际发现并修复：无身体角色作为远方对方时漏刷 attention owner；终态回收未刷新另一方 Open Context。另将测试的“可见检查允许尝试”与“权威提交成功”分开，没有通过泄露对方私产修正测试。新增反例覆盖精确 Fold，守恒不代替合法性。最终独立复核未发现剩余阻断。

命令与结果留在本地 `artifacts/living-world-trades/` 的 `build.log`、`domain-full`、`cli-full`、`completion-full` 及对应 TRX。复验测试使用 `-c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify`。未运行 Linux 或整仓 solution 测试。

## 实际 Console 与冷开

```powershell
dotnet build src/ResourceCycle.Cli/ResourceCycle.Cli.csproj -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
pwsh -NoProfile -File scripts/Smoke-Hillside.ps1 -Scenario Trade
pwsh -NoProfile -File scripts/Smoke-Hillside.ps1
```

Trade 场景使用实际 CLI 与两居民默认新局，第一进程 a 发报价、第二进程 b 冷开接受；随后通过普通命令拒绝第二份报价，b 开始等待，a 撤回第三份报价。7 条领域命令全部成功并有配对操作 ID、DomainEvent、WorldVersion。a 用一份水换得 2 钱，双方资金分别 14/10，总钱仍为 24；拒绝与撤回不转移资产。结束时仍为 D0 08:00，b 的等待截止保持 D0 09:00。

原始输入/输出/错误及哈希保存在本地 `artifacts/living-world-trades/console-smoke/`，两个进程标准错误均为空。这是预编路线的工程验收；没有真实 LLM 调用、自由游玩或真人参与。世界事实以 E/S 为权威，宿主日志仅提供操作执行证据。

```text
CLI SHA-256:        D65D74C36BC265D350904601BCA2188E583788AC59A1D95F83901A1BB02533F6
operations SHA-256: 092FC9243F0325FFF24E81D5FCD671525E100EA6F749BE717482F0E13E377EAB
```

默认 Economy 场景在同一最终构建上另开单居民世界，39 条普通命令全部成功，复验[前片](living-world-economy-acceptance.md)的两次镇山出行、加工、消费、市场与中途冷开。证据位于 `artifacts/living-world-trades/economy-regression/`，operations SHA-256 为 `3A193DF476AB4552A78EBA41A8705CA596CE503C2AF1D5E9D6125F3E5ACA37C0`。该路线与 Trade 世界分别留证，不能把不同世界时长相加作为 C7 三日证据。
