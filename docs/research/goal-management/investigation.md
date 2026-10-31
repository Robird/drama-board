# 刑侦与情报分析：把目标和判断分开

调查日期：2026-10-31。本文是针对[目标管理设计](../../design/goal-management/design.md)的研究建议，不修改已定范围，不声称现实刑侦方法已经验证了 LLM 的效果。

最可借鉴的不是“猜出别人真实目的”，而是保存判断的来源、替代解释和复议条件，使角色在信息不完整时仍能采取下一步。调查规范、分析方法与预测准确性的实验证据不是同一种东西。

## 1. 目标、已知信息和预测不能共用一个状态

ODNI 的 [ICD 203，D.6.e(1)–(3)，正文第 4–5 页](https://www.dni.gov/files/documents/ICD/ICD-203.pdf)要求区分来源信息、假设与判断，并区分事件发生的可能性和分析依据的可信程度。英国警务学院的 [Investigation process，“Building hypotheses”](https://www.college.police.uk/app/investigation/investigation-process?highlight=use+of+forces+powers)同样把假设作为待调查的解释。

映射到本项目，应明确区分：

- 我的目标：与阿青会合。
- 已收到的信息：阿青说他会去溪流湾。
- 我的判断：阿青目前可能在那里。
- 我的预测：如果他按原计划行动，傍晚可能遇见他。

首版无需增加四套对象；在目标的来源、进展、下一步文本中明确措辞即可。“对方声明”不能直接成为对方私有 Goal，更不能成为世界事实。后续若增加概率字段，也不能把模型随手填写的百分数当成校准结果。

## 2. 记录能区分解释的线索，而非堆积支持材料

Heuer 的原始 [《Psychology of Intelligence Analysis》第 8 章，ACH 步骤 3、5、6](https://www.cia.gov/resources/csi/static/Pyschology-of-Intelligence-Analysis.pdf)强调证据对不同解释的区分作用、尝试反驳假设，以及关键证据错误时结论是否改变。[警务学院“Making good decisions”，Evidence summary / Ensuring impartiality](https://www.college.police.uk/guidance/conducting-effective-investigations/making-good-decisions)指出初始判断可能影响后续信息解释，但没有宣称训练可以消除偏见。

我们的轻量版本可以是：只有当不确定性影响下一步时，写下一个替代解释和一个值得检查的问题。例如“未见阿青，可能已经离开，也可能还在途中；先检查最后约定时间是否已到”。无需每个采水目标附一张 ACH 矩阵。

限制：没有看见不等于不存在，只有具备观察机会时，缺失才可能有区分价值。环境不能为角色提供不可感知的对方坐标；多个模型重复同一猜测也不等于独立证据。

## 3. 信息获取可以是子目标，完成不等于证实原猜测

上述 [Investigation process，“Building / Testing hypotheses”](https://www.college.police.uk/app/investigation/investigation-process?highlight=use+of+forces+powers)通过不同解释产生调查方向；[National decision model，“Information”与“Options”](https://www.college.police.uk/app/national-decision-model/national-decision-model?highlight=national+intelligence+model)把当前未知和可选行动纳入决策。

迁移建议：允许普通 Goal 表达“核实渡口是否开放”，完成标准是获得足以决定路线的信息，而不是“渡口开放”。发现关门仍完成了核实，但“通过渡口离开”的方法需要修订。这样信息型子目标可以服务多个父目标，不必另建 InvestigationTask 类型。

“下一步”可写“询问渡口开放时间”，并简述答案如何影响行动。若即将失温，先避险而不是穷尽所有解释；调查收益应与时间、身体和机会成本一起判断。首版不引入信息增益计算器。

## 4. 新事实触发复议，不直接改写意图

[ACH 第 8 章步骤 8](https://www.cia.gov/resources/csi/static/Pyschology-of-Intelligence-Analysis.pdf)建议预先识别可能改变判断的后续迹象；[ICD 203，D.6.e(7)，正文第 6 页](https://www.dni.gov/files/documents/ICD/ICD-203.pdf)要求说明重要判断为何改变；[NDM，“Action and review”](https://www.college.police.uk/app/national-decision-model/national-decision-model?highlight=national+intelligence+model)包含监测实际结果和重新评估。

首版在挂起线索中写“再次遇见阿青时检查”“到傍晚仍未会合则修订路线”，仍由控制器显式复议。之后若接事件提醒，只生成“这个目标值得重看”的材料，不自动恢复、完成或切换焦点。封路否定一种方法，并不否定返回社会的上层目的；过去到过某处的记录也不应被覆盖。

目标修订只需保存简短的决定摘要与可用的感知引用，不要求输出完整推理过程。NDM 自身也强调记录应适度，而非把每次决定全部展开。

## 可玩验证

**错过会合**：阿青曾说会到溪流湾，阿白先到但暂未见人。对照两种真实原因：阿青晚到、阿青明确发消息改约。检查模型是否把缺席误写成背约，是否在消息出现后修订会合方法，以及等待期间是否保留补给目标。只向角色提供实际可感知信息，测试侧另掌握真相。

**渡口关闭**：角色同时想逃离和给同伴找安全路线，共享“核实渡口”子目标。发现渡口关闭后，该调查可以完成，两项上层目标继续；改走替代路线不删除原调查成果。可在 EX-01/EX-04 的小场景中实现，不需要刑侦玩法。

## 采用边界

- **首版**：明确目标与判断的措辞；允许信息获取目标；挂起记录复议线索；用上述反例检查模型行为。
- **延期**：结构化假设、证据关联、事件触发提醒和预测校准。只有文本材料反复失效时再增加字段与索引。
- **不搬**：执法权限流程、犯罪画像、全局嫌疑评分、每回合 ACH 矩阵、自动推断其他角色私有意图。公开规范提供方法启发，不证明这些方法必然提高本游戏模型的成功率；需与现有 handoff/Goal 基线实测比较。

资料核验：CIA 与 ODNI 原文 PDF 已读取；警务学院 APP 根链接直读遇到 403，使用同站带检索参数的官方索引内容核对命名章节；调查过程另核对[官方 2022 年 PDF，§5.3–5.4，第 28 页](https://assets.college.police.uk/s3fs-public/2022-12/Investigation%20process.pdf)。PDF 用于章节交叉核对，不据旧版推断现行法律要求。
