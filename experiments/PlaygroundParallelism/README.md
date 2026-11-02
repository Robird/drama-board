# 真实 Playground 请求 gate 实验

该独立入口使用普通 `LlmCharacterController`、真实 CLI renderer/Guidance、真实 Kernel、原 parser/校验与 DurableGraph E/S 存档，不改变默认 CLI。首版从无 Context 的双角色同刻初态出发，每个角色首次成功世界操作后结束；拒绝不计完成，另有转换上限。模型可能不遵循 handoff 引导，在线运行按原规则处理并记录实际工作量。

预计侧从同一来源 S 独立调用真实 OpenContext → Fold → Complete → Validate，scratch 不写历史。gate 捕获普通控制器构造的完整 CompletionRequest，发送一次物理请求，并始终扣住响应。预计控制器绝不消费结果，feedback 配置使用会抛错的 sentinel。实际 Kernel 开启真实 Context 后，普通实际控制器重新构造请求；只有实际分支/目标 Context、连接 ID/协议/模型/配置哈希及完整请求 JSON 一致才交付缓存。续调、feedback 和协议纠正统一走需求路径；实际反馈由既有 `FeedbackSession` 保存。

连接配置复用 `LlmConnectionConfiguration.Load` 和 `LlmConnectionPool`，两角色借用同一个 pool 中的默认连接。当前只使用默认 invocation options，连接有效设置由现有 factory 应用；没有输出 token 上限。连接文件仅记录 SHA256，不导出配置内容或凭据。共享 dispatcher 对全部物理调用限流、计数，候选采用不重复累计 usage。先结清所有候选和请求，再释放 pool；主错误和 cleanup 错误分别保留。

## 离线运行

在仓库根目录，用 global.json SDK 构建后运行，所有 dotnet 操作串行：

```powershell
dotnet build experiments/PlaygroundParallelism/PlaygroundParallelism.csproj -c Release
dotnet run --no-build --project experiments/PlaygroundParallelism -c Release -- --mode fixture --strategy candidate --output artifacts/predictive-time-parallelism/new-candidate
pwsh -File experiments/PlaygroundParallelism/Compare-Fixture.ps1 -Output artifacts/predictive-time-parallelism/new-paired-fixture
```

`--strategy serial|candidate`，`--delay-ms 250`，`--max-transitions 24`。fixture 完全离线，按完整请求的 SHA256 生成响应 ToolCallId，按 actor 返回 `handoff fixture-<actor>`，不使用全局调用序号寻址。宿主提交前分配 actor/context/operation 位置确定的 OperationRecordId；时间线保留生成的原始宿主 ID 与实验 ID 映射。实际请求中的所有已有身份都保留，不删字段制造命中。

`--workload playground|fixed-inputs` 默认 playground。fixed-inputs 只构造两条固定的极短无工具请求（分别带 actor a/b 标签，要求回复 OK），复用相同 pool、dispatcher 与预算；serial 顺序 await，candidate 使用受两槽限制的 Task.WhenAll。它不创建世界、不执行 feedback/工具、不做模型纠正或重试，所有调用结束后才释放连接。完整结果、usage、输入指纹与墙钟时间仍写入工件，Completed 与文本是否恰为 OK 分别记录。它只探测端点容量，不属于真实游戏同轨迹对照。

```powershell
dotnet run --no-build --project experiments/PlaygroundParallelism -c Release -- --mode fixture --workload fixed-inputs --strategy candidate --output artifacts/predictive-time-parallelism/new-fixed-input-fixture
```

对照脚本先保存计划与每对策略顺序，再运行一次预热对照与默认 5 次配对；配对按 serial→candidate、candidate→serial 轮换。完整比较 `semantic-trace.json`（所有实际请求原值、有序全部公开事实字段/逻辑时刻/cursor、最终材料），另检查物理调用数、采用数和确实达到 2 个在途。事实中的 `PlaygroundVisibleValue` 写出全部存储联合字段；不调用非数值分支会抛错的派生 Number getter。每次运行使用新进程/独立存档，进程启动耗时不在 runner 的 Stopwatch 内，runner 的配置、Git 核验、准备和存档开销在内；小样本不宣称统计显著。

故障注入验收也是完全离线，使用独立的合成 ledger 和抛错 fixture，不加载任何真实连接：

```powershell
dotnet run --no-build --project experiments/PlaygroundParallelism -c Release -- --self-check artifacts/predictive-time-parallelism/new-admission-self-check
```

它分别注入 invoked 刷盘失败、provider 原始错误加终态 ledger 刷盘失败、provider 原始错误加 evidence 刷盘失败；验证 writer 退役后不再发出物理请求、AggregateException 保留原错误、独立 summary 仍可写出。退役 writer 不再追加 run-finished 或重试尾部；它的失败只作为 cleanup 错误保留。该入口不授权建立真实研究新账本或在线调用。

## 在线批次必须由外层协调者预留

本入口不会自动建立研究总账或分配在线预算。协调者先核对历史总账和在途/未决记录，复制 `budget-grant.example.json` 到忽略工件目录，为每个策略分配不同的新 BatchId。首批建议每策略最多 4 次物理请求、2 个在途、90 秒宿主期限。然后在统一总账追加并持久刷盘一行：

```json
{"Kind":"batch-reserved","ResearchId":"predictive-time-parallelism","BatchId":"与grant一致","Grant":{"ResearchId":"predictive-time-parallelism","BatchId":"与grant一致","LedgerPath":"总账绝对路径","Strategy":"serial","MaxCalls":4,"MaxInFlight":2,"TimeoutSeconds":90}}
```

runner 要求既存总账与唯一未使用 reservation 完全匹配；在运行期间独占写者，追加 `run-started`、`request-invoked`、`request-finished`、`run-finished`。记录的 `Detail` 保存请求身份、结果与 usage。每次 provider 调用之前先持久刷盘 invoked；feedback、纠正、fallback、失败与取消都共用 cap。取消不返还次数，未知 usage 保持 null。批次出现 run-started 后即拒绝复用，即使零调用；崩溃后的未决调用须由协调者保守对账，不能删记录重跑。未使用预留由外层之后核销，runner 不自动释放。

若先执行端点探测再执行游戏，首批 8 次额度可拆成四个独立批次：fixed-inputs serial 2 / candidate 2，然后 playground serial 2 / candidate 2。fixed-inputs 在线入口强制 grant.MaxCalls=2；游戏遇到失配、feedback 或纠正需要第 3 次时按预算明确失败，由外层分析后另批授权，不偷偷重试。切换 workload 不会绕过统一总账或复用旧 BatchId。

```powershell
dotnet run --no-build --project experiments/PlaygroundParallelism -c Release -- --mode online --strategy serial --connections <现有连接文件> --budget-grant <已预留grant.json> --output artifacts/predictive-time-parallelism/new-online-serial
```

没有 grant、总账缺失、reservation 不匹配、同批次重跑、预算耗尽均明确失败，不静默转 fixture、不自行重试。凭据由现有连接 pool 在运行时读取。期限只通过宿主 CancellationToken，不在 transport 新增 timeout。不限制模型输出 token，也不能声称四次请求有精确金额保证。未经协调者本轮指令不得运行在线模式。

## 工件与证据边界

每个新 output 保存 manifest、timeline、真实 store、实际反馈文件（发生时）、最终材料、完整语义轨迹和 summary。manifest 含源 commit/dirty 状态/已跟踪 diff 哈希、所有本实验顶层源码文件 SHA256（含未跟踪文件）、实际程序集哈希、连接身份和配置哈希；复现实验还应由协调者保存集成补丁。时间线有来源 S、预测目标、真实采用边界、完整请求与原生响应、物理调用/usage/单调计时和实际世界提交。summary 分别记录有效工作结束、排空/释放完成瞬间、冷开及收尾后的总耗时。成功后先关闭写者，再独立核验每个实际请求边界和冷开最终材料，不能同时占用同一 store。

工件含完整请求、角色材料及原生 reasoning payload，默认输出到忽略的 `artifacts/predictive-time-parallelism/`；不要将这些批量材料直接提交。诊断文件是研究证据，不能替代世界 E/S 或驱动恢复。记录 world-event-planned 时尚未证明 E/S 发布，只有 world-state-committed 才表示 Step 正常完成；提交后的诊断 I/O 在 Kernel publication API 之外。

本入口目前仅比较一项同刻首次请求策略，fixture 后端只模拟独立延迟；有限并发槽与共享吞吐模型由另一小模型 harness 提供。它不覆盖所有 M1–M9、不提供安全窗口证明、不支持 Human、绑定热切换或跨时刻预测，也没有修改 pending 恢复算法。在线随机输出可能改变后续输入、命中和调用数，serial/candidate 只报告集成现象与条件化耗时，不断言同轨迹或公平加速比。代码存在不表示在线验证完成；C6 是否满足由后续实际工件及独立验收决定。
