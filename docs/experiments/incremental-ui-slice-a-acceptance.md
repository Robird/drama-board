# 增量 UI 切片 A 验收

日期：2026-10-28。依据[切片 A 合同](../design/incremental-ui-slice-a.md)实施；领域与 CLI 分包施工，另设独立源码审查、Console 试玩与 PTY 试玩。主线程核对关键源码、运行集成构建/全套测试、比较输出轨迹并验收修复。

## 交付范围

- `--survival` 的 decision/classic 两布局支持 full/delta，默认 delta；`:mode` 与 `:help` 枚举参数，切换及 `:look` 全量重同步。`--play` 明确仅支持 full。
- 事件先行、实际效果缩进、当前状态按键去重；补给上限裁剪、库存归零、期间身体变化、天气扣除、庇护/昼夜速率、采集产出及结束、通知确认与终局均接通。
- 事件材料随 E 保存；正常操作答复与冷开缺结果补录共用材料，不重新规划或重复执行。Fact Schema 7，GameRevision 6，不读取旧开发存档。
- 只有已提交世界进入统一输出；成功 Write/Flush 后安装基线，实际进入控制器才声明可输入。首次、新回合、冷开全量；终局一次完整尾部。
- 没有新增最近 n 回合的持久历史集合、Kernel 接口、LLM 适配器或屏幕交付重放。B 的边界见[更新后的阶段目标](../design/incremental-ui-milestone.md)。

## 工程与语义验证

主线程实际执行：

```powershell
dotnet build DramaBoard.slnx -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
dotnet test DramaBoard.slnx -c Release --no-build --no-restore -m:1
```

构建通过，0 警告、0 错误；Schema Verify 通过。全套 **646 项通过、0 失败、0 跳过**：Host 2、Inventory 27、Kernel 131、Spatial 91、ResourceCycle 239、CLI 156。

| 合同 | 证据入口与实际覆盖 |
| --- | --- |
| 真实效果与顺序 | [ObservedEventTests](../../tests/ResourceCycle.Tests/ObservedEventTests.cs)：90→100 补水实际 +10、最后一份扣减、自然变化/寒风分离、黄昏旧速率积分、最终采集产出与结束分离、隐式确认、草稿交付消费、远处资源隔离；篡改材料、伪造中间值及不同状态键事件换序被拒绝。 |
| 磁盘恢复与操作答复 | 同上及 [OperationRecoveryIntegrationTests](../../tests/ResourceCycle.Cli.Tests/OperationRecoveryIntegrationTests.cs)：真实 pending E、已完成 S 但缺宿主结果、后续身体已改变；恢复仍使用原实际收益和原操作身份，不重复执行。 |
| 快照和更新 | [IncrementalProjectionTests](../../tests/ResourceCycle.Cli.Tests/IncrementalProjectionTests.cs)：语义重建、净零事件、物品归零一次、视野离开/返回、风险撤回、警告上下文、其他角色隔离、提交来源不重编号；历史撤回保留全部语义键且不伪装成当前身体消失。 |
| 输出会话 | [IncrementalSessionTests](../../tests/ResourceCycle.Cli.Tests/IncrementalSessionTests.cs)：写入失败不安装基线、提交不冒充输入、两个布局零耗时操作不全量重绘、重同步、mode 帮助与多人回退、全量等待风险、Pending 说明、无变化答复、终局完整尾部。 |
| 原玩法回归 | 既有 Survival/Weather/Continuity/Spatial 测试同轮通过，包含庇护抵达、同刻仲裁、出口/死亡边界、终局与 E-only 恢复。 |

没有新增整屏快照、章节位置或精确缩进断言。语义撤回允许在文本层按历史摘要分组，测试仍核对各状态键的值。

## 独立审查与试玩修复

独立源码审查发现并复核关闭：全量页面遗漏了基线里的长等待风险/历史库存；Pending 阶段误称首次决策；零变化操作没有可见答复。主线程另核对效果端点、草稿消费归属及恢复结果共享入口。最终源码审查未发现新的必须修项；审查者没有把只读检查当作运行测试。

首次独立 Console 试玩仅依据 UI 选择行动，6 个决策回合后于 D0 22:30 抵达山脚公路获救，结束温暖 4、食物 62.5、饮水 88。实际看到补水 +30、+28 的上限裁剪，最后一份归零，途中补给保留温暖警告和原抵达期限。主动 `:look` 一次用于验证命令，没有因信息不足补救重绘；未报告 UI 导致的错误行动。

独立 PTY 试玩选择长等待至饥饿失败。原始 ANSI 流显示同回合查询/切换/补给没有清屏，新回合出现回顶和逐行覆盖；自然温暖 +6、寒风 -10、当前 62，以及昼夜速率变化可分别辨认。终局完整画面一次，进程正常退出。

两路都发现同一可读性缺陷：旧 Incoming 移除时，稀疏键丢失原标签，输出大量裸内部字段以及容易被误认为当前状态的“食物：无、活动：无”。修复为从当前或上一份可见快照保留标签，整体历史摘要撤回用一条明确说明；通路撤回保留对象 ID，内部值改为中文显示。零耗时阶段文案改为“正在处理结算，尚未进入下一次输入”，不声称时间流逝；等待历史不再显示采集 0/0 份。

两名试玩者均在最终构建上定点复验通过。Console 原采集接受块从 43 条当前项降至 13 条，裸内部标签从 16 条降至 0；没有假身体“无”。PTY 再次确认修复和终局去重。复验没有重复整条获救路线；各游戏/驱动已退出。

本地原始工件（`artifacts/` 不入 Git）：

- `artifacts/incremental-ui-a/playtest-console-20261028-224423.txt` 及同名前缀输入文件。
- `artifacts/incremental-ui-a/playtest-pty-raw.json`、`playtest-pty-report.md`。
- `artifacts/incremental-ui-a/retest-console-20261028-225044.txt` 及输入文件。
- `artifacts/incremental-ui-a/retest-pty-raw.json`、`retest-pty-report.md`。

## 同轨迹输出对照

主线程在最终构建上分别以 full/delta 执行相同操作：初始补水、写入/重复/清空交接、等待、去溪流湾、采集两份浆果、连续吃两份、`:look`、退出。两个进程均正常退出，最终 `:look` 全量画面逐字相同。该对照证明本条轨迹的最终可见状态一致；没有把文本比较宣称为全部隐藏业务对象的逐字段证明。

| 模式 | 输出字符（Unicode 字符数） | 输出行数 |
| --- | ---: | ---: |
| delta | 20,386 | 731 |
| full | 46,379 | 1,503 |

含启动、模式重同步、全部过程、提示与最后 `:look`，delta 字符约减少 56%、行数约减少 51%；没有只截取短正文。原始输入/输出及统计见 `artifacts/incremental-ui-a/comparison-{delta,full}-{input,output}.txt` 与 `comparison-metrics.json`。这不是 token 计数，也不是对所有路径的压缩率承诺。

## 验证边界

PTY 提供 ANSI 文本流而非终端截图，足以核对调用边界，不能证明所有终端的滚动历史清除或视觉排版。一次中文 PTY 输入被拒、ASCII 正常，可能受工具输入编码影响；重定向中文交接正常，本轮未定位该终端输入现象。没有进行真实 LLM 决策、prompt-cache、Linux 终端或多控制器增量分发验证。

B 仍需先裁决历史窗口/容量、实际发生与首次获知、规划时绑定持久来源身份，以及历史与 Incoming 的信息重复；不因 A 保存了 E 材料就宣称角色已拥有跨回合经历列表。
