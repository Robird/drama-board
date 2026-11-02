# 独立宿主反馈

状态：2026-11-02，已实施。独立反馈原切片的 Windows 工程、Completion fixture 与脚本 CLI 证据见[实施验收](../experiments/independent-feedback-acceptance.md)，其中没有在线 LLM、真人自由试玩或 Linux 验证。本文已同步后续[统一宿主日志](character-history-and-diagnostics.md)：反馈仍独立保存、查询和分叉；主日志只关联成功 ID，不接管反馈保存或把反馈正文当作世界事实。

## 入口与职责

反馈默认可用，独立于 Gym 轨迹录制。Human 输入 `:feedback <text>`，LLM 调用 `feedback({"text":"…"})`，两者进入同一个宿主 `FeedbackSession`。正文只要求非空、非纯空白，并且不超过 Playground 统一常量 `FeedbackRecord.MaxTextCharacters` 的 4000 个 UTF-16 code unit，模型和 Human 使用同一正文上限；不要求分类、摘要、障碍或期望支持字段。身份、时间、当前角色材料序号与世界边界由宿主捕获，提交者无需抄写内部 ID。

Console 在 `operations.Receive` 前识别并处理反馈，包括错误格式、空正文、超限与保存失败。它们均不成为操作输入，不产生 operation-received/completed、不进入领域 parser/Fact/Context，不推进时间、不确认警告、不轮转角色服务。LLM 的反馈工具也不成为游戏行动；用于提出反馈、继续决定的实际模型调用仍计入共享调用预算，成功反馈继续受每次决定的既有额度约束。工具收据与原始 assistant 消息只保留在本次局部 Tail；下一次决定和冷开不从反馈日志恢复 Tail。

持久游戏懒建 `store/feedback.jsonl`；只有实际提交时才创建文件。`--ephemeral` 的世界不落盘，但反馈可懒建于 `home/feedback.jsonl`，此时 `Boundary=null`，不能分叉。程序化入口既无 store 也无 home 时禁用真实保存，不报告虚构成功。`--gym off` 不关闭反馈；持久局的必要宿主/操作事实仍写 host/events.jsonl，`record` 另外捕获详细人物材料和 feedback-saved 关联。

## 最小记录与定位含义

每行是独立反馈记录，带格式 `Version`，核心字段如下。

| 字段 | 含义 |
| --- | --- |
| `FeedbackId` / `CreatedAt` | 本份记录的唯一 ID 与宿主记录时间 |
| `RunId` / `ActorId` / `Source` | 本次宿主运行、实际服务角色、HumanConsole 或 Llm 来源 |
| `ContextSequence` / `Text` | 提交时的角色材料序号与原始反馈正文 |
| `Boundary` | 可空的完整 `PlaygroundDecisionBoundary`；持久运行捕获已完成的决定前 S |
| `Model` | 仅 LLM 的实际 `RequestId`、`CallNumber`、`ToolCallId`；Human 为 null |
| `StorePath` | 可选的位置提示，不是仓库全球唯一身份或可移植保证 |

不新增全球 StoreId。统一主日志 metadata 的 JournalId 只标识其日志，不充当 DurableGraph repository 身份。Boundary 保留分支、S commit、角色、Context/操作序号、lineage、转换数、时间与游戏修订等完整检查信息；不能只凭裸 commit 或当前 head 定位。

边界代表**提出反馈时**的现场，不保证就是正文所述问题发生时、最初原因处或最后失败动作的提交点。文本讨论过去经历时，开发者仍需核对历史材料。定位能够支持检查和另开实验分支，不承诺重跑远端模型得到相同输出，也不自动向角色展示其他角色的私有材料。

## 保存、失败与回执

串行写入完整 JSONL 行并执行 `Flush(flushToDisk: true)` 后，才返回 FeedbackId 并生成成功回执。只读查询不创建、截断或修复文件；末尾不完整行不作为已保存记录，结构、字段或身份校验不通过的完整行明确报错；此轻量 JSONL 不含 hash chain，不承诺检测所有合法 JSON 内的位变更。读取报告忽略尾部的字节数。新写入实例可以在追加前处理未完成尾部；不将读取自动升级成修复。

写入或刷盘失败即退役该写入实例，不自动重试、不继续使用失败实例，不声称“确定未保存”。已写入而未收到收据可能产生重复报告；FeedbackId 不是业务事务或 exactly-once 保证。LLM 保存失败暂停当前服务，Console 明确显示不确定结果并允许用户自主决定后续操作。

保存与回执交付是两个事实。日志中有记录或宿主生成了回执，不能证明模型/用户实际收到；后续 model-request 的 Tail 携带匹配工具结果，证明回执进入了该次构造并保存的输入，不证明远端收到或处理。不能以重新执行游戏动作补日志。

FeedbackSession 完成独立保存后才通知主日志。LLM 通过保存 try/catch 之后的 Feedback trace 关联，Human 先显示成功 ID，再在保存 catch 外通知。主日志关联失败不能改报 feedback-save-failed 或自动重交；成功 ID 仍可通过独立管理查询，宿主可因诊断写入失败暂停。主日志记录与反馈文件不宣称原子提交。

## 管理、分叉与轨迹缺口

`--feedbacks` 直接查询独立反馈，支持角色与分支筛选，不要求主日志存在或健康、模型配置或运行游戏；无 Boundary 的记录不匹配具体分支。`--fork-feedback ID` 从独立记录读取完整 Boundary，先 `Inspect` 核对真实世界，再 `Fork`；不读取或写入主日志，不受旧宿主格式的运行预检阻塞。空 Boundary 拒绝分叉，不能用当前 head 代替。旧 `--gym-feedbacks`、`--gym-fork` 移除，不保留别名；`--gym off|record` 不含 feedback 模式。

管理仍显式选择 home/store，直接 store 优先，不加载模型客户端或恢复 pending。查询 ephemeral 反馈必须显式使用 `--home PATH --ephemeral --feedbacks`，定位 home/feedback.jsonl，不能附带 `--branch`，也不可分叉；省略 `--ephemeral` 则按 home 解析 store 并查询持久报告。`--feedbacks` stdout 只输出 JSONL；完整行校验失败则拒绝，不完整尾通过 ReadSnapshot 忽略并在 stderr 警告字节数，原文件不变。分叉注册、打开和运行是不同结果，不因注册或记录结果不确定自动重试。

独立反馈分叉没有精确主日志序列截点，因此不继承祖先宿主记录，不能按时间、tick 或最新请求猜切点。后续子分支导出必须说明祖先 history 缺口，不混入父分支未来。世界历史仍由真实 S 分叉继承，此限制只涉及宿主证据。`--gym-fork-request ID` 从唯一 model-request 的完整 Boundary 分叉，继承截至其 Sequence−1 的精确祖先前缀。

角色 history 只查询已选前缀中的 feedback-saved ID；不按相同 S/Context 把父分支未来反馈拼入子分支。没有主日志关联的报告仍可用 `--feedbacks` 独立查询。实际模型请求里的反馈正文与收据保留原样，不因反馈文件独立而删改调用证据。

## 实施验收边界

本切片需要验证 Gym off 的 Human/LLM 共用保存、4000 字符边界、Console 在操作记录前分流、同一决定继续行动、实际模型预算、懒创建、ephemeral 空 Boundary、无目录禁用、写入/刷盘失败退役与未知结果、只读查询源字节不变、完整 Boundary 的 Inspect/Fork、独立分叉的导出缺口和请求分叉精确前缀。领域 Schema、GameRevision、操作恢复与警告语义不能因反馈改变。

工程 fixture、代理操作 Console、实际真人体验与真实模型调用分别报告。原反馈切片验证及未覆盖范围见[实施验收](../experiments/independent-feedback-acceptance.md)；主日志、模型阅读投影与分叉前缀的当前合同及验收矩阵见[统一宿主日志](character-history-and-diagnostics.md)。旧 Gym 验收保持原历史语境；不引入通用事件/日志框架。
