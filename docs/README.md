# DramaBoard documentation

Start with the repository's [current project state](../PROJECT-STATE.md). It identifies the active core, validation entry points, and deferred product decisions.

## Current design and core

- [Simulation Kernel](design/simulation-kernel.md): occurrence, time, publication, and Player boundaries.
- [Graph Spatial World](design/graph-spatial-world.md): the objective-space model and its Kernel boundary.
- [DurableGraph occurrence persistence](design/durablegraph-occurrence-persistence.md): retained E/S seam and finite recovery contract.
- [Kernel occurrence baseline](implementation/kernel-occurrence-baseline.md): time and scheduler evidence.
- [Passage contact floor timing](worksets/passage-contact-floor.md): retained Spatial floor-contact and ceil-arrival semantics.
- [Player spatial knowledge](research/player-spatial-knowledge.md): the retained known-graph seam; exploration disclosure is deferred.
- [DurableGraph package source](worksets/durablegraph-package-source.md): fixed package preparation and consumption path.

## Current work

[Archive FirstBoard and verify the core](build-log/0024-archive-firstboard-and-verify-core.md) records the completed archival boundary and A1--A7 validation. No free-play scene, Human frontend, map/trajectory model, or new persistence adapter has been implemented.

## Consumer feedback

[DramaBoard to DurableGraph](feedback/durablegraph/README.md) indexes active API feedback. Its FirstBoard consumer evidence is historical and links to the archive where needed.

## Historical material

[FirstBoard and LLM archive](archive/firstboard-llm.md) is the recovery index for the retired source, tests, Demo, LLM implementation, and dedicated documents. [Archive](archive/README.md) also indexes older retained material. Both `legacy/` and the repository-root `archive/firstboard-llm/` bodies are excluded from default local text search; inspect them explicitly with `rg --no-ignore`.
