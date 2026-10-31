# Shared character model invocation

`LlmDecisionLoop` owns the operational tool loop. It borrows an `ICompletionClient`; the composing host owns client lifetime and connection configuration. One loop can serve different characters sequentially. Call/protocol budgets are host state, not character memory.

Each call supplies a `LlmDecisionInput`: actor identity, positive context sequence, already-rendered actor-visible material and game-specific policy. The loop supplies the `act`/optional `report-feedback` protocol. It returns `CommandSubmission`, preserving provider tool-call identity and assigning an operation identity. A returned submission is not a successful game operation: the game still parses, validates, records and applies it.

The optional feedback sink must durably record feedback before returning its receipt. The loop then retains the native assistant message and matching tool result within this decision only. Subsequent decisions start without this Tail. Trace callbacks expose actual request/response identities to the host; they do not publish game state. Host failures must not be translated into invented game actions.

The game must publish a frozen decision boundary before invoking the loop if it needs Gym fork/recovery. Renderers that hold presentation state need their own sequential protection. The [ResourceCycle adapter](../ResourceCycle.Completion/LlmCharacterController.cs) and [Microtown adapter](../Microtown.Completion/README.md) both protect rendering and invocation as one serial service. Shared transport material does not replace a game's persistent Context or visibility checks.

## Host connection binding

`CompletionConnectionBinding.ConnectAsync(CompletionConnectionOptions, CancellationToken)` composes a selected Completion client and exposes `Client`, `Model` and `ConnectionId`. The binding owns its registry and must be asynchronously disposed by the host. The loop and game adapters borrow clients; a caller-supplied client remains the caller's responsibility.

- Without `Catalog`, the binding uses Codex subscription authentication from the default Codex CLI auth source or explicit `AuthFile`; the default model is `gpt-6.1-sol`.
- With `Catalog`, `Connection` is mandatory and matched exactly. `Model` optionally overrides that connection's model. `AuthFile` cannot be combined with a catalog, and a connection ID cannot be supplied without a catalog.
- Catalog loading validates the entire strict document, but resolves environment settings only for the selected connection. Unselected providers neither require secrets nor initialize clients. This uses upstream `CompletionConnectionConfigLoader.LoadSelectedConnectionFile`, introduced in adjacent `atelia-completion` commit `ef78922`; a package predating that API is insufficient.
- Cancellation is checked before configuration/auth access. Hosts create the binding lazily at the first actual model request, after pending game recovery and evidence reconciliation. Management commands need no client.

Connection configuration and credentials are host data, never character memory or game facts. Gym records public model/connection identities, not auth files or credential contents. General-purpose provider errors may contain sensitive values; the host controls their presentation.

## Operational boundaries

`LlmControllerOptions` defaults to 128 model calls per instance, 3 consecutive protocol failures, 200,000 UTF-16 code units of rendered material plus local protocol history, and 8,192 code units per action. Oversize material pauses instead of being silently truncated. Feedback defaults to at most 3 successful reports per service and 4,000 combined decoded characters per report. These are process-local budgets, shared across the characters using one instance; restarting creates new budgets.

`LlmControllerPausedException` reports a pause, not a fallback action. Successful feedback persistence precedes the tool receipt; neither feedback nor a model proposal proves that a game effect committed. Native Tail survives only the current service, not cold reopen or another character's decision. There is no generic persistent model session or automatic retry of an external call.

Pure cross-game protocol and connection tests live in [CharacterControl.Completion.Tests](../../tests/CharacterControl.Completion.Tests). Game-specific adapter tests live in [ResourceCycle.Completion.Tests](../../tests/ResourceCycle.Completion.Tests) and [Microtown.Completion.Tests](../../tests/Microtown.Completion.Tests).
