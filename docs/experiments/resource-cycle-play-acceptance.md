# ResourceCycle Console 采集切片验收

> 历史命名说明：本文保留当时的 ResourceCycle / Cycle 类型、命令与验证记录。当前应用已重命名为 Playground；现行入口与持久身份边界见[重命名说明](../../docs/implementation/playground-rename.md)。旧名称源码链接固定到重命名前的 Git 版本。

> 本文保留采集首片验收；当前恢复为 Fold → Complete → S，允许重算派生预测，仍不重复输入。存储形状与本轮证据见[运行时缓存验收](resource-cycle-runtime-cache-acceptance.md)，本页旧 schema/测试数量不作为当前基线。

> 2026-10-27。对应[可玩设计](../design/resource-cycle-play.md)。实现位于原 ResourceCycle 库与 CLI，没有修改 Kernel、Spatial 或上游仓库产品代码。

## 交付

- `CycleWorld.CreatePlayable()` 创建无销毁脚本的单地点世界，包含一个角色及独立背包子图。
- `ICycleDecisionSource` 是运行时输入接口，显式传入角色身份。Console、测试脚本共用行动校验；尚未实现多个角色或 LLM 输入者。
- `wait 1..24` 发布开始与完成，期间自然事件推进；`gather` 只允许成熟资源，同一事实中完成资源重置与背包增加。
- `--play [--store PATH]` 展示中文完整观察、受阻理由和已提交事件；原批处理入口继续保留。
- 可玩存档与批处理模式误用会在 pending 恢复/推进前拒绝；本片不提供旧 schema 存档迁移。

## 构建和测试

主线程串行执行：

```powershell
dotnet build DramaBoard.slnx -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Publish
dotnet test DramaBoard.slnx -c Release --no-build --no-restore -m:1 --logger trx --results-directory artifacts/resource-cycle-play-tests
dotnet build DramaBoard.slnx -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
pwsh -NoProfile -File scripts/Smoke-ResourceCyclePlay.ps1 -OutputDirectory artifacts/resource-cycle-play-acceptance
pwsh -NoProfile -File scripts/Smoke-ResourceCycle.ps1 -OutputDirectory artifacts/resource-cycle-batch-after-play
```

结果：Release 构建零警告/错误；全量 **211/211** 通过，0 跳过（Host 2、Kernel 114、Spatial 60、ResourceCycle 35）。新增 13 个测试案例见 [PlayableCycleTests](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Tests/PlayableCycleTests.cs)。

生成历史新增 8 份记录。World/Fact、两个枚举以及直接内联枚举的 Binding/Prediction 升为 v2，新角色/背包为 v1；最终 Schema Verify 通过。初次构建暴露 Binding/Prediction 的枚举形状随之改变，已同步升级声明，未手改生成历史。

全量测试之后只修正 CLI 文案（重复句号、内存模式取消提示），再构建并执行最终两份 smoke；没有为文案重复整个测试集。

忽略目录中的本机回执：

- `artifacts/resource-cycle-play-build.log`、`resource-cycle-play-build-verify.log`
- `artifacts/resource-cycle-play-tests.log` 与 `resource-cycle-play-tests/*.trx`
- `artifacts/resource-cycle-play-acceptance/acceptance.json`、逐进程输出、`final-observation.md`
- `artifacts/resource-cycle-batch-after-play/acceptance.json`

## 行为证据

### 手算轨迹与持久恢复

从 D0 16:00 开始，等待 6 小时至 22:00：月光草成熟，太阳花日落暂停在 2/3；采月光草，背包 +1，植物重新生长。再等待 9 小时至 D1 07:00：月光草已于 02:00 再次成熟，太阳花在日出后补足一小时成熟。此时分别采集，最终太阳花 ×1、月光草 ×2。

测试比较增量/全量的完整事实、世界状态和游标；覆盖自然成熟/日落与等待结束同刻的顺序、旧快照不变，以及等待开始后冷开续行。三个真实 E-only 故障注入点覆盖等待开始、等待完成、采集：先将 E 落盘再抛错，重开只 Fold/S，输入源调用和预测重算均为零，采集物品不会重复发放。

### Console 跨进程脚本

[Smoke-ResourceCyclePlay.ps1](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/scripts/Smoke-ResourceCyclePlay.ps1)执行实际 DLL 子进程：

1. 建档后 `look/help/未成熟采集/wait 0/wait 25/未知命令/quit`，比较存档所有文件的 SHA256，内容不变。
2. 第一进程等待 6 小时并采月光草，退出；第二进程继续等待 9 小时并采两种植物，与单进程相同命令的最终完整观察一致。
3. 冷开 `look` 后 EOF 正常退出；时间和背包不变。
4. 两种模式互相误开存档返回 1，存档文件内容不变；`--play --steps 1` 拒绝混用批处理参数。

旧批处理五进程 smoke 同样通过，继续比较连续、全量、分段冷开和最终重开的轨迹与快照。每次 smoke 使用新输出目录，不删除或重置旧存档。

### 独立试玩与复核

独立 agent 审查状态、候选身份、同刻顺序、纯 Fold 和输入边界后，使用已编译 DLL 另行试玩。产物为 `artifacts/resource-cycle-play-review/review-20261027-a/`。

首次路线在 D1 07:00 获得月光草 ×2、太阳花 ×1；重开只读/非法输入/EOF 保持不变；再次重开等待 3 小时并采太阳花，在 D1 10:00 两种物品各 ×2。各进程正常退出，日夜暂停、进度保留及输入冻结世界的文案可理解。复核发现受阻原因的句号重复，已修复并由最终 smoke 覆盖。

Kernel 输入取消通过门闩式测试验证，不依赖 sleep。主线程另在工具提供的 Windows PTY 中发送 Ctrl+C，进程结束且未再推进；该通道显示 EOF 退出文案、外层 shell 返回 1，因此**不把这项探针当作原生终端精确返回码 130 的验收**。程序自身的取消分支返回 130；真实终端差异未作完整覆盖。

## 边界与后续

当前仍为一个角色、一处场景、两种植物；没有移动、多人轮转、GUI、LLM、delta 或完整事件—状态因果 UI。即时采集不模拟作业中断；等待可以跨自然事件。近期结果仅保存角色最近行动文本，不是完整持久叙事档案。

下一步优先按[预测对象核心草稿](../design/prediction-objects-roadmap.md)统一预测生命周期，再扩展世界。这个草稿与本片并行形成，但本轮没有实施核心重构。当前可玩场景和批处理对照可继续作为重构的行为基线。
