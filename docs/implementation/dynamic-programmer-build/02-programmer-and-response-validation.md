# R2 · Programmer 接口与响应校验器

状态：已完成。依赖：R1。产物：`src/Player`、`src/Decision.Validation`、`tests/Player.Tests`、`tests/Decision.Validation.Tests`。

## 目标

定义 Programmer 的新合同（替代 `IPlayerDriver` 的位置，但本轮**不删除**旧接口），并提供 Human 与脚本/测试共用的纯响应校验器。

## 前置阅读

- [机制模型](../../design/player-runtime/dynamic-programmer.md) §5（一次响应的生效）、§7（建议配置）
- 风格参照：`src/Player/IPlayerDriver.cs`、`src/Player/ScriptedPlayerDriver.cs`、`src/Decision.Validation/PlayerDecisionValidator.cs`

## IDynamicProgrammer（src/Player）

```csharp
public interface IDynamicProgrammer
{
    /// <summary>Proposes one complete program and subjective revision for the exact frozen request.</summary>
    ValueTask<ProgrammingResponse> ProgramAsync(
        ProgrammingRequest request,
        CancellationToken cancellationToken);
}
```

`ScriptedDynamicProgrammer`：镜像 `ScriptedPlayerDriver` 的形状——构造接收 `IEnumerable<Func<ProgrammingRequest, ProgrammingResponse>>`，按请求顺序出队，耗尽时抛 `InvalidOperationException`，factory 返回 null 抛错。

## ProgrammingResponseValidator（src/Decision.Validation）

沿用 `PlayerDecisionValidator` 的结果类型风格：

```csharp
public enum ProgrammingResponseValidationError { None, ActivationIdMismatch, UnknownPassageHandle, WorkspaceTooLong }
public readonly record struct ProgrammingResponseValidationResult(...) { public bool IsValid => ...; }
public static class ProgrammingResponseValidator
{
    public static ProgrammingResponseValidationResult Validate(ProgrammingResponse response, ProgrammingRequest request);
}
```

校验项（纯函数，不读世界、不做 I/O）：

| 检查 | 失败值 |
|---|---|
| `response.ActivationId == request.ActivationId` | ActivationIdMismatch |
| 每条 `MoveInstruction.PassageHandle` ∈ `request.KnownPassages` 的 Handle（Ordinal） | UnknownPassageHandle |
| `response.Workspace.Text.Length <= Math.Max(65_536, request.Workspace.Text.Length)`——只限制 Programmer 发起的增长；当前已超基线时原样/缩短重交合法 | WorkspaceTooLong |

**明确不检查**：指令当前是否可执行、出口是否开放、角色是否在端点。接受程序只查知识/引用资格；`Move(ac), Move(bd)` 在 A 地必须通过校验，受阻是后来的正式世界内结果（机制文档 §4"入队时检查可判定事项，执行时再检查当时条件"）。Think/Stay 不携带引用，无需额外检查（参数合法性由 R1 构造器保证）。

该校验器是 HTTP 预检（R7 Submit）与规则内权威校验（R6 respond Plan）共享的同一个纯函数。

## 测试清单

- Player.Tests：`ScriptedDynamicProgrammer` 顺序出队、耗尽抛错、null factory 抛错、请求非 null。
- Decision.Validation.Tests：ActivationId 不匹配；Move 引用未知 handle 拒绝；引用已知 handle 通过（含"已知但当前不可进入"的 handle 也通过——用例名体现该语义）；空 Program + 合法工作区通过；**空 Program + 超长工作区仍整体拒绝**（主观修订非法而程序为空时整份响应不生效、不规范化为 [Stay]——机制 §5.3）；增长界三例：基线内提交超 65,536 拒、当前已 70,000 时原样重交（70,000）过、提交 70,001 拒；Think/Stay 程序通过。

## 验收

```text
dotnet test DramaBoard.slnx
→ 全绿；IPlayerDriver 及其驱动器、PlayerDecisionValidator 保持原样且各自测试不变绿。
```

## 禁止事项

- 不删除或修改 `IPlayerDriver`、三个现有 driver、`PlayerDecisionValidator`（R8 统一处置）。
- 校验器不得依赖 Server、Kernel、Spatial。
