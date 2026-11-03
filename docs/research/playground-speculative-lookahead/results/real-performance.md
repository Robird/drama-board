# 真实模型与 Human Console 的实际性能观察

2026-11-03。本轮新增 **8 个生产 durable Host 运行、50 次真实 provider 调用**，均达到预声明的 8 个成功领域操作并正常停止。全部模型使用本机 `http://127.0.0.1:8491/responses`、`deepseek-v4.1-flash`、`reasoningEffort=low`，没有人为增加模型延迟、修改游戏提示或自动重试。

真实 Completed 响应的客户端延迟为 **1.506–10.759s，中位数 4.056s（46 次）**。在这样的实际延迟下，LLM/LLM 的两对 on 运行耗时分别少 14.8%、6.7%；脚本 Human/LLM 的两对分别少 34.2%、18.4%。这些是本批的**观察值**：自然响应、后续轨迹、模型时间跨度和部分角色工作量不同，不能称为同轨迹因果加速比，也不外推其他模型、端点或 reasoning 档位。

## 预声明与测量范围

工件根：[p5-real-performance](../../../../artifacts/playground-speculative-lookahead/p5-real-performance/)。[plan.json](../../../../artifacts/playground-speculative-lookahead/p5-real-performance/plan.json) 在执行前固定两种场景各两对、每 run 8 成功操作目标、最多 12 次物理请求、最多 2 个本地在途请求。每种场景顺序均为 pair1 off→on、pair2 on→off；没有在线 warmup 或挑选样本。

- 每 run 使用独立新 home/store，相同普通 living-world 定义与 camp/cabin 初始位置，保留需要、天气、经济规则。八个初始 Business SHA-256 均为 `14430188D0372248948AF8E85CE806E64A7DD327006CB59BCFACD561575C4D8F`。
- Human A 由脚本操作真实 `ConsoleCharacterController.ReadLineAsync` 输入入口，每次固定等待 5000ms，再交替输入 `wait 1` / `continue`。重复确认可能被拒绝，原样保留。它不是实际人类试玩，也不替代尚待真人完成的体验验收。
- “8 成功操作”包括零模拟时间操作，不表示 8 回合或相同游戏进展。每角色成功/拒绝、上下文与模型时刻完整保存在 `samples.json`、`operations.json` 和 Host journal。
- Host wall 从生产 `RunAsync` 入口到返回/异常捕获，包含领域推进、磁盘发布、Console、模型、lookahead 与清理排空；不含创建初态、来源归档以及事后导出/冷开检查。
- 物理延迟是 budget wrapper 写出 `request-invoked` 到 `request-finished/failed` 的单调时钟区间，包含客户端传输、provider 等待、序列化与 wrapper 记账/刷盘。它不是 provider 内部纯计算时间。各计时存在嵌套或并发，不能相加。

## 固定操作目标下的运行结果

每行均为 8 成功提交、Host exit 0。A/B 栏为成功操作数；拒绝均来自脚本 A 的多余 `continue`，B 与全部 LLM 场景均无拒绝。模型跨度为初态至最终已提交状态的小时数。

| 场景 / pair / 开关 | A/B 成功 | A 拒绝 | 需求/候选物理调用 | 采用/废弃/停机未采用 | 模型跨度 h | Host s |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| LLM / 1 / off | 6/2 | 0 | 8/0 | 0/0/0 | 4.00 | 35.466 |
| LLM / 1 / on | 6/2 | 0 | 6/6 | 2/4/0 | 2.25 | 30.206 |
| LLM / 2 / on | 6/2 | 0 | 6/2 | 2/0/0 | 1.75 | 33.683 |
| LLM / 2 / off | 6/2 | 0 | 8/0 | 0/0/0 | 2.50 | 36.101 |
| Human-script / 1 / off | 5/3 | 4 | 3/0 | 0/0/0 | 4.00 | 67.437 |
| Human-script / 1 / on | 4/4 | 3 | 1/4 | 3/0/1 | 3.00 | 44.393 |
| Human-script / 2 / on | 5/3 | 4 | 0/3 | 3/0/0 | 4.00 | 54.630 |
| Human-script / 2 / off | 5/3 | 4 | 3/0 | 0/0/0 | 4.00 | 66.925 |

LLM pair1 的 on 消耗 12 次请求，其中 4 个候选取消且未进入实际业务；Human pair1 on 另有一个完整 Completed 候选在停机时未采用。这 **5 次未交付的物理调用**全部计入预算及 usage 已知部分，不因“预测没有采用”返还。全批 35 个需求、15 个候选，10 个候选采用，45 个物理响应进入实际模型服务；64 个成功游戏操作由 45 个 LLM 操作与 19 个脚本 Human 操作组成。没有 feedback 调用，完整反馈位置与原始审计仍保留。

真实轨迹不同的直接例子：LLM pair1 off 首步 A 为 `go trail-1`，on 为 `go road-camp`；其后路径及采集/交易操作不同。Human pair1 的 A/B 成功比例也从 5/3 变成 4/4。因此不能用这张表声称严格固定模型工作量的吞吐提升；这里测量的是相同成功操作停止规则下的实际运行耗时。

## 正常响应延迟与实际角色服务

下表只统计 **Completed** 物理响应，排除 4 个取消结束区间。完整分布数组见 `samples.json` 的 `CompletedPhysicalLatencyMilliseconds`，取消区间单列在 `CancelledOrFailedPhysicalIntervalsMilliseconds`。

| 场景 / pair / 开关 | Completed 数 | 中位数 s | 最小–最大 s | A 实际服务：次数 / 中位数 s | B 实际服务：次数 / 中位数 s |
| --- | ---: | ---: | ---: | ---: | ---: |
| LLM / 1 / off | 8 | 3.964 | 1.785–4.142 | 6 / 4.087 | 2 / 2.701 |
| LLM / 1 / on | 8 | 3.582 | 1.684–6.586 | 6 / 2.671 | 2 / 2.806 |
| LLM / 2 / on | 8 | 4.467 | 1.755–6.059 | 6 / 4.702 | 2 / 2.982 |
| LLM / 2 / off | 8 | 3.308 | 1.506–5.758 | 6 / 4.139 | 2 / 3.643 |
| Human-script / 1 / off | 3 | 5.371 | 4.629–6.723 | 9 / 5.023 | 3 / 5.455 |
| Human-script / 1 / on | 5 | 5.092 | 2.729–8.612 | 7 / 5.031 | 4 / 0.150 |
| Human-script / 2 / on | 3 | 8.603 | 3.841–10.759 | 9 / 5.024 | 3 / 0.377 |
| Human-script / 2 / off | 3 | 5.626 | 1.777–6.209 | 9 / 5.035 | 3 / 6.327 |

实际角色服务计至控制器返回，包含其内部呈现、连接解析、模型协议等成本；Human A 另含准备 join，其次数包括拒绝。LLM 操作接收在该计时之后。on 的 B 服务可以很短，即使其真实物理响应花了数秒，因为请求已在 A 输入期间完成；这不是把 provider 服务缩短到 150ms。

## 请求重叠、提前覆盖与剩余等待

LLM on 的两 run 客户端物理请求区间重叠分别为 **17.851s、3.906s**，本地峰值 2；全部 off 为 0。Human on 的 provider 请求彼此没有重叠（峰值 1），但与真实 Console 等待重叠：

| Human 场景 | 输入等待次数 | 实测等待总和 s | 物理请求与输入等待交集 s |
| --- | ---: | ---: | ---: |
| pair1 off | 9 | 45.033 | 0 |
| pair1 on | 7 | 35.067 | 21.992 |
| pair2 on | 9 | 45.020 | 21.876 |
| pair2 off | 9 | 45.056 | 0 |

所有 10 个采用候选如下。物理序号为该 run 的 ordinal；“提前覆盖”是候选客户端区间在采用观察点之前已过的部分，上限为其完整区间；“剩余”是采用观察点至物理 wrapper 完成的非负区间。观察钩子在生产 raw 记录后写入，与物理记录使用同一 `Stopwatch` 时钟。

| run | 物理序号 | Completed 物理区间 s | 提前覆盖 s | 采用时剩余 s |
| --- | ---: | ---: | ---: | ---: |
| llm-pair1-on | 2 | 3.366 | 2.280 | 1.085 |
| llm-pair1-on | 12 | 6.586 | 5.588 | 0.997 |
| llm-pair2-on | 2 | 6.059 | 2.866 | 3.193 |
| llm-pair2-on | 4 | 1.755 | 1.755 | 0 |
| human-script-pair1-on | 1 | 4.092 | 4.092 | 0 |
| human-script-pair1-on | 2 | 5.092 | 5.092 | 0 |
| human-script-pair1-on | 3 | 8.612 | 8.612 | 0 |
| human-script-pair2-on | 1 | 3.841 | 3.841 | 0 |
| human-script-pair2-on | 2 | 10.759 | 10.501 | 0.258 |
| human-script-pair2-on | 3 | 8.603 | 8.603 | 0 |

部分 Human 候选横跨一次被拒绝的 `continue` 与下一次输入等待，所以覆盖可以超过单次 5s。全批提前覆盖合计 53.231s、采用时剩余区间合计 5.534s；这些嵌套区间不等于 Host 节省时间，不能从 wall 中机械扣减。请求区间重叠证明客户端并发；本批无法直接观察服务端调度，**不据此证明独立服务器推理容量**。

## 预算、未知结果与 usage

原账本追加 8 个新 batch，各 12 调用，共预留 96；实际本轮 50 invoked/50 本地结束、8 run-finished，无未决本地 Task。原有 16 次不计入本轮，也未被覆盖：旧账本字节前缀 SHA-256 仍为 `4dd3a1527970a4b8b214fe87991357f140e1e9965c7525920c4afd492706e71b`。剩余 46 份预留保持保留，本轮不再使用。

46 次有 Completed terminal，另 4 次是 LLM pair1 on 的 discarded candidate（physical 4/6/8/10），本地取消后记录 `remote-unknown`。**本地全部排空不代表这 4 次远端没有计算、没有费用或已停止**；没有重试、返还调用数或补 usage。Human pair1 on 的停机未采用候选是已完成的真实响应，usage 仍计入。

46 次已知输出 tokens 合计 **22,428**，已知 cache-read tokens 合计 **183,936**；对应另 4 次均未知。50 次 UncachedInputTokens 与 CacheCreationInputTokens 均为 null，不能解释为 0，也不据此估算完整输入量或费用。原始 `PromptCache` 观测保留于物理结果；不把逻辑 `model-response` 再加一次 usage。

## 来源与复核

运行来源为 `3122f4e97f8aab05b7f2ce3548d639ac8371bead` **加当时的 dirty 修改**，不是干净提交。[build-provenance.json](../../../../artifacts/playground-speculative-lookahead/p5-real-performance/build-provenance.json) 保存实际 DLL/依赖运行配置散列、tracked patch 散列及新增源文件散列；`source.patch` 保留运行前修改，各 run manifest 另保存当时源码/依赖状态。批次结束后分析脚本新增 Completed-only 与 Human overlap 汇总字段，没有改 C# 或重建 DLL。

[冷连接离线自检](../../../../artifacts/playground-speculative-lookahead/p5-real-performance-cold-self-check/summary.json) 以 fixture 验证四个生产 durable Host profile 均达到目标并排空；`cold-human-check.json` 额外证明首个 B 物理请求为 candidate，发生在首个 Human Console 等待开始之后、脚本输入提交之前。它与真实在线 50 次分开计数。

[evidence-verification.json](../../../../artifacts/playground-speculative-lookahead/p5-real-performance/evidence-verification.json) 重算全部八个 raw/Host journal 的原始 Payload 哈希链，核对 50 次物理调用与预算 input hash；45 个实际逻辑响应均完整比对有效输入、Blocks、Invocation、Termination、Usage、Failure、Errors。on 的 FullInput/dispatch/physical ordinal 逐项连接，不用动作猜中概率替代采用判断。所有 raw、请求/响应、操作、拒绝、反馈位置、初态、final 冷开快照与逐调用统计均保留。

最终账本 SHA-256：`05721ed2b948db481da46805ca2b065b2e990820b9d156a2432783030b63150a`。批次汇总见 [audit.json](../../../../artifacts/playground-speculative-lookahead/p5-real-performance/audit.json)、[samples.json](../../../../artifacts/playground-speculative-lookahead/p5-real-performance/samples.json)。


## Human 来源的功能变更与工程验收

现有 `--lookahead on` / home `lookahead: on` 同时支持当前角色 A 为 LLM 或 Human Console，默认仍关闭。下一目标 B 仍必须是 LLM；遇到下一位 Human 不跳过继续向后找。

[Host](../../../../src/Playground.Cli/PlaygroundHost.cs) 先启动实际 Human 输入，再由[协调器](../../../../src/Playground.Cli/PlaygroundLookaheadController.cs)后台准备预测请求。Human 来源允许解析尚未使用的 B 连接；原 LLM 来源保留只使用 ready binding 的限制。已有候选继续保留到真实需求比较，不因插入 Human 就覆盖；候选沿宿主 token 存活，仍执行原有完整 input/target/binding 验证和统一物理额度。

Human 输入结束、EOF、异常或取消时，先退休准备准入并等待准备任务退出，再允许真实 Kernel 发布。即使取消回调抛异常也会 join，保留原 Human 错误。不合作的冷连接 resolver 可能延迟 Human 提交或退出；这是最小串行实现的明确边界，未承诺任意认证可即时取消。未采用响应不执行反馈、不进入角色 history。另有内部只观察的 LookaheadEvidence 接缝，为实验提供同源单调时钟，未加入领域持久状态。

主线程实际验证（Windows、Release）：

- [HumanSourceTests](../../../../tests/Playground.Cli.Tests/PlaygroundLookaheadHumanSourceTests.cs)新增 8 例，覆盖首次 Human 冷连接采用、开关、立即 EOF、忽略取消的 resolver、输入改变、反馈隔离和取消回调异常；全部包含于 lookahead 组 **85/85** 通过。
- CLI 完整回归 **670/670** 通过，零失败、零跳过。TRX 为 `p5-human-implementation/lookahead.trx` 与 `cli-all.trx`；独立代理复核了新八例实际结果。
- 实验项目 Release 构建使用 `-warnaserror -p:DurableGraphSchemaHistoryMode=Verify`，零警告、零错误；实际调用前完成普通与加强冷连接四 profile 离线自检，旧短等待自检也保留。没有把 fixture 调用计作真实网络调用。

本轮改动限 Playground 宿主、测试和实验入口，没有修改世界规则、领域 Schema、跨仓 API，未合并主线或推送。先前 G1–G7 和固定服务时长的报告保留为独立历史证据，本轮不覆盖或重写它们。
