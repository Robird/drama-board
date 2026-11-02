# 后续待办：宿主日志去重与统一诊断反馈

状态：2026-11-02，独立反馈已实施，合同见[独立宿主反馈](../design/independent-feedback.md)，验收见[实施验收](../experiments/independent-feedback-acceptance.md)；本文保留日志去重的延期方向与启动前调查快照。日志布局仍未裁决，不随反馈切片合并 JSONL，也不作为 [home / 角色控制重构](../design/playground-home.md)的附加验收条件。

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

关键实现入口：

- [PlaygroundOperationJournal](../../src/Playground.Cli/PlaygroundOperationJournal.cs)：接收/结果身份、刷盘、恢复对账及 `ExactResult`。
- [PlaygroundHost / LlmRunAudit](../../src/Playground.Cli/PlaygroundHost.cs)：运行审计、模型 trace、提交摘要；当前还将 trace 转发给 Gym。home 重构已将 Human/LLM 收敛为统一宿主，日志职责重整仍属本待办。
- [GymSession](../../src/Playground.Cli/Gym/GymSession.cs)：实际请求与决定前边界关联、反馈保存、Context/操作捕获。
- [GymJournal](../../src/CharacterGym/GymJournal.cs)：共享序列、校验链、完整行刷盘及尾部处理。
- [GymCommands](../../src/Playground.Cli/Gym/GymCommands.cs)、[轨迹导出器](../../src/CharacterGym/GymTraceExporter.cs)：按角色/分支导出、反馈定位与 fork。
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

## 5. 日志组织候选，尚未裁决

| 候选 | 可能收益 | 下次必须证明的问题 |
| --- | --- | --- |
| 先保留现有文件边界，明确每类记录的唯一来源，其他视图引用或派生 | 迁移较小，可先消除模型载荷/操作复制 | 独立查看/归档如何补齐引用；损坏或缺文件时如何标注缺口 |
| 将非模型的操作、轨迹索引、反馈整理成一个宿主日志，模型细节保持独立 | 减少跨文件对账与重复元数据 | 可选轨迹开关不能关闭必要操作记录；保存回执、分支隔离、持久性与故障边界如何保持 |
| 保留具有恢复对账职责的操作日志，另设诊断/反馈索引引用世界与模型证据 | 运行可靠性与可选实验材料职责清楚 | 是否只是换名后继续重复；索引可重建范围、写入时序与最小字段是什么 |

这三项只用于下一轮比较，不要求各做一个框架或原型。不预先决定合并文件数量、新日志名称、通用事件总线、数据库或统一日志框架。`llm-runs` 独立不意味着永久保留其所有 host/committed 重复内容；专用模型载荷与通用宿主信息可以分别裁决。

反馈与轨迹关系已确定：反馈默认独立保存，Gym off 不禁止 feedback.jsonl。其余日志去重候选仍未裁决。

## 6. 后续工作入口与验收方向

独立反馈切片已实施并通过工程与脚本 CLI 验收；以下日志去重工作仍待用户另行启动。相关反馈验收由独立设计承载，不以此清单暗中扩大施工范围：

- [ ] 在 home/控制重构后的实际源码上重查所有 JSONL 写入点、读者、导出器、smoke 脚本及现有证据，列出“记录种类 → 唯一权威/副本/派生视图”。
- [ ] 为 Human 当前输入反馈、LLM 决策中反馈、失败操作反馈、冷开补对账、请求边界分叉各画一条实际顺序；明确缺失信息和失败窗口。
- [ ] 比较上面的候选，以保留真实语义所需的最少职责为标准，形成正式设计；单独决定反馈合同和诊断关联，不先造泛化框架。
- 独立反馈纵向切片已实施并验收：两种入口 → 宿主附证 → 保存回执 → 查询/分叉；见[实施验收](../experiments/independent-feedback-acceptance.md)，不同时重写模型协议、其他日志与领域存档。
- [ ] 验证无模型 RequestId 的 Human 报告、真实模型报告、收到与未收到回执、日志部分失败、重开/重复 ID、同 tick 多转换、父子分支隔离；不靠重跑业务补日志。
- [ ] 验证现有操作结果对账、模型原始材料、只读导出及 fork 证据仍可追查，世界 Schema/行为不因诊断重构变化。明确旧实验材料的读取/归档政策，再决定是否转换；不覆盖旧证据文件。

home/控制绑定初版曾暂留 off/record/feedback；独立反馈切片正式替换此临时模式，历史实验命令不改写。日志去重仍保留 llm-runs 的独立模型材料，不预先选择通用日志框架或合并文件数。

本文调查快照不作为本次运行证据；独立反馈的工程与脚本 CLI 结果见[实施验收](../experiments/independent-feedback-acceptance.md)，本轮无在线模型调用。
