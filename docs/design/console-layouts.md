# Console 可切换布局

本文描述当前实现。共享游戏文档与 Human Console 功能已按[元命令边界合同](console-command-boundary.md)分离；会话构造时由同一份 `ConsoleMetaCommands` 目录控制提示和解析，普通试玩显式选用 `HumanDefault`，内部可传入 `None` 全部关闭。

[CharacterContextRenderer](../../src/Playground.Cli/CharacterContextRenderer.cs) 从已提交的 Opening 与当前保留的有序 Items 组织完整角色材料；完整重绘与增量追加共享该入口。布局（Play layout）只负责把这些材料组织成文本，不读取完整世界、不校验或执行行动、不确认感知。接口为 [IPlayLayout](../../src/Playground.Cli/IPlayLayout.cs)，接受角色观察、全量语义快照或更新消息；分别渲染全量、事件/效果与当前状态、游戏行动帮助。元命令提示、输入提示符与带外答复由 Console 会话组合。增量合同见[切片 A](incremental-ui-slice-a.md)。

## 会话与命令

[PlayPresentation](../../src/Playground.Cli/PlayPresentation.cs) 持有本次 Console 会话的布局选择；所有角色共用该偏好，不把它写进角色或存档。生存模式默认 `decision`，多人采集默认 `classic`；二者均可在等待输入时切换。终局使用会话当前选择的布局，输出冻结记录后退出。

| 元命令 | 行为 |
| --- | --- |
| `:layout` | 显示当前布局及可选项 |
| `:layout decision` / `:layout classic` | 切换并立即重绘同一份观察 |
| `:mode` / `:mode full` / `:mode delta` | 列出或切换输出模式；生存默认 delta，多人采集明确仅支持 full |
| `:look` | 用当前布局重绘 |
| `:layout <名称> once` | 按指定布局临时显示完整观察，不改变布局、输出模式或消费游标 |
| `:help` | 元命令帮助、当前布局及全部可用布局的切换命令、当前布局的行动帮助 |
| `:exit` | 退出；也可使用 `:quit` |
| `:feedback <text>` | [独立保存开发反馈](independent-feedback.md)，不登记操作 |

元命令只接受冒号前缀形式；无冒号的 `look / help / quit / exit / details / layout` 均无效。游戏世界内的 `go / gather / use / wait / continue / cancel / handoff` 不加冒号。查看、切换、未知布局及非法元命令均留在同一次输入循环，不推进时间、不消费通知、不写游戏 E/S。EOF 沿用退出语义。退出重启后使用该模式默认布局。

上表是 `HumanDefault` 能力；`None` 不提示或执行这些元命令，`:exit` / `:quit` 均记录为拒绝，随后仍可接受游戏行动。行动语法由 [PlaygroundCommandParser](../../src/Playground/PlaygroundCommandParser.cs) 独立解析；`handoff :exit` 的正文不会再次分派。EOF 与宿主取消不受目录控制。配置不依据输出是否重定向；本轮未提供 CLI 禁用参数或会话中途切换。

两种场景均以 `(ActorId, ContextSequence)` 标识角色上下文，首次交付输出“LLM 上下文开始”，放行模拟后输出“LLM 上下文结束”，标记包含角色和回合号。启动说明与元命令帮助位于开始标记之前；中途管理回复和预览包在 Console 带外块中，不制造新的上下文边界。切回仍 Open 的角色时重放保存材料，不增加回合号；同一活动视图没有新条目时跳过重复输出。终局是新的只读 Context，有成对标记，无新行动或未来交接草稿提示；EOF/exit 只关闭 Console 会话，不伪造领域放行。标记在交互终端使用颜色，重定向使用纯文本。

[PlayPresentationSession](../../src/Playground.Cli/PlayPresentationSession.cs) 只维护临时消费游标及呈现状态；写出并 Flush 成功后才推进游标，失败后停止当前宿主交互。delta 追加新 Items 的事件、效果和受影响当前值；full 完整重放当前保留的材料，并为更新提供完整状态尾部。相邻观察来自持久 Model，查询不会从 live world 补取新信息。耗时行动的接受效果和之后结算归下一 Opening，Closed 的旧上下文不再刷结算文本。

完整角色材料与最新状态预览是两个入口：`CharacterContextRenderer.RenderFull` 输出 Opening + 当前保留 Items 及成对范围标记，不依赖 Console 曾打印过什么，也不包含提示符、输入回显、元命令、ANSI 或诊断；`RenderLatestState` 用于 `:look`、布局/模式切换与 once，只展示保存的最新观察及相关历史，不重放全部操作。纯输出是诊断未来 LLM 可见范围的依据；普通终端保留带外内容与滚动历史，并非当前 Model 的逐字副本。已裁剪拒绝不会在完整重绘中复活，无需擦掉终端旧文字。

两种布局通过 [ContextHistoryPlayView](../../src/Playground.Cli/ContextHistoryPlayView.cs) 展示 Opening 的上次放行信息与同源经历。背景使用保存的 `Opening.Background`，各 Item 的历史草稿从保存的效果还原，不借用当前草稿；本轮补给不改写历史效果。`handoff <文字>` 覆盖草稿，`handoff --clear` 清空，旧交接不自动续传。原始输入、交接文字等作为数据引用并转义控制字符，不把其中的 `:exit` 等文本解释成管理指令。

[操作 ID 合同](decision-context-handoff.md#44-操作-id结果配对与-completion-边界)由 Console 与 [PlaygroundOperationAudit](../../src/Playground.Cli/PlaygroundOperationAudit.cs) 落实：除[独立反馈](independent-feedback.md)的 `:feedback` 在 Receive 前分流并单独保存外，非空提交在解析前隐式生成 ToolCallId 并登记，查询/拒绝/元命令也保存同 ID 结果。`--store` 使用存档下的 `host/events.jsonl`，`--gym off` 仍保存操作；没有存档时仅保留在内存。空行和 EOF 不产生操作记录，布局偏好仍仅在当前进程中生效。

## 单次布局预览（2026-10-29）

`:details` 已移除，不保留别名；原用途由 `:layout classic once` 覆盖，也可用 `:layout decision once` 或预览其他已注册布局。

- 语法为 `:layout [名称 [once]]`：无参数列出选项；只有名称时持续切换并预览最新保存状态；名称后加小写 `once` 时仅展示一次完整观察。持续选择只在本次 Console 会话有效，不写入存档。
- 预览复用指定布局的 `RenderFull(PlaygroundViewSnapshot)`，数据来自当前角色的同一份观察。先输出所预览的布局和仍在使用的布局，随后输出游戏正文；不会临时修改 `Current` 再恢复，也不会增加上下文边界。
- 预览走独立查询答复，保持输出模式、消费游标和上下文身份；不推进时间、不确认警告、不发布游戏 E/S。操作入口仍生成 ToolCallId，完整答复按同 ID 配对记录。预览当前布局也遵守查询语义；`:look` 同样只预览最新保存状态，不重建或推进消费游标。
- 名称按注册表验证，`once` 按大小写精确匹配；未知名称、错误后缀或多余参数拒绝且保留原状态。`:layout once` 会将 `once` 当作布局名称查询；当前没有此布局，因此拒绝，不省略必需的名称。目录为 `None` 时预览同样禁用。
- 帮助与布局列表均从实际注册项生成持续切换和单次预览示例。测试覆盖语法、指定渲染器、偏好/游标保持、操作配对与只读边界，不固定具体布局内容的位置。

## 信息组织

- 两种布局的全量正文均采用“背景知识 → 上次推进及相关历史 → 当前状态与环境 → 当前行动”的阅读顺序。Console 元命令提示位于共享游戏正文之前，仍由会话按能力目录生成；Console 输入提示符明确标为宿主交互。
- 背景区展示一般规则和已知常识，再展示曾到访地点。地点记忆是经历形成的知识，但不是永远有效的事实：保留观察时间，明确旧库存可能已经改变，不暗示当前远处状态。规则只取本场景已有信息，不能泄露未探索的地点或资源。
- 历史区展示 Opening 中上次放行区间的经历、上回合主观交接、历史通知/未确认警告和最近一次实际天气。后三者可能早于上一回合，因此不统称为“上一回合事件”，也不伪装成尚未实施的完整历史窗口。本轮交接草稿放在当前区；放行行动只在下一 Opening 作为历史出现；下一天气窗口、当前暴露和趋势也属于当前区。
- `classic` 保留 [PlayView](../../src/Playground.Cli/PlayView.cs) 的表格与完整分栏；`decision` 使用 [DecisionPlayLayout](../../src/Playground.Cli/DecisionPlayLayout.cs) 的简洁状态与行动分组。背包、资源和活动先呈现当前状态，行动集中在尾部；为保持操作局域性，行动旁仍可重复必要的数量、耗时、使用效果和受阻原因。身体阈值预测集中在属性附近；行动仅补充改变预测条件的例外，相同通路提示按组显示。
- [ContextHistoryPlayView](../../src/Playground.Cli/ContextHistoryPlayView.cs) 分别渲染已保存历史与本轮交接，天气同样分开渲染历史效果与当前窗口；领域材料由 Context 提供，布局不累计另一份摘要。
- 此顺序适用于新回合全量、重绘、单次预览及 full 模式中的完整正文。增量输出仍按“事件 → 从属效果 → 受影响状态最新值”追加，不重复背景规则。终局保留历史与冻结状态，省略行动引导和未来风险预测。输入提示显式标注 Console 与角色；只有实际进入控制器才显示输入提示符；即时反馈不再显示“正在处理结算”。
- 帮助提供行动语法与条件，完整观察提供当前可见知识、历史材料与状态；它们均不等于全量世界或完整事件日志。当前事实、历史记录和未来条件估计必须可区分。
- 行动条件和额外提示采用游戏产生的 `Commands`，属性阈值预测来自保存的 `SurvivalObservation`；不在布局中复制业务规则。终局明确冻结，不提供新行动或未来风险预测。
- 采集保留本人等级、资源等级门槛和每份耗时；每份耗时读取冻结的 `HarvestDurations`，当前活动使用已保存的周期及期限。布局不按技能等级重算全局速度百分比，不以当前规则重新解释旧 Context。

新增布局只需实现接口并在 `PlayPresentation.ForMode` 注册。当前不引入插件发现、动态程序集加载、布局持久化或通用 UI 框架。

## 警告原因与途中路线

- 返回原因取本次 Opening.Experiences 的 `SurvivalWarning`，按事件中新增的 `warning-food/water/warmth` 效果列出全部触发属性；经历正文复用同一解释。历史待确认警告仍在历史区，不据此推断本次返回原因，也不从补给后的当前值反推。
- `PlaygroundObservation.Journey` 保存 [PlaygroundJourneyObservation](../../src/Playground/PlaygroundJourneyObservation.cs)：出发地名、通路标识、行进方向的线索、可空的已知目的地名；抵达期限继续使用 `ArrivalDue`。两者在途中同时存在，抵达后同时清除，途中终局则保留为冻结记录。
- 观察构建从当前空间行程取得方向；只有该角色曾到访的目的地才能公开名称。去往未探索地点时显示“未知目的地（尚未探索）”和出发时可见的通路线索，不把世界地图中的真实目的地身份带入角色材料。反向行走使用反向线索。
- 两种布局通过共享 `JourneyView` 呈现路线、抵达时刻、剩余时间及不可取消约束；终局改为原计划与未完成记录。增量快照也含行程键。查看、补给、continue 与冷开不靠上回合命令猜路线；完整重绘只读冻结材料。
- 持久化增加 Observation V2、JourneyObservation V1，GameRevision 12；不迁移旧开发存档。测试覆盖多属性同时告警、补给后保留真实历史原因、旧警告不冒充新原因、未知目的地不泄露、反向已知路线、抵达清除及观察落盘还原，不锁定排版位置。

本轮验证（2026-10-29）：Playground 312 项、CLI 273 项测试通过；Release solution 构建与 Schema Verify 通过，零警告。实际 CLI 首次探索／返程与退出冷开共两次会话验证了两种布局、历史警告、未知目的地边界和途中补给；本地原始输出在 `artifacts/journey-playtest/`，未验证终端颜色与窄屏换行。

## 打磨与验证

具体布局是高频调整的 UI：不建立整屏快照，不断言字段所在行、章节顺序或精确措辞。自动测试集中于元命令解析、布局路由、会话范围与只读边界；信息完整性、可发现性和决策局域性通过实际 CLI 观察与独立试玩复核。既有经典布局测试暂保留，不复制为新布局的格式合同。

属性预测的共享材料与行动提示职责按[属性阈值预测方案](attribute-threshold-forecasts.md)调整：每项属性附近呈现降至警戒线与耗尽时刻，行动区仅保留改变预测条件的例外。布局不另写预测算法，也不从风险文案反向推断业务事实。
