# 功能与实现设计

状态：入口与个人连接知识见[切片 A](../../experiments/character-navigation-a-acceptance.md)，map/path 与持久候选见[切片 B](../../experiments/character-navigation-b-acceptance.md)；导航执行待实施。要求来源见 [需求账本](README.md)。本篇为完整目标的规范性正文；尚未实施部分的命名不表示存在同名 API。

## 1. 用户体验与知识边界

角色在营地看到入口 e-c7「树林旁的小径」。执行 `go e-c7`，途中显示“从营地出发，正穿过树林旁的小径，另一端未知”，到达溪边后才确认该连接。只去过营地和溪边、但没有走过这条通道，不足以确认它们相连。

同一地点可有多个入口通往同一目的地，每条路径必须保留具体入口，不能只保存节点列表。图中绕路、环和不连通区域均合法。

到过地点、已见入口、已确认连接是三类知识语义，不要求三个独立存储集合：首版静态地图、到点可看见所有本地入口，已见入口可从已访问地点派生。**不能从已访问节点诱导出所有真实连接。**

首次出生视为观察所在地点；抵达时记录地点及本地入口。完整穿越后确认连接的另一端，进入和途中提醒不确认。跟随者抵达时获得同样的本人知识。说话、大喊、领队路线、宿主日志都不能自动增加地图知识。

完整走通 A→B 后知道两个端点属于同一通道。寻路接收有向邻接，不因“通道相连”自行添加反向边。当前 Playground 内容构建固定双向可通；首版保留这个规则，抵达 B 并观察本端后两方向均可寻路。无需为此新增单向道路配置或方向认知状态；有向邻接的正确性由纯搜索夹具验证，生产单向道路按真实内容需求重开。本端入口的固定成本在当地可见。

## 2. 入口身份和地图内容

### 2.1 内容模型

在 Playground **唯一的通道定义**两端分别附稳定的 `EntranceId`、本端显示名称/描述，由 `GetEntrances(place)` 一类纯投影派生本端入口视图；不另建第二份持久入口拓扑表。内部绑定 `(PlaceId, PassageId)`；真实 PassageId 继续用于 Spatial。两端 EntranceId 独立、不复用，不从可见字符串暴露 PassageId、对端 PlaceId 或共同配对前缀。不使用 `passageId + A/B` 这类可拆分编码，也不在运行时随机生成别名。字段平铺或端点值对象由实施决定。

首版优先在保存的内容定义中显式配置不透明入口 ID；校验唯一性、端点归属和内部映射完整性。地图本身已存在于内容定义，不额外维护一个宿主别名表。名称不是命令身份，重名可用入口 ID 消歧。

`go <EntranceId>` 始终是角色命令，即使走通后也不切换参数体系；走通后可以附加通道真名与已知目的地。内部 PassageId 无需为了“真名”而重新暴露；真名用单独的人类可读名称或已有适合的内容字段表示。世界内容若要通过路牌透露地名，属于未来明确的发现机制，本阶段系统文案不得直接背书未确认的连接。

**随仓内容修订属于首版交付。** 当前 `PlaygroundExpeditionMap` 的地点描述和入口描述都存在明示邻接；将其改为本地感官线索，确认后的目的地从结构化知识另行显示。不阻止角色根据线索猜测，不开发自然语言脱敏、污点或内容推理框架。

### 2.2 必须审计的输出面

入口隐藏不能只改地图列表。地点/入口描述、共享观察、Journey、可执行命令目录、parser 格式化、行动结果、LastResult、PendingAdvance、可见事件/effect 的 subject/key/detail、背景提示、两种 Console 布局、LLM 工具描述和角色 history 的人物材料均须同改。

未知/不在本地的入口与原始 PassageId 猜测提交统一按“当前没有可用的这个入口”处理，不通过错误差别披露远方存在性。宿主原始事实和诊断可保存真实 PassageId，但不能回灌角色材料。用户自己输入的自由文本允许原样留在操作记录；系统不得给猜测背书或追加隐藏信息。

## 3. 角色空间知识与投影

知识是持久领域状态，语义上归角色，存于同一 Business 根。首版优先复用 `PlaygroundExpedition.Visits` 的 `(ActorId, PlaceId)` 权威记录，并为每个角色补充已确认通道集合；不要在 Character 再复制一份独立的 visited 集合。可用角色视图封装存放位置；不为“挂在角色上”重排整个根。

访问记录中的资源是历史快照；已知静态地名/入口可读取固定定义。新连接只能由完整实际穿越产生，记录在到达事实中，不能在 renderer、Fold、反序列化或读地图时补发现。

已知地图视图只枚举已访问节点与已确认、且该方向已知可用的边；内部读取完整定义但对算法只暴露过滤后的邻接和合法成本。未知入口可以在地图中显示“去向未知”，不能作为连到未知节点的可寻路边。

首版沿用静态拓扑知识，不建版本向量、传闻置信度或认知事件溯源。若今后道路会变化，增加最后已知状态及更新事件；不得用当前远方真相默默刷新旧知识。

冻结观察包含本人已知地图、候选和活动导航材料。默认界面应自足：给出已知地点目录/连接概览、当前入口、候选路径 ID、导航终点/进度/下一段及本次 continue 的含义；`map` 展开完整已知图。两种 Console 布局和 LLM 都从同一材料投影，不能只让 Human 查图或依赖已被裁剪的旧回执找回路径 ID。小地图先保证信息完整，不预建分页、摘要或缓存；增量呈现沿用现有冻结材料机制。

## 4. 查询与寻路

拟议普通角色命令（两类控制器均可用，不是 Console 专属元命令）：

```text
map
path <目标地点ID> [from <起点地点ID>]
navigate <路径ID>
continue
cancel
go <本地入口ID>
```

地点 ID 必须已知且内容配置不含空白；名称在输出中并列，首版不实现模糊匹配。省略起点仅在实际位于地点时有效；途中可以显式指定已知起点做规划，但不能启动另一趟导航。所有途中命令仅在已有提醒等原因形成的合法决策窗口内执行；不增加随时输入、后台 Console reader 或新的唤醒机制。

`map` 输出全体已知地点、已确认连接及未知出口；不改变知识、不推进时间、不确认提醒。`map/path` 采用类似现有目标查询的普通零耗时操作回执，沿用成功操作的服务轮转规则，不等同于 Console 元命令；UI 的纯重绘仍保持纯读取。新查询事实不能顺手刷新他人 Visits/资源快照，当前 `Planning.Observe` 与事实校验中的例外须一致调整。

`path` 在已知子图运行 A*，首版 `h=0`；最小化各段公开固定行程 ticks，所有加法检查溢出。实现以入口 Ordinal 遍历与队列入队序号稳定处理同成本，等成本不替换先发现路线；使用 Int128 累计，不让超范围支路遮蔽可表示的路线。最短路线仍超出 long 时拒绝并保留候选，不误报无路。无需通用策略接口或额外图副本。找不到只说明“在已知地图中未找到路径”，不证明世界无路。

输出路径 ID、起终点、逐段入口及预计耗时；估计不包含确认停留、未来补给或他人干预，不保证途中存活。起终点相同返回零段路径，“规划起终点相同，无需移动”；只有真实位置相同才可称“已在目标地点”。启动零段路径仍检查真实起点，只给完成回执，不放行时间、不创造导航。

每角色最多一份候选路径。成功查询替换候选；合法但无路查询清空候选并返回无路，语法/未知地点拒绝不改变候选。ID 可复用现有角色/Context/OperationSequence 身份组合，不另建随机 ID 服务、图版本或哈希协议。解析只查当前 Business 根中该角色的候选槽，不跨角色或跨根查找；分叉继承的候选及 ID 继续有效。同一字符串在不同根各自解析，不承诺识别字符串复制自哪个分支，不引入领域 BranchId。

候选路径必须保存在可恢复领域状态，不能只在 CLI/LLM session 字典；其中保存输入、路径步骤和成本，冷开不偷偷重算出另一条路径。候选与活动导航共用一个不可变路径值类型；启动时活动计划持有选中的值，新查询只替换候选槽，不修改活动路线。不强制深复制、不新增 NavigationId；活动计划不得靠 ID 回查可被替换的候选槽。被替换的候选 ID 不再用于启动，明确说明失效，不自动改用最新候选。

## 5. 执行与控制节奏

### 5.1 最小权威状态

活动导航保存不可变路径和进度（例如已完成段数）。当前地点、正在通道及其期限仍由 Spatial 唯一保存；是否“途中/等待下一段”从空间位置及进度派生，不额外保存并行阶段枚举或导航计时器。

```text
navigate：当前位置=路径起点，空闲且未跟随 → 保存计划并原子启动第一段
arrive：结算本段、更新知识、前移进度 → 中间点等待确认；终点清除计划
continue：在中间点检查并启动下一段；途中只确认提醒、维持原段
cancel：清除剩余导航；若在途中，现有移动仍继续至本段终点
```

`navigate` 不接受同时已有活动导航、生产/采集/等待或跟随；先取消或解除对应关系。候选路径的起点必须等于真实当前位置。入口、本人及跟随队员是否可出发沿用普通 go 校验；失败不部分带队移动。

普通 go、navigate 首段、中间点 continue 共用“本次将出发的单段”解析、校验、风险预览与执行。当前 `ActionWarning` 仅识别 Go，必须调整，保留离开庇护和温暖耗尽等已实现的提示；警告仍不等于禁止行动。途中 continue 没有新出发段，不重复预览；不新增整条路线的生存模拟器。

到中间点后角色应处于需要决策状态；导航存在不意味着继续休眠。先结算同刻全部到达与更高优先级事件，再创建或更新观察。`continue` 在此既确认待响应提醒又尝试下一段；若出发失败，保持导航和提醒、返回本地原因，允许取消或稍后再试，不自动重规划。

有活动导航时，原地开启其他耗时活动、普通 go 或 follow 都要求先 cancel；map/path、允许的即时交互不隐式取消导航。无活动导航时 cancel 保持已有活动取消语义；首版通过互斥避免“取消哪个”的歧义。途中 cancel 只取消导航计划，不修改 movement generation/到期时间，也不自动确认已有警告。

导航取消不应无条件复用现有 `ActivityCancelled`：后者会在生存和通信规则中自动确认提醒。需有可区分的导航取消事实或同等明确的确认判据，同时保留普通活动取消行为。典型轨迹为“途中警告唤醒 → cancel 清导航但保留提醒与移动 → 可补给/map → continue 确认并关闭 Context → 到点停下”；不新增 Context 状态。中间点取消后则为原地空闲，需要另选耗时行动/跟随才能放行。

终局清除候选与活动导航，不再派发下一段；历史知识与可见经历保留。导航取消后的本段到达仍更新知识，但不再更新已移除计划。续局不重新执行已提交的出发。

### 5.2 跟随

领队每段通过现有队伍移动入口提交，跟随者不复制活动导航。不要求队员预先知道路线；所有队员实际抵达各自更新知识。到达中间点只有领队因导航待确认获得通常决策，队员沿用跟随休眠/提醒规则。同刻逐实体到达中间提交可恢复，全部到达之前禁止下一段控制器动作。

领队取消剩余导航不自动解散跟随，终局沿用既有关系清理。不能用导航绕过原有跟随者动作限制。

## 6. 层级、提交与已有机制

| 职责 | 放置与接缝 |
| --- | --- |
| 客观图、单段出发/到达 | Spatial，继续使用真实 PassageId；不加入角色知识或路线缓存。 |
| 入口内容、认知、寻路、导航策略 | Playground；纯过滤/搜索函数与既有规划编排，暂不抽独立框架。 |
| 命令、规则和事实 | PlaygroundCommandParser/Actions/Planning 与 Fact；普通 go、navigate、continue 共用单段出发逻辑。 |
| 观察、材料与体验 | 冻结 Observation/CharacterContext，再供 Console/Completion/history 消费；renderer 不查真实世界、不发现知识。 |
| 生命周期/持久/预取 | 既有 Kernel/Host/adapter；无新的导航线程、计时器或宿主 authoritative 字典。 |

到达时的空间变化、知识更新与导航进度变更在同一业务事实中计划并随同一发布根安装。E 保存已决定结果；pending 重放应用已记录变化，不重问控制器、不重新寻路/发现/分配 ID。纯查询不推进模型时间，不代表候选路径的持久写入可以跳过 E/S。

保留局部因果校验：新连接只能来自本人本次完整到达；进度只能由与当前路径段吻合的本人到达前移一次；map/path/拒绝/其他角色到达不能授予连接或推进本人的导航。验证已记录路径的步骤连通、已知性、成本及首末位置，不必在 Fold 重新求最短路。取消后到达不能重新构造已经清除的计划。重放读取记录不等于允许任意字段替换。

`NeedsDecision`、命令合法性、`ReleasesSimulation`、`Advance`、Opening.Ongoing、事实校验、观察相等性与可见变化检测必须一致调整。不能只增加 parser 分支。沿用单 Kernel 串行模型，无需并发锁或跨线程领域状态修改。

lookahead 中的候选路径/知识/导航只修改分支根；正式采用仍是采用控制器响应并通过主线命令处理，不合并分支世界。新增材料须进入完整输入比较，隐藏地图变化若未影响角色可见材料不应凭空使请求不同。ID 不依赖 wall-clock、全局静态计数或分支外缓存。

同版本 S 冷开、E-only/pending、Gym 分叉必须保留。允许提升当前 GameRevision、更新 schema/内容 fingerprint 并拒绝旧开发存档；不要增加本阶段无人需要的旧格式迁移。历史日志不原地重写，新角色材料不能继续泄漏。

## 7. 设计起点与后续源码入口

以下记录设计起点（`6cfcf5c`）的源码接缝，保留为改动依据；其中入口与连接知识问题已由[切片 A](../../experiments/character-navigation-a-acceptance.md)解决，不作为当前待办。求路与导航实施前仍须核对当前源码：

- [PlaygroundExpedition](../../../src/Playground/PlaygroundExpedition.cs)：Visits 按角色/地点保存；[Planning.Observe](../../../src/Playground/PlaygroundPlanning.cs) 在 Plan 记录本地资源快照。
- [Observation](../../../src/Playground/PlaygroundObservation.cs) 与 [JourneyObservation](../../../src/Playground/PlaygroundJourneyObservation.cs)：当前按目的地 Visits 显示名称，须改为连接知识判定。
- [WorldDefinition](../../../src/Playground/PlaygroundWorldDefinition.cs)：GetPassages 及保存内容定义；入口配置需覆盖地图工厂、内容校验、fingerprint 和实际配置消费者。
- [ExpeditionMap](../../../src/Playground/PlaygroundExpeditionMap.cs)：当前唯一通道定义附正反描述，部分地点/入口文案明示拓扑；WorldDefinition.BuildGraph 固定双向，新增入口仍附唯一通道定义。
- [Actions](../../../src/Playground/PlaygroundActions.cs)、[CommandParser](../../../src/Playground/PlaygroundCommandParser.cs)、[Planning](../../../src/Playground/PlaygroundPlanning.cs)：当前 go 接受 PassageId，出发回执直接包含 ID；continue 需已有待确认事项，cancel 只支持 Activity。
- [SurvivalRules](../../../src/Playground/PlaygroundSurvivalRules.cs) 的 ActionWarning 只认 Go；Enrich 与 [FactApplication](../../../src/Playground/PlaygroundFactApplication.cs) 对 ActivityCancelled 自动确认生存/通信提醒。
- [InteractionRules](../../../src/Playground/PlaygroundInteractionRules.cs)、[InteractionValidation](../../../src/Playground/PlaygroundInteractionValidation.cs)、[VisibleEndpointChanges](../../../src/Playground/PlaygroundVisibleEndpointChanges.cs)：放行、冻结材料及变化检测需闭合。
- [FollowingMovementTests](../../../tests/Playground.Tests/FollowingMovementTests.cs)、[FollowingRecoveryTests](../../../tests/Playground.Tests/FollowingRecoveryTests.cs)、[FollowingObservationTests](../../../tests/Playground.Tests/FollowingObservationTests.cs)：现有带队、恢复和个人知识约束的验证入口。
- FollowingObservationTests 的首个测试当前仅补充目的地 Visit 就断言已知终点；此次应改成明确连接知识夹具并新增旧规则不再成立的反例，不能将旧断言当成兼容要求。
- [PlaygroundLookaheadInputTests](../../../tests/Playground.Cli.Tests/PlaygroundLookaheadInputTests.cs)、[HumanSourceTests](../../../tests/Playground.Cli.Tests/PlaygroundLookaheadHumanSourceTests.cs)：完整输入与 Human 来源预取的回归入口。

## 8. 延期与重开条件

| 延期项 | 重开条件 |
| --- | --- |
| 地图分享、传闻、路牌、可见但不可达远处地点 | 出现明确的非亲历知识消费者，再扩充知识来源。 |
| 动态拓扑/远方封路记忆 | 实际游戏内容能改变道路，补充最后已知属性与当地更新；不预建版本体系。 |
| 生产内容的单向道路配置 | 出现单向道路玩法，接入已保留的有向邻接边界；当前无需扩充地图权限模型。 |
| 挂起/恢复、不停站导航、自动改道 | 逐站试玩证明干扰或调用成本过高，由用户选择旅行策略。 |
| 路径库、跨角色句柄、坐标启发式/路线缓存 | 一份候选确实妨碍使用或实测搜索性能不足。 |
| 途中转向、停车、制造 | 单段不可取消或活动互斥成为明确体验瓶颈。 |
| 通用地图知识模块/程序集 | 第二个真实游戏复用相同知识与导航规则。 |
