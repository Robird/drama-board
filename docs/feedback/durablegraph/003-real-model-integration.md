# 003：真实领域模型接入反馈

状态：2026-09-12，record class 易用性反馈已由上游 DB-068（`1c6083c`）落实，DramaBoard 已采用 `IDurableObject` 包。[当前包来源与迁移证据](../../worksets/durablegraph-package-source.md)。下述首轮功能和成本观察使用旧包 `f68388f`，不代表新版性能测量。

## 已满足的需求

`CreateBranch → CommitDomainEvent → CommitDomainState(nextState) → Resume` 足够实现当前游戏持久化。完整 S 包含世界、有限调度游标、精确内容和规则绑定；E 是小型强类型事实图。正常重开不执行历史业务 reducing，pending 只处理一次已记录 occurrence，不重问 Player。

证据是[实际 adapter](../../../src/FirstBoard/Persistence/FirstBoardOccurrenceHistory.cs)、[真实包行为测试](../../../tests/FirstBoard.Persistence.Tests/FirstBoardPersistenceTests.cs)与[独立进程测试](../../../tests/FirstBoard.Persistence.Tests/ColdProcessTests.cs)。模型直接位于原 Kernel / Spatial / FirstBoard，共 64 份声明历史；没有 SavedWorld 镜像、动态世界 JSON 或旧 Journal 双写。独立事件登记明确排除了 World/State 根，完整事实 union 的读取仍成功。

本批没有发现阻断接入的存储功能缺口。ReadPair、局部历史分页、ArtifactStore 或运行栈恢复均不是这次集成的前置条件。

## 已回应的易用性需求：不可变 class 声明

主要工作量在领域声明适配，而非保存 API。原来的 positional `record class` 需要改写为普通 partial durable class，并手工恢复构造、复制更新、值相等、hash、必要的 `==` 与只读集合外观。实际改造集中在 [FirstBoardDomain](../../../src/FirstBoard/FirstBoardDomain.cs) 和 [GraphSpatialFact](../../../src/Spatial/Facts/GraphSpatialFact.cs)。

这不是单纯的写法偏好：cold E/S 的 contact key 必须按值比较；事件文本可以包含任意字符，不能用分隔符拼接代替结构相等；`readonly List<T>` 只禁止替换引用，仍然允许修改内容。迁移中这些错误都需要额外审查。当前消费方已修正，不需要为此暂停玩法开发。

上游 [DB-068 消费示例](../../../../durable-graph/experiments/PackageConsumerProbe/RecordClassConsumer/README.md)现已支持普通/positional `record class`、泛型跨库继承以及 class→record 同版 Schema history；以 `IDurableObject` 替代框架基类。DramaBoard 本次先迁移 marker 并重编译，保留原字段、构造与相等行为。后续遇到具体模型时再用 record 删除样板代码：`with` 仍是浅复制，集合内容相等与事件快照纪律仍由应用负责。

## 首次成本观察

使用[实际 process consumer](../../../tests/FirstBoard.Persistence.Process/Program.cs)及[低层诊断](../../../tests/FirstBoard.Persistence.Process/Metrics.cs)，从 Alice/Bob traveling S0 完成 encounter 打开及 Continue/Reverse 响应，各产生 `S0,E1,S1,E2,S2`。以下为 Windows Debug 单次、未预热观察，不能作为稳定性能结论；没有同机旧后端对照。

| 观察 | Continue | Reverse |
|---|---:|---:|
| 存档所有文件总字节 | 12,528 | 12,764 |
| S0 local Base 对象数 | 46 | 46 |
| E1 / S1 local Base 数 | 9 / 14 | 9 / 14 |
| E2 / S2 local Base 数 | 6 / 9 | 8 / 18 |
| S1 / S2 map Remove 数 | 10 / 10 | 10 / 20 |
| S1 / S2 local object body 字节 | 148 / 156 | 148 / 287 |
| 两次 E 提交耗时 ms | 146.0 / 69.4 | 164.6 / 80.5 |
| 两次 S 提交耗时 ms | 155.9 / 58.4 | 96.9 / 174.5 |
| 两次 E 当前线程分配字节 | 1,835,416 / 2,060,712 | 1,835,392 / 2,076,864 |
| 两次 S 当前线程分配字节 | 3,196,912 / 3,608,688 | 3,196,888 / 3,302,912 |

此处所有 local object records 都是 Base；S1/S2 的 ObjectHeadMap 是 Delta。当前纯 fold 替换了变化对象，未变化对象继续共享，因此出现少量新 Base 与 map Remove，仍是增量保存，不能把“对象 Delta 为零”解释成整世界重写。改为稳定可变实体是否值得，需要同时比较领域维护成本、实际长轨迹写入和分配。

提交计量围绕消费方同步 Commit 调用，包含框架处理、校验和 I/O；不包含 S0 创建、Plan、scratch fold、其他线程分配和进程总启动成本。body 字节排除了 frame/map/schema 开销；总文件字节含 Journal、refs、Schema、State。诊断依赖固定版本的公开低层 API 与文件布局，不把 `GraphFrame.RevisionAddress` 当跨重开书签。

复现时使用尚不存在的目录，先构建 process 项目，再运行；两种回应分别执行即可：

```powershell
$consumer = 'tests/FirstBoard.Persistence.Process/bin/Debug/net10.0/DramaBoard.FirstBoard.Persistence.Process.dll'
dotnet $consumer create artifacts/my-continue Continue 2
dotnet $consumer inspect artifacts/my-continue
```

后续优先实际玩法扩展与较长轨迹测量；模型升版选择真实业务字段再做两代续写见证。暂不从这一小样本推导缓存、可变模型或新存储子系统需求。
