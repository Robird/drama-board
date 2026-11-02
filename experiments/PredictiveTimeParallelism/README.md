# Synthetic predictive-time harness

本工程为独立、无网络、无产品项目引用的 .NET 10 合成实验。它不实现 Playground 接入，不证明产品控制器或任意玩法可以并行。按仓库 `global.json` 运行；所有 dotnet 操作由研究主线程串行执行。

```powershell
dotnet run --project experiments/PredictiveTimeParallelism -- --self-check
dotnet run --project experiments/PredictiveTimeParallelism -c Release -- --scenario independent --strategy speculative --backend independent --actors 8 --rounds 2 --candidate-limit 4 --seed 17 --latency-ms 20 --output artifacts/predictive-time-parallelism/example/result.json
dotnet run --project experiments/PredictiveTimeParallelism -c Release -- --matrix --actors 8 --rounds 2 --candidate-limit 4 --repeats 5 --latency-ms 20 --output artifacts/predictive-time-parallelism/example/matrix.json
dotnet run --project experiments/PredictiveTimeParallelism -- --influence-check
```

CLI 无隐式联网。未知参数、无效场景、非法容量等返回退出码 1。`--output` 可选，指定后覆盖该结果文件；stdout 始终输出同一 JSON。调用前为每次实验选新目录，并在外层另存版本、环境、manifest 与文件哈希；本工程输出不是完整来源 manifest。

六种规则场景：`independent`（独立角色）、`shared-resource`（所有操作改变共享观察）、`new-message`（操作即时传递给下一角色）、`zero-chain`（即时闭包影响所有角色）、`delayed-message`（延后三个逻辑边界送达）、`future-time`（目标边界时间进入输入）。每轮按角色编号服务，轮间依次推进；同刻角色始终遵守编号顺序。每次实际提交含完整输入、操作身份、响应及世界状态。资源数量与完整状态即使不在特定角色观察中，仍进入串行/候选轨迹对照。

`serial` 按实际轮次请求；`safe` 仅在 `independent`/`future-time` 的已知独立规则中预发其他角色，其余退化到需求请求；`speculative` 固定窗口尝试预发，实际采用前逐字比较完整结构化请求编码。每角色最多一项未提交候选，不预测该角色下一轮。`--candidate-limit` 同时限制候选窗口和本地候选总量，不是全程调用次数预算；该工程全部为本地替身，无付费调用。

`--total-call-limit` 独立限定每次 Run 的累计请求预留数（默认 1024）。唯一协调者在进入队列之前同步预留；超限立即停止并取消/排空已有候选，退出码 1，不重试。排队后取消也不返还额度，因此这是 actual invoked 的保守上限。输出 Reservations、UninvokedReservations 和 Invoked 分列；invoked 仅在取得后端槽位且通过取消检查后计入。矩阵对每个策略样本分别应用该上限，外层在线总账是另一个独立合同。失败检查可运行 `--total-call-limit 1 --candidate-limit 4`。

请求包含 branch、控制代次、wake 代次、模拟时刻、角色/操作身份、协议/模型/工具/选项、共享规则身份、opaque Tail 内容及 Origin、局部数值、共享资源、消息和 seed。哈希仅供诊断，采用依据为完整编码相等。失配候选在当前实际输入下重新请求；废弃响应不会变成操作、消息或经历。候选工作者只接受冻结值，不持有世界对象；世界由单一协调者串行提交。

三个延迟后端：`independent` 独立等待，`slots` 通过 `--slots` 限制服务槽位，`shared` 固定一个吞吐服务站，所有请求争用同一容量（不模拟精细 token 级流量）。基础耗时及响应均按完整输入/seed 的 SHA256 派生，不随启动顺序取共享随机数。真实 timer 与 OS 调度仍会影响墙钟样本。取消前进入实际服务的请求记录 `canceled-remote-unknown`，未进入的记录 `canceled-before-invoke`；这是替身对远端语义的保守标记，不声称真实请求取消后未计算。

JSON 保留每个请求 queued/invoked/completed、候选 admitted/adopted/discarded、实际 committed 时间线。source/target 为合成逻辑边界编号；Sequence 仅为诊断序号。`WorkMilliseconds` 计至预定提交全部完成，`DrainMilliseconds` 单列停止后的排空时间；验证耗时与当前线程分配字节单列，未包含完整日志/估计器分项开销。usage/价格不适用于本地替身，未伪造为真实 provider 零 usage。

`--matrix` 在六规则 × 三后端内运行三策略配对样本，同一重复使用同一 seed（重复间递增）。每个规则/后端组合先让三个策略按相同配置分别预热，Warmups 单独保留、不计入 Results。测量顺序随重复轮换（serial/safe/speculative，safe/speculative/serial，speculative/serial/safe），Orders 记录实际顺序；并非随机顺序，无法完全消除长期机器漂移。各策略必须具有相同轨迹哈希，否则立即失败。当前输出保留原始样本，不自动宣称显著性或 p95，也不含人为压缩后的最佳样本。全矩阵默认五次重复，成本可用参数预先调整。

`AdoptedCandidates` 是兼容首轮结果的全部候选采用数，含 source=target 的即时需求，不能直接当成预取命中数。PrefetchedCandidates 仅计 source&lt;target 的提前候选；AdoptedPrefetchedCandidates 仅计其中实际采用者；ImmediateCandidates 记录即时创建者。DecisionTimings 逐次记录实际可服务边界开始到响应可用的墙钟等待、候选来源及提前量，失配重请求等待计入同次样本。取消或失败的未完成决策不伪造等待样本，其过程留在 Timeline。

Run 预算耗尽、取消或异常会先结清所有本地候选，再以 RunFailedException.Snapshot 携带失败时的完整原始 Timeline、计数、已提交前缀、Outcome/ErrorType/ErrorMessage 与 RemainingCandidates。CLI 同样写入 `--output` 并返回 1。自检查直接验证预算及取消 snapshot 的候选清零、调用结算和无提交；输出目标本身不可写时 stderr 另报写入失败，保留原始运行错误。矩阵若中途失败，目前 CLI 保存失败子运行 snapshot；已完成其他样本不在该失败 snapshot 中，完整成功矩阵才包含所有样本。

`--self-check` 覆盖六场景的串行对照、确有并发、失配废弃、17 类完整输入变化、容量/调用结算、取消传播。停止时请求本地排空后才返回；候选不持久化、不恢复、不重试。正常返回必须没有剩余候选。进程硬杀没有恢复协议，外层工件应标 unknown。

限制：这里的安全判断是对六种人工规则的白名单，不是通用影响估计器；`Influence/` 提供另一独立研究模块。场景没有模拟动态移动/新拓扑、feedback 工具循环、持久化故障、真实连接 Dispose、原生模型响应失败或真实恢复。相关产品接入和在线证据必须单独交付，不能由本工程自检查代替。

当前本地 Task.Delay 取消后立即释放服务槽位，因此后端只刻画可取消的合成计算。它没有模拟“远端已接受且继续不可撤销计算/计费”；canceled-remote-unknown 是语义提醒，不能据此估真实浪费费用或取消后的实际后端负载。`delayed-message` 的“三步延迟”按提交逻辑边界而非 ModelTime 计量；不能当成有限模拟时间传播窗口的证据。这两个机制需要另轮实验。
