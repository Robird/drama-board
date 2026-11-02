# Character Gym 一期工程验收

> 历史命名说明：本文保留当时的 ResourceCycle / Cycle 类型、命令与验证记录。当前应用已重命名为 Playground；现行入口与持久身份边界见[重命名说明](../../docs/implementation/playground-rename.md)。旧名称源码链接固定到重命名前的 Git 版本。

日期：2026-10-31；实现提交 `d42fc8b`。范围为[一期里程碑](../design/character-gym/milestone.md)的轨迹、反馈及完成 S 分叉续跑。真实模型的独立运行证据见[串联试玩](character-gym-playtest.md)，操作入口见[使用说明](../design/character-gym/usage.md)。本文件区分确定性测试、真实模型试用与尚未证明的能力。

## 实现与权威边界

| 交付 | 实现与可复查证据 |
| --- | --- |
| 稳定现场定位 | [CycleBranchBoundary](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/src/ResourceCycle/Persistence/CycleBranchBoundary.cs) 捕获当前待服务角色的完成 S，检查历史祖先、版本、游标及可服务条件；[GymBranchTests](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Tests/GymBranchTests.cs) 验证精确旧 S、源分支不变、冷开、保留 lineage/随机坐标及不支持边界拒绝 |
| 持久轨迹 | [GymJournal](../../src/CharacterGym/GymJournal.cs) 提供全局序号、格式版本、校验链及落盘刷新；[测试](../../tests/CharacterGym.Tests/GymJournalTests.cs) 覆盖读开不修改、不完整尾部、完整行损坏、未知格式、写入/刷新失败后退休 |
| 真实材料导出 | [GymMessageProjection](../../src/CharacterGym/GymMessageProjection.cs) 保留嵌套原始消息及原生 replay 字段；[投影测试](../../tests/CharacterGym.Tests/GymMessageProjectionTests.cs) 与[导出测试](../../tests/CharacterGym.Tests/GymTraceExporterTests.cs) 检查角色隔离、分支截点、完整 Payload、Markdown fence、缺口及 Interrupted 不冒充游戏提交 |
| 反馈工具 | [LlmCharacterController](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/src/ResourceCycle.Completion/LlmCharacterController.cs) 先保存再返回收据，携原始 assistant/tool-result 临时续问；[LlmFeedbackTests](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Completion.Tests/LlmFeedbackTests.cs) 覆盖关闭、持久失败、取消、不完整响应、多工具拒绝、字符/反馈/调用预算及 Responses 原生消息映射 |
| 管理与恢复 | [GymCommands](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/src/ResourceCycle.Cli/Gym/GymCommands.cs) 提供查询、导出、按反馈/请求 fork；[命令测试](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Cli.Tests/GymCommandsTests.cs) 检查准确边界、大小写归一化、只读行为、分支已注册而来源日志失败的报告 |
| 宿主端到端 | [GymIntegrationTests](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Cli.Tests/GymIntegrationTests.cs) 使用真实磁盘和 CLI 宿主、夹具 Completion，检查反馈前后同 S、无反馈对照、双方经历不变、父未来隔离、父 pending E/操作日志不被子冷开修改及反馈 Tail 不跨冷开恢复 |

反馈和轨迹是宿主证据，不授权重放游戏操作。分支在 DG 中独立注册；源 S 的领域状态和 KernelCursor 原样继承，没有为了分支 ID 新增游戏提交。子分支独立操作日志防止父未来待完成操作在子分支被补录。

首版保留 GameRevision **15**，没有新增 DurableGraph Schema。日志格式为 **1**。未知游戏/日志版本明确拒绝，不新增历史游戏迁移政策；相邻 DG/Storage/Completion 使用现有源码引用。

## 构建与回归方法

```powershell
dotnet build DramaBoard.slnx -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
dotnet test DramaBoard.slnx -c Release --no-build --no-restore -m:1
pwsh -NoProfile -File scripts/Smoke-ResourceCycle.ps1 -OutputDirectory artifacts/gym-regression/batch
pwsh -NoProfile -File scripts/Smoke-ResourceCyclePlay.ps1 -OutputDirectory artifacts/gym-regression/play
pwsh -NoProfile -File scripts/Smoke-ResourceCycleSurvival.ps1 -OutputDirectory artifacts/gym-regression/survival
```

Schema Verify 构建通过，零警告、零错误。最终全 solution **1,113 项全部通过**：Host 2、Inventory 36、Kernel 131、CLI 368、Completion 44、ResourceCycle 441、Spatial 91；三个既有真实进程 smoke 也通过。原始日志保留于本地 `artifacts/gym-build-final.log`、`artifacts/gym-tests-verified.log`、`artifacts/gym-integration-verified.log` 与 `artifacts/gym-regression/`，不以测试源码存在代替实际运行。最终测试前仅修订集成测试的经历投影，并用 Verify 重新构建受影响项目；无未验证的生产改动。

集成期间发现并修正：原审计的抽象消息投影丢失嵌套内容；根操作日志不能安全服务子分支；仅按 S 筛选证据会混入父后续调用；Interrupted 结果不能称游戏提交；分支十六进制大小写应归一化。新测试中关于 Context 条目数量、handoff 经历和分支 ID 的错误假设也被修正，反馈隔离最终通过无反馈基线比较，而非假定正常游戏没有观察更新。独立复核最终没有发现未解决阻断项。

## 真实模型验收范围

采用真实 `gpt-6.1-sol`，Coding Agent 操作 a 的 Human Console，b 由模型决策。共 13 次实际调用、3 条反馈及 10 项有成功领域结果的游戏操作；父分支经历正常退出/冷开，随后从早期反馈对应 S 注册子分支、冷开并行动。主线程阅读原始日志、分支列表、导出及验证脚本后重新执行核对。

父分支 head 在注册和子续跑后相同，父导出逐字节不变；子首次请求材料与原请求相同，导出精确继承截点前的父记录并排除父未来。模型反馈由明确试用提示引导，不称自发发现。证据 ID、哈希、实际配置、操作和精确限制均在试玩报告，避免两处维护。

试玩使用的生产二进制在最终导出措辞修整前构建；其后生产代码仅将 `ExactResult=false` 说明收紧为“补录的宿主结论”，以免暗示 Interrupted 存在领域事实。最终测试覆盖该代码，不把后续构建冒作试玩当时二进制。

## 保留的限制与重开条件

- 支持角色待决策的完成 S，不支持任意 E 中间点；已发布 E 仍正常恢复。完整原生工具 Tail/调用执行栈不持久恢复。
- 反馈可重复，按调用身份去重不等于按语义去重；实际观察到重复地图建议。是否抑制及如何判断反馈质量留给二期。
- 记录只覆盖已保存证据；未启用 Gym 的间隙不会凭当前 Context 补造。导出报告可检查的关联缺口，但不证明覆盖全部游戏历史。
- 目前按同仓库、串行操作分支验收；没有承诺多进程并发、可独立搬运案例包或长期旧存档兼容。
- 本轮无需通关或修复反馈；代理操控 Human 入口不能替代另行保留的人类合作试玩验收。二期闭环改进须另行启动。
