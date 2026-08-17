# 研发计划 005：LLM Player 设计研究

**定位：第二阶段"AI Player driver"方向的设计研究文档。回答三个核心设计问题（Observation 形状 / Action 形状 / Player 内部状态），并给出 LLM 后端接入与成本策略的分析。本文档是 WP12 的产出，后续 WP13–WP16 的执行依据。**

**日期：2026-08-17。基线：主 slnx 179 / Local.slnx 193 测试绿（WP12 写作时点的历史基线；最新测试数以 002 状态表为准），S1–S14 门槛全修。**

---

## 0. 先盘点：现有架构已经为 LLM 接入准备好了什么

这部分是设计的地基——很多"LLM 接入难题"其实已经被第一阶段的裁决消解了：

| 已有机制 | 对 LLM 接入的意义 |
|---|---|
| **决策进 journal 即 authority**（externalInputs 批次，A1/WP9a） | LLM 的非确定性被 event-sourcing 完美隔离：replay 时不再调 LLM，journal 里的决策就是事实。模拟可重放，只是"未来"不可预测。这是最重要的一条。 |
| **决策停机谓词**（构造注入） | LLM 只在真决策点被问，不是每 tick 轮询——调用次数天然稀疏，成本可控的结构性保证。 |
| **同刻决策屏障**（β/S3） | 同批请求在同一快照构造、彼此不知情、整批提交。天然支持多角色并行问 LLM（未来并行化 driver 调用无语义障碍）。 |
| **拒绝闭环**（RejectedIntent 回带 + 验证失败重问一次 + 拒绝预算超阈强制 wait，S2） | 就是为不可靠决策者设计的。LLM 幻觉出非法动作会被这套机制驯化，不会卡死模拟。 |
| **信念层**（α/S1：affordance 只由 KnownFacts 计算） | Observation 的信息不对称已在装配层成立。LLM 看到的世界天然是"该角色有权知道的世界"。 |
| **决策三态 + 取消恢复**（β） | LLM 超时/网络失败 = 决策 Open 未提交，重入续问。进程崩溃后从 checkpoint 恢复重问即可。 |
| **IPlayerDriver 抽象** | LLM driver 是第四个实现（Null/Scripted/Random 之后），Host 零改动。 |

**结论：Kernel/Host/Protocol 三层预计零改动或近零改动。本方向的全部新代码在"driver 及其后端"这一层。**

---

## 1. Q1：Observation 应该是什么样的？

### 裁决：结构化 DTO 保持不变，新增"叙事渲染层"（纯函数），LLM 消费渲染后的自然语言

- **Protocol 层不动**：Observation/KnownFact/DecisionRequest 保持结构化 wire 形状。理由：结构化是可测试、可版本化的契约；自然语言不是。
- **新增 PromptRenderer（Player 层纯函数）**：`(DecisionRequest, 渲染上下文) → prompt 文本`。渲染是确定性的纯函数，可以单元测试锁定（同输入同文本），LLM 不确定性被隔离在渲染之后。
- **设计依据**：LLM 对自然语言的理解质量和 token 效率显著优于生 JSON；而且**渲染风格本身是戏剧资产**——给 AI 演员的"舞台指示"怎么写，直接影响演出质量。把它做成独立可替换的纯函数层，prompt 工程的迭代不触碰任何契约。

### "自上次以来发生了什么"：用 KnownFacts diff，不新增 Kernel 感知机制

角色需要叙事连续性（"Bob 刚才离开了酒馆"），但 Kernel 没有"事件目击者"机制。两条路：

- 方案 A（弃）：Kernel/装配层新增事件感知过滤器（事件 × 观察者 → 可感知？）——多一套机制，且与 KnownFacts 职责重叠。
- **方案 B（取）：Player 层做 KnownFacts diff**——上次决策时的 KnownFacts vs 本次 → "你新知道的事"。FirstBoard 信念层已经把"感知到的事件"落成 KnownFact（secret 发现、拒绝反馈都是），diff 就是增量叙事的素材。零新机制，与 event-sourcing 精神一致。
- 若未来 diff 表达力不足（如"目击一个动作过程"），再立案感知事件过滤器——先用便宜的方案打到表达力天花板再说。

### Observation 渲染的内容分区（prompt 骨架草案）

```
[角色卡]   你是{名字}：{性格、目标、说话风格}。（静态，装配方提供）
[世界规则] 简述可做什么、输出格式约定。（静态）
[内心状态] 你上次记下的：目标/计划/情绪/对他人的判断。（来自记忆文档，见 Q3）
[当前观察] 现在 {时间}，你在{位置}；在场:{...}；你看见:{...}。（KnownFacts 全量投影）
[新近变化] 自上次以来你得知:{KnownFacts diff}。{若有:你刚才的尝试被拒绝:{RejectedIntent+原因}}
[决策请求] 你现在可以:{AvailableActions 渲染}。请输出:内心独白/选定行动/台词(可选)/更新后的内心状态。
```

---

## 2. Q2：Action 应该是什么样的？

### 裁决：**行动结构化、言语自由化**

- **行动（move/take/place/observe/wait…）**：LLM 从 AvailableActions 中做结构化选择（输出解析为 Intent）。理由：行动改变世界状态，必须过 system 校验；封闭集合+已有的数值域校验（S9）保证可靠性。表达力不足时扩动作集，不开自由通道。
- **言语（say/talk）**：FreeText 自由文本，**不做语义校验**。理由：这是戏剧游戏，对白就是内容本身；说什么谎、许什么诺，语义层面无所谓"非法"。Intent.FreeText 字段已存在。
- **不做"自由意图解析层"**（LLM 说"我想把钥匙偷偷塞进 Bob 口袋"→ 另一个 LLM/规则解析成 Intent）：引入第二层不确定性且贵。表达力问题优先用"扩动作词汇表"解决——这是装配方（Board 设计）的职责，不是 driver 的。远期若真需要，它是独立的可选组件。

### 输出格式与解析

- 主 actor 单次调用仍输出四合一：**内心独白 + 选定行动（结构化）+ 台词（可选）+ 本轮记忆提议**。WP21 起不再让这一输出整体替换 Memory；独立 maintainer 根据提议维护各分块。
- 解析器（Player 层纯函数）：宽松解析（分节标记或 JSON 块），失败→重问一次→仍失败按 driver 失败处理（已有降级路径兜底）。解析器可用录制样本做单元测试。

---

## 3. Q3：LLM Player 的内部状态应该是什么样的？

这是三问中最深的一个。候选模型：

| 模型 | 描述 | 致命缺陷 |
|---|---|---|
| a) 无状态重构 | 每次从 journal/KnownFacts 重构全量 prompt | 没有"内心连续性"：计划、情绪、猜疑不落在世界事实里，角色会失忆自己的意图 |
| b) 会话式 | 每角色一个持久 LLM 会话，增量追加 | 会话是**影子状态**：不在 journal、不可恢复、不可审计——破坏"initialWorld+journal 充分"的全局纪律；上下文无限增长 |
| **c) 显式记忆文档（取）** | 角色维护一份自我书写的"内心状态文档"，每次决策 = 渲染(记忆+新观察) → LLM → (决策+记忆更新) | 每次决策要产出记忆更新（已并入四合一输出）；记忆质量依赖模型 |

### 裁决 c 的设计依据

1. **可恢复性**：记忆文档显式落盘 → Player 状态可 checkpoint，崩溃/重启后精确恢复。会话式做不到。
2. **可审计/可观赏**：能看到每个角色"心里在想什么"——**内心独白是 DramaBoard 独有的观赏维度**（观众看到 Alice 表面应承、内心盘算，这就是戏剧性本身）。
3. **token 有界**：记忆文档给大小预算（如 ~2K token），LLM 自己决定留什么忘什么——遗忘也是角色扮演的一部分。
4. **活原型验证**：这正是本项目主线会话自己的工作模式（memory-notebook + 压缩前刷新），已被多轮压缩实践验证可行。
5. **与后端解耦**：记忆文档模式 = 每次决策都是自包含调用 → 不依赖任何后端的会话连续性 → 后端可以是"每次新会话"的 codex app-server，也可以是无状态 API。**这个选择直接消解了下面 §4 的大半难题。**

### 记忆文档的持久化

- 第一版：记忆文档作为 Player 私有状态存文件（每决策后覆写 + 决策序号后缀留档）。
- 演进方向（留档不立案）：记忆更新本身事件化（player-memory journal），与世界 journal 平行——"角色内心史"也可 replay 审计。
- **边界纪律**：记忆文档**绝不**进入世界状态/journal；台词（say）进 journal（别人可感知），独白只进记忆/戏剧记录。内外之分就是信息不对称的 Player 侧延伸。

### WP20/21 实验后的认知模型演进

单块显式记忆的总方向保持不变，但真场证明“每轮整体替换”把近期处境、长期承诺、猜想和关系置于同一个遗忘概率中。原拟的不可变 Scene Briefing 又会把“材料写了什么”误当成“角色必须相信什么”。因此后续拆成两次独立增强：

1. **WP20 叙事化长期材料**：`ReferenceMaterial(Id, Source, Content)` 每轮保存来源与原文，不保证真实；信任和解释仍由角色更新。见 Design Note 004。
2. **WP21 分块 MemoryBank**：schema 可配的文本分块各有维护策略；独立 maintainer 看冻结旧完整 bank，只能对自己返回 keep/replace，单块错误局部 fallback keep。FirstBoard 用 working/commitments/beliefs/relationships 四块，允许重叠。见 Design Note 005。

决策模型与记忆维护模型也从此解耦：混合场可由 DeepSeek 维护 DeepSeek/Luna 角色的私有认知。该维护器仍以角色身份工作，只读该角色可见输入；它不是全知摘要器。

---

## 4. 后端接入与成本策略

### 4.1 三通道分析

| 通道 | 上下文自由度 | 成本模型 | 合规风险 | 判定 |
|---|---|---|---|---|
| **codex app-server**（ChatGPT Pro 订阅 OAuth + GPT-5.6 luna） | 会话制；但**每决策开新 thread ≈ 重构式**（见 4.2） | 订阅平价，token 近似免费，约束在速率/额度窗口；24×7 可行，可多实例 | 官方产品的公开接口（JSONL over stdio 的 JSON-RPC，`codex app-server` 子命令），用订阅额度跑 agent 是产品设计内用法 | **主力通道** |
| OpenAI 兼容 API（deepseek-v4-flash 等） | 完全自由（messages 任意构造） | 按 token 付费；v4-flash 在帕累托前沿，一局 FirstBoard 量级估算 < ¥1 | 合规 | **对照/精控通道** |
| CLIProxyAPI 类订阅转 API | 完全自由 | 同订阅 | 灰色地带，封号风险自担 | **不内置支持**——它对外就是 OpenAI 兼容 API，我们的 HTTP adapter 天然可接；风险留给运行时配置者，代码不站队 |

### 4.2 关键论证：订阅制 + 每决策新会话 = 动态上下文自由度问题基本消解

用户顾虑"app-server 对动态构造上下文功能受限"。但结合 Q3 的裁决（显式记忆文档 = 每次决策自包含），**我们根本不需要服务端会话连续性**：

- 每个决策：`thread 新建 → 发送一条完整渲染的 prompt → 收 agentMessage → 丢弃 thread`。上下文由我们在 Player 层完全动态构造，app-server 只是运输载体。
- "重构式每次全量重发 token 贵"的经典缺点，被订阅制（token 近似免费）中和——**订阅制约束的是次数/速率，恰好我们的决策停机谓词已把调用次数压到最稀**。两边的短板互相抵消。
- 真正受限的是：无法控制 codex 前置的系统提示（行为污染风险）、无法用 temperature/seed、模型只能选订阅内的。前两者对戏剧角色扮演是否致命——**这是实验问题，不是设计问题**（WP14 连通后先跑小样验证）。
- 已查证（openai/codex repo）：app-server 为 JSONL-over-stdio 的 JSON-RPC，`initialize` 握手 + v2 thread/turn API 族；approval 类交互是 server→client 反向请求（我们场景下应配置为免审批/只读沙箱，角色扮演不需要它动文件）。协议细节 WP14 实现前再钉。

### 4.3 架构裁决：端口抽象，双 adapter

```
LlmPlayerDriver (IPlayerDriver 实现，认知循环所有者)
   │  渲染 prompt → 调后端 → 解析输出 → 记忆更新
   ▼
ILlmChatBackend (最小端口: prompt in → text out, async)
   ├── CodexAppServerBackend   (子进程 + stdio JSON-RPC，BCL: Process/StreamReader)
   └── OpenAiCompatBackend     (HTTP + SSE 可选，BCL: HttpClient/System.Text.Json)
```

- **零 NuGet 纪律不必破**（修正 002 此前预判）：stdio JSON-RPC 与 HTTP 调用全部 BCL 可覆盖（`System.Diagnostics.Process` / `System.Net.Http` / `System.Text.Json`）。codex 的 JSON-RPC framing 是 line-delimited JSON，自实现 ~百行级。
- src/Player.Llm 编译期零外部依赖 → **进主 slnx**，测试用假后端（录制/回放式）。真 LLM 调用不进自动测试（不确定、耗额度、需凭据），载体是手动 harness（WP15 的 demo console）。
- 后端选择是运行时配置，两 adapter 共存互为 fallback：app-server 若实测行为污染严重（codex 拒演角色），切 deepseek API 即一行配置。

### 4.4 远期留档（不立案）

- **分层调度（导演调度器）**：例行决策（在场无冲突的 wait/move）走便宜模型甚至 RandomDriver，戏剧关键时刻（冲突/发现/对话）走强模型。IPlayerDriver 组合器即可表达（TieredDriver）。等有真实成本压力再做。
- **多实例并行**：同刻屏障语义已支持；并行化 session 的 driver 调用属 Host 小改，等单实例质量验证后再做。

---

## 5. WP 切分草案（WP12–WP25）

| WP | 内容 | 依赖 | 验收 |
|---|---|---|---|
| WP12 | 本设计研究文档 | — | 用户对四个裁决（Q1/Q2/Q3/后端策略）异步纠偏 |
| WP13 | src/Player.Llm 骨架：PromptRenderer（含 KnownFacts diff）+ 输出解析器 + 认知循环（LlmPlayerDriver）+ 记忆文档读写（勘注：实际交付为内存态 + CurrentMemory 只读访问，落盘移至 WP15 harness）；全部纯逻辑 + 假后端单测 | WP12 | 主 slnx 测试绿；渲染/解析双向锁定；假后端跑通 FirstBoard 一局（脚本化 LLM 输出） |
| WP14 | 两个真后端 adapter：CodexAppServerBackend（stdio JSON-RPC，协议细节此时钉死）+ OpenAiCompatBackend | WP13 | 手动连通性验证各一次（真实一问一答）；协议交互有录制样本回归测试 |
| WP15 | Demo harness（console）：FirstBoard + LlmPlayerDriver 真跑一局，输出**戏剧记录**（journal 叙事 dump + 各角色内心独白轨迹）——顺带吸收此前"journal 可读性工具"穿插件 | WP14 | 真实完整一局跑通；戏剧记录人工可读；质量观感汇报给用户 |
| WP16 | 对话反应、权威动作回执、最小 `action.use` 与真模型舞台调度实验 | WP15 | 完成：两个后端均形成真实往返；局部反应闭环进入 Board 事件链 |
| WP17 | 谈判到世界后果：物化密信/交易筹码，优先复用 `give` 验证非原子交换 | WP16 | 真模型从议价推进到至少一次权威所有权转移；保留违约/背叛可能 |
| WP18 | 可验证议价：最小 `action.show`/`object.shown`，展示产生目标私有事实但不转移所有权 | WP17 | 真模型以 show 回答“先验货”，再自主选择 give、拒绝或欺诈 |
| WP19 | 目标化观察：复用 `action.observe(TargetObjectId)` 只检查自持对象，不新增 inspect/read 动词 | WP18 | 两个真后端均自主检查持有物并吸收对象特有私有事实 |
| WP20 | 叙事化长期材料：稳定保存来源/原文，不冻结角色信念 | WP19 | 完整替换 Memory 后材料仍可查，角色能明确怀疑或反转解释 |
| WP21 | 分块 MemoryBank：独立 maintainer、keep/replace 与局部 fallback | WP20 | 不同稳定性自然出现；承诺被明确完成/放弃而非静默丢失；混合真场跑通 |
| WP22 | 公共放置与检查：`action.put` 将持有物转为当前地点公共无主态，目标 observe 可检查本人物或同地公共物 | WP21 | 机制、落盘 replay 与模型理解均验证；保留被 take 风险和策略性拒绝空间 |
| WP23 | LLM runtime profiling 与 memory pipeline：逐调用测量 latency/token/cache/overlap；可选延迟提交维护 | WP22 | 超时局保留部分 profile；实测并反事实分析 blocking/pipelined 关键路径 |
| WP24 | Providence Phase 0 readiness：最小 Scenario Definition/Instance 与 run provenance | WP23 | 初始场景可复制/参数化，运行可关联 seed/model/effort/config hash；不建 DSL/在线干预 |
| WP25 | Passive Curator 轨迹诊断 MVP：只读分析 journal/turn/runtime trace，不干预 Player 或 World | WP24 | 能识别重复谈判/低信息区段、相遇密度、关系/所有权变化和高影响决策点 |

## 6. 开放问题

**需要用户裁决（愿景级）：**
1. 角色内心独白是否展示给"观众"（进戏剧记录）？我的倾向：**展示**——这是 DramaBoard 区别于一般 RPG 的观赏维度；但若愿景里观众也该被蒙在鼓里（悬疑性），则独白仅留审计用。
2. Prompt/记忆文档工作语言：我的倾向 **中文**（与项目语言一致、对国产模型友好；OpenAI 系双语能力无碍）。

**实验回答（WP14/15 出数据）：**
3. codex app-server 每决策新 thread 的延迟与额度消耗实测；能否配置成"无工具、纯问答"形态；GPT-5.6 luna（coding 特化后训练）演戏剧角色的质量。
4. deepseek-v4-flash 同 prompt 对照质量与实际成本。
