# DramaBoard 工作交接文档(memory-notebook)

**用途:跨会话/跨 agent 的工作交接快照。任何接手本项目的 coding agent 会话(无论 Copilot、codex 还是其他),在开工前读此文件恢复完整工作状态。本文档不假设你拥有之前任何会话的记忆。**

**本版:2026-08-17,codex 完成 WP14。状态:第一阶段(WP0–WP11)+ 两轮攻击性评审修复(A1–A10、S1–S14)+ 第二阶段 WP12(LLM Player 设计研究)、WP13(src/Player.Llm 骨架)、WP14(真 LLM 后端双 adapter)完成;主 slnx 198 / Local.slnx 212 测试绿;下一步 WP15(demo harness 真跑一局并产出戏剧记录)。**

---

## 1. 项目与接手者角色

- 项目:DramaBoard(戏剧棋盘)——AI Player 参与的沙盒戏剧棋类。已建成:确定性 event-sourced Simulation Kernel + Protocol DTO + Host 决策编排 + FirstBoard 场景 + atelia 落盘 adapter + LLM Player 认知循环骨架。当前目标:把真 LLM 接进来,真跑一局看戏。
- repo:`e:\repos\dream-board`(目录名旧,正式名 DramaBoard,代码 `DramaBoard.*`)。
- 接手者工作模式:按"自主循环协议"(002 顶部)每轮自主规划→实现→验收→commit→更新状态表与"下一轮建议"→汇报。之前的主线会话把实现派发给 subagent;如果你是单会话 agent(如 codex),直接自己实现+自我验收即可,协议其余部分不变。方向级决策点:列选项+你的倾向,先按倾向推进不阻塞,用户异步纠偏。
- **基调(用户明示,持续有效)**:第二阶段是探索性快速原型,**探索可能性优于严谨性与可审计性**——测试覆盖关键路径即可,不追求 property/快照级锁定,不预留抽象。但第一阶段已建成的确定性/event-sourcing 不变量不得破坏(它们是已交付资产,不是未来约束)。

## 2. 恢复上下文的阅读路径(按序)

以本节顺序为准（002 顶部的"前提文档"清单是历史派发用语）。协议中提到的"repo memory"是前任 Copilot 会话的私有设施，内容已并入本文档，无此设施的 agent 跳过即可。

1. `docs/研发计划_002_工作包分解.md` — **最重要**:自主循环协议(顶部)、状态表(WP 备注=设计裁决留档)、"下一轮建议"。
2. `docs/研发计划_005_LLM_Player设计研究.md` — **当前阶段执行依据**:三大设计问题(Observation/Action/内部状态)的裁决与依据、后端接入与成本策略、WP13–WP16 路线。
3. `docs/研发计划_001_架构基线与决策记录.md` — D1–D9(D1 纯函数零依赖、D2 事件链是 authority、D9 强制 event-sourcing + EventKind envelope)。
4. `docs/研发计划_003_Kernel攻击性评审_2026-08-16.md`(A1–A10,全修)与 `docs/研发计划_004_Host落盘层攻击性评审_2026-08-17.md`(S1–S14,全修;头部有修复 commit 对照)——两轮评审的发现模式对后续开发有预警价值。
5. `git log --oneline -20`;干活前跑 `dotnet test DramaBoard.slnx` 确认基线(198 绿;Local.slnx 212 绿,含 atelia 落盘集成)。
6. 设计愿景按需:`docs/开放世界棋盘游戏设计_003_Forecast_Elapse_Decide_SimulationKernel.md`(Kernel 语义之源)、`开放世界棋盘游戏设计_002_整体软件架构与技术栈.md`(Host/Player/Protocol 边界、§3.1 信息不对称是 Core 规则、§9 版本语义)、`Design Note 001`(游戏愿景)、`基础设施.md`(不变量清单、"架构攻击者"方法论出处)。**注意：愿景文档中的早期技术选型（MonoGame、StateJournal 脊柱、μ0–μ4 相位、基础设施.md 的项目切分建议）已被 ADR（D1–D9）与 002 状态表取代，冲突时以后者为准。**

## 3. 系统全景(认知快照)

### 项目结构(双 solution)
- **DramaBoard.slnx(主,CI 用,零外部依赖)**:src/Kernel、src/Protocol、src/Host、src/Player.Llm + tests/Kernel.Tests、Protocol.Tests、Host.Tests、Player.Llm.Tests、FirstBoard.Tests。CI 已钉死到此 slnx。
- **DramaBoard.Local.slnx(全量)**:以上 + src/Journal.Atelia、tests/Journal.Atelia.Tests、tests/FirstBoard.Persistence.Tests(唯一碰 atelia 的三个项目,ProjectReference → `e:\repos\Atelia-org\atelia`)。
- src 下项目**零 NuGet**(BCL only);测试项目可用 xUnit v2 2.9.3 + CsCheck 4.8.0。新建项目必须同时登记进两个 slnx。

### Kernel(src/Kernel,零 NuGet)
- **event-sourced 强制**(D9):`ISimSystem.Resolve` 只产事件;world 由 Loop 将已提交事件交全局 `IEventReducer` 折叠;replay=纯 fold(initialWorld+journal 充分)。
- `SimulationLoop.Run(world, SimulationCursor, until, journal, externalInputs?)` → `{World, Cursor, StopReason(Exhausted/BoundaryReached/DecisionRequired), Version, DecisionEvents}`。决策停机谓词构造注入(控制流不入 journal);external 批次也过谓词(S10);同刻 resolve 预算+no-op 守卫在 cursor 跨 Run 存续;切分等价被 CsCheck 钉死。
- `SimulationCursor`:LineageId/Now/预算/守卫/NextBatchOrdinal;**ToSnapshot/FromSnapshot=持久化契约**(γ)。
- `Journal/`:DomainEvent(Timestamp, **Cause**, Kind, Payload);EventCause=(ResolveBatch|ExternalInput, SourceId, CandidateId, Due, BatchOrdinal)由 Loop 分配(A5);**EventKind(Id,Version) 相等只比 Id**(S11,路由跨版本稳定);EventKindRegistry;InMemoryJournal 严格递增;**IJournalSink.AppendBatch**(默认逐个,Loop 调批量,批次原子性信息源)。
- `Random/`:DeterministicRandom 坐标寻址纯函数 + DeriveStreamId(long/string)(streamId 必须由持久身份派生);采样坐标入事件 payload(journal 自含可审计);RNG 常量=replay 兼容契约。
- `Time/`:ModelTime(1 tick=1ms)/ModelDuration/Microstep/LogicalTimestamp,checked。`Scheduling/`:tie-break (Due,SourceId,CandidateId) **只决定执行顺序不决定游戏结果**;Generation/InvalidateSource 已删。
- 同刻语义:顺序 Resolve+tie-break+因果穿透;争抢用**顺序无关仲裁**(胜者由 RNG 坐标决定,交换 Resolve 顺序胜者不变——夹宝模型示范,FirstBoard take 冲突复用)。
- SourceId 一律由世界持久实体 id 派生;动态实体生死=世界状态变化(Kernel 零改动支持)。

### Protocol(src/Protocol,零依赖,不引用 Kernel)
- DTO:DecisionRequest(含 LineageId/Microstep/Reason/**RejectedIntent 回带**/AvailableActions)、PlayerDecision、Intent(扁平,六动作表达力锁定,**数值域校验** S9)、ExpectedOutcome、Observation(含 Microstep)、KnownFact、AvailableAction。时间/版本用原始 long(DTO=wire 形状)。JSON round-trip 锁定。

### Host(src/Host,引用 Kernel+Protocol,零领域内容)
- IPlayerDriver:Null/Scripted/**Random(坐标寻址,同 request 幂等,S12)**。
- **PlayerDecisionSession=同刻决策屏障模式**(β,推翻早期"逐个提交"):停机批次=同刻决策集合,同一快照构造全部请求(彼此不知情)→ 全部问 driver → 整批 externalInputs 一次提交;决策三态(Open/Answered/Invalidated)会话字段,**取消/异常可恢复**(未提交批次不污染状态,重入续问 Open);验证失败重问一次;requestBuilder 可返回 null(Invalidated:StaleRequest);**拒绝预算**超阈强制降级 wait(rejectionSelector 注入,S2);重入防护(S14);Host 级切分等价成立(S5)。

### FirstBoard(tests/FirstBoard.Tests,纯装配方,三层零改动)
- tavern/market/cellar、Alice/Bob、brass-key、secret(持钥匙在地窖 Observe 才发现)、60min deadline(地窖封)。六动作。Intent 校验在 system(非法尝试→action.rejected 入 journal)。
- **信念层**(α):affordance 只由 actor.KnownFacts 计算(封闭须先知情才影响选项);拒绝写入 KnownFact+LastRejectedIntent;placed/carried 可见性(携带物仅持有者可见)。

### Journal.Atelia(src/Journal.Atelia,唯一碰 atelia)
- AteliaJournalSink:IJournalSink+IDisposable;写路径 AppendEventFrame(utc=0 确定性)+批级 AdvanceRef(**一批一帧**,半批崩溃=不可见 orphan);内存镜像;OpenAndReplay(残批防御性截断);ForkBranch 只接受批次边界;**lineage 元帧**(LineageId/parent/fork prefix 落盘校验)。
- **envelope v2**(JSON,固定 key 序,双目录字节逐位相等已验证):v/t{ms,us}/c{k,s,cid,due,b}/kind{id,ver}/**pc**(codec 标识)/**bi/bc**(批内序号/批大小)/p(base64)。v1 拒读。
- 反序列化委托收 EventKind(upcaster 挂载点);CursorSnapshotEnvelopeCodec(读档续跑);FirstBoard 真落盘+读档续跑端到端已验收(多态 payload 用 kind→type 显式映射,评审警告的抽象类静默 {} 已规避)。

### Player.Llm(src/Player.Llm,WP13+WP14,零 NuGet,引用 Protocol+Host)
- **ILlmChatBackend 最小端口**:`CompleteAsync(LlmChatRequest(System,User), ct) → string`。
- **PromptRenderer(纯函数,中文)**:system=[角色卡][世界规则+四分节输出格式约定];user=[内心状态(记忆文档)][当前观察][新近变化(KnownFacts diff + RejectedIntent 反馈)][决策请求(AvailableActions 渲染)]。
- **四分节输出解析器(宽松)**:【独白】【行动】(单 JSON 对象,字段 action/targetActor/targetObject/destination/freeText/durationMs/untilModelTimeMs,大小写不敏感,容忍 ```json 围栏与杂文)【台词】(非空覆盖 freeText)【记忆】(整体替换)。缺【行动】或 JSON 非法=解析失败(返回失败对象不抛异常)。
- **LlmPlayerDriver : IPlayerDriver(per-actor 实例,构造收 CharacterCard/初始记忆/backend)**:认知循环=渲染→调后端→解析;失败带纠正提示重问一次→再失败 wait 保底;**失败不污染记忆/KnownFacts 快照**;记忆内存态,`CurrentMemory` 可读(落盘留给 WP15 harness)。driver 不预验证 affordance(语义非法交给 Board 拒绝闭环,权威唯一)。
- FirstBoard 集成测试(tests/FirstBoard.Tests/LlmPlayerIntegrationTests.cs):假后端脚本化完整一局——拒绝闭环/台词入事件/记忆演进均验证。
- **CodexAppServerBackend(主力)**:`CodexAppServerOptions(CommandPath/Model/WorkingDirectory/ReasoningEffort/RequestTimeout)`;无 BOM UTF-8 JSONL;进程复用、调用顺序门控,EOF/畸形/超时/server error 后回收且下次重启;v2 initialize/initialized;每决策 `thread/start(ephemeral=true)`→`turn/start(approval=never,readOnly,no-network)`→取 final agentMessage/turn.completed→`thread/unsubscribe`;所有 approval 反向请求拒绝。真实协议有一条关键竞态:**短 turn 的 item/turn completed 可早于 turn/start response**,实现会提前缓存,测试已锁。
- **OpenAiCompatBackend(对照)**:构造收 HttpClient/baseUrl/apiKey?/model;POST `{baseUrl}/chat/completions`,system+user 两条 message;只取 `choices[0].message.content`;非 2xx/畸形 response 抛异常;key 不入 repo/错误文本。真 smoke 走 OpenRouter compatible 成功;官方 OpenAI key 本轮返回 429(额度外部状态)。

## 4. 当前状态与后续计划(交接核心)

- **已完成**:WP0–WP14 全部;A1–A10 + S1–S14 两轮评审修复全清。关键 commit:δ=cd75fcc、α=992c5a2、β=a3f78a2、γ=392a63d、WP12 设计=b16ca21、WP13 实现=c5307a2。
- **元教训**(评审二核心发现,后续开发警惕):Kernel 层修对的东西没有"继承机制",Host/装配层会重蹈覆辙(S4 重现 A2、S3 架空 A4、S11 架空 A7)——**修复时优先把约定变机制**(如 EventKind 相等语义)。
- 残留风险(已留档 002,不阻塞主线):checkpoint 与 journal head 无统一事务;orphan 帧无 GC;LineageId 全局唯一性靠调用方;FirstBoard 手写 codec 新增事件需同步。

### 下一步:WP15 demo harness 真跑一局

目标:FirstBoard + 两个 LlmPlayerDriver 跑第一场真戏,输出 journal 叙事 dump + 各角色独白/记忆轨迹;记忆文档按决策序号落盘。运行时可选 Codex 主力或 OpenAI-compatible 对照,凭据只从参数/环境读取且不写日志。

前置方向选项(未否决按倾向推进):
1. **FirstBoard 的运行时代码归属**:A=demo 直接引用 `tests/FirstBoard.Tests`(快,但引入 xUnit/test assembly);B=把 domain/systems/scenario 提升为 BCL-only `src/FirstBoard`,现有测试改引用它。**倾向 B**——FirstBoard 已从架构探针成熟为首个可运行 Board,应解除测试程序集依赖;Kernel/Host/Protocol 仍零改。
2. **独白轨迹采集**:A=backend decorator 重解析 raw response(形成第二解析权威);B=LlmPlayerDriver 增加最小 turn-trace 回调,只在解析成功/状态提交时发出 `(decision, monologue, dialogue, memory)` 给 harness。**倾向 B**——与 driver 的记忆提交点一致,无双轨。
3. 新增 BCL-only console(倾向 `src/FirstBoard.Demo`),登记两个 slnx;支持短局参数/超时/模型选择;逐决策原子写记忆快照与 append 戏剧记录。先求真实完整一局与人工可读,不在 WP15 做 UI/复杂配置框架。

之后 WP16:根据首场观感做 prompt 工程、记忆压缩、解析/拒绝/降级率统计。005 §6 两个开放问题仍按既定倾向:独白展示给观众;工作语言中文。

## 5. 工作方式约定与红线(实践验证)

- **红线(绝对)**:不 `git push`;不修改 `e:\repos\Atelia-org\atelia`(只读);src 项目不加 NuGet;愿景级取舍(游戏性/观赏性的方向选择)留给用户;API key 类凭据不入 repo/日志。
- **授权**:本地 `git commit` 自主进行(逐工作包提交,message 英文,风格见 `git log`)。
- **每轮闭环**:开工前跑测试确认基线→实现→自我验收(跑两个 slnx)→commit→更新 002 状态表(WP 备注写入设计裁决,这是项目的决策台账)与"下一轮建议"→按需刷新本文件→向用户汇报(中文,含产出/设计发现/下轮打算/方向级问题)。
- **用户偏好**:中文交流;精力有限,**异步纠偏**模式(方向级列选项+倾向,先按倾向推进不阻塞);用户会顺手做换行规范化编辑(insertions==deletions 的对称大 diff 即是,收成独立 style commit,不是内容变更)。
- **评审习惯**:重大节点后做"攻击性评审"(用异构强模型扮架构攻击者找设计缺陷),报告必须当轮持久化进 docs/(否则随上下文丢失)。
- 细节:测试验收用简单命令(长管道偶发折行乱序);Windows 环境,.editorconfig 要求 LF 但工具链偶产 CRLF,提交前留意。

## 6. atelia 备忘(已接入)

- 真 repo:`e:\repos\Atelia-org\atelia`(**`e:\repos\atelia` 是无关旧 Python 项目**)。net10.0,零 NuGet。**只读纪律:绝不修改。**
- EventJournal:AppendEventFrame(parent, payload, utc)/CommitToRef/AdvanceRef(CAS)/ReadChronologicalChain/ForkBranch;AteliaResult<T> 需显式检查;IDisposable;目录落盘无内存实现。坑:CommitToRef 不支持指定帧时间戳(所以 adapter 用 AppendEventFrame(utc=0)+AdvanceRef);ref/reflog 写 wall-clock(events/ 目录字节确定,refs/ 不保证)。
- StateJournal/SessionJournal:不碰(前者永不进 Kernel 领域模型,后者不稳定)。
