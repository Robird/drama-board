# 角色跟随实施验收

2026-11-01，Windows / .NET SDK `10.0.201`。对应[角色跟随合同](../design/character-following.md)，实现提交 `ebdecebc4dfa8a6c1aa9ad0eff080e6a5d95d32d`。本记录区分自动测试、代理操作真实 Console 和真实模型试玩；没有以代理代操冒充真人体验。

## 实现范围

- `CycleCharacter.FollowingLeaderId` 是唯一关系权威；`CycleFollowingRules` 在游戏内派生成员、校验直属同地关系并清理终局。无 Party、成员缓存、FollowId 或新的到达阶段。
- 共享 parser / Check / Plan 提供 `follow`、`unfollow`、`dismiss-followers`。跟随者不得独立 `go` / `gather` / `wait`；领队普通 go 原子记录全部成员出发，个人身体、提醒、Context、操作身份和地图仍独立。
- 逐实体到达保留，关系校验接受严格限定的同刻混合位置；全部同刻到达先于输入。终局在同一事实解除关系，幸存队员保留已启动行程。
- Follow 确认本人提醒并关闭 Context；Unfollow / Dismiss 保留提醒且不放行。队员原 Follow PendingAdvance 可以跨多段同行保留；解除关系后按原操作交付。唯一决策谓词为 `CycleActions.NeedsDecision`。
- 冻结观察、个人经历、端点变化、两种 Console 布局和 LLM 材料包含关系及可执行性。模型不需要反复为队员提交移动。
- `--survival --characters 1|2|3`，新局默认 1，续局缺省读取存档人数；显式不符拒绝。多人 `--llm` 将 a 绑定 Human，其余绑定同一串行模型控制器与共享预算；`--duo` 只作为参数解析别名。

GameRevision 从 16 提升至 **17**，生产拒绝旧开发存档。新增九份 Schema history：Character 8、CommandKind 4、Command 5、EventKind 11、Fact 14、Observation 6、OtherCharacterObservation 2、ObservedEventKind 3、ObservedEvent 3。包含枚举的类型也提升版本，未绕过 Schema 校验。

## 自动验证

主线程串行执行，Schema 首次生成用 Publish，收尾用 Verify：

```powershell
dotnet build DramaBoard.slnx -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Publish
dotnet build DramaBoard.slnx -c Release --no-restore -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
dotnet test DramaBoard.slnx -c Release --no-build --no-restore -m:1 --logger trx --results-directory artifacts/following-test-results
```

最终构建零警告、零错误，全部当前 Schema candidate 通过 Verify。首次全套 1,330 项中 1,329 通过、1 项新夹具失败：`trail-2` 通往 ridge，但测试错误地只为领队设置 cabin 访问史。修正为 ridge，并增强为前后都明确显示“风口山脊”，队员仍未知；没有修改生产地图或降低隐私断言。随后重新 Verify 构建并复验受影响项目：

```powershell
dotnet test tests/ResourceCycle.Tests/ResourceCycle.Tests.csproj -c Release --no-build --no-restore -m:1 --logger trx --results-directory artifacts/following-retest
dotnet test tests/ResourceCycle.Completion.Tests/ResourceCycle.Completion.Tests.csproj -c Release --no-build --no-restore -m:1 --logger trx --results-directory artifacts/following-retest
```

最终各项目的实际通过证据如下，合计覆盖 1,330 项，没有跳过项；全套首次执行和修复后的定向复验分别保留原始结果，不伪称全套第二次重跑。

| 测试项目 | 通过 | 证据 |
| --- | ---: | --- |
| ResourceCycle | 441 | 修复后全部复验 |
| ResourceCycle.Cli | 349 | 全套执行 |
| ResourceCycle.Completion | 45 | 最终提示文案修改后全部复验 |
| Kernel / Spatial / Host | 131 / 91 / 2 | 全套执行 |
| Inventory / Goals | 36 / 31 | 全套执行 |
| CharacterControl.Completion / CharacterGym | 11 / 30 | 全套执行 |
| Microtown / Microtown.Cli / Microtown.Completion | 142 / 15 / 6 | 全套执行 |

原始证据：`artifacts/following-build-final.log`、`following-tests.log`、`following-domain-retest.log`、`following-completion-retest.log` 和上述两个 TRX 目录。未运行 Linux CI；真实模型调用不计入离线测试数量。

| 合同边界 | 具体证据 |
| --- | --- |
| 关系与命令 | [FollowingRulesTests](../../tests/ResourceCycle.Tests/FollowingRulesTests.cs)：parser 往返、场景边界、自环/链/未知/远处目标、队员权限、忙碌领队、本人提醒确认、解散与退出保留提醒 |
| 出发、到达和终局 | [FollowingMovementTests](../../tests/ResourceCycle.Tests/FollowingMovementTests.cs)：精确全员出发、不同 MovementGeneration、单人无法出发全员拒绝、领队 go 的提醒所有权、个人死亡/天气死亡/出口、三人完整采集再集合；逐步 Validate 与 Rebuild 对照 |
| 个人可见性 | [FollowingObservationTests](../../tests/ResourceCycle.Tests/FollowingObservationTests.cs)：队员移动经历及未知目的地、忙碌领队的成员材料、解散、领队死亡不是队员死亡；漏记队员经历的事实拒绝 |
| Context 生命周期 | [FollowingContextTests](../../tests/ResourceCycle.Tests/FollowingContextTests.cs)：多段路不调用队员控制器、say→follow 休眠、原地 continue、保留队员已打开 Context 与草稿 |
| 磁盘恢复 | [FollowingRecoveryTests](../../tests/ResourceCycle.Tests/FollowingRecoveryTests.cs)：原地跟随、途中、三人到达中间、解散后四处 S 冷开及四处 E-only/pending；禁止恢复调用控制器或 Plan，继续完成剩余同刻到达 |
| 宿主/呈现 | [DuoHostTests](../../tests/ResourceCycle.Cli.Tests/DuoHostTests.cs)、[FollowingViewTests](../../tests/ResourceCycle.Cli.Tests/FollowingViewTests.cs)：三人实际路由、共享调用预算、保存人数恢复与冲突、两布局冻结材料、终局/非生存边界 |

## 真实运行

[三人 Console 试玩](character-following-console.md)由独立代理操作实际进程完成：a/b 跟随 c，三段往返同行，解散后各采食/食用，重新集合后一起离开。32 条新游戏操作中 26 成功、6 次预期拒绝；四条成功 go 均属于 c。另验证途中提醒补给、同地通信后 follow、多个冷开边界、人数不符拒绝、两布局和最终只读恢复游戏文件哈希不变。主线程回读配对操作记录和实际二进制哈希核验。

两人真实 LLM 闭环使用通过普通 Console 采集补给、创建协作目标得到的受控起点；需求与天气仍启用，未修改世界数据。Human 入口由代理操作，通过游戏内 say 协调。主线程独立解析两份 audit，核对模型输入/输出次数、已提交操作、两人到达与停止原因：

| 领队 | 真实模型 / 调用数 | 已确认轨迹 | 结束边界 |
| --- | --- | --- | --- |
| Human a | b 使用 `gpt-6-luna`，7 次 | b follow a；a 连续两次 go，途中不调用 b；20:00 解散后双方采水各得一份；b 再 follow，a 带队离开；警告时 b continue | 21:15 双方抵达营地，Human 正常退出 |
| LLM b | `gpt-6.1-sol`，8 次 | b 接受领队分工，a follow b；b 两次 go，双方在19:00/20:00完成到达；b 解散后双方采水；20:15 b 邀请，a 再 follow，b 带队出发 | 21:00 第三段途中警告时 Human 正常退出；未声称第三段抵达或获救 |

具体目录为 `artifacts/character-following-llm-20261101/prepared-human-leader-r2/` 与 `prepared-sol-leader/`，均有真实请求、响应、已提交事实和 `human-session-ended`。后者的 Completion 适配 DLL 哈希与最终构建一致。

失败样本也保留：轻量模型初次解散后自行离队，另一次领队局两段后未按约解散；一次 transport failure 由宿主暂停，之后显式新建宿主继续已保存状态。这些不计作通过，不以修改游戏规则替模型兜底。调用配置、完整轨迹和前置目标原文见[模型试玩](character-following-llm.md)。本轮证明真实控制器可使用跟随完成合作流程，不证明任意模型能自发记住约定；既有真人合作试玩仍需要实际人类参加。

## 来源与限制

构建使用本地源码 ProjectReference，未发包或发布：Completion `97f755f184a1027323e270d16f91a3f89b134701`；DurableGraph `f7f0c28bacaa67993b7b07a436c3d82bc4ebdda8`；Storage `9ad39fc51a420e8302468533b82d8bc3a91a403e`。

最终 Release DLL SHA-256：

| 文件 | SHA-256 |
| --- | --- |
| DramaBoard.ResourceCycle.Cli.dll | `7D93CE78DC6FF14E6E03CD907C37EB348C34D38EA9111152CE8375CAFAE33E68` |
| DramaBoard.ResourceCycle.dll | `D42CB7D49EC40345BD77D83D5C2576C4B3FFB49A0B521C483E28214502DE6119` |
| DramaBoard.ResourceCycle.Completion.dll | `D5DD06366421C3CC1FB948326FFF76990A90633CB13B5DA54D41F6D40B54F3DD` |

已保留设计的产品边界：没有自动等待未加入的队友、途中主动脱队、异步 Console 插话、共享私有知识或自动补给。真人只能在自己的决策窗口退出跟随；这些不是本轮遗漏接线。是否扩大这些能力应根据真实体验重新裁决。
