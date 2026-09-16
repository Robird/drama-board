# DramaBoard documentation

Start with the repository's [current project state](../PROJECT-STATE.md). It identifies the active core, validation entry points, and deferred product decisions.

## Current design and core

- [Player runtime and Plan-Maintainer principles](design/player-runtime/README.md): shared Human/LLM control, future plans, attention, and the time-consuming “think again” action; mechanism and engineering choices remain open.
- [World VM, Player Process, and Dynamic-Programmer](design/player-runtime/dynamic-programmer.md): editable subjective state, sequential Move/Think/Stay programs, execution boundaries, interrupts, and a minimal validation slice.
- [Simulation Kernel](design/simulation-kernel.md): occurrence, time, publication, and Player boundaries.
- [Graph Spatial World](design/graph-spatial-world.md): the objective-space model and its Kernel boundary.
- [DurableGraph occurrence persistence](design/durablegraph-occurrence-persistence.md): retained E/S seam and finite recovery contract.
- [Kernel occurrence baseline](implementation/kernel-occurrence-baseline.md): time and scheduler evidence.
- [Passage contact floor timing](worksets/passage-contact-floor.md): retained Spatial floor-contact and ceil-arrival semantics.
- [Player spatial knowledge](research/player-spatial-knowledge.md): the retained known-graph seam; exploration disclosure is deferred.
- [DurableGraph package source](worksets/durablegraph-package-source.md): fixed package preparation and consumption path.

## Current work

The [shared Player-runtime principles](design/player-runtime/README.md) now have a [Dynamic-Programmer mechanism model](design/player-runtime/dynamic-programmer.md), refined through [independent review and cross-examination](worksets/dynamic-programmer-review.md). Valid complete programs authorize execution; accepted empty programs become Stay, and a Stay prefix preserves a future program while idling. Active reactivation is deferred. The next step is an engineering work order for the sequential Move/Think/Stay slice; these runtime capabilities are not implemented yet.

[Server, two WebUI pages, and the first movement loop](build-log/0025-server-webui-first-movement.md) is implemented: a local executable, a small fully known map, Human movement, and separate Player/diagnostic views. See the [verification and design handback](build-log/0025-verification.md) for executed checks and platform boundaries.

[Archive FirstBoard and verify the core](build-log/0024-archive-firstboard-and-verify-core.md) records the completed archival boundary and A1--A7 validation. Disk persistence and LLM integration remain deferred.

## Consumer feedback

[DramaBoard to DurableGraph](feedback/durablegraph/README.md) indexes active API feedback. Its FirstBoard consumer evidence is historical and links to the archive where needed.

## Historical material

[FirstBoard and LLM archive](archive/firstboard-llm.md) is the recovery index for the retired source, tests, Demo, LLM implementation, and dedicated documents. [Archive](archive/README.md) also indexes older retained material. Both `legacy/` and the repository-root `archive/firstboard-llm/` bodies are excluded from default local text search; inspect them explicitly with `rg --no-ignore`.
