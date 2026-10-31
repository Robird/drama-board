# 目标操作与游戏接线合同

状态：2026-10-31，首版施工依据，尚无这些生产命令。领域语义见[设计](design.md)，材料与限额见[容量及 Context](capacity-and-context.md)。本合同固定行为和首版传输选择；具体 C# 类型名不是公共 API 承诺。

## 1. 一次提交一项操作

沿用 `ICycleCharacterController` 返回原始 `CycleControllerOperation`、Completion 单 `act(command)` 的入口。首版使用**固定命令名 + 一个严格 JSON 对象**，不再设计 shell quoting、批处理数组、条件表达式、JSON Patch 或可执行脚本。Human Console 使用相同文本；帮助提供可复制示例。

```text
create-goal {"title":"给同伴两份水","criteria":"同伴实际收到两份水","nextStep":"先去溪流补水","focus":true}
create-goal {"title":"确认水源","criteria":"记录此地有无可用水及依据","parent":"G1","linkReason":"决定补给路线","focus":true}
update-goal {"id":"G2","progress":"已确认溪流湾有水","revisit":null}
focus-goal {"id":"G2","via":["G1","G2"]}
show-goal {"id":"G1"}
```

JSON 对象只接受该操作声明的键；拒绝重复键、未知键、错类型、多余尾文本、非法 Unicode 和超过 4 层的嵌套。可用数组仅为 focus 的 ID 路径，至多 32 个不同 ID，不支持操作数组。未提供字段不变，可空正文显式 null 或空白表示清空；必填文本不能用 null/空白。解析不改变游戏状态，格式错误走现有记录式拒绝。

共享目标 parser 新增完整命令至多 8192 UTF-16 code units 的门槛，Human 与 LLM 都适用；当前 Console 尚无此共同限制，不能视为已实现。Completion 仍先执行自身传输检查；字段 scalar 上限与输入 UTF-16 上限分别提示，不能混称 token 限制。JSON 转义会占输入额度，即使各字段长度合法，整条超限仍拒绝，可采用更短编码或拆分非原子必要的编辑；不在 parser 悄悄裁字。被领域拒绝的原始提交照现有合同保存，这不等于建立宿主日志总大小上限。

null 只用于 focus.id 清焦点，以及 progress/nextStep/revisit 清正文；show.id、parent、via 等出现时必须是声明类型，不能传 null，目录查询用 `{}`。no-op 也先验证全部已提供字段，不能绕过无效或超长 reason。同名 reason 按语义限长：pause.reason 是挂起说明（240 scalar）；reopen/update 敏感修订理由及 link/update-link 理由为 160 scalar。

| 命令 | JSON 字段与原子范围 |
| --- | --- |
| create-goal | 必填 title、criteria；可选 progress、nextStep、revisit、parent、linkReason、focus（默认 false）。parent 与 linkReason 成对提供；只建一个 Open Goal，可原子创建一条父关联并聚焦 |
| update-goal | 必填 id，至少一个可编辑字段：title、criteria、progress、nextStep、revisit、pauseReason、endNote；另有 reason。字段状态限制按下文，禁止混入 state 或 focus |
| link-goal | parent、child、reason；生成新 Link ID，同一父子对已有边则拒绝 |
| update-goal-link | id（Link ID）、reason；只改现有边理由，不改端点 |
| unlink-goal | id（Link ID）；删一条边并原子修复 Focus |
| focus-goal | id（Goal ID 或 null）；非空时可带 via，省略则 [id]；null 表示清空且禁止 via |
| pause-goal | id、reason（挂起原因）；可选 revisit，省略保留，null 清空 |
| resume-goal | id；只 Paused → Open，不自动聚焦 |
| finish-goal | id、outcome（completed / abandoned）、note；原子完成状态、说明、结束序号及 Focus 回退 |
| reopen-goal | id、reason；原子记录旧终态并重开，不自动聚焦 |
| forget-goal | id；仅已结束且孤立的 Goal，身份不复用 |
| show-goal | 可选 id；有值展开一个目标，无字段的 `{}` 显示整理目录。不是任意深度递归查询 |

create 的所有结果一起验证：无效父 ID、字段超限、任何额度不足或新焦点非法时，不留下半建对象、不消耗身份。`focus:true` 无 parent 时取 [新 Goal]，有 parent 时取 [parent, 新 Goal]，不隐式继承当前 Focus 或猜父目标的祖先；更长来源可随后显式 focus。父目标可以是任意状态，来源状态如实显示。

update 对 Open/Paused 可改普通正文；pauseReason 仅 Paused 可改且不能清空，endNote 仅 Completed/Abandoned 可改且不能清空。已结束目标禁止其他正文字段更新。实际改变 title/criteria 或勘误 endNote 时，reason 必填且留一条敏感修订；reason 不是自动拼进 progress 的日志。普通文字不要求额外理由。参数齐全但值均相同的合法 update/focus/update-link 返回“未改变”，不追加敏感记录；仍记录一次成功交互。非法状态转换不会因此变成 no-op。

短回执给出分配的 Goal/Link ID、变更字段、必要的 Focus 前后路径、剩余额度或可纠正拒绝原因。不要原样复制全部编辑正文。拒绝不会撤销前一条已成功操作；跨操作没有隐式事务。重挂可先 link 后 unlink，额度满可先解绑，孤立目标仍合法；不需 batch/reparent 接口。

## 2. 身份、权限及持久边界

角色集合拥有单调递增 Goal 与 Link 计数，UI 使用 G1/L1 等局部 ID；完整身份是角色 ID 加局部 ID。绑定角色来自引擎的决策请求，不接受命令指定另一个角色。其他角色即使也有 G1，读取的也是当前角色自己的 G1；不能根据不存在/禁止访问的差别泄露别人的目标。

成功 Plan 在候选状态中分配 ID，记录新对象、关联、Focus、计数与交互材料。业务验证失败不安装成功编辑效果、不消耗 Goal/Link ID，按既有拒绝路径发布拒绝交互 E/S；宿主或持久化异常仍按原故障合同处理。重开和 forget 均不复用身份。使用 checked 递增，耗尽明确拒绝。原始 ToolCallId/OperationRecordId 表示一次操作，不兼任长期 Goal ID，也不额外承诺相同输入重发的全局去重。

Fact 保存完整已决定效果及冻结材料，Fold 按既有方式核验与应用，不重新请模型决定或重新分配身份。已发布 E、未发布 S 的恢复，只安装那次效果一次；校验可重用纯规则核对记录前后值，但不能重新执行控制器或用当前新策略产生另一份操作。原始输入、身份、typed 摘要、Changed、后态之间必须一致，不能相信调用方自报结果。

采集/警告规则等只看原世界事实；目标编辑不能制造产物、赠与、胜利或感知。目标及回执仅当前角色可见；其他角色的冻结观察不因私有编辑出现变化或无内容通知。首版目标操作不生成物理/交流经历或通用 Before/After 文本效果，以权威 typed 操作摘要及冻结目标快照表达变化；相同短回执投影供正常交付与日志修复使用。

首版为 survival / duo（包括 Human 和 LLM）启用目标能力，普通批处理与未启用能力路径保持原帮助/提示词。所有角色的目标集合在新游戏工厂初始化为空；持久状态挂在角色私有交互边界且独立于某个 CurrentContext。开启/关闭/终局、handoff、拒绝和自然感知等现有 wrapper 重建路径必须保留目标集合及计数，不能只修改目标编辑分支。不恢复旧开发存档，按现有 State/Event 修订路径提升游戏修订和相关 Schema；不能只在 UI 初始化第二份目标状态。

## 3. 交互元数据与世界时间

[CycleInteractionRules](../../../src/ResourceCycle/CycleInteractionRules.cs)、[CycleActions](../../../src/ResourceCycle/CycleActions.cs) 和 [CycleFactApplication](../../../src/ResourceCycle/CycleFactApplication.cs) 已为 handoff 提供所需参照：

| 情况 | 目标/物理状态 | 交互和服务 |
| --- | --- | --- |
| 成功编辑 | 原子应用目标变化；物理状态不变 | 保存 E/S、原始身份和结果；递增决策/操作序号，更新 LastServedActorId；Context 保持 Open |
| 合法 no-op / show | 目标及物理状态不变 | 同样是记录式成功交互，按当前调度合同轮转其他待服务角色 |
| 拒绝 | 无目标部分更新、无物理效果 | 保存拒绝及身份，递增决策/操作序号；不更新服务游标 |

因此 show 是**目标与世界只读、交互有记录**的查询。纯详情投影函数无副作用，但提交 show 不等于无 E/S 写入。这样下一次独立模型调用、S 冷开和 E-only 恢复都能获得同一次查询材料，不需要额外本地 tool-loop。

所有目标操作只在角色已有合法输入机会时可用，不能主动唤醒正忙且无感知的角色。在移动中的输入机会也允许；须扩展当前 travelling 白名单，不能因此让移动可取消。编辑/show 不推进 ModelTime、不释放模拟、不清除身体/通信/资源待响应警告、不改变活动期限、库存或随机流；其他角色可在同 tick 获得轮流输入，不等于自动推进时间。

沿用宿主调用/连续协议失败预算与 Kernel 同刻转换护栏，不把瞬时目标操作算作通信发送额度，也不因 show、拒绝或 no-op 返还预算。宿主调用计数与角色业务状态是不同层次，不将现有每进程预算夸大为跨冷开持久预算；Kernel 的既有同刻护栏仍按当前恢复合同工作。

## 4. 具体施工位置与边界

| 现有入口 | 必需接线 |
| --- | --- |
| [CycleCommandParser](../../../src/ResourceCycle/CycleCommandParser.cs)、[CycleEngine](../../../src/ResourceCycle/CycleEngine.cs) | 严格语法、角色权威校验、目标交互分派；按操作形状组织代码，不无限增加互斥可选参数 |
| [CharacterInteractionState](../../../src/ResourceCycle/CharacterContext.cs) 与应用工厂 | 独立目标集合/计数及不可变材料，创建/冷开/新 Context 均一致 |
| CycleFact / CycleFactApplication / CycleInteractionValidation | 目标替换与操作摘要、状态/容量/Focus/世界不变校验；pending 恢复只用记录效果 |
| [CycleOperationResults](../../../src/ResourceCycle/CycleOperationResults.cs)、[ReadCommittedOperations](../../../src/ResourceCycle/Persistence/CycleDurableHistory.cs) | 为没有 ObservedEvents 的成功目标操作生成同源短回执；当前 survival 分支只接纳 handoff/continue，须扩展并验证日志冷开补录 |
| [CycleObservation](../../../src/ResourceCycle/CycleObservation.cs)、观察相等比较与冻结验证 | 只观察本人目标，快照不原地变异，覆盖无变化和其他角色隔离 |
| [CharacterContextRenderer](../../../src/ResourceCycle.Cli/CharacterContextRenderer.cs)、布局与 delta | 实现目标单次当前投影、有限回执及最近有效 show；见容量文档，不把视图窗口当存储回收 |
| [LlmCharacterController](../../../src/ResourceCycle.Completion/LlmCharacterController.cs) | 继续单 act；能力可用时替换长期 handoff 提示，保留实际系统提示词和模型材料证据 |

局部目标数据与规则不依赖具体地图/植物/角色名，先在应用中形成边界；本轮不为此增加 Kernel 概念、通用规划器或新的公共程序集。生产类如何拆文件由实现者决定；原子性、恢复、权限及材料合同不能因文件组织改变。
