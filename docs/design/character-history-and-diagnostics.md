# 角色 History 与宿主诊断日志收敛

状态：2026-11-02，已完成三方独立审阅、交叉质询及剩余争议裁决；实施方案，尚未实施。本文承接[原调查待办](../worksets/diagnostics-feedback-backlog.md)，独立反馈保存合同继续由[独立宿主反馈](independent-feedback.md)维护；本文明确改变的录制默认值和 off 文件行为见第 4 节。

## 1. 目标、需求来源与边界

交付一个按角色与分支阅读的 history：能连续看到角色得到的观察、LLM 的可读 reasoning 和普通输出、尝试的操作、游戏实际回应，以及过程中提交的开发反馈。它帮助开发者研究经历管理和记忆召回；本轮不实现角色记忆检索，也不把诊断记录自动回灌角色。

| 需求 | 来源 | 设计约束 |
| --- | --- | --- |
| LLM 回合包含 Observation、reasoning、response、操作、世界回应；Human 没有 reasoning/response | 用户本轮明确要求 | 同一阅读视图支持两种控制器，不能从 Human 输入编造想法 |
| reasoning 直接使用 Completion 的 PlainText；文本文件或按需导出均可 | 用户本轮明确要求 | 选择按需 Markdown 导出，不再持久维护每个角色的一份完整副本 |
| 保留独立模型材料与独立 feedback | 用户此前方向、已实施反馈合同 | 不合并全部 JSONL，不把反馈依赖重新绑回 Gym |
| 实际模型请求、接收/结果配对、请求分叉前缀不能丢 | 当前代码与测试，见第 2 节 | 保存真实材料与精确引用，不按时刻或最后结果猜因果 |
| 串行宿主、串行模型决策、E/S 与日志独立提交 | 当前 PlaygroundHost、LlmDecisionLoop、Journal | 不设计并发总线或跨文件事务；故障不重做业务补日志 |
| 无兼容性包袱，但旧证据不能改写为新证据 | 用户此前授权及仓库实验习惯 | 新格式显式版本；旧文件保留原状，旧导出工具通过 Git 获取 |
| 正常运行能产出可用 history | 本方案选择，非用户此前指定的默认值 | 持久运行默认 record；off 只关闭详细角色轨迹，基本运行诊断保留 |

本轮仅写方案和审阅结论。以下接口、格式和命令是实施目标，不是当前 API。无需变更 Playground 世界 Schema、GameRevision、Kernel 或 Completion provider 协议。

## 2. 当前实现证据

- [PlaygroundHost / LlmRunAudit](../../src/Playground.Cli/PlaygroundHost.cs)：每次运行包括全 Human 都创建模型日志；审计类同时写文件、显示观战内容、转发 Gym 和结束 Gym。
- [GymSession](../../src/Playground.Cli/Gym/GymSession.cs)：复制 request/response 全载荷和已完成 operation；每次提交扫描全部历史操作；perception 保存当时渲染的保留材料。其 RunId 与 FeedbackSession 独立产生。
- [操作日志](../../src/Playground.Cli/PlaygroundOperationJournal.cs)：Receive/Complete 单独刷盘，冷开根据世界提交补对账，保留 Interrupted 和 ExactResult=false。每个 OperationRecordId 仅一个不可变 Received 和至多一个不可变 Completed；它还覆盖非领域操作，不能由世界 E/S 完全替代。
- [LlmDecisionLoop](../../src/CharacterControl.Completion/LlmDecisionLoop.cs)：一次角色服务可能含多次调用；反馈 response 之后还可能出现同 RequestId 的 Failure。act 返回不等于已执行游戏行动。模型流当前没有 observer，传输异常未必能保留部分输出。
- [角色 Context](../../src/Playground/CharacterContext.cs)：Opening、ItemSequence、OperationSequence、ExperienceSequence 已存在；耗时操作的 PreviousAdvance 和经历可能在下个 Opening 交付。只读当前 Context 会遗漏已经替换的回合。
- [GymTraceExporter](../../src/CharacterGym/GymTraceExporter.cs) 和 [GymCommands](../../src/Playground.Cli/Gym/GymCommands.cs)：请求分叉以 request.Sequence - 1 继承祖先证据；独立反馈分叉没有精确轨迹切点，不猜祖先材料。
- [GymIntegrationTests](../../tests/Playground.Cli.Tests/GymIntegrationTests.cs)、[GymTraceExporterTests](../../tests/CharacterGym.Tests/GymTraceExporterTests.cs)、[操作日志测试](../../tests/Playground.Cli.Tests/OperationJournalTests.cs)是迁移验收入口；脚本 Smoke-PlaygroundHome.ps1 / Smoke-HillsideGym.ps1 也消费现有日志布局。

上轮真实 proxy 运行的既有三次调用中，模型 input/output 共约 70 KB，对应 Gym request/response 约 76 KB；这是可定位的重复材料，不是性能基准。本轮只读复核，没有新模型实验。

## 3. 最小数据组织

保留四类宿主文件职责，加上已有世界 E/S；history.md 是导出产物。

| 文件 | 唯一保存责任 | 去掉的重复职责 |
| --- | --- | --- |
| 世界 repository | 世界事实、状态、角色保留材料、恢复与真实分叉 | 不承接外部模型审计或反馈正文 |
| operations.jsonl / branch-operations | 输入接收、确定的原始结果或补录结论 | 不承担模型思考和全部角色轨迹 |
| feedback.jsonl | 反馈正文、提出时边界、可选模型调用关联 | 不再在轨迹中复制正文 |
| llm-runs/&lt;RunId&gt;.jsonl | 每次实际 Completion 调用的输入、输出、工具往返及失败 | 删除 host/committed/terminal 以及独立的反馈正文副本 |
| gym/events.v2.jsonl（新版宿主轨迹） | run-start/end、角色材料快照、决定前边界、源记录引用、分叉切点 | request/response/operation 改为引用，完整载荷只留源文件 |

保留 gym 目录、项目名称和 `--gym-export` 命令；让现有导出命令升级为角色 history。新版轨迹文件独立命名，避免改写旧证据，具体策略见第 7 节。`--llm-log` 仍可覆盖模型文件位置。模型文件在首次实际调用前懒创建，全 Human 不生成模型文件。

宿主创建一个 RunId 并传给各记录器。RunId 是一次运行身份，不能替代 BranchId、RequestId、OperationRecordId 或世界提交号。不新建全局 StoreId；引用在显式选中的 store 内解释，Gym 的日志身份只用于防止误接另一份日志。

模型记录引用使用 `(RunId, RecordSequence)`，附带 RequestId 做一致性校验。一个 RequestId 可有多条 response/failure，不把它当唯一日志行。run-start 提供模型文件位置：store 内相对路径；外部路径为位置提示。缺文件显式报缺口，不自动全盘搜索；默认布局整体搬迁后可读。

操作引用使用 `(BranchId, OperationRecordId, Part=Received|Completed)`，核对 ActorId 和对应记录身份。现有两个部分均不可重写，因此无需另暴露物理行号；只解析轨迹明确引用的 Part，收到 Received-ref 不准顺带读取后来出现的 Completed。操作日志增加只读 snapshot 与成功 append 后的窄通知，保留现有刷盘/对账合同。反馈按 FeedbackId 精确关联，正文不复制；成功保存并不证明反馈收据交付。

共享部分限于模型/轨迹采用现有 GymJournal 的中立 envelope 或小型底层读写 helper；不借机统一重写 operations/feedback，尤其不为 feedback 新加 hash chain。不建立订阅总线、全局事件类型注册表或通用数据库。游戏字段、分支解释和导出组装留在 Playground.Cli，CharacterGym 保留无游戏依赖的文件/阅读基础能力。

## 4. 记录内容与时间顺序

### 4.1 宿主轨迹的最小记录

| 种类 | 必须保留的内容 |
| --- | --- |
| run-start/end | RunId、BranchId、控制绑定、预算、内容/构建身份、模型文件位置、record/off、结束原因及最后完成 S；开始与结束允许不配对 |
| perception/terminal | ActorId、ContextSequence、Status、捕获时完成 S、下述薄阅读投影；明确标记 retained-material，不宣称真人实际看过 |
| request-ref | ActorId、完整决定前 Boundary、模型源记录引用 |
| model-result-ref | ActorId、模型源记录引用；通过原记录 RequestId 关联请求，保留多阶段失败顺序 |
| operation-ref | ActorId、Received/Completed 精确 Part 引用、捕获时完成 S；该 S 不冒充原始操作提交点 |
| feedback-ref | ActorId、FeedbackId；仅在轨迹可用时追加，反馈保存成功不依赖它 |
| branch-fork | 现有来源分支与 SourceThroughSequence，保留请求切点合同 |

record 捕获薄阅读投影，直接从已提交 CharacterContext 生成，不从旧 Material 字符串反解析：

- `ObservationText`：捕获时角色的冻结观察阅读文本；不是完整世界快照。
- Opening 材料片段：当时渲染文本、已有 ExperienceSequence 列表，以及可空 PreviousAdvance 的 SourceContextSequence、OperationSequence、OperationRecordId。它只证明该 Opening 承接哪次操作，不证明每条自然经历都由该操作造成。
- 有序 Items 材料片段：ItemSequence、可空 OperationSequence/OperationRecordId、已有 ExperienceSequence 列表、当时渲染文本；保留拒绝和目标操作的显示结果。

这些是宿主 JSON 阅读 DTO，不新增 DurableType，不复制整个领域对象或重算可见世界。Renderer 在捕获时产出片段；以后更换 Renderer 不改变已保存文本。每次仍保存一份完整投影快照，不实现 delta 存储；导出用既有身份识别重复 Opening/Item，已有经历 ID 仅作为引用，不从多经历片段里猜出单条因果。只有身份和文本均相同时才折叠重复展示；同一领域片段在另一 Run/构建产生不同文本时保留再次捕获并标注呈现变化，不覆盖旧文本、不误报世界损坏，不新增 RendererId。结构身份或源记录内部不一致才报告异常；当前 Observation 可随提交变化，不按 ContextSequence 独自去重。

LLM 请求中再次带入此前材料属于请求事实，不做跨请求压缩。世界材料投影与请求文本即使相同，职责不同，允许这部分重复。实际请求指提交给 Completion 的结构化请求，不宣称是 provider 最终 HTTP 字节。

同理，模型 request/response 自带的反馈正文和工具收据保留原样；“feedback 正文不复制”指不额外写一份报告对象，不是删除真实调用中的相同文字。人物 history 的请求/Tail 阅读始终按消息和 block 类型白名单投影，未知 block 只显示类型/缺口，不能用原始 JSON dump 兜底。

录制选择在配置解析时保留“未指定”与显式值的区别，运行时仍只有 off/record 两种模式：

| 运行 | 有效默认值 | 文件及覆盖范围 |
| --- | --- | --- |
| 持久 store，未指定 gym | record | 新版轨迹基本记录与详细材料；operations、feedback、按需模型文件各司其职 |
| 持久 store，显式 off | off | 新版轨迹只保存 run-start/end；不捕获人物材料/源引用，导出列出关闭区间 |
| ephemeral，未指定或显式 off | off | 不创建 runtime/Gym 文件；基本状态只输出 Console，模型与反馈仍按各自合同保存 |
| ephemeral，显式 record | 拒绝 | 沿用详细轨迹需要 store 的约束，不悄悄降级 |

这是有意改变当前默认 off、off 不创建 Gym 的行为：宿主基本诊断从原来总写入的 LlmRunAudit 移到同一新版轨迹；off 仅关闭详细采集。ephemeral 不再持久保存基本 host 审计，也是明确收缩，不能声称旧行为不变。配置/世界验证失败前不为这些新功能提前建日志。不能把 off 的请求/操作按当前 head 补成完整 history。程序化无 home/store 入口可注入内存记录用于测试，不发明文件位置。

### 4.2 写入序列

record 下的 LLM：捕获决定前边界 → 模型 request 完整落盘 → 轨迹 request-ref 落盘 → 调用模型 → 模型 response/failure 完整落盘 → 轨迹引用 → 若 act 有效则 operations.Receive → 世界 E/S 提交 → operations.Complete → 轨迹 operation/perception。off 跳过详细轨迹步骤，模型/操作自身的保存合同不变。

feedback：模型 response 或 Human 当前输入 → feedback 单独落盘 → 返回已保存 ID → 保存边界之外记录附属引用/呈现回执。LLM 后续实际请求带入工具结果才证实收据进入该请求。FeedbackSession 不调用轨迹 writer；保存后的附属引用失败不能从 SaveHuman/SaveLlm 抛成保存失败。LLM 复用现有保存 try/catch 之后的 Feedback trace；Human 在成功 ID 已显示之后、保存 catch 之外调用窄的反馈已保存通知。引用失败作为独立诊断暂停，保留已知 ID，不再提交反馈或调用模型补回执。

Human：捕获当前角色材料 → 展示/读取输入 → 非 feedback 输入先 Receive → parser/元命令或领域行动 → Complete → 轨迹引用。Console 一次 ChooseCommandAsync 内可执行多个查询/元命令，不等到该方法返回才捕获全部结果；通过 journal 成功 append 后的窄通知同步记录新增 Part。通知在源行刷盘并安装到内存后触发，失败不会撤销该源记录；幂等重复 Complete 不发出伪新增通知。

冷开：恢复 pending 世界 E/S → 仅对账已有操作 → run-start → 将此次新补录的 Part 标为 reconciliation 并记录引用 → 捕获当前角色材料 → 服务控制器。Reconcile 返回本次新增项或由窄通知暂存它们，不能在每个 Step 扫描累计 Entries。补录前不得初始化 provider。历史结果补录与历史原回执分别标识，不回写旧轨迹，不重新执行操作；缺失旧 Received-ref 时不伪造当年的接收时刻。

## 5. Character history 的阅读合同

使用现有管理入口：

```powershell
dotnet run --project src/Playground.Cli -- --home artifacts/my-world --branch <id> --actor a --gym-export artifacts/a-history.md
```

只有一个分支时可省略 branch。导出只读，不初始化模型、不恢复 pending、不修尾、不修改世界，输出使用 CreateNew 防止覆盖旧证据。

### 5.1 组织方式

先按分支继承规则收集有效轨迹，再筛选角色、解析精确引用，最后按轨迹 Sequence 排序。分组键为 `(来源 BranchId, ActorId, ContextSequence)`；只为相邻材料加分组标题，不先全局 GroupBy 后重排时间线。RunId 标注续局/控制器变化，不把同一个 Context 的不同调用压成一个“回合”。模型调用以 RequestId 分小节，失败/反馈调用也保留；无模型调用的被动经历仍须出现。

阅读视图示例（仅示意，不是真实模型材料）：

```text
角色 a · 分支 … · Context 12 · D1 08:00
  Observation [retained material / S …]
    当前位于林地，背包里有两份食物……
  LLM 调用 [Run … / Request … / Connection …]
    Observation [actual request]: 本次发送的观察材料
    Reasoning [provider PlainText，可能为摘要]: …
    Response [模型普通输出，非角色对白]: …
    操作尝试: act({command: "go forest-town"})
    世界回应 [Operation … / S 或领域事件引用]: 移动开始
  后续经历 [Experience … / 交付于 Context 13]
    抵达小镇
```

“移动开始”与之后“抵达”分开保存、分开呈现。只根据已有 PreviousAdvance、OperationSequence、OperationRecordId、ExperienceSequence 关联，不能把时间上紧邻的所有事件都归因于上一条命令。反馈和 Console 元命令标为宿主交互，不冒充世界动作。

### 5.2 五类材料的来源

| 阅读部分 | 来源与缺失行为 |
| --- | --- |
| Observation | LLM 展示保存的实际请求 Shared/Tail 中 observation，系统指令/工具定义放附录；另保留角色材料作为经历视图。Human 展示捕获的角色材料，标注不等于屏幕逐字实录。不得把全部 Tail（含 assistant/tool）扁平化成观察 |
| Reasoning | 从结果 Blocks 中按原顺序取 ReasoningBlock.PlainText；null/空表示未提供可读内容，不能写“未思考”，不解密、不额外调用模型生成摘要 |
| Response | 只取 ActionBlock.Text，保留 block 顺序/位置；空文本写“无普通文本输出”，工具调用单列。普通输出不是自动对其他角色说话 |
| 操作 | 模型工具尝试与已接收游戏操作分开；可读正文以角色命令为主，关联 ToolCallId/RequestId/OperationRecordId。无效协议输出、未提交、拒绝与成功区别显示 |
| 世界输出响应 | 已保存的操作结果及角色 Context 的 Opening/Items/Experiences；结果要携带 ExactResult/Interrupted 语义。被动经历、同 tick 多次转换和 Terminal 均不丢 |

Human 小节完全省略 Reasoning/Response，不填猜测或占位推理。LLM incomplete/failed 的可见输出可作为诊断材料显示，但不得列为已成功决定；transport 中断无完整 Result 时明确“部分输出未捕获，远端结果未知”。本轮不增加流式 token 录制。

`PlainText` 的合同是 provider 可读视图，可能为明文或摘要，不能承诺是全部推理，也不强造“完整/摘要”的可靠分类。只消费 Completion.Abstractions 公共 API；现有 RequestReasoningText 默认 true。模型原生签名/加密 payload 可留在专用审计，不进入角色 Markdown 正文、不跨协议回放。

history 是开发者视图，不是角色拥有的完整记忆；不自动注入 LLM，不改变角色可见范围。按 actor 筛选后仍可能包含该角色私有材料、开发反馈和 provider 输出，文件明确标注用途。原文使用安全 Markdown fence，避免模型文本改变文档结构；不输出凭据。

### 5.3 Feedback 导航、缺口与归档

在人物轨迹中按 FeedbackId 标出报告，并展示提出时 Boundary；LLM 通过 RequestId 关联，Human 通过实际保存处的轨迹位置关联。只纳入已选择轨迹前缀内的 feedback-ref，不额外扫描同 S/Context 的祖先报告：父分支切点之后的反馈也可能处于同一个 S。没有精确引用时提示可另用 `--feedbacks` 查询，不能补成时间线或混入“相关报告”。从报告可以定位现场和前后材料，但正文描述的历史问题不自动认定为当场发生。

缺失模型文件、源行、未完成尾、无结果、录制关闭、未观察到的收据交付、未知祖先前缀分别列入“证据缺口”。不将缺口当作世界未执行的证据。完整行损坏明确报错；可忽略的未完成尾要报告字节数。选择的分支/角色不存在则报参数错误，不产出空成功文件。

整体搬迁 store 应继续解析默认相对路径。外置 `--llm-log` 缺失时仍可导出已有角色材料并列缺口；首版不实现归档打包器或路径映射系统。已解析的 Markdown 是自足阅读产物；重新导出和复现仍需要源证据与世界 repository。

## 6. 故障、持久性与分叉

模型日志改用完整行、顺序号、校验链和 Flush(true)，达到被删除的 Gym 完整副本原有持久性。源行成功后才发布引用。任一写入失败退役相应 writer；不得对不确定 append 自动重试。不要求四个文件原子提交：源已保存而引用缺失不代表源没有执行，不重新调用模型补齐。缺少 run-end 或记录诊断失败时，history 明示覆盖可能不完整；不保证仅靠引用检测所有未被引用的源行，也不把这些源行扫描后补入历史前缀。

运行诊断失败默认暂停宿主，stderr 独立报告最小错误；不能再依赖已故障的日志写 host-stop，造成原异常被覆盖。世界已完成 S 的事实不受日志失败撤销；下一次运行仅按现有恢复合同对账。feedback 的保存回执强度不受可选轨迹失败改变。

模型 request 落盘不证明网络发送；request 无 response 表示结果未知。成功结果落盘不证明游戏已接收 act；operations 收到不证明世界已提交。导出保留这些分界。

请求分叉继续使用 `request-ref.Sequence - 1`，子分支不继承切点请求及其后来的响应。必须先截断轨迹再解引用，禁止先按全 store 的 RequestId/OperationRecordId 做最新值联接。source Completed 行即使后来补录，也不能在历史前缀没有对应引用时混入子分支。

独立 feedback fork 仍不依赖轨迹文件；没有精确切点就报告祖先证据缺口，世界 S 的真实继承不受影响。本轮不为它新增反向推断切点协议。只读导出不打开可写世界；只读世界状态辅助导出与从历史 S 重新渲染完整回合留待真实需要。

## 7. 实施落点与纵向切片

建议在 Playground.Cli 新建 `CharacterHistoryExporter` 负责分支选择、来源解析和人物阅读投影，`GymCommands` 调用它；复用旧 exporter 中中立的前缀选择与 Markdown 字面量代码，不先把游戏依赖加进 CharacterGym 再搬回。`LlmRunAudit` 拆为模型 writer 和观战 presenter，宿主显式接线 Gym/反馈/操作通知。具体内部类名可调整，不增加公共跨仓 API。模型投影只依赖 Completion.Abstractions，CharacterGym 不引用 Playground 或其 persistence。

| 切片 | 修改与消费者 | 完成判据 |
| --- | --- | --- |
| A：可阅读的角色 history | 从 CLI 捕获第 4 节薄投影并实现 CharacterHistoryExporter；新版轨迹、共享 RunId、独立耐久模型 writer/request 引用一起接通。Human + LLM、PlainText、多调用和延后结果贯通 | 单个角色跨回合可阅读，材料来自实际捕获；正文白名单投影，无原始 Blocks/Tail 附录，不依赖旧 Material 猜关联 |
| B：操作与宿主职责收敛 | 操作 journal 的只读 snapshot/Part 引用/新增通知；反馈保存边界外通知；独立观战 presenter；冷开补录与跨运行/分支导出 | 模型载荷与操作结果不再在 Gym 复制，人物五类材料及反馈链完整，长跑不再每次全扫历史 |
| C：管理与故障闭环 | 默认/开关语义、只读管理、反馈关联、搬迁/缺文件、fork/补录/损坏、smoke 与文档更新 | 下列验收矩阵通过，旧证据仍原样保留，默认启动和真实 CLI 可用 |

A 是最小可检查纵向切片；全部完成条件仍包括 B/C，不以 A 完成冒充整个重构完成。直接实施新格式，不先造未使用的日志框架、不长期保留两套新旧写入器。中间 fixture/试验产物用独立 artifacts 目录，不作为发布后兼容负担。

新版 writer 固定写 `gym/events.v2.jsonl`，格式头显式 v2；旧 `gym/events.jsonl` 和旧模型文件原样保留，新模型文件按 RunId CreateNew。现有世界可直接续局：从当前完成 S 开始新版 run-start/材料捕获，并标记此前轨迹未纳入；不转换、不读取旧版前缀、不猜旧 fork 切点。新管理命令只使用新版轨迹，旧证据通过 Git 中旧工具读取；未找到请求 ID 时提示可能属于旧版记录，不能自动选最新请求替代。独立 feedback 管理与原有 operations 格式无需为此迁移。旧世界分支缺少新版 branch-fork 时同样报告祖先证据缺口。

## 8. 验收矩阵与完成条件

| 场景 | 必须验证的事实 |
| --- | --- |
| Human、LLM 与同角色续局换控制器 | Human 无 reasoning/response；LLM 有完整五类材料与来源；Run/Context 不混淆；全 Human 不创建模型文件 |
| reasoning | 明文、opaque 携带 PlainText、无 PlainText、多 block；不调用 provider codec 解密、不显示 native payload；Response 只取 Text |
| 多次调用 | feedback → 工具回执 → act；协议失败 → 修正 → act；同 RequestId response 后 failure 均保持次序 |
| 世界结果 | 操作成功/拒绝/Interrupted、ExactResult=false、Console 查询/元命令；移动开始与后续抵达、被动经历、终局独立记录；同 tick 多转换不误合并；只展示角色已保留/交付材料，Closed 期间未交付经历不伪装已观察 |
| 控制台 | 输入 EOF、只反馈后退出、连续查询后退出也有记录；不需要等游戏 Step 完成才保存；混合控制不把其他角色材料打印给玩家 |
| 故障 | source 成功/ref 失败、模型输出后/Receive 前、世界 S 后/Complete 前、feedback 保存后/ref 失败/回执前；Human 已知成功 ID 保留、LLM 不误报 feedback-save-failed 且不继续调用；不自动重试外部调用或业务 |
| 分叉 | 请求切点不纳入父未来；冷开补录不倒灌；独立反馈 fork 无轨迹依赖且显式祖先缺口 |
| 只读/搬迁 | 不恢复 pending、不修尾、不加载凭据/调用模型；源字节不变；默认相对路径搬迁有效，外置源缺失明确报告 |
| 去重与成本 | 原请求载荷、原操作结果各一个保存来源；观战输出与写文件解耦；新增操作捕获不全扫累积 Entries |
| 边界 | 世界 Schema/GameRevision/行为不变；feedback 默认独立；record/off、ephemeral 覆盖明确；旧 store 续跑只创建新版轨迹，不改变旧证据字节 |

受影响工程：CharacterGym.Tests、Playground.Cli.Tests，必要时 CharacterControl.Completion.Tests；按依赖图串行 dotnet，使用 Schema Verify。更新 Smoke-PlaygroundHome / Smoke-HillsideGym 并新增或扩充混合控制 history smoke。最后用一次小预算真实 LLM 加脚本 Console 运行，检查导出的人物故事链和原始调用对应；fixture、代理操作 Console、真实模型、真人体验分别报告。文档阶段不执行这些测试，也不把既有在线日志当作新验收。

## 9. 辩证简化裁决

三位审阅者分别担任需求质疑、最小架构和语义守护，先独立查源码，再交叉质询；仅对 off 的基本运行记录追加第 3 轮。依据是实际代码/消费者，不按人数投票。结论直接进入上述正文，不将分歧原稿当作实施待办。

| 裁决 | 收敛结果与原因 |
| --- | --- |
| simplify | 操作引用从物理 JournalSequence 改为既有 OperationRecordId + Part；两个部分不可重写，仍防止父分支后来结果倒灌。取消一个额外定位合同 |
| keep | 薄材料投影现在就做；旧 Gym 仅有字符串，无法凭空得到 Item/Experience/PreviousAdvance 身份。它不成为新领域历史 |
| merge | host 起止诊断移入新版轨迹；模型审计只保留模型职责。off 仍有原宿主诊断的唯一归宿，无需第五类宿主文件 |
| delete | 完整 raw payload 附录删除；人物正文只用 PlainText/普通输出，native 留源文件。删除按 S/Context 补接祖先 feedback 的隐含规则 |
| simplify | 从首个切片即在 CLI 组装人物历史；直接新文件版本，不先兼容旧字符串再搬层、不改项目/命令名 |
| keep | 模型源耐久性、按可见前缀解析引用、同 RequestId 多阶段记录、反馈保存与附属日志异常隔离，都有现存失败序列支持 |
| defer | delta 存储、全量 StateJournal 重渲染、自动摘要和记忆回灌，均不属于本次最小阅读闭环 |

其中 off 曾有真实分歧：保留旧“off 不建 Gym”会在模型文件纯化后丢掉已存在的 host-stop/recovery/config 事实。Host 源码证明这些记录不依赖 Gym；HillsideGym 脚本在 record 下消费暂停原因，Home 脚本消费全 Human/ephemeral 审计，不能把前者误说成 off 验收。最终选择第 4 节的明确行为变更；ephemeral 不为基本记录扩建无持久边界的日志系统。没有留下阻挡实施的技术选项；用户若调整默认录制体验，只需修改该表及对应验收，不影响来源和因果合同。

## 10. 延后项目

角色记忆回灌/检索、自动摘要、向量数据库、完整屏幕录像、stream token 持久化、跨请求内容寻址压缩、可移植证据打包、任意 S 重渲染、全局事件总线、多进程并发写入均延期。只有明确消费者或测量证明首版无法满足阅读/诊断时再重开，不用本轮 history 预建这些设施。
