# 角色 History 与统一宿主日志实施验收

日期：2026-11-02。环境：Windows、SDK 10.0.201、Release。合同见[设计](../design/character-history-and-diagnostics.md)，使用入口见[Gym 使用说明](../design/character-gym/usage.md)。本轮完成设计的三个切片，保留真实模型、确定性 fixture 和代理操作 Human Console 的证据区别。

## 实现与数据边界

- [PlaygroundHostJournal](../../src/Playground.Cli/PlaygroundHostJournal.cs) 独占 `host/events.jsonl`，保存有序主记录、JournalId、校验链和增量操作索引；完整行刷盘后才安装内存状态，失败 writer 退役。旧独立操作/Gym writer、共享旧导出器及其运行接线已撤下。
- [PlaygroundOperationAudit](../../src/Playground.Cli/PlaygroundOperationAudit.cs) 绑定分支和 RunId，保留幂等 Receive、不可改写 Completed、Interrupted 和恢复补录。世界 [ReadCommittedOperations](../../src/Playground/Persistence/PlaygroundDurableHistory.cs) 只匹配当前分支创建的提交，祖先同 ID 事实不能完成子分支输入；世界状态和历史继承保持原行为。
- [HostSession](../../src/Playground.Cli/PlaygroundHostSession.cs) 捕获薄角色材料、捕获世界 S/lineage/transition/tick 和模型阅读投影；捕获定位与冻结 Observation 时间分别保存。它和 [LlmRawAudit](../../src/Playground.Cli/LlmRawAudit.cs)、反馈共用本次 RunId。raw 首次实际调用前创建，记录完整模型证据；全 Human 不创建模型文件。
- [人物导出](../../src/Playground.Cli/CharacterHistoryExporter.cs) 先选可证明分支前缀，再按角色和追加顺序呈现。Reasoning 只来自 `PlainText`，正文不转储 native payload；普通导出不打开 raw，反馈附件损坏仅造成附属缺口。请求 fork 保存请求前的 Sequence 截点及 SourceJournalId；独立反馈管理不依赖主日志。
- 持久运行默认 record，off 仍保存运行和操作。ephemeral 默认 off，显式 record 拒绝，主记录仅在内存。旧宿主存档在世界 Open/Create 前只读拒绝；不迁移、删除或挪动旧文件。可以保留原 home/连接配置并选择新 store。

没有修改 DurableType、Schema、GameRevision 22、Kernel 或 Completion provider。原始审计可含模型私有/native 内容，人物 history 是开发者材料，不自动交付其他角色或回灌记忆。

## 工程与故障验证

同一构建图串行运行：

```powershell
dotnet build DramaBoard.slnx -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
dotnet test DramaBoard.slnx -c Release --no-build --no-restore -m:1 --logger trx --results-directory artifacts/history-final-tests
```

最终完整构建零警告、零错误，Schema Verify 通过；10 个测试项目共 **1484 通过、0 失败、0 跳过**。其中 CLI 563、Playground 548、CharacterControl.Completion 13、CharacterGym 7。日志为 [final build](../../artifacts/history-final-build.log)、[final tests](../../artifacts/history-final-tests.log)及 [TRX](../../artifacts/history-final-tests/)。主要验收来源：

| 合同 | 本次运行的测试来源 |
| --- | --- |
| 刷盘前不安装、校验链、完整行损坏、尾部只读、幂等及 branch-local pending | [OperationJournalTests](../../tests/Playground.Cli.Tests/OperationJournalTests.cs) |
| 旧数据拒绝且世界/旧文件不变、坏新文件不重建、domain-only 覆盖起点、三种模式 | [HostJournalInitializationTests](../../tests/Playground.Cli.Tests/HostJournalInitializationTests.cs) |
| 真正 Host 的 raw→主日志顺序；request 失败零调用、response 失败无 Receive；反馈已保存后关联失败 | [HostTimelineRuntimeTests](../../tests/Playground.Cli.Tests/HostTimelineRuntimeTests.cs)与[FeedbackHostTests](../../tests/Playground.Cli.Tests/FeedbackHostTests.cs) |
| 主日志或诊断失败叠加 client Dispose 失败，保留原异常/退出码；正常结束清理失败退出 1 | HostTimelineRuntimeTests；清理只向 stderr 报固定诊断，不写故障 journal |
| E/S 中断后补结果、不重做输入、不恢复已裁剪拒绝 | [OperationRecoveryIntegrationTests](../../tests/Playground.Cli.Tests/OperationRecoveryIntegrationTests.cs) |
| 父 pending E/操作不被 child 恢复；父已完成同 ID 与 child 仅 Receive 同 ID 的真实存档对照 | [GymIntegrationTests](../../tests/Playground.Cli.Tests/GymIntegrationTests.cs)；[领域分支测试](../../tests/Playground.Tests/GymBranchTests.cs)继续证明世界前缀继承 |
| 文本/opaque PlainText/null/多块、工具收据、未知投影缺口、Human 无虚构思考 | [投影测试](../../tests/Playground.Cli.Tests/CharacterHistoryProjectionTests.cs)与[导出测试](../../tests/Playground.Cli.Tests/CharacterHistoryExporterTests.cs) |
| 多次 feedback→Tail 收据→act、同 RequestId response 后 failure、跨 Run/Context、延后交付/Terminal | GymIntegrationTests、导出测试及既有生活世界 Host 测试 |
| 精确 fork、选中前缀不能倒灌后来 Completed、readonly 字节不变、不覆盖输出 | [GymCommandsTests](../../tests/Playground.Cli.Tests/GymCommandsTests.cs)、导出测试 |

## 实际进程与模型证据

`artifacts/` 为本机保留证据，不提交存档、模型原文或临时配置。

1. [Home smoke](../../artifacts/history-home-final/manifest.json)：最终二进制的实际 CLI 进程，验证 home/CWD、持久默认/off、ephemeral、全 Human 无 raw。无模型调用。
2. [最终混合运行](../../artifacts/history-live-final-20261102/manifest.json)：新实例，两角色；a 为代理通过标准输入操作的 Human Console，b 使用本机 Responses proxy 的 `deepseek-v4.1-flash`。2 次调用均 Completed，按共享调用预算退出 2；5 对 Receive/Complete、11 条角色材料、1 条 Human 独立反馈与主关联。两次均有可读 PlainText，第二次同时包含普通 Text；11 条材料均有捕获世界位置。stderr 为空。
3. 将该实例全部 raw 文件移至实例下 `raw-detached/` 后，分别重新导出 a/b；每个角色前后文件 SHA-256 完全一致：a 为 `D80C82E1706512D8417DBD44E21B7A8FC40583FFE4E521EC2B5CBCD3470F87FF`，b 为 `42DD17FAB2EC684FB9DCF798B5BE1573D7F1688BB2575A27F73BBFB6F6F890AB`。导出正文保留观察、推理、普通输出、工具尝试、操作结果及已交付经历，不依赖 raw。
4. [管理源实例](../../artifacts/history-gym-source-20261102/)：三角色新局，另做 2 次同模型真实调用，Completed 后预算暂停。它满足既有 HillsideGym smoke 的前置条件：a 在 08:00 的首次请求。先将两角色/b 请求实例交给该专用脚本的尝试明确因前置条件拒绝，未分叉或调用模型，失败证据保留于 `artifacts/history-gym-smoke/`。
5. [管理分叉 smoke](../../artifacts/history-gym-final/manifest.json)：最终二进制从上述真实请求普通 CLI 注册 child，两次 Human 冷开完成 handoff 和移动，无新模型调用/raw 文件；逐角色检查导出 marker、请求前截点、私有材料隔离，父世界 head、宿主分支投影和人物导出保持不变。每次 readonly 管理均核对全 store 字节不变。
6. [Hillside 经济 smoke](../../artifacts/history-hillside-final/)和[命名内容 smoke](../../artifacts/history-content-final/)验证迁移后的操作解析脚本；前者两进程实际提交 39 条命令，后者覆盖命名内容和冷开，均通过。均无模型调用。

实现过程另保留[最初四次混合调用](../../artifacts/history-live-20261102/)及首次管理 smoke 的证据；连同管理源两次和最终两次，本轮真实调用共 8 次，均 Completed。最终 CLI SHA-256 为 `AC2B46018C22C82E67894973281C4E6B82BC22221C3FB545BB44375B7237A684`，各 final manifest 记录同一身份。真实调用验证当前 proxy/模型与主阅读链路，opaque reasoning 的可读摘要/null 等组合由离线 fixture 验证。本轮没有真人自由试玩，也没有对长期日志体量、记忆回灌效果或跨版本续局作出验收结论。

## 复用与继续

```powershell
dotnet run --project src/Playground.Cli -c Release -- --home artifacts/my-world --actor a --gym-export artifacts/a-history.md
```

多分支时添加 `--branch`。输出使用 CreateNew，已有文件不能覆盖。旧宿主版本可从重构前 Git 提交 `c0e1880` 取回；本次没有旧数据迁移工具。自动摘要、检索/回灌、数据库索引、分段压缩和并发分支仍按设计延期。
