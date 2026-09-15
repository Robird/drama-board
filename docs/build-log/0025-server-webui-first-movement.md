# Build Log 0025：Server、双页面与首个移动闭环

**状态：已按用户启动的 Goal 实施，A1—A8 验收通过。详见 [实施验收与结果包](0025-verification.md)。以下方案正文保留原设计时态，当前事实以源码及验收记录为准。**

**日期：2026-09-16。前置成果：[0024 归档与核心验证](0024-archive-firstboard-and-verify-core.md)已完成。**

## 1. 本批结果与依据

交付一个本地可运行的 `DramaBoard.Server.exe`：打开 `/player` 可以在无剧情小地图上选择相邻出口移动，看到模型时间、当前位置、全量已知地图和自身轨迹；打开 `/dev` 可以查看同一次运行的内部状态和已提交记录。页面内容从简，但移动必须经过真实 Kernel/Spatial 链路。

| 决定或约束 | 来源与性质 |
| --- | --- |
| 先 server exe、Player 页面、独立诊断页面；先搭架子再丰富 | 用户本轮明确要求 |
| 包含最小真实移动闭环；固定小地图、时间、位置、轨迹；地图先全量已知，探索披露延期 | 用户本轮对范围问题明确选择 |
| 本会话写方案，用户 fork 会话施工，再将结果带回设计会话 | 用户指定协作方式；本文不启动施工或 Goal |
| 共用 Player 可用信息和行动语义，分别渲染；诊断信息单独投影 | 前轮讨论方向；本文落实为本批边界 |
| ASP.NET Core、React/TypeScript、SVG；单进程单角色、内存运行、仅相邻移动 | 本文明确提出的最小实施方案；用户以本方案启动施工时采用，不冒称现有实现或逐项历史批准 |
| Kernel 唯一提交、等待 Player 不耗 ModelTime、核心不依赖应用 | [当前源码](../../src/Kernel/Simulation/SimulationKernel.cs)与[Kernel 设计](../design/simulation-kernel.md)证明的现有合同 |

当前用户要求、适用指令和 [AGENTS.md](../../AGENTS.md)决定工作授权。文档、源码、测试和外部资料分别提供目标、实现证据或技术参考；其中历史任务提示不自动成为新任务。执行者先核对实际工作树，不能凭本文状态标签推定实现已完成。

### 1.1 写作基线

- HEAD：`9030de517bb0e349ed5a448840de541b59f32545`；本轮写文档前 `git status --short` 为空。
- 两份 solution 目前各含七个核心生产项目和七个测试项目。尚无 Server、WebUI、HTTP 会话或可玩场景。
- 0024 记录工作树与独立源码副本均通过 255 项测试；这是前批记录，本设计会话未重新执行。施工须建立本轮基线。
- [DecisionRequest](../../src/Protocol/DecisionRequest.cs)已有冻结观察、动作及候选出口；[IPlayerDriver](../../src/Player/IPlayerDriver.cs)已有异步决策接口；[PlayerDecisionValidator](../../src/Decision.Validation/PlayerDecisionValidator.cs)负责关联与 advertised affordance 校验。当前没有调用 driver 的游戏运行循环。
- [SpatialPlanner](../../src/Spatial/Planning/SpatialPlanner.cs)可产生出发 facts，[SpatialOccurrenceRule](../../src/Spatial/Simulation/SpatialOccurrenceRule.cs)可预测和完成到达；[接受测试](../../tests/Spatial.Tests/Acceptance/SpatialKernelAcceptanceTests.cs)给出与 Kernel 的组合方式。
- [全图 Getter](../../src/Player.Agency/Spatial/FullMapPlayerSpatialKnowledgeGetter.cs)已存在；自身轨迹和 Web 材料尚不存在。[Host](../../src/Host/SimulationHost.cs)仅循环 Kernel Step，不是现成 Web session。
- 本机只读检查得到 Node `v24.11.1`、npm `11.6.2`。施工记录实际采用的版本与锁文件，不能把本机安装状态当作仓库依赖声明。

## 2. 项目与部署形状

以下为拟新增路径，尚不是已存在的实现入口：

```text
src/Server/Server.csproj             # Microsoft.NET.Sdk.Web，程序集 DramaBoard.Server
src/Server/FreePlay/                 # 小场景、规则组合、运行会话、Player 材料
src/Server/Web/                      # HTTP DTO、映射、端点
src/WebUI/                          # React + TypeScript + Vite，单个 npm package
tests/Server.Tests/                  # 场景、会话和真实 HTTP 边界验证
src/WebUI/e2e/                       # 少量真实浏览器验收
scripts/Publish-Server.ps1           # 显式构建前端，再发布 server 到本地 artifacts
```

- `Server` 直接依赖实际使用的 `Kernel`、`Spatial`、`Protocol`、`Player`、`Decision.Validation`、`Player.Agency`；本批运行循环需要逐次观察提交，可直接调用 Kernel，不为凑依赖而引用 `Host`。
- 小场景先保持 app-local，通过目录和类型分清场景、会话与 HTTP。暂不新增 FreePlay 类库、通用 WebHost 框架、共享 DTO 程序集或第二个服务。
- 七个核心项目的引用、领域行为、协议 public surface 和 DurableGraph schema 不变。新增 Server 与 Server.Tests 到两份 solution，使其同为 16 个 .NET 项目；WebUI 由 npm 构建。
- [核心引用守卫](../../tests/Kernel.Tests/ProjectDependencyGuardTests.cs)继续检验七核心，不使核心测试依赖 Server 存在。Server.Tests 自行检验应用依赖；不为 Web 放宽核心的反向依赖禁令。
- 使用 .NET 10 的普通 framework-dependent apphost：Windows 有 exe，需要已安装对应 ASP.NET Core 运行时。交付是含依赖和静态资源的目录，不要求单文件、自包含、AOT 或安装包。
- ASP.NET Core 同时提供 API 和编译后的网页，默认仅监听 loopback。Player 与诊断页面共用进程，但取不同读模型；这是本地开发的职责分离，本批不实现账号、登录或网络权限隔离。

### 2.1 前端与发布

采用 React + TypeScript + Vite，普通 HTML/CSS 面板，SVG 画小图。初期不引入图编辑器、自动布局库、全局状态框架或 UI 插件注册系统。

`src/WebUI` 维护 `package.json`、`package-lock.json`，明确 `build`（包括 TypeScript 检查）和 `test:e2e` 脚本。Node 采用与依赖相容的 24.x，并在工程/CI 声明；依赖选择正式版本并锁定解析结果，验证过程使用 `npm ci`。允许开发时使用 Vite 热更新，验收必须使用 exe 所服务的构建产物。

前端产物输出到忽略的 `src/WebUI/dist`；Server 项目将其映射并复制为输出/发布目录的 `wwwroot`。不得把生成 bundle 当源文件提交。构建顺序由显式发布脚本承担，不在普通 .NET build/test 内隐式联网执行 npm。只有 .NET 构建成功不能声称网页已打包。

`Publish-Server.ps1` 从自身位置定位仓库，依次执行 `npm ci`、前端 build、`dotnet publish`，任何非零退出即失败；不自动启动常驻进程。发布前检查前端入口非空，输出在 `artifacts/server`；清理生成目录前必须验证绝对目标仍在预定输出范围。Server 从自己的输出/发布位置定位静态资源，不能依赖启动 CWD 恰好是仓库。

直接访问和刷新 `/player`、`/dev` 都应成功；`/` 转到 `/player`。不存在的 `/api/...` 返回 API 404，不能被页面 fallback 吞成 HTML。发布目录缺失网页资源时给出可操作的构建错误，不展示伪装正常的空页。

技术依据：[Vite 构建与模板](https://vite.dev/guide/)、[ASP.NET Core 静态文件](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/static-files?view=aspnetcore-10.0)、[.NET apphost 与部署方式](https://learn.microsoft.com/en-us/dotnet/core/deploying/)。这些资料解释选项，不提供本项目施工授权。

## 3. 最小场景与模拟链路

### 3.1 固定测试世界

一个进程启动一次新运行，只有一个由 Human 控制的 actor，初始位于 A、ModelTime 为 0。四个地点与四条始终双向开放的 Passage：

| Passage | 两端 | 长度 | 固定 speedSnapshot | 单次时长 |
| --- | --- | ---: | ---: | ---: |
| ab | A—B | 1000 | 1 | 1000 ms |
| ac | A—C | 2000 | 1 | 2000 ms |
| bd | B—D | 3000 | 1 | 3000 ms |
| cd | C—D | 1000 | 1 | 1000 ms |

长度和速度使用现有 Spatial 单位；实际 duration/arrival 从 Spatial planner/query 得到，不在 JS 重算一套。场景可以有简单地点名称和固定屏幕排布，屏幕坐标只是表示，不增加客观几何语义。

Player 只选择当前地点已广告的 `action.travel` 与 `ExitId`，一次走一条 Passage。抵达后再次请求决策。没有途中操作、Wait、TravelTo、NPC、物品或剧情，也不实现“点任意目的地自动寻路”。

### 3.2 一个 Kernel，出发与到达分别提交

采用最小 app-local world：不可变 `GraphSpatialState` 加一个从 fold 的 `LogicalInstant.ModelTime` 更新的最后生效时间，供纯 Forecast 在地点上产生下一次决策候选。该字段只能从 Kernel 提供的 instant 派生，不能由 HTTP/墙钟推进；完成边界应与 Kernel 当前时间一致。

使用一个 `SimulationKernel` 和一个 `InMemoryOccurrenceHistory`；本批 facts 可直接使用 `GraphSpatialFact`，不必创建新的 durable 类型。通过小型 candidate 包装组合两类 rule：

1. **地点决策 rule**：actor 在地点上时，从 committed world 生成一个立即到期候选；key 从固定规则身份、actor 与运动 generation 等客观状态推导。只有 winner 的 `PlanSelectedAsync` 创建决策材料并调用 `IPlayerDriver.DecideAsync`。
2. **Spatial rule adapter**：将现有 `SpatialOccurrenceRule` 的候选与计划结果接入同一 Kernel；包装/解包保留原 key、due 与 data，不重新生成到达身份。actor 在 Passage 上时没有本场景的决策候选，由既有 arrival law 完成到达。

Human 的合法 `Travel` 由 owning rule 调用 `SpatialPlanner.TryStartTraversal`，将完整出发 facts 返回 Kernel；组合 fold 调用 `GraphSpatialReducer` 并更新派生时间，最后执行完整 Spatial 校验。HTTP 不直接调用 reducer、安装 World 或递增版本。

运行循环串行执行 Step：出发提交 → 全量重新 Forecast → 到达提交 → 下一地点决策等待。ModelTime 可直接跳到下一 occurrence；不等同于播放动画的墙钟。稳定的地点画面允许直接从 A 切到 B，内部仍必须有两次独立提交。

本批调用 `StepAsync(new ModelTime(long.MaxValue), applicationToken)`，每次 `Committed` 后发布完整快照再进入下一 Step；该上界允许选择未来到达，并不把当前时间设为上界。不能用 `CurrentModelTime` 持续限制 Step，否则出发后的 arrival 会被边界挡住。`Exhausted` 或 `BoundaryReached` 都须停止循环并报告；在本固定连通场景中属于意外终态，转为 faulted，不原样忙循环。

候选身份在 Player 回答前确定，不能包含点击顺序、所选出口或随机运行标识来影响仲裁。移动后客观状态必须使候选取得进展，遵守 Kernel 的同 key 进展规则。

### 3.3 会话与 Human driver

Server 持有一个应用会话、一个运行循环和一个 Web Human driver。`RunId` 每次启动重新生成，只标识进程内这次游戏，不是账户。`DecisionId` 应包含运行身份并在每个新决策点更新；网页刷新复用仍挂起的决策，不能重新发号或推进世界。

- driver 等待外部回答；提交端将合法答案交给当前唯一 waiter。可用异步 completion source，但不把它放进 World 或历史。
- 校验与占用 waiter 是一个原子操作，先验证再消费。两个页面同时回答同一请求，至多一个被接收。
- 发布时点明确为：请求挂起时发布 waiting；合法答案被占用时先发布 advancing，再唤醒 driver；每次完成提交后发布新世界材料。状态发布与 waiter 切换协调，不能由迟到的 HTTP 回调覆盖下一决策的快照。
- 使用现有 `PlayerDecisionValidator`，再做本场景严格形状校验与 owning rule 的 Spatial 计划校验。只有 `action.travel + ExitId` 有意义，额外参数、未知出口和未广告动作不能被忽略后执行。
- 预期非法输入不完成 waiter，不增加提交，不改 ModelTime；用户可对同一个请求重试。owner 在 Plan 阶段仍保有最终校验责任，不能假定所有未来 driver 都经过 HTTP。
- 等待 Human 期间 GET 能读取冻结材料；不能持有阻塞 GET/POST 的锁跨越 `await DecideAsync`。UI 快照按完整对象原子发布，不让 HTTP 拼接正在变化的 World、cursor 和 history。
- 浏览器取消请求、关闭页面或断网不取消游戏 waiter；后台会话寿命由应用 shutdown token 管理。长时间没有输入时保持待决，零 ModelTime 流逝，不自动 Wait 或代选出口。
- 应用停止时取消 waiter 并结束运行循环。不可预期的模拟故障使会话进入 faulted，保留最后完成的快照，停止接收动作，并在诊断面记录原因；不能静默重置为新游戏或继续制造成功结果。

这只是本场景的默认行为：在地点等待，在出发后由空间规则完成到达。本批不设计通用“默认动作集合”协议。

## 4. 两种读模型与 HTTP 边界

### 4.1 共用的 Player 决策材料

在 Server 的场景层产生与 React 无关的不可变 Player 材料：现有 `DecisionRequest`、已知图、当前位置说明和自身轨迹。HTTP 只映射该材料，未来 LLM renderer 可以消费同一来源。

当前 `IPlayerDriver` 参数本身还没有地图/轨迹字段。通过 app-local 材料提供者把当前冻结材料交给 Web adapter；不要把新增材料仅保存在浏览器，也不要宣称现有接口已自动解决所有未来 LLM 信息交付。本批保持核心协议不变，等真实 LLM consumer 出现再裁决通用化。

- **已知地图**：通过 `FullMapPlayerSpatialKnowledgeGetter` 显式提供完整静态图。Player 页标识为“已知地图（本场景全图已知）”；不模拟逐步探索。
- **当前位置与出口**：来自 committed Spatial 查询；可选出口与请求 affordance 一致。历史上走过不代表当前一定可选。
- **自身轨迹**：本批定义为起始地点以及每次已提交到达的顺序记录，至少有地点、抵达 ModelTime、经过的 Passage。只投影自己的记录，地图可据此标记走过的边。
- 轨迹从 genesis 和 `CompletedEvents` 中的自己的出发/到达事实投影。可以每次重新计算小列表，或做可重建缓存；不能从已接收的 Intent 预先追加，更不能依赖浏览器追加日志来维持完整性。
- `CompletedEvents` 本身是可增长历史的只读视图，不是冻结副本。在串行模拟路径上把轨迹/诊断列表投影为独立快照，再发布给 HTTP；不能将活列表或可变缓存直接挂进 DTO。
- 所有材料同属一个完成的世界前缀；读模型同时带运行身份和快照修订号。修订号只帮助 UI 识别新旧响应，不是第二个 WorldVersion 或游戏时钟。

### 4.2 端点与返回语义

采用普通 HTTP JSON 与串行短轮询，先不引入 SignalR、SSE、WebSocket。建议以下明确路由：

| 端点 | 本批语义 |
| --- | --- |
| `GET /api/player/view` | 返回 Player 快照：runId、viewRevision、模型时间、位置、knownMap、trajectory、当前 decision（等待输入时存在）、waiting/advancing/faulted 状态 |
| `POST /api/player/decisions` | 接收扁平 `{ decisionId, actionKind, exitId }`，映射成现有 PlayerDecision/Intent |
| `GET /api/dev/view` | 独立诊断快照：运行/会话状态、Kernel cursor/version、客观位置、最近完成记录的时间/cause key/fact kind、当前等待与最近拒绝/故障摘要 |

HTTP DTO 使用明确的 camelCase、字符串 ID 与数值；不直接序列化世界对象、DurableGraph 对象图或多态 facts。对未定义的输入字段拒绝，避免看似接受却未执行。类型命名、JSON 映射方式由执行者在上述合同内选择，保持端到端一致。

响应语义固定：合法答案交给 waiter 后返回 **202 Accepted**，只表示已接收；刷新快照看到提交结果才表示移动完成。格式错误/动作不合法返回 400，旧决策或已消费请求返回 409，会话故障/停止返回 503。失败 body 给稳定错误码与简短说明；Player 响应不带堆栈、候选集或 cause key。

POST 只回答当前 pending 请求，不提供修改时间、位置、调度器或任意 world state 的接口。旧 RunId 对应的 DecisionId 在新进程中也必须被拒绝。

### 4.3 页面内容

**`/player`**：时间/位置摘要、SVG 已知图、当前出口按钮及旅行时长、自身轨迹列表。能看懂当前在等输入、正在处理、连接失败还是会话故障；正在提交时禁用按钮，但服务端仍独立处理重复提交。文字首先清楚可读，地点标签和动作使用中文即可。

**`/dev`**：与 Player 页独立的页面组件与 API client，显示内部状态及最近提交/错误摘要，初期表格或格式化文本足够。只读；不增加重置、单步、改状态、暂停/继续控制按钮。显示运行身份，便于与 Player 页对齐。

两个页面可以共享布局组件和样式，Player 页不能请求诊断 API 或先拿完整内部数据再隐藏。诊断模式不是 Player 页的一个显示开关。生产场景虽全图已知，仍要测试两个 DTO 的信息边界，防止把内部调度数据混入 Player 材料。

浏览器只保存选中项、表单状态和展示偏好。首次加载、刷新和重新打开都从服务端取得完整快照；同一次运行的地图与轨迹不丢失。轮询只读，不调用 Kernel Step；不重叠发出无限 GET，不让旧响应覆盖新快照。无需动画、拖拽面板、响应式移动端打磨或成套设计系统。

## 5. 施工关卡

每关先核对当前事实，再实施最小闭包、验证行为、检查 diff。这里的文件/目录名是施工目标；不要把尚未创建的命令称为已有入口。

### G0：基线与构建入口

完整读 AGENTS、PROJECT-STATE 和本文；记录实际 HEAD、staged/unstaged/untracked 修改。使用既有固定包准备路径，串行 build/test 当前核心作为本轮基线，不查归档寻找旧会话整块复制。

新增 Server、WebUI、Server.Tests 的最小目录与构建配置；两份 solution 同步。确定并记录 Node/npm 与包锁，保持原固定 DurableGraph 包和 schema 不变。先证明 .NET 和前端分别能构建，默认 .NET 构建不依赖 Node 的隐式执行。

### G1：真实场景与决策会话

先闭合第 3 节：固定图、一个 actor、组合 rule/fold/history、异步 Human 等待。用已有 Scripted driver 或有界测试 driver 驱动 A→B→D，证明出发/到达都由 Kernel 提交，时间为 0→1000→4000，地点和记录一致。

验证等待输入不推进、同一决策只接收一次、非法答案不消费请求、应用取消能结束等待。不要为了方便测试把直接修改 Spatial state 当作运行期移动实现；genesis 构造与运行期提交必须区分。

### G2：HTTP 与双页面

完成第 4 节的冻结材料、轨迹投影、DTO 与 API，接上真实场景。HTTP 集成测试覆盖有效/非法/重复/旧运行决策，并验证查询在等待期间能返回。

Player 页实现四块最小内容；诊断页显示真实内部记录。刷新不丢运行状态，两页同时打开不产生第二个 actor/session，不重启模拟。验证 Player 请求和返回没有诊断数据。页面可先朴素，不能用写死位置/时间的示例数据代替真实闭环。

### G3：exe、浏览器与工程收口

完成显式本地发布脚本、静态资源复制、启动说明及 CI 前端构建。CI 保留当前固定包准备与 .NET 验证，增加 Node/npm 构建和同一套小型 Chromium 验收；不为触发 CI 自行 push。新测试依赖只加入相应测试工程。

CI 在 .NET 收集网页资源之前完成前端构建，再执行 .NET build/test、本地发布和发布产物的 E2E；浏览器运行时依赖按测试工具的官方安装方式准备。共享已完成的构建输入即可，不为每个测试重装或重建整套依赖。

从新生成的发布目录启动实际 exe，使用非仓库 CWD；浏览器访问两页，执行 A→B→D、刷新、并看诊断记录对应更新。可以使用 [Playwright 的 webServer 支持](https://playwright.dev/docs/test-webserver)或等价的有生命周期管理的本地测试启动方式；测试不连接已有未知进程，不遗留后台服务。

最后串行完整 .NET 验证、前端 build 与真实浏览器检查；审计解决方案集合、核心依赖/schema、归档排除、文档和 diff。只修改本批所需 CI/ignore/导航；不用重新进行 0024 的全部归档/独立副本审计。

## 6. 完成合同

| 编号 | 关卡 | 必须给出的可观察证据 |
| --- | --- | --- |
| A1 | G0/G3 | 两份 solution 同含原七核心及新 Server/对应测试；原核心依赖与 schema 不变；前端锁文件可用于 npm ci |
| A2 | G1 | A→B→D 实际为四次出发/到达提交；在地点 B 为 1000 ms、D 为 4000 ms；浏览器/HTTP 没有写 World 捷径 |
| A3 | G1/G2 | 等待、反复 GET、刷新不改 ModelTime/提交数；合法输入一次接收；并发重复最多一个 202，其他为 409；非法输入不消费请求 |
| A4 | G2 | 旧 DecisionId（包括旧运行）拒绝；断开浏览器不取消会话；应用停止可结束 waiter；故障明确可见且停止接收动作 |
| A5 | G2/G3 | Player 页四块内容来自服务端；刷新保留当前地点、地图和轨迹；图中按钮和已广告出口一致，没有任意传送 |
| A6 | G2/G3 | 独立诊断页显示同一次运行的真实 cursor/记录；Player API/页面不读取诊断材料；GET 可在 pending 决策期间正常返回 |
| A7 | G3 | 发布目录含 exe 和网页资源；从其他 CWD 启动能直接加载/刷新两页；真实 Chromium 完成移动，页面无致命脚本错误，未知 API 仍是 404 |
| A8 | G3 | 本轮基线与最终检查分别记录；README 可照做启动；PROJECT-STATE 如实说明已实现边界；所有本批 diff 可解释，既有修改保留 |

测试重点是领域提交、并发/等待边界、刷新后的信息完整性和实际发布入口。不要堆纯组件快照测试、逐字段镜像测试或像素基线；也不能只看截图就声称移动经过 Kernel。时间等待测试优先使用可控制 waiter/同步点，避免靠长时间 sleep 猜测。

### 6.1 验证入口

以下现有命令用于建立基线及最终核心/Server 集成验证，依次执行并检查退出码：

```powershell
git status --short
git rev-parse HEAD
pwsh -File scripts/Prepare-DurableGraph.ps1
dotnet build DramaBoard.slnx -t:Rebuild -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
dotnet test DramaBoard.slnx --no-build --no-restore -m:1
dotnet build DramaBoard.Local.slnx --no-restore -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
git diff --check
```

下面是本批需要实现并实跑的入口合同，当前尚不存在：

```powershell
npm --prefix src/WebUI ci
npm --prefix src/WebUI run build
pwsh -File scripts/Publish-Server.ps1
npm --prefix src/WebUI run test:e2e
```

发布脚本可复用刚安装的依赖优化本地流程，但默认完整路径仍须可由锁文件重建。E2E 明确消费发布目录中的 Server 和静态文件，给出端口、进程退出和测试产物处理方式；不能只测 Vite dev server。实跑平台据实报告；Windows 本机通过加 CI 配置审查不等于 Linux 已实跑。

## 7. 阶段边界与交回设计会话

本批不做 LLM 接入、探索迷雾、Player 私有记忆、途中决策、默认策略框架、自动导航、NPC/剧情/物品、存档恢复、多会话/多人、账号、远程部署、商用画面。仅用内存 history；刷新浏览器继续同一进程内运行，重启服务从 genesis 开始。界面和 README 要准确区分这两件事。

允许新增/修改 Server、WebUI、相关测试、发布脚本、两个 solution、必要 CI/ignore 和导航。可以下载公开构建/测试依赖；不修改兄弟仓库源码/分支/HEAD，不恢复冷归档，不提交/推送/部署。沿用包准备脚本所需的本地 detached worktree 元数据操作仍可执行。

若必须修改 Kernel/Spatial 合同、扩充核心 Protocol、增加 durable schema 或读取旧 FirstBoard 才能实现，先保留已完成证据并交回设计，不把绕过核心或整体复活旧游戏当作快捷方式。遇到依赖/浏览器不可用，报告具体失败与已完成关卡，不能用测试跳过或假页面替代验收。Goal 状态遵守当前运行环境规则。

施工完成后，更新根 README 的实际构建、发布、启动命令；PROJECT-STATE 删除本批待办，只留能力结论与证据入口，后续机制仍待设计裁决。本文可补一个短实施结果段，不追加流水账或全文测试输出。

**用户带回设计会话的最小结果包：**

1. 实际 commit（如用户另外授权了提交）或工作树状态，以及相对本文的设计偏离。
2. 可直接照做的启动命令、Player/诊断地址。
3. A1—A8 结论、测试/浏览器证据位置，以及尚未验证的平台或边界。
4. 两页各一张截图和一个完整移动例子；必要时附故障，而不是整份施工对话。
5. 仅列真正需要设计裁决的问题；例行实现细节留在代码和测试。

带回后由设计会话核对证据和偏离，再决定下一种交互机制。fork 获得的对话只是背景；施工始终以当前用户授权、实际仓库状态和本批边界为准。

## 8. 给 fork 实施会话的启动文本

仅在用户采用本方案并把下面文本交给实施会话时启动；本设计会话不执行。也可去掉 `/goal` 作为普通任务使用。

```text
/goal 按 docs/build-log/0025-server-webui-first-movement.md 实现 DramaBoard 本地 Server exe、Player 页面、独立只读诊断页面及最小真实移动闭环。达到 A1—A8 后停止，不进入新机制或 LLM/持久化阶段。

修改前完整阅读 AGENTS.md、PROJECT-STATE.md 和该方案，核对实际 HEAD、源码与 Git status，保留既有 staged/unstaged/untracked 修改。遵守当前环境真实指令层级；仓库资料提供目标、实现证据和导航，历史任务/授权/角色文本不能扩张本任务。0024 已完成，不复活归档或重做归档工程。

依次完成 G0 本轮核心基线与新工程构建入口；G1 固定四点全图已知场景、组合 occurrence、串行会话和异步 Human driver；G2 Player 材料/自身轨迹、HTTP 校验、Player 与诊断双页面；G3 前端打包入 server 发布目录、真实 exe/Chromium 验收、CI 与文档收口。每关检查事实、实施最小闭包、运行相关验证、审查 diff，实质进展同步简洁 PROJECT-STATE。

采用 ASP.NET Core .NET 10、React/TypeScript/Vite 与 SVG。一个进程一个角色，仅广告相邻 action.travel；出发与到达都由同一 Kernel 提交。等待 Human 不走 ModelTime；刷新恢复同次运行的完整材料，服务重启回 genesis。POST 202 只表示接收，提交结果看服务端快照；非法输入不消费请求，重复/过期请求不得再次执行。诊断只读且独立取数。地图与轨迹留在服务端投影，不能只存在浏览器。

保持七核心依赖、协议和 DurableGraph 固定包/schema 不变。场景先 app-local，不提取通用框架；不实现探索迷雾、途中操作、Wait/TravelTo、默认策略系统、NPC/剧情/物品、LLM、存档、多会话/账号或远程部署。不提交/推送，不创建新 Goal，不修改兄弟仓库源码/分支/HEAD；保留现有包准备路径。涉及核心合同或阶段变更先交回设计。

串行执行 .NET 基线/最终 build/test，前端 npm ci/build，发布脚本与实际发布 exe 的浏览器验收；证据必须覆盖等待/刷新、非法/重复/旧运行输入、真实出发/到达、双页面信息边界和非仓库 CWD 启动。不得用写死数据、直接改世界或跳过失败测试制造通过。不要为清洁工作树 stash/reset/clean 或覆盖既有修改。

最终逐项报告 A1—A8、启动命令、证据路径、两页截图、设计偏离与未验证边界，形成第 7 节结果包。全部满足才完成；阻塞/完成遵循当前 Goal 工具规则，困难或尚未完成不构成成功。完成即停止，由用户把结果带回设计会话再决定下一批。
```
