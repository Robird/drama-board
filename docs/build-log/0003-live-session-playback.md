# Build Log 0003：权威模拟领先于表现的 Live Session 竖切

> 状态：**Implemented and verified**
> 记录日期：2026-08-23
> 设计基线：`81ae496 docs(spatial): close passage encounter slice`
> 产品方向：面向可在 Steam / TapTap 交付的单人单机游戏；第一版 Human UI 采用 Console，图形前端延期。
> 核心裁决：两个 async loops、两个 committed-prefix frontiers、一个世界权威；Presentation 内完成 Human 视角过滤，并可切换 Player / Developer 模式。

## 1. 目的与上下文

本批要建立 DramaBoard 从“可离线跑完并事后 dump 的模拟原型”走向“Human 可以边看世界历史边参与决策的单机游戏”的第一个运行时骨架。

LLM Player 的一次决策可能消耗数秒到数分钟 wall-clock time。若 Human 所见的播放节奏与权威模拟严格同步，每当 AI Player 思考时，画面都会直接停住。目标方案是让权威模拟与表现播放解耦：

- 权威模拟不被动画和日志播放速度限速；
- Presentation 只播放已经确定、已经原子提交的世界历史；
- 当 Authority 暂停等待 AI 时，Presentation 可以继续消费此前积累的 committed backlog；
- 当 Authority 抵达 Human decision 时，必须等 Presentation 播完此前历史，才向 Human 展示该 DecisionRequest；
- backlog 耗尽而 AI 仍未返回时，明确进入 buffering / thinking 表现，绝不伪造未来；
- 第一版以受控速率输出 Human 可见的文字 cue，未来同一骨架可接 Godot 或其它图形前端。

这个能力可概括为：

> **Authoritative Simulation Ahead of Presentation —— 权威模拟领先于表现。**

本文用于下一批具体施工，也作为上下文压缩后的独立交接件。新会话应先阅读本文，再按第 2 节链接核对实际代码是否已经演进。

## 2. 权威上下文与当前代码路径

### 2.1 上位设计

- [Design Note 002：整体软件架构与技术栈](../开放世界棋盘游戏设计_002_整体软件架构与技术栈.md) 已冻结 Authoritative Host、Player Ports 与可替换 Frontend 的基本分层，并明确 HumanPlayerDriver 与 Presentation Adapter 是正交维度。
- [Simulation Kernel](../design/simulation-kernel.md) 已明确区分 Model Time、Causal Ordinal、Wall-Clock Time 与 Presentation Time；等待 Player 消耗零 ModelTime，Presentation 不得改变 winner。
- [Kernel occurrence baseline](../implementation/kernel-occurrence-baseline.md) 是当前 Kernel 实现与验收边界。
- [Build Log 0001](0001-travel-to.md) 与 [Build Log 0002](0002-passage-encounter.md) 已建立自动导航、途中 encounter、完整 batch、Replay/Fork 等可供实时播放的真实历史内容。

### 2.2 当前实现证据

- [`SimulationKernel.StepAsync`](../../src/Kernel/Simulation/SimulationKernel.cs) 已保证一个 lineage 同时至多一个 in-flight Step。它从 frozen committed world Forecast winner，在 `PlanSelectedAsync` 中等待 Player，scratch-fold 完整 draft，发布一个 `JournalBatch` 后才安装新 World / WorldVersion。
- [`DecisionPointRule`](../../src/FirstBoard/FirstBoardSystems.cs) 与 passage encounter response rule 当前直接在 selected Plan 中 `await driver.DecideAsync(...)`。所以 AI/Human 等待已经自然冻结 Authority，但不会占用 ModelTime。
- [`IJournalSink`](../../src/Kernel/Journal/IJournalSink.cs) 明确按 serial drive 使用；[`InMemoryJournal`](../../src/Kernel/Journal/InMemoryJournal.cs) 和[当时的 AteliaJournalSink](https://github.com/Robird/drama-board/blob/3b77450fee639a362aa1d0630cc872bcfb02dd14/src/Journal.Atelia/AteliaJournalSink.cs) 暴露的 `Batches` 都不是 live concurrent collection。Presentation 不得从另一个 task 并发枚举它们。
- [`SimulationHost.RunUntilAsync`](../../src/Host/SimulationHost.cs) 目前只支持循环 Step 直到 `Exhausted` / `BoundaryReached` 后整体返回，没有 live committed-history notification、Presentation cursor 或 Human reveal barrier。
- [`FirstBoardScenario.CreateKernel`](../../src/FirstBoard/FirstBoardScenario.cs) 已公开完整 composition seam；实时 App 可以直接创建 Kernel 并自行串行 Step，无需修改 Kernel。
- [`FirstBoardScenario.RunAsync`](../../src/FirstBoard/FirstBoardScenario.cs) 当前创建 `InMemoryJournal` 后调用 `SimulationHost.RunUntilAsync`，适合 headless / batch run，但不能在每次 commit 后驱动实时播放。
- [`FirstBoard.Demo/Program.cs`](../../src/FirstBoard.Demo/Program.cs) 已拥有两名 Player 的 LLM backend、MemoryBank、profiler、run manifest 和最终 drama record composition；[`DramaRecordWriter`](../../src/FirstBoard.Demo/DramaRecordWriter.cs) 只在整局结束后全知地遍历 Journal，不是 Human-safe live renderer。
- [`DecisionRequest`](../../src/Protocol/DecisionRequest.cs) 当前包含 `DecisionId`、Actor、ModelTime、Observation 与 affordances，但没有 Design Note 002 曾建议的 `BasedOnWorldVersion`。本批的进程内 Human barrier 可以由 app-local interaction envelope 捕获 Authority head，不需要为此修改 Protocol；跨进程 UI、热切换 lineage 或 speculative decision 出现时再重新裁决。

继续实施前先运行基线测试并重新核对以上文件。若当前代码已改变这些事实，不要机械套用本文伪代码。

## 3. 冻结的概念模型

### 3.1 一个权威、两个循环

运行时只有一个 Objective World authority：

```text
AuthorityLoop
    │ serial StepAsync
    ▼
SimulationKernel ── atomic AppendBatch ──> Journal
    │                                         │
    │ after StepStatus.Committed              │ source of truth
    └──── immutable committed envelope ───────┘
                          │
                          ▼
                  PresentationLoop
                          │ Human-safe cues
                          ▼
                   Console / future UI
```

两个 loop 是两个异步控制流，不是领域中的两个线程，也不要求两个专用 OS Thread：

- **AuthorityLoop** 是唯一调用 `StepAsync` 的 writer。它不按动画帧率限速；selected occurrence 需要 Player 时，正常等待相应 `IPlayerDriver`。
- **PresentationLoop** 是 committed history 的单消费者。它维护可丢弃、可重建的 replay/projection state，控制日志、插值、动画和镜头节奏，但不能 Forecast、Plan、Step、写 Journal 或改变 Objective World。

Console MVP 可以用两个 `Task`。未来 Godot 通常让 Presentation 在 UI/main thread 的 `_Process(delta)` 中运行，让 Authority pump 在后台 task 上串行执行。线程拓扑只是 adapter 细节，不写入 Kernel Law。

### 3.2 两个 committed-prefix frontiers

定义：

```text
C = CommittedFrontier
  = Authority 最新完整提交并已安装的 WorldVersion

P = PresentationFrontier
  = Presentation 已经完整播放并确认的 WorldVersion
```

必须始终满足：

```text
C.LineageId == P.LineageId
0 <= P.TransitionCount <= C.TransitionCount
```

`WorldVersion(LineageId, TransitionCount)` 已唯一标识 lineage 内的完整 batch prefix；本批不新增 `HistoryCursor`、`CommitOrdinal` 或第二个 durable version。`JournalBatch.Instant` 已提供播放所需的 ModelTime 和 CausalOrdinal。

`P` 只有在一个 batch 对应的全部 visible cue 播放完成、该 batch 的 projection fold 完成后才前进。收到 channel item 不等于已经呈现，不能提前 ack。

若一个 batch 对 Human 不可见，Presentation 可以不产生 cue，但仍必须按顺序 fold 并推进 `P`。否则隐藏事实会永久阻塞 Human reveal barrier。

“occurrence 不可见”不等于两个 batch 之间的时间区间不可见。远方隐藏事件把 committed head 推到更晚 ModelTime 时，Presentation 仍可表现 Human 周围在该已确定区间内的时钟、移动和环境 Lazy Flow，只过滤该远方 occurrence 自身的内容。

### 3.3 Latency Buffer

```text
Committed Latency Buffer = (P, C] 中尚未呈现的完整 committed batches
```

它是已确定历史，不是预测缓存。它可以掩盖 Authority 等待 AI 的 wall-clock 延迟，但不能保证永远有内容。

产品上真正有意义的缓冲量是“这些待播 cues 预计占用多少 Presentation Time”，而不是简单的 transition 数或 ModelTime 差：十分钟世界旅行可以被压缩成十秒动画，十个同刻对话 occurrence 反而可能需要更长表现时间。

本批不让 buffer 大小进入世界状态、Forecast 或 Journal，也不让 Presentation 反压 Authority。后续只有在真实 profile 证明 unbounded ahead-run 造成内存或体验问题时，才设计 high-water policy。

## 4. AuthorityLoop 协议

### 4.1 App-local committed envelope

首个 consumer 只有 FirstBoard.Demo，因此本批不新建 `src/Runtime`，也不预建泛型 `ICommittedHistory<TFact>`。在 App 内部使用最小 envelope：

```text
CommittedTransition
    Version : WorldVersion
    Batch   : JournalBatch<FirstBoardFact>
```

它不复制 batch identity，不是第二份历史；只是把 Kernel 已完成安装的 authoritative commit 交给单消费者。

### 4.2 Channel

使用 .NET 标准库的 unbounded single-writer/single-reader `Channel<CommittedTransition>`：

- 只有 AuthorityLoop 写；
- 只有 PresentationLoop 读；
- AuthorityLoop 在 `StepAsync` 返回 `Committed` 后、开始下一 Step 前，从同一个 task 读取 `journal.Batches[^1]`；
- 验证 `kernel.Version.TransitionCount == journal.Batches.Count`；
- 由 AuthorityLoop 把同步的 app-local `SessionHead.CurrentCommittedVersion` 更新为 `kernel.Version`；Human driver 只读这个同步投影，不并发读取 Kernel；
- 将 `kernel.Version` 和刚提交 batch 放入 channel；
- Presentation 从不访问 `kernel.World`、`kernel.Version` 或 `journal.Batches`；
- `TryWrite == false` 是 session invariant failure，不能静默漏掉历史。

Channel 是本进程 hot-path 传输，不替代 Journal authority，也不承诺 crash catch-up。它可以直接传递 immutable batch 引用，无需深复制 facts。

首个 live slice 只从新建的 empty lineage / genesis world 启动，因此 `C=P=(LineageId, 0)`，后续每次投递都严格 +1。已有 Journal prefix 的 resume session 需要先把 prefix 投影到 P、再接 live suffix，属于第 11 节延期的恢复设计；本批不要偷偷用并发枚举 `Batches` 补做半套 resume。

### 4.3 建议伪代码

```text
AuthorityLoop(kernel, journal, notAfter, channel, sessionState, ct):
    committedThisRun = 0
    try:
        while true:
            status = await kernel.StepAsync(notAfter, ct)

            if status == Committed:
                committedThisRun += 1

                // Commit 后不得先观察 cancellation。
                batch = journal.Batches[^1]       // authority task only
                version = kernel.Version
                require version.TransitionCount == journal.Batches.Count
                sessionState.PublishCommitted(version)
                require channel.Writer.TryWrite((version, batch))

                continue

            sessionState.RecordTerminal(status, kernel...)
            channel.Writer.TryComplete()
            return HostRunResult(...)
    catch error:
        sessionState.RecordFault(error)
        channel.Writer.TryComplete(error)
        throw
```

顺序中的关键点：Kernel 在 Journal publish 成功后不再观察 cancellation。即使 `StepAsync` 返回时取消已经被请求，这个 batch 仍是正式历史，AuthorityLoop 也必须先投递它，再决定停止。否则会出现 `C` 已推进、P 永远无法收到该 batch 的永久裂缝。

`BoundaryReached` / `Exhausted` 正常完成 channel；fault 以 fault 完成 channel。Presentation 应先消费所有已入队 committed items，再报告终止或故障。未发布的 selected Plan、未返回的 Player response 和 candidate 永远不进入 channel。

### 4.4 Authority 等待的准确语义

“Engine 只等待 Human”不能解释成 AI 正在思考时越过它继续提交别的 occurrence。正确语义是：

> Authority 不等待 Presentation 节奏，但必须等待当前 winner 所需要的 Human / AI / script Player 和 Journal publication。

若 AI DecisionPoint 已胜出，在其返回前越过它会破坏 frozen world、single winner、re-Forecast 和因果顺序，并迫使系统引入 speculative branch、stale proposal 和完成顺序竞态。本批明确不做。

## 5. PresentationLoop 协议

### 5.1 私有 replay/projection world

Presentation 不读取 Authority 当前 World，因为 Authority 可能已经领先多个 transitions；直接读取会造成角色瞬移、提前显示死亡/相遇/物品转移，并破坏 Human 所见的历史顺序。

PresentationLoop 从与 Authority 相同的 genesis world 开始，维护自己的私有 replay state：

```text
PresentationState
    ReplayWorld
    PresentedVersion P
    LastPresentedInstant
    Mode : Player | Developer
    HumanActorId
```

`ReplayWorld` 使用 FirstBoard reducer 按完整 batch 增量 fold。它只为历史查询、插值和 cue projection 服务，不能被 Player action planner 或 Kernel 使用。关闭 UI 后可以丢弃；从 genesis + Journal prefix 可完全重建。

当前 [`FirstBoardReducer`](../../src/FirstBoard/FirstBoardDomain.cs) 已是 public，Presentation 可以用相同 `ScenarioInstance.Graph` 创建独立 reducer，逐 fact 调用现有 `Apply` 并在完整 batch 后调用 `Validate`。不要在 Presentation 复制领域折叠规则，也不要为了 UI 再造一份简化 reducer。

### 5.2 一个 batch 的播放步骤

对每个 `CommittedTransition(version, batch)`：

1. 验证 lineage 相同且 `version.TransitionCount == P.TransitionCount + 1`。
2. 用 batch 前的 `ReplayWorld` 表现 `LastPresentedInstant.ModelTime → batch.Instant.ModelTime` 之间已经被 committed prefix 证明成立的 Lazy Flow。
3. 在 scratch projection 上原子 fold 整个 batch、验证 post-world；不得把 batch 内 fact prefix 暴露为可交互 world。
4. 从 `(HumanActorId, Mode, preWorld, batch, postWorld)` 生成 presentation cues。
5. 以 Presentation pacing 播完本 batch 的 visible cues。
6. 将 `ReplayWorld = postWorld`，最后才令 `P = version` 并通知所有 catch-up waiters。

同一 ModelTime 上的 batches 必须按 CausalOrdinal / TransitionCount 顺序播放。一个多 fact batch 可以编排成连续文字或镜头，但不能在中间开放 Human input。

### 5.3 不外推未提交未来

Presentation 只允许在已提交 prefix 覆盖的时间范围内插值：

- 若 traversal start 已提交、Authority head 已推进到稍后 T，则可按该 segment law 求值并播放到 T；
- 若 head 仍停在 start 的同刻，不得仅根据 `ArrivalDue` 猜测角色必然抵达；中途可能发生尚未提交的 encounter、reverse 或其它 discontinuity；
- Forecast candidates、预计到期、LLM 尚未返回的 proposal 都不是 known history。

首个 Console slice 不要求实现逐帧空间插值；可以先把 ModelTime gap 压缩为等待和文字 cue。但 replay state 与处理顺序必须为未来图形插值保留正确位置。

## 6. Player / Developer 两种表现模式

### 6.1 用户裁决

面向 HumanPlayer 的信息遮蔽放在 PresentationLoop 内，而不是增加安全边界、复制 Journal 或改造 Kernel。单机游戏不追求抵抗恶意本地用户；目标是避免正常游玩中的剧情泄露，并让开发者可以一键观察更多信息。

```text
raw committed batch
        │ App-private
        ▼
FirstBoard Presentation Projector
        ├── Player mode cues ─────> 正式玩家 UI
        └── Developer overlays ───> 额外面板 / 调试行 / 标记
```

模式切换只影响 cue projection 和 UI 元素，不得影响：

- Kernel Forecast / selection；
- Objective World 或 Journal；
- AI/Human 的 `Observation` 与 affordances；
- PlayerSpatialKnowledgeGetter；
- Presentation cursor 的 batch 顺序；
- model-time 或 scheduler seed。

### 6.2 Player mode

Player mode 以指定 `HumanActorId` 为 viewpoint。第一版采用简单、显式的 whitelist projector：

- Human 自己参与或能够直接观察的行动与结果可显示；
- Human 当前可见的同地点 Actor / Object 变化可显示；
- Human 自己获得的 KnownFacts、持有物和 TravelGoal 可显示；
- 与 Human 无关且不在其当前可见环境中的 AI 私有观察、秘密、内心独白和远方状态变化不显示；
- 对时间推进必要但不可见的 batch 静默 fold，仍推进 P；
- 世界级公开事件（例如将来明确设计为全局可感知的钟声）由 FirstBoard projector 显式列入 whitelist，而不是因为 raw fact 存在就默认公开。

精确的感知范围属于 FirstBoard 产品规则，不能从 `ActorId == Human` 这一条条件草率推导。施工时先覆盖当前 FirstBoard 的 fact union；出现语义不确定的 fact 时默认不向 Player mode 展示，并通过测试明确裁决。

### 6.3 Developer mode

Developer mode 在 Player mode 主画面之外增加显式 debug 元素，例如：

- Objective batch 的 FactName、CandidateKey、LogicalInstant、WorldVersion；
- 全部 Actor 的地点 / traversal target / ETA / activity / TravelGoal；
- pending encounter 与 passage entry access；
- AI decision / memory profiler 状态；
- C、P、积压 batch 数和估算 presentation seconds；
- 当前 Authority status、channel 状态和最近 fault。

Developer mode 可以全知，但 UI 必须清楚区分“角色可知内容”和“开发者 overlay”。不要把 debug 文本混进 `Observation`、MemoryBank 或 Player-facing cue，从而无意改变 AI/Human 决策。

第一版允许启动参数选择 `--presentation player|developer`；运行时热切换可以很便宜时一并提供，否则延期。切换模式不能回放或跳过历史，只改变后续 cue/overlay 的展示。

## 7. Human Decision Barrier

### 7.1 必须解决的剧透轨迹

```text
Authority 已提交到 C=N
Presentation 只播放到 P=N-20
Kernel 从 version N 的 frozen world 选中 Human DecisionPoint
```

如果此时马上显示 request，Human 会先看到未来 observation / affordances，再看到导致它们成立的二十个历史 batch。例如先出现“你在通道中遇见 Bob，继续还是掉头”，几秒后才播放进入通道和相遇。这违反因果体验和信息隔离。

### 7.2 最小 app-local Human driver

使用 `PresentationGatedHumanPlayerDriver : IPlayerDriver`，不让 Kernel 或 FirstBoard rule 判断 PlayerKind：

```text
DecideAsync(request, ct):
    require no other active Human request

    revealAfter = synchronized SessionHead.CurrentCommittedVersion
    create one pending interaction(request, revealAfter, completion)

    await presentationGate.WaitUntilPresented(revealAfter, ct)
    expose request to Console UI

    loop:
        input = await UI.ReadDecision(ct)
        if input envelope / affordance invalid:
            show local validation error and continue
        complete interaction once
        return decision
```

为什么在 `DecideAsync` 调用时捕获 C 是正确的：当前 selected Step 已占住 lineage，在 driver 返回前不会有另一个 commit。因此 captured head 就是 request 的 frozen-world committed prefix。

共享 C/P 的 gate 必须使用 lock、channel 或正确的 async condition；Human/UI task 不得并发裸读 `kernel.Version`。`TaskCompletionSource` 使用 `RunContinuationsAsynchronously`，避免 Human input callback 内联继续执行 Kernel Plan。

### 7.3 Prompt 何时可见

在 `P == revealAfter` 且 revealAfter 对应 batch 的全部 cue 已完成前：

- 不显示 request 内容；
- 不显示 affordances；
- 不显示“轮到 Human”“遇到了谁”等可能泄露未来的具体提示；
- 可以显示与未来内容无关的通用播放状态。

追平后：

- 发布 request；
- Authority 继续停在当前未完成 Step；
- Human 输入合法 decision；
- FirstBoard rule 继续进行最终通用 validation 和 action planning；
- Kernel 提交 N+1；
- UI 不直接假定输入成功，等待 N+1 的 committed cue 再改变世界画面。

### 7.4 单 pending 与取消

单机、单 UI、Kernel 单 in-flight Step 下，本批只允许一个 pending Human interaction：

- UI 回答必须匹配当前 `DecisionId`；
- completion 只能成功一次；
- malformed / 非 affordance 输入在 Human adapter 内提示并重问，不要让整个 Step 因普通输入错误 fault；
- FirstBoard rule 仍执行最终 `PlayerDecisionValidator`，Human adapter 不是新的 authority；
- cancellation、退出或 session fault 清除 pending interaction，迟到输入被拒绝；
- restore / fork 本批不保留 pending TCS，未来从 committed prefix 重新 Forecast request。

额外 `InteractionToken`、跨窗口 mailbox、pending interaction persistence 和热切换 lineage 均延期。当前 TCS 实例、单 pending 约束、DecisionId 与 cancellation 已足够。

## 8. AI 等待、缓冲耗尽与运行状态

AI Player 继续使用现有 `IPlayerDriver`。Authority selected AI decision 后等待其 `DecideAsync`；Presentation 同时继续排空 channel。

可见行为：

```text
Engine ahead
→ Presentation 按节奏消费 backlog

P == C 且 AI still thinking
→ 显示非领域性的 buffering / thinking 状态
→ 不推进 P，不推进 ModelTime，不伪造活动

P == C 且 Human pending
→ 此时才显示 Human request

Human submits
→ 等下一 committed batch
→ 再显示行动后果
```

首个 slice 可以保留 app-local、ephemeral session telemetry：Authority 是否正在 Step、是否有 Human prompt、channel 是否为空、terminal status / fault。不要把 LLM 开始思考、buffering、UI pause 或动画完成写成 DomainFact 或 Journal history。

本批不要求精确区分“AI 正在网络排队”“模型推理”“MemoryBank maintenance”等 UI 状态。现有 [`DemoLlmProfiler`](../../src/FirstBoard.Demo/DemoLlmProfiler.cs) 可以继续记录开发者 telemetry；Player mode 只需一个不泄露内容的通用等待提示。

## 9. 第一版 UI 技术裁决

### 9.1 选择 Console

第一批采用 Console，而不是 SignalR 或 WinForms：

- 不引入 web server、browser asset、连接生命周期或前后端协议；
- 不绑定 Windows，未来 Steam / TapTap 技术路线仍开放；
- coding agent 可直接运行、截图/抓取输出、编写 fake terminal tests；
- 与当前 `FirstBoard.Demo`、LLM backend、profiler 和输出目录自然结合；
- 足以验证双循环、固定节奏日志、Human barrier、Player/Developer mode 和 AI 延迟遮蔽。

SignalR 只有在浏览器 UI 成为真实 consumer 时再考虑；WinForms 只有在决定接受 Windows-only frontend 时再考虑。Godot 仍是正式图形产品的优先候选，但本批不为它预建 scene、animation 或 IPC abstraction。

### 9.2 落点

首个垂直切片优先原位扩展 `src/FirstBoard.Demo`，增加一个 interactive live-session mode，而不是立即新建通用 Runtime 或复制整套 LLM composition：

```text
src/FirstBoard.Demo/Live/
    LiveSession.cs
    CommittedTransition.cs
    PresentationLoop.cs
    PresentationGate.cs
    TerminalHumanPlayerDriver.cs
    FirstBoardPresentationProjector.cs
    TerminalUi.cs
```

实际类名可按实现调整，责任边界不能混淆。`Program.cs` 只负责 composition、启动、取消与最终输出；不要继续把并发协议堆进顶层文件。

建议增加配置：

```text
--human alice|bob|none
--presentation player|developer
--presentation-interval-ms N
```

`--human none` 继续支持 all-AI demo。指定 Human 后只为另一名 Actor 创建 LLM driver；Human 不创建或调用 LLM backend。默认值在施工时可从最便于现有 smoke run 的选择出发，但 help 和 manifest 必须记录实际配置。

组合约束：

- `--human` 只接受 Alice、Bob 或 none，不支持同时两名 Human；
- `--presentation player` 要求存在一个 HumanActorId；all-AI run 使用 developer mode，避免伪造一个不存在的角色 viewpoint；
- Human 侧不创建 `LlmPlayerDriver`、MemoryBank maintainer 或 `DecisionBudgetPlayerDriver`；
- 当前 `Program.cs` 对 Alice/Bob 两个 LLM 的 flush/dispose 假设必须改为只处理实际创建的 drivers/backends；
- manifest、最终 record 和 profiler 必须容许一侧没有 LLM turn/memory trace。

### 9.3 可测试 UI port

生产实现不能让并发语义只能通过真实 `Console.ReadLine` 测试。App 内使用窄的 terminal port 或可注入 delegates：

```text
write cue / status / prompt
read one Human command asynchronously
```

测试替身可以控制输入到达时刻、记录 prompt 首次可见时的 P，并避免 wall-clock flaky test。这个 seam 是具体 UI consumer 的测试接缝，不要提升成通用游戏 UI framework。

## 10. Presentation pacing

第一版 pacing policy 保持简单：

- 每个 visible batch 或 cue 使用可配置固定最小间隔；
- hidden occurrence 不加 occurrence-specific 人为等待，但若它与前一 batch 之间存在需要表现的 Human-visible interval flow，仍按相同 pacing 播放该区间；随后按序 fold / ack；
- 输出中保留 batch 的世界时间，wall-clock 间隔不声称与 ModelTime 1:1；
- `presentation-interval-ms = 0` 可用于测试和开发者快速排空；
- 任何 delay 都支持 cancellation；
- Human prompt 的 reveal 必须等待该 batch 的所有 delay/cue 完成。

未来图形前端可把 pacing 替换成 domain cue duration、镜头、速度调节与小说式时间压缩，但不得改变 `P <= C`、完整 batch ack 和 Human barrier。

本批不做：

- 根据 buffer 自动变速；
- 为遮蔽 LLM 延迟而伪造 idle DomainFact；
- 把每个 ModelTime ms 映射为固定 wall-clock 比例；
- rewind、seek、pause 后继续推进的完整产品语义；
- 动画 clip schema 或通用 tween framework。

## 11. Persistence、退出与恢复边界

本批 live loop 可以继续使用 `InMemoryJournal`，不要求把 Atelia persistence 接进 interactive Demo。必须先把运行时语义跑通，不能借 UI slice 顺便设计完整 save system。

未来 MVP 最简单的干净存档点是：

```text
P == C
且 Kernel 正等待 Human 的未完成、尚未发布 Step
```

此时取消未完成 Step 会零提交；重开后 Replay 到 C，再 Forecast 会重新产生 Human decision。若只允许在这种 Human barrier 创建正式 save，就不需要持久化 Presentation cursor 或 pending interaction。

必须明确这不等于任意时点 crash-resume：当 P<C 时 Authority 可能已经持久化 Human 尚未看见的 tail。没有单独保存 P 或历史 range replay 时，崩溃后无法判断哪些 committed events 已经展示。未来若产品要求中途随时存档/崩溃恢复，再选择：

- 持久化 presentation cursor；或
- 只把 P=C checkpoint 提升为正式 save，之后的 ahead tail 视为临时运行；或
- 接入可按 WorldVersion prefix 补读的 history reader。

本批不声称解决该问题，也不为它预建接口。

## 12. 分层与依赖裁决

### 12.1 本批保持

- Kernel：唯一 Forecast / selection / commit authority；零 Presentation/Human 类型。
- Host：现有通用 headless runner 保持可用；本批不因唯一 App consumer 立刻扩大公共 API。
- FirstBoard：领域 rules、facts、reducer、Observation 和 Human-safe projection policy 的语义来源。
- Player / Protocol：Human、AI、script 继续共享 `IPlayerDriver` / `DecisionRequest` / `PlayerDecision`。
- FirstBoard.Demo：首个 live session composition、Console UI、app-local channel/gate/projector。

### 12.2 明确延期

- `src/Runtime` 新程序集；
- 通用 `ICommittedHistory<TFact>` / subscriber bus；
- Journal concurrent read API；
- `HistoryCursor`、presentation receipt 或 ack journal；
- Kernel/Host 中的 Human/AI PlayerKind 分支；
- `DecisionRequest.BasedOnWorldVersion` 协议变更；
- SignalR、WinForms、Godot 或 RPC UI；
- 多 Human、多窗口、远程输入与认证；
- 任意时点 crash-resume。

触发提取通用 Runtime 的证据应至少是以下之一：

- Godot frontend 成为第二个真实 consumer，并开始复制同一 AuthorityLoop / gate；
- 第二个 game prototype 需要相同 committed feed；
- persistence restore 需要按 cursor 补读且 app-local channel 无法满足；
- profiling 证明 live runner 生命周期和 backpressure 已成为跨游戏共同问题。

在此之前，标准 `Task + Channel + WorldVersion` 比新的框架程序集更简单、更灵活。

## 13. 施工顺序

按以下 waves 落地；每一 wave 结束保持 solution 可编译：

1. **App-local coordination primitives**
   - committed envelope、C/P state、unbounded SPSC channel、PresentationGate；
   - 用纯 fake batches/world 测顺序、ack 与 cancellation；
   - 不修改 Kernel / Journal。
2. **FirstBoard PresentationLoop**
   - presentation-side replay world；
   - 完整 batch fold；
   - Player / Developer projector；
   - fixed-rate terminal cue playback。
3. **Human driver**
   - 单 pending request；
   - capture revealAfter=C；
   - 等 P catch-up 后显示；
   - Human 输入解析、局部重问、最终通用 validation。
4. **AuthorityLoop integration**
   - 在 Demo live mode 中直接使用 `FirstBoardScenario.CreateKernel`；
   - 每个 successful Step 后投递一次；
   - normal completion、fault、cancellation 与 channel drain。
5. **Composition and UX**
   - `--human`、`--presentation`、pacing options；
   - 一名 Human + 一名 LLM 的真实 run；
   - all-AI live log 保持可运行；
   - developer overlay 展示 C/P/backlog/LogicalInstant。
6. **Regression and documentation**
   - 现有 headless scenario、persistence、LLM、Spatial 全回归；
   - 更新本文状态和实际文件/提交/测试结果；
   - 若公共边界发生变化，再同步 Design Note 002/003；不提前改 Law。

为 app-local internal types 新建 `tests/FirstBoard.Demo.Tests`，加入两个 solution，并通过 `InternalsVisibleTo` 或等价的窄测试接缝访问；不要为了测试把 live-session coordination 全部改成 public framework API。主要并发验收放在这个项目，FirstBoard 领域投影语义可按实际归属补进现有 `FirstBoard.Tests`。

若实现发现 app-local coordination 无法在不复制 Kernel authority 的前提下完成，先停下讨论，不要直接把 live notification 塞进 `AppendBatch` 或让 UI 并发读取 Kernel。

## 14. 验收矩阵

| ID | 必须证明的行为 |
|---|---|
| AUT-1 | 只有 AuthorityLoop 调用 `StepAsync`；慢 Presentation 不阻止 Authority 连续提交非 Player occurrence。 |
| AUT-2 | 每个 `StepStatus.Committed` 恰好产生一个含对应 `WorldVersion` 与完整 `JournalBatch` 的 channel item；没有 fact prefix。 |
| AUT-3 | commit 后即使 cancellation 已请求，也先投递该 batch；未提交 draft、candidate 和失败 Plan 零投递。 |
| AUT-4 | `BoundaryReached` / `Exhausted` 正常完成 channel；fault 允许 Presentation 排空 committed items 后看到同一 fault。 |
| CUR-1 | 同 lineage 始终 `0 <= P.TransitionCount <= C.TransitionCount`，并且 P 逐 batch +1。 |
| CUR-2 | P 只在 batch 的全部 cue/delay 完成且 projection fold 成功后推进；收到 item 不提前 ack。 |
| CUR-3 | 同 ModelTime 多个 batches 按 TransitionCount/CausalOrdinal 呈现；atomic batch 不暴露可交互中间 world。 |
| JRN-1 | Presentation 不并发枚举 `journal.Batches`，不读取 `kernel.World/Version`；只有 Authority task 读取 latest batch。 |
| PRJ-1 | Presentation replay world 对每个 prefix 与 authoritative reducer replay 等价；它不能回写 Authority。 |
| PRJ-2 | Player mode 不显示另一 Actor 的私有 observation/known fact/LLM trace 或远方隐藏变化；不可见 batch 仍推进 P。 |
| PRJ-3 | Developer mode 在 Player cues 外显示 objective facts、C/P 和 debug state；切换模式不改变 Journal、Observation 或最终 World。 |
| HUM-1 | C=N、P<N 时 selected Human request 对 UI 完全不可见；只有 P==N 且最后 cue 完成后才显示。 |
| HUM-2 | Human 思考期间 Authority version 不变；合法 answer 后只由 Kernel commit 使 C 前进，UI 不直接改 World。 |
| HUM-3 | malformed/非 affordance Console 输入在 adapter 内提示并重问；不发布 fact、不结束当前 request。 |
| HUM-4 | cancellation/fault 清除单 pending interaction；迟到输入不能完成新 request。 |
| AI-1 | AI selected 时 Authority 正确等待；若 P<C，Presentation 同时继续播放；P==C 后只显示 buffering，不播放预测未来。 |
| UI-1 | 一名 Human + 一名真实或 controllable delayed AI 能完整运行；Human 能通过 Console 完成普通与 passage encounter decision。 |
| UI-2 | `--human none` 的 all-AI live playback 仍可运行并生成现有 trace/record/manifest。 |
| RGR-1 | Kernel、Host、FirstBoard、Persistence、Player.Llm、Spatial 与两个 solution 全回归；Kernel/Journal 无不必要修改。 |

### 14.1 必测并发轨迹

测试避免真实 wall-clock 长等待，使用 controllable `TaskCompletionSource`、fake pacing 与 fake terminal：

1. **Authority ahead**：Presentation gate 暂停；Authority 连续提交多个无需 Player 的 transitions；断言 C 前进而 P 不变，channel 顺序完整。
2. **AI latency hidden**：delayed AI 尚未完成；Presentation 继续消费已有 backlog；AI 完成后 Authority 恢复，无重复调用。
3. **AI buffer exhausted**：P 追到 C，AI 仍未完成；断言没有新 cue/world advance，只有 ephemeral buffering status。
4. **Human future hidden**：Human request 已在 driver 内等待，P<C；断言 terminal 没有 prompt/affordance；完成最后 cue 后恰好显示一次。
5. **Human consequence committed**：输入合法 decision 后 UI 不直接改变 projection；收到下一 committed batch 后才显示结果。
6. **Same-time batches**：多个同 ModelTime batches 保持 ordinal 顺序，P 逐 transition 前进。
7. **Hidden fact**：Player mode 对某 batch 零 cue，但 projection 和 P 正确推进；Developer mode 能看到相应 overlay。
8. **Cancel after commit**：模拟 commit 后 cancellation；断言 committed batch 仍被 channel 接收和播放。
9. **Fault after prefix**：前两个 batches 已提交，下一 Step fault；断言 Presentation 先播放两个，再报告 fault。

## 15. 明确不做与复杂性停线

本批不做：

- 两个可写 World 或 Presentation authority；
- speculative AI decision、并行 winner 或越过未决 AI occurrence；
- 把 Forecast future、ArrivalDue 或 LLM proposal 当作历史播放；
- 通用实时游戏 Runtime 平台；
- Journal pub/sub、并发 collection 或 durable presentation ack；
- Human/AI 类型进入 Kernel；
- 多人 Human、网络、SignalR、Forms、Godot；
- 图形插值、镜头系统、动画资源 schema；
- 任意时刻 save/load、崩溃后精确恢复已观看 frontier；
- 安全沙箱、反作弊或阻止本地用户读取存档；
- 把 Developer overlay 信息写入 Player Observation / Memory。

遇到以下情况应先与用户商量：

- FirstBoard reducer 无法被 Presentation 增量 replay，必须复制领域规则才能绘制；
- Human request 在捕获 C 后仍可能发生同 lineage commit，说明 single in-flight 假设被破坏；
- Console slice 必须修改 Kernel 才能获得 committed batch；
- Player mode 的最低可玩投影要求建立一套新的 durable perception history；
- 一名 Human + 一名 AI 无法形成可玩的真实 trace，必须先扩张 FirstBoard 内容；
- unbounded channel 被真实长跑证明造成不可接受的内存问题；
- 必须支持 P<C 时的正式存档或 crash resume 才能完成当前 playable slice；
- 为共享 Demo LLM composition 不得不引入大规模程序集重组。

## 16. Definition of Done

施工完成至少执行：

```powershell
dotnet restore DramaBoard.Local.slnx --nologo
dotnet test tests/Kernel.Tests/Kernel.Tests.csproj --no-restore --nologo
dotnet test tests/Host.Tests/Host.Tests.csproj --no-restore --nologo
dotnet test tests/Protocol.Tests/Protocol.Tests.csproj --no-restore --nologo
dotnet test tests/Decision.Validation.Tests/Decision.Validation.Tests.csproj --no-restore --nologo
dotnet test tests/Player.Tests/Player.Tests.csproj --no-restore --nologo
dotnet test tests/Player.Llm.Tests/Player.Llm.Tests.csproj --no-restore --nologo
dotnet test tests/FirstBoard.Tests/FirstBoard.Tests.csproj --no-restore --nologo
dotnet test tests/FirstBoard.Demo.Tests/FirstBoard.Demo.Tests.csproj --no-restore --nologo
dotnet test tests/FirstBoard.Persistence.Tests/FirstBoard.Persistence.Tests.csproj --no-restore --nologo
dotnet test tests/Spatial.Tests/Spatial.Tests.csproj --no-restore --nologo
dotnet test DramaBoard.slnx --no-restore --nologo
dotnet test DramaBoard.Local.slnx --no-restore --nologo
dotnet build src/FirstBoard.Demo/FirstBoard.Demo.csproj --no-restore --nologo
git diff --check
git status --short
```

另需进行至少一次真实或手动 smoke run：

```text
Human Alice + AI Bob
→ Bob 的 delayed LLM decision 期间仍有 committed log 可播放
→ Alice 的 prompt 只在 P 追上 C 后出现
→ Alice 能完成普通地点行动
→ Alice 或 Bob 触发 passage encounter 时，Human 能 Continue / Reverse
→ terminal/timeout 可干净取消
→ 最终 drama record、turn trace 和 manifest 仍生成
```

实现完成后：

- 把本文状态改为 **Implemented and verified**；
- 记录实际 commits、测试计数、smoke run 配置与结果；
- 记录 Player / Developer projector 的实际披露规则；
- 记录是否修改了 Host/Protocol 以及为什么；
- 若 `src/Runtime` 仍未建立，明确继续延期的证据；
- 若发生有意识偏差，写出原方案、实际方案与具体 failure trace，不只写“实现调整”。

## 17. 本轮设计收敛记录

本轮由主 agent 核对仓库，两名独立 reviewer 分别从最小架构与并发/因果语义审查。两方在以下内容上直接一致：

- Kernel/Journal 继续作为唯一 authority；
- 使用 AuthorityLoop 与 PresentationLoop，而不是两个共同推进世界的线程；
- 以 WorldVersion 表达 C/P，冻结 `P <= C`；
- Human request 必须等 Presentation catch-up；
- Presentation 不并发读现有 Journal collection；
- raw objective Journal 必须经过 Human-facing projection；
- AI backlog 耗尽后不能伪造未来。

最初的唯一分歧是是否立即新建 `src/Runtime` 和泛型 committed-history API。交叉检验没有找到当前 app-local `Task + Channel + WorldVersion` 方案会失败的具体轨迹；主张立即建 Runtime 的 reviewer 因而撤回该建议。最终一致裁决是先完成 FirstBoard.Demo Console 垂直切片，等第二个真实 consumer 或 persistence range-read 需求出现后再提取。

用户随后补充并冻结：

- FirstBoard 可以增加 UI，具体技术由 coding agent 按施工便利选择；本文件据此选择 Console。
- HumanPlayer 信息遮蔽放在 PresentationLoop 内；支持 Player / Developer 一键切换，Developer 通过额外 UI 元素显式显示更多信息。
- 单机产品不要求把这条投影边界建设成抵抗恶意本地用户的安全边界。

这些裁决共同形成了本文的施工范围。

## 18. 实施与验收记录

### 18.1 实际落点与边界

本批于 2026-08-23 完成。实现保持了第 12 节裁决：运行时能力全部落在
[`src/FirstBoard.Demo/Live`](../../src/FirstBoard.Demo/Live)，没有建立 `src/Runtime`，也没有修改
Kernel、Host、Protocol、FirstBoard、Spatial、Journal 或 Player 的权威语义与公共 API。两个 solution
只增加了新的 internal Demo 测试项目。

主要组成如下：

- `LiveAuthorityLoop` 是唯一 Kernel writer；每个已安装的 commit 恰好投递一个
  `CommittedTransition`。
- `LiveSessionCoordination` 在一个同步对象中维护 C/P、catch-up gate 与 failure；authority fault
  后仍允许已经入队的 committed prefix 前进 P，但 failure 优先于新的 Human prompt。
- `FirstBoardPresentationLoop` 从 genesis 维护私有 replay world，完整 scratch-fold/validate 一个 batch
  后才输出并 ack；正常运行中每个 visible cue/overlay 完成 pacing 后才推进 P。
- `PresentationGatedHumanPlayerDriver` 捕获 request 产生时的 C，只在 P 追平后显示
  `DecisionRequest`；解析错误和不在 affordance 内的输入原地重问。
- `TerminalUi` 显示观察到的出口、目的地、预计时长、可用性、当前 action affordances 和全部候选
  ID，因此 Human 不需要查看 objective world 或源码才能输入合法命令。
- `DemoLlmComposition` 只为实际 AI Actor 创建 LLM driver、MemoryBank 和 backend；Human 侧配置保留
  在 manifest 中作为声明，但不会实例化或调用。
- `Program` 现在只做 composition、Ctrl+C/timeout、最终 artifacts 与资源释放；all-AI 和 Human+AI
  都走同一个 LiveSession。

取消采用“完整输出、取消 pacing、快速排空”的终止语义：已经提交的所有 cues/overlays 仍被输出，
然后 P 才 ack；剩余人为 delay 被跳过。Authority 取消前已经提交的 prefix 会被保存在 app-local
`LiveSessionCanceledCapture` 中，生成状态为 `Canceled` 的局部 drama record 和带 result summary 的
manifest。若模拟已完成、只在 memory flush 时取消，则保存完整 authoritative capture，并在 manifest
中记录 `CanceledAfter<StepStatus>`。

`src/Runtime` 继续延期的具体证据是：本批仍只有 FirstBoard.Demo 一个真实 consumer；
`Task + Channel<CommittedTransition> + WorldVersion` 已通过完整并发轨迹，未发生跨游戏复制，也没有
cursor range-read、durable presentation ack 或第二个 frontend 的现实需求。此时提取泛型程序集只会增加
尚无调用者的生命周期与恢复 API。

### 18.2 实际 Player / Developer 披露规则

Player projector 对当前 FirstBoard 的 17 种 Game payload 和 9 种 Spatial payload 使用显式、穷尽检查的
whitelist；新增而未分类的 payload 默认不可见，并由测试迫使后续设计者明确裁决。

- Human 自己的行动、等待、观察、票券、目标、拒绝结果和新增 KnownFacts 可见。
- 与 Human 同地点的 Actor 行动、松散 Object 变化、抵达与开箱可见；对话只在 Human 是说话者或
  接收者时可见。
- passage contact/open/resolution 只对精确参与者可见；Continue/Reverse 都使用 commit 后 world
  描述实际结果。
- passage entry access 只在 Human 位于其任一端点时可见；未来 scheduled patch 本身不泄露，生效后
  才按端点可见性显示。Cellar seal 的 Game/Spatial facts 合并为一个主 cue。
- 同一 atomic batch 内的 Game/Spatial 配套 facts 会合并，避免重复讲述 travel、object move、contact
  和 reversal。
- 远方私有观察、KnownFact、行动和 passage 状态不显示，但 batch 仍完整 fold 并推进 P；跨越到更晚
  ModelTime 时仍可产生不泄露远方事实的 `time.advance`。

Developer mode 在上述 Player lane 之外显示每个 objective batch 的 WorldVersion、LogicalInstant、
CauseKey、FactName/摘要以及当时 C/P/backlog。它不写回 Observation、MemoryBank、Journal 或
Objective World。all-AI developer run 没有伪造 Human viewpoint，因此只显示 developer lane 与通用状态。

### 18.3 测试与真实 smoke

最终验证结果：

```text
dotnet restore DramaBoard.Local.slnx                  passed
dotnet test DramaBoard.slnx                          448/448 passed
dotnet test DramaBoard.Local.slnx                    472/472 passed
dotnet test tests/FirstBoard.Demo.Tests              130/130 passed
dotnet build src/FirstBoard.Demo                     0 warnings, 0 errors
git diff --check                                     passed
```

其中项目级计数为 Kernel 95、Host 2、Protocol 33、Decision.Validation 31、Player 5、
Player.Llm 31、Player.Agency 8、FirstBoard 61、FirstBoard.Demo 130、Spatial 52、
Journal.Atelia 14、FirstBoard.Persistence 10。

并发与可玩性证据包括：Authority ahead、AI backlog 排空后 buffering、Human future hidden、完整 batch
后果、same-time ordinal、hidden fact、cancel-after-commit、fault-after-prefix、canceled prefix replay、
Player privacy 和 real Kernel 最终 replay 等价。默认 genesis + seed 0 还形成了确定性的 4-batch
passage encounter：Alice 与 controllable Bob 从道路两端进入，Human Alice 分别通过 Console command
完成 Continue 与 Reverse；两条分支都验证 opened cue 在第二次 prompt 之前，resolved cue/事实在
Kernel commit 之后。

三次程序级 smoke 的可再现配置与结果：

1. **Human Alice + real AI Bob**：`--human alice --presentation player
   --presentation-interval-ms 100 --seed 0 --until-ms 0 --max-turns-per-actor 1`，Bob 使用本机 Codex
   `gpt-5.6-luna/low`。Alice 输入 `wait 1000` 后，已提交的 `actor.wait-started` 在 Bob 二十余秒的真实
   LLM 等待期间先播放，随后进入 buffering；最终 `BoundaryReached`、3 transitions、1 LLM turn，
   record/turn trace/runtime profile/manifest 全部生成于 `artifacts/smoke-0003-human-ai`。
2. **all-AI compatibility**：相同 seed/boundary，`--human none --presentation developer
   --presentation-interval-ms 0`；最终 `BoundaryReached`、3 transitions、2 LLM turns，objective overlays
   与全部 artifacts 生成于 `artifacts/smoke-0003-all-ai`。
3. **真实 Console 取消**：Human prompt 正阻塞于 `Console.In.ReadLineAsync` 时发送 Ctrl+C；最终
   `[status:canceled]`、进程正常退出，canceled manifest、局部 drama record、trace 与 profiler summary
   生成于 `artifacts/smoke-0003-cancel-2`。

`artifacts/` 按仓库规则忽略，不作为 source commit 的一部分。

真实 LLM 行动具有非确定性，因此没有强迫一次 real-backend run 同时随机形成 passage encounter。
验收把证据拆成“真实 LLM latency/resource/artifact smoke”与“real Kernel + controllable AI 的确定性
Continue/Reverse 轨迹”；两者共同覆盖 UI-1，而不会为了测试操纵 LLM 输出或增加 initial-world seam。

### 18.4 实施中发现并修正的 failure traces

首次 Ctrl+C smoke 在当前 Windows PTY 中使 `ReadLineAsync` 先返回 EOF，`CancelKeyPress` 尚未来得及
设置 token，旧实现因此抛出 `EndOfStreamException` 并把 run 标为 failed。实际修正是把 Human 输入
结束建模为 app-local `HumanSessionExitException`，由 LiveSession 转换成带 committed capture 的 clean
cancellation；随后同一 smoke 通过。没有为此引入后台输入线程、mailbox 或公共 Protocol 类型。

独立收尾审查还发现两个取消相关的窄问题并已修正：一是不能为了快速取消而跳过 committed cues 后
直接 ack P；现在只取消 pacing，完整输出保持 CUR-2。二是模拟完成后 memory flush 被取消时也必须保留
完整 capture，而不能写一个声称“尚无 prefix”的空 manifest。最终 reviewer 未留下 blocking/high finding。

### 18.5 提交记录

本批 source commits：

```text
d85eab2 feat(demo): add live session coordination
6cf74ce feat(demo): publish live authority commits
0e5b3a5 feat(demo): configure live playback
18d4650 refactor(demo): compose only active AI players
c5110b0 feat(demo): gate human decisions on playback
50fb1ca feat(demo): project committed history for humans
ff04122 feat(demo): replay committed presentation history
79d7c82 docs(demo): describe live player roster
97b56ff feat(demo): run live human sessions
6ccf3f9 fix(demo): make live sessions playable and cancelable
2a27320 fix(demo): preserve live session cancellation
```

本文的验收回写由其后的 documentation commit 完成。
