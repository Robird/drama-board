# 本阶段真实模型接线与续局

2026-11-03。使用真实 `PlaygroundHost`、普通 living-world 规则和冻结角色材料，在独立 home/store 上完成新建及续局。不是控制器两步演示，也没有修改模型提示以迎合预取。请求经本机 `http://127.0.0.1:8491/` 的 `responses` 路径，模型 `deepseek-v4.1-flash`，reasoning effort low，无 API key。

## 调用与结果

工件根：`artifacts/playground-speculative-lookahead/`。预声明见 [连续对照计划](resident-experiment.md) 和 `p3-online-plan/`。

| 目录 | 物理需求 / 候选 | 采用 / 废弃 | 实际响应 / 成功提交 | 操作模型时刻数 | 本地峰值 | Host 时长 |
| --- | --- | --- | --- | --- | --- | --- |
| `p3-online-off` | 6 / 0 | 0 / 0 | 6 / 6 | 4 | 1 | 38.914s |
| `p3-online-on` | 4 / 2 | 2 / 0 | 6 / 6 | 4 | 2 | 24.168s |
| `p3-online-on-resume` | 2 / 2 | 1 / 1 | 3 / 3 | 3 | 2 | 12.649s |

独立研究账本 `online-budget.jsonl` 预留 16，实际 16 invoked、16 Completed、0 未决；三批均因本地额度达到上限正常暂停，Host ExitCode=2。没有重试、追加批次或借用前期研究的八次调用。on/new 和 on/resume 各有一个 raw model-failure，表示下一次实际需求被本地预算拦住，不能算作第 17/18 次物理调用。

两个新局的完整初态相同，Business SHA-256 为 `14430188d0372248948af8e85ce806e64a7dd327006cb59bcfacd561575c4d8f`。普通需要、天气和经济规则保留，只把两人初始地点设在 camp/cabin。新局最初两个有效输入相同，但真实模型响应与后续轨迹已不同；缓存命中也不同。因此表中时长只说明实际运行，**不构成公平加速比**。

on 新局候选 physical-2 在途采用、physical-4 完成后采用。续局 physical-2 因 target-changed 废弃，其工具调用身份没有进入真实操作；physical-4 后来采用。物理时间线记录实际请求重叠，分别约 4562.857ms 和 7936.822ms。候选在真实采用后才经普通模型响应、命令校验、Host 操作和 Kernel 提交。

## 对账与恢复

`physical.jsonl` 保存完整 request/options、provider result、单调时钟与 UTC。RequestId 与账本关联；物理 ordinal 与 raw `lookahead-physical-request` 的序号关联，并用完整请求核验；raw actual-dispatch 再连接真实逻辑 RequestId。使用量只统计物理结果，不能把角色 model-response 再加一次。

独立审查逐条比较了交付输入、Blocks、Invocation、Termination、Usage 与 Host received/completed 的身份关联，并重算 raw 和两个 Host journal 的原始 Payload 哈希链。on 新局的 Host 记录为 2–40，续局为 41–63，分析时按 RunId 分组，不能把同一 store 的两次运行重复统计。

续局从同一个 on home/store 冷开：新局 run-end 与续局 run-start 的 StateCommitId 均为 `8d12f1530e0000200a000000`，起点 cursor transition=19、time=44100000、a context=4/operation=1；后续成功提交版本为 20、24、28。这直接验证本阶段实际冷开接续；没有另行解码全部二进制 S/E 或重开最终存档，pending 恢复合同另由定向测试证明。

| 批次 | 已知输出 tokens | 已知缓存读 tokens |
| --- | --- | --- |
| off | 2812 | 5248 |
| on | 2231 | 17024 |
| resume | 1737 | 12032 |

16 次 UncachedInputTokens 和 CacheCreationInputTokens 都是 null，缓存观测为 Partial。未知不补零，不估算总费用。续局废弃候选的 551 输出 tokens 已计入该批。

## 来源、故障与边界

在线工件在最终实验整理提交 `a063efa` 前产生；各 manifest 记录运行时 CLI 源码哈希、初态、配置路径与规则，三个运行的 CLI 来源一致。`p3-online-archive.json` 补充账本、配置、协议与在线材料的**事后归档哈希**，不能伪称为调用时已记录的实验封装 / Completion 依赖指纹。正式离线矩阵 manifest 另保存冻结 commit 及相邻依赖状态。

最终在线账本 SHA-256：`4dd3a1527970a4b8b214fe87991357f140e1e9965c7525920c4afd492706e71b`。每批 manifest、physical、raw、summary 及 initial 的散列见上述归档清单。

独立审查发现的实验记账异常遮蔽问题已经修复：原 provider/Host 异常与记账失败分别保留，账本退休后禁止新调用，summary 尽力写入。`p3-online-fault-self-check` 使用离线 factory 和 beforeFlush 注入验证 request-finished/run-finished 失败、原异常身份、禁止重试、summary 留证；它没有追加网络请求，也不把正常 16 次结果改写为故障样本。

本批验证真实模型的实际循环、采用/废弃、持久提交与续局；性能结论以同规则固定工作量的[连续矩阵](resident-experiment.md)为依据。
