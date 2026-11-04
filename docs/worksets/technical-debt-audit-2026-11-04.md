# DramaBoard 技术债调查与整改（临时）

> 2026-11-04；基线 `97ffbcc58a01fb9851d10b9f383213b36c361657`，调查开始时工作树干净。
> 用户已授权按一般软件工程经验逐项整改，无旧版本兼容负担，可按需提交 Git。本文件保存问题状态与局部验收；[PROJECT-STATE](../../PROJECT-STATE.md) 仍是项目入口，现行产品延期边界不变。

## 维护状态

- **执行方式**：Goal 持续维护，无固定轮数上限；每轮选择一项、与 subagent 讨论、实施、独立验收后再推进。当前完成 4 轮：TD-01 时间边界主项、TD-02 内容 ID 合同、TD-03 命令身份与标签、TD-05 宿主日志写读合同。
- **当前结果**：TD-05 已独立复核并完成验收；人物／模型／操作 payload 共用严格读取，缺字段、非法 null 和身份错配在写前与重读时拒绝。CLI 全套、真实进程 Human／冷开／只读导出通过，没有未验收的本轮代码改动。
- **下一步**：优先 TD-04 预测身份与相等合同，然后 TD-06 真实进程回归进入 CI；其余已确认债务继续保留，不以已完成四项缩小 Goal。
- **保留事项**：TD-01 附记的 hydrate 结构检查与时间合同独立，仍未处理；在主项之后单独收敛，不以主项完成抹去。三项性能观察保持测量后裁决。

## 范围与结论

六个 subagent 分别调查核心时空、玩法领域、CLI、持久化与 Gym、模型调用与 lookahead、工程与测试入口。主线程复核下面各项的关键源码，合并重复发现，并运行独立边界探针和相关回归。

收录 **10 项技术债、3 项观察项**。其中 TD-01/02/03 有实际边界行为复现，TD-05 有内部写读合同缺口复现；其余主要是可由源码确认的维护成本和回归防线缺口。没有足够证据报告 P0/P1 问题，也没有证明当前内置 hillside/gentle 正常流程发生存档损坏或预算绕过。

这里的 P2 表示优先处理的常规债务；P3 表示较低优先级整理。顺序随当前证据调整；除特别注明外，静态结论置信度高，性能观察没有测量结果。

| 编号 | 建议级别 | 关注点 | 问题 | 本轮证据 |
| --- | --- | --- | --- | --- |
| TD-01 | P2 | Spatial | 时间边界主项已修复；hydrate 结构附记仍待处理 | 公开入口、冷开、pending 与消费者回归 |
| TD-02 | P2 | 内容／领域 | 已修复：内容 ID 单词元与物品 money 保留规则 | 作者拒绝、往返／实际采集、匹配指纹冷开 |
| TD-03 | P2 | CLI | 已修复：完整命令临时身份与结构化显示 | 键碰撞、标签／撤回／Context 回归及真实 Console |
| TD-04 | P2 | 预测维护 | 诊断 ToString 同时充当身份编码与相等合同 | 源码复核；未发现当前碰撞 |
| TD-05 | P2 | 宿主日志 | 写入接受的 payload 不一定满足阅读合同 | 实际写盘、重读、导出探针 |
| TD-06 | P2 | 工程 | CLI 真实进程 smoke 未纳入 CI | CI／夹具复核；Home smoke 实跑 |
| TD-07 | P3 | CLI／领域 | 采集速度公式在领域和两布局分别维护 | 源码三处复核 |
| TD-08 | P3 | 反馈日志 | 故障清理异常可能覆盖首个 I/O 错误 | 控制流复核；双失败未实测 |
| TD-09 | P3 | Completion | 共享协议主要回归仍依赖游戏测试夹具 | 实现／测试归属复核 |
| TD-10 | P3 | 文档入口 | 活跃导航仍把已实现能力和旧参数写成旧状态 | 与当前代码、主入口交叉核对 |

## TD-01：时间边界主项已修复，hydrate 结构附记待处理

- **原问题**：公开构造 anchor=10、arrival=20 的运动后，`Rebuild(business, 0)` 曾成功，同一时刻 GetLocation 随后抛错；当时只检查 arrival 不早于边界，没有检查 anchor。没有证据表明正常 Kernel 提交会生成这种状态。
- **方案与实现**：主线程与 subagent 交叉审查后，在 [SpatialSimulation.Validate:106](../../src/Spatial/Simulation/SpatialSimulation.cs#L106) 增加未来 anchor 拒绝，并保留 arrival 上界。沿用 Rebuild、Kernel、Replay 和 Playground 的既有校验 seam；不改 Schema／修订号，不给无时间的结构校验添加游标，也不在 Complete 重复校验。
- **回归**：[SpatialPredictionBoundaryTests:118](../../tests/Spatial.Tests/Persistence/SpatialPredictionBoundaryTests.cs#L118) 验证 Validate／Rebuild／Kernel／空 Replay 拒绝未来 anchor，验证首个实体合法但后续实体非法，验证两个运动分别处于 arrival／anchor 的同一边界；真实写盘冷开覆盖 0、9 拒绝，10、15、20 可查询，21 拒绝。新增用例在修复前明确失败，修复后通过。
- **恢复合同**：[新运动 E 的真实 pending 恢复](../../tests/Spatial.Tests/Persistence/SpatialPredictionPersistenceTests.cs#L148) 增加 anchor 等于恢复目标当前时间、位置查询成功的断言，保留禁止 Forecast／Plan、缓存重建与后续轨迹一致的原验收。
- **夹具收敛**：[BusinessRuleAdapters](../../tests/Spatial.Tests/TestSupport/BusinessRuleAdapters.cs#L7) 移除 `long.MinValue` 伪边界：有运动取最大 anchor，无运动取最早 schedule due，皆无取 0；再走严格 Rebuild。该值只为没有 cursor 的业务数学测试选择兼容快照，不冒充真实历史边界；过期 arrival／contact／schedule 仍拒绝。
- **本轮验证**：Release、`--no-restore -m:1 -p:DurableGraphSchemaHistoryMode=Verify` 下，Spatial.Tests 全部 114、Playground.Tests 全部 621、Playground.Cli.Tests 全部 691 通过，共 1426 通过、0 失败、0 跳过。未运行其他测试项目、真实模型或 Linux；本轮没有实际进程入口改动，不重复跑初始 Home smoke。

**仍未处理的附记**：hydrate 绕过构造后，负 AnchorOffset、非规范 consumed-contact 实体顺序未被 [GraphSpatialStateValidator](../../src/Spatial/State/GraphSpatialStateValidator.cs#L45) 完整重验。这属于独立的无时间结构合同，后续应在该 Validator 配合绕过构造／真实水合夹具处理；本轮主项验收不关闭这两处缺口。

## TD-02：内容 ID 与命令语法合同已修复

- **原问题**：内容定义曾接受 `town well` 等含空白 ID，生成 `gather town well 1` 后无法解析；物品 `money` 又会被 Give／Trade 解释为现金。内置 hillside/gentle 没有这些命名，但普通 C# 内容可以产生不可准确操作的世界。
- **方案与实现**：主线程与两位 subagent 复核，在 [WorldDefinition:293](../../src/Playground/PlaygroundWorldDefinition.cs#L293) 用共用 IsCommandToken 检查非空且不含任意 `char.IsWhiteSpace`；Unique 沿用原冒号禁令，移除地点／入口重复空白检查。[物品键:238](../../src/Playground/PlaygroundWorldDefinition.cs#L238) 另拒绝精确小写 `money`，不引入物品冒号禁令。构造、Change 与恢复的 Validate 共用此边界；引用继续通过成员关系绑定到已验证 ID，不向 DTO、Inventory、Spatial 复制命令规则。
- **保留行为**：Unicode、emoji、`|`、原有标点范围和 ordinal 身份保持；物品 `item:原料|甲`／`Money`、其他命名空间的 `money`、以及 `all/from/for/--clear` 均可用。名称、描述、ContentId 保留自由文本规则。不改 parser、Schema 或保存布局，不引入引号语法。
- **回归证据**：[内容定义测试:83](../../tests/Playground.Tests/HillsideContentDefinitionTests.cs#L83) 拒绝资源、角色、配方、物品的内部／边缘空格、tab、换行、NBSP 和全角空白；[正例:111](../../tests/Playground.Tests/HillsideContentDefinitionTests.cs#L111) 创建六组合法改名内容，验证规范命令值往返及观察，经真实文本控制器、解析和 Kernel 完成采集入包。路径／交易的冒号 ID 仍可解析；自由文本的既有 Trim／handoff 清空语义不在本项扩大。
- **真实冷开**：[HillsideContentBindingTests:29](../../tests/Playground.Tests/HillsideContentBindingTests.cs#L29) 绕过构造加入未引用的非法物品键，并重算匹配指纹后写盘；Open 必须因结构规则拒绝，磁盘哈希保持不变。新增 16 项回归在修复前 10 失败／6 通过，修复后通过，避免让旧指纹检查代替新语法校验。
- **本轮验收**：定向内容／绑定测试 28 通过；Release、`--no-restore -m:1 -p:DurableGraphSchemaHistoryMode=Verify` 下，Playground.Tests 全部 637、Playground.Cli.Tests 全部 691、Playground.Completion.Tests 全部 51 通过，共 1379 通过、0 失败、0 跳过。独立 diff 复核无阻断意见；未运行完整 solution、真实模型或 Linux。当前作者规则同步到 [README](../../README.md#L40)。

## TD-03：命令临时身份与显示合同已修复

- **原问题**：未转义 `|` 串既作键又被按固定位置反解析，使 `gather herb|rare 1` 显示成 `gather herb rare`。进一步复现：`Give("a|b", "c", 1)` 与 `Give("b", "c|a", 1)` 的旧键相同，可能使目录快照因重复键失败。
- **身份与映射**：主线程与 subagent 讨论、另一位 subagent 独立复核后，[Snapshot:256](../../src/Playground.Cli/PlaygroundViewSnapshot.cs#L256) 用完整命令 JSON 形成不解析、不显示的临时键，保留全部 19 个字段及嵌套 Goal／资产值、null／默认值。Snapshot 保存只读命令映射，[Update:63](../../src/Playground.Cli/PlaygroundViewUpdate.cs#L63) 合并上次与本次目录、当前优先，命令退出目录后仍保留结构化解释，不延长整个旧 snapshot 生命周期。
- **显示收敛**：[Renderer:32](../../src/Playground.Cli/PlaygroundUpdateRenderer.cs#L32) 只从映射取命令，复用 [CommandParser.Format](../../src/Playground/PlaygroundCommandParser.cs#L79)。删除 CommandSyntax 和固定下标反解析；四处完整／历史调用也直接使用同一 formatter。wait 范围压缩、accept 可尝试提示、Decision 布局筛选均改按 Kind／Hours 判断，移除键前缀依赖；合法 `|` 不受限制。
- **回归**：[CommandPresentationTests](../../tests/Playground.Cli.Tests/CommandPresentationTests.cs) 覆盖实际赠与键碰撞、全部命令字段／嵌套 Goal／null 与空串、规范标签、撤回条件与旧警告、wait 范围、accept 提示、两种完整布局及保留 Context 重渲染。初始 14 项回归在旧代码全部失败；最终新增 16 项通过，UI 不输出身份 JSON。
- **真实进程证据**：[临时 manifest](../../artifacts/td03-command-label-probe/manifest.json) 记录两个成功退出且 stderr 为空的 CLI 进程。自定义 `herb|rare` 存档初始背包满，`use item.food` 释放容量后，[delta:208](../../artifacts/td03-command-label-probe/live-delta.stdout.txt#L208) 正确显示采集条件满足；退出并冷开后，[保存 Context:171](../../artifacts/td03-command-label-probe/cold-context.stdout.txt#L171) 重新生成相同标签。模型记录为 0；这是自动 Console 输入验证，不代替独立真人体验验收。artifacts 不随 Git 保存。
- **验收范围**：Release、`--no-restore -m:1 -p:DurableGraphSchemaHistoryMode=Verify` 下，Playground.Cli.Tests 全部 707 通过、0 失败、0 跳过；独立 diff 复核无阻断意见。领域 Effects 不生成且不接受 command 类别，持久观察保存命令对象，重放重新生成 UI 键；因此不改领域 Schema／修订／协议，不做旧临时键迁移。未运行完整 solution、真实模型或 Linux。当前投影合同同步到[切片 A 当前接点](../design/incremental-ui-slice-a.md#当前实现接点)。

## TD-04：预测 ToString 承担调度身份和语义相等

- **证据**：[PlaygroundForecasts.cs:26](../../src/Playground/PlaygroundForecasts.cs#L26) 定义 ToString；[PlaygroundEngine.cs:120](../../src/Playground/PlaygroundEngine.cs#L120) 用它构造／比对 CandidateKey、检查候选是否存在及有效；[PredictionMaintenance.cs:43](../../src/Playground/PlaygroundPredictionMaintenance.cs#L43) 还用字符串相等决定是否复用旧预测。
- **维护成本**：诊断文案改动同时改变身份和缓存行为；新增身份字段依赖维护者同步隐式编码。当前字段尚未证明遗漏或碰撞，不能写成已有仲裁错误。
- **最小建议与验收**：显式提供候选键编码和类型化比较，ToString 只用于阅读；重构保持现有键字节与仲裁行为。验证全部身份字段参与比较，诊断显示改动不影响键或实例复用。
- **已有防线**：[PlaygroundMaintenanceBoundaryTests.cs:78](../../tests/Playground.Tests/PlaygroundMaintenanceBoundaryTests.cs#L78) 检查等值重算复用；部分轨迹 oracle 自身也通过 Forecasts.ToString 比较，尚不能独立约束诊断／身份边界。

## TD-05：宿主日志可写合同与可读合同不对称（已处理）

- **原问题**：只有 RequestId 的 model-request 曾可写盘、重读，随后人物 history 抛 JsonException；正常 HostSession 使用 typed projection，未发现真实运行已生成这种坏记录。复核还确认 operation payload 的可空但必需构造参数同样存在两端反序列化差异，这是内部合同和防回归债。
- **实现**：[HostHistoryPayloads](../../src/Playground.Cli/HostHistoryPayloads.cs) 统一必需构造参数、非空引用读取与 InvalidDataException；[journal 写前／重读](../../src/Playground.Cli/PlaygroundHostJournal.cs#L205)、[人物 history](../../src/Playground.Cli/CharacterHistoryExporter.cs#L85) 及 [request-fork](../../src/Playground.Cli/Gym/GymCommands.cs#L88) 共用。材料／模型 snapshot 检查各层集合元素、材料 Context 与请求 Boundary 的 envelope 身份；操作继续由现有 branch-local index 配对，feedback-saved 前置非空 FeedbackId 检查。Gym 的部分 request 夹具改为完整 snapshot，仍验证与真实世界决定前边界不一致时不注册分支。
- **保留边界**：空 Boundary、部分为空的捕获坐标、负 LineageId、失败无 Result、未完成 Response、无候选操作、Reasoning 无 PlainText、未知内容阅读缺口仍合法。管理 kind 不新增通用 DTO／状态机。校验只读取原 payload，不改 checksum 字节、I/O 故障退役、尾行处理或领域 Schema／修订；不承担旧日志迁移。合同同步到[主日志设计](../design/character-history-and-diagnostics.md#41-信封身份与写入)。
- **回归与复核**：[新增 67 项](../../tests/Playground.Cli.Tests/HostHistoryPayloadContractTests.cs) 覆盖 33 类负例的 Append 与有效校验链完整坏行 Read/Open 对称拒绝，以及完整投影保存、重开、导出。写前拒绝保持字节／序号不变且 writer 继续可用，完整坏行拒绝时不修复源文件；正例保留合法未知／不完整证据。独立复核发现的 `Errors:[null]` 漏项已补齐，`Errors:null` 仍合法。首轮写前夹具的 Windows 文件共享错误已修正，不作为产品缺陷证据。
- **真实进程证据**：[Home smoke manifest](../../artifacts/td05-home-smoke/manifest.json) 的 13 个 CLI 进程按预期退出，覆盖 Human 运行、冷开、off／ephemeral 与管理；另 [export manifest](../../artifacts/td05-home-smoke/export-manifest.json) 记录 a/b/c 三次 history 导出均 exit 0、stderr 为空，源存档逐文件哈希不变、模型记录为 0。导出材料覆盖按各角色实际保存记录检查，不替尚未打开 Context 的角色补造材料；artifacts 不随 Git 保存。
- **验收范围**：Release、`--no-restore -m:1 -p:DurableGraphSchemaHistoryMode=Verify` 下，Playground.Cli.Tests 全部 774 通过、0 失败、0 跳过，包括已有操作恢复／配对、Gym、原始编码字节、torn tail 和 I/O fault 合同。主线程核验源码、测试输出、真实进程和产物；独立静态复核无剩余阻断意见。未运行完整 solution、真实模型或 Linux。

## TD-06：真实 CLI 进程回归未进入 CI

- **证据**：[ci.yml:42](../../.github/workflows/ci.yml#L42) 只有 solution build/test；[HostFixture.cs:14](../../tests/Playground.Cli.Tests/HostFixture.cs#L14) 等测试直接调用 Options／Host，未启动真实 CLI 进程。[Program.cs:8](../../src/Playground.Cli/Program.cs#L8) 的启动分流、编码、管理入口和退出码映射不在这条联测路径上。
- **已有可复用产物**：[Smoke-PlaygroundHome.ps1:16](../../scripts/Smoke-PlaygroundHome.ps1#L16)、[Smoke-Hillside.ps1:27](../../scripts/Smoke-Hillside.ps1#L27) 已使用真实进程、重定向标准流、超时和退出码检查。主线程本轮实际运行 Home smoke 成功。
- **影响边界**：内部测试通过不保证 Program 接线未退化；本轮没有证明当前进程入口有故障。
- **最小建议与验收**：在现有 CI 的 build/test 后复用一项无模型进程 smoke，保留 stdout／stderr／manifest。先覆盖全新 home、Human 操作、冷开、只读管理及错误启动退出码；Windows 与 Linux 分别验证，避免把本轮 Windows 结果当跨平台结论。

## TD-07：采集速度解释在两个布局重算领域规则

- **证据**：[PlaygroundActions.cs:95](../../src/Playground/PlaygroundActions.cs#L95) 的真实耗时采用 `100 + 25 * SkillLevel`；[DecisionPlayLayout.cs:50](../../src/Playground.Cli/DecisionPlayLayout.cs#L50)、[PlayView.cs:62](../../src/Playground.Cli/PlayView.cs#L62) 各自计算同一百分比。实际 HarvestDurations 已在冻结观察中。
- **维护成本**：调整等级收益要同步三处，领域正确也可能显示过时解释；本轮没有发现当前公式不一致。
- **最小建议与验收**：领域统一提供解释数值进入冻结观察，或布局只展示现有周期。一次规则调整应使实际周期和两布局解释共同更新，布局不保留独立倍率公式。
- **已有防线**：[PlayablePlaygroundTests.cs:195](../../tests/Playground.Tests/PlayablePlaygroundTests.cs#L195) 检查精确耗时，但所查 CLI 用例没有领域公式与两布局解释一致性合同。

## TD-08：反馈日志清理可能掩盖原始错误

- **证据**：[FeedbackJournal.cs:82](../../src/Playground.Cli/Feedback/FeedbackJournal.cs#L82) 的 catch 先 Dispose，再清空字段和重抛；若 Dispose 也失败，首个写入／刷盘异常被覆盖，字段清空也不执行。外层 Dispose 还可能再次触发。[HostJournal.cs:98](../../src/Playground.Cli/PlaygroundHostJournal.cs#L98) 已有 faulted 清理保护，可作本地参照。
- **影响边界**：仅在写入与清理双失败时丢失首个错误和原失败位置；没有实测文件系统双失败，不据此推断正常保存损坏。
- **最小建议与验收**：先解除字段所有权，并保护 faulted cleanup 的首个异常。使用可注入故障夹具验证写入失败＋清理失败时保留首错且 writer 退役。
- **已有防线**：[FeedbackJournalTests.cs:58](../../tests/Playground.Cli.Tests/FeedbackJournalTests.cs#L58) 覆盖 beforeFlush 失败，不覆盖后续清理也失败。

## TD-09：共享协议回归仍主要放在游戏适配测试中

- **证据**：协议、超大材料／参数、transport 分类、反馈 Tail 和保存失败由共享 [LlmDecisionLoop.cs:71](../../src/CharacterControl.Completion/LlmDecisionLoop.cs#L71) 实现；主要回归却仍在 [LlmCharacterControllerTests.cs:141](../../tests/Playground.Completion.Tests/LlmCharacterControllerTests.cs#L141) 与 [LlmFeedbackTests.cs:94](../../tests/Playground.Completion.Tests/LlmFeedbackTests.cs#L94)，并构造 Playground Context。[共享测试](../../tests/CharacterControl.Completion.Tests/LlmDecisionLoopTests.cs) 主要覆盖跨角色、预算、绑定和串行合同。
- **维护成本**：独立修改／复用共享库时，主要协议验证仍需游戏夹具知识；[共享 README:27](../../src/CharacterControl.Completion/README.md#L27) 的测试归属描述也不完全符合实际。
- **边界**：这些行为已有测试，完整 solution 会执行；本项不等于协议缺少测试或当前循环错误。
- **最小建议与验收**：把纯 loop 合同迁到共享测试，直接使用 LlmDecisionInput 和伪客户端；游戏能力指引、冻结 Context、引擎提交留在适配集成测试。分别验证两个测试项目，保持覆盖而不复制两份用例。

## TD-10：活跃文档导航保留过时能力状态

- **证据 A**：[docs/README.md:67](../README.md#L67) 仍称 lookahead 在实验分支计划中、等待新 Goal；[根 README:63](../../README.md#L63)、[专题入口](../research/playground-speculative-lookahead/README.md) 与当前 Host 已提供主线默认关闭的实现。
- **证据 B**：[docs/README.md:51](../README.md#L51) 仍用现在时描述 `--play`／`--duo`；[根 README:126](../../README.md#L126) 明确旧模式退出 CLI，[DuoHostTests.cs:51](../../tests/Playground.Cli.Tests/DuoHostTests.cs#L51) 专门验证旧开关拒绝。
- **影响**：维护者从文档导航进入时可能误判能力或选择错误入口，多个位置的状态摘要已发生实际漂移。
- **最小建议与验收**：只修活跃导航摘要，链接当前能力和专题；旧验收正文继续保留历史语境，Slice B 继续延期。核对主入口、状态页、docs 导航和 --help 一致，并检查修改链接。

## 观察项：有结构性成本，尚无瓶颈证据

这些不计入上述十项的整改优先级；先测量或等待实际消费者。

1. **角色材料 Capture 重复遍历前缀**：[CharacterHistoryProjection.cs:26](../../src/Playground.Cli/CharacterHistoryProjection.cs#L26) 对每个 Item 构造 prefix；[CharacterContext.cs:92](../../src/Playground/CharacterContext.cs#L92) 复制列表，[CharacterContextRenderer.cs:50](../../src/Playground.Cli/CharacterContextRenderer.cs#L50) 再遍历。单次 Capture 的遍历随 Items 呈二次增长。成功／感知 Item 没有独立总数上界，但有同刻转换、LLM 字符预算等间接护栏。先测 n=32/128/512 的时间和分配；当前全量日志、delta 和容量延期已有明确设计，不直接登记为已发生性能故障。
2. **导航语义值使用完整 Markdown**：[ViewSnapshot.cs:191](../../src/Playground.Cli/PlaygroundViewSnapshot.cs#L191) 把 RenderCandidate／RenderActive 的文本用于变化比较，输出时又渲染。可维护性与 Capture 重复工作相互叠加，但没有性能实测或漏报复现。仅在导航投影改造或测得成本时，考虑复用 [ObservationEquality](../../src/Playground/PlaygroundObservationEquality.cs) 的结构化路径比较。
3. **不可变内容在世界验证边界重复验证／计算指纹**：[PlaygroundModels.cs:116](../../src/Playground/PlaygroundModels.cs#L116) 调 Definition.Validate，后者重新 ValidateStructure、BuildGraph、编码并 SHA；[Engine.Rebuild](../../src/Playground/PlaygroundEngine.cs#L32) 还会在后续 Validate 再走一次。仅确认重复工作，未测占比；如要缓存，必须保留创建／水合后的指纹篡改拒绝合同，不能仅为少一次哈希削弱冷开校验。

## 已排除与未覆盖

- 没有将当前选择的相邻源码依赖、精确 SDK、Storage pin、两种布局、无旧开发存档兼容，或“先游戏层验证再提取模块”列为债务。
- 自动摘要／记忆回灌、历史 delta／索引、动态拓扑、更强空间索引、通用框架、任意倒带、并发分支、完整原生模型会话恢复等明确延期能力不转成缺陷；真正人类参与的独立试玩验收仍保持原边界。
- Kernel 的 E/S 发布与故障锁定、S 成功才安装、pending 不重做 Plan、同刻转换预算；lookahead 的完整请求／目标匹配、共享物理预算、未合作取消占槽均有实现及专门测试反证，未发现可确认绕过。这里只是本轮有界审查，不表示整个模块没有其他技术债。
- 不审计归档应用内部，也不把上游仓库全部纳入调查；没有运行真实模型、长期压力测试、完整 solution 测试或 Linux 验证。

## 初始调查的验证与复现（整改前）

调查环境：Windows，SDK `10.0.201`；相邻源码 HEAD：DurableGraph `f7f0c28bacaa`、Storage `9ad39fc51a42`、Completion `d1dd88d61cce`。调查构建使用 `DurableGraphSchemaHistoryMode=Verify`，当时未修改产品代码或 Schema。以下命令、输出和测试结果属于整改前基线；当前代码的修复与验收以各项处理结果为准。

主线程临时探针位于忽略目录 [artifacts/technical-debt-audit-20261104-probe](../../artifacts/technical-debt-audit-20261104-probe/Program.cs)，可运行：

```powershell
dotnet run --project artifacts/technical-debt-audit-20261104-probe/DebtProbe.csproj -c Release -p:DurableGraphSchemaHistoryMode=Verify
```

实际观察到的关键结果：

```text
Rebuild accepted AnchorTime=10 at boundary=0.
Query at the same boundary rejected: ArgumentOutOfRangeException
Authored resource accepted; Check=allowed; Format=gather town well 1; Parse=null
Authored money item accepted; command Kind=Give; roundtrip Kind=GiveMoney
Command label for gather herb|rare 1: gather herb rare
Incomplete model-request payload appended and reopened successfully.
History export rejected the saved record: JsonException.
```

相关测试以 Release、`--no-restore -m:1 -p:DurableGraphSchemaHistoryMode=Verify` 运行，结果 **61 通过、0 失败、0 跳过**：

| 测试项目 | 本轮 filter 中的类 | 通过 |
| --- | --- | --- |
| Spatial.Tests | GraphSpatialStateTests、SpatialPredictionStorageShapeTests | 9 |
| Playground.Tests | HillsideContentDefinitionTests、PlaygroundMaintenanceBoundaryTests | 10 |
| Playground.Cli.Tests | IncrementalProjectionTests、OperationJournalTests、CharacterHistoryExporterTests、FeedbackJournalTests | 42 |

示例复验命令；其余项目按上表替换 project 和 filter：

```powershell
dotnet test tests/Spatial.Tests/Spatial.Tests.csproj -c Release --no-restore -m:1 -p:DurableGraphSchemaHistoryMode=Verify --filter "FullyQualifiedName~GraphSpatialStateTests|FullyQualifiedName~SpatialPredictionStorageShapeTests"
pwsh -NoProfile -File scripts/Smoke-PlaygroundHome.ps1 -OutputDirectory artifacts/technical-debt-audit-20261104-home-smoke-new
```

实际 Home 进程 smoke 成功，证据在 [manifest.json](../../artifacts/technical-debt-audit-20261104-home-smoke/manifest.json)。测试通过说明既有覆盖仍成立，不否定新探针暴露的合同缺口。artifacts 是本机临时证据，不随 Git 保存；目录清理后以正文复现说明和源码位置为准。

余项顺序以顶部维护状态为准；TD-04 的身份编码、TD-06 的进程回归可按现有工程边界分别收敛。其余低优先级项目随对应改动处理，不因此启动整仓重构。
