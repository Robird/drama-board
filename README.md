# DramaBoard

DramaBoard is an experimental spacetime engine composed of three libraries: **Kernel** arbitrates and commits deterministic occurrences, **Spatial** models places, passages, moving entities and contacts, and **Host** drives Kernel steps. The core does not require a Player or an application server.

Read [PROJECT-STATE.md](PROJECT-STATE.md) for the active boundary and deferred decisions, and [docs/README.md](docs/README.md) for design and evidence. [AGENTS.md](AGENTS.md) defines repository collaboration rules. The application-layer ResourceCycle example explores persistent predictions and dependency-driven invalidation without changing the spacetime core.

## Build and test

Requires .NET SDK **10.0.201** and sibling `durable-graph` / `atelia-storage` source checkouts. Build and test either equivalent solution entry point directly:

```powershell
dotnet build DramaBoard.slnx -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
dotnet test DramaBoard.slnx -c Release --no-build --no-restore -m:1
```

Source roots, generator/schema wiring and validation boundaries are documented in [DurableGraph source dependencies](docs/source-dependencies.md). Both solutions contain Kernel, Spatial, Host, ResourceCycle, its Console entrypoint and their tests. The default workflow needs no package preparation, Node, browser, Player, or Server.

## Day/night resource cycle experiment

Two plants grow in their preferred light: sunflower needs three daylight hours, moongrass needs four night hours. Growth pauses across light changes; a fixed destruction script resets growth. Persistent predictors declare event dependencies, route invalidations, and recompute only affected predictions. The diagnostic view shows conditions, remaining work, predictions, subscriptions and process-local counters.

```powershell
# In-memory demonstration, including every intermediate diagnostic frame:
dotnet run --project src/ResourceCycle.Cli -c Release -- --trace

# Save after five transitions, then resume the same world in another process:
dotnet run --project src/ResourceCycle.Cli -c Release -- --store artifacts/my-cycle --steps 5
dotnet run --project src/ResourceCycle.Cli -c Release -- --store artifacts/my-cycle --until-hour 88

# Full recomputation baseline, and the multi-process comparison smoke:
dotnet run --project src/ResourceCycle.Cli -c Release -- --full-recompute
pwsh -NoProfile -File scripts/Smoke-ResourceCycle.ps1
```

The default world starts at D0 16:00 and runs through events up to absolute hour 88 (D3 16:00), without wall-clock delays. If no event lands on that boundary, the clock stays at the last committed event. Use `--help` for options, or `--snapshot PATH` for a final Markdown diagnostic. An existing store is resumed, never reset; use a new path to start again.

This example has one DurableGraph root for the world subgraphs, predictions, bindings and Kernel cursor. Recorded events contain concrete changes; pending recovery does not rerun business planning. There are no characters, items, Player calls or spatial integration. See [the design](docs/design/resource-cycle-demo.md) and [acceptance evidence](docs/experiments/resource-cycle-acceptance.md) for the limits of this prototype.

## Small world scenario

[SpacetimeHostScenarioTests](tests/Spatial.Tests/Acceptance/SpacetimeHostScenarioTests.cs) directly composes all three libraries. Two entities travel toward each other, meet at the floored contact time, one reverses strictly inside the passage, and both arrive at the same model time. It checks contact consumption, invalidation of the old arrival, deterministic arbitration under reordered inputs, and replay. Run it separately with:

```powershell
dotnet test tests/Spatial.Tests/Spatial.Tests.csproj --filter FullyQualifiedName~SpacetimeHostScenarioTests
```

The spatial scenario uses the in-memory E/S contract. ResourceCycle separately provides an application-specific disk adapter; it is not a generic persistence host for Spatial or arbitrary games.

## Technical reserve

The Player/Protocol, role planning, free-play Server, WebUI, presentation caches, and dedicated tests and documents are frozen in the [Player runtime archive](docs/archive/player-runtime.md). The earlier [FirstBoard and LLM archive](docs/archive/firstboard-llm.md) remains available. Restore the documented baseline in a separate worktree to run an old application; archived projects are outside the active solutions and need not compile against the current core.
