# 山野镇生产与市场验收

> 历史命名说明：本文保留当时的 ResourceCycle / Cycle 类型、命令与验证记录。当前应用已重命名为 Playground；现行入口与持久身份边界见[重命名说明](../../docs/implementation/playground-rename.md)。旧名称源码链接固定到重命名前的 Git 版本。

2026-11-01，基于 `85f82cd` 后的本提交工作树，Windows / SDK 10.0.201 / Release，GameRevision 20。本文证明生产与市场纵切，不宣称 [C1–C8](../codex-goals/living-world/README.md) 全部完成。居民报价交易、内容指纹、最终产品归档及真实模型生活仍见[工作计划](../codex-goals/living-world/work-plan.md)。

## 实现与不变量

- 角色现金、有限日供应/收购市场、口粮和木器生产、赠钱进入同一 Cycle 世界根。山林采集物进入生产与消费；资金和物品总账随事实原子结算。
- 生产开始托管原料，完成消耗原料并交付全部产物；取消或死亡退料。接纳物品时分别检查完成和退料两个互斥结果，不相加预留。
- 市场只在当地开放，配方只在工坊可见；本人现金与生产信息进入冻结 Context，其他居民的现金/背包不泄漏。两种 Console 布局及模型材料均已接线。
- 通信、补给、赠钱不改变进行中的生产期限，不隐式解散跟随。日市场更新由固定预测 owner 负责，冷开重建，不累积旧日未用额度。
- 独立审查发现并修复两个事实校验缺口：伪造 Visit 不能借市场刷新获取远处地点知识；ItemUsed 不能凭守恒正确就附带超量恢复或错误属性变化。新增反例测试覆盖这些情况。

实现入口：[经济规则](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/src/ResourceCycle/CycleEconomyRules.cs)、[经济校验](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/src/ResourceCycle/CycleEconomyValidation.cs)、[事实应用](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/src/ResourceCycle/CycleFactApplication.cs)、[内容](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/src/ResourceCycle/LivingWorldEconomyContent.cs)、[呈现](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/src/ResourceCycle.Cli/EconomyView.cs)。设计合同见[经济迁入](../codex-goals/living-world/economy.md)。

## 工程验证

同一构建图串行运行；Schema Publish 生成 20 个新历史记录，后续 Verify 验证 63 个当前 Schema / 163 个历史文件。构建 0 警告、0 错误。

| 检查 | 实际结果 |
| --- | --- |
| ResourceCycle 领域完整测试 | 505 项中 496 通过、9 失败；失败均来自旧速率解释夹具缺少正常 Observe 阶段，与新增 Visit 校验冲突。 |
| 修复夹具后的 RateExplanationContextTests | 整类 12 项全部通过，覆盖上述 9 项；仅修夹具接线，原断言保留。没有把分批结果表述为一次完整运行全绿。 |
| 新经济测试 | 完整领域运行中全部 13 项通过：独立验收 7 项、领域反例与规则测试 6 项。 |
| ResourceCycle.Cli.Tests | 最终完整运行 384 项全部通过。 |
| ResourceCycle.Completion.Tests | 排除 LiveE2E/LocalE2E 后 45 项全部通过；属于离线证据。 |

独立[经济验收测试](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Tests/HillsideEconomyAcceptanceTests.cs)使用普通领域命令覆盖重复出行、守恒、生产 pending 恢复、市场同刻竞争与容量；[领域测试](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Tests/HillsideEconomyTests.cs)补充拒绝与伪造事实反例。CLI 的经济视图与宿主测试验证局部材料、隐私和活动保持。

复验命令（先构建，再串行运行各测试项目）：

```powershell
dotnet build src/ResourceCycle.Cli/ResourceCycle.Cli.csproj -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
dotnet test tests/ResourceCycle.Tests/ResourceCycle.Tests.csproj -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
dotnet test tests/ResourceCycle.Cli.Tests/ResourceCycle.Cli.Tests.csproj -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
dotnet test tests/ResourceCycle.Completion.Tests/ResourceCycle.Completion.Tests.csproj -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify --filter 'Category!=LiveE2E&Category!=LocalE2E'
pwsh -NoProfile -File scripts/Smoke-Hillside.ps1
```

本次日志和 TRX 保存在本地 `artifacts/living-world-economy/`：`build-publish.log`、`domain-full`、`rate-final`、`cli-final`、`completion-final`。未运行 Linux 或整仓 solution 测试。

## 实际 Console 两次出行

[Smoke-Hillside.ps1](../../scripts/Smoke-Hillside.ps1)使用实际 CLI、普通命令和同一存档的两个进程；每进程最多 45 秒，证据目录必须为空，不覆盖旧记录。它核对全部命令的顺序、成功结果、ToolCallId 及 DomainEvent/WorldVersion 引用。不是通过直接写库存或存档制造循环。

实际执行 `-OutputDirectory artifacts/living-world-economy/console-smoke`：39 条普通命令全部成功，两个进程标准错误均为空。第一次在工坊加工、市场买卖后去溪流采集并返回住处；第二次冷开后再进山采集、回镇加工消费和出售，D0 23:15 活着回到住处。两次均保留身体需求与正常天气规则。

| 项目 | 可核对的账 |
| --- | --- |
| 钱 | 初始 12 + 出售 10 − 购买 10 = 12 |
| 食物 | 初始 3 + 购买 3 + 采集 4 − 生产投入 4 − 消费 3 = 3 |
| 水 | 初始 3 + 购买 1 + 采集 2 − 消费 4 = 2 |
| 口粮 | 生产 4 − 消费 2 − 出售 2 = 0 |

原始输入、输出、错误与 `result.json` 保存在上述本地目录。SHA-256：

```text
CLI:        7A6468AFA653B892B2D65FB7A4C593D0746DF8E32D5B4E2C05F52A800C925393
operations: 87A1BF8D7BAD0FE60A2A79F50F364D6620AE2E27C30D3F90826B23DDC67D94FE
```

这是预编路线的工程验收，未进行真实 LLM 调用、自由游玩或真人验收。宿主日志提供执行证据，世界历史仍以 E/S 为准；39 次成功不证明无限时长经济稳定。

本次相邻源码版本：DurableGraph `f7f0c28bacaa67993b7b07a436c3d82bc4ebdda8`，Completion `97f755f184a1027323e270d16f91a3f89b134701`，Storage `9ad39fc51a420e8302468533b82d8bc3a91a403e`；未修改这些仓库。
