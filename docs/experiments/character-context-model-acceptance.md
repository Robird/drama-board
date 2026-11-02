# 角色上下文 Model 验收

> 历史命名说明：本文保留当时的 ResourceCycle / Cycle 类型、命令与验证记录。当前应用已重命名为 Playground；现行入口与持久身份边界见[重命名说明](../../docs/implementation/playground-rename.md)。旧名称源码链接固定到重命名前的 Git 版本。

状态：2026-10-29 已完成工程与独立 Console 试玩验收。范围对应[完整设计](../design/character-context-model.md)，包含所有受控角色的持久材料、原始操作与拒绝、Console 呈现和开发者诊断；不是 LLM 端点验收。

## 合同与证据

| 合同 | 实现与可复验的证据 |
| --- | --- |
| 世界规则、感知、交互分别组织，同一 E/S 发布 | [CycleEngine](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/src/ResourceCycle/CycleEngine.cs) 在业务/访问/身体事实之后组合感知与交互；[CycleFactApplication](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/src/ResourceCycle/CycleFactApplication.cs) 校验并安装记录替换，单根 Business.InteractionStates 不形成第二个发布事务。 |
| 冻结背景、完整观察与有序 Items；没有 live world 引用 | [持久材料测试](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Tests/VisibleMaterialPersistenceTests.cs)、[Context 模型](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/src/ResourceCycle/CharacterContext.cs)、[纯渲染测试](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Cli.Tests/CharacterContextRenderingTests.cs)；后者分别验证保存背景和早期草稿，不从最新状态反写历史。 |
| use/handoff 等同轮；放行关闭；新 Opening 继承过程 | [生命周期测试](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Tests/CharacterContextLifecycleTests.cs) 验证原身份、交接、实际经历与冻结旧条目；放行接受结果不追加旧 Items。 |
| 各角色自己的公开/私有材料；无事件文案也交付快照变化 | [全角色事件测试](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Tests/AllActorObservedEventTests.cs) 与生命周期测试覆盖同地公开变化、私有物品隔离、未服务角色积累、两个 Open 角色的快照更新；纯渲染测试覆盖切回另一角色后的消费。经历只使用接收者局部顺序。 |
| Terminal 新序号、末次经历不丢，不自动请求控制器 | 生命周期及[持久恢复测试](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Tests/CharacterContextPersistenceTests.cs)；[呈现会话测试](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Cli.Tests/IncrementalSessionTests.cs)检查新的只读起止范围、无输入提示。 |
| 原始行动、成功或拒绝、原 ToolCallId 成组保存 | [共享 parser](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/src/ResourceCycle/CycleCommandParser.cs)、[拒绝测试](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Tests/CharacterContextRejectionTests.cs)覆盖空白原文、非法语法、非法目标、角色可见拒绝与直接提交 `:exit`；Console 元命令仍是本地管理操作。 |
| 最近 3 次操作只裁剪纯拒绝；不改变玩法或轮转 | 拒绝测试覆盖连续 4 次、成功交错、放行清理、其他角色/自然事件不计数、稳定条目与工具身份；拒绝保持活动、身体、通知、时间和服务游标，只递增请求版本。 |
| S 冷开、E-only 恢复安装原材料，不重新规划 | 持久恢复测试覆盖瞬时、放行、开轮与终局；[拒绝恢复测试](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Tests/CharacterContextRejectionPersistenceTests.cs)验证裁剪后的计数/集合和继续分配；纯渲染测试比较恢复前后完整角色输出。 |
| 审计补录保持拒绝，不重新执行或复活已删条目 | [操作恢复集成测试](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Cli.Tests/OperationRecoveryIntegrationTests.cs)覆盖 E-only、S 已写/宿主结果缺失、未提交 Received、已清理拒绝及原 ID 配对。 |
| 1024 同刻转换护栏不因清理或冷开返还 | 拒绝恢复测试推进全部预算、检查下一控制器未被调用并真实冷开；进程实录另保留预算耗尽与冷开立即失败证据。该限制仍存在。 |
| 完整 Context 和增量显示同源 | [CharacterContextRenderer](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/src/ResourceCycle.Cli/CharacterContextRenderer.cs) 从 Opening 和保留 Items 生成完整正文，增量从相同条目及相邻观察生成。会话测试检查重复消费、写出失败不推进游标，角色输出不再接收 Fact。 |
| 宿主提示、元命令、退出与角色范围分开 | [PlayPresentationSession](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/src/ResourceCycle.Cli/PlayPresentationSession.cs) 使用带外块；开始/结束带 actor 与 ContextSequence。look/layout/once/mode 只读保存材料；切换仍 Open 的角色不伪造领域关闭。 |
| 数据不获得 Console 权限或伪造结构 | 纯渲染测试覆盖多行、Markdown、`:exit` 与终端控制字符；原文在 Model 中保持，渲染为转义数据字面量。不是对同进程恶意 Controller 的隔离沙箱。 |
| 开发者诊断可选，提交后调用，失败不重做 | [CycleContextDiagnostics](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/src/ResourceCycle.Cli/CycleContextDiagnostics.cs) 保留强类型 completion 与角色经历关联；[诊断测试](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Cli.Tests/ContextDiagnosticsTests.cs)通过实际宿主比较启用/禁用、故障后的已提交状态、日志修复及 pending 恢复不重发。 |

## 呈现约定

纯输出是 `CharacterContextRenderer.RenderFull(state, layout)`，独立于终端历史；即使 Context 尚 Open，导出的范围标记也成对包围这次材料快照，不表示领域被关闭。正常 Console 的结束标记则绑定已提交关闭或终局。多个角色都 Open 时，带外切换/恢复说明只改变当前显示对象，旧角色材料仍留在自己的 Model。

完整重放按 Opening 与条目顺序表示原始操作、反馈、经历和必要状态；历史快照不读取当前草稿或尚未交付的 PendingAdvance。full 模式可重画保存材料，delta 按条目身份追加。已打印的过期拒绝不擦除，但纯输出不会重新混入它们。`:look` 与布局预览只重绘最新已记录状态，不强制重放整轮历史。

不再从运行时 `Check/Warning` 给 Console 注入未记录的行动反馈；角色风险来自已保存的观察和行动目录。原始输入/主观文字按数据渲染，有事件的成功操作不再重复输出同源结果文案。

## 验证与保留边界

最终呈现源码完成后，CLI 测试 245 项全部通过；完整 solution Release 构建（`--no-restore -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify`）零警告、零错误，Schema Verify 通过。领域部分在 `e350c88` 检查点运行 ResourceCycle 测试 281 项全部通过，此后只改 CLI、CLI 测试和文档，没有改领域源码。最终日志分别为本地 `artifacts/context-render-tests.log`、`artifacts/context-render-solution.log`；领域检查点日志为 `artifacts/context-rejection-domain-final.log`。

独立代理运行六组真实 CLI 流程，并在最终 DLL 上补验终局修正；全部进程退出 0、stderr 为空。覆盖瞬时操作/full/delta、元命令与 once、冷开同一 Open、放行进入新轮、自然终局与终局冷开、两个角色同时 Open 的轮转。检查两种布局纯输出与实际持久 Model：

- 同一 Open 冷开后仅执行元命令，前后 Model 与纯输出逐字节一致；已裁剪拒绝不复活，原始空格及成功操作历史保留。
- go 的接受、身体变化、抵达与警告只进入新 Opening；旧 Context 结束后不直出结算。
- 双角色夹具中 b 取消等待，使仍 Open 的 a 获得只有观察更新的 Item；显示与纯输出包含 Bob 已空闲，不泄露 b 的私人物品。夹具只预设初始状态，后续操作、持久化、轮转和渲染均由真实 CLI 执行。
- 终局冷开不请求输入，无元命令提示或未来交接草稿；历史交接和末次经历由语义测试另行覆盖。

详细本地工件保存在 `artifacts/context-render-playtest/`，包括 `playtest-report.md`、各流程的 input/stdout/stderr/exit 和各 `pure-*` 目录；主线程核对了原始输出、纯材料及测试结果。前一领域检查点的拒绝/同刻预算进程证据在 `artifacts/context-raw-submission-playtest/`。这些忽略目录是本机可查的运行工件，不作为仓库内永久文件链接；可复验合同保留在上述源码与测试中。

验收使用重定向 Console 与内部纯渲染入口，没有验证 Windows Terminal 的交互颜色或接入实际 LLM。测试检查材料归属、顺序和恢复，不锁定章节位置、整屏快照或缩进。

GameRevision 9；不兼容旧开发存档。本片没有实现 LLM 端点、完整外部模型会话恢复、任意长 tool-loop、长期记忆、最近 n 个完整回合窗口、通用空间可见性框架或耐久诊断日志/轮转。诊断 callback 只面向新的正常提交，冷开不重发旧世界日志；重新显示角色信息依赖保存的 Context。后续扩展由真实消费者与玩法证据重开。
