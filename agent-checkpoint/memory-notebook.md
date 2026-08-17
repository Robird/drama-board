# 主线会话 Checkpoint(memory-notebook)

**用途:上下文压缩前的主动快照。压缩后的主线会话(我)在 Observe 阶段读此文件恢复完整工作状态。**
**本版:2026-08-17 第七轮压缩前。状态:第一阶段(WP0–WP11)完成 + 第二阶段门槛(S1–S14)全修;主 slnx 179 / Local.slnx 193 测试绿;第二阶段方向待用户定向。**

---

## 1. 我是谁、在做什么

- 我是 DramaBoard 项目的**主线会话**:保持高层理解与方向,按"自主循环协议"(002 顶部)每轮自主规划,把实现派发给 `coder` subagent(`runSubagent`,agentName=`coder`,GPT-5.6 Sol),验收后逐包 commit;评审类任务派 Opus(异构模型,报告质量极高)。
- 项目:DramaBoard(戏剧棋盘)——AI Player 参与的沙盒戏剧棋类。已建成:确定性 event-sourced Simulation Kernel + Protocol DTO + Host 决策编排 + FirstBoard 场景 + atelia 落盘 adapter。
- repo:`e:\repos\dream-board`(目录名旧,正式名 DramaBoard,代码 `DramaBoard.*`)。

## 2. 恢复上下文的阅读路径(按序)

1. `docs/研发计划_002_工作包分解.md` — **最重要**:自主循环协议(顶部)、状态表(WP 备注=设计裁决留档)、"下一轮建议"。
2. `docs/研发计划_001_架构基线与决策记录.md` — D1–D9(D1 纯函数零依赖、D2 事件链是 authority、D9 强制 event-sourcing + EventKind envelope)。
3. `docs/研发计划_003_Kernel攻击性评审_2026-08-16.md`(A1–A10,全修)与 `docs/研发计划_004_Host落盘层攻击性评审_2026-08-17.md`(S1–S14,全修;头部有修复 commit 对照)。
4. repo memory(`/memories/repo/project-facts.md`)+ `git log --oneline`;干活前跑 `dotnet test DramaBoard.Local.slnx` 确认基线(193 绿;主 slnx 179 绿)。
5. 设计愿景按需:003 设计文档(Kernel 语义之源)、002 设计文档(Host/Player/Protocol 边界、§3.1 信息不对称是 Core 规则、§9 版本语义)、001(游戏愿景)、基础设施.md(不变量清单、"架构攻击者"方法论出处)。

## 3. 系统全景(认知快照)

### 项目结构(双 solution)
- **DramaBoard.slnx(主,CI 用,零外部依赖)**:src/Kernel、src/Protocol、src/Host + tests/Kernel.Tests、Protocol.Tests、Host.Tests、FirstBoard.Tests。CI 已钉死到此 slnx。
- **DramaBoard.Local.slnx(全量)**:以上 + src/Journal.Atelia、tests/Journal.Atelia.Tests、tests/FirstBoard.Persistence.Tests(唯一碰 atelia 的三个项目,ProjectReference → `e:\repos\Atelia-org\atelia`)。

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

## 4. 当前状态与下一轮队列

- **门槛已清**:A1–A10(评审一)+ S1–S14(评审二)全修。关键 commit:δ=cd75fcc、α=992c5a2、β=a3f78a2、γ=392a63d。
- **元教训**(评审二核心发现):Kernel 层修对的东西(A2/A4/A7)没有"继承机制",Host/装配层会重蹈覆辙(S4 重现 A2、S3 架空 A4、S11 架空 A7)——**修复时优先把约定变机制**(如 EventKind 相等语义)。
- 残留风险(γ 报告,已留档 002):checkpoint 与 journal head 无统一事务;orphan 帧无 GC;LineageId 全局唯一性靠调用方;FirstBoard 手写 codec 新增事件需同步维护。
- **下一轮队列**:
  1. **等用户定向第二阶段**(愿景级,不擅自开工):AI Player driver(LLM,我的倾向;将首破零 NuGet——隔离独立项目)/ 更丰富 Board / Godot(基础设施.md 建议现阶段不做)。
  2. 未定向时的穿插件(不越愿景边界):a) journal 可读性工具(dump 一局为可读文本,调试 AI 轨迹价值高);b) 残留风险小包;c) 第三次 adversarial review(门槛修复回归审计)。

## 5. 协作模式要点(实践验证)

- 派发 prompt 结构:环境+基线测试数+按序阅读清单+**主线已裁决的设计(执行依据)**+任务清单+验收标准+报告要求。**subagent 无对话记忆,必须自包含;把裁决写死,把探索空间标明。**
- coder 可信度高(多轮零偏差),但主线必独立跑测试+抽查关键形状;coder.md 约定:不改状态表、不 commit。评审任务派 Opus 且**报告必须当轮持久化进 docs/**(否则随上下文丢失)。
- 用户偏好:中文;精力有限,**异步纠偏**(方向级列选项+我的倾向,先按倾向推进不阻塞);授权本地 commit;**不 push、不改 atelia、愿景级取舍留给用户**;用户会顺手做换行规范化编辑(insertions==deletions 的大 diff 即是,收成独立 commit 即可)。
- 终端坑:run_in_terminal 可能剥 cd 前缀沿用旧 cwd;长命令输出偶发折行错乱,关键验收用简单命令。
- 记忆闭环:002 状态表+"下一轮建议"+本文件;每轮结束更新,压缩前刷新本文件。

## 6. atelia 备忘(已接入)

- 真 repo:`e:\repos\Atelia-org\atelia`(**`e:\repos\atelia` 是无关旧 Python 项目**)。net10.0,零 NuGet。**只读纪律:绝不修改。**
- EventJournal:AppendEventFrame(parent, payload, utc)/CommitToRef/AdvanceRef(CAS)/ReadChronologicalChain/ForkBranch;AteliaResult<T> 需显式检查;IDisposable;目录落盘无内存实现。坑:CommitToRef 不支持指定帧时间戳(所以 adapter 用 AppendEventFrame(utc=0)+AdvanceRef);ref/reflog 写 wall-clock(events/ 目录字节确定,refs/ 不保证)。
- StateJournal/SessionJournal:不碰(前者永不进 Kernel 领域模型,后者不稳定)。
