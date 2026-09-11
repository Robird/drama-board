# DurableGraph 下的 Occurrence 提交与恢复

> 状态：2026-09-12 用户授权实施，本批采用以下提交与恢复语义；**代码迁移进行中**。接口名为职责示意，具体签名随实现收敛。
> 本文细化[独立 E/S 消费者合同](../research/event-journal-state-store-draft.md)在 DramaBoard 的映射；任务顺序只维护在[近期计划](../worksets/durablegraph-first-integration.md)。

## 1. 保留什么，改变什么

保留统一 Forecast、确定性单 winner、1ms 时间、同刻因果序号、可信 rule 规划和 Game + Spatial 完整提交。Spatial 的运动、接触、导航与 Player 信息边界不变。

改变的是 [SimulationKernel](../../src/Kernel/Simulation/SimulationKernel.cs) 的 `AppendBatch → 安装世界` 接缝：历史成为 `S0 → E1 → S1`，正常恢复加载 S，只有 pending E 需要执行尚未完成的一次领域处理。

首片推荐继续纯 fold、不可变替换式领域更新。DurableGraph 支持 `CommitDomainState(nextState)`；完整 State 根可以每次替换，**不再要求为旧 GraphSession 根限制建立稳定可变外壳**。包装完整 State 的理由仅是将世界与 Kernel 边界共同保存。

## 2. E 的业务含义

推荐 E 表示：**winner 的可信 rule 已经生成，并通过完整世界预检的一次有序领域变化**。它不是 Forecast candidate、原始 Player proposal，也不是每一条子 fact。

```text
OccurrenceEvent
    CauseKey                  // 当前 CandidateKey 的规范内容
    TargetInstant             // 拟完成的 LogicalInstant
    Facts[]                   // 非空、有序、强类型；同一次 Occurrence
```

沿用现有 FirstBoardFact / GameBoardFact / SpatialBoardFact 及其 payload 的业务含义，按 DG 支持的类型形状改声明。它们本来就是领域内存对象，不必发明第二套 JSON 事件正文、通用 command bus 或 SavedFact 映射。

事件通过业务 ID、值及必要快照描述变化，不回指当前世界、会话或 Player。集合及元素也必须保持记录时的内容；只读接口和 DG 捕获都不自动隔离可变别名。

### 为什么不先记录“请处理这个 winner”

| E 的位置 | 可获得的语义 | 本轮判断 |
|---|---|---|
| Plan 前 | 可以记录尚未完成的决策机会；冷开可能还需询问 Player | 涉及持久决策请求、重问和取消策略，不作为首片默认。 |
| Plan 后、fold/验证前 | 就地处理更直接；无效 draft 也可能成为永久 pending E | 可行，但改变现有领域错误的历史含义，需另行采纳。 |
| Plan 与 scratch 验证后 | 已拒绝的 proposal / 无效 draft 不进入历史；E-head 不重问 Player | **推荐首片**，复用当前已验证的处理边界。 |

这项选择不提供完整决策尝试审计。Player 已返回而 E 尚未发布时中断，下次仍可能重问；若必须保留这段交互，需要另定 Player 持久化合同。

## 3. S 是完整可续局边界

建议由 FirstBoard 的 State 根组合以下内容，不把 Kernel/Spatial 的运行服务一起持久化：

| 内容 | 必须保留的语义 |
|---|---|
| FirstBoardWorld | 完整 Game + Spatial；Game 的 WorldSeed、NextPersistentId、Actors/Objects、知识全文、活动、DecisionSequence、TravelGoal、机关标志、PendingEncounter 均在内。 |
| Spatial 动态状态 | location 的完整派生类型、anchor/time/speed/arrival、movement generation、entry overrides、scheduled patches、consumed contacts。位置、路线和关系仍按需推导。 |
| Kernel 游标 | WorldVersion(LineageId, TransitionCount)、GenesisTime、LastCommittedInstant、LastCauseKey；最后一项替代当前通过 Journal 尾部做的立即重复 cause 检测。 |
| 运行绑定 | 精确 Definition 内容/身份、影响未来行为的 Ruleset 标识及配置，包括同刻预算；固定测试 driver 的主体集合与策略合同由测试绑定并校验。 |

`LastCauseKey` 是明确的有限恢复游标，不嵌入上一整份 Event。初始化时 count=0、last instant/key 为空；完成一次 S 才共同更新它们。

沿用 Game.Now 和 Game.WorldSeed 时，Kernel 的当前时间/规则种子必须与它们对齐；不另造第三份时钟或种子。恢复先校验完整边界，再交给查询与下一轮 Forecast。

Definition 首片可复用 [ScenarioInstance](../../src/FirstBoard/ScenarioDefinition.cs) 已有的 canonical 内容与 hash：保存精确内容，重建 GraphDefinition 并验证绑定。这里的内容 JSON 是现有内容格式，动态世界与 E 仍由强类型领域模型保存。不能只保存场景名字再加载当前默认地图。

规则对象、driver、GraphDefinition 查询索引、candidate/owner map 和呈现上下文留在图外。构造器不在 DG 恢复时运行，应用需显式完成领域校验与 Transient 重建；不能只依赖构造器中的规范排序和合法性检查。

## 4. 正常 Step 与恢复

```text
从已完成 S 取得 frozen World 和游标
→ Forecast / 选 winner / 检查 notAfter 与预算
→ PlanSelectedAsync（必要时调用 Player）
→ 按顺序纯 fold 全部 facts，验证 scratch World
→ 构造 E、next S，完成最后一次取消检查
→ CommitDomainEvent(E)
→ CommitDomainState(next S)       // 热路径直接复用 scratch
→ 安装已完成边界，交付本次完成通知
```

E 发布后至 S 发布之间不重新 Forecast，不再观察普通取消请求。E 代表持久待办；只有 S 的成功发布才完成本次 Occurrence、推进版本/逻辑时刻并允许下一次 Step。两次提交不是一个存储事务，不能声称 E/S 同时发布。

恢复入口先 `OpenExisting / Resume`，然后：

- **S-head**：校验绑定与 S，直接交付；不 fold 历史、不调用 Player，也不自动创建新 E。
- **E-head**：取得 DG 配对的前 S 与 pending E；校验 cause/目标时刻/预算与前 S 的关系，按 E 的 facts 纯 fold、验证并提交一个 S。既不重新选择 winner，也不调用 Plan/Player。
- E-head 的完成是已有工作的恢复，不受一次新的 `Step(notAfter)` 所选上界重新裁决。恢复可以在开始前取消而保持 E；开始提交后遵守相同不可取消区间。
- 读取、绑定、纯 fold 或验证失败时报告并停止，未执行新的提交；不跳过 E、不自动回退、不生成伪成功 S。提交报错则可能已经推进 head，必须按下一节重开判定。跨版本的 pending 处理须满足声明的 Ruleset/Schema 兼容规则。

同一个纯 fold 既可计算热路径 scratch，也可处理冷恢复 pending E；热路径不为持久化再 fold 一遍。S-head 恢复执行历史 fold 的次数必须是零，E-head 只处理这一个 pending occurrence。

## 5. 失败与观察者

| 边界 | 对外结果 |
|---|---|
| Forecast / Plan / scratch 验证 / 最后取消失败 | 无新 E/S，已完成世界不变；保留现有非法 proposal 与世界内合法失败的区分。 |
| E 提交失败 | 停止当前提交尝试，关闭并重开检查实际 head；不能从异常推断 E 一定未发布。 |
| E 已发布，S 尚未发布 | 上次 S 仍是最新完成世界；E 在历史中可见但待处理，不将它呈现为已完成 gameplay transition。 |
| S 提交报错，包括 ref 已发布但调用未返回 | 停用本次会话/Kernel，重开按 S/E head 恢复；不透明重试、不复用旧 CLR 世界继续运行。 |
| S 正常提交成功 | 安装世界并交付完成材料；之后取消不撤销结果。 |

DG 不回滚应用内存，也不保证外部副作用恰好一次。首片采用保守的停止/重开策略，不为不同异常类别建设通用重试框架。

现有 LiveAuthorityLoop 从 `journal.Batches[^1]` 取呈现材料，应改为消费**S 成功后的本次完成结果**。可继续用其中的 facts 在独立 presentation world 上 fold；这是展示投影，不再是存档恢复 authority。S-head 重开直接从保存的 S 建立展示起点，不重新播报已完成事件；E-head 则从前 S 建立起点，pending 成功完成后交付本次完成材料。该通知不保证跨进程恰好显示一次；需要完整历史播放时显式读取历史。

## 6. 模型与模块接缝

- 在现有 Kernel / Spatial / FirstBoard 项目中声明需要保存的真实类型；record class 改为受支持的 partial durable class，值类型保留原有值语义并显式标记。具体持久数组/List 放在字段上，可继续公开只读视图。
- 保留替换时对未变子对象的共享，不做整世界深拷贝。`with` 改写和语义相等是实际工作，尤其 contact key 的冷 E/S 比较不能退化为引用相等。
- Kernel 的新历史接缝只承担取得完成边界/pending、记录 E、发布完整 S；DG adapter 负责模型登记、frame 与会话。exact 签名在首片实现时固定，不预建多后端平台。
- 内存实现使用同一 E/S 合同服务现有调度测试；它不是第二份落盘 authority。DG 路径不双写旧 AteliaJournalSink，也不为喂旧 count 校验而重建整链 InMemoryJournal。
- 模型程序集直接用 DG Runtime 包及其生成/history 工具；只有实际持有 repository/session 的适配层需要 StateStore。调整禁止所有包的旧 dependency guard，同时保留 Kernel/Spatial 禁止依赖 Player、Host、FirstBoard 等方向约束。

## 7. 可变模型的重开条件

就地 apply 在单 writer、失败后丢弃会话并重开的模型下是可行的，不必有 undo log 或通用事务系统。本轮不推荐先改它，是因为所选 E 前预检已经产生 scratch，直接把 scratch 保存为 S 最少增加机制；稳定实体回写反而需要第二次 apply 或受控复制。

若真实改写证明复制/update 方法的维护明显更重，或写入/分配成本不可接受，再选择局部稳定实体或改为 E 后 apply，并同时调整预检、失败后视图与呈现快照合同。不要为了实现形式统一而全库可变化，也不要把“必须先测到性能瓶颈”当作改善可维护性的唯一理由。
