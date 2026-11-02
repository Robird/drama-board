# 多模型连接与角色绑定

本设计落实用户已选择的两文件方案。共享调用层处理连接配置、生命周期与不透明角色 ID 路由；游戏只提供角色集合并校验宿主绑定。当前入口见[根 README](../../README.md)，接线见[新增游戏](../implementation/adding-a-game.md)。验收证据见文末。

## 配置合同

连接文件为 `{ "version": 1, "defaultConnectionId": "quick", "catalog": { "v": 3, "connections": [...] } }`。外层默认连接选择属于 DramaBoard 宿主，内层是 Atelia.Completion 的严格 V3 目录。目录本身没有 `defaultConnectionId`；不把旧 V2 文档直接当作此文件。每条连接明确声明 `id`、`kind`、`modelId`、`completionSurfaceId`，以及 `baseAddress` / `baseAddressEnv` 二选一和所需参数。连接 ID 按区分大小写的精确身份匹配，默认 ID 和所有覆盖引用都必须存在。V3 可选的 `bindings` / `selectableConnectionIds` 元数据仍接受结构和引用检查，但不参与此宿主的路由；路由只由外层默认与角色覆盖决定。

可直接使用[连接示例](../../config/llm-connections.example.json)：`quick` 与 `deliberate` 都是 Codex `gpt-6.1-sol`，分别使用 low / medium。Codex 的 kind 和 surface 均为 `openai-codex-responses`，baseAddress 必须是工厂 canonical 值 `https://chatgpt.com/backend-api/codex/`。认证沿用现有 Codex file-backed 登录发现，由 Completion 读取；示例不携带密钥，也不要求额外填写模型。增加其他 provider 时按 Completion 目录合同填入实际可用的 model、surface、地址及认证来源，不能把模板占位 model 当作可运行配置。

可选角色覆盖文件为 `{ "version": 1, "overrides": { "b": "deliberate" } }`，见[示例](../../config/playground-character-connections.example.json)。缺少某角色的覆盖即回退默认连接。共享层把角色 ID 当不透明字符串；CLI 根据当前新局或存档中的真实角色集合拒绝未知角色。未知连接、非法版本/字段及显式指定却不存在的文件报错，不静默回退。省略整个覆盖参数表示没有覆盖。

Human 选择优先于模型路由。Human 角色可以保留 dormant override，配置仍须引用真实角色和连接；本次不会因此读取该连接的凭据或调用模型。无 `--llm` 时使用 Human Console。`--llm` 必须与 `--llm-connections PATH` 配合，覆盖用 `--character-connections PATH`。旧 `--model`、`--auth-file`、`--catalog`、`--connection` 删除，没有兼容层。

## 共享层与宿主分工

`LlmConnectionConfiguration.Load(path, overridesPath?)` 一次读取、校验并冻结文件；`ConnectionIds`、`OverrideActorIds` 支持宿主检查，`GetConnectionId(actorId)` 返回覆盖或默认。运行中改文件不影响当前运行，续局可重新选择文件。嵌入目录的解析、协议配置与客户端工厂属于 Atelia.Completion，不在游戏代码复制 provider 解析。

`LlmConnectionPool.GetAsync(id, ct)` 返回 `LlmDecisionBinding(Client, ModelId, ConnectionId)`。Pool 按 connection ID 惰性解析所选连接的环境设置、取得凭据并创建客户端，缓存并拥有其生命周期；不同连接即使模型相同也保留独立配置与身份。未实际使用的 provider 不要求可用秘密，不创建客户端。宿主异步释放 pool，loop 和游戏 adapter 借用客户端。固定 `ICompletionClient` 的 programmatic loop 构造仍可用于测试和独立集成，CLI 不通过旧参数恢复固定默认连接。

CLI 先校验可知配置与实际角色，完成存档身份检查、pending 恢复和宿主证据核对；首次真实模型决策才访问对应凭据/客户端。管理查询、导出与 fork 不运行模型，也不需要连接。领域对象、事实、世界定义、Schema 与存档不保存连接、密钥或宿主路由。

## 决策、预算与失败

一个 `LlmDecisionLoop` 顺序服务所有模型角色，所有连接共用本次宿主的总调用预算和连续协议失败预算；不能通过切换连接重置预算。每次角色决策先解析并固定一个 binding，该决策中的反馈调用和最终 `act` 使用同一连接与本地原生 Tail。另一角色或下一决策从自身冻结 Context 开始，不继承该 Tail。

Human 与 LLM 共用领域校验，模型提交不等于游戏成功。超限、取消、transport/provider 失败及协议失败按现有合同暂停或计入预算，不伪造替代动作。没有自动 failover、自动外部调用重试、跨模型 Tail 回放或并行角色服务。仅 Completed 正文进入成功路径；terminal 前中断的远端结果未知。

## 证据与恢复

Gym 和每次运行的审计在实际 call 处记录 actor、connection、model、protocol；不能用全局默认值替代实际绑定。调用元数据属于宿主实验材料，不进入世界，也不会成为角色额外知识。不得写入凭据、认证文件内容或秘密环境变量。

续局和分叉可以采用新配置，旧请求保留原身份，新请求记录新身份；这不是恢复原生模型会话。已发布 pending E 不重问控制器，未形成已发布事件的模型决定在恢复后可能重新调用，可能重复计算或计费。配置文件与代码版本应随实验现场另行保留；日志不是配置或世界状态的权威。

首版不提供热加载、运行中切换、自动故障转移、持久模型会话、连接级独立预算或领域中的连接属性。需要这些能力时按真实运行需求另行裁决。

## 实施与验收（2026-11-02）

上游 Completion 提交 `97eb74f` 提供 `DecodeCatalogWithoutEnvironment`，复用现有 V3 parser，不复制或放宽协议配置解析。下游使用相邻源码引用，尚未发布 NuGet。旧单连接 Binding、隐式 Codex 选择和 CLI 多层参数覆盖已删除；固定 client 的程序调用重载仍有独立用途。新局在写入存档前校验 Human 与覆盖角色，已取消的运行在读取配置前退出。

Windows / SDK 10.0.201 验证：

- Release solution 构建与 Schema Verify 零警告、零错误；领域 Schema 和游戏修订号没有变化。
- DramaBoard 全 solution 回归后，对修正的 fixture 和增强的测试定向复测；最终各项目共 1,353 项通过、零跳过。其中共享调用层 19、CLI 408、游戏 Completion adapter 46 项。覆盖两文件严格校验、显式缺文件、配置冻结、Human 优先、凭据延迟解析、客户端缓存/释放、共享预算、单决策固定连接、局部 Tail、pending 恢复和冷开重新绑定。CLI 路由 fixture 分别使用 Responses / Chat 协议身份，验证 Gym 不把默认协议套到其他连接上。
- 上游 Completion 离线回归 1,295 项通过，3 项 Unix 文件权限测试在 Windows 跳过；关闭 CI 列出的 live opt-in 开关并排除 LiveE2E / LocalE2E，没有将跳过视为在线成功。
- 使用仓内两份配置示例完成真实 Codex 运行：三角色共 8 次请求、8 次 Completed、8 次游戏操作提交；a/c 使用 `quick`，b 使用 `deliberate`，第 8 次后按全局预算暂停（退出码 2）。两连接分别配置 low / medium，同为 `gpt-6.1-sol`；这是连接路由验证，不是模型效果比较。
- 冷开同一存档，将 c 覆盖为 `deliberate`，完成第 9 次真实请求与操作提交，达到本次 1 次预算后暂停。旧请求保留旧身份，新请求记录新连接；实际协议为 `openai-codex-responses-v2`。
- 更新后的 `Smoke-HillsideGym.ps1` 在该真实请求存档上通过分叉、两次 Human 冷开及导出验证。完整配置可读取，但凭据环境变量被显式移除；没有模型请求。源分支 head、操作日志与导出保持不变，管理只读调用不改变存档字节。

日志与 TRX 位于本机忽略目录 `artifacts/multi-connection-acceptance/`：`build-final.log`、`tests/`、`shared-final/`、`cli-final/`、`adapter-fixed/`、`live/` 与 `gym-smoke/manifest.json`。上游测试证据位于其同名 artifacts 目录。首轮 8 次调用的审计 SHA-256 为 `4852A4143570450682831FD8FE584D13EA5DFBA8B68552EF3B6BE69C28290D5E`。

本轮没有执行 Linux 验证或其他 provider 的在线调用；跨协议路由由离线 fixture 验证。历史验收命令保留当时版本，不改写成新接口。
