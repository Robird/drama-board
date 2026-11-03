# P2 生命周期与隔离证据

2026-11-03。实现冻结参照 `a063efa8b574fd619733a29bb752417518cf60e9`。本报告保留定向测试的原始证据及边界；后续最终全量复跑见末节和总报告。没有追加真实模型调用。

本批增加 **14 个唯一参数化测试实例**：DiagnosticFailure 3、HostEvidence 4、Temporal 4、Protocol 2、HumanWaiting 1。三个成功 TRX 共记录 17 次执行，其中 Temporal 的 3 个实例重复执行；不能写成 17 个独立案例。这里的“新增”仅指本批，不包括此前 runtime/lifecycle/input 等已有测试。

## 原始结果与计数

以下工件在忽略目录中保留，链接相对本报告回到仓库根四层。读取 TRX 的 `UnitTestResult` 与 `Counters` 得到：

| 工件 | 执行结果 | 本报告对应范围 |
| --- | --- | --- |
| [p2-evidence-2.trx](../../../../artifacts/playground-speculative-lookahead/p2-evidence/p2-evidence-2.trx) | 10 passed，0 failed | DiagnosticFailure 3、HostEvidence 4、Temporal 3 |
| [p2-temporal-protocol-2.trx](../../../../artifacts/playground-speculative-lookahead/p2-evidence/p2-temporal-protocol-2.trx) | 6 passed，0 failed | Temporal 4、Protocol 2；其中 Temporal 3 已在上一行出现 |
| [p2-human-wait.trx](../../../../artifacts/playground-speculative-lookahead/p2-evidence/p2-human-wait.trx) | 1 passed，0 failed | HumanWaiting 1 |

原始 TRX SHA256：

```text
p2-evidence-2.trx           2c50038f3f5f903f9ce2b4bdc80c13e4b0e8f5145037a25949c73e07200fb09d
p2-temporal-protocol-2.trx  deb4714ee2d8df77f7a9623411e47d1db58b11f5473523fbd56b9e1dbbf3ccf4
p2-human-wait.trx          138dedeb60cf1d5de177c9ff2d4d75e7fdffa2d9890e79f213cd1dcf1f0cba88
```

## 诊断失败与请求排空：3 例

源码：[PlaygroundLookaheadDiagnosticFailureTests](../../../../tests/Playground.Cli.Tests/PlaygroundLookaheadDiagnosticFailureTests.cs)。边界是 **coordinator + 内存 Kernel + 注入的 raw writer**，不是完整 durable Host 的 raw 故障集成测试。

- `Raw_flush_failure_with_two_requests_in_flight_retires_admission_and_disposal_drains_both`：A/B 两个 provider Task 均在途时让候选创建记录的 raw flush 抛出原始 IOException；Kernel 不提交、cursor 不变。再次服务被拒绝，物理调用数保持 2。Dispose 向两者发取消，但非协作 Task 未结束时清理仍等待；分别完成两 Task 后才结束，原始诊断错误在清理 AggregateException 中仍可辨认。该例同时核对逻辑 RequestId 与物理 ID 的关联及诊断中不含模拟敏感异常文本。
- `Failure_evidence_keeps_transport_facts_and_only_local_rejection_proves_no_remote_outcome(rejected: false/true)`：两例分别覆盖 transport exception 和本地 request rejection。transport 保留结构化 Failure/RetryAfter、远端结果未知；本地拒绝才记录已知没有远端结果。两者均保留原始异常关联、不泄露模拟敏感文本，并计入一次实际尝试。

这些结果证明指定故障点的准入退休和任务观察合同，不证明所有 raw I/O 故障时机、进程强杀后的远端结果或完整 Host 清理组合已经穷尽。

## 实际 durable Host 的 history、连接与主日志：4 例

源码：[PlaygroundLookaheadHostEvidenceTests](../../../../tests/Playground.Cli.Tests/PlaygroundLookaheadHostEvidenceTests.cs)。这些测试使用磁盘存档和真实 Host 路由，provider 为离线 fixture。

- `Discarded_candidate_response_reasoning_and_proposed_operation_never_enter_durable_history`：声连地点中的 A 实际大喊使预计 B 请求失效。候选 reasoning 与候选 handoff 仅存在于 raw 物理响应；普通 host records、B 的角色 history、保存后的 B DraftHandoff/完整渲染均不含候选标记，候选操作未进入实际接收路径。
- `Adopted_response_links_actual_logical_request_to_one_physical_usage_record`：B 的实际逻辑 RequestId 通过 actual-delivery 关联到已采用 PhysicalId；host 有该逻辑请求与响应，物理 request/completed 各只有一份。两个真实物理请求的 fixture OutputTokens 各为 7，按 physical-completed 汇总为 14，run-summary 的 PhysicalCalls 为 2。逻辑响应中携带的 usage 是交付证据，不能再加到物理总账；本例不宣称代码已提供通用费用汇总器。
- `Different_connection_is_not_resolved_speculatively_but_becomes_ready_after_actual_use`：B 首次使用不同连接时先记录 binding-not-ready，B 的第一次实际请求仍是 demand；实际解析连接后，后续可以在该连接创建候选。fixture 让 B 首次实际大喊改变 A 的预计输入，避免两个角色永远 wait 1 导致预取方向固定，仍严格验证后续 other 连接的 candidate 物理请求。
- `Host_response_flush_failure_stops_admission_and_observes_already_started_candidate`：通过 journalFactory 注入普通 model-response 的主日志 flush 故障。实际 Host 停止，无 operation-received；两个已启动物理请求均完成，候选被 drained，没有第三次请求，存档无 pending E。该例的 provider 同步完成，非协作在途排空压力来自上一节的 raw 故障例，不能混称同一端到端证明。

## 移动、时间与目标身份：4 例

源码：[PlaygroundLookaheadTemporalTests](../../../../tests/Playground.Cli.Tests/PlaygroundLookaheadTemporalTests.cs)。本组使用真实规则、独立内存历史与 coordinator；不把内存路径描述成完整磁盘 Host 验收。

- `Scratch_continue_advances_real_arrival_without_changing_source_spatial_time_or_weather_random_address`：预测 B continue，scratch 按真实移动规则推进 A 于 11 点抵达 stream，预计材料不再显示 Journey；来源 world/cursor/事件数、移动与等待对象、天气 seed/sequence/nextDue 等保持不变。本例证明天气随机地址未被改变，不证明本例执行了一个天气抽样事件。
- `Sound_wakes_same_target_before_predicted_sunset_and_invalidates_time_and_shared_light_material`：预计 A 在 18 点夜间服务，B 实际大喊使同 context/operation sequence 的 A 在 17 点白天提前服务。完整输入与时间身份失配，旧响应废弃，提交 fresh-target，未执行 stale-target。
- `Retained_candidate_is_rejected_after_real_movement_when_target_is_replaced_or_reopens(serviceActor: "a", hour: 11)` 与 `(serviceActor: "b", hour: 12)`：两例刻意保留原 B@10 候选，期间使用**另一个脚本控制器驱动真实 Kernel**移动 B，再交回原 coordinator。前者交给不同目标 A，B 尚在途中；后者交给已到 ridge 且 context sequence 更新的 B。两者均拒绝旧候选并校验实际新操作成功。

最后两例是 controlled stale-candidate 压力实验，证明延后检查时的身份/完整输入拒绝能力。正常 Host 会在更早的真实请求边界检查候选；本组不能作为“自然 Host 会把该候选保留到移动完成”的证据。

## 采用后的协议纠正：2 例

源码：[PlaygroundLookaheadProtocolTests](../../../../tests/Playground.Cli.Tests/PlaygroundLookaheadProtocolTests.cs)。`Adopted_invalid_act_is_corrected_only_in_actual_loop_and_does_not_start_another_prefetch(feedbackEnabled: false/true)` 使用实际 Host 的内存历史入口。

候选先返回参数 `command: 42` 的协议无效 act；A 被阻塞时，没有操作或普通 model-response 交付。真实 B 采用后才进入普通协议校验与纠正，随后有效 wait 1 提交。无 feedback 模式的纠正在 Shared 中，有 feedback 模式的纠正在 Tail 中，后者没有保存反馈记录。两例均只有一次预取、共 3 个物理请求，并逐一关联逻辑 dispatch/delivery 和实际 request ID；B 的纠正为第二工具轮，不启动第二次预取。

## Human 实际等待：1 例

源码：[PlaygroundLookaheadHumanWaitingTests](../../../../tests/Playground.Cli.Tests/PlaygroundLookaheadHumanWaitingTests.cs)。`Human_inserted_before_candidate_blocks_input_without_delivering_candidate_feedback_or_mutating_B` 使用实际 Host 的内存历史入口与可阻塞 TextReader。

预计下一目标是 B@11，但 A 实际大喊先唤醒 Human C@10。确认宿主正在等输入且尚未 EOF 后，让 B 候选完成并返回 feedback 工具；此时 world/cursor/事件数和 B interaction/activity 保持不变，没有 feedback 保存、B 普通响应或候选采用，也没有新请求。随后释放 EOF，Host 正常停止并 drained 候选，仅 A 产生实际操作。该例覆盖真实等待后 EOF，不等同实际人类体验验收，也不证明任意 Human 输入后续轨迹。

## 失败记录与剩余范围

[p2-evidence.trx](../../../../artifacts/playground-speculative-lookahead/p2-evidence/p2-evidence.trx) 保留原始 8 passed、2 failed。失败分别来自混合连接 fixture 的固定预取方向，以及测试对 VisibleValue 便利属性的反射序列化；修正后由 p2-evidence-2.trx 重跑对应集合。失败工件没有覆盖或删除，不能把成功重跑反写为初次即通过。

本报告是 P2 的补充证据，不替代[原型验收](runtime-prototype.md)和最终 results/summary.md。正式性能矩阵与在线接线由各自工件验收。最终 `p4-engineering/` 已运行 Release/warnaserror/Schema Verify 和完整 solution 测试：1583/1583，通过的 CLI 662 包含本批 14 例；计数与每份 TRX 哈希见该目录 summary.json。完整 G1–G7 裁决见[总报告](summary.md)。
