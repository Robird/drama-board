# 实际宿主预取原型：P1 与首批 P2 证据

2026-11-03。实际 Playground 宿主已接入单分支预取；本报告是中间工程验收，不是 G1–G7 总结，不提供性能结论。本批全部离线，没有真实模型请求。

后续工程、连续矩阵与在线接线已完成，当前结论见[最终报告](summary.md)。下文保留本批中间验收的原始范围与计数。

## 实现与已验证行为

- [宿主](../../../../src/Playground.Cli/PlaygroundHost.cs)沿用 home、控制绑定、连接池、日志和 pending 恢复；`--lookahead on|off` 或 home 顶层同名字符串显式选择，默认 off。恢复完成后才可能创建物理请求。来源检查当前 history/kernel world 引用、cursor 和无 pending E。
- [内存推演](../../../../src/Playground.Cli/PlaygroundLookaheadProjection.cs)只返回 A 一次合法 continue/wait，复用实际 Kernel 机械规则，下一角色输入处捕获冻结 Context 后停止。默认 128 步、24 模型小时、250ms 协作式上限；没有领域 Schema 或存档格式变化。
- [协调器](../../../../src/Playground.Cli/PlaygroundLookaheadController.cs)先发真实请求再准备候选。目标身份包括 run/branch、actor、ContextSequence、LastOperationSequence、时间；比较完整支持的首请求原值与 binding。未知首请求形状不复用。物理调用总账统一计数，候选采用不重复扣账；最多一个额外在途请求。
- [raw 审计](../../../../src/Playground.Cli/LlmRawAudit.cs)同锁追加独立 `lookahead-event`。实际逻辑 RequestId 与 PhysicalId 在 dispatch/delivery 关联；未采用内容不走真实控制器 Trace。完成任务在不被候选持有时及时观察并释放；取消不合作请求仍占槽，池销毁前排空。

| 证据 | 本批已证明 | 尚不证明 |
| --- | --- | --- |
| [RuntimeTests](../../../../tests/Playground.Cli.Tests/PlaygroundLookaheadRuntimeTests.cs) | 实际 Host 中 A 预测 wait 1、实际 wait 2，B 仍命中；B 先完成不能提交；MaxCalls=2 仍交付缓存；跨 tick 反复采用；非零目标操作序号匹配；声音连接导致同目标输入失配。 | 完整串行轨迹等价、性能矩阵或在线调用。 |
| [ProjectionTests](../../../../tests/Playground.Cli.Tests/PlaygroundLookaheadProjectionTests.cs) | 天气机械演化、来源状态/cursor/随机地址不变；非法 wait、合法 continue 保留活动；重复 A、Human、同地、未知控制及限额停止。 | 所有移动、目标死亡/替换组合。 |
| [LifecycleTests](../../../../tests/Playground.Cli.Tests/PlaygroundLookaheadLifecycleTests.cs) | 在途采用等待；不合作取消占槽并在停止时排空；采用/废弃错误分流；feedback 采用前无副作用、采用后保存并续轮；Human EOF。 | 所有诊断故障、连接清理失败和多个连接配置组合。 |
| [InputTests](../../../../tests/Playground.Cli.Tests/PlaygroundLookaheadInputTests.cs) | 当前 act/feedback 首请求可比较；原值、顺序及合同约束影响 key；未知消息/嵌套 schema/非空 Tail 拒绝复用；上游公共属性清单作为升级复核 gate。 | 任意原生多轮协议可缓存；首版明确不支持该范围。 |
| [RawAuditTests](../../../../tests/Playground.Cli.Tests/LookaheadRawAuditTests.cs)、[配置测试](../../../../tests/Playground.Cli.Tests/PlaygroundLookaheadConfigurationTests.cs) | 并发追加 hash 链、懒创建、写失败退休、旧 raw 阅读；配置默认/覆盖及严格校验。 | 所有宿主 raw 故障时的物理调用对账。 |
| [LlmHostTests](../../../../tests/Playground.Cli.Tests/LlmHostTests.cs) | 开/关两种模式的 pending E / 已存 S 恢复；开启模式先 recovery-only 暂停，无模型或 raw，再继续普通调用；caller 取消保持已提交状态。 | 中断前候选的持久恢复；本功能不保存候选。 |

## 执行证据

最终本批 CLI 全量命令：

```powershell
dotnet test tests/Playground.Cli.Tests/Playground.Cli.Tests.csproj -c Release -m:1 -p:DurableGraphSchemaHistoryMode=Verify --logger "trx;LogFileName=p2-cli-all.trx" --results-directory artifacts/playground-speculative-lookahead/p2-lifecycle
```

648/648 通过，零失败、零跳过。相关源码图构建及 Schema Verify 同步通过；这不是全 solution 最终 G7 工程验收。

| 工件（根为 `artifacts/playground-speculative-lookahead/`） | 结果 | SHA-256 |
| --- | --- | --- |
| `p1-integrated/p1-integrated.trx` | 初始 46/46 | `3ace16899e3e83b9a52440449bcc5f90e328b90aea4b250f24b35052c05b9959` |
| `p1-integrated/p1-cli-all.trx` | 初始 CLI 628/628 | `d3e67b4485596d77a54dbdfe0c2c983bdf305e3572e543ae905b8f7bc470182d` |
| `p2-lifecycle/p2-input-lifecycle-2.trx` | 扩展定向 68/68 | `d174c74fb86955db6a31de9f71eda24696f8a820f0a39cf8adffa40540b1c09f` |
| `p2-lifecycle/p2-cli-all.trx` | 本批最终 CLI 648/648 | `0368e3bfbd4092846fe874100752bef53f141de7e66deff335d8bdd1044ebc31` |

`p2-cli-smoke/manifest.json` 与四组 stdout/stderr 保存真实 CLI 的 init、新建 on、续局 off 和 CWD 默认 on，全部退出 0。使用脚本 Human 输入和示例连接文件，全程没有 provider 请求或模型 raw 文件。这验证实际命令、配置优先级与存档继续，不冒充真人试玩或在线 LLM 验收。所用 CLI DLL SHA-256 为 `70607a0d37ca7558ffae0d1b61587a038439f1d7ac1a5facf73a432fdd5fe8c4`。

初始失败工件保留：`p1-foundation-2.trx` 的天气夹具缺身体定义，已按内容合同修正；`p2-input-lifecycle.trx` 的三个失败来自无声音连接却预期失配、给所有 B 调用注入错误、真实新请求仍返回反馈的夹具错误。修复没有削弱采用合同；相反，camp/cabin 的大喊不影响 B 输入时允许采用，是本方案正确性的正例。最终声音失配测试显式验证目标身份不变、Reason 为 input-changed。

独立源码复核确认当前支持形状未见漏字段、没有分支事实或反馈污染路径；发现并修复了清理诊断覆盖主错误、目标连接慢认证以及长期保留已完成响应的问题。主线程重新检查实现并集中执行上述命令；作者报告没有被当作运行证据。

## 后续验收边界

完整串行输入/事实/状态对照、低/高耦合常驻测量、服务瓶颈比较、新一批在线调用与总账、详细 history 隔离的实际工件、独立新目录重跑及最终工程审查仍未完成。此报告不将 G1–G7 任一缺口改为可选，下一包以[工作计划](../work-plan.md)为唯一施工状态。
