# Playground 命名与持久身份切换

2026-11-02。应用用途改名为 Playground，山野镇 / Hillside 内容身份保持。此变更不提供旧开发存档兼容，不扩展玩法。

## 声明级映射

实施前声明清单以 baseline `8401bf0` 为依据，保存在[完整声明映射](playground-rename-symbols.json)（138 个唯一前缀声明，含 partial 声明位置）；以下规则完整定义这些声明的目标名：`CycleX → PlaygroundX`、`ICycleCharacterController → IPlaygroundCharacterController`。另有 `PlayableCycleTests → PlayablePlaygroundTests`，共 139 个唯一声明。

三个产品项目及对应测试项目的 `ResourceCycle` 前缀改为 `Playground`，项目目录、csproj、同名类型文件和 solution 引用同步；`DramaBoard.ResourceCycle*` namespace、程序集和 IVT 同步切换。没有保留转发类型或兼容程序集。实现使用 Roslyn 语法树按已盘点声明标识符精确重写引用，不进行 Cycle 子串替换；项目路径与品牌字符串另行处理。

保留实际周期含义：活动的 `CycleIndex` / `_cycleIndex`、市场的 `Cycle` / `_cycle` / `cycle`、测试 `AssertCycle`、采集轮次描述、图环与生命周期名称。Hillside 类型与内容名、内容 ID、内容指纹计算、`GameRevision = 22` 保持。

## 持久边界

领域 75 个现用 DurableType 的 ID 从 `DramaBoard.ResourceCycle.*` 改为 `DramaBoard.Playground.*`，新 ID 的版本均从 1 开始，不继承旧 ID 的祖先链。领域旧 Schema 历史精确退役（含退休 ID），历史原文由 Git 保存；新 baseline 由生成器 Publish 后 Verify。

Inventory 的 ID 从 `DramaBoard.ResourceCycle.Backpack` 改为 `DramaBoard.Inventory.ItemInventory`，版本 1；仅退役 Inventory 对应旧历史。Kernel、Spatial、Goals 的持久 ID 与历史不改。字段序号、数据结构和事件/状态校验不改，测试中的版本边界断言不机械重置。

天气随机流以原派生值 `0x3EA96D0230A07494UL`（原 `DeriveStreamId("resource-cycle/weather/v1")`）写成数值常量，保留历史来源说明，避免应用改名改变随机轨迹。新根分支的应用名使用 `playground`。宿主临时路径与诊断品牌同步；日志字段、内容定义与内容指纹保持原合同。

## 验收

先 Publish 新 Schema baseline，再以 Release 构建和 Schema Verify 检查项目引用闭包；运行完整 solution 的离线回归。以新 CLI 入口启动、暂停和冷开同一新存档，核对恢复及 Hillside 内容身份。当前文档与脚本由对应工作包同步，历史归档保留来源可追溯性。真实模型调用不属于本次纯重命名必要验收。

## 本轮验收证据

2026-11-02，主线程独立验收：

- `dotnet build DramaBoard.slnx -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify` 通过，0 warning / 0 error。
- 全 solution 离线测试关闭全部 `ATELIA_RUN_*` 开关与 `OPENROUTER_API_KEY`，排除 `Category=LiveE2E` / `Category=LocalE2E`：10 个项目，1338 passed、0 failed、0 skipped。构建与测试日志、TRX 位于 `artifacts/playground-rename-acceptance/`。
- `Smoke-HillsideContent.ps1` 以四个独立 CLI 进程验证 gentle 新局、生产与出售、保存定义冷开、显式内容不匹配拒绝且不改盘、匹配内容冷开。结果在 `artifacts/playground-rename-acceptance/content/result.json`；CLI SHA-256 为 `D34664B2F745E636CD456C3A462319DBF0D12B31DD627EA2640CD4198B0C0CDB`。
- `Smoke-Hillside.ps1 -OutputDirectory artifacts/playground-rename-acceptance/economy` 通过：两个 CLI 进程提交 39 条普通命令，覆盖两次离镇返回、加工、采集、交易与冷开；结果为 `economy/result.json` 及同目录输入输出。这是脚本工程验收，未计作自由试玩或真实模型体验。
- 同条件下重命名前后 gentle / hillside 的内存启动输出全文一致，原文为同目录 `before-*.txt` / `after-*.txt`；新 `--help` 显示 `Playground.Cli`。独立复核确认 76 份 Schema 的字段结构不变，共享模块的持久身份与历史未变。
- 105 份受影响 Markdown 的本地文件链接、15 处 Markdown 锚点及 172 个去重固定 Git 源码对象均有效；历史工件未在当前检出中保存的路径已明确标记，不作为本轮运行证据。

本次没有进行真实模型调用或 Linux 验证；上述成功范围为 Windows 离线工程、Console 与持久化恢复。历史 Smoke 脚本里的旧模式不再由当前 CLI 支持；重命名前 Git 版本只证明源码来源，不保证旧命令当时仍可运行。
