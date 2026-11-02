# 真实 Playground gate 与首批在线验证

2026-11-03。本轮将测试内的请求 gate 提取为独立可运行 [PlaygroundParallelism](../../../../experiments/PlaygroundParallelism/README.md)，并完成真实模型的固定输入探测和双角色应用对照。默认 CLI 没有启用并行，产品的领域规则、串行提交和恢复算法均未改变。

## 采用边界与离线证据

预计侧使用普通 `LlmCharacterController` 生成真实请求，scratch 运行真实 OpenContext → Fold → Complete → Validate；来源 S 不变。预计控制器被 gate 挡住响应，因此不能生成操作、执行 feedback 或开始纠正续调。实际 Kernel 轮到角色时重新构造请求，只在分支/目标 Context、完整输入及连接身份匹配时交付候选；随后仍由普通控制器和领域规则处理。

原型复用真实配置 loader/pool、反馈保存器、Kernel、parser、领域校验和磁盘 E/S；采用前不会伪造实际决定边界。候选来源 S、预计目标和实际请求边界分别留证。完整请求与原生响应保留在忽略目录，诊断文件不参与恢复。完成后先关闭 writer，再独立核验实际决定边界与冷开人物材料。

`r3-playground-gate/fixture-pairs` 中，一次预热对照与五次测量配对全部通过：逐字比较完整请求、全部有序事实、cursor 和最终材料；每策略两次 provider 调用，candidate 两次采用、无失效、峰值在途 2。配对顺序轮换。最终代码又在新目录 `final-fixture` 运行一次预热和一次配对，通过相同检查。250ms fixture 只模拟独立服务，不代表真实端点吞吐。

这些运行包含独立进程的配置、来源 Git 核验和新 store 初始化；工作耗时不包含进程启动，但包含这些准备步骤。不能把整个进程级工作量都解释为 LLM 等待或纯机械规则成本。五对旧批的整段比值约 1.12–1.62，中位 1.16，只作为测量口径示例；实际重叠和轨迹一致性是更直接的验证。

## 预算与故障审查

所有 provider 请求，包括 fallback、feedback 续调和纠正，都通过一个 dispatcher 与统一上限。在线运行只能消费协调者已持久预留的 batch；请求前先刷盘，失败和取消不返还已调用次数，已开始的 batch 不可重用。缺少总账、未预留或损坏尾部均拒绝新调用。

独立审查曾发现 writer 写失败后仍可能准入其他请求；已修为首次失败永久退役，保留原 provider 异常和独立诊断错误，失败后不继续向同一 writer 追加。`final-fault-checks/summary.json` 的离线注入验证请求前账本失败、终态账本失败、终态证据失败，以及失败消耗预算、批次拒绝重用、缺预留和截断尾拒绝。此检查使用假的 provider，实际网络调用为零。它不模拟物理磁盘断电或任意文件系统故障。

## 首批真实调用

配置为本机 `http://127.0.0.1:8491/`，Responses 路径 `responses`，模型 `deepseek-v4.1-flash`，reasoning effort `low`，无需 API key。每个批次最多 2 次实际调用、同时最多 2 个在途、宿主期限 90 秒；无输出 token 上限、无自动重试。调用先固定输入，再进入应用。

| 工作负载 | 策略 | 实际调用 | 峰值在途 | 工作完成时间 | 候选采用/丢弃 |
| --- | --- | ---: | ---: | ---: | --- |
| 两个固定短请求 | serial | 2 | 1 | 4.272s | 不适用 |
| 同两个固定短请求 | candidate | 2 | 2 | 2.074s | 不适用 |
| 两角色首次成功操作 | serial | 2 | 1 | 8.429s | 不适用 |
| 同应用初态与停止条件 | candidate | 2 | 2 | 5.018s | 2 / 0 |

四个短请求都 Completed 且可见文本为 OK。两策略按 actor 的完整输入哈希一致；candidate 的请求区间实际重叠约 0.859s。单对样本有冷连接/缓存/输出长度等影响，不能据此估计稳定加速或后端内部推理调度方式。

应用运行从同一语义初态的新存档开始，关闭 needs/weather，两角色同地，提示只提交 handoff；这是固定短工作负载，不是自由游玩。实际四次请求都返回成功 handoff，按 actor 的完整输入哈希相同，普通控制器接收后仍由唯一 Kernel 串行发布。候选请求重叠约 1.626s，b 的 provider 先完成，但实际操作仍按 a → b 提交。每个运行分别通过真实边界回读和最终状态冷开检查。

两策略输出的 handoff 正文和原生身份不同，最终世界内容也不同。应用 candidate 的两次响应各报告 CacheReadInputTokens=4,224，serial 各为 0，存在明显缓存条件差异。在线证据支持真实接线、有效输入验证和请求重叠，**不构成同轨迹等价证明**；等价性来自离线固定响应对照。8.429s 与 5.018s 只报告条件化样本，不能把随机输出、缓存、启动准备和服务波动的差异全部归因于并行。

## 总账与工件

研究总账为 `artifacts/predictive-time-parallelism/online-budget.jsonl`：截至本报告实际调用 8 次、8 个终态、0 个未决请求。第一次固定串行尝试因实验连接 JSON 漏写 `completionSurfaceId`，在调用前配置校验失败；Calls=0，旧批次保留并核销其未调用预留，补齐字段后使用独立新 batch，没有复用或抹除失败记录。

八次 usage 合计已知输出 token 为 1,087；输入 token 分解未知，保持 null，不能报告总 token 或精确费用。没有真实失败/取消请求，也没有为本轮验证取消计费行为。仅针对本次端点、协议、模型及输入范围成立。

本轮目录 `artifacts/predictive-time-parallelism/r3-playground-gate/` 保留：

- `online-fixed-serial`：未发起请求的配置失败。
- `online-fixed-serial-v2` / `online-fixed-candidate`：固定输入的 manifest、原始请求/响应/usage 时间线及 summary。
- `online-game-serial` / `online-game-candidate`：实际应用工件、真实 store、完整语义轨迹、来源 S 与实际边界、冷开验证。
- `online-overview.json` / `provider-intervals.json`：主线程从原始数据提取的计数与区间，不能替代时间线。
- `fixture-pairs` / `final-fixture`、`final-fault-checks`、构建与测试记录，以及来源/工件哈希清单。

工程检查：两个实验入口 Release 构建通过，实际应用入口启用 Schema Verify 与 warn-as-error，零警告；CLI 全量 569 项测试通过，零跳过，TRX 在 `cli-suite`。来源与原始工件哈希见 `evidence-manifest.json`。独立审查直接复核四组在线时间线、真实边界、提交顺序和统一总账，没有仅依赖 summary 的通过标记。

运行参数和预留 JSON 格式见实验 README。后续在线运行必须先核对既存总账，再预留新的批次与新目录，不能直接复用本报告的 grant。

这为 C6 提供了真实应用和真实模型对照证据，但不自动完成 C5 的全部 M 场景或 C7 综合交付。跨时刻唤醒、运动、身体/天气投影、Human 共存，以及一般化的安全调度仍需要剩余实验。
