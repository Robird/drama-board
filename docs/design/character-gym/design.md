# 角色实验环境：设计思路与边界

状态：2026-10-31，一期已实现并完成验收；本文记录当前实现与边界，范围以[里程碑](milestone.md)为准。具体证据见[工程验收](../../experiments/character-gym-acceptance.md)与[真实试玩](../../experiments/character-gym-playtest.md)。

## 1. 当前实现入口

| 已核对入口 | 基础职责 | 一期实现 |
| --- | --- | --- |
| [LlmControllerTrace](../../../src/ResourceCycle.Completion/LlmControllerTrace.cs)、[LlmConsole / LlmRunAudit](../../../src/ResourceCycle.Cli/LlmConsole.cs) | 保存实际请求、响应/失败及已提交事实；每次宿主运行使用新审计文件 | [GymSession](../../../src/ResourceCycle.Cli/Gym/GymSession.cs) 关联 Run/Request/S；[GymTraceExporter](../../../src/ResourceCycle.Cli/Gym/GymTraceExporter.cs) 按角色导出 |
| [CycleOperationJournal](../../../src/ResourceCycle.Cli/CycleOperationJournal.cs) | 原始提交与结果按身份配对，可补录已有游戏结果；独立于游戏 E/S | 根/子分支使用各自操作日志；Gym 导入结果，明确 Interrupted、恢复补录和没有领域事件的查询不是游戏提交证明 |
| [LlmCharacterController](../../../src/ResourceCycle.Completion/LlmCharacterController.cs) | 每次从角色材料构造独立服务，最终返回一个 act | 可选 report-feedback，持久回执后携原始消息继续本次服务；协议重试、字符与调用预算共同约束 |
| [CycleDurableHistory](../../../src/ResourceCycle/Persistence/CycleDurableHistory.cs) | E/S 提交和恢复仍由游戏适配器负责 | [CycleBranchBoundary](../../../src/ResourceCycle/Persistence/CycleBranchBoundary.cs) 捕获/检查完成 S、列分支、显式打开与 fork；多分支时拒绝默认猜选 |
| 相邻 DG 的 `Repository.ForkBranch` / `ReadCheckpoint` | 从精确 commit 建立分支；分支注册不等于成功打开领域对象图 | 游戏适配器直接使用 DG 分支；[GymCommands](../../../src/ResourceCycle.Cli/Gym/GymCommands.cs) 连接反馈/请求与该边界 |
| [GameRevision](../../../src/ResourceCycle/Persistence/CycleGameRevision.cs)、[修订设计](../game-initialization-and-revision.md) | S/E 分别修订内存表示后进入正常流程；生产目前接受当前版本 | 声明续跑兼容范围、保留来源版本；不前置全历史迁移注册表 |

现有[目标管理试玩](../../experiments/goal-management-playtest.md)提供了原始运行证据组织经验，但临时脚本与本地 artifacts 不是可交付的 Gym 功能。

## 2. 三类信息各有权威

**游戏历史**负责已经发生什么、当前世界与角色记录是什么；仍由 E/S 和正常恢复判定。**运行证据**负责控制器实际收到/返回什么、调用是否中断、配置是什么。**需求反馈**保存控制器报告的障碍或建议，是线索，不是世界事实或已确认缺陷。

三者用稳定引用连接，不要求一起塞入角色状态图。反馈工具属于宿主实验设施，不自动写入角色 Goal、handoff、对白或其他角色上下文；只在本次局部交互返回必要收据。若角色需要记住自己的打算，仍使用既有游戏工具。

开发者视角可以连接更多世界诊断，导出时须显式标明；这些诊断不得反向注入角色材料。宿主审计不是新增的角色长期记忆。

## 3. 轨迹及关联形状

用语见[术语表](../../glossary.md)。运行记录（Run）表示一段宿主执行，不等于游戏分支、角色决策回合或模型调用。一次续局可以产生新 Run；一个分支会有多个 Run。

运行清单由 GymJournal 的 metadata/run-start 承载：StoreId、RunId、BranchId、起点 commit、GameRevision、程序集 SHA256、模型/连接及预算、呈现标识与原始审计位置。分叉来源单独记录；提示词、工具合同与 Shared/Tail 实际材料保存在每条 request 中。身份不依赖本进程对象句柄或随启动重置的 CallNumber。

每个请求应可连接角色 ID、Context 序号、调用身份、当时的完成 S 位置，以及后续工具调用/游戏操作身份。一个 S 可以对应多次请求，不能用 S 或模型时间当成唯一调用 ID。共享历史中的旧身份保留，新分支的新请求另有身份。

文本导出按角色和历史顺序组织：决策位置、实际输入、输出/工具、接受/拒绝/未完成、游戏已提交结果、可感知后续及反馈。概要链接完整材料；正文可分文件，但不能靠摘要静默丢掉原始请求。只保证启用记录以来实际保留的范围，旧运行缺口明确显示；不读取模型不可见的隐藏推理来填空。

分支轨迹由可追溯共享前缀与该分支后缀组成，不能把父分支在分叉点之后的动作、反馈或模型输出拼入子轨迹。当前 Context 只是当次保留材料，不能从它反推已经裁剪掉的完整历史。

## 4. 反馈交互

工具为 `report-feedback(kind, summary, obstacle, desired_support?)`，kind 区分 issue / feature-request。模型描述“想做什么、被什么阻碍”，改进建议可选。身份与现场引用由宿主填入，模型不负责抄写 commit。具体字段与额度由 [LlmFeedbackSubmission](../../../src/ResourceCycle.Completion/LlmFeedbackSubmission.cs)、控制器工具合同及 Options 校验。

反馈可选、可关闭、不强制每回合检查；有正文/调用预算，不能无限报告而永不行动。收到反馈后返回收据并继续同一次角色服务，只有最终游戏操作走原有权威入口。不消耗游戏通信次数、不放行世界；宿主 LLM 调用仍计入调用预算。原始 assistant 消息与工具回执组成临时 Tail；不引入完整持久 Agent 状态机。

反馈须先可靠记录再确认成功。同一 RequestId/ToolCallId 的相同内容可取得已有 FeedbackId，内容冲突明确拒绝；重新生成的新反馈可能重复，不承诺按语义恰一次。记录失败暂停本次服务，不返回成功收据。已记录而未送达时，feedback-receipt 只能证明宿主生成过回执；下一条 request 的 Tail 才证明回执进入后续请求。冷开不恢复临时 Tail。

反馈自动指向发出它的请求及该请求之前的支持 S。若它讨论更早经历，可以附文字或已有引用供开发者查找；默认定位只保证准确关联，不保证自动找到问题的最早因果起点。重跑入口可选择轨迹中的其他支持边界。

## 5. 从完成边界分叉

一期支持在角色即将决策、已有可恢复 Context 的完成 S 处重新开始决策。先确认该位置能稳定恢复同一待服务角色与合法操作窗口；不是任意墙钟时刻、任意日志行或半个事件。

明确区分原记录重放与重新运行：已发布 E 的效果由正常恢复处理，不重新询问控制器；若要改变那次决定，选择其之前的支持 S，并创建新分支。源分支可以保有更晚的 E/S，子分支只继承所选前缀。

分支注册、打开、运行是不同结果。打开失败时不能假定分支不存在，也不能自动再次注册。增加分支元数据可以修改仓库目录，但不得移动源分支头或改写其业务历史。首版串行管理即可，跨进程并行另验。

子分支保留源 S 的 `WorldVersion.LineageId`、转换序号与随机坐标，不插入一次重标提交。BranchId 区分分支，WorldVersion 不承担跨分支唯一身份。子分支独立操作日志避免补录父分支未来操作；分叉本身不改变世界随机条件。

原版实际输入永远来自原审计；新版可重新呈现继承的冻结信息。若新程序需要重新生成观察或转换 Context，必须作为显式修订/新材料来源处理，不得覆盖旧输入、泄露未知状态或声称材料完全相同。新版本可通过明确支持的升级续跑；差异太大时拒绝兼容。第一期不要求制作某个功能改进版本来证明兼容。

## 6. 实施尺度

先在当前应用与宿主接线，复用 Completion、操作身份、冻结 Context、DG 历史及修订边界；不提取通用 Gym 引擎。记录格式需有足够版本标识以便明确拒绝不支持的数据，但不承诺长期格式兼容。

一次实际串联验收证明“记录 → 反馈 → 定位 → fork → 续跑”可用即可。判断反馈是否合理、选择修改方案、比较改善程度和统计模型可靠性均留给二期。

## 7. 实现裁决与有限保证

- 保留源 S 的 KernelCursor、LineageId 与业务随机坐标；DG BranchId 隔离分支，外部定位不能仅用 WorldVersion。Context 打开已有独立完成 E/S，控制器请求前可直接捕获准确 S，无需改造 Kernel 提交阶段。
- 每次真实请求生成独立 RequestId，同一次角色服务中的反馈续问/协议重试仍可共用决定前 S。反馈由宿主关联该请求，不让模型填写位置。
- 首版使用存档内 `gym/events.jsonl` 的有序、校验链持久记录承载跨 Run/Branch 的证据；可靠刷新后返回反馈收据。游戏恢复仍由 E/S 决定，Gym 日志不授权重做操作。只读读取不修复尾部，写开才能处理未完整末行。
- 分支来源额外保存日志截断序号，默认截至源请求之前；该请求与源反馈作为来源引用，不把之后的父输出算成子分支轨迹。仅按请求前 S 筛日志不足以排除父未来。
- 原根分支保留现有 operations.jsonl；子分支使用独立操作日志，避免冷开补录误结算父分支待完成操作。Gym 记录统一可查，但角色导出严格按角色和分支前缀筛选。
- 反馈在控制器的一次服务内用原始 assistant 消息与工具结果续问，默认最多三条；只在最终 act 返回后走游戏提交。局部协议历史不跨冷开恢复，原始请求与反馈仍可回看；从其中某次请求关联的 S 重跑会启动新的角色服务。

工程及模型验收状态只在[实施入口](../../implementation/character-gym-plan.md)维护，使用命令见[操作说明](usage.md)。当前只接受游戏修订 15 和日志格式 1；没有新增游戏 Schema。原始材料保留在宿主证据中，不自动注入角色记忆，也不承诺跨进程恢复原生工具会话。
