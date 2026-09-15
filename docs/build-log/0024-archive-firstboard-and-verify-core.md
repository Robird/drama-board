# Build Log 0024：归档旧实验与核心独立验证

> 状态：**设计与实施交接已撰写；归档及构建验收尚未执行**。
> 日期：2026-09-16。
> 撰写时源码基线：`d3d6cefc5b4faee652e12fd47d3ed5626b226efb`；工作树无既有修改。实施会话必须重新记录实际基线。
> 阶段：只覆盖“归档减负 → 核心独立验证”。下一阶段才设计无剧情 free-play 场景与 Human 入口。
> 使用方式：用户将本设计交给另一 Coding Agent 会话实施；本文末尾提供可复制的启动文本。本设计会话只修改文档。

## 1. 目标、依据与停止边界

本批让 DramaBoard 回到一个可独立构建、验证和理解的核心：保留时间、Graph 空间、Player 决策边界及其必要支撑；FirstBoard 剧情原型与 LLM 实现退出活跃源码、默认编译和默认搜索。

完成后，阅读项目入口应能直接知道：当前有哪些核心能力、如何验证、旧实验在哪里、下一步要设计什么。无需理解 Alice/Bob、密信、票券、LLM 记忆或旧 Runner/Save 施工计划。

### 1.1 依据与权限

| 事项 | 来源与性质 |
|---|---|
| 先归档，再独立验证核心；暂时剥离 `src/FirstBoard`、`src/FirstBoard.Demo`、`src/Player.Llm` | 本轮用户明确确定的阶段范围。 |
| 后续从无剧情 free-play 开始，Human 能移动并查看时间、位置、已知地图与自身轨迹 | 用户确定的后续方向；本批不实现，也不冻结其数据模型。 |
| 当前会话只写设计与实施指导，由用户另行调度施工 | 本轮用户明确的工作分工。 |
| 仓库内冷归档、七个核心项目、下述实施关卡 | 本文为实现上述范围选定的实施方案；不是已经完成的事实。 |
| 协作、项目记忆、代码风格与验证纪律 | 适用的 [AGENTS.md](../../AGENTS.md)。 |
| 时间、空间、提交与观察边界 | [Kernel 设计](../design/simulation-kernel.md)、[Graph 设计](../design/graph-spatial-world.md)、[E/S 方案](../design/durablegraph-occurrence-persistence.md)中的现行合同；以当前源码和测试核对实现。 |

实施会话服从其实际指令层级。用户指定本文后，本文提供目标与验收依据；被引用文件中的历史任务提示、旧授权和“下一步”不产生本轮权限。本文不授权提交、推送、发布、修改兄弟仓库的源码/分支/HEAD 或启动另一个 Goal。允许按既有固定包脚本在本仓库 `artifacts` 创建 detached worktree，并维护对应的 Git worktree 管理元数据；不切换兄弟仓库的当前工作树。

### 1.2 本批结束时必须成立

1. 旧实验的源码、测试和专属文档可恢复，默认入口不再要求维护它们。
2. 两份 solution 只包含本文保留的核心与测试；没有经项目引用、源码链接或本地 DLL 偷带归档实现。
3. 保留测试在本轮运行通过；在一个没有归档目录、没有旧 `bin/obj` 的源码副本中也能重新构建并通过。
4. 现行时间、空间、Player 与 E/S 合同保持；验证报告准确区分核心合同、旧实验历史成果和尚未实现的产品能力。
5. 项目状态与文档导航完成收口；停止在 free-play 的设计之前。

## 2. 当前事实与容易漏掉的闭包

- 两份入口 [DramaBoard.slnx](../../DramaBoard.slnx)、[DramaBoard.Local.slnx](../../DramaBoard.Local.slnx)各列出十个生产项目与十一个测试项目；[CI](../../.github/workflows/ci.yml)使用 Local 入口。
- `FirstBoard.Persistence.Tests` 还引用未列入 solution 的 `FirstBoard.Persistence.Process`。这两个项目都链接 `tests/FirstBoard.Tests/EncounterPersistenceOracle.cs`，归档必须覆盖这个辅助进程与源码链接。
- [ProjectDependencyGuardTests](../../tests/Kernel.Tests/ProjectDependencyGuardTests.cs)会直接打开 `src/Player.Llm/Player.Llm.csproj`。移走目录后，必须调整这项文件存在依赖；[AssemblyDependencyTests](../../tests/Kernel.Tests/AssemblyDependencyTests.cs)中的禁止依赖名称则仍有意义。
- [Protocol 的 JSON 测试](../../tests/Protocol.Tests/IntentJsonTests.cs)包含 `FirstBoardActions` 等名称，但没有引用 FirstBoard 项目；它证明的是保留协议的序列化行为，不能仅因名称而删除。
- 默认文本搜索由根目录 [.ignore](../../.ignore)控制，当前只排除 `docs/archive/legacy/`。本批沿用这个入口，不另建同义配置文件。
- Kernel、Spatial 仍引用固定 DurableGraph 包；[Directory.Build.props](../../Directory.Build.props)、[包准备脚本](../../scripts/Prepare-DurableGraph.ps1)和[包来源说明](../worksets/durablegraph-package-source.md)是保留构建路径的一部分。
- 实际落盘 adapter、场景绑定和冷进程续局验证在 FirstBoard 内。归档后保留的是 Kernel 的 [IOccurrenceHistory](../../src/Kernel/Journal/IOccurrenceHistory.cs)、[InMemoryOccurrenceHistory](../../src/Kernel/Journal/InMemoryOccurrenceHistory.cs)及其合同测试，不是一个已经可用的新游戏存档入口。
- [analyze_llm_runtime.py](../../scripts/analyze_llm_runtime.py)专门分析旧 Demo 的模型调用日志，也随实验归档；包准备脚本保留。

上述事实来自源码与配置阅读；本文撰写时未运行构建或测试。历史文档中的测试结果不能充当本批基线。

## 3. 选定设计

### 3.1 活跃核心固定为七个生产项目

| 保留项目 | 当前直接项目依赖 | 本批保留内容 |
|---|---|---|
| `Kernel` | 无 | 时间、全量 Forecast、单 winner、纯 fold、E/S 历史接缝与有限游标。 |
| `Spatial` | `Kernel` | Graph、客观位置与运动、导航、方向许可、arrival/contact 及其不变量。 |
| `Protocol` | 无 | 冻结的观察、决策请求、意图和稳定身份。 |
| `Player` | `Kernel`、`Protocol` | `IPlayerDriver` 与现有 Null/Scripted/Random 策略。 |
| `Decision.Validation` | `Protocol` | 请求关联、动作和候选目标校验。 |
| `Host` | `Kernel` | 串行驱动公开 Step 的薄循环。 |
| `Player.Agency` | `Spatial` | 静态已知图 Getter、精确子图快照与当前 FullMap 实现。 |

保留同名的七个 `tests/<项目名>.Tests` 项目。上表只列 ProjectReference，不表示禁止现有 DurableGraph PackageReference。两份 solution 保留相同的这十四个项目，不新增第三份 solution。

“最简”的本批判据是活跃闭包能够脱离旧实验工作。以下内容不随归档重做：

- 不合并或重命名核心程序集，不抽取通用 Game/Runner/Presentation 框架。
- 不裁剪 `Protocol` 的动作词汇、nullable 参数和 JSON 合同；它们仍有独立测试，下一场景再按真实需要裁决。允许把误导性的测试名改为协议名称，断言和用例保留。
- 不删除 Spatial 的 contact、Reverse、动态入口等既有能力；单 Player 首场景暂时不使用，不等于核心实现应被移走。
- 不删除 durable 声明、生成器配置或已提交的 Kernel/Spatial schema history，不升级包、不更换包来源、不借机简化上游打包脚本。
- 不把旧 `LiveSession`、`TravelGoal`、持久 adapter、记忆系统迁入核心来维持原 Demo。需要再次使用时，从新消费者的需求重新设计。

### 3.2 仓库内冷归档

采用统一目录 `archive/firstboard-llm/`，按原仓库相对路径保存内容，例如：

```text
archive/firstboard-llm/
    src/FirstBoard/...
    src/FirstBoard.Demo/...
    src/Player.Llm/...
    tests/FirstBoard.Tests/...
    tests/FirstBoard.Persistence.Tests/...
    tests/FirstBoard.Persistence.Process/...
    tests/FirstBoard.Demo.Tests/...
    tests/Player.Llm.Tests/...
    scripts/analyze_llm_runtime.py
    docs/...                         # 按 §3.3 的清单归档
    README.md                        # 修改前的根 README 快照，保留旧运行说明
    manifest.json                    # 实施时生成的归档清单
docs/archive/firstboard-llm.md         # 实施时建立的简短恢复入口
```

这些是本批拟创建路径，目前尚不存在。归档进入版本控制，不能通过 `.gitignore` 隐藏。给 `.ignore` 增加根路径限定的 `/archive/firstboard-llm/`；默认 `rg` 不遍历其正文，显式 `rg --no-ignore` 仍能查阅。活跃目录不保留源码副本、兼容转发项目或假壳。

`manifest.json` 只承担归档完整性证据：记录实际归档前 HEAD，以及每个需保留文件的原路径、归档路径、移动前后可核对的 SHA-256。不记录凭据、环境变量或机器私有配置。收录原有受 Git 管理的源码、项目、schema history、测试、文档，以及实施会话确认属于这些目录的未提交文件；生成输出不作为源码证据。

移动采用原样保留：不重排、不格式化、不修补归档内部的 C# 引用或历史 Markdown 链接。归档是历史资料，不承诺在当前目录直接构建。短索引说明原始路径映射、实际基线、归档前未提交内容是否存在，以及如何从独立的旧版本 checkout 恢复原构建上下文；存在未提交内容时，不能声称仅 checkout 该 HEAD 就恢复了全部归档内容。

不要求创建 Git 标签、分支或提交。若旧目录有 `bin/obj`、运行产物或不明来源文件，保留在冷归档或单独记录的隔离位置，不为获得空目录而递归删除。Windows 上每次目录移动前核实绝对源/目标位于本仓库与选定归档根内；目标冲突时停止，不覆盖已有归档。

### 3.3 文档分流

本批移动以下历史正文至 `archive/firstboard-llm/<原路径>`，实施前按实际文件逐项核对：

| 归档组 | 撰写基线中的范围 |
|---|---|
| 旧 build-log | `docs/build-log/` 中现存编号 0001—0020 的文件；不含本文 0024。0004 等已改名条目按下面的路径处理。 |
| 旧 Content/Save 目标 | `docs/implementation/game-content-save-boundary.md`。 |
| 已完成的准备与接入工作集 | `docs/worksets/pre-integration-cleanup.md`、`durablegraph-first-integration.md`。 |
| 已被接入结果替代的研究 | `docs/research/durablegraph-consumer-preflight.md`、`event-journal-state-store-draft.md`。 |
| 延期的 Script VM 研究 | `docs/research/persistent-script-vm-selection.md`；只归档资料，后续重开条件仍在项目状态保留一句。 |
| 根目录早期混合设计 | `docs/Design Note 001：戏剧棋盘——一种面向 AI Player 的角色化沙盒游戏.md`、`Design Note 004：叙事化长期材料——来源与信念分离.md`、`Design Note 005：分块 MemoryBank——不同稳定性的独立认知维护.md`、`Design Note 006：Scenario Definition、Instance 与 Run Manifest.md`。 |
| 根目录旧计划 | `docs/研发计划_001_架构基线与决策记录.md`、`研发计划_003_Kernel攻击性评审_2026-08-16.md`、`研发计划_004_Host落盘层攻击性评审_2026-08-17.md`、`研发计划_005_LLM_Player设计研究.md`。 |
| 根目录旧总体与玩法设计 | `docs/开放世界棋盘游戏设计_002_整体软件架构与技术栈.md`、`开放世界棋盘游戏设计_004_Providence与因果模板.md`、`开放世界棋盘游戏设计_006_伴生RPG与AI_Player体验设计.md`。 |

保留以下活跃依据，并只做必要的状态说明、导航与链接修正：

- `docs/design/` 的三份 Kernel/Graph/E/S 设计；明确哪些条款是核心合同，哪些是已归档 FirstBoard 的历史映射。
- `docs/implementation/kernel-occurrence-baseline.md`；保留其时间/调度证据，旧 Journal 条款仍由 E/S 方案替代。
- `docs/worksets/passage-contact-floor.md`；其中 Spatial 的 floor contact / ceil arrival 仍是当前语义，FirstBoard 部分改作归档证据引用。
- `docs/research/player-spatial-knowledge.md`；Getter 与精确子图仍有消费者和测试。探索披露、轨迹模型与 UI 不在本批裁决。
- `docs/worksets/durablegraph-package-source.md` 与 `docs/feedback/durablegraph/`；维持依赖来源和上游反馈入口，历史消费证据指向归档。反馈存在不代表本批要继续开发持久化。
- 既有 `docs/archive/` 资料与索引；本批不再次整理更老的 archive。

`README.md`、`docs/README.md`、`PROJECT-STATE.md` 与 `docs/archive/README.md` 是必须收口的入口。修改根 README 前原样复制到归档并纳入 manifest，保留旧 Demo 命令与续局说明；活跃 README 保留核心构建入口，并明确此阶段没有活跃可玩程序。根 README 因而是“保留原文快照后改写活跃入口”的特例，不按普通文档移动后删除。

活跃文档引用被移动材料时，改指归档实际位置或短索引，并标明其历史性质。只修复本次引入的断链；归档原文中既有的失效历史链接按原语境保留。默认搜索仍可命中短索引、本文清单或禁止依赖断言中的 `FirstBoard`/`Player.Llm` 字样，这不构成隔离失败。

### 3.4 核心语义不变量

本批只有结构性归档与必要的检查/导航适配，不改变：

1. 全量 Forecast、确定性单 winner、每次 Step 至多一个完整 Occurrence；等待决策不推进 ModelTime。
2. Game/策略输出不能绕过领域规划成为事实，Kernel 独占安装世界与推进游标。
3. 预检失败不发布 E；E 后完成 S；S 成功才安装世界；pending 恢复只完成已记录工作。
4. Spatial 独占客观位置、运动与导航；contact floor、arrival ceil 和方向许可规则不变。
5. Player 通过冻结的观察与请求获得信息，返回关联决策；Kernel/Spatial 不反向依赖 Player、Protocol 或具体游戏。

## 4. 验证证据范围

优先运行已有有效测试，不为搬文件复制一套相同测试，不把归档测试复制回核心以保持旧测试总数。

| 要保留的证据 | 现有入口 |
|---|---|
| 单 winner、全量重预测、取消、提交失败、pending 与 completed 恢复 | [SimulationKernelTests](../../tests/Kernel.Tests/Simulation/SimulationKernelTests.cs)、[InMemoryOccurrenceHistoryTests](../../tests/Kernel.Tests/Journal/InMemoryOccurrenceHistoryTests.cs)。 |
| 时间与因果顺序、身份、确定性随机和调度 | `tests/Kernel.Tests/Time/`、`Scheduling/`、`Random/`、`Simulation/CoreContractTests.cs`。 |
| Graph 查询/导航/运动及纯 Spatial 接入 Kernel | [SpatialKernelAcceptanceTests](../../tests/Spatial.Tests/Acceptance/SpatialKernelAcceptanceTests.cs)、[SpatialContactKernelAcceptanceTests](../../tests/Spatial.Tests/Acceptance/SpatialContactKernelAcceptanceTests.cs)，以及 Spatial 的定义、规划、查询和 contact 测试。 |
| Player 请求关联、策略、协议快照/JSON、动作校验 | `tests/Player.Tests/`、`Protocol.Tests/`、`Decision.Validation.Tests/`。 |
| 薄 Host 与已知图边界 | `tests/Host.Tests/`、`Player.Agency.Tests/`。 |
| 活跃项目依赖 | Kernel、Spatial、Player.Agency 的现有依赖 guard；必要时扩展为覆盖两份 solution 和全部保留项目。 |

若需要补测试，只补本次隔离暴露的实际缺口，例如保留项目引用归档、两个 solution 项目集合不一致。不能删除行为断言、跳过失败用例或放宽合法性规则来获得绿色结果。

核心验证不证明：新游戏可玩、Human UI 可用、已知图会随探索增长、轨迹可显示、实际磁盘恢复、LLM 能继续决策。它们分别属于后续场景与存储工作。

## 5. 实施关卡

### G0：核实输入与建立核心基线

- 完整阅读 AGENTS、PROJECT-STATE 与本文，再按表查看源码、项目和测试；不顺读旧剧情和全部历史设计。
- 记录实际 HEAD、`git status --short`、既有 staged/unstaged/untracked 修改；逐项生成归档路径与哈希清单。
- 核对七个保留项目的真实引用闭包，包括 ProjectReference、Compile Link、Reference/HintPath、导入的 props/targets 和测试中硬编码文件访问。
- 准备固定包，逐个运行七个核心测试项目，得到归档前的当前基线。无需调用模型、运行旧 Demo 或重验旧 FirstBoard 存档。
- 若核心原本就有失败，先区分缺包/环境问题与代码问题；保存失败证据。必要的环境修复可以继续，领域语义修复不并入归档。

通过条件：归档文件能保留、核心基线可解释、后续修改不会覆盖他人内容。与当前设计不符的新依赖需要先报告，不能把核心一并归档或复制旧游戏代码来掩盖依赖。

### G1：归档实现并切断默认编译

- 原样移动 §3.2 的八个源码/测试目录及 LLM 分析脚本，补全 manifest；归档前后逐文件哈希匹配。
- 两份 solution 同步收敛到七个生产项目与七个测试项目。
- 调整 `ProjectDependencyGuardTests` 中要求旧项目存在的条目，保留禁止核心依赖它的断言；必要时补齐活跃项目集合检查。
- 核对整个活跃闭包不含旧项目、归档源文件或归档 DLL。既有基于协议的测试继续保留。
- CI 继续使用其固定包与 Local 入口；只在本批确实需要时修改配置，不触发依赖升级。

通过条件：活跃构建图只含核心；旧源码完整归档；不存在靠跳过失败测试维持的假绿。

### G2：隔离搜索并收口文档

- 执行 §3.3 文档映射，同样记录并验证原文哈希；归档文档的旧链接不在原文内重写。
- 更新 `.ignore`，创建 `docs/archive/firstboard-llm.md` 短索引，修复活跃文件指向旧路径的链接。
- 更新 README、docs 索引和 PROJECT-STATE；AGENTS 中的历史检索说明只补充新归档入口，不改稳定协作原则。
- 默认 `rg --files` 不返回冷归档正文；显式 `rg --files --no-ignore archive/firstboard-llm` 能列出它们。
- 项目状态区分“核心仍有某合同”和“旧 FirstBoard 曾有某产品能力”，移除旧接入授权和已完成任务对当前施工的引导。

通过条件：新会话从活跃入口即可理解当前核心与验证方法；旧资料需要经明确的历史入口访问。

### G3：独立验证与交付

- 在工作树串行重建并运行全部保留测试；比较归档前后用例范围，解释改名或新增的结构性检查。
- 比较两份 solution 的规范化项目集合。CI 入口与标准入口都必须实际构建成功；相同项目集合的测试只需完整运行一遍。
- 制作包含本轮最终源码的独立临时副本：保留根构建配置、两个 solution、七个生产项目与七个测试项目及其非生成编译输入；不带 `archive/`、旧项目、`bin/obj` 或旧 DLL。
- 副本必须包含本轮尚未提交的新文件与修改，不能用 `git archive HEAD` 取得的旧树代替。按纳入副本的相对路径与文件哈希核对其等于最终活跃源码。
- 临时副本可以使用已经校验的固定包 feed；显式传入绝对 `DurableGraphPackageSource`。不能从旧工作树加载项目或本地编译 DLL。这个验证证明源码闭包独立，不声称离线获取包或重新发布包。
- 在该副本重新 restore/build/test 并保留输出证据；确认既有 schema history 未被重新接受或改写。
- 核对归档哈希、搜索结果、链接、集成 diff 与最终状态；将详细命令/TRX 留在 `artifacts/core-archive-validation/` 等忽略目录，交付说明概括证据，不只给临时日志路径。

通过条件：§6 的每项验收均有本轮证据。停止于核心独立；不创建 free-play 项目、Human driver、地图 UI、轨迹 schema 或新的落盘 adapter。

## 6. 完成验收表

| 编号 | 要求 | 关卡与可观察证据 |
|---|---|---|
| A1 | 旧内容可恢复 | G0/G1/G2：路径映射完整，归档前后 SHA-256 相同，索引记录实际 HEAD 与未提交内容边界。 |
| A2 | 默认编译隔离 | G1/G3：两份 solution 为同一十四项目集合，活跃引用闭包无归档输入，CI 入口构建成功。 |
| A3 | 默认搜索隔离 | G2：默认文件枚举不含归档正文，显式枚举能找到；短索引可见。 |
| A4 | 核心行为保留 | G0/G3：七个核心测试项目当前基线与最终结果可比较，无新增失败或未解释跳过。 |
| A5 | 独立源码可用 | G3：不含旧实现、归档与缓存输出的副本重新 restore/build/test 成功。 |
| A6 | 状态描述准确 | G2/G3：入口不再宣传活跃 FirstBoard/LLM/存档程序；核心 E/S 与历史落盘成果分开描述。 |
| A7 | 修改范围闭合 | G3：schema 与包版本不变，本轮改动均可解释，既有工作树修改保留，未进入新场景阶段。 |

全部成立才标记本批实施完成。归档导致总测试数减少是预期现象，保留测试的具体失败不能用历史总数或旧 FirstBoard 验收替代。

## 7. 验证命令与运行纪律

以下路径已按撰写基线核对，命令由实施会话实际运行。PowerShell 在仓库根执行；每个外部命令失败时先处理或报告，不继续把后续输出拼成成功结论。

### 7.1 准备与归档前核心基线

```powershell
git status --short
git rev-parse HEAD
pwsh -File scripts/Prepare-DurableGraph.ps1
```

固定包准备成功后，串行运行：

```powershell
$coreProjects = 'Kernel', 'Spatial', 'Protocol', 'Player', 'Decision.Validation', 'Host', 'Player.Agency'
foreach ($coreProject in $coreProjects) {
    dotnet test "tests/$coreProject.Tests/$coreProject.Tests.csproj" -m:1 -p:DurableGraphSchemaHistoryMode=Verify --logger trx --results-directory artifacts/core-archive-baseline
    if ($LASTEXITCODE -ne 0) { throw "Core baseline failed: $coreProject" }
}
```

### 7.2 归档后完整验证

```powershell
dotnet sln DramaBoard.slnx list
dotnet sln DramaBoard.Local.slnx list
dotnet build DramaBoard.slnx -t:Rebuild -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
dotnet test DramaBoard.slnx --no-build --no-restore -m:1 --logger trx --results-directory artifacts/core-archive-validation
dotnet build DramaBoard.Local.slnx --no-restore -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
git diff --check
git status --short
```

独立副本在其根目录运行同样的 build/test 命令；第一次 build 前用绝对包源执行 restore，后续 build/test 同样带入该属性或保持对应 restore 结果。包源通过本轮实际 feed 的绝对路径赋给 `$coreFeed`，不能使用未替换的示例路径：

```powershell
dotnet restore DramaBoard.slnx "-p:DurableGraphPackageSource=$coreFeed"
dotnet build DramaBoard.slnx --no-restore -t:Rebuild -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify "-p:DurableGraphPackageSource=$coreFeed"
dotnet test DramaBoard.slnx --no-build --no-restore -m:1 --logger trx --results-directory artifacts/core-archive-validation "-p:DurableGraphPackageSource=$coreFeed"
```

### 7.3 结构与搜索审计

```powershell
rg --files src tests -g '*.csproj'
rg -n 'ProjectReference|Compile Include|HintPath|Import Project' src tests -g '*.csproj' -g '*.props' -g '*.targets'
rg --files | Select-String '^archive[\\/]firstboard-llm[\\/]'
rg --files --no-ignore archive/firstboard-llm
```

引用审计结果逐项解释，不能只搜索三个项目名就宣称闭包成立。默认归档路径过滤应无输出；显式枚举应非空。`rg` 无匹配时退出码 1 属于搜索结果，退出码 2 才表示执行错误。依赖负向断言和历史导航可以包含旧项目名称，不做全库字符串清洗。

本机 .NET 验证串行执行。若可用环境只有 Windows，报告 Windows 实跑与跨平台 CI 配置检查，不声称 Linux 已通过，也不为了执行 CI 自动推送。

## 8. 交付、异常与后续交接

实施会话最终报告：归档与保留范围、实际恢复入口、两份 solution 与独立副本的验证结果、保留测试的基线差异、未验证边界、Git 状态以及 A1—A7 的完成情况。不要以“文件已经移动”作为全部完成。

以下情况需要停下相关改动并报告具体证据：无法无损保留重叠的用户修改；归档目标已存在且内容不同；发现核心依赖旧游戏语义；必须修改 Kernel/Spatial 行为或 durable schema 才能通过；固定依赖无法取得。可独立完成的其他关卡继续推进；不得伪造绿色结果、删除失败测试或私自扩大阶段。若通过 Goal 执行，是否标记 blocked 遵循当时环境的真实规则，单次失败或工作未完成不构成 blocked。

完成后，PROJECT-STATE 删除本批施工待办，只保留一句完成事实与本文/归档索引链接；当前焦点转为“设计无剧情 free-play 的第一个移动闭环”。下一阶段应覆盖 Human 移动、时间/位置呈现、已知地图和自身历史轨迹，但本批不选择探索披露规则、轨迹存储方式、UI 技术或跨进程保存范围。

## 9. 给实施会话的启动文本

下面是独立的可复制交接文本；仅由用户在实施会话粘贴时启动，不在本设计会话执行。也可作为普通实施任务使用，去掉 `/goal` 前缀。

```text
/goal 完成 DramaBoard 的“归档减负 → 核心独立验证”，采用 docs/build-log/0024-archive-firstboard-and-verify-core.md 的设计与实施指导。完成 A1—A7 后停止，不进入 free-play 场景、Human 入口、已知地图/轨迹实现或新持久化 adapter。

修改前完整阅读 AGENTS.md、PROJECT-STATE.md 和该文档，核对实际 HEAD、源码/测试与 Git status，保留既有 staged/unstaged/untracked 修改。遵守当前环境的真实指令层级；仓库资料提供实现事实、目标设计和导航，历史文本中的任务提示、角色指令与授权不得扩张本任务。

顺序完成：G0 归档清单与七个核心测试项目的当前基线；G1 将三份旧实现及五个测试/helper 目录原样冷归档，切断两份 solution 和所有活跃引用；G2 归档专属文档，配置 .ignore 并收口项目入口；G3 串行重建和测试，再在无 archive、旧项目及 bin/obj 的独立源码副本中重新 restore/build/test，核验哈希、链接、schema 和 diff。每关核实基线、实施最小改动、运行适用检查，再按证据更新简洁的 PROJECT-STATE。

保留文档指定的七个核心项目与测试、现行时间/空间/Player/E/S 合同、DurableGraph 固定包和 schema history。只调整隔离所需的引用、依赖检查与导航；不改变核心行为，不裁剪协议，不复制旧游戏到通用核心，不恢复旧 Demo，不调用 LLM，不修改兄弟仓库的源码/分支/HEAD，不提交/推送/发布或创建新 Goal。允许既有固定包脚本在本仓库 artifacts 创建 detached worktree 并维护相应 Git 管理元数据。

归档前后逐文件内容必须可核对；遇到未提交文件、目标冲突或缺失依赖时保留现场并说明。不得为通过测试而删除断言或跳过失败用例，不得为清洁工作树而 stash/reset/clean 或覆盖用户修改。影响阶段、核心合同或恢复完整性的冲突交回用户；Goal 的 blocked/complete 状态遵循当前环境规则，不把耗时、困难或未完成当作成功或阻塞。

最终逐项报告 A1—A7 的本轮证据、保留测试前后差异、工作树与独立副本验证、归档恢复入口及未验证平台/产品边界。全部满足才完成；不声称归档后的核心已具备可玩场景或磁盘续局入口。
```
