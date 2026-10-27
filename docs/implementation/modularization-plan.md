# 模块化重构方案：从 ResourceCycle 的真实协作边界开始

> 状态：渐进实施中；依据 2026-10-27 当前源码。A 已落地，后续切片按证据继续细化。分层与组合原则已采纳，见[设计决策](../design/layering-and-composition.md)；尚未确定的公共 API 或程序集名称不视为实施承诺。

## 1. 目标与范围

让现有代码更容易放入新内容、替换局部规则及组合另一种游戏，同时保留当前可玩的三角色采集闭环。先形成清楚的逻辑边界，再依据独立测试与实际复用收益拆程序集；不以文件数量、项目数量或去掉所有 `Cycle` 前缀作为进度。

已定原则由设计文档维护，本文只把它们落实为源码入口、可验收切片及暂缓条件。当前不新增商店、战斗、空间接线、LLM 控制器、动态插件、通用组件接口或多模块版本协调器。容量背包属于候选验证场景，未批准改变现有 ResourceCycle 玩法。

## 2. 当前职责与耦合地图

| 当前入口 | 已承担职责与可用边界 | 后续整理时的重点 |
| --- | --- | --- |
| [Kernel](../../src/Kernel/Simulation/SimulationKernel.cs)、[Spatial](../../src/Spatial/Simulation/SpatialSimulation.cs)、[Host](../../src/Host/SimulationHost.cs) | 通用发生项执行、可选空间机制、驱动循环；已有独立程序集 | 保留分界；不把采集顺序、昼夜规则或控制器轮转提升为 Kernel 默认政策。 |
| [CycleBackpack](../../src/ResourceCycle/CycleBackpack.cs)、[CycleCharacter](../../src/ResourceCycle/CycleCharacter.cs) | 背包独立承载物品数量与单份接纳查询，不读取整个世界；角色文件保留活动、感知和角色 | 库存能力先留在原程序集；角色的活动、感知与服务代数仍是本游戏组合的一部分，不能随背包一起提走。 |
| [CycleResource](../../src/ResourceCycle/CycleResource.cs) | 资源定义与运行状态同对象；包含余量、容量、单项产出、光照约束、进度和采集要求 | 不预设“定义/实例”必须分图；当前只接受 `herbalism`，不能直接宣称为通用资源框架。 |
| [CycleActions](../../src/ResourceCycle/CycleActions.cs) | 命令形状、控制器入口、接受条件、采集时长公式；受阻结果是中文字符串 | 区分局部规则检查与本游戏命令组合；保留“接受时合法”和“到期能否取得资源”的不同检查时点。 |
| [CyclePlanning](../../src/ResourceCycle/CyclePlanning.cs) | 同时确定资源替换、背包增加、活动续接、感知与结果文字 | 这是明确存在的跨模块编排；可调用局部能力，但仍由它决定一整个发生项的结果。 |
| [CycleFact](../../src/ResourceCycle/CycleFact.cs) 与 [CycleBusinessState](../../src/ResourceCycle/CycleModels.cs) | 前者记录跨对象替换事实；后者是具体游戏的业务根，校验跨对象引用 | 当前事实和根保留应用归属；不要求一个模块对应一类通用 Fact 或一个独立提交。 |
| [CycleEngine](../../src/ResourceCycle/CycleEngine.cs) | Kernel 接线、候选优先级与角色轮转、Fold、Complete、绑定、预测和验证 | 可逐步分离内部职责，但不能把 same-tick 优先级和角色公平轮转当作通用调度公理。 |
| [CycleForecasts](../../src/ResourceCycle/CycleForecasts.cs)、[CycleDiagnostics](../../src/ResourceCycle/CycleDiagnostics.cs) | 运行时预测/绑定及诊断；缓存不落盘 | 保留冷开 Rebuild、增量 Complete 与全量对照，避免模块提取后各自维护世界时间或发布。 |
| [CycleGameFactory](../../src/ResourceCycle/CycleGameFactory.cs) | 具体场景对象图、默认资源/角色/脚本、起始游标 | 作为组合入口保留；模块工厂最多返回其负责的子图，不另造全世界根。 |
| [Persistence](../../src/ResourceCycle/Persistence/CycleDurableHistory.cs) | 当前游戏根、版本化 E、加载修订、正常提交与恢复 | 已独立逻辑分区但仍在 ResourceCycle 程序集；不因此立即抽通用存档框架。 |
| [PlayConsole](../../src/ResourceCycle.Cli/PlayConsole.cs) | 驱动循环、角色视图、命令解析、Console 控制器 | 先让纯文本构造与 Console I/O 可独立验证；现有控制器仍接收完整 `CycleWorld`，不宣称已具备信息隔离 API。 |

当前项目引用也提供了边界：[ResourceCycle](../../src/ResourceCycle/ResourceCycle.csproj) 引用 Kernel 与 DG Persistence；[CLI](../../src/ResourceCycle.Cli/ResourceCycle.Cli.csproj) 引用 ResourceCycle；[Host](../../src/Host/Host.csproj) 只引用 Kernel。ResourceCycle 没有接入 Spatial，也没有经 Host 驱动交互循环；本轮方向不要求为统一入口强行改接。

需要特别保留的事实：

- `CycleBackpack.Add` 返回新对象；`CyclePlanning.CompleteGather` 同时选择资源、角色与感知的新值。局部能力不能就地改变已发布对象。
- 背包 `int.MaxValue` 接纳规则由 `CycleBackpack.CanAdd` 提供，接受命令与采集结算分别调用；预检查不预留，结算仍需重查。
- `CycleEngine.Affected` 依赖子对象引用变化判断 dirty；`Complete` 还验证折叠结果与记录事实相符。重构若无条件复制子图，可能保持玩法结果却破坏增量行为。
- `CyclePrediction.ToString()` 当前参与候选稳定身份与比较；业务对象的 `ToString()` 也用于诊断轨迹。不能把所有字符串一概当作可任意调整的展示实现。
- `CycleFact.Result`、`CycleCharacter.LastResult`、`CycleNotice.Reason` 已进入持久对象图，规划代码生成中文文本；`PlayConsole` 又根据结构化信息渲染感知。展示职责尚未完全分离，调整时必须说明哪些是历史记录，哪些是当前状态投影。

## 3. 所有切片共同保留的合同

1. **一次业务转换，两个发布边界。** 采集结算中的资源、背包、活动和感知一并进入事实。正常 Step 先 Plan、Fold/Complete/Validate 准备并验证结果，再依次发布 E、S，S 成功才安装运行视图；pending 恢复从已记录 E 开始准备结果并发布 S。不能称为一次不可分割的磁盘事务，也不能改成每模块独立提交。
2. **已记录事实独立恢复。** pending E 按记录恢复，不重新调用控制器、仲裁、Plan 或用新命令门槛重新判断历史决定；跨图按领域 ID 关联。
3. **就绪根与运行视图共享业务态。** 持久业务与运行时派生缓存分开；Rebuild/Complete 不生成新业务效果，候选查询不改变已发布状态。
4. **既有玩法不顺便改变。** `all` 固定接受时余量、不预留；先完成先得；同刻自然过程和活动完成先于输入；加入/耗尽唤醒不取消活动；非法输入与查看不推进时间、不确认消息、不交棒。
5. **初始化与游戏修订仍由游戏负责。** 模块整理不改变 State/E 各自的 `GameRevision` 合同；加载不写盘、不执行 pending、不推进游标。生产当前仅支持修订 1；夹具覆盖多版本不等于已有真实旧存档支持。

对应证据入口：[多人玩法](../design/resource-cycle-multiplayer-gathering.md)、[运行时缓存](../design/runtime-derived-predictions.md)、[初始化与修订](../design/game-initialization-and-revision.md)。各片按实际修改范围运行回归，未实施部分不借用历史测试结果作为验收。

## 4. 渐进切片

### A. 先整理库存接纳边界，留在原程序集

**状态：已实施并验收。** `CycleBackpack.CanAdd(itemId)` 是内部纯查询，`Add` 返回新快照并保留 checked 表示保护；没有扩展公共 API、容量玩法或持久 Schema。开始和结算两处复用查询，失败文案、批次续接与资源效果仍归原编排。[BackpackTests](../../tests/ResourceCycle.Tests/BackpackTests.cs) 覆盖临界上限、其他物品独立接纳、输入与输出快照隔离；ResourceCycle.Tests 全套 Release 测试及 Schema Verify 已通过，包含拒收不扣资源与真实 E-only 恢复。

**目标**：以一个实际重复的规则为起点，明确“库存表示及接纳结果”与“采集后如何改变世界”的分工。把背包从角色相关大文件中分离只是附属整理，主要收益是接受与结算复用同一个接纳规则，以及局部规则能够单独测试。

**入口**：`CycleBackpack`、`CycleActions.Check` 的 Gather 分支、`CyclePlanning.CompleteGather`，以及 [GatheringBoundaryTests](../../tests/ResourceCycle.Tests/GatheringBoundaryTests.cs)。具体方法签名在实施时决定；不预设 `IInventory`、策略注册表或统一结果泛型。

**保留合同**：开始前检查与结算时检查都存在；背包拒收时资源不扣减、无物品增加；成功后产生新背包，原快照不变；批次停止/续接及通知仍由采集编排决定。当前所谓无限背包仍有整数表示上限。

**非目标**：引入重量/格子/堆叠规则，改变当前容量，抽新程序集，新增物品定义系统。

**验收与推进条件**：局部接纳测试覆盖可接纳、已满、增加后达上限；既有“开始已满拒绝”和“第一份成功、下一份不可容纳且不扣资源”的集成测试成立；E-only 恢复仍按已记录替换执行。若整理只剩文件移动、需要扩大大量无关 public API 才能成立，缩小或暂停提取，先保留应用内方法边界。

### B. 分离应用内部的编排、预测维护和文本构造

这一步可以按实际改动点分为独立小提交，不要求同时完成三处，也不以建立三套框架为目标。

**目标与入口**：

- 在 `CycleEngine` 内区分 Kernel 接线/候选政策、事实折叠校验、派生预测维护；优先提取可单独检查输入输出的内部职责。现有世界/事实类型继续作为应用组合合同。
- 保留 `CyclePlanning` 为跨规则编排；局部规则可以接收所需对象而非完整世界，但不要为每个现有调用引入上下文接口。
- 从 `PlayConsole` 提取纯渲染与纯命令解析，使角色视图、受阻原因与输入语法可以不驱动真实 Console 而测试；输出仍保留当前 Markdown 行为。

**保留合同**：增量结果与全量 oracle 等价；不相关预测不重算；活动对象未变时 attention 变化不重新计时；同刻顺序、CandidateKey 和角色轮转不变；视图仍仅显示当前角色的私有技能、活动细节和背包。

**非目标**：通用事件总线、通用效果语言、独立 RuleSuite 程序集、强制所有规则一个接口、一次改完所有文本。尤其不自动把持久 `Result/LastResult/Reason` 改成临时字符串或新的历史消息 schema。

**验收与推进条件**：根据所改职责选跑 [CycleCompletionTests](../../tests/ResourceCycle.Tests/CycleCompletionTests.cs)、[CycleBehaviorTests](../../tests/ResourceCycle.Tests/CycleBehaviorTests.cs)、[PlayableCycleTests](../../tests/ResourceCycle.Tests/PlayableCycleTests.cs) 与对应恢复测试；Console 拆分需增加行为性视图/解析检查并做真实脚本试玩。若分拆只能通过共享可变全局状态或层层透传整个世界成立，先保持显式应用编排；不为了目录对称而继续拆。

文本进一步结构化是后续设计点：先区分历史事件描述、角色持久感知与当前观察，再确定哪些内容应在 Plan 记录、哪些由展示生成；不得借“展示无关”之名让恢复重新解释历史事实。

### C. 用另一个真实约束检验库存能力边界

**目标**：验证 A 的能力能否支撑“当前无玩法容量限制”与“某一种有容量约束”两种用法。后者可以先用独立测试组合探针，不改变默认 demo。容量按总件数、重量还是格子计算，仍需在实施前选择；不一次支持所有变体。

**入口**：A 整理出的局部能力、测试夹具与具体游戏组合代码。探针不要求另建 Console 或存档入口，也不凭一个测试夹具宣称已证明完整第二游戏复用。

**保留合同**：模块只回答自己的状态与约束，游戏决定失败是否耗时、是否结束采集、怎样告知角色。预检查不预留资源；结算再检查；从已经记录的 E 恢复时不重判接纳政策。

**非目标**：全面替换当前角色模型，内容插件 DSL，容量策略持久化 delegate，多模块版本政策。

**验收与推进条件**：探针能够不用 `CycleWorld`、资源点、控制器或 Console 测试库存语义；不同约束共享的内容与各自差异可说清。若 API 被格子/重量等尚无需求的假设撑大，退回各自明确实现；若需要记录新政策状态，先评估 DG schema 与游戏修订，不先塞进运行时配置使冷开结果变化。

### D. 有证据后拆程序集，明确游戏组合入口

**进入条件**：至少有一个职责边界能解释清楚、向下依赖成立，且拆出后确实获得独立测试、独立使用或避免反向引用的收益。第二个生产游戏不是硬性门槛，但单纯改目录不算收益。库存是首个候选，资源再生、活动或整套采集是否一起提取仍需单独判断。

**实施意向**：移出的模块拥有其局部状态与规则；具体游戏引用模块并组合根、事实与工厂。模块不引用 `ResourceCycle`、CLI 或具体游戏持久化 adapter，也不拥有独立世界提交入口。游戏负责组合模块模型注册，模块可提供自身注册入口，具体形状在现有 DG 生成器能力下验证后确定。

**入口**：候选模块源码、`ResourceCycle.csproj`、[CycleDurableModels.Register](../../src/ResourceCycle/Persistence/CycleDurableModels.cs)、[DG 源码接线](../source-dependencies.md)、[solution](../../DramaBoard.slnx)、相关测试项目。

**保留合同**：游戏继续拥有根、初始组合、跨模块编排和 GameRevision；全部子图仍经同一个根保存。源码 mod 可以作为共同编译的组合消费者，不要求动态加载、沙箱或二进制稳定 API。

**验收与推进条件**：程序集引用无环且不反向引用游戏；模型注册完整，无漏注册或身份冲突；新游戏与真实冷开/E-only 恢复均覆盖移出的类型；现有 Console 闭环保持。若必须依赖游戏根、复制一套模型或新增大量友元才能抽出，回到逻辑边界调整，不先完成形式上的项目拆分。

## 5. 命名、Schema 与验证安排

命名和程序集迁移各自都可能影响代码及持久化，施工前逐项核查：

1. 若概念名称需要变化，先列声明符号的新旧映射、关联字段/方法及用途变化；使用 Symbol Rename 或等效符号级工具。文件/命名空间整理与行为改变分开提交，避免全仓字符串替换。同步核对[术语表](../glossary.md)，候选名称不能写成已存在 API。
2. 清点 `DurableType` 逻辑身份、`DurableField` 编号、引用字段类型、生成定义与 schema 历史。CLR 类型改名、命名空间变化、项目迁移与逻辑身份变化分开评估，不能假设“只搬文件/程序集一定不影响 Schema”。
3. 移动生成器归属时检查 `CycleDurableModels.Register`、新模块定义注册、source props/targets 与历史文件归属。不要手改生成文件；不为让 Verify 通过直接删除历史。若有 schema 变化，明确是表示演进还是游戏语义变化，再决定修订号及支持范围。
4. 没有旧存档兼容负担不代表本轮可以意外破坏当前 E/S 恢复。不承诺所有开发存档跨重构兼容；确需拒绝旧记录时明确写入该片说明，并仍验证新格式的 State-only、pending E 与提交失败边界。

验证按影响范围进行，先使用已有行为测试，新增测试覆盖本次抽出的边界及新风险，不镜像实现。核心证据入口：

| 风险 | 当前测试入口 |
| --- | --- |
| 不变快照、增量/全量轨迹、同刻自然事件 | [CycleBehaviorTests](../../tests/ResourceCycle.Tests/CycleBehaviorTests.cs)、[CycleCompletionTests](../../tests/ResourceCycle.Tests/CycleCompletionTests.cs) |
| 采集原子效果、轮转、感知、同刻竞争 | [PlayableCycleTests](../../tests/ResourceCycle.Tests/PlayableCycleTests.cs)、[MultiplayerEdgeTests](../../tests/ResourceCycle.Tests/MultiplayerEdgeTests.cs)、[GatheringBoundaryTests](../../tests/ResourceCycle.Tests/GatheringBoundaryTests.cs) |
| 真实 E-only、失败提交与运行视图安装 | [CyclePersistenceTests](../../tests/ResourceCycle.Tests/CyclePersistenceTests.cs)、[MultiplayerRecoveryTests](../../tests/ResourceCycle.Tests/MultiplayerRecoveryTests.cs) |
| 根/事件真实字段、缓存不落盘、工厂、内存修订 | [CycleStorageShapeTests](../../tests/ResourceCycle.Tests/CycleStorageShapeTests.cs)、[CycleInitializationTests](../../tests/ResourceCycle.Tests/CycleInitializationTests.cs)、[CycleRevisionTests](../../tests/ResourceCycle.Tests/CycleRevisionTests.cs) |

影响项目引用、公共模型或生成器时，串行构建以免相邻 DG 共享输出竞争，并执行当前 solution 的 Schema Verify 构建与测试；涉及 Console 则补内存运行、磁盘新建及续局脚本。每片记录实际运行结果，不把上片或文档中的历史测试数量作为新基线。

## 6. 下一轮建议与待决边界

下一片先做 B 的 Console 纯渲染/解析：独立测试当前角色的可见文本和输入语法，保持 IO 驱动与领域合法性判定分开。预测维护作为 B 的独立后续片，再依据局部 API、测试和试玩结果选择 C 或下一实际玩法需求，不把 A→D 当作必须机械走完的迁移流水线。

尚待证据选择：库存变体的容量含义；展示文字中历史记录与当前投影的具体拆分；哪个模块首先值得独立程序集；定义与运行实例是否拆开；后续空间与采集怎样组合。确定前保持普通类型、显式调用与应用层组合，不为这些问题预建公共接口。
