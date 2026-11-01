# 山野镇 Gym 实际运行与入口收敛

2026-11-01。领域源码基线 `ea87f55`，GameRevision 22；本片只调整说明和验收脚本，不改变游戏规则。C1 的唯一入口采用山野镇 `ResourceCycle.Cli`，Microtown 明确停止独立产品演进，保留源码、测试和历史证据；内部 `Cycle*` 命名不要求机械迁移。

## 真实模型探路

通过已构建产品入口新建默认三居民世界，`a/b/c` 均由真实 `gpt-6.1-sol` 控制，使用宿主内置 Codex 连接。未修改角色提示、存档或初始资产；需求与天气保持开启。预先声明最多 6 次调用、200 次提交、绝对第 10 小时、墙钟 8 分钟，任一上界或错误即停，不自动重试。

```powershell
dotnet src/ResourceCycle.Cli/bin/Release/net10.0/DramaBoard.ResourceCycle.Cli.dll --store artifacts/living-world-gym/world --llm --gym --model gpt-6.1-sol --max-calls 6 --max-failures 2 --steps 200 --until-hour 10
```

实际墙钟 23.342 秒，6 个 Completed 响应对应 6 条成功领域行动，无协议失败；退出码 2、`call-budget-exhausted`，不是世界结束。三角色分别执行 `go market-home`、`go market-workshop`，从 08:00 开始经过市场到工坊，尚不足以判断长期生活或合作能力。模型未产生交易、生产或消费；这不是自由游玩，也不满足 C7 三日要求。

Provider usage 合计未缓存输入 31,739 tokens、输出 117 tokens、缓存读取/创建均为 0；这些是返回 usage，不由字符数推算。无本次计费金额数据。原始证据保存在 `artifacts/living-world-gym/`：

- `01-scout.result.json`、`01-scout.output.txt`、`01-scout.error.txt`（空）。
- `world/llm-runs/3219ad0bcc634f66a73ed1ed22abb11f.jsonl`：宿主预算、模型身份、输入响应、提交与停止原因。
- `world/gym/events.jsonl`：请求/响应各 6，操作 6、perception 13、run-start/end 各 1，以及 metadata。
- `world/operations.jsonl` 与完整游戏存档：宿主关联与领域 E/S 各自保存；前者不是后者的替代品。

源内容为 `hillside-v1 / E763926A859EDDEC4F1219F5701186F03656D17693E99435193D752C50C18E22`。源分支 `4a8047e0`；首个 a 请求 `b4a14def692e45bfad7d00cd1aab5b37`，Gym sequence 4，决定前 S `4a8047e00e0000d000000000`，08:00、transition 1、Context 1、operation 0。此请求提供真实的管理分叉锚点。

探路 CLI SHA-256 为 `EF0BFE2E8CFA4BEB8DC3FB0461DBD4D366C1BF93226B1275B2697610D1FB67FA`，领域程序集为 `A2CAA85B1DE10D1D8115D9A96B80FE1A7EF7003F332C69B92D580A2363FCF111`。后续 Verify 重建仅伴随文档改动，构建来源标记可能改变程序集哈希；各会话以自身记录的哈希为准，不声称字节相同。

## 管理分叉、导出与两次冷开

主线程实际执行 [Smoke-HillsideGym.ps1](../../scripts/Smoke-HillsideGym.ps1)，使用上述真实请求局，在新目录保留全部管理进程、输入输出与审计：

```powershell
pwsh -NoProfile -File scripts/Smoke-HillsideGym.ps1 -Store artifacts/living-world-gym/world -OutputDirectory artifacts/living-world-gym/management-final
```

12 个 CLI 进程全部退出 0、stderr 为空。`management-final/manifest.json` 保存逐进程参数、前后逐文件哈希、源/子分支身份与断言；脚本只通过产品 CLI 注册分支和执行普通命令，不直接改世界或存档文件。两次子分支运行显式指定不存在的连接 catalog，若意外走到模型初始化就失败，因此不会借环境凭据额外调用模型。

| 验收点 | 实际证据 |
| --- | --- |
| 从完成 S 注册 | 子分支 `488047e0` 的 Head、CompletedState 和 ForkedFrom 均为上述决定前 S；注册不执行该次模型选择。 |
| 截断父未来 | branch-fork 的 `SourceThroughSequence=3`，严格小于 request sequence 4；子导出逐记录序号等于允许的祖先前缀＋子记录，不继承父之后的行动或模型响应。 |
| 两次冷开 | 第一进程 Human 控制 a，`handoff gym-child-private` 后一步预算暂停；第二进程 Human 控制 b，打开 Context 后 `go home-workshop`，两步预算暂停。两次 run-start/end 完成 S 严格承接，内容身份与源一致。 |
| 成功行动与绑定变化 | 两条普通命令各有唯一成功领域回执；第二审计明确 `MoveStarted(b)`，没有将请求或宿主文字当作移动成功。控制绑定变更不替换角色，最终子 S 为 `488047e00e00007401000000`。 |
| 源分支不变 | 源 head 仍为 `4a8047e00e0000b009000000`；源 operations SHA-256 仍为 `83344A782943D9B037DA0C729D20358602D2BDCB0F9F6BD31A6A9CA71551F682`，源 a 导出前后完全相同。不可变源 S 的身份保持，未用子世界冒充源世界。 |
| 角色与操作隔离 | 子操作进入 `branch-operations/488047e0.jsonl`；a 私有 handoff 存在于 a 导出，不在 b/c 导出或 b 的终端中。导出逐记录校验角色范围和精确祖先序号。 |
| 管理只读 | 每次 branches/export 前后对整个存档逐文件 SHA-256 比较，均未改变；fork 和普通行动是明确的写入操作，不要求整个目录不变。 |
| 无额外模型调用 | 两次子运行没有 provider binding、request、response；绑定中的两个 LLM 角色未实际发起调用。因此此脚本不算 C7 真实混合控制会话。 |

第一次脚本运行保留在 `management/`：错误假设 handoff 后仍由 a 连续操作，实际公平调度转到 b，缺失 catalog 阻止了连接初始化。handoff 已提交，无网络请求；旧子分支 `4b8047e0` 和失败 manifest 均保留。修正脚本为明确步数及启动控制绑定，再从同一源请求注册新子分支验收。没有修改生产调度以迎合脚本，也没有把失败子分支伪装成最终通过。

这补齐 C5 的实际 Gym 导出与管理入口分叉证据；正常冷开、E-only 不重 Plan、拒绝/竞争与内容恢复仍由[连续世界](living-world-foundation-acceptance.md)、[经济](living-world-economy-acceptance.md)、[交易](living-world-trades-acceptance.md)和[内容验收](living-world-content-acceptance.md)共同支持。只接受当前修订，原生模型会话恢复、任意倒带与并发分支运行不在支持范围。

## 工程回归

本轮按 `Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify` 串行运行：

| 范围 | 通过 / 失败 / 跳过 | TRX 目录 |
| --- | --- | --- |
| CharacterGym.Tests 全部 | 30 / 0 / 0 | `artifacts/living-world-gym/gym-tests/` |
| ResourceCycle.Cli.Tests，`FullyQualifiedName~Gym` | 27 / 0 / 0 | `artifacts/living-world-gym/cli-tests/` |
| ResourceCycle.Tests，GymBranchTests 与 HillsideContentBindingTests | 17 / 0 / 0 | `artifacts/living-world-gym/domain-tests/` |

测试覆盖日志完整性、反馈/Tail、角色范围导出、祖先截断、分支与内容绑定等；实际模型证据与这些 fixture 分开。当前片无生产源码改动，完整领域/CLI/Completion 回归引用同一规则版本的[内容验收](living-world-content-acceptance.md)，不把本轮 74 项说成重新跑了全套。
