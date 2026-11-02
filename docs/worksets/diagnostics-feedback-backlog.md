# 后续待办：宿主日志去重与统一诊断反馈

状态：2026-11-02，独立反馈及[统一宿主日志与角色 history](../design/character-history-and-diagnostics.md)均已实施，见[统一日志验收](../experiments/character-history-and-diagnostics-acceptance.md)。本文仅保留启动前调查与历史候选，不维护第二份施工清单。独立反馈现行合同见[独立宿主反馈](../design/independent-feedback.md)。

## 1. 用户观察与方向

- 当前几份 JSONL 日志存在职责和内容重叠，后续需要重新梳理，尽量减少重复存储和重复维护。
- 这项工作单独设计、单独重构，更适合在 home/CWD 与角色控制重构之后启动；不要把尚不清楚的日志决策塞进当前切片。
- 将反馈能力从 LLM Controller 的专用工具中独立出来，使 LLM Controller 与 Human Console 都能提交开发反馈。
- 反馈时由宿主自动捕获诊断、定位和复现所需关联信息，典型是相关 ID 和 address；操作者主要描述问题，不应手抄、猜测内部定位值。
- `llm-runs/*.jsonl` 内容细致且模型专用，保持独立性合理；“减少重叠”不等于把所有东西装进同一个文件。
- 其他日志如何分工、是否合并、如何引用，用户尚未选定；本文记录候选方向，不提前冻结最终结构。

## 2. 独立反馈启动前的职责快照

以下为独立反馈启动前的源码调查快照，不是新接口；当前反馈保存与管理以[独立合同](../design/independent-feedback.md)为准。路径均相对 store；`--llm-log` 可以将本次模型审计放在外部路径。

| 载体 | 当前保存内容与消费者 | 当前开关/生命周期 |
| --- | --- | --- |
| DurableGraph 世界 E/S | 世界事实、状态、游标及角色记忆；游戏恢复和分支的权威 | 持久运行正常提交，不受 Gym 开关控制；不属于本次 JSONL 合并对象 |
| `operations.jsonl`、`branch-operations/<branch>.jsonl` | 输入接收及结果配对；Console 查询/拒绝/元命令也可记录；冷开与已提交游戏操作对账 | Human/LLM 宿主独立于 Gym 使用；根分支与子分支分别记录 |
| `llm-runs/<run>.jsonl` | 实际模型请求/响应、工具输出、协议错误、调用身份；也包含 host/committed/terminal/停止等宿主材料 | 当前 LLM 宿主每次生成独立文件，不受 `--gym` 控制；全 Human 的旧 PlayConsole 不走这条路径 |
| `gym/events.jsonl` | metadata、run-start/end、perception/terminal、request/response、operation、feedback/receipt，以及分支关联 | 当前 `--gym` 开启记录，`--feedback` 还开启模型工具；同 store 的运行、角色、分支共享序列，通过 ID 区分 |

`gym/events.jsonl` 的 events 指实验记录，不是完整世界事件流。`perception` 是捕获时角色保留的材料，不能等同于模型实际收到的 request 或真人已看到的屏幕。模型返回 act 不等于领域接受或提交成功。

原调查中的 PlaygroundOperationJournal、LlmRunAudit、GymSession、GymJournal 和 GymTraceExporter 可从重构前提交 `c0e1880` 检出；它们已从当前运行路径和源码撤下。现行实现入口：

- [PlaygroundOperationAudit](../../src/Playground.Cli/PlaygroundOperationAudit.cs)：接收/结果身份、本分支恢复对账及 `ExactResult`，委托主 writer 保存。
- [PlaygroundHost](../../src/Playground.Cli/PlaygroundHost.cs)：统一运行时，依序接线独立 raw、主投影和 Console 呈现。
- [PlaygroundHostSession](../../src/Playground.Cli/PlaygroundHostSession.cs)：完成 S、运行/模型/反馈关联和角色材料捕获。
- [PlaygroundHostJournal](../../src/Playground.Cli/PlaygroundHostJournal.cs)：共享序列、校验链、完整行刷盘、增量操作索引及尾部处理。
- [GymCommands](../../src/Playground.Cli/Gym/GymCommands.cs)、[人物导出器](../../src/Playground.Cli/CharacterHistoryExporter.cs)：按角色/分支导出、反馈定位与 fork。
- [使用说明](../design/character-gym/usage.md)：当前查询/导出/fork 入口；当前用法已更新，历史实验命令保留原语境。

## 3. 已发现的重叠与不能机械合并的地方

**模型材料重复。** 同一 `LlmControllerTrace` 写入运行审计的 model-input/output，又写入 Gym 的 request/response；其中请求消息、结果块和实际连接身份存在重叠。未来可以研究让专用模型日志保存完整载荷，其他记录只引用，但需要先证明关联、导出、归档与分支前缀仍可靠。

**操作材料重复。** 操作日志保存 Received/Completed；Gym 又复制已完成操作；运行审计还写 committed 摘要。它们目前服务不同消费方式，去重前必须分别识别：接收记录、实际业务提交、对操作者的结果回执、事后补全记录。不能把“记录了操作”统一解释为“已提交世界行动”。

**角色材料与运行元数据重复。** Context 作为角色状态已在世界中保存，Gym 捕获阅读材料，模型请求中还可能包含同一段渲染文本；run-start/host、终局与暂停信息也有重叠。需要决定哪些属于可派生视图，哪些必须保留当时实际发送的字节/材料。后续 renderer 变化意味着“能从世界重新渲染”不保证等于历史模型输入。

**持久性强度不同。** 操作日志和 GymJournal 有完整行校验链、刷盘及失败实例退役规则；LlmRunAudit 当前是每次运行的独立 JSONL 文件，使用 AutoFlush，未采用同一协议。不能因为格式都是 JSONL 就互换，或在删除一份记录时无意削弱接收/回执保证。

**恢复职责不同。** 游戏 E/S 决定哪些行动已经提交。操作日志在 pending E 恢复之后补对账，缺失结果可以标为 Interrupted 或 `ExactResult=false`，绝不重做操作；Gym 的 `operation.StateCommitId` 是捕获时边界，不保证就是该操作原始提交点。重构时不能让一个简化的“最新状态地址”覆盖这层因果差异。

**分叉与证据前缀。** 现有 fork 使用已完成的决定前 S，不是任意日志行或任意 E；子分支导出包含切点以前的祖先证据和自己的后续，不混入父分支未来。若改为跨文件引用，引用解析也必须遵守这项边界。只读管理不修尾、不恢复 pending、不调用模型；fork 本身则是显式写操作。

## 4. 统一反馈的已定切片与历史设计起点

用户已选择最小独立切片：Human `:feedback <text>` 与 LLM `feedback({text})` 共用 FeedbackSession，自由正文默认上限 4000 个 UTF-16 code unit；store/feedback.jsonl 懒建，ephemeral 写 home 且无可分叉 Boundary。宿主捕获提出时的角色、Context、完整 PlaygroundDecisionBoundary 和可选实际模型调用；刷盘后返回 ID，失败退役且不自动重试，保存与回执交付分别判断。

`--feedbacks` / `--fork-feedback ID` 脱离 Gym；反馈分叉直接 Inspect/Fork，不读写 Gym。无精确 Gym 截点不猜祖先前缀，导出标缺口；请求分叉保留精确前缀。`--gym` 仅 off/record，旧 `--gym-feedbacks` / `--gym-fork` 移除。详细字段与失败语义仅在[设计](../design/independent-feedback.md)维护。

以下定位调查仍供日志去重参考，不增加本切片字段或承诺历史问题自动定位。

当前可复用的定位信息：

| 关联层次 | 已有信息 | 必须区分的含义 |
| --- | --- | --- |
| 运行与分支 | RunId、BranchId、审计路径、构建哈希、GameRevision、内容 ID/指纹 | 路径可能搬动；代码版本/定义相同不保证远端模型行为相同 |
| 世界边界 | StateCommitId、LineageId、TransitionCount、TimeTicks | 保存状态、因果序号与模型时间不同；同一个 tick 可有多个转换 |
| 角色决策 | ActorId、ContextSequence、OperationSequence | 角色材料边界与请求次数不同，不合并编号 |
| 操作 | OperationRecordId、BatchId、ToolCallId、WorldVersion、DomainEvent、ExactResult | ToolCallId 可能跨请求/运行重复；补全结果不能假装为原始回执 |
| 模型调用 | RequestId、CallNumber、ConnectionId、ModelId、ApiSpecId | 仅适用于真实模型调用；不应成为 Human 反馈必填项 |
| 报告/证据行 | FeedbackId、Gym Sequence、已有校验链 | 报告已保存与回执实际交付是两个事实；文件行号也不应自动等同业务身份 |

[PlaygroundDecisionBoundary](../../src/Playground/Persistence/PlaygroundBranchBoundary.cs)已经把分支、S commit、角色与决定前序号组合起来；Capture/Inspect 会验证其能否作为该角色的决定前边界。该结构可作为复现关联的起点，但不要强迫任意时刻的反馈都符合“可立即 fork 的决定前 S”。无法取得该边界时应报告证据缺口，而非伪造定位。

**ID/address 后续必须核实作用域。** 当前 Gym metadata 的 StoreId 是 GymJournal 自行生成的日志身份，不可直接当作 DurableGraph repository 的全球唯一身份；BranchId/CommitId 按当前代码约定作用于原 repository。用户提到的 address 可以包含底层物理定位，但本轮没有建立物理 FrameAddress/EventAddress 的公开采集合同。先查是否确有消费者、是否能从逻辑 commit 定位、搬迁/分叉/压缩后的有效性，再决定是否采集；不以本地文件路径或单一裸地址承诺可移植复现。

“可复现”先指能找到正确世界边界、角色材料、操作及实际模型证据，必要时从支持的边界另开实验分支；不承诺重跑外部模型能得到相同回答。诊断附件不保存凭据，不把其他角色私有材料自动显示给提交反馈的玩家。

## 5. 启动前比较过的日志组织候选

| 候选 | 可能收益 | 下次必须证明的问题 |
| --- | --- | --- |
| 先保留现有文件边界，明确每类记录的唯一来源，其他视图引用或派生 | 迁移较小，可先消除模型载荷/操作复制 | 独立查看/归档如何补齐引用；损坏或缺文件时如何标注缺口 |
| 将非模型的操作、轨迹索引、反馈整理成一个宿主日志，模型细节保持独立 | 减少跨文件对账与重复元数据 | 可选轨迹开关不能关闭必要操作记录；保存回执、分支隔离、持久性与故障边界如何保持 |
| 保留具有恢复对账职责的操作日志，另设诊断/反馈索引引用世界与模型证据 | 运行可靠性与可选实验材料职责清楚 | 是否只是换名后继续重复；索引可重建范围、写入时序与最小字段是什么 |

这三项是历史候选，不要求各做一个原型。用户最新选择已进入[统一宿主日志方案](../design/character-history-and-diagnostics.md)：operations 与 Gym 合并为同一有序日志，直接保存操作事实和模型可读投影，人物主体导出不依赖 raw 文件；完整模型审计与反馈独立。早先保留全部文件边界、逐项引用的方案已被替代，不保留为并行实施选项。

反馈默认独立保存，Gym off 不禁止 feedback.jsonl；后续方案不改变这一保存合同。方案提出的默认录制和 off 基本运行记录行为在当时方案阶段尚未实施，现已由统一日志实现。

## 6. 后续实施入口

以[角色 History 与统一宿主日志](../design/character-history-and-diagnostics.md)为唯一实施方案：第 1–2 节记录需求和依据，第 3–8 节规定保存、阅读、恢复/分叉及旧数据边界，第 9–10 节给出实施切片和验收矩阵。后续完成时直接收尾该方案中的施工项，不在本文追加逐轮进度。

本文调查快照和旧实验材料不作为本次运行证据；独立反馈的既有验收仍由其专门文档承载。本轮方案阶段无代码修改、工程测试或在线模型调用。
