# 山野镇 Character Gym 使用说明

更新：2026-11-02。本文说明山野镇当前入口，按 [CLI 参数](../../../src/Playground.Cli/Program.cs)、[管理命令](../../../src/Playground.Cli/Gym/GymCommands.cs)与 [LLM 宿主](../../../src/Playground.Cli/LlmConsole.cs)编写。Gym 一期的[工程验收](../../experiments/character-gym-acceptance.md)与[真实试玩](../../experiments/character-gym-playtest.md)属于原型阶段证据；山野镇的完成条件与新证据以[专题工作计划](../../codex-goals/living-world/work-plan.md)为准。命令可用性不等于所有融合体验已验收。

## 1. 创建或继续山野镇实验

在仓库根目录的 PowerShell 中运行。第一次选一个**尚不存在**的存档目录；同一路径再次启动即为续局，不会重新初始化世界。

```powershell
$gymStore = "artifacts/hillside-gym-first-run"
# 新建两角色世界，Console 控制 a，模型控制 b：
dotnet run --project src/Playground.Cli -c Release -- --characters 2 --llm --human a --llm-connections config/llm-connections.example.json --character-connections config/playground-character-connections.example.json --feedback --store $gymStore --max-calls 16

# 恢复同一世界，人数与内容沿用保存定义：
dotnet run --project src/Playground.Cli -c Release -- --llm --human a --llm-connections config/llm-connections.example.json --character-connections config/playground-character-connections.example.json --feedback --store $gymStore --max-calls 16
```

角色 `a` 由当前 Console 的人类操控，`b` 由模型操控；轮到哪个角色决定就暂停世界等其操作。人类输入以当前画面列出的合法行动为准；`Ctrl+C` 可暂停，已提交的游戏进度保留。调用预算耗尽也会暂停，不会替模型选择行动。此例的 `16` 是本次宿主运行预算，续局重新计算。

- `--feedback` 同时启用 Gym 记录和模型的 `report-feedback` 工具。反馈默认关闭；仅传 `--llm` 不启用反馈，也不启用 Gym 轨迹。
- 只想记录而不提供反馈工具时，把 `--feedback` 换成 `--gym`。两者都必须与 `--llm --store` 配合。
- `--llm` 必须带 `--llm-connections PATH`。上述示例使用已有 Codex 登录：默认 low 连接，b 覆盖为 medium；配置格式见[多模型连接](../multi-llm-connections.md)。可选 `--character-connections PATH`，省略则全体模型角色用默认连接；Human 优先，保留其 dormant override 不会触发调用。
- 可另加 `--max-calls N`、`--max-failures N`、`--max-context-chars N`；所有连接共用一个 loop 的总调用与连续失败预算。续局需重新指定所需配置、预算和记录/反馈开关。旧 `--model` / `--auth-file` / `--catalog` / `--connection` 不再接受。
- 新局用 `--characters 1|2|3` 选择人数，省略为三人；续局省略人数则沿用存档，显式人数必须一致。`--llm` 控制全体，`--human ID` 将任意一个现存角色交给 Console；省略 `--human` 即全模型观战。单模型新局可用 `--characters 1 --llm`，无需另一种游戏模式。
- 新局可用 `--content hillside|gentle` 选择内容。续局省略则使用保存定义；显式选择须与保存内容身份一致。修改控制绑定不更换角色、内容或既有状态。
- `--steps N` 与 `--until-hour N` 限制本次宿主运行，分别为提交转换数与绝对模拟小时上界；不会结束世界。时间停在最后完成事件，不保证恰好到达上界。

存档只有一个分支时，可原样重跑上述命令续局。注册过 fork 后，连原分支也应明确选择，见第 3、5 节。

## 2. 反馈与模型的局部工具循环

启用反馈后，模型可先调用 `report-feedback`，收到已保存回执，再调用 `act` 选择游戏行动。反馈包括问题或需求类型、摘要、遇到的障碍及可选的期望支持。反馈不推进游戏时间、不替代行动、不自动写进角色 Goals，也不代表开发者已确认缺陷或接受需求。模型是否提交、内容是否有价值，需要实际试玩判断。

反馈响应与工具回执只追加到**本次角色服务**的局部 Tail，最终产生行动后结束该循环。冷开、fork 续跑或下一次角色服务不会从实验日志恢复这段 Tail；它们从游戏保存的角色 Context 重新构造输入。已保存反馈仍可查询，但模型可能不记得刚才报过的问题，也不保证重跑输出相同内容。看到“反馈已保存”不等于后续模型请求确实收到了回执，需核对请求证据。

## 3. 查看分支和反馈

以下是独立管理命令，不运行游戏，也不需要模型配置。一次只选一种管理操作，不与 `--llm`、`--human`、`--feedback`、`--characters`、`--content` 等游戏配置混用。

```powershell
dotnet run --project src/Playground.Cli -c Release -- --store $gymStore --branches
dotnet run --project src/Playground.Cli -c Release -- --store $gymStore --gym-feedbacks
```

分支列表给出 `BranchId` 等信息；**复制 ID，不用显示名称代替 ID**。反馈列表给出 `Payload.FeedbackId`、`Payload.RequestId`、角色、分支及决定前边界等定位信息。

可按角色、分支筛选反馈。将下列占位值替换成实际列表里的 ID：

```powershell
$sourceBranch = "替换为实际 BranchId"
dotnet run --project src/Playground.Cli -c Release -- --store $gymStore --gym-feedbacks --branch $sourceBranch --actor b
```

`--gym-feedbacks` 不指定分支表示查询所有分支；`--branches` 本身不接受 `--branch`。但**打开游戏和导出轨迹**需要选定一个分支，存档有多个分支时不能省略 `--branch`。`--actor` 是查询/导出的角色筛选，不改变控制器绑定。

## 4. 导出角色轨迹

```powershell
dotnet run --project src/Playground.Cli -c Release -- --store $gymStore --gym-export "gym-b-source.md" --actor b --branch $sourceBranch
```

目标目录须已存在，目标文件须尚不存在。导出使用 `CreateNew`，不会覆盖旧文件；再次导出请换文件名。导出对游戏存档与实验日志只读，只创建指定的 Markdown 文件；不会恢复 pending 事件、调用模型或修改日志末尾的不完整记录。

导出按所选角色组织已记录的材料、实际请求与响应、操作结果、反馈及公共运行信息；每次调用的 actor、connection、model、protocol 取自实际绑定，续局可以换配置，旧记录保持原值；同时列出可检查的证据缺口。`perception` 是保留的角色可感知材料，不能当作已经发送给模型的请求；模型提出了行动，也不能当作游戏已经提交。Human 角色没有模型请求属正常情况。

子分支只继承来源切点及以前的祖先记录，不混入父分支后来的经历。未启用 Gym 的旧历史、未保留的日志和缺失的回执不会由当前 Context 猜补。Markdown 的阅读视图用于浏览；完整原始 Payload 用于核对请求和关联，不是游戏内对白。

## 5. 按反馈或请求分叉，再另行续跑

从反馈列表复制一个真实 `FeedbackId`：

```powershell
$feedbackId = "替换为实际 FeedbackId"
dotnet run --project src/Playground.Cli -c Release -- --store $gymStore --gym-fork $feedbackId --fork-label "核对采集反馈"
```

没有反馈也可从已记录的模型请求分叉。在导出轨迹的 `request` 记录中找到 `Payload.RequestId`：

```powershell
$requestId = "替换为实际 RequestId"
dotnet run --project src/Playground.Cli -c Release -- --store $gymStore --gym-fork-request $requestId --fork-label "重看这次决定"
```

两种 fork 方式选其一。它们由原记录定位来源分支，**不接受额外的 `--branch` 或 `--actor`**。命令会核对记录与真实存档中的决定前完整 S 边界，随后注册新分支；不会打开新分支，也不会运行模型或回放那次行动。来源是世界整体的已完成状态，不是只复制一个角色，也不是恢复某次 API 调用执行栈。

复制成功输出中的新 `BranchId`，用原存档目录另行启动：

```powershell
$forkBranch = "替换为刚注册的新 BranchId"
dotnet run --project src/Playground.Cli -c Release -- --llm --human a --llm-connections config/llm-connections.example.json --character-connections config/playground-character-connections.example.json --feedback --store $gymStore --branch $forkBranch --max-calls 16
```

回到源分支时改用 `--branch $sourceBranch`。新分支与来源共享已有历史，后续各自推进；不要把新分支的 ID 当成新的 `--store` 路径。模型会基于该分支重新构造输入、重新作答，不承诺复现原输出。新分支的反馈与导出仍按第 3、4 节查询，只需改选分支 ID。

## 6. 保存材料与处理失败

先保留整个 `--store`，再考虑清理。游戏 E/S 和分支元数据决定可恢复的世界；`gym/events.jsonl` 保存实验记录，根分支的 `operations.jsonl` 与子分支的 `branch-operations/` 保存操作关联，存档下默认的 `llm-runs/` 保存每次宿主运行的独立审计。若使用 `--llm-log PATH` 把审计放在存档外，也应一并保留；该文件同样不能覆盖已有文件。

当前按同版程序、同版游戏修订与 Schema 使用，不承诺旧开发存档跨版本升级。保存可复现现场时同时保留对应代码/构建版本与所用非敏感配置；记录或分支不是独立于存档即可运行的便携包。实验数据不是角色私有记忆的新增来源。

若 fork 报告“分支已注册，但来源日志未能确认保存”，分支注册与日志保存已经产生不同结果：**先用 `--branches` 核查返回或异常中给出的分支 ID，不要盲目重试 fork**，否则可能再创建一条分支。注册本身报告结果不确定时也先核查。命令没有替你打开或运行新分支；缺失来源日志时，导出可能无法拼接祖先轨迹，应保留错误信息与现有文件供排查。

普通暂停后重新运行会先验证存档及显式内容身份，再恢复已发布的 pending 工作，最后进入控制器；不会把审计响应当成游戏事实。模型调用尚未形成已发布游戏事件便中断时，重开可能重新调用模型。模型调用预算暂停返回码为 `2`，宿主模拟预算暂停为 `0`，取消为 `130`；这些都应结合停止原因与存档判断，不能仅凭没有新的响应就认定世界没有执行。

实现边界与一期验收要求见[设计](design.md)、[里程碑](milestone.md)和[实施入口](../../implementation/character-gym-plan.md)。
