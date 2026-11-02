# 山野镇内容定义与存档身份验收

> 历史命名说明：本文保留当时的 ResourceCycle / Cycle 类型、命令与验证记录。当前应用已重命名为 Playground；现行入口与持久身份边界见[重命名说明](../../docs/implementation/playground-rename.md)。旧名称源码链接固定到重命名前的 Git 版本。

2026-11-01，Windows / .NET SDK 10.0.201 / Release。基于 `2f9a5826c3c0bbe7b34636efd40100d296716549` 加本片改动，GameRevision **22**。本片落实 [C6](../codex-goals/living-world/README.md)；[内容合同](../codex-goals/living-world/content.md)记录实际 API 和支持范围。C5 的 Gym 实际轨迹导出、C7 的真实模型与自由游玩仍未完成。

## 工程证据

| 验证 | 本次结果 / 原始工件 |
| --- | --- |
| CLI Release 构建，warnings as errors | 0 警告、0 错误；先 Publish 11 条新增 schema-history，再 Verify。领域 75 个当前候选 / 186 条历史。 |
| ResourceCycle.Tests 全组 | 548 执行、548 通过、0 跳过；`artifacts/living-world-content/domain-final/gdtutu_TUTU17_2026-11-01_18_25_13.trx`。 |
| ResourceCycle.Cli.Tests 全组 | 402 执行、402 通过、0 跳过；`artifacts/living-world-content/cli-final/gdtutu_TUTU17_2026-11-01_18_24_38.trx`。 |
| ResourceCycle.Completion.Tests 离线 | 45 执行、45 通过、0 跳过；`artifacts/living-world-content/completion-final/gdtutu_TUTU17_2026-11-01_18_26_35.trx`。 |

共 995 项通过，数量按实际 TRX Counters 核对。所有构建/测试使用 `-c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify`，测试另带 `--logger trx --results-directory ...`；Completion 关闭兄弟仓 CI 所列全部 opt-in 和 `OPENROUTER_API_KEY`，排除 `Category=LiveE2E` 与 `Category=LocalE2E`。本轮没有在线调用、Linux 验证或整个 solution 回归。

独立审查核对了字段与指纹覆盖、不可变集合、实例缓存、运行静态引用、温暖预测终止以及持久化顺序。主线程复核实际代码、TRX 和终端原始轨迹，不把子代理报告当作运行验收。

初轮发现并修复了 ScenarioId 未绑定有效 ContentId；另把旧天气夹具改为显式绑定实际 Enabled/Seed，观察复制夹具补齐冻结名称/补给说明，重复 ID 断言改为内容构造提前拒绝的异常。没有为通过旧夹具放宽生产身份检查。失败与最终结果分别保留于 `domain-targeted`、`domain-full`、`cli-full` 和上述 final 目录。

## C6 逐项证据

| 合同 | 可核对证据 |
| --- | --- |
| 普通 C# 内容入口与合法变体 | [定义测试](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Tests/HillsideContentDefinitionTests.cs)：`Named`/`Change`，覆盖参数归一化、集合复制/只读、重复 ID、坏引用、非法周期/配额/参数；不修改 Kernel。 |
| 全字段确定身份 | 同上：无序输入及语义边方向规范后指纹相同；反向提示、身体、天气、补给、市场周期、背包容量改变指纹；角色顺序不被排序抹去。 |
| 参数在真实规则中生效 | [独立行为验收](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Tests/HillsideContentAcceptanceTests.cs)用默认与自定义变体交错执行普通移动、采集、生产、买卖、消费，断言路线耗时、再生、配方投入/产出/期限、价格、初始资产、补给、代谢及天气差异；每步比对 Rebuild 预测和空间 Contacts。 |
| 合法温和身体参数不会预测异常 | [身体内容测试](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Tests/BodyContentDefinitionTests.cs)覆盖舒缓三日稳定、零速率、缓慢负漂移第三夜耗尽、末 tick 及不同内容的天气/补给。它不是 C7 三日模型运行证据。 |
| 冷开及 pending 不改用默认、不重 Plan | 独立行为验收：变体生产完成 E-only 恢复使用禁止 Forecast/Plan 的规则，投入/产出仅结算一次；正常冷开继续保持定义。另建默认局不影响变体分支。 |
| 保存内容与 S/E 绑定 | 独立行为验收和[绑定测试](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Tests/HillsideContentBindingTests.cs)：S 字段/指纹篡改、E/S 身份不符、显式期望定义不符均拒绝，逐文件 SHA-256 不变；正确身份仍可恢复 pending。 |
| 运行中不可更换定义 | 绑定测试：有效但不同定义的 nextState 配正确 cursor 仍在发布前拒绝，适配器 faulted，磁盘不变；重开仍保留原 pending，可完成一次。 |
| 分叉继承且不洗白错配 | 绑定测试分别破坏 predecessor S 身份/指纹、E 身份、selected S 身份，保证 selected S 单独合法后仍拒绝注册，源文件不变；正向分叉冷开移动，父 head 和业务材料不变。 |
| 修订不洗白错配 E | [修订测试](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Tests/CycleRevisionTests.cs)保持 S1/E2 成功中断合同，并新增错配 E 在进入 EventStep 前拒绝，不能靠转换重新贴标签。 |
| Console/Completion 使用冻结内容 | [宿主内容测试](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Cli.Tests/ContentHostTests.cs)覆盖人数归一化、省略内容续局、两宿主 pending 不符零模型调用/零写入；两布局显示自定义 17 点补水与世界名称。Completion 完整离线回归同步冻结名称。 |

## 实际 Console

最终 CLI 与领域二进制 SHA-256：

- `DramaBoard.ResourceCycle.Cli.dll`：`EF0BFE2E8CFA4BEB8DC3FB0461DBD4D366C1BF93226B1275B2697610D1FB67FA`。
- `DramaBoard.ResourceCycle.dll`：`A2CAA85B1DE10D1D8115D9A96B80FE1A7EF7003F332C69B92D580A2363FCF111`。

```powershell
pwsh -NoProfile -File scripts/Smoke-HillsideContent.ps1 -OutputDirectory artifacts/living-world-content/console-final
pwsh -NoProfile -File scripts/Smoke-Hillside.ps1 -OutputDirectory artifacts/living-world-content/economy-regression
pwsh -NoProfile -File scripts/Smoke-Hillside.ps1 -Scenario Trade -OutputDirectory artifacts/living-world-content/trade-regression
```

命名变体脚本使用四个独立进程：新建 `gentle --characters 1`，普通命令前往工坊、制作口粮、返回市场、卖出口粮；第二进程省略内容直接续局；第三进程显式要求 hillside 并验证拒绝不改存档；第四进程显式 gentle 匹配后续局。共 4 条成功领域命令，生产期限为 30 分钟，现金由 20 变成 26。保存身份为 `hillside-gentle-v1 / C6C54D1133A3DC671FF23A5C47CBC652DFA5D7264CD0F026D5C75DE28F0EC405`。成功进程 stderr 为空，拒绝进程返回 1 并报告内容身份不符。原始输入、stdout/stderr、存档和 `result.json` 位于对应目录；预检版本另保留在 `console-smoke`。

默认世界经济脚本在两个进程执行 39 条成功命令，复验两次镇山往返、采集、加工、消费、买卖与冷开继续；`operations.jsonl` SHA-256 为 `94FA65A2B652AE558D40E7D2EF4BF374D798A4EEFB393800704D36860F1E1C81`。交易脚本在两个进程执行 7 条成功命令，复验冷开成交、拒绝和等待中撤回。三组使用不同存档，不能把模拟时长相加用于 C7。

这些都是工程脚本，不是自由游玩或真人体验。需求与天气保持默认开启；未修改存档、暗补资产或通过停用需求制造成功。

## 依赖与剩余工作

本轮实际检查的相邻源码均干净且未修改：DurableGraph `f7f0c28bacaa67993b7b07a436c3d82bc4ebdda8`，Completion `97f755f184a1027323e270d16f91a3f89b134701`，Storage `9ad39fc51a420e8302468533b82d8bc3a91a403e`。

C6 已具备定义入口、实例隔离、身份恢复与变体运行证据。实际 Gym 轨迹导出/管理入口分叉、最终产品入口收敛、三日真实模型历史、混合控制与两次独立自由游玩继续按[工作计划](../codex-goals/living-world/work-plan.md)推进。磁盘故障测试覆盖 E/S 发布窗口，不外推为物理断电或任意损坏恢复；旧开发档没有兼容承诺。
