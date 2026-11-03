# 独立审查与代表重跑

2026-11-03。由未编写本阶段关键实现的代理独立检查源码、运行冻结 DLL、核对原始工件。代表重跑、实际 CLI 入口、正式矩阵数据及最终工程证据均通过；未发现阻止 G1–G7 原范围验收的缺口。主线程仍负责最终文档、diff 与集成裁决，本报告不授予合并主线权限。

## 来源与执行边界

实现来源为 `a063efa8b574fd619733a29bb752417518cf60e9`。执行期间不构建、不编辑源码、不调用网络；正式矩阵结束后，依次运行实验 DLL 与 CLI DLL，全部结束后才交回主线程运行最终工程检查。

| 冻结入口 | SHA-256 |
| --- | --- |
| `DramaBoard.PlaygroundLookahead.dll` | `1B41205146CBE9A4BB518A0E70879D05218E09258714A305C9F342FF9DAA70CC` |
| `DramaBoard.Playground.Cli.dll` | `E4EED045382B87C9C78F54865107876BE46DAD88580621D1ED41531B35B77D76` |

最终构建后，又逐项核对正式矩阵的 `binaries.json`：30 项程序集、依赖描述及运行配置文件的哈希均保持一致。CLI 源输出目录中的入口与实验目录中的副本也相同。

## 独立连续重跑

从仓库根实际执行，退出 0：

```powershell
dotnet experiments/PlaygroundLookahead/bin/Release/net10.0/DramaBoard.PlaygroundLookahead.dll --self-check --pairs 1 --operations 16 --delay-ms 100 --output artifacts/playground-speculative-lookahead/p4-independent
```

新目录中共有四类场景、四组配对、八个测量样本。每样本恰好 16 次成功操作，合计 128 次；低耦合跨八个操作时刻，高耦合跨四个操作时刻。测量样本合计 142 个物理请求，全部观察并排空，关闭/开启峰值分别为 1/2。

| 开启场景 | 创建 / 采用 / 废弃候选 | 物理请求 | 工作墙钟 off / on（秒） |
| --- | --- | --- | --- |
| 低耦合 / 独立 | 8 / 8 / 0 | 16 | 2.353 / 5.980 |
| 低耦合 / 共享 | 8 / 8 / 0 | 16 | 3.240 / 4.507 |
| 高耦合 / 独立 | 12 / 4 / 8 | 24 | 2.452 / 4.871 |
| 高耦合 / 共享 | 10 / 4 / 6 | 22 | 3.895 / 4.365 |

100ms 下四组均更慢，保留为短服务开销的负结果。这次只有每类一对，定位是代表正确性重跑，不能称作正式 1 秒矩阵的性能复验。高耦合共享场景的请求数为 22，正式矩阵为 20；保留旧请求占槽及取消时机随服务时长变化，允许工作浪费量不同，实际有效轨迹仍须相同。

独立编写的 Python 标准库核验器没有调用实验的 Compare 实现。它直接读取四组重跑及正式矩阵的二十组配对，逐项验证：

- `actual-inputs.json` 原始文件逐字节相同，不修改模型可见字符串；初态一致。
- 按操作顺序核对 actor/context/operation sequence，建立唯一操作 ID 双射；仅替换名为 `OperationRecordId` 的显式属性，再比较全部 `operations.json`、`events.json` 和 `final.json`。
- 生产 raw 的序号、前向哈希及 SHA-256 链完整；实际逻辑请求、响应和保存的完整输入一致。
- fixture 的物理调用、生产协调器的物理请求及实际交付按完整输入关联；已采用候选的目标身份相同，废弃候选未被采用；每个本地物理结果均被观察。
- 每个样本的有效工作量、多时刻覆盖、候选生命周期、在途上限与排空成立；Host 总时长与首服务前、工作窗、最终提交后尾段的分解相符。

四组独立重跑的 680 条 raw 记录、正式四十样本的 3340 条 raw 记录全部通过。正式测量样本合计 640 次有效操作、700 个物理请求；这些计数不含预热。可复现核验器与结果分别为 `p4-independent/verify_evidence.py`、`independent-evidence-check.json`，原始运行参数、stdout/stderr、退出码与时长在 `independent-invocation.json` 及同目录文本文件。初次核验器误用了失败事件名称的检查失败也保留为 `verifier-schema-failure.json`；修正读取 `lookahead-failure` 后全量核验通过，没有修改被测算法或数据。

## 实际 CLI 新建、续局与 CWD

`p4-independent-cli/` 保存四次真实 `dotnet <CLI DLL>` 调用的完整参数、CWD、stdin/stdout/stderr 及退出码：

| 调用 | 参数要点 | 结果 |
| --- | --- | --- |
| init | `init --home <新 home>` | 退出 0，仅创建 manifest，未建 store。 |
| new-on | `--home <home> --characters 2 --lookahead on --steps 100` | 退出 0，新建并保存。 |
| resume-off | `--home <同一 home> --lookahead off` | 退出 0，续局。 |
| cwd-on | CWD 为 home，无参数，home 内 `lookahead: on` | 退出 0，续局。 |

连接目录原样复制仓库示例，仅将 a/b 配为 Human Console。脚本输入包含 `wait 1` 与 `:quit`；三次游戏运行沿用同一分支，宿主日志共 30 条哈希链记录、六组输入/结果配对，所有 stderr 为空。无模型请求/响应记录，也没有模型 raw 文件。这证明 CLI 配置、持久运行及 Human 路由可用，不冒充真实模型性能对照或真人试玩。具体校验在 `independent-cli-check.json`。

## 正式结果与范围审查

从每样本原始计时和服务记录独立重算，与[正式报告](resident-experiment.md)吻合：

| 1 秒场景 | 配对改善中位数 | 每 run 物理请求 off / on |
| --- | --- | --- |
| 低耦合 / 独立 | +31.7062% | 16 / 16 |
| 低耦合 / 共享 | −3.4811% | 16 / 16 |
| 高耦合 / 独立 | −0.1862% | 16 / 24 |
| 高耦合 / 共享 | −23.7258% | 16 / 20 |

改善先在每对计算，再取中位数；不能从两组墙钟中位数反推。五对小样本不提供统计显著性或全体模型的性能保证。同步 raw 刷盘成本已包含在实际墙钟内，不能直接扣除累计日志时间虚构优化后收益。工作窗终点为 inner S 提交返回，最终事实投影、诊断及排空位于 Host 尾段；服务累计计时含渲染和工具续轮。独立/共享 profile 同时改变容量与服务中取消策略；比较各自 on/off 公平，跨 profile 不只反映容量。

G2 的“来源存档不变”由[Projection](../../../../src/Playground.Cli/PlaygroundLookaheadProjection.cs)的输入与提交能力边界、source/cursor/随机地址测试及持久 Host 隔离测试共同支持，没有做整个 store 的字节哈希实验。G3 的共享依赖由共享昼夜材料覆盖；该例同时改变目标时间，不能单独归因于 light。相同目标身份下的 `input-changed` 由[RuntimeTests](../../../../tests/Playground.Cli.Tests/PlaygroundLookaheadRuntimeTests.cs)的大喊用例独立证明；没有共享库存竞争专项，不声称已验证全部资源玩法。

此前指出的 Provider 原错被记账错误覆盖、收尾记账失败阻止摘要的问题，已核对修复与离线故障工件。新的[HumanWaiting 测试](../../../../tests/Playground.Cli.Tests/PlaygroundLookaheadHumanWaitingTests.cs)确实先阻塞输入，再完成候选，并检查世界、游标、事件与 feedback 不变，最后才释放 EOF；不再只用立即 EOF 代替等待证据。

“可考虑作为默认关闭的实验能力合并”与现有证据相称；不支持默认开启、自动合并或所有世界/连接均受益。低耦合独立容量下无额外请求且五对均受益，高耦合与瓶颈情形的负结果和成本完整保留。

## 最终工程证据

主线程执行最终 Release 构建、Schema Verify 与完整 solution 测试；独立审查直接读取 `p4-engineering/build.log`、`test.log` 和全部十份 TRX，而非仅转述 summary。构建日志显示零警告、零错误及各模块 schema-history 验证；每份 TRX 的结果节点与计数相符，合计 **1583/1583 Passed、零失败/跳过**，其中 CLI 为 662。全部 TRX、build/test 日志的实际哈希与主线程记录一致。

复核结果留在 `p4-independent/independent-engineering-check.json`。本审查没有再次执行工程命令；实际独立运行的是前述八样本与四个 CLI 入口。G7 的独立代码审查、代表重跑与工程工件验真均已有证据。

## 工件索引

较大证据位于仓库忽略目录 `artifacts/playground-speculative-lookahead/`，Git 文档不会自动携带它们。两个独立目录的 `artifact-hashes.json` 列出全部实际证据文件的相对路径、大小和 SHA-256，自身除外。

| 工件 | SHA-256 |
| --- | --- |
| `p4-independent/summary.json` | `748B28309DD3F3D1C4C217C755ADED6B2E9F888B7572F838AB68C39AD7FD3250` |
| `p4-independent/independent-evidence-check.json` | `BD5D64B237C1A4E87990F66C41DF61A38472BEF3225F22769951377DB683339F` |
| `p4-independent/verify_evidence.py` | `66BEAE5E08F5DDB1015214A747453EB984B16516CEFE7CEF8ABE7E0860626BF2` |
| `p4-independent/artifact-hashes.json` | `DF8CF48620D0A8D65CC99EC905926CD289BA0990D117FD76A1F553B9041FE274` |
| `p4-independent-cli/manifest.json` | `EAE4F1B619366511173E12B8399D1D6C76102D452D4CE83E74157415F986DEF8` |
| `p4-independent-cli/artifact-hashes.json` | `0E7B20617A4E677B1EC569C6FC0651BD984F48CD79220FB615BBAC09D77FD157` |
| `p4-engineering/build.log` | `10028C55D2B6D9222D78DB5DD314D3762B234BC6E16BDBFD8D69CC07C586E363` |
| `p4-engineering/test.log` | `9328DF9C839AB19F21A069801A049FFFF819A59A5144556D14204D413E61E33C` |
