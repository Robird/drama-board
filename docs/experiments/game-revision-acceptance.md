# ResourceCycle 初始化与游戏修订首片验收

日期：2026-10-27。范围对应[设计决策](../design/game-initialization-and-revision.md)：区分存档还原与运行就绪，版本贯穿创建、E/S 保存、加载和正常恢复。没有扩大 Kernel 的职责或增加生产历史兼容政策。

## 实施边界

- [CycleGameFactory](../../src/ResourceCycle/CycleGameFactory.cs) 返回 `(World, Cursor)`，batch/play 的内存与磁盘新游戏消费同一初始化结果。默认子图各次独立；传入实体保留，输入序列复制。
- [CycleDurableRoot](../../src/ResourceCycle/Persistence/CycleDurableRoot.cs) 保存 `GameRevision`，Schema 升为 v2；新增 [CycleDurableEvent](../../src/ResourceCycle/Persistence/CycleDurableEvent.cs) 保存自己的修订号及 occurrence，Schema 为 v1。保留旧 Schema 记录不代表支持旧无版本存档。
- [CycleDurableHistory](../../src/ResourceCycle/Persistence/CycleDurableHistory.cs) 按还原 S/E、历史结构检查、内存修订、完整校验和 Rebuild 的顺序打开。就绪根与 runtime wrapper 共享 Business；WorkingCopy 继续管理磁盘基线，提交成功才安装新视图。
- [CycleGameRevision](../../src/ResourceCycle/Persistence/CycleGameRevision.cs) 使用普通函数协调转换。State 单步不接收 pending；先把 S 对齐 E，再逐步一起转换。检查修订恰好加一、游标及事件身份不变。具体转换仍有保持输入快照和历史语义稳定的职责，编排器不尝试通用证明。
- **生产当前修订为 1，仅接受当前修订。** internal 测试接缝让 v1/v2/v3 夹具走真实 adapter 和提交路径；这些编号是 `GameRevision`，夹具使用同一套当前 Schema，不证明历史 Schema 解码或升级。这些人工转换不是生产兼容史。正常恢复仍由 Kernel 的 `RecoverPending` 执行，不在加载阶段 Fold、Plan、询问控制器或发布。

## 自动化证据

| 合同 | 证据 |
| --- | --- |
| 工厂初态独立、输入 ownership、内存/磁盘一致 | [CycleInitializationTests](../../tests/ResourceCycle.Tests/CycleInitializationTests.cs)：batch/play × 昼夜初态，冷开后继续比较 8 个转换的世界、事件和游标。 |
| S/E 修订号真实落盘，运行预测不落盘 | [CycleStorageShapeTests](../../tests/ResourceCycle.Tests/CycleStorageShapeTests.cs)：直接读取 DG 图，检查根/包装字段及模型目录。 |
| 加载不写盘、不结算、重复重开稳定 | [CycleRevisionTests](../../tests/ResourceCycle.Tests/CycleRevisionTests.cs)：S-only 与 S/E 升级到 v3，比较所有存档文件的 SHA256、游标和未执行的库存。 |
| 旧 S、新 E、再跨一版恢复 | 同文件的 `Old_state_new_event_crash_aligns_state_before_multihop_event_conversion_and_recovers_once`：S1 → 内存 S2 → 真正 Kernel Plan/CommitEvent E2 → 抛错阻止 S → v3 重开；直接核实磁盘 S1/E2，复现原 S2，E 仅转换 2→3。 |
| 跨图替换事实不覆盖修订结果 | 夹具逐步重命名角色/物品并各增加十件背包物品，E 转换读取每步原/新 State；恢复恰好多一份产出，无旧 ID 回流。v3 提高采集门槛仍保留已决定的归属，不重新裁决命令。 |
| 发布成功后返回异常 | `Successful_state_publication_followed_by_failure_is_not_applied_again_after_reopen` 在真实 S 提交后抛异常；Kernel fault，冷开识别完成态，不重复 Fold 或推进游标。该证据模拟调用边界异常，不是断电/文件系统故障实验。 |
| 旧表示允许修复，当前/最终状态仍严格校验 | 先存一个不符合当前业务规则的候选，通过修订修复后才能交付；当前版本跳过转换仍拒绝失效业务。普通根构造仍拒绝过期资源状态。 |
| 不支持或坏边界拒绝且不写盘 | 未知 S/E 修订、逆序版本、缺失/错误步骤、游标/事件身份改写、删除 pending、无版本事件包装均有拒绝证据。 |

原有 [CyclePersistenceTests](../../tests/ResourceCycle.Tests/CyclePersistenceTests.cs)、[MultiplayerRecoveryTests](../../tests/ResourceCycle.Tests/MultiplayerRecoveryTests.cs) 等同时通过，覆盖 E-only、拒绝提交后重开、就绪 wrapper 安装时机及不重做控制器/Plan。

主线程运行：

```powershell
dotnet build src/ResourceCycle.Cli/ResourceCycle.Cli.csproj -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Publish --no-restore
dotnet build DramaBoard.slnx -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify --no-restore
dotnet test DramaBoard.slnx -c Release --no-build --no-restore -m:1
```

首次构建新增 2 份 Schema 记录；随后完整构建与 Verify 为 0 警告、0 错误。测试 325 项通过：Host 2、Kernel 131、ResourceCycle 101、Spatial 91，无跳过。此为 Windows 本地证据，未运行 Linux/远端 CI。

## Console 实际运行

使用 Release DLL，批处理内存直跑到绝对小时 88；磁盘路径先运行 5 步，关闭重开再到小时 88。两份 `--snapshot` 完全相同，累计 29 次转换，SHA256 为 `EA199EED12637CB399D9A5A61686E77880E5E3CD65B11A69481F0B21B20326EB`。

可玩路径使用同一 Console：a/b 各安排一份太阳花，c 等待，退出时保留 a 的加入感知及未完成活动；冷开后 `look` 保留这些信息，`continue` 后 b 在 16:48、a 在 17:00 各取得一份，库存降至 1。继续等待经过 18:00 天黑，在 b 的 18:48 决策点退出，再冷开仍在此处。输入中的不合法动作没有推进世界或消除通知。

本地诊断日志位于忽略目录 `artifacts/game-revision-smoke-113f1dd21ca04bba9fef85803a5d915a/`，不作为必须随 Git 分发的测试依赖。

## 审阅与后续

实施分为持久化、初始化和恢复夹具三个工作包；独立审阅检查版本贯穿、旧 S/新 E 窗口、完整校验位置及加载副作用，主线程复核源码并执行集成验收。

首个真实规则修订出现后，需要定义并测试实际相邻转换、业务 ID 映射、活动进度政策和支持范围。多模块版本、持久替代 E、交互式过程 E、通用迁移接口继续延期。本片不承诺旧无版本存档兼容，也不为其补造默认修订号。
