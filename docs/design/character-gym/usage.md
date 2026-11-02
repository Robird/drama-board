# 山野镇 Character Gym 使用说明

更新：2026-11-02。本文说明山野镇的[角色 history 与统一宿主日志](../character-history-and-diagnostics.md)入口，按 [CLI 参数](../../../src/Playground.Cli/Program.cs)、[管理命令](../../../src/Playground.Cli/Gym/GymCommands.cs)、[统一宿主](../../../src/Playground.Cli/PlaygroundHost.cs)编写。反馈保持[独立合同](../independent-feedback.md)。Gym 一期的[工程验收](../../experiments/character-gym-acceptance.md)与[真实试玩](../../experiments/character-gym-playtest.md)保留原版本语境，不替代重构验收或实际真人体验。

## 1. 创建或继续山野镇实验

在仓库根目录的 PowerShell 中运行。第一次选一个**尚不存在**的存档目录；同一路径再次启动即为续局，不会重新初始化世界。

```powershell
$gymHome = "artifacts/hillside-gym-first-run"
$gymStore = "$gymHome/store"
dotnet run --project src/Playground.Cli -c Release -- init --home $gymHome
Copy-Item config/llm-connections.example.json "$gymHome/llm-connections.json"
# 新建两角色世界，Console 控制 a，模型控制 b：
dotnet run --project src/Playground.Cli -c Release -- --characters 2 --home $gymHome --character-controls config/playground-character-controls.example.json --max-calls 16

# 恢复同一世界，人数与内容沿用保存定义：
dotnet run --project src/Playground.Cli -c Release -- --home $gymHome --character-controls config/playground-character-controls.example.json --max-calls 16
```

角色 `a` 由当前 Console 的人类操控，`b` 由模型操控；轮到哪个角色决定就暂停世界等其操作。人类输入以当前画面列出的合法行动为准；`Ctrl+C` 可暂停，已提交的游戏进度保留。调用预算耗尽也会暂停，不会替模型选择行动。此例的 `16` 是本次宿主运行预算，续局重新计算。

- `--gym off|record` 选择详细人物记录；未指定时持久运行默认 record，ephemeral 默认 off。持久 off 仍保存运行和必要操作事实；ephemeral 主日志只在内存，显式 record 拒绝。反馈默认可用且不依赖详细录制。
- 默认全 LLM；`--character-controls PATH` 使用 typed 逐角色覆盖，`--no-character-controls` 清除 home 的覆盖选择。示例中 a 为 Human、b 为 deliberate。全 Human 也要求合法连接目录，但不读取凭据。
- 预算可以写入 playground.json 或由 CLI 显式覆盖；跨连接共享。配置路径规则和初始化见 [home 方案](../playground-home.md)。
- 新局 `--characters 1|2|3`，续局省略沿用存档；绑定不改变角色身份。
- 新局可用 `--content hillside|gentle` 选择内容。续局省略则使用保存定义；显式选择须与保存内容身份一致。修改控制绑定不更换角色、内容或既有状态。
- `--steps N` 与 `--until-hour N` 限制本次宿主运行，分别为提交转换数与绝对模拟小时上界；不会结束世界。时间停在最后完成事件，不保证恰好到达上界。

存档只有一个分支时，可原样重跑上述命令续局。注册过 fork 后，连原分支也应明确选择，见第 3、5 节。

## 2. 反馈与模型的局部工具循环

Human 可输入 `:feedback <text>`，模型可调用 `feedback({"text":"…"})`，收到已保存回执，再选择游戏行动。正文非空且默认不超过 4000 个 UTF-16 code unit；宿主自动附加身份与提出时的现场。两者共用独立 FeedbackSession，Gym off 也可保存。反馈不推进游戏时间、不替代行动、不自动写进角色 Goals，也不代表开发者已确认缺陷或接受需求。模型是否提交、内容是否有价值，需要实际试玩判断。

反馈响应与工具回执只追加到**本次角色服务**的局部 Tail，最终产生行动后结束该循环。冷开、fork 续跑或下一次角色服务不会从日志恢复这段 Tail；它们从游戏保存的角色 Context 重新构造输入。已保存反馈仍可查询，但模型可能不记得刚才报过的问题，也不保证重跑输出相同内容。主日志中后续 model-request 的 Tail 带匹配收据，证明收据进入了该次构造并保存的输入；不证明远端收到或处理。

## 3. 查看分支和反馈

以下是独立管理命令，不运行游戏，也不需要模型配置。显式 `--store` 绕过 home 读取，stdout 保持原机器可读格式。一次只选一种管理操作，不与 `--character-controls`、`--gym`、`--characters`、`--content` 等游戏配置混用。

```powershell
dotnet run --project src/Playground.Cli -c Release -- --store $gymStore --branches
dotnet run --project src/Playground.Cli -c Release -- --store $gymStore --feedbacks
```

分支列表给出 `BranchId` 等信息；**复制 ID，不用显示名称代替 ID**。反馈列表直接给出 `FeedbackId`、`ActorId`、`Boundary` 等定位信息，LLM 关联位于可选 `Model`；Human 不伪造请求 ID。ephemeral 反馈位于 home/feedback.jsonl，须用 `--home PATH --ephemeral --feedbacks` 查询，不能配 `--branch`，没有可分叉 Boundary。省略 `--ephemeral` 时按 home 解析 store 查询持久报告。查询 stdout 仅含 JSONL；不完整尾在 stderr 警告忽略的字节数，完整行校验失败则拒绝，均不修改原文件。

可按角色、分支筛选反馈。将下列占位值替换成实际列表里的 ID：

```powershell
$sourceBranch = "替换为实际 BranchId"
dotnet run --project src/Playground.Cli -c Release -- --store $gymStore --feedbacks --branch $sourceBranch --actor b
```

`--feedbacks` 不指定分支表示查询所有分支；`--branches` 本身不接受 `--branch`。但**打开游戏和导出轨迹**需要选定一个分支，存档有多个分支时不能省略 `--branch`。`--actor` 是查询/导出的角色筛选，不改变控制器绑定。

## 4. 导出角色 history

```powershell
dotnet run --project src/Playground.Cli -c Release -- --store $gymStore --gym-export "gym-b-source.md" --actor b --branch $sourceBranch
```

目标目录须已存在，目标文件须尚不存在。导出使用 `CreateNew`，不会覆盖旧文件；再次导出请换文件名。导出对游戏存档与实验日志只读，只创建指定的 Markdown 文件；不会恢复 pending 事件、调用模型或修改日志末尾的不完整记录。

导出先选择本分支及可证明的祖先前缀，再按角色和主 Sequence 排列；每次调用的 actor、connection、model、protocol 取自当时实际绑定，续局换配置不改旧记录。正文包括 Observation、LLM reasoning、普通 response、操作及游戏回应/后续经历。reasoning 只采用 Completion 的 PlainText，可以是明文、摘要或空；普通 response 不自动成为角色对白。Human 回合省略 reasoning/response。旧 assistant Tail 标为携带材料，不重复解释为本次新输出；候选 act 另需 operation-received/completed 才能陈述宿主接收或提交。

人物主体只读 `host/events.jsonl`，不加载或校验 `llm-runs/`：全部 raw 移走也可导出已有五类材料和工具收据。raw 位置仅作为未验证附件提示；专项诊断时才检查完整原始审计。角色材料被捕获不证明玩家看见或模型收到；Opening 中的经历也不都由上一动作导致。

请求分叉的子分支只继承该请求之前的精确主日志前缀，不混入父分支后来经历、输出或补录的操作结果。独立反馈分叉不读写主日志，没有可证明的截点，导出报告祖先缺口。off 期间观察与模型材料未录制、日志尾不完整或投影不支持时明确报告，不从 raw 或当前世界猜补。角色 history 只查可见 feedback-saved ID 对应的正文；其他报告仍用 `--feedbacks` 查询。

## 5. 按反馈或请求分叉，再另行续跑

从反馈列表复制一个真实 `FeedbackId`：

```powershell
$feedbackId = "替换为实际 FeedbackId"
dotnet run --project src/Playground.Cli -c Release -- --store $gymStore --fork-feedback $feedbackId --fork-label "核对采集反馈"
```

没有反馈也可从已记录的模型请求分叉。在导出 history 的 `model-request` 记录中找到 RequestId：

```powershell
$requestId = "替换为实际 RequestId"
dotnet run --project src/Playground.Cli -c Release -- --store $gymStore --gym-fork-request $requestId --fork-label "重看这次决定"
```

两种 fork 方式选其一。`--fork-feedback` 直接读取独立反馈并 Inspect 完整 Boundary 后 Fork，不需要主日志存在或健康；Boundary 为 null 时拒绝。`--gym-fork-request` 从主日志的唯一 model-request 取得 Boundary，保存 `SourceThroughSequence = model-request.Sequence - 1`；所选请求及其后父记录都不进入子 history。它们由原记录定位来源分支，**不接受额外的 `--branch` 或 `--actor`**。命令核对真实存档中的决定前完整 S，随后注册分支；不会打开新分支、运行模型或回放行动。来源是整个世界的已完成状态，不是只复制一个角色，也不是恢复 API 调用执行栈。

复制成功输出中的新 `BranchId`，用原存档目录另行启动：

```powershell
$forkBranch = "替换为刚注册的新 BranchId"
dotnet run --project src/Playground.Cli -c Release -- --home $gymHome --character-controls config/playground-character-controls.example.json --branch $forkBranch --max-calls 16
```

回到源分支时改用 `--branch $sourceBranch`。新分支与来源共享已有历史，后续各自推进；不要把新分支的 ID 当成新的 `--store` 路径。模型会基于该分支重新构造输入、重新作答，不承诺复现原输出。新分支的反馈与导出仍按第 3、4 节查询，只需改选分支 ID。

独立反馈先完成刷盘才回成功 ID；保存与回执实际交付不同。写入失败会退役当前实例，不自动重试，不能断言反馈未保存。边界是提出反馈时的现场，不能直接认定是正文中问题发生的时刻。

## 6. 保存材料与处理失败

先保留整个 `--store`，再考虑清理。游戏 E/S 和分支元数据决定可恢复的世界；`host/events.jsonl` 是各分支/运行共用的时间线，保存运行、操作及可选详细材料；`feedback.jsonl` 保存独立反馈。`llm-runs/<RunId>.jsonl` 专门保存完整 Completion 审计，首次调用才创建，全 Human 无 raw 文件。若用 `--llm-log PATH` 把 raw 放到存档外，也应一并保留；CreateNew 不覆盖已有文件。普通 history 不依赖 raw，不表示可以丢弃专项模型诊断材料。

旧 `operations.jsonl`、`branch-operations/` 或旧 Gym 文件不自动导入新日志。没有有效新主日志且发现旧宿主格式时，运行在打开世界前拒绝；保留旧 store，选择新 store 路径开始，home/连接配置可复用。旧数据可用 Git 中对应工具查看，主线不迁移、不移动或重写旧文件。独立反馈查询/反馈 fork 和分支列表仍可用。

当前按同版程序、同版游戏修订与 Schema 使用，不承诺旧开发存档跨版本升级。保存可复现现场时同时保留对应代码/构建版本与所用非敏感配置；记录或分支不是独立于存档即可运行的便携包。实验数据不是角色私有记忆的新增来源。

若请求 fork 报告“分支已注册，但来源日志未能确认保存”，分支注册与日志保存已经产生不同结果：**先用 `--branches` 核查返回或异常中给出的分支 ID，不要盲目重试 fork**，否则可能再创建一条分支。注册本身报告结果不确定时也先核查。命令没有替你打开或运行新分支；缺失来源日志时，导出可能无法拼接祖先轨迹，应保留错误信息与现有文件供排查。

普通暂停后重新运行会先验证存档及显式内容身份，打开校验主日志、记录 run-start，再恢复世界 pending 并对账当前分支操作，最后捕获材料和服务控制器。恢复不处理祖先前缀中的 pending 操作，也不把模型输出当作世界事实。Received 无可证明世界结果时补 Interrupted，不重做原输入；世界已提交但结果漏记则补原结论并标 ExactResult=false。主日志与世界不原子提交，没有 Completed 不等于没有执行。

模型调用尚未形成已发布游戏事件便中断时，重开可能再次询问模型；本次 raw/主日志保存失败会暂停、退役 writer，不自动重问或补写。反馈已独立保存但关联失败时，成功 ID 保留，不应重复提交。模型调用预算暂停返回码 `2`，宿主模拟预算暂停为 `0`，取消为 `130`；应结合停止原因与存档判断。

当前实现与验收合同见[统一宿主日志](../character-history-and-diagnostics.md)，源码见[主 journal](../../../src/Playground.Cli/PlaygroundHostJournal.cs)、[操作审计](../../../src/Playground.Cli/PlaygroundOperationAudit.cs)和[导出器](../../../src/Playground.Cli/CharacterHistoryExporter.cs)。一期[设计](design.md)、[里程碑](milestone.md)和[实施入口](../../implementation/character-gym-plan.md)保留原阶段背景。此次重构不改变领域 Schema 或 GameRevision，也不自动把 history 回灌角色。
