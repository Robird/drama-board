# 角色目标管理：真实模型试玩

状态：2026-10-31，本轮试玩结束。6 个世界、7 个进程共完成 80 次真实模型调用；包含一次正常存档冷开和一次合作逃离。此文记录实际操作与未覆盖边界，不替代工程测试或主线程验收。目标见[里程碑](../design/goal-management/milestone.md)，故事见[用户故事](../design/goal-management/user-stories.md)。

## 操作者与证据边界

- a 的 Human Console 入口由 coding subagent `/root/goal_business` 操作，**不是用户本人试玩**；b 由游戏 Completion 适配器调用真实 `gpt-6.1-sol`。
- 使用默认 Codex Responses 连接，认证文件只交给现有凭据提供器，不读取或记录凭据。实际模型绑定、系统提示词、完整输入、工具输出和提交回执在各次 `run.jsonl`。
- `artifacts/goal-playtest/run_console.py` 仅将人工选择的 UTF-8 命令文件转交 CLI stdin，不代模型选择行动、不构建代码；`sent.jsonl` 标记 a 的实际输入。每次独立进程保留 stdout/stderr 原始字节、启动参数、产物哈希与退出码。历史 Git HEAD 不代表未提交施工版本，精确产物以 `transport.json` 的 DLL 哈希及实际提示词为准。
- 所有目标由 b 自行创建维护，不向角色预填 Goal。没有代理操作 b 的目标，也不把模型自由文本当成已提交行为。

## US-01 基本型：分工、冷开与合作逃离

默认 `--duo --llm` 新世界。a 在营地提出“自己采两份食物并给 b 一份；b 采四份水并给 a 两份；随后会合找出口”，然后沿溪声下坡。b 的实际前 3 次调用为：

1. `say a ...` 接受分工，保留资源不足或危险时调整的条件。
2. `create-goal` 建立 G1“与阿青分工补给后寻找出口”，记录采四份、赠两份、接收食物和寻找出口；对 a 尚未兑现的说法注明未证实。
3. `go trail-1`。

两人 D0 16:00 到达溪流湾，a 在自己的输入机会提交 `:exit`，宿主退出 0；以同一存档、新日志路径重开。恢复后的第一次真实模型请求为 b 的 Context 2，材料中保留原 G1，Tail 为空，随后直接执行 `gather water 4`，没有重复创建或重写 handoff。

D0 17:00 a 完成浆果两份并赠 b 一份食物。b 恢复进程的调用 2 实际赠 a 两份饮水，调用 3、4 使用食物与水，调用 5 更新 G1 的实际采集/赠与进展与下一步路线。随后双方经狭谷与旧桥探索，21:00 的低温警告后分别 `continue` 保持 22:00 抵达期限，最终两人均在 D0 22:00 获救。

| 观察项 | 实际证据 |
| --- | --- |
| 真实模型调用 | 暂停前 3 次，重开后 8 次；均完成响应、形成合法操作 |
| 目标编辑 | 1 次 create、1 次 update；无重复创建、无 handoff |
| Context 接续 | 首次调用属于 Context 1；恢复后依次在 Context 2～5 调用 |
| 合作结果 | a 赠食物 1；b 赠饮水 2；双方各自使用补给并最终获救 |
| 恢复 | 正常 S 保存暂停；重新启动真实客户端后继续使用旧目标；不是 E-only 故障试验 |
| 拒绝与错误 | 本局 0 游戏拒绝、0 模型协议错误；两个进程退出 0 |

**限制**：b 选择将补给与逃离合为一个目标。赠水后 update 记录了已兑现进展，但未 finish；到终局后不再有模型输入机会，因此 G1 仍为 Open。不能把这局真实成功写成“目标已标完成”，也不能单凭本局覆盖 US-01 的结束说明细项、方法失效变体或共享图。目标进展里包含写入当时的身体数值，属于历史角色记录，后续当前身体必须仍以观察为准。

证据目录：`artifacts/goal-playtest/us01-first/`、`us01-resumed/`，单根存档及操作日志为 `us01-store/`。实际启动形式：

```powershell
python artifacts/goal-playtest/run_console.py --run artifacts/goal-playtest/us01-first --store artifacts/goal-playtest/us01-store
# 正常 :exit 后，以新 run 目录和同一 store 重开。
python artifacts/goal-playtest/run_console.py --run artifacts/goal-playtest/us01-resumed --store artifacts/goal-playtest/us01-store
```

该辅助脚本内部使用已构建的 Release CLI，参数为 `--duo --llm --model gpt-6.1-sol --auth-file W:/CodexHome/auth.json --max-calls 128`，独立 `--llm-log` 保存每次运行。

| 文件 | SHA-256 |
| --- | --- |
| us01-first/run.jsonl | `AA7D468C0ED52C39E2DAD89952DF0AA19BCE960447A29C128C6283BBA09AD6DC` |
| us01-resumed/run.jsonl | `CD503783DED7CF5010F84D3D816B8B37E8AE97098421F16E2B21BCB9BEF2C243` |
| us01-first/console.txt | `531C20BE3B547153B58AA1E3E69A94968ED71715ACEE21A13D565D43F8EDE856` |
| us01-resumed/console.txt | `7D4D18C1B84C748543049F9122E00834C015146F49464525A1BD0DD4800ECEDD` |

## 受控初态与干预来源

后续短局使用 `artifacts/goal-playtest/Fixture/Program.cs`，引用已编译 Release DLL，以公开 `CycleGameFactory`、领域构造器、`CycleEngine.Rebuild` 和 `CycleDurableHistory.Create` 创建初态；不改运行中的存档根，不绕过事件/状态发布。每局 store 的 `fixture-description.txt` 保存种子描述。Goals、Context、Activity 均从空开始，没有预置模型决策。

| 初态 | 相对普通双人游戏的差异 | 隔离目的 |
| --- | --- | --- |
| depletion | D0 08:00 开局、关闭天气；正常 `CycleScript` 于 09:05 销毁溪流水库存，随后自然再生 | 采集方法失效，不靠告知模型虚构失败 |
| cold / cold-guided | D0 18:00，双方在山脊且仅知山脊；食物/水分均 90，各带食物和水各 3；a 温暖 90、b 温暖 31；关闭天气 | 预报可见的夜间低温与返回原目的，避免补给和随机天气掩盖问题 |
| inquiry / inquiry-scoped | D0 08:00，双方在山脊且仅知山脊；关闭生存需求和天气，空背包；资源和地图保持原规则 | 共享调查与否定结论，不用于验证生存平衡 |

例：先运行 `dotnet artifacts/goal-playtest/Fixture/bin/Release/net10.0/DramaBoard.Fixture.dll artifacts/goal-playtest/cold-store cold`，再用上文 transport 启动该 store。Fixture 编译只引用现有 DLL，不构建主 solution。

## US-01 方法失效变体：保留目的、分批兑现、正常收尾

`depletion` 中，a 提出独立的“准备两份水给我”，建议 b 采四份再分两份，说明遇到困难可以换办法或等恢复；找出口等补给兑现后再商量。这比基本型更明确地区分了结果与方法，不能将两局差异独立归因于系统能力。

- b 调用 1 创建 G1“备水并交给阿青两份”。调用 2 试图对已离开的 a 说话，被游戏拒绝；调用 3 改为下坡，没有重复同一无效操作。这一次失败保留在日志中。
- 09:00 开始 `gather water 4`；09:05 正常脚本事件耗尽资源，尚未得到首份产出。b 调用 5 取消活动，调用 7 更新**原 G1**，保留赠水目的，记录缺水和再生信息，改为等待期间采浆果。
- 调用 14 于 10:20 实际赠水 1；调用 15 记录“已交一份、还差一份”。调用 19 于 11:35 再赠水 1，调用 20 更新实际兑现情况。两次 `ItemGiven` 提交为准，不把 say 中的承诺视为交付。
- a 自然确认收到两份、同意后续路线，没有给目标工具指令。b 调用 22 `finish-goal`，说明已分两次赠足、a 已确认及同意路线。随后主动停局，不重复整条逃离路线。

该局没有机械完成最初“采四份”的方法，而是在资源约束下完成赠两份的结果。它提供了实际方法替换、未过早结束、根据世界结果更新并收尾的实例；自然确认属于实验干预，不能宣称模型无提示必定及时收尾。

## US-02：避寒后返回，区分行为返回与 Focus 指针

**第一局 `cold` 不要求目标拆分。** a 请 b 亲自新采四份野菜，太冷可以先避寒、安全后回来。b 只创建一个 G1，将避寒写入 nextStep；温暖 31 时根据预测先去林屋，18:30 收到警告仍保持行程，20:00 以温暖 7 抵达庇护。回暖、补水后 04:00 返回，06:00 开始采四份，08:00 实际采足并 finish G1。模型没有把初始背包食物误算成新采成果。

这是合理的简单策略，证明预防危险、跨 Context 保留目的及返回执行。它**没有创建临时目标，也没有切换 Focus**，因此不满足用户故事中全部指针操作细节；不将这种省去拆分的选择列为玩法失败。

**补充局 `cold-guided` 明确要求分别记录采菜和临时避寒，先聚焦避寒，安全后结束临时事项并把注意力转回采菜。** 没有给命令语法、ID 或预填目标。

- 调用 1 创建 G1 采菜，调用 2 创建 G2 避寒并 `focus:true`；后续 update G2 记录抵达庇护和回暖计划。
- 调用 9 在 D1 04:00 温暖 100 后 finish G2；调用 10 `show-goal G1`，取回原采菜材料；调用 11 实际返程，调用 12 开始采菜。停局前已有三次 `GatherYielded`，不再等同一条四份结算链重跑结束。
- **未调用 focus-goal G1**。结束 G2 后 Focus 为空，模型通过 show 读回原目标并行动。Focus 不自动跳到无关旧根是已定设计；本局覆盖“临时目标完成后返回原目的”，未覆盖模型显式重新聚焦 G1 的故事细节。不继续诱导模型补一个漂亮操作。

a 在两局都通过真实 use/wait/continue 维持自己的生存和推进调度；部分 `show-goal {}` 是 a 查询自己的空目录以交还决策机会，不读取或修改 b 的目标。完整命令在各局 `sent.jsonl`。

## US-03：共享调查、限定否定结论、沿另一来源继续

两局都由 a 提出“可靠饮水来源”和“有水的会合点”两个目的，要求用一个调查同时服务两者。属于**明确的概念级共享引导**；模型自行选择实际 Goal/Link 命令、正文与后续行动，不能称作自主发现共享抽象。

首试 `inquiry` 使用“屋顶附近是否有水”。b 连续创建 G1/G2 两个父目标、G3 调查，建立 G1→G3，再实际 `link-goal` 建立 G2→G3。抵达林屋后看见只有干木，b 更新 G3 为当前无可见水、下坡水声待查，并开始走 trail-7。这个“附近”范围容许继续调查，**没有形成否定结论**；11:00 主动停局，保留未完成尝试，不能把溪流湾后来可能有水写成林屋的否定样本。

第二局 `inquiry-scoped` 改为明确的有限问题：

> 只核实屋顶所在那个地点当前列出的可采资源是否含饮水，不把通路另一端算进去。答案可以是有或没有，查清当前可见资源即可结束这项调查；父目的未满足就另找办法。

这没有泄露实际答案，但增加了范围和完成标准引导。

1. b 调用 1～4 实际创建 G1/G2/G3，保留两个父来源及两条链接。
2. 抵达林屋后调用 7 finish G3，结束说明为“当前列出的可采资源仅干木，不含饮水”，并明确 G1 和 G2 尚未实现。G3 的**完成**表示调查有结论，不表示找到水；两个父目标仍 Open。
3. a 自然要求转而处理会合点，沿用调查，不必重查。调用 8 `focus-goal G2`；调用 9 的实际 model-input 从 G2 展示已结束 G3 的结论，模型据此 update G2，准备前往溪流湾确认水源和会合条件。
4. 调用 10 说明带不确定性的后续计划，调用 11 实际 `go trail-7`。没有重新执行已完成的林屋调查。主动停局，不要求每个短局都逃离世界。

这覆盖了实际 Link、有限的否定结果、父目的不自动达成及从另一来源读取并利用共享结论。这里只证明**本局当前可见资源**，不代表整个林屋周边永远无水；也未检验共享图在另一次进程冷开后的恢复。后一恢复能力不能由本例外推。

## 调用量、原始证据与版本

下表由 `artifacts/goal-playtest/summarize_runs.py` 对原始日志生成。目标编辑包括 create/update/link/focus/finish，不含 show；拒绝包括所有提交者，此次唯一拒绝来自 b。Shared 大小只计当前材料 Content 的 UTF-16 单元，不含系统提示、工具 schema 或 JSON 外壳，**不是 token 数**。

| run | 模型调用 | b 目标编辑 / 查询 | 游戏拒绝 | Shared 峰值 |
| --- | ---: | ---: | ---: | ---: |
| us01-first | 3 | 1 / 0 | 0 | 4556 |
| us01-resumed | 8 | 1 / 0 | 0 | 6854 |
| depletion | 22 | 5 / 0 | 1 | 6338 |
| cold | 13 | 3 / 0 | 0 | 5846 |
| inquiry | 8 | 5 / 0 | 0 | 4527 |
| inquiry-scoped | 11 | 7 / 0 | 0 | 5036 |
| cold-guided | 15 | 4 / 1 | 0 | 5847 |

合计 80 次真实模型调用、26 次目标编辑、1 次目标查询、1 次游戏拒绝。各次输入 Tail 均为空，每份 Shared 恰有一个“## 目标记录”；均无协议错误、未使用 handoff，7 个进程退出码均为 0。各次新请求包含持久 Context 材料，Tail 为空不等于没有历史信息。

实施提交基线为 `42da30d`；首局发生在其提交前。试玩期间主线程先修正焦点变化回执，后补齐目标命令适用状态帮助，两次均在局间重编。这里不声称所有局使用完全相同二进制。CLI DLL SHA-256：

| 使用范围 | DLL SHA-256 |
| --- | --- |
| us01-first / resumed | `fb1510bc13ffc85583149d3edc3c142630c9fb052befc7b3dd3a1af50b51934b` |
| depletion / cold | `a532a5b7d94c6d5b14e51a42d9c8cc6c652b38910754c822ca85a8223c34ac32` |
| inquiry / inquiry-scoped / cold-guided | `d41b362d2e97bcc54870cb1ff4bb59fd9178c58bc52b7ca636b937129afac365` |

其余 `run.jsonl` SHA-256（基本型见上表）：

| run | SHA-256 |
| --- | --- |
| depletion | `ad66da095c2cc5db6931ef6e572737e2f44c3f4030b7f38ca0cdb0be64155187` |
| cold | `8b1384e09bc65b59ab377e0b236ce2d7f057414063ec9ed838891a1c66c68c94` |
| inquiry | `b0eeb748497d0ac508715e121148199d447795f61dc0c6e5bc2d23e04c8ff96b` |
| inquiry-scoped | `81c42ec03362b7ce7657856bd1b04b0ef823370c041406ec6b73d9c3a3b1149e` |
| cold-guided | `a1d9c9d471ed1485d804fc6e8f134aedd7cbf8c7adb32842870febdf60078dd2` |

原始日志、fixture 和保存的世界位于本地忽略目录 `artifacts/goal-playtest/`，不随此文自动进入 Git。审计入口为每局 `run.jsonl`、`sent.jsonl`、`console.txt`、`transport.json` 和对应 store。摘要便于定位，最终以原始 model-input/output 与 committed 对照为准。

## 实证边界与下一步

- 这些实例支持“角色目标记录能在独立调用/Context 和一次正常冷开后继续指导行动”。没有随机种子统计、无目标管理对照组或多模型比较，不能证明总体可靠性或量化净收益。
- 临时目标与共享目标有明确自然语言引导，复杂结构不是模型自发发现的证据；最简单的一目标方案也能完成避寒任务。不要把更多目标编辑次数当作更好的规划。
- 模型结束临时 Goal 后未显式重新聚焦 G1，而是 show 后继续它；基本型复合 G1 在终局仍 Open。是否增加终局整理机会、如何说明 Focus 的用途，需另做体验判断，不能为了试验收尾强迫每次模型补操作。
- 目标 progress 中的身体快照可能变旧；角色记录与当前观察的标签区分必须保留。旧通用反馈“本时刻剩余交流次数：-1 次”实际上表达变化量，容易读成余额，记录为既有 UI 润色线索，本轮未扩范围修改。
- 本轮没有真实模型检验 pause/resume、reopen 勘误、forget/容量压力、循环拒绝或故障 E-only 恢复；工程测试与这些实际玩法体验应分别报告。没有把未运行或未完成的故事细节列为通过。
