# 角色目标管理：首版施工入口

> 历史命名说明：本文保留当时的 ResourceCycle / Cycle 类型、命令与验证记录。当前应用已重命名为 Playground；现行入口与持久身份边界见[重命名说明](../../docs/implementation/playground-rename.md)。旧名称源码链接固定到重命名前的 Git 版本。

状态：2026-10-31，A1/A2/B 已实现并通过工程验证，C 的核心场景试用已完成，模型自发行为与明确引导试验分别留证。范围以[里程碑](../design/goal-management/milestone.md)为准，本文保留实施切片与源码入口，不新增目标或代替真实模型验收。实际工程结果统一见[工程验收](../experiments/goal-management-acceptance.md)，模型行为见[试玩记录](../experiments/goal-management-playtest.md)。

## 合同与推进顺序

领域规则见[设计](../design/goal-management/design.md)，解析/提交/源码入口见[操作合同](../design/goal-management/operations.md)，快照与呈现见[容量和 Context](../design/goal-management/capacity-and-context.md)。使用[合同验收用例](../design/goal-management/acceptance-cases.md)选择测试边界，不能仅按命令各写一个 happy path。

| 切片 | 工作与退出证据 |
| --- | --- |
| A1 局部规则 | 在 ResourceCycle 应用内建立角色目标集合、Goal/Link、Focus 与有界敏感修订；严格 JSON 解析和候选状态转换。验证共享 DAG、末端回退/解绑后缀、纠错、no-op、限额与清理出口；不依赖地名/角色特例 |
| A2 发布与可玩入口 | 新游戏初始化、角色 interaction 重建路径、命令能力与途中白名单、Fact/Fold/校验、操作结果及修订/Schema；Human survival/duo 可实际编辑。验证隔离、世界不变、服务轮转、S 冷开及 E-only 恰一次，日志修复与正常回执相同 |
| B 跨回合材料 | 冻结目标快照、单次当前投影、有界回执、最近成功 show 失效、full/delta 与两种布局；能力同时接通 Completion 的新固定规约，替换长期 handoff 指引。量测最大合法图/Unicode 输入；连续操作不叠加旧全文，不裁危险/交流 |
| C 孵化与模型试用 | US-01 分工兑现、US-02 临时切换返回、US-03 共享探索及其变体；真实模型自行编辑、跨 Context 与至少一次落盘重开。保留原始材料、实际提示词、操作和失败，按里程碑而非漂亮单局判断 |

A1/A2/B 已形成可编辑、持久化及跨回合呈现的闭环并通过工程验证；C 已保留真实模型自行调用目标操作、跨 Context 行动和磁盘续局证据。显式分目标/共享关系的引导试验与模型自发选择分别报告，不能把机制可用扩大为一般自主规划可靠。

当前源码入口：局部模型/额度在 [GoalModel](../../src/Goals/GoalModel.cs)，语法与转换在 [GoalCommandParser](../../src/Goals/GoalCommandParser.cs) / [GoalRules](../../src/Goals/GoalRules.cs)，持久交互组合在 [CycleGoalInteractionRules](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/src/ResourceCycle/CycleGoalInteractionRules.cs)，当前材料投影在 [GoalView](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/src/ResourceCycle.Cli/GoalView.cs)，能力对应的固定提示词在 [LlmCharacterController](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/src/ResourceCycle.Completion/LlmCharacterController.cs)。

## 必须守住的接线点

- 角色私有目标集合独立于 CurrentContext；所有 interaction 构造/替换路径都保留它，不能在开启新回合、拒绝、handoff 或终局时丢掉。
- 原始操作、typed 结果、Changed、冻结材料及后态由同次权威规划记录并校验；pending 不再调用控制器或分配 Goal ID。
- 目标成功事实无需伪造世界经历，但须接通 `CycleOperationResults`，包括 show/no-op 的无 ObservedEvents 路径和宿主冷开日志补录。
- 目标字段加入冻结观察相等/校验；新实例共享控制器时不漏给其他角色，旧实例快照不原地改写。
- 显式检查原始输入、通用事件效果、历史视图等所有文字输出路径，目标长文不能从旁路重复进入完整模型输入。
- 当前无旧开发存档兼容负担；通过现有 GameRevision + Schema 验证形成可拒绝的版本边界，不顺带建设历史升级框架。

## 验证与证据

工程验证按实际改动选择项目构建、相关测试与 Schema Verify，补上真实磁盘 S/E-only、提交失败、无变化/拒绝及原有生存/采集回归。Human Console 至少真实运行一段创建、共享、更正、查询、满额整理和冷开流程；脚本可支撑确定性复现，但须说明操作来源。

模型运行前核对实际请求：目标能力与帮助一致、完整未结束目录可见、当前详情/回执有界、角色私有材料隔离、长期事项不强迫双写 handoff。场景观察世界事实和角色自评分别留证：赠水是否真实发生、目标是否被标完成、模型何时得到证据不能合并成一个“成功”。具体故事、真实调用次数不在本文另建一套口径。

实施阶段可调整文件组织、JSON 易用性和原型容量，但应同步合同与验收，不静默改变 Focus/状态、原子性、恢复或信息权威。剩余依赖真实运行的问题统一维护在[设计末节](../design/goal-management/design.md#7-剩余问题与重开条件)，不在这里再复制待办列表。
