# Console 可切换布局

布局（Play layout）只负责把角色观察组织成文本，不读取完整世界、不校验或执行行动、不确认感知。接口为 [IPlayLayout](../../src/ResourceCycle.Cli/IPlayLayout.cs)，输入只有 `CycleObservation`；分别渲染日常观察、帮助与输入提示。

## 会话与命令

[PlayPresentation](../../src/ResourceCycle.Cli/PlayPresentation.cs) 持有本次 Console 会话的布局选择；所有角色共用该偏好，不把它写进角色或存档。生存模式默认 `decision`，多人采集默认 `classic`；二者均可在等待输入时切换。终局使用会话当前选择的布局，输出冻结记录后退出。

| 元命令 | 行为 |
| --- | --- |
| `:layout` | 显示当前布局及可选项 |
| `:layout decision` / `:layout classic` | 切换并立即重绘同一份观察 |
| `:look` | 用当前布局重绘 |
| `:details` | 临时查看经典完整观察，不改变布局选择 |
| `:help` | 元命令帮助、当前布局及全部可用布局的切换命令、当前布局的行动帮助 |
| `:exit` | 退出；也可使用 `:quit` |

元命令只接受冒号前缀形式；无冒号的 `look / help / quit / exit / details / layout` 均无效。游戏世界内的 `go / gather / use / wait / continue / cancel / handoff` 不加冒号。查看、切换、未知布局及非法元命令均留在同一次输入循环，不推进时间、不消费通知、不写游戏 E/S。EOF 沿用退出语义。退出重启后使用该模式默认布局。

生存模式在首次交付及 `(ActorId, ContextSequence)` 变化时清屏；启动说明与帮助显示在首次清屏之后。同一回合的补给、交接编辑、查询与布局切换追加输出；放行动作的即时结果也先追加，直到新回合才清屏。终局封存结果后退出，不虚构新的可行动回合。未启用连续性的多人采集仍以角色/时间判断展示边界。清屏由 Console 控制器管理，与所选布局无关；输入或输出重定向时保留完整文本，终端清屏发生 I/O 错误则退回追加输出。

此行为用于试玩时检查单次观察是否足以决策，不等同于 LLM 上下文隔离：终端自身的回滚历史不作跨平台保证，未来 LLM 收到哪些历史消息仍由上下文构造决定。

两种布局通过 [ContinuityPlayView](../../src/ResourceCycle.Cli/ContinuityPlayView.cs) 展示[决策回合的信息继承](decision-context-handoff.md)：当前观察、固定的上次行动结果、主观交接文字与本轮草稿分别呈现。本轮补给不改写历史身体值，旧交接不会自动续传；`handoff <文字>` 覆盖草稿，`handoff --clear` 清空。

[操作 ID 合同](decision-context-handoff.md#44-操作-id结果配对与-completion-边界)由 Console 与 [CycleOperationJournal](../../src/ResourceCycle.Cli/CycleOperationJournal.cs) 落实：非空提交在解析前隐式生成 ToolCallId 并登记，查询/拒绝/元命令也保存同 ID 结果。`--store` 使用存档下的 `operations.jsonl`；没有存档时仅保留在内存。空行和 EOF 不产生操作记录，布局偏好仍仅在当前进程中生效。

## 信息组织

- `classic` 保留 [PlayView](../../src/ResourceCycle.Cli/PlayView.cs) 的完整分栏观察，便于比较与查询细节。
- `decision` 使用 [DecisionPlayLayout](../../src/ResourceCycle.Cli/DecisionPlayLayout.cs) 围绕当前处境、需响应事项和眼前行动组织信息。通路旁呈现耗时与风险，相同风险按组只显示一次；补给旁呈现数量与使用效果；资源旁呈现库存、周期和采集操作。输入附近保留当前时间、剩余行程、身体与庇护摘要及合法响应；途中不显示等待模板。非生存模式复用经典布局。
- 稳定规则、完整历史与所有等待时长的详细风险可通过帮助或完整观察查询。当前事实、历史记录和未来条件估计必须可区分；任何布局都不能增加观察之外的世界知识。
- 校验和风险采用游戏产生的 `Commands`，不在布局中复制业务规则。终局明确冻结，不提供新行动或未来风险预测。

新增布局只需实现接口并在 `PlayPresentation.ForMode` 注册。当前不引入插件发现、动态程序集加载、布局持久化或通用 UI 框架。

## 打磨与验证

具体布局是高频调整的 UI：不建立整屏快照，不断言字段所在行、章节顺序或精确措辞。自动测试集中于元命令解析、布局路由、会话范围与只读边界；信息完整性、可发现性和决策局域性通过实际 CLI 观察与独立试玩复核。既有经典布局测试暂保留，不复制为新布局的格式合同。

当前预测只提供最早耗尽时刻及行动风险文本，尚未携带造成风险的指标。后续如需显示“饮水将耗尽”等原因，应扩充游戏的观察投影；布局不另写预测算法，也不从风险文案反向推断业务事实。
