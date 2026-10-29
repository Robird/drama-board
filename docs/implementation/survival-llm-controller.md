# 生存 2A：独立 Context 调用与 LLM 控制器

对应[二期目标 §3.2](../design/survival-demo-milestone.md#32-2a单角色-llm-接线)。本片只接单角色；双角色规则、控制器路由和交流赠与仍属于 2B/2C。

## 运行

使用相邻 atelia-completion 源码，依赖入口见[源码说明](../source-dependencies.md)。普通构建和测试不调用真实模型。

```powershell
dotnet run --project src/ResourceCycle.Cli -c Release -- --survival --llm `
  --model gpt-6.1-sol --store artifacts/my-llm-game --max-calls 128
```

默认明确选择内置 `drama-codex` 连接，使用 Completion 的 `OpenAICodexResponsesClient`。认证从 Codex 的标准 auth.json 路径读取，也可提供 `--auth-file <绝对路径>`。认证读取、账号指纹检查和网络协议由 Completion 负责，游戏不读取或保存 token。此路径按本次宿主启动读取的账号配置客户端；不承诺跨进程原生模型会话恢复。

自定义连接使用 `--catalog <V3目录文件> --connection <精确ID>`；不根据目录顺序或默认值回退。未提供 `--model` 时沿用所选连接的模型。只有所选连接才解析环境配置；目录中的 Codex 连接使用 Completion 的 `ATELIA_CODEX_SUBSCRIPTION_*` 环境配置，不与内置路径的 `--auth-file` 混用。

同一命令、同一 `--store` 即为续局。省略存档仅内存运行。日志默认保存在存档下的 `llm-runs`；无存档则使用 `artifacts/llm-runs`。`--llm-log <新文件路径>` 可指定本次证据文件，已存在文件不会覆盖。

## 分层和调用顺序

- [ResourceCycle.Completion](../../src/ResourceCycle.Completion/LlmCharacterController.cs) 只实现 `ICycleCharacterController`：借用 `ICompletionClient`，接收模型 ID、纯 Context 投影函数、运行限额和宿主 trace 回调。它不执行游戏命令、不持久化客户端、不私藏角色历史。
- [LlmConsole](../../src/ResourceCycle.Cli/LlmConsole.cs) 负责连接目录、客户端生命周期、旁观输出和审计；直接注入现有 `CharacterContextRenderer.RenderFull`。因此不需要把 CLI 渲染库整体搬迁，也不让领域库依赖 Completion。
- 每次请求由系统说明、单个 `act` 工具定义和当前角色完整保留材料构成。协议纠错时另附简短的宿主纠错消息；不伪造工具执行结果。上一模型的 assistant/reasoning 消息不续接。
- 仅接受 `Completed` 中恰好一个 `act({command:string})`；工具选择为 Codex 可表达的 Auto，但本地仍严格检查数量、名称和参数形状。空字符串和游戏语法错误由游戏持久拒绝，畸形工具 envelope 则不进入游戏。
- 原样保留 ToolCallId，独立生成 OperationRecordId。模型返回后先保存宿主 receipt，游戏经共享 parser/校验发布 E/S，只有 Step 提交成功后才显示 committed。不能把 response 日志当作执行收据。
- 即时操作和拒绝后重新投影同一 Context；接受放行操作后关闭旧 Context，结果在下一 Opening 交付。系统说明提醒首次形成计划时使用 handoff，已有适用草稿时直接行动，避免笔记重写循环。

旁观终端显示实际模型输入、可见输出/操作及已提交结果；JSONL 另保存完整响应块、调用描述、usage 和工具 schema。opaque reasoning/replay 不显示在终端，审计也不自动成为角色知识。日志属于宿主证据，不是可恢复的原生协议会话。

## 限额、暂停与恢复

默认本次宿主最多 128 次调用、最多 3 次连续完整响应协议失败、完整 Context 最多 200000 个 UTF-16 代码单元。对应参数为 `--max-calls`、`--max-failures`、`--max-context-chars`。适配器另外限制解码命令长度为 8192；游戏自身继续校验 handoff 等内容。超大材料显式暂停，不裁剪。重启宿主取得新的运行预算，角色 Context 与游戏同刻护栏不重置。

协议错误可在限额内纠正；网络失败、本地请求拒绝、非 Completed 或流中断立即停止，不透明重试。Ctrl+C 取消当前调用。退出码：正常旅程结束 `0`、宿主/连接失败 `1`、限额或已分类调用失败暂停 `2`、取消 `130`。任何停止都不自动选择 wait 或 continue；角色已接受的活动可在下次请求前正常推进，调用预算约束模型调用而非每个世界事件。

恢复顺序为：加载游戏 → `RecoverPending` → 用已提交游戏事实修复操作收据 → 初始化模型连接 → 服务下一请求。已终局存档直接重绘，无需认证或再次调用模型。

| 中断位置 | 恢复行为 |
| --- | --- |
| 请求发出、响应中途或完整响应已返回，但 E 未发布 | 使用原已提交 Context 再次生成；结果和计费可能不同，不从模型审计重放操作 |
| E 已发布、S 未发布 | 由记录事实完成 S，不询问控制器，不重复效果 |
| S 已发布、旁观显示/宿主结果日志尚未完成 | 从保存材料重新展示，按游戏事实修复已知收据 |

不承诺外部调用恰好一次、全局 ToolCallId 唯一或跨模型原生推理续接。生存领域 Schema/GameRevision 在本片不变。证据与实际限制见[2A 验收](../experiments/survival-llm-acceptance.md)。
