# 角色 History 与统一宿主日志

状态：2026-11-02，待实施方案。用户已选择“单一有序宿主时间线＋独立模型原始审计＋独立反馈”；本轮结合恢复/分叉与模型阅读投影两项源码审查修订。本文替代此前按 operations、Gym、模型文件互相引用的布局，下一轮按第 9–10 节实施验收。当前代码尚未改变。

## 1. 目标与已定边界

按角色导出连续可读的 history：Observation、LLM reasoning、普通 response、尝试的操作、游戏回应和后续经历。Human Console 回合省略 reasoning/response。它是开发者研究经历管理、记忆召回的材料，不自动成为角色记忆，不改变世界或控制策略。

| 决策及来源 | 实施约束 |
| --- | --- |
| 用户要求逐角色 history，文本或按需导出均可 | 按需导出 Markdown，不另行持久维护每角色的完整文本副本 |
| 用户要求 reasoning 使用 Completion 的 PlainText | 不解密、不再调用模型总结、不把 null 解释为未思考 |
| 用户认可统一日志为主体，模型审计和反馈独立 | 合并 operations 与 Gym 存储；内联可读材料，raw 不是普通 history 的必要依赖 |
| 当前单宿主、串行决策与写入 | 每个 store 一条有序日志；不引入分布式时钟、队列、事件总线或数据库 |
| 当前世界 E/S 独立提交、pending 恢复不重做决定 | 世界仍为恢复权威；宿主日志与世界不宣称原子提交 |
| 用户要求独立 feedback 文件和成功回执 | 保存/查询/反馈分叉继续独立，主日志只附加关联 |
| 无兼容性包袱 | 不实现旧宿主格式迁移，保留旧文件和 Git 中旧工具，不静默遗弃旧操作对账义务 |

不修改 DurableType、领域 Schema、GameRevision、Kernel 或 Completion provider 协议。原生模型会话恢复、记忆检索、自动摘要、全量世界事件导出不在本轮范围。

## 2. 行业经验与当前证据

以下仅借鉴机制，不引入相应产品或照搬存储结构：

- OpenTelemetry 用 TraceId/SpanId 关联记录，并区分事件时间与观察时间。借鉴“顺序、身份、时间含义分别表达”，不推导日志必须分文件。[日志模型](https://opentelemetry.io/docs/specs/otel/logs/data-model/)
- Langfuse 将调用和工具步骤组织为 observations，再按 trace/session 聚合；其文档以统一 observations 表解释查询。借鉴统一集合上的阅读视图，不把逻辑表当作单物理文件的证据。[数据模型](https://langfuse.com/docs/observability/data-model)
- Claim Check 把大载荷外置，同时增加孤立载荷、引用可用性和生命周期协调。仅模型原始载荷使用这种关系，小型操作事实直接内联。[模式与代价](https://learn.microsoft.com/en-us/azure/architecture/patterns/claim-check)

当前源码与消费者：

- [PlaygroundHost](../../src/Playground.Cli/PlaygroundHost.cs) / LlmRunAudit 同时写文件、观战和转发 Gym，全 Human 也建模型日志；此次拆开接线。
- [PlaygroundOperationJournal](../../src/Playground.Cli/PlaygroundOperationJournal.cs) 的刷盘、不可变结果、Interrupted、ExactResult=false 保留，去掉独立文件所有权。
- [GymSession](../../src/Playground.Cli/Gym/GymSession.cs) 复制完整调用和操作结果、每步扫描历史 Entries；改为单 journal 的直接追加，不再 operation-ref。
- [LlmDecisionLoop](../../src/CharacterControl.Completion/LlmDecisionLoop.cs) 一次服务可调用多次，同 RequestId 可先 Response 后 Failure，传输异常未必保存部分输出。
- [CharacterContext](../../src/Playground/CharacterContext.cs) 已有 Opening/Item/Experience/Operation 身份与 PreviousAdvance；旧 Gym 只有 Material 字符串，无法据此可靠还原关联。
- [GymCommands](../../src/Playground.Cli/Gym/GymCommands.cs) / [GymTraceExporter](../../src/CharacterGym/GymTraceExporter.cs) 是管理、请求 fork 和前缀的迁移入口。[独立反馈](independent-feedback.md)的保存、查询和 fork 不改为依赖主日志。
- [OperationJournalTests](../../tests/Playground.Cli.Tests/OperationJournalTests.cs)、[GymIntegrationTests](../../tests/Playground.Cli.Tests/GymIntegrationTests.cs)、[GymCommandsTests](../../tests/Playground.Cli.Tests/GymCommandsTests.cs)与 Home/HillsideGym smoke 是回归入口，不以历史通过结果代替新验收。

## 3. 保存布局与唯一职责

路径相对 store；ephemeral 的模型/反馈路径继续相对 home。目标布局如下：

| 载体 | 保存责任 | 消费者 |
| --- | --- | --- |
| DurableGraph repository | 世界事实、状态、角色保留材料与真实分支 | 游戏恢复、世界查询、Inspect/Fork |
| **host/events.jsonl** | 有序宿主事实：运行、操作接收/结果、角色材料、模型可读请求/输出、反馈关联、分叉截点 | 操作对账、人物 history、请求 fork |
| llm-runs/&lt;RunId&gt;.jsonl | 完整 Completion 请求/响应、native reasoning/replay、工具往返、调用失败 | 专项模型诊断与人工查看 |
| feedback.jsonl | 反馈原文、提出时 Boundary、可选模型身份 | 独立回执、查询、反馈 fork |
| 导出 Markdown | 从选定历史生成的自足阅读产物 | 开发者、Coding Agent、经历与记忆研究 |

不再写 operations.jsonl、branch-operations、gym/events.jsonl 或此前草案的 gym/events.v2.jsonl。每 store 一个主文件覆盖所有分支/运行，仍只允许一个串行 writer，不按角色或分支另开主文件。保留 `--gym off|record`、`--gym-export`、`--gym-fork-request` 名称，帮助文字改为详细人物记录；不扩大为 CharacterGym 项目改名。

主日志的 operation-received/completed 本身就是操作记录，无第二次 operation-ref。对账和人物阅读是两个消费者，不是两份持久操作事实。模型可读投影和 raw 的少量文字重复是有意保留的不同投影，均从同一次不可变 trace/result 生成，不额外调用模型，不在读取时选择“较新版本”。raw 缺失不影响主日志已有的五类人物材料，只影响专项诊断。模型文件首次调用前懒创建，全 Human 不创建。

## 4. 主日志合同

### 4.1 信封、身份与写入

应用层 `PlaygroundHostJournal` 复用现有完整行、严格 UTF-8、校验链、尾部处理和 Flush(true)；不构造可注册业务类型的日志框架。

- metadata 声明 FormatVersion=1、随机 JournalId；后者只标识日志，不是假定的世界全局 StoreId。
- 每行有单调 Sequence、PreviousHash、原始 Payload 字符串和 Hash；校验保存字节，不重新序列化后计算。
- Payload 使用固定 Kind 与 typed 数据，按需有 RunId、BranchId、ActorId、ContextSequence；管理记录可无 RunId，不拿当前身份补猜缺失字段。
- 宿主统一生成 RunId，传给各记录器。OperationRecordId、RequestId 仍表示各自关联；Sequence 表示追加顺序，不代替业务身份或模拟时间。
- 有世界定位时保存捕获时完成 S、lineage/transition/tick；模型决定前保存完整 PlaygroundDecisionBoundary。捕获 S 不是操作原始提交点。
- 全行刷盘后才安装内存状态、返回成功。写入/刷盘失败退役 writer，不自动重试。完整行损坏报错；只读不修尾，writer 重开可处理未完成尾并报告字节数。

所有主记录共用一个 writer。启动一次扫描构建内存索引，运行随成功 append 增量更新，不每步全扫 Entries。首版可全量读取小世界日志，不提前做分段/压缩/数据库；未来分段仍须保持逻辑 Sequence 与前缀合同。

### 4.2 记录种类与开关

| Kind | 最小内容 | off 仍写 |
| --- | --- | --- |
| run-start / run-end | 控制绑定、预算、内容/构建身份、详细模式、raw 路径、起止完成 S、停止原因；结束可缺失 | 是 |
| operation-received | Received 全字段：OperationRecordId、BatchId、ToolCallId、ActorId、ContextSequence、ToolName、RawInput | 是 |
| operation-completed | OperationRecordId、Status、Result、WorldVersion、DomainEvent、ExactResult、正常/恢复补录来源 | 是 |
| character-material | 第 5.1 节薄投影、完成 S、Context 状态，包含 Terminal | 否 |
| model-request | 调用身份、完整 Boundary、白名单请求阅读投影、raw 定位 | 否 |
| model-response / model-failure | RequestId、阶段、终止/失败事实、可读 blocks、工具尝试、候选 OperationRecordId、raw 定位 | 否 |
| feedback-saved | FeedbackId、实际角色/Context、可选 RequestId；不证明收到回执 | 否 |
| branch-fork | 来源分支、世界 S、SourceThroughSequence、SourceRequestId | 管理命令独立于录制开关 |

持久运行未指定 gym 默认 record；ephemeral 未指定默认 off，显式 record 继续拒绝，不静默降级。配置合并保留未指定与显式值区别，运行时只有两种模式。

持久 off 关闭详细材料，不关闭主文件、操作对账、raw 或 feedback。导出可显示运行/操作，明确观察和模型阅读未录制，不从 raw 或当前世界补造时间线。ephemeral 主日志及操作投影只在内存，不保存主文件、不承诺可分叉 history；基本状态输出 Console，模型/反馈沿用各自保存合同。无 home/store 的程序化入口不发明文件位置。

这是对现有默认 off、off 不建 Gym、ephemeral 持久保存 host 审计的有意变更。必要操作记录的可靠性不随详细开关下降。

### 4.3 操作与恢复

将 PlaygroundOperationJournal 收敛为绑定 `(BranchId, RunId)` 的 `PlaygroundOperationAudit`：保留 Receive/Complete/Reconcile 和字段语义，委托主 journal 追加，移除自身 stream/hash/刷盘。内部命名可调整，不留误导性的双 journal 包装。

1. 索引按 `(BranchId, OperationRecordId)` 分区，BatchId/ToolCallId 重用校验也在分支内。Receive 同 ID 只允许同内容；Completed 至多一个且不可改写，相同重入返回既存结果，不新增行。
2. **恢复只看当前 BranchId 自己的操作行，不继承 history 的祖先前缀。** 祖先未完成输入不是子分支待执行或待对账任务。
3. 打开校验主日志/分支投影 → run-start → Kernel.RecoverPending → 从世界对账本分支 Pending IDs → 追加新的 operation-completed → 捕获当前材料 → 服务控制器。恢复/对账结束前不初始化 provider。
4. 世界证明已提交则补原成功/拒绝并标 ExactResult=false；无可证明结果则补 Interrupted，不执行原输入、不恢复已裁剪拒绝。补录使用本次 Sequence，不回填旧位置。
5. 世界 E/S 与主日志仍分别提交。没有 Completed 不证明世界未执行，宿主日志不授权重做游戏行为。

### 4.4 调用、失败与反馈顺序

LLM（record）：构造实际请求 → raw request 保存 → 主 model-request → 调用 provider → raw response/failure 保存 → 主 model-response/failure → 有效 act 返回 → operation-received → 世界 E/S → operation-completed → character-material。off 跳过详细主记录，操作步骤不跳过。

Human：捕获/展示角色材料 → 输入 → 非 feedback 先 operation-received → parser/查询/元命令或领域提交 → operation-completed → 显示结果。一次服务内连续查询后 EOF 也当场保存，不等 Step 完成，不再通知 Gym 记第二份。

raw 成功而主日志失败可能留下孤立 raw；暂停，不重问模型补日志。共用 journal 故障同时阻断后续操作审计与详情，这是有意取舍。stderr 独立报告最小错误，不再写已故障 journal；Dispose 不覆盖原异常、不伪造正常 run-end。

FeedbackSession 只保存独立文件、刷盘并返回 ID。LLM 用保存 try/catch 之后的 Feedback trace 追加 feedback-saved；Human 先显示成功 ID，再在保存 catch 外通知主日志。附属记录失败不能变成 feedback-save-failed 或反馈未保存，不自动重交；可暂停且明确交付未知。`--feedbacks` / `--fork-feedback` 不打开主 writer，后者也不依赖主日志存在或健康。

## 5. 可独立阅读的投影

### 5.1 角色世界材料

从已提交 CharacterContext 捕获薄 JSON DTO，不反解析旧 Markdown、不复制完整领域对象：

- ObservationText：捕获时角色冻结观察的阅读文本。
- Opening：当时片段、ExperienceSequence 列表、可空 PreviousAdvance 的 SourceContextSequence/OperationSequence/OperationRecordId。
- Items：按 ItemSequence 的片段，含当时文本、可空 OperationSequence/OperationRecordId、ExperienceSequence 列表，保留拒绝、目标操作和被动感知。

每次保存完整投影快照，不做 delta journal。相同来源分支/角色/Context/Item 且文本相同时导出可折叠；换 Run/构建导致同一片段出现不同文本时保留再次捕获并标呈现变化，不覆盖旧文、不判世界损坏、不新增 RendererId。Observation 可随提交变化，不能只按 ContextSequence 去重。

PreviousAdvance 只证明下一 Opening 承接哪次操作，不证明每条天气/通信经历由它造成。Closed 未交付经历不伪装已观察；无模型调用的被动经历和 Terminal 仍由角色材料呈现。

### 5.2 模型阅读投影

主模型记录是实际 LlmControllerTrace 的白名单投影，**普通 history 不调用 raw reader，不依赖 provider codec**：

| 部分 | 内联内容及边界 |
| --- | --- |
| 身份 | 信封保存 RunId/BranchId/ActorId/ContextSequence；payload 保存 RequestId、CallNumber、ConnectionId、ModelId、ApiSpecId、raw RecordSequence。不从当前配置回填身份 |
| 请求消息 | 有序 Shared/Tail，保留 section、message index、kind；Observation 原文，assistant 可读 blocks，ToolResults.Content 及每个结果的工具名/ToolCallId/Status/可读 blocks |
| 输出 blocks | 按原 block index 排列：Text.Content；ReasoningBlock.PlainText（可空）；ToolCall 名称/ID/InputKind/RawInput或JSON参数。禁止 native/opaque/signature dump |
| 终止与失败 | Stage、Termination、ProtocolError、可用 Usage/Invocation 身份；可空 Result。同 RequestId 先 response 后 failure 保留两条，不用字典唯一结果覆盖 |
| 操作关联 | trace 提供的 OperationRecordId、ToolCallId、角色命令只是候选，须另见 operation-received/completed 才陈述接收或提交 |

SystemPrompt、完整工具定义及 native 留 raw，人物核心阅读不依赖这些协议材料。实际观察/纠正消息和工具收据要内联：后续 model-request 的 Tail 带匹配收据，证明它已进入该次构造并保存的输入。请求行先于调用保存，因此不证明 Completion 调用已开始、远端收到或处理，也不等于 provider HTTP 字节。

投影匹配 ToolResultsMessage 必须先于其基类 ObservationMessage，避免扁平化丢收据。Tail 的旧 assistant 内容标为本次携带材料，不再次当作此次新 reasoning/response；协议纠正文本不是世界事件。

Reasoning 仅用 PlainText，可能为明文、摘要或无可读内容，不额外推断思想或强造完整/摘要分类。Response 只取 Text，普通输出不是角色对白。保留交错 block 顺序而非拼成三个无序大字符串。未知 block 记类型/投影缺口，禁止原对象 JSON 兜底；无文本表示的多模态结果明确缺口。

Incomplete/Failed 可展示已有输出，但不标成功行动；传输中断无 Result 时报告完整结果未捕获、远端结果未知，不增加 stream token 持久化。Human 完全省略 reasoning/response；null PlainText 不等于未思考。

### 5.3 原始审计

`--llm-log` 可覆盖位置，默认按 RunId CreateNew。run-start 保存位置提示，store 内用相对路径，外置不全盘搜索。raw 采用完整行/序号/校验链/Flush(true)，延续原 Gym 全载荷副本的耐久强度；feedback 不因此增加 hash chain。

可读投影与 raw 来自同一 trace，不允许 reader 选较新者。普通导出不为了检查附件加载/校验 raw，只标注原始审计未验证并给定位；raw 缺失/损坏不阻止人物主体导出。专项原始审计读取才验证其格式与完整性。实际请求里的反馈原文及收据保留原样，不因独立 feedback 文件而篡改调用材料。

## 6. 人物 history 与分叉

沿用管理入口，行为升级尚未实施：

```powershell
dotnet run --project src/Playground.Cli -- --home artifacts/my-world --actor a --gym-export artifacts/a-history.md
```

多分支必须显式 `--branch`。导出不启动游戏、不恢复 pending、不加载模型配置/凭据、不修尾。只读快照忽略未完成尾并报告字节数，完整行损坏失败；先完成读取/校验，再用 CreateNew 输出。角色不存在报错；存在但 off/无详情则输出覆盖缺口。

### 6.1 选择与呈现

先选本分支自己的记录与有效祖先前缀，再筛角色，按主 Sequence 展示，按相邻 `(来源 BranchId, ActorId, ContextSequence)` 加标题，不全局 GroupBy 后重排。多个 Run/请求保持身份；追加顺序不等于模拟时间或因果。

```text
角色 a · Context 12
  Observation：冻结观察和已交付经历
  模型 Q1：Reasoning / Response / feedback 尝试
  反馈已保存 F1（不等于收到回执）
  模型 Q2：观察及携带 F1 收据的 Tail
    Reasoning / Response / act(go …)
  接收 O1 → 世界回应 O1：移动开始
角色 a · Context 13
  Opening 承接 O1；交付抵达等经历
```

只用已选前缀的操作行配对，不查全文件的最新操作索引来补历史。恢复索引不能用于 history，否则后来 Completed 会倒灌切点。区分世界已提交结果、宿主查询、被动经历、Interrupted、ExactResult=false；同 tick 多转换不能合并。

正文白名单投影，无完整 Blocks/Tail JSON 附录；使用足够长的 Markdown fence/安全字面量。history 是开发者视图，可能含角色私有材料，不自动展示给其他玩家或回灌角色，不保存凭据。

### 6.2 请求与反馈 fork

请求 fork 从主日志唯一 model-request 取得完整 Boundary，Inspect 后 Fork；`SourceThroughSequence = model-request.Sequence - 1`。branch-fork 写在子分支，保存来源分支/S/请求及精确切点；祖先前缀排除切点请求与父未来，JournalId/来源身份一致，不按同 tick/S 猜请求。

世界注册与主日志追加分别提交。后者失败返回分支已注册、来源记录未确认及分支 ID，不重新注册；子分支可运行，但缺祖先前缀就报缺口。只有显式 fork 才打开 writer，普通导出不升级为写操作。

反馈 fork 继续只读 feedback、Inspect/Fork，不读写主日志。无可证明主切点时不继承祖先宿主记录，真实世界 S 继承不受影响。history 只解析可见前缀中的 feedback-saved ID；正文缺失标缺口，无引用报告另用 `--feedbacks` 查询，不按同 S/Context 补入父未来反馈。

## 7. 中断窗口

| 最后已知事实 | 恢复/阅读行为 |
| --- | --- |
| raw 已保存，主模型行未成功 | 暂停，raw 可能孤立；不扫描补入时间线、不重问模型 |
| 主请求已保存，无完整输出 | 结果未知，不声称未发送或已完成 |
| 主 response 有 act，无 Received | 候选输出，未证实宿主接收，不据此执行 |
| Received 后中断，无世界可证明结果 | 本分支补 Interrupted/ExactResult=false，不重做输入 |
| 世界 E 已发布、S 未完成 | Kernel 恢复 pending 后对账，不重问控制器/Plan |
| 世界 S 已完成、Completed 未保存 | 本分支补原结论/ExactResult=false，新 Sequence，不伪造原回执 |
| Completed 已保存，显示/详细材料未完成 | 结论保留，交付/捕获未知，不撤销世界 |
| feedback 已保存，关联/回执失败 | 保留成功 ID，不误报保存失败、不重交，独立管理可用 |
| 主日志损坏/刷盘不确定 | writer 退役、暂停、独立 stderr，不覆盖源文件 |

缺 run-end、off、未支持投影和附件不可用是覆盖限制，不承诺检测所有未记录事实。保存、回执生成、回执进入下一请求、远端处理是不同结论。

## 8. 旧数据与初始化

新路径 `host/events.jsonl` 不混写旧文件。**不承担旧宿主存档迁移**：检测到旧 operations/branch-operations 或旧 Gym 文件且没有有效新主日志，运行入口明确拒绝，提示保留旧 store、使用新 store 开始。预检位于 PlaygroundHost.RunAsync 的 PlaygroundDurableHistory.Open/Create 及主 journal 初始化之前，只读检查，禁止先创建新文件再用它的存在绕过旧格式检查。不能换个日志名后忽略旧 Pending IDs。旧工具通过 Git 获取，旧文件不删除/挪动/自动转换。

无旧宿主文件的领域/测试存档可首次建立主日志，run-start 明确覆盖起点（当前完成 S、此前历史未录制），不声称没有过去外部操作或拥有完整 history。主日志校验失败不能当作没有日志重建。世界新建与主日志初始化之间中断亦按覆盖起点处理，不将无主文件等同于新世界。旧世界子分支缺主日志切点就报祖先缺口。

`--feedbacks`、`--fork-feedback`、分支列表不受旧宿主格式检查阻塞；history/request-fork 只用新版主日志，找不到请求明确报错，不回退旧 Gym/raw。若以后必须保留旧世界宿主续局，另开显式离线迁移，不进入本轮双 reader/双 writer 主路径。

## 9. 软件落点与切片

| 组件 | 目标责任 |
| --- | --- |
| PlaygroundHostJournal | CLI 内唯一主 writer/readonly snapshot，typed records、校验、顺序和刷盘；复用小型底层实现，不建注册框架 |
| PlaygroundOperationAudit | 分支绑定 Receive/Complete/Reconcile 与增量 pending 索引，没有自己的文件 |
| 角色/模型阅读投影 | CLI 捕获/渲染帮助；模型部分只依赖 Completion.Abstractions，不用 provider codec |
| raw writer / 观战 presenter | 从 LlmRunAudit 拆出，宿主依序显式调用；文件写入不控制 Console 或其他 logger 生命周期 |
| CharacterHistoryExporter | CLI 选择前缀/角色、格式化主日志，可选查 feedback；不靠 raw 拼主体、不读可写世界 |
| GymCommands / Home / Program | 主路径、默认值和管理接线，更新帮助/init/配置及 smoke，保持管理命令名称 |

CharacterGym 不反向引用 Playground/CLI，可提供中立字面量/文件底层。移除 GymSession 复制/全扫、独立操作 stream、旧 exporter 原始 dump，不长期保留双运行路线。

1. **统一事实闭环**：主 journal＋操作审计＋Host/Console。Human 接收/结果/恢复/子分支隔离切新文件，off 仍保存必需事实；先以短操作与中断 fixture 验证，无 operation-ref。
2. **人物阅读闭环**：薄角色投影＋模型白名单内联＋raw writer＋exporter；共享 RunId，贯通 Human/LLM、多调用、反馈 Tail、延后 Opening。移走 raw 后主体仍可导出。
3. **管理故障闭环**：请求/反馈 fork、保存异常、模式默认、旧数据拒绝/覆盖起点、只读管理与实际 CLI smoke；更新说明，不保留临时双写。

三片均为交付要求，不把片一通过当作完成。内部类名可调整，来源/顺序/故障合同不能弱化；不留存储架构选项给实施者重选。

## 10. 验收矩阵

| 场景 | 必须证明 |
| --- | --- |
| 唯一存储 | 一个主 writer，不生成旧 operations/Gym 副本，Received/Completed 各保存一次 |
| 原操作合同 | 前置 Receive、刷盘后回执、同值重入无新行、异值拒绝、结果不可改写、不重做业务 |
| 恢复分区 | 子分支不恢复父 pending E、不补父 pending 操作；公共文件允许追加 child，父分支投影不变 |
| raw 独立 | 全 Human 无模型文件；移走全部 raw 后五类阅读及工具收据证据仍在主 history |
| 投影 | 明文、opaque带PlainText、null、多块、未知块；消息/块顺序与种类保留，无 native dump，Human 无虚构思考 |
| 多调用 | feedback收据进入下一请求再act；纠正重问；同 RequestId response+failure 不覆盖；旧 Tail 不是新输出，构造请求不等于远端接收 |
| 经历 | 同 Context 多操作、跨 Run 换控制器/Renderer、移动开始后延迟抵达、被动感知/Terminal，不猜因果或提前显示未交付经历 |
| Console | 连续查询/只反馈后EOF、元命令拒绝；混合控制不向玩家泄漏其他角色私有材料 |
| 故障 | 第7节窗口，writer退役、Dispose不遮蔽原异常、反馈成功ID保留、无自动重试或后续模型调用 |
| 分叉 | 前缀不含父未来/后来补录，不用当前操作索引补历史；feedback fork无主日志依赖且明确祖先缺口 |
| 只读 | 不修尾/恢复pending/初始化模型，源字节不变；完整行坏则失败、尾部缺口可见、输出不覆盖 |
| 模式/旧数据 | 持久默认record、off仍存操作、ephemeral无主文件且显式record拒绝；旧store预检拒绝后世界/旧日志字节不变、无新主文件，独立feedback管理不受阻 |
| 成本与领域 | 初始扫描后增量索引，不每步扫历史；Schema/GameRevision/游戏行为不变 |

至少运行 Playground.Cli.Tests、CharacterGym.Tests，按需扩充 CharacterControl.Completion.Tests；dotnet 同一构建图串行、Schema Verify。更新 Smoke-PlaygroundHome / Smoke-HillsideGym，再做小预算真实 LLM＋脚本 Console 混合运行，逐角色核对导出与真实调用/操作。fixture、代理操作 Console、真实模型、真人体验分开报告；文档阶段不执行这些验收。

## 11. 收敛结论与延期

前轮辩证审阅确认的薄材料、前缀、PlainText 白名单、同请求多阶段和反馈异常隔离继续有效；保留四类宿主文件并逐项引用的布局已被用户最新选择替代。此次减少一类持久宿主文件，删除 operation-ref 及其保存窗口；人物阅读不再需要模型文件 resolver，恢复与历史阅读仍有不同分支选择规则。

延期 SQLite/OTel/Langfuse 集成、分段/压缩、delta、自动摘要/检索/记忆回灌、屏幕录像、stream token、任意 S 重渲染、并发分支及跨文件事务，待测量或明确消费者再开。背景保留在[调查来源](../worksets/diagnostics-feedback-backlog.md)，本文为唯一实施方案。
