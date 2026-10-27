# DramaBoard

DramaBoard is an experimental spacetime engine composed of three libraries: **Kernel** arbitrates and commits deterministic occurrences, **Spatial** models places, passages, moving entities and contacts, and **Host** drives Kernel steps. The core does not require a Player or an application server.

Read [PROJECT-STATE.md](PROJECT-STATE.md) for the active boundary and deferred decisions, [the task brief](docs/implementation/spacetime-engine-todo.md) for the extraction and dependency upgrade, and [docs/README.md](docs/README.md) for design and evidence. [AGENTS.md](AGENTS.md) defines repository collaboration rules.

## Build and test

Requires .NET SDK **10.0.201**, PowerShell 7, and the fixed sibling `durable-graph` / `atelia-storage` source revisions. Prepare the package feed, then build and test either equivalent solution entry point:

```powershell
pwsh -File scripts/Prepare-DurableGraph.ps1
dotnet build DramaBoard.slnx -t:Rebuild -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
dotnet test DramaBoard.slnx --no-build --no-restore -m:1
```

The fixed package source and its validation rules are documented in [DurableGraph package source](docs/worksets/durablegraph-package-source.md). Both solutions contain only Kernel, Spatial, Host and their tests. The default workflow needs no Node, browser, Player, or Server.

## Small world scenario

[SpacetimeHostScenarioTests](tests/Spatial.Tests/Acceptance/SpacetimeHostScenarioTests.cs) directly composes all three libraries. Two entities travel toward each other, meet at the floored contact time, one reverses strictly inside the passage, and both arrive at the same model time. It checks contact consumption, invalidation of the old arrival, deterministic arbitration under reordered inputs, and replay. Run it separately with:

```powershell
dotnet test tests/Spatial.Tests/Spatial.Tests.csproj --filter FullyQualifiedName~SpacetimeHostScenarioTests
```

The in-memory occurrence history retains the E/S and pending-recovery contract; this repository does not currently provide an active disk adapter.

## Technical reserve

The Player/Protocol, role planning, free-play Server, WebUI, presentation caches, and dedicated tests and documents are frozen in the [Player runtime archive](docs/archive/player-runtime.md). The earlier [FirstBoard and LLM archive](docs/archive/firstboard-llm.md) remains available. Restore the documented baseline in a separate worktree to run an old application; archived projects are outside the active solutions and need not compile against the current core.
