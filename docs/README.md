# DramaBoard documentation

Start with the repository's [current project state](../PROJECT-STATE.md). The active engine consists of Kernel, Spatial and Host. ResourceCycle provides the current Console interaction; the earlier Player planning and presentation stack remains a frozen experiment.

## Current design and core

- [Terminology](glossary.md): shared Chinese/English vocabulary and current code mappings for characters/controllers, time, predictions, publication and space.
- [Layering and composition](design/layering-and-composition.md): accepted logical boundaries, domain modules, game composition and source-based mod direction; assembly extraction follows evidence, without a generic component framework.
- [Stateless character controller decision](design/stateless-character-controller.md): accepted state ownership, per-character context and recovery boundaries, plus the completed symbol rename map; LLM/context APIs are not yet implemented.
- [Game initialization and revision](design/game-initialization-and-revision.md): shared new-game factory, separately versioned State/Event representations and load-time revision before normal execution; production accepts the current revision, with historical semantics exercised by [disk recovery fixtures](experiments/game-revision-acceptance.md).
- [Simulation Kernel](design/simulation-kernel.md): occurrence, time, deterministic arbitration and atomic publication; no Player implementation is required.
- [Graph Spatial World](design/graph-spatial-world.md): objective locations, passages, motion, contact and arrival.
- [DurableGraph occurrence persistence](design/durablegraph-occurrence-persistence.md): retained E/S seam and finite pending recovery contract; this is not a disk persistence adapter.
- [Kernel occurrence baseline](implementation/kernel-occurrence-baseline.md): scheduler evidence, with superseded storage clauses identified.
- [Passage contact floor timing](worksets/passage-contact-floor.md): retained Spatial floor-contact and ceil-arrival semantics.
- [DurableGraph source dependencies](source-dependencies.md): current sibling-source development entry, including generator and schema verification.

## Current work

The active refactoring direction is [gradual modularization](implementation/modularization-plan.md): organize ResourceCycle responsibilities first, preserve gameplay and E/S recovery, then evaluate a bounded inventory extraction. Backpack acceptance, pure Console view/input, recorded fact application and prediction maintenance boundaries are implemented; see [acceptance evidence](experiments/modularization-acceptance.md). A total-unit capacity probe is next, followed by an evidence-based assembly extraction decision.

The application-layer ResourceCycle example implements shared resource stock, day/night regeneration and three characters with independent timed gathering through one Console, using Mud as a reference rather than migrating it in place. See [the multiplayer rules](design/resource-cycle-multiplayer-gathering.md) and [acceptance evidence](experiments/resource-cycle-multiplayer-acceptance.md). The original [resource cycle design](design/resource-cycle-demo.md), [prototype scope](implementation/resource-cycle-prototype.md), [batch acceptance](experiments/resource-cycle-acceptance.md) and [single-character slice](design/resource-cycle-play.md) describe preceding experiments.

Spatial implements passage-scoped discovery, one transition-completion seam and time-aware validation. [Runtime derived predictions](design/runtime-derived-predictions.md) keep immutable Contacts in memory, persist business truth and recorded decisions, and rebuild on load; see [current acceptance evidence](experiments/runtime-derived-predictions-acceptance.md). The preceding [persisted-contact acceptance](experiments/local-prediction-acceptance.md) is historical. The [dialectical review](research/local-prediction-design-review.md) and [new-combination discovery survey](research/prediction-discovery/README.md) explain the discovery choices.

ResourceCycle separates durable business state from runtime predictions and fixed bindings; see [the original cache migration evidence](experiments/resource-cycle-runtime-cache-acceptance.md). Multiplayer preserves that boundary: activity deadlines and attention requests are independent, while joining/depletion perceptions and inventory transfers are recorded atomically in business facts. Balance changes to saved movement/growth parameters require separate explicit semantics.

The previous [Spacetime engine TODO / task brief](implementation/spacetime-engine-todo.md) is complete; see the [executed evidence](build-log/0027-spacetime-extraction.md).

## Application research

- [Game revision cross-review](research/game-revision-review.md): independent challenges and cross-examination of load-time State/Event upgrades; source-state stability, mixed-version alignment and a smaller application API.
- [New-combination discovery and simulation algorithms](research/prediction-discovery/README.md): five parallel surveys, primary sources, current time-semantics counterexamples, and a staged local-discovery proposal; algorithm and API recommendations remain provisional.
- [Bringing Mud's player experience to a new DemoApp](research/mud-experience-portability.md): source-backed feasibility, reusable presentation and action contracts, application gaps, time/persistence integration boundaries, and suggested slices. This is an investigation, not an approved implementation plan.

## Frozen applications and design reserve

The [Player runtime archive](archive/player-runtime.md) preserves Protocol, Player, Decision.Validation, Player.Agency, Server, WebUI, their tests and dedicated documents. It includes the Player/Plan principles, Dynamic-Programmer design, R1–R8 build orders, spatial knowledge work and historical browser acceptance. These are technical reserves rather than active engine requirements.

The [FirstBoard and LLM archive](archive/firstboard-llm.md) preserves the earlier scenario, Demo, persistence adapter and LLM implementation. Its [0024 archival record](build-log/0024-archive-firstboard-and-verify-core.md) describes that historical split, not the current project list or a new free-play task.

## Consumer feedback and older material

[DramaBoard to DurableGraph](feedback/durablegraph/README.md) indexes version-specific API feedback. Its FirstBoard consumer evidence is historical and does not prove the current dependency upgrade.

The [archive index](archive/README.md) lists recovery paths and older records. Historical bodies under `docs/archive/legacy/` and repository-root `archive/` units are excluded from default local text search; inspect them explicitly with `rg --no-ignore`.
