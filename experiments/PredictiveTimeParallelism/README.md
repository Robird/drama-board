# Synthetic predictive-time harness

本工程为独立、无网络、无产品项目引用的 .NET 10 合成实验。它不实现 Playground 接入，不证明产品控制器或任意玩法可以并行。按仓库 `global.json` 运行；所有 dotnet 操作由研究主线程串行执行。

```powershell
dotnet run --project experiments/PredictiveTimeParallelism -- --self-check
dotnet run --project experiments/PredictiveTimeParallelism -c Release -- --scenario independent --strategy speculative --backend independent --actors 8 --rounds 2 --candidate-limit 4 --seed 17 --latency-ms 20 --output artifacts/predictive-time-parallelism/example/result.json
dotnet run --project experiments/PredictiveTimeParallelism -c Release -- --matrix --actors 8 --rounds 2 --candidate-limit 4 --repeats 5 --latency-ms 20 --output artifacts/predictive-time-parallelism/example/matrix.json
dotnet run --project experiments/PredictiveTimeParallelism -- --influence-check
```

CLI 无隐式联网。未知参数、无效场景、非法容量等返回退出码 1。`--output` 可选，指定后覆盖该结果文件；stdout 始终输出同一 JSON。调用前为每次实验选新目录，并在外层另存版本、环境、manifest 与文件哈希；本工程输出不是完整来源 manifest。

七种规则场景：`independent`（独立角色）、`shared-resource`（所有操作改变共享观察）、`scarce-resource`（仅一份库存，取一份或等待）、`new-message`（操作即时传递给下一角色）、`zero-chain`（即时闭包影响所有角色）、`delayed-message`（延后三个逻辑边界送达）、`future-time`（目标边界时间进入输入）。每轮按角色编号服务，轮间依次推进；同刻角色始终遵守编号顺序。每次实际提交含完整输入、操作身份、响应及世界状态。资源数量与完整状态即使不在特定角色观察中，仍进入串行/候选轨迹对照。

`scarce-resource` 的权威 Apply 拒绝负数量、超库存和一次多取；fixture 按输入中的剩余库存决定取 1 或等待 0。两角色最小例中，旧的第二角色候选被丢弃，fresh 请求等待，库存加所有持有量始终为 1。它是有限竞争例，和旧 `shared-resource` 的充裕库存观察变化分开。

`serial` 按实际轮次请求；`safe` 仅在 `independent`/`future-time` 的已知独立规则中预发其他角色，其余退化到需求请求；`speculative` 固定窗口尝试预发，实际采用前逐字比较完整结构化请求编码。每角色最多一项未提交候选，不预测该角色下一轮。`--candidate-limit` 同时限制候选窗口和本地候选总量，不是全程调用次数预算；该工程全部为本地替身，无付费调用。

`--total-call-limit` 独立限定每次 Run 的累计请求预留数（默认 1024）。唯一协调者在进入队列之前同步预留；超限立即停止并取消/排空已有候选，退出码 1，不重试。排队后取消也不返还额度，因此这是 actual invoked 的保守上限。输出 Reservations、UninvokedReservations 和 Invoked 分列；invoked 仅在取得后端槽位且通过取消检查后计入。矩阵对每个策略样本分别应用该上限，外层在线总账是另一个独立合同。失败检查可运行 `--total-call-limit 1 --candidate-limit 4`。

请求包含 branch、控制代次、wake 代次、模拟时刻、角色/操作身份、协议/模型/工具/选项、共享规则身份、opaque Tail 内容及 Origin、局部数值、共享资源、消息和 seed。哈希仅供诊断，采用依据为完整编码相等。失配候选在当前实际输入下重新请求；废弃响应不会变成操作、消息或经历。候选工作者只接受冻结值，不持有世界对象；世界由单一协调者串行提交。

三个延迟后端：`independent` 独立等待，`slots` 通过 `--slots` 限制服务槽位，`shared` 固定一个吞吐服务站，所有请求争用同一容量（不模拟精细 token 级流量）。基础耗时及响应均按完整输入/seed 的 SHA256 派生，不随启动顺序取共享随机数。真实 timer 与 OS 调度仍会影响墙钟样本。取消前进入实际服务的请求记录 `canceled-remote-unknown`，未进入的记录 `canceled-before-invoke`；这是替身对远端语义的保守标记，不声称真实请求取消后未计算。

JSON 保留每个请求 queued/invoked/completed、候选 admitted/adopted/discarded、实际 committed 时间线。source/target 为合成逻辑边界编号；Sequence 仅为诊断序号。`WorkMilliseconds` 计至预定提交全部完成，`DrainMilliseconds` 单列停止后的排空时间；验证耗时与当前线程分配字节单列，未包含完整日志/估计器分项开销。usage/价格不适用于本地替身，未伪造为真实 provider 零 usage。

`--matrix` 在七规则 × 三后端内运行三策略配对样本，同一重复使用同一 seed（重复间递增）。每个规则/后端组合先让三个策略按相同配置分别预热，Warmups 单独保留、不计入 Results。测量顺序随重复轮换（serial/safe/speculative，safe/speculative/serial，speculative/serial/safe），Orders 记录实际顺序；并非随机顺序，无法完全消除长期机器漂移。各策略必须具有相同轨迹哈希，否则立即失败。当前输出保留原始样本，不自动宣称显著性或 p95，也不含人为压缩后的最佳样本。全矩阵默认五次重复，成本可用参数预先调整。

`AdoptedCandidates` 是兼容首轮结果的全部候选采用数，含 source=target 的即时需求，不能直接当成预取命中数。PrefetchedCandidates 仅计 source&lt;target 的提前候选；AdoptedPrefetchedCandidates 仅计其中实际采用者；ImmediateCandidates 记录即时创建者。DecisionTimings 逐次记录实际可服务边界开始到响应可用的墙钟等待、候选来源及提前量，失配重请求等待计入同次样本。取消或失败的未完成决策不伪造等待样本，其过程留在 Timeline。

Run 预算耗尽、取消或异常会先结清所有本地候选，再以 RunFailedException.Snapshot 携带失败时的完整原始 Timeline、计数、已提交前缀、Outcome/ErrorType/ErrorMessage 与 RemainingCandidates。CLI 同样写入 `--output` 并返回 1。自检查直接验证预算及取消 snapshot 的候选清零、调用结算和无提交；输出目标本身不可写时 stderr 另报写入失败，保留原始运行错误。矩阵若中途失败，目前 CLI 保存失败子运行 snapshot；已完成其他样本不在该失败 snapshot 中，完整成功矩阵才包含所有样本。

`--self-check` 覆盖七场景的串行对照、确有并发、失配废弃、17 类完整输入变化、容量/调用结算、取消传播。停止时请求本地排空后才返回；候选不持久化、不恢复、不重试。正常返回必须没有剩余候选。进程硬杀没有恢复协议，外层工件应标 unknown。

限制：上述人工场景的安全判断是人工规则白名单，不是通用影响估计器；`Influence/` 提供独立估计与枚举模块，下面的 `Causal/` 才将估计接入调度。这些场景没有模拟动态移动/新拓扑、feedback 工具循环、持久化故障、真实连接 Dispose、原生模型响应失败或真实恢复。[实际应用入口](../PlaygroundParallelism/README.md)的接入和在线证据单独交付，不能由本工程自检查代替。

`--cancel-mode cooperative` 是旧默认行为：已开始的本地 Task.Delay 可取消并释放槽位，Canceled 计数表示这种替身取消，不能推广为真实远端已停止。`--cancel-mode noncooperative` 中，排队请求仍可取消，但取得槽位并开始的服务不再响应 host token，继续计算到原定结束/失败并占用容量。host 只停止等待/采用，所有真实开始的服务在 shutdown 后完整排空，之后才产出正常或失败 snapshot。请求次数不因 host 取消或候选废弃返还，不伪造 provider usage/价格。

过期候选在本地标 candidate-abandoned，异步观察物理结果，不在实际需求前主动等待它结束；新的需求仍可能被真实占用的槽位阻塞。独立后端现在也有 `--candidate-limit` 个物理服务槽位，避免后台过期工作形成无界物理并发；slots/shared 继续使用各自容量。请求队列受累计 TotalCallLimit 保守约束。候选数量与物理服务数量是不同指标。AbandonedCandidates 记录本地放弃；Completed/PhysicalFaulted/Canceled 记录物理服务结果，全部 invoked 必须最终满足 `Invoked = Completed + PhysicalFaulted + Canceled`；RemainingPhysical 和 RemainingCandidates 归零后才交付 snapshot。

Services 按 reservation 记录完整输入哈希、实际服务开始/结束、host 取消时刻和物理结果；ServiceMilliseconds 为全部服务时间之和，ServiceAfterHostCancelMilliseconds 仅累计开始服务后收到 host 取消至物理结束的耗时。它不是墙钟耗时或价格，也不包含取消前已完成但最终废弃的计算，需结合废弃时间线统计总浪费。各 ServiceRecord 的时间相对本次 Backend 建立，Timeline 相对 Run 开始，两者相差初始化开销，不能直接混用时间原点。总 DrainMilliseconds 包含有效工作结束/失败后仍需等待的物理服务。后台 stale-failure-isolated 有独立计数 StaleFailures，不替换原始主错误。

确定性故障 `--fault-mode stale-shared` 仅令完整输入中规则为 shared-resource、actor=1 且 Resource=10000 的请求失败，并将其服务延长四倍；它不检查请求到达序号或策略。串行实际 actor1 通常已经观察到资源变化，所以不匹配该故障；投机输入过期后即使物理失败也只记诊断并继续正确需求。`--fault-mode actor-zero` 对 actor0/wake0 的实际输入失败，采用输入匹配时立即失败，不隐式重试。`none` 为默认值。所有实际故障与 host 取消均保留失败 snapshot。

```powershell
# 过期物理故障被隔离，实际轨迹仍与serial相同；对照时只改strategy
dotnet run --project experiments/PredictiveTimeParallelism -- --scenario shared-resource --actors 4 --rounds 1 --candidate-limit 4 --latency-ms 20 --cancel-mode noncooperative --fault-mode stale-shared --output artifacts/predictive-time-parallelism/new-stale/result.json
# 实际匹配请求失败，退出1并保存完整失败snapshot
dotnet run --project experiments/PredictiveTimeParallelism -- --actors 4 --rounds 1 --cancel-mode noncooperative --fault-mode actor-zero --output artifacts/predictive-time-parallelism/new-matching-failure/result.json
# 自检查包含host取消后服务继续占槽/完成、真实drain与计数结算
dotnet run --project experiments/PredictiveTimeParallelism -- --self-check
```

这些仍是可控服务替身，不证明真实端点的取消、计费、排队公平或吞吐。host 取消通过 RunAsync 的 CancellationToken 测试，CLI 当前没有额外取消定时参数。`delayed-message` 的“三步延迟”仍按提交逻辑边界而非 ModelTime 计量，不能当成有限模拟时间传播窗口的证据。

## 模型时间窗口与独立核验

`--model-time-check` 运行独立 `Causal/ModelTimeWindows.cs`：模型时间事件队列、同 tick 机械事件优先、已排队效果的 scratch 投影、未决决定的影响种子，以及粗闭包/延迟下界两种估计驱动的安全调度。六种规则×两种唤醒形状×四种策略，五个配对 seed；完整输入和事实逐条件对照。动态拓扑窗口和未声明机制拒绝安全证书，目标时刻恰好到达同样拒绝。

```powershell
dotnet run --project experiments/PredictiveTimeParallelism -c Release --no-build -- --model-time-check --output artifacts/predictive-time-parallelism/new-causal/result.json
python experiments/PredictiveTimeParallelism/Verify-ModelTime.py artifacts/predictive-time-parallelism/new-causal/result.json --output artifacts/predictive-time-parallelism/new-causal/replay.json
```

Python 核验器独立重放实际输入、事实、响应和调用区间，并直接比较四策略完整轨迹；输入缺失或篡改返回非零。支持的机制、实测与证明边界见[研究报告](../../docs/research/predictive-time-parallelism/results/model-time-windows.md)。固定唤醒与加法传播小模型的正确性不证明任意游戏安全。

## 生命周期与规模切片

`--lifecycle-check` 在有限队列实际执行提前、延后、合并、取消、死亡、重新唤醒及分支/绑定变化；交叉 provider 先/后完成与两种取消行为，检查 36 对完整输入/事实/提交前缀，另有 unknown 退化。冷开例只是这个小模型的队列 JSON 重建；真实磁盘恢复由 Playground 测试另证。

`--scale-check` 固定 2/8/32 角色、两轮、独立/共享资源两场景、独立/单槽服务两后端、三策略，候选上限 min(4, actorCount)，不可协作取消，5ms 基础延迟。各条件三策略预热后，五个配对 seed 轮换顺序，共 180 个测量运行、36 个预热；保留完整原始结果、吞吐和验证成本所需字段。它不会扩大实际 CLI 的人数。

```powershell
dotnet run --project experiments/PredictiveTimeParallelism -c Release --no-build -- --lifecycle-check --output artifacts/predictive-time-parallelism/new-lifecycle/result.json
dotnet run --project experiments/PredictiveTimeParallelism -c Release --no-build -- --scale-check --output artifacts/predictive-time-parallelism/new-scale/result.json
```
