# 语义空间连接与大喊：实现验收

> 历史命名说明：本文保留当时的 ResourceCycle / Cycle 类型、命令与验证记录。当前应用已重命名为 Playground；现行入口与持久身份边界见[重命名说明](../../docs/implementation/playground-rename.md)。旧名称源码链接固定到重命名前的 Git 版本。

2026-11-01，落实[设计合同](../design/spatial-relations-and-shout.md)。Spatial 提供独立于移动的静态语义连接和一跳实体查询；ResourceCycle 的 `shout <text>` 已贯通 Human Console、Completion、冻结 Context、多人事实及磁盘恢复。

## 实现边界

- [PlaceRelationDefinition](../../src/Spatial/Definitions/PlaceRelationDefinition.cs) 使用规范化无向端点和 `ulong Flags`，只命名 `PlaceRelationCapabilities.Sound`。未知位（含 bit 63）完整保留；图拒绝自连接、未知端点与反向重复。
- [SpatialQueries.GetEntitiesInOneHop](../../src/Spatial/Queries/SpatialQueries.cs) 返回本地及匹配关系一跳内的全部 AtPlace 实体，零掩码拒绝、全部位匹配、结果立即物化且按实体 ID 排序。没有实体兴趣过滤、距离推导、多跳或订阅框架。
- 地图显式配置 `camp ↔ stream`、`ridge ↔ cabin`。游戏层筛出其他活动角色；一次大喊一个事实，所有参与者统一变化，只扣一次共享交流额度。无听众仍成功，回执不披露名单或数量。
- 收听保留活动期限、身体速率、提醒和跟随；原文以 `SpeechShouted` 的 SubjectId/SubjectName/Detail 交付。渲染先判别 Kind，`ColdWind`、`Terminal` 不会被翻译成机器原因；不增加听众的地图知识或远程交互权。
- pending E 恢复只应用记录，不重做 Plan、控制器或扣额；校验使用同一个纯接收选择函数核对集合。既有 say/give 仍严格为两名参与者，并补齐其 `Communication` 交付相等检查。

## 来源与构建

Windows，.NET SDK `10.0.201`。DramaBoard 设计基线为 `b2fd0c250be7f5986e6508f1c9ca2d8f8299f4ee`；本报告与实现位于同一后继提交，运行发生在提交前工作树。相邻源码依赖：

| 仓库 | commit |
| --- | --- |
| durable-graph | `f7f0c28bacaa67993b7b07a436c3d82bc4ebdda8` |
| atelia-storage | `9ad39fc51a420e8302468533b82d8bc3a91a403e` |
| atelia-completion | `97f755f184a1027323e270d16f91a3f89b134701` |

先对 CLI 构建图执行 Schema Publish，新增 6 条历史，不覆盖旧条目。最终执行：

```powershell
dotnet build DramaBoard.slnx -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
dotnet test DramaBoard.slnx -c Release --no-build --no-restore -m:1 --logger trx --results-directory artifacts/shout-acceptance-20261101/test-results
```

最终构建通过，0 警告、0 错误。全套首次运行暴露 4 个失败：两个新增赠与测试缺少物品，两个修订测试仍断言 17；夹具补齐及版本断言更新后，针对最终构建串行复验 ResourceCycle 领域、CLI、Completion 三套。最终三套为 **489 / 361 / 45 全通过**，其余 10 套在此前完整运行中全部通过，未受最后背景文案与夹具修复影响；合计 **13 套、1401 个用例通过、0 跳过**。这不是声称修复后又重跑了全部 solution。

原始日志和 TRX 位于忽略的 `artifacts/shout-acceptance-20261101/`：`build-initial.log`、`build-final.log`、`tests.log`、`*-final.log`、`test-results/`、`final-test-results/`。首次构建还发现新增测试使用 `Assert.Single(Where(...))` 触发 xUnit2031，已改为 Single 的 predicate 重载，再完成零警告构建。

最终入口 DLL SHA-256：

| DLL | SHA-256 |
| --- | --- |
| ResourceCycle.Cli | `8a713f05111c4769bdb088644fb92ad4a8f82bdb448d06ebbb07a5bb16321323` |
| ResourceCycle | `b3c7d533cf7adc756f04f6570aa0d9dc9dc3e94d27586105d34eab9aaaa1a913` |
| ResourceCycle.Completion | `3b1750da5c2e51ba894132741bb4db59e187b29bea30c777ff6b5822cacdba72` |

## 关键行为证据

| 验收项 | 可执行证据 |
| --- | --- |
| 无向规范化、位保留、重复/坏端点、零能力 | [PlaceRelationDefinitionTests](../../tests/Spatial.Tests/Definitions/PlaceRelationDefinitionTests.cs) |
| 本地含自己、双向一跳、不传播二跳、全部位、途中排除、普通实体与只读确定结果 | [OneHopQueryTests](../../tests/Spatial.Tests/Queries/OneHopQueryTests.cs) |
| 0/1/2 听众、独立地图关系、途中/终止排除、活动/跟随/自定义速率保留、完整参与者校验、容量原子拒绝 | [ShoutRulesTests](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Tests/ShoutRulesTests.cs) |
| Human/LLM 共同 parser、空白保真、2000 Unicode scalar 边界、坏参数拒绝 | [ShoutInputTests](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Tests/ShoutInputTests.cs) |
| 原文、署名、个人编号、无听众泄漏、重复接收、Open/Closed 交付、畸形材料拒绝 | [ShoutContextTests](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Tests/ShoutContextTests.cs) |
| say/give 的交付载荷被替换时拒绝 | [CommunicationDeliveryIntegrityTests](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Tests/CommunicationDeliveryIntegrityTests.cs) |
| 正常 S 与 E-only 冷开、不调用 Plan/控制器、不重复交付、预测 oracle、同刻到达先于输入 | [ShoutRecoveryTests](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Tests/ShoutRecoveryTests.cs) |
| 两种布局、full/delta、特殊字面量、个人材料与额度回执 | [ShoutViewTests](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Cli.Tests/ShoutViewTests.cs)、[CommunicationViewTests](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Cli.Tests/CommunicationViewTests.cs) |
| 实际宿主＋离线 Completion fixture：shout 提交、磁盘冷开、下一次模型材料保真 | [LlmHostTests](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Cli.Tests/LlmHostTests.cs)、[LlmCharacterControllerTests](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Completion.Tests/LlmCharacterControllerTests.cs) |

独立审查发现接收者选择曾跟随业务列表顺序，现已保留 Spatial 的 ID 顺序；事实记录继续遵循业务顺序，二者职责明确。另补齐发声者经历计数耗尽的前置拒绝，并以真实 Kernel 操作验证拒绝可以保存。终止角色测试通过真实离场事实建立合法夹具。

真实 Console 进程完成“营地喊话 → 邻地采集者听到 → 完成采集返回 → 主动跟随 → 三人同行”的闭环，38 条游戏操作中 37 成功、1 次预期远程跟随拒绝；主线程独立运行工件校验器，13 组断言通过。试玩发现回执把额度差量误标为剩余值，已修成 `4 次 → 3 次` 并复验。首批、复验、最终背景补充构建的证据范围与各自哈希在[Console 报告](spatial-shout-console.md)明确区分，没有把旧轨迹称为最终二进制的全流程重跑。

## 修订与限制

生产 `GameRevision = 18`。DurableType 版本为 CommandKind 5、Command 6、EventKind 12、Fact 15、ObservedEventKind 4、ObservedEvent 4。ResourceCycle 的 57 个当前候选通过 143 条 Schema 历史校验；Spatial 的 24 个候选与 24 条历史未增加语义关系持久类型。静态图内容通过游戏修订绑定，修改声音表必须考虑该边界。

旧开发存档不迁移；修订测试验证旧 S/带 pending E 的拒绝不写磁盘。本轮未调用在线模型，没有真实人类合作试玩，也未以代理操作 Human Console 代替用户体验验收。真实模型自主理解召集、完整获救与长期事件订阅体系不在本轮验证结论中。
