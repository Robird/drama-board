# Playground home 与角色控制实施验收

日期：2026-11-02。依据 [home 方案](../design/playground-home.md)，本次由配置、CLI、统一宿主、测试和文档工作包实施，另有独立只读审查；主线程检查集成源码并执行最终验收。此文件记录本次证据，不替代原多连接与生活世界验收。

## 实现边界

- `Options.Parse` 只解析显式参数，`Resolve` 读取严格 version 1 manifest；home 为显式路径或启动 CWD，不向父目录搜索。配置路径相对 home，CLI 路径相对启动 CWD。
- `init` 用 CreateNew 只创建 `playground.json`。普通运行始终要求有效连接目录；可选的 typed 控制文件只有显式选择才读取。默认全部 LLM，单个角色可覆盖为 Human Console 或 LLM(ConnectionId)。
- `PlaygroundHost` 是唯一世界执行流程；原 `PlayConsole` 只保留 Console 控制器与操作辅助。所有 Human 共用 Console，所有 LLM 共用 loop 和调用预算；连接池只在实际模型决策时读取所选凭据。
- 内容/人数一致性验证在 pending 恢复和宿主日志之前；新局角色/预算/连接引用校验在创建 store 之前。程序注入依赖仍可使用内存 history，CLI 不提供绕开 home 的第二种运行入口。
- 显式 store 的管理命令不读取 home，查询/导出不恢复 pending；fork 继续为显式写操作。`--ephemeral` 只使世界不落盘，审计仍写入 home。
- 删除旧全局控制开关、单 Human 参数、角色字符串映射与旧 Gym 两布尔 CLI。领域、Schema、GameRevision、内容指纹无改动；日志去重与 Human/LLM 共用反馈继续独立延期。

## 工程验收

Windows、SDK 10.0.201；同一构建图串行验证。最终完整 Release solution 使用 `-warnaserror -p:DurableGraphSchemaHistoryMode=Verify` 构建，零警告、零错误，记录于 `artifacts/home-build-final.log`。领域目录与 schema-history 无 diff。

测试覆盖严格 JSON、路径与优先级、管理惰性、全默认/覆盖连接、多 Human、全 Human、共享调用预算、默认连接缺失环境变量时仍不访问凭据、冷开改绑、pending 不重问、操作单记、角色私密材料隔离与多 Human 终局各交付一次。三人交流夹具使用 `continue` 确认通信并保留活动；再次 `wait` 会被领域拒绝，不能据此假定下一角色已经轮转。

完整 solution 回归后，对继续补强的 CLI 测试重新构建与全量复测；最终各项目合计 **1,403 项通过、零失败、零跳过**：共享 Completion 13、CharacterGym 30、Goals 31、Host 2、Inventory 36、Kernel 131、CLI 464、游戏 Completion 46、Playground 548、Spatial 102。初次迁移过程中遇到的旧格式夹具、摘要字符串和三人输入时序断言均已修正；最终 CLI TRX 在 `artifacts/home-tests-final-pass/`，其余九项目 TRX 在 `artifacts/home-tests/`。

## 实际 CLI 验收

以下均为 Coding Agent 编写脚本驱动 Human Console 的工程验收，不等于实际人类自由试玩：

- [home smoke](../../scripts/Smoke-PlaygroundHome.ps1)：init 不覆盖、缺连接不建档、坏 home 下 help、含空格路径、不同 CWD 与 home 内冷开、多 Human 实际操作、Human/LLM 无效新局预算均无 store/log、坏/缺 home 配显式 store 的 JSONL 查询及源文件 hash 不变、ephemeral 日志落 home。
- [内容 smoke](../../scripts/Smoke-HillsideContent.ps1)：gentle 单角色新局与续局，显式不符内容在恢复和写入前拒绝。
- [经济/交易 smoke](../../scripts/Smoke-Hillside.ps1)：两个进程间冷开，分别提交 39 / 7 条操作。
- [Gym smoke](../../scripts/Smoke-HillsideGym.ps1)：使用前次多连接实验的真实请求存档副本，验证当前 CLI 分叉、Human 冷开及按角色导出；不将历史请求当作本轮在线成功。具体结果见 `artifacts/home-acceptance/gym/manifest.json`。

本机原始构建、TRX、进程 stdout/stderr 和审计均保留在忽略目录 `artifacts/home-acceptance/`、`artifacts/home-tests*/` 与 `artifacts/home-*.log`；失败的中间证据没有覆盖或冒充最终成功。

## 真实模型与限制

本次用示例目录配置 quick 默认连接、b=deliberate，Gym record 与 3 次共享调用预算启动真实 Codex：首个 a→quick 请求记录 model=`gpt-6.1-sol`、protocol=`openai-codex-responses-v2`，随后 **transport-failure**，`CallsMade=1`，宿主退出码 2。未取得 Completed，也没有自动重试、failover 或代选行动；不把它计为在线成功，剩余连接路由由离线 fixture 证明。证据在 `artifacts/home-acceptance/live/`；详细失败诊断能力属于既定独立日志重构，不在本次扩大范围。

本次不发布 NuGet、不推送、不改变相邻 Completion/DurableGraph 源码；未执行 Linux 验证或其他 provider 在线实验。日志布局和统一反馈不在本次范围。
