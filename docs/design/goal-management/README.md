# 角色目标管理

状态：2026-10-31，用户选定的下一个里程碑；目前完成目标、设计与用户故事记录，尚未实施。这里的 Goal 是游戏角色的目标，不是 coding agent 的外部工作 Goal，也不是 Kernel 的转换规划 Plan。

让角色明确采纳的事情跨 Context 保留，在后续决策中自动可见；挂起仍属于未结束，完成或放弃后按结束记录政策保留。以可玩的环境验证目标管理，再依据使用证据提取可复用模块；更广泛的结构化经历、实体解析和记忆召回暂缓。

## 文档入口

| 文档 | 职责 |
| --- | --- |
| [里程碑](milestone.md) | 范围、建议切片、完成标准与延期边界 |
| [设计草案](design.md) | 目标与来源关联、焦点、状态、上下文和提交语义；区分已采纳方向与待裁决细节 |
| [角色决策规约](decision-policy.md) | LLM 固定提示词的四项检查、同回合续接、handoff 迁移与实验；不引入固定阶段管线 |
| [用户故事](user-stories.md) | 核心验收场景、反例与可扩展的游戏实验；不是新增玩法全量施工清单 |
| [跨领域研究综合](../../research/goal-management/README.md) | 军事、商业、刑侦/情报及 AI 规划的一手资料、迁移边界与实验候选；本稿已吸收轻量语义与验收建议，未引入自动规划或预测框架 |

后续实施方案与实验记录继续按仓库分类放入 `docs/implementation/`、`docs/experiments/` 的对应主题目录，再从这里链接；不在本目录另建动态项目状态副本。

## 与现有工作的关系

- [角色 Context](../character-context-model.md)与[共享无状态控制器](../stateless-character-controller.md)提供角色隔离、上下文材料和状态归属边界。
- [交流合作预演](../../experiments/survival-communication-acceptance.md)暴露了新分工未写入 handoff 后跨 Context 遗失的问题。目标管理应减少反复抄写长期事项的负担；是否改善决策要重新实测。
- [生存二期](../survival-demo-milestone.md)的实际人类合作验收仍未完成，单独保留；本里程碑不改写其结果，也不以该试玩作为本次文档工作的前置条件。
- [分层设计](../layering-and-composition.md)继续适用：先明确领域能力与游戏接线，程序集提取依实际消费者决定。归档 Player 规划仅供参考，不恢复旧运行时。

当前项目焦点和后续顺序统一见 [PROJECT-STATE](../../../PROJECT-STATE.md)，术语见[术语表](../../glossary.md)。
