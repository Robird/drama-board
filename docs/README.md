# DramaBoard documentation

Start with the repository's [current project state](../PROJECT-STATE.md). The active engine consists of Kernel, Spatial and Host. Player planning, interaction and presentation are retained as a frozen experiment.

## Current design and core

- [Simulation Kernel](design/simulation-kernel.md): occurrence, time, deterministic arbitration and atomic publication; no Player implementation is required.
- [Graph Spatial World](design/graph-spatial-world.md): objective locations, passages, motion, contact and arrival.
- [DurableGraph occurrence persistence](design/durablegraph-occurrence-persistence.md): retained E/S seam and finite pending recovery contract; this is not a disk persistence adapter.
- [Kernel occurrence baseline](implementation/kernel-occurrence-baseline.md): scheduler evidence, with superseded storage clauses identified.
- [Passage contact floor timing](worksets/passage-contact-floor.md): retained Spatial floor-contact and ceil-arrival semantics.
- [DurableGraph source dependencies](source-dependencies.md): current sibling-source development entry, including generator and schema verification.

## Current work

The first application-layer example implements day/night changes, plant growth and scripted destruction, using Mud as a reference rather than migrating it in place. See [the resource cycle design](design/resource-cycle-demo.md), [prototype scope](implementation/resource-cycle-prototype.md) and [execution evidence](experiments/resource-cycle-acceptance.md). It demonstrates persistent predictions, dependency-driven invalidation and single-root recovery. Characters, multiplayer gathering and spatial changes remain deferred.

The previous [Spacetime engine TODO / task brief](implementation/spacetime-engine-todo.md) is complete; see the [executed evidence](build-log/0027-spacetime-extraction.md).

## Frozen applications and design reserve

The [Player runtime archive](archive/player-runtime.md) preserves Protocol, Player, Decision.Validation, Player.Agency, Server, WebUI, their tests and dedicated documents. It includes the Player/Plan principles, Dynamic-Programmer design, R1–R8 build orders, spatial knowledge work and historical browser acceptance. These are technical reserves rather than active engine requirements.

The [FirstBoard and LLM archive](archive/firstboard-llm.md) preserves the earlier scenario, Demo, persistence adapter and LLM implementation. Its [0024 archival record](build-log/0024-archive-firstboard-and-verify-core.md) describes that historical split, not the current project list or a new free-play task.

## Consumer feedback and older material

[DramaBoard to DurableGraph](feedback/durablegraph/README.md) indexes version-specific API feedback. Its FirstBoard consumer evidence is historical and does not prove the current dependency upgrade.

The [archive index](archive/README.md) lists recovery paths and older records. Historical bodies under `docs/archive/legacy/` and repository-root `archive/` units are excluded from default local text search; inspect them explicitly with `rg --no-ignore`.
