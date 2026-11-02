# M5：有限模型唤醒生命周期

2026-11-03。本工作包新增 `Lifecycle/WakeLifecycle.cs`，入口 `WakeLifecycle.RunAsync()`。主线程构建运行后，独立审查者又从新目录重跑最终源码，核对真实队列变化、72 个运行及 36 对轨迹比较；最终原始结果为 `artifacts/predictive-time-parallelism/r6-independent-review/wake-lifecycle.json`。这是有限模型结果，不是真实 Playground 生命周期验证。

假设：只为首个未决 wake 启动一个候选，实际服务者始终由权威事件队列决定；目标 wake 或运行身份改变后立即废弃旧候选，并保留采用前完整输入比较，则提前/延后/合并/取消等生命周期不会造成陈旧或重复提交。响应按完整输入寻址，废弃调用不移动后续 fixture。

初始 wake 在 tick 5，tick 2 发生机械事件并优先于同 tick 的 wake。advance 删除原目标并创建 tick 3 新目标；delay 改为 tick 8；merge 将 tick 5 和 tick 7 两个目标合为 tick 4 一次目标；cancel 删除目标；death 标记死亡并删除目标；cancel-and-rewake 在 tick 2 取消，再由 tick 3 的真实队列事件创建 tick 6 新目标。branch-cold-open 序列化并重建世界队列、递增 branch 代次，运行时候选不恢复；binding-generation 改变控制绑定代次。unchanged-control 保留原目标并应采用初始候选。

每种情况均运行 serial 与 bounded-speculation，并交叉两种受控服务完成顺序与两种取消行为。服务通过 TaskCompletionSource gate 确保候选已经进入等待：一组先完成再执行 tick 2 事件，一组在失效处理后才释放；协作服务响应 token，非协作服务忽略 token 但最终仍须排空。候选的 `discarded` 属于本地采用状态，调用的 `completed` 或 `cancelled-remote-unknown` 单独记录。取消不被解释为远端没有计算，不返还调用次数。

每对运行比较完整实际输入 hash、完整机械/提交事实 hash、完整 command/commit 数组和最终 cursor。取消与死亡必须没有需求调用和提交；提前/延后/合并/重新唤醒/身份变化必须恰好提交一次；只有 unchanged-control 可以采用原候选。所有运行结束时应无队列 wake、无未观察服务任务。入口按任一违反抛错，便于主线程验收。

返回 raw 包括逐项 Events、Candidates、Calls、Inputs、Commits、Cursor，合计九种生命周期 × 两顺序 × 两取消模式 × 两策略，36 对轨迹比较。实际结果 `Failures=0`：36 个投机运行中，四个 unchanged-control 采用原候选，其余 32 个废弃；全部调用无遗留 in-flight。另两个 unknown 串行/候选策略运行不启动候选而由实际需求继续。最终结果文件 SHA256 为 `83A397DAEBD6B216ED1194D360BCE1684340A5E8D67F114A7EDAF8E35F9AF1C6`。

完整输入在此模型包含 actor、wake 身份/目标 tick/代次、branch/binding 代次、alive、cursor 和工具/协议合同。它不是 Completion 请求、实际 Context 或真实模型材料。冷开仅是有限模型的序列化队列重建，没有 DurableGraph 存档或 pending 恢复主张。历史 `r4-lifecycle/lifecycle.json` 保留了已删除的无效局部 catch 微例字段 `StopErrorPreserved`，不计入任何清理证据；最终 r6 结果不再含该字段。真实 runner 的主错误与 Dispose 错误检查由[最终复核](final-review.md)中的六种 controller 组合提供，不与此有限模型混算。这里没有 Human、死亡玩法、运行中产品 binding 热切换、LLM 工具续轮或真实在线调用。

从仓库根目录使用新工件目录复现：

```powershell
dotnet build experiments/PredictiveTimeParallelism -c Release -m:1
dotnet run --project experiments/PredictiveTimeParallelism -c Release --no-build -- --lifecycle-check --output artifacts/predictive-time-parallelism/new-lifecycle/lifecycle.json
```

运行结果支持本有限模型的目标替换、合并、取消与代次失效合同；不提供真实服务取消政策、费用或性能结论。
