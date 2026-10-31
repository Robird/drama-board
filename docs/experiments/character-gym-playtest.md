# 角色实验环境一期：真实模型串联试玩

状态：2026-10-31，完成本轮正常路径实证。对应[一期里程碑](../design/character-gym/milestone.md)的记录、反馈、冷开、定位分叉及子分支续跑；不验证反馈质量、修复效果或一般规划可靠性。

## 操作者、配置与证据

a 的 Human Console 入口由 coding subagent `/root/goal_business` 操作，**不是用户本人试玩**；b 通过既有 Completion 适配器调用真实 `gpt-6.1-sol`。使用默认 `--duo --llm --feedback`，未修改初始世界、预填目标、脚本代写反馈或替模型提交行动。

仅运行主线程已构建的 Release CLI。认证文件通过 `--auth-file W:/CodexHome/auth.json` 传给既有提供器，未读取或打印凭据。三个游戏进程的 CLI DLL SHA-256 相同：`7094db13a737f5e926739a1645b05835eaca6d0cf229924887e95151deee8c8f`。配置和当时 Git HEAD 见各局 `transport.json`；工作树当时仍在施工，不能只用 Git HEAD 代表实际二进制。

原始证据保存在本地忽略目录 `artifacts/gym-playtest/`：

- `parent-first/`、`parent-resumed/`、`child/`：stdout/stderr 原始字节、a 实际输入 `sent.jsonl`、CLI 原始模型审计 `run.jsonl`、启动配置和退出码。
- `store/gym/events.jsonl`：真实共享有序 Gym 日志；`store/` 保留游戏仓库及分支操作记录。
- `branches-before/`、`branches-forked/`、`branches-after/`、`feedbacks/`、`fork/`、`export-*/`：实际管理 CLI 的参数、stdout/stderr、退出码。
- `parent-before.md`、`parent-after.md`、`child.md`：实际产品导出文件；`verify.py` 只读核对这些文件，结果为 `verification.json`。

辅助 `run_console.py` 只将操作者选择的 UTF-8 命令送入 stdin 并留证，不选择 b 的行为。记录工具的代码不替代产品的 Gym 功能。原始证据不随本文件自动进入 Git；哈希用于校验，不等于证据本身。

## 父分支：引导反馈、正常行动和冷开

新世界从 D0 15:00 开始。a 原样说：

> 请先为开发者试用反馈工具，根据你实际看到的界面报告一条真实障碍或需要的支持建议，说明我们想活着离开野外时缺什么；别编造已经发生的错误。报告完继续正常行动。我们先沿溪水声下坡补水，我来采食物，你准备饮水，可以吗？

这是**明确的反馈工具试用引导**。模型自行选择工具和具体正文，不能称作自发发现缺陷。

父进程第一次真实调用提交 feature-request：“希望支持同地伙伴共享简短的补给与路线计划”，描述当前只能通过交流与个人目标同步分工，希望共享计划区分角色声明和世界验证。此项仅为模型建议，本轮没有将其确认为缺陷、实现功能或比较收益。

| 关联 | 实际身份 |
| --- | --- |
| StoreId | `40aa0752486449c29d71674e9ab9b95a` |
| 父分支 | `41941807` |
| RequestId | `1baf4d43459345fc96de044fc334fb81`，Gym sequence 7 |
| FeedbackId | `c2c02358c1cb40f18b439acce81d02d4`，sequence 9 |
| 决定前完成 S | `419418070e0000dc01000000` |
| 边界 | b、Context 1、OperationSequence 0、Lineage 1、Transition 3、D0 15:00、GameRevision 15 |

sequence 10 保存回执；下一请求 sequence 11 的 Tail 实际包含该反馈 ID，且完整 Boundary 与 sequence 7 相同。因此这一局反馈没有推进游戏时间、操作序号或发布新游戏状态；“回执已送达请求”有实际材料证据，不只依赖反馈日志行。

b 随后真实 `say` 接受分工、创建 G1“寻找出路并负责饮水补给”、`go trail-1`。a 同样下坡，D0 16:00 抵达溪流湾后在 Human 输入机会 `:exit`，正常保存、退出 0。

以相同 store、新进程和新审计目录 `parent-resumed` 冷开。a 补水并开始采浆果；b 的 4 次实际调用为：说明看见溪水/浆果及补给计划、更新继承的 G1、`gather water 3`、`give a item.water 1`。世界记录了实际采集产出和赠水，16:45 因赠与唤醒 a 后再次正常 `:exit`。这证明父分支已经在早期反馈之后推进，后续 fork 不能退化为从最新状态启动。

## 从反馈注册分支并冷开续跑

所有游戏进程退出后，实际使用以下产品入口；每条完整命令及退出码保留在管理证据目录：

```text
--store artifacts/gym-playtest/store --gym-feedbacks --actor b
--store artifacts/gym-playtest/store --branches
--store artifacts/gym-playtest/store --branch 41941807 --actor b --gym-export artifacts/gym-playtest/parent-before.md
--store artifacts/gym-playtest/store --gym-fork c2c02358c1cb40f18b439acce81d02d4 --fork-label gym-real-feedback
```

fork 命令注册子分支 `40941807`，然后退出，未打开或运行新分支。再以**新的游戏进程**显式传 `--branch 40941807 --duo --llm --feedback` 冷开。无需 a 重发指令，待服务角色即为 b；宿主恢复到 Lineage 1 / Transition 3。

子分支第一次请求的 `SystemPrompt`、`OutputContract`、`Shared`、`Tail` 与父 sequence 7 的对应字段逐项完全相等；Boundary 除 BranchId 改为子分支外也完全相同，RequestId 则重新生成。说明此次同版本重新呈现确实继承选定边界材料，未把父分支后来探索、采集或 G1 的进展混进来。所选早期边界尚无目标和活动，因此本实测不单独证明复杂非空目标图或进行中活动的 fork；这些边界需由领域测试另证。

子模型重新报告地图记录建议，随后真实 `say` 接受分工。a 下坡后，b 又提交一次语义相近的地图建议、创建自己的补水调查目标、`go trail-1`，最终 D0 16:00 抵达溪流湾。随后正常 `:exit`，退出 0。实际模型操作已经进入正常游戏提交路径，不仅是创建分支目录或返回工具文本。

两条子反馈分别为 `7d53a789148946879461f9fa6a2dbff1`、`249ac28f79ce4ed0bb10422397d03e3b`。都具有新请求/反馈身份；各自后的请求实际携带收据，反馈前后 Boundary 不变。原来的 a 指令被边界继承，子局没有额外重发“请反馈”。重复建议是保留的体验现象：独立服务时反馈不自动成为角色长期记忆，也不保证语义去重。本轮不为改善这一现象改变提示词或追加运行。

## 来源不变、角色隔离与共享前缀

实际 `--branches` 的三次结果中，父分支完整 `CycleBranchInfo` 逐项相同：

| 位置 | 父 head / 完成 S | 子 head / 完成 S |
| --- | --- | --- |
| fork 前 | `419418070e0000d40c000000` | 尚未注册 |
| 注册后 | `419418070e0000d40c000000` | `419418070e0000dc01000000` |
| 子运行后 | `419418070e0000d40c000000` | `409418070e00004404000000` |

父分支一直指向同一个不可变完成 S，没有 pending E；子分支自行推进。父分支导出文件在 fork 前与子运行后**字节完全相同**，支持父角色记录和当时材料未被子运行改写。仓库新增分支元数据、子历史和 Gym 后缀属预期，不要求整个仓库目录字节不变；此项没有对全对象图另做深度反序列化比较。

Gym sequence 59 的 `branch-fork` 保存 `SourceThroughSequence=6`，以及原反馈、请求和 S 引用。子导出的共享父前缀恰为可见 sequence **2、6**，后接 fork 记录和子分支后缀；源请求 sequence 7、反馈 sequence 9 及其后的父响应/操作不属于子轨迹。来源 ID 在 fork 元数据中出现，只用于定位，不能误认成子分支实际请求或反馈。

`verify.py` 将导出的每个“记录 N”与原日志过滤结果逐项比较，并解析全部 JSON fence：

- 父导出 42 条，子导出 27 条；所有完整 Payload 与原记录 JSON 值完全相等。
- 所有角色记录 ActorId 仅为 b；ActorId=null 只允许公开 run-start、run-end 和 branch-fork。a 的私有记录未进入导出。b 本来可感知的 a 对话仍保留，不把正常感知误作隐私泄漏。
- 子导出没有任何父 sequence 7 及之后的轨迹记录；父后续水源观察、采集与赠与未作为子历史拼接。
- 子导出指出祖先 run-start 在切点前、run-end 在切点后而不可见的缺口。这是切片截断的诚实提示，不是断言父进程崩溃。

## 实际数量及校验值

| 进程 | 调用上限 | 真实模型调用 | 反馈调用 | 游戏操作输出 | 协议错误 / 游戏拒绝 | 退出 |
| --- | ---: | ---: | ---: | ---: | --- | ---: |
| parent-first | 12 | 4 | 1 | 3 | 0 / 0 | 0 |
| parent-resumed | 12 | 4 | 0 | 4 | 0 / 0 | 0 |
| child | 10 | 5 | 2 | 3 | 0 / 0 | 0 |

共 13 次真实调用、3 次模型反馈及 10 次游戏操作输出，均在提交记录中找到实际结果。未触发预算暂停，没有为了耗尽额度继续调用。所有管理 CLI 退出 0。

| 证据 | SHA-256 |
| --- | --- |
| parent-first/run.jsonl | `6e6bb138119203bccbbded56b089d17e4f3c813ec8b067bcd6779a6c5aa4ce18` |
| parent-resumed/run.jsonl | `7ea90f99a5851f57b0988c5c738a5c7cd1d273e3d470ec10d6cbefaa67fa40e4` |
| child/run.jsonl | `fb2d17133c1df0ba3761382657a5a1950ac04075d3c9ee7b23f0ef3165880c3f` |
| store/gym/events.jsonl | `6843943eafcf57855ac8ea890cdeae1ee6761d7d7a724f05bc1e9d689576e6ab` |
| parent-before.md = parent-after.md | `895ccbdcfdac4f46264ccfe354f9a6f83fe43708862fa85a4c689ccf6aeda84e` |
| child.md | `8730ecca71af832436ca265f508b00cefe8ec783ddca816e2410f9dea1fc6420` |

## 未证明与保留问题

这是正常路径、单模型、明确引导的功能验收。没有故障注入、跨进程并行写入、反馈关闭试局、旧版本迁移、极大材料压力或修复前后对照；相关工程测试需单独报告。父/子均在可控短局停下，没有要求通关。代理操作 Human 入口不替代人类玩家体验验收。

反馈重复及建议是否超出现有能力，需要二期评估，不以“模型提交了建议”证明产品必须新增该功能。反馈工具没有自动写入角色目标；模型主动在 say 中提到自己的建议是其自主游戏发言，与宿主自动传播反馈不同。原始请求是旧版本实际材料，后续重新导出或修改布局时应保留本次产物和对应 DLL 哈希，避免把新版呈现冒作本次输入。
