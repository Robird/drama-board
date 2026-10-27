# DramaBoard

DramaBoard is an experimental spacetime engine composed of three libraries: **Kernel** arbitrates and commits deterministic occurrences, **Spatial** models places, passages, moving entities and contacts, and **Host** drives Kernel steps. The core does not require a Player or an application server.

Read [PROJECT-STATE.md](PROJECT-STATE.md) for the active boundary and deferred decisions, and [docs/README.md](docs/README.md) for design and evidence. [AGENTS.md](AGENTS.md) defines repository collaboration rules. The application-layer ResourceCycle example explores persistent business state and dependency-driven runtime predictions, with a multiplayer gathering Console mode, without changing the spacetime core.

## Build and test

Requires .NET SDK **10.0.201** and sibling `durable-graph` / `atelia-storage` source checkouts. Use the single solution entry point:

```powershell
dotnet build DramaBoard.slnx -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
dotnet test DramaBoard.slnx -c Release --no-build --no-restore -m:1
```

Source roots, generator/schema wiring and validation boundaries are documented in [DurableGraph source dependencies](docs/source-dependencies.md). `DramaBoard.slnx` contains Kernel, Spatial, Host, Inventory, ResourceCycle, its Console entrypoint and their tests, plus the sibling source projects needed for IDE/LSP navigation. The redundant `DramaBoard.Local.slnx` has been removed. The default workflow needs no package preparation, Node, browser, Player, or Server.

[Inventory](src/Inventory/ItemInventory.cs) is a small domain module for immutable item counts and optional total-unit capacity. It depends on DurableGraph, independently of the spacetime core and the game. ResourceCycle composes it with gathering rules and registers its models under the same game root; see [layering and composition](docs/design/layering-and-composition.md).

## Day/night resource cycle experiment

Two resource patches regenerate in their preferred light: sunflower adds one unit every three daylight hours, moongrass every four night hours, until storage is full. Regeneration pauses across light changes and preserves progress; a fixed batch-only destruction script clears stock and resets progress. Runtime predictors declare event dependencies and recompute affected predictions. The diagnostic view shows conditions, stock, predictions, subscriptions and process-local counters.

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

This example has one DurableGraph root for business world subgraphs and the Kernel cursor. Predictions and fixed bindings are runtime caches, rebuilt on cold open and maintained after business facts are folded. Pending recovery repeats pure cache completion, never player input or business planning. See [runtime cache acceptance](docs/experiments/resource-cycle-runtime-cache-acceptance.md). The batch experiment has no character or inventory; its original scope is recorded in [the design](docs/design/resource-cycle-demo.md) and [original acceptance](docs/experiments/resource-cycle-acceptance.md).

## Play the gathering scene

```powershell
dotnet run --project src/ResourceCycle.Cli -c Release -- --play
dotnet run --project src/ResourceCycle.Cli -c Release -- --play --store artifacts/my-garden
pwsh -NoProfile -File scripts/Smoke-ResourceCyclePlay.ps1
```

Three characters share two resource patches and take turns using the same Console. Each has a separate inventory, skill level and timed activity. The full Markdown view identifies the active character, their current batch and pending perceptions, resource stock and other collectors; it hides other characters' skill levels and future completion times.

Commands: `look`, `help`, `wait <1–24 hours>`, `gather sunflower <count|all>`, `gather moongrass <count|all>`, `continue`, `cancel`, and `quit`. `all` freezes the current stock as a finite target; it does not reserve stock. Each cycle consumes one available unit and grants one item. A cycle that finishes without stock ends the batch. Faster collectors can finish first; simultaneous completions use deterministic arbitration.

Logical time pauses for each requested decision. A new collector or a stock transition to zero wakes affected collectors without resetting their activity. `continue` acknowledges perceptions and preserves the original deadline; `cancel` keeps collected items but abandons the unfinished cycle. Ordinary intermediate harvests continue automatically. Queries and invalid commands do not change the world or acknowledge perceptions. For a first outing, assign `gather sunflower all` to two characters, let the third `wait 1`, and respond to the joining/depletion notices.

With `--store`, completed changes are saved automatically and the same command resumes the scene. Without it, progress exists only in memory. Play and batch stores are deliberately distinct; neither mode silently advances the other's store. This mode has no scripted destruction, movement, NPCs, LLM calls or GUI. Older ResourceCycle saves are not a compatibility target; use a fresh directory.

See [the multiplayer gathering rules](docs/design/resource-cycle-multiplayer-gathering.md) and [acceptance evidence](docs/experiments/resource-cycle-multiplayer-acceptance.md). The earlier [single-character slice](docs/design/resource-cycle-play.md) and [its acceptance evidence](docs/experiments/resource-cycle-play-acceptance.md) describe the preceding experiment, not the current timed gathering rules. The current prediction/storage contract is [runtime-derived predictions](docs/design/runtime-derived-predictions.md).

## Play the survival prototype

The exploration, survival-needs and weather slices of the [survival milestone](docs/design/survival-demo-milestone.md) are playable:

```powershell
dotnet run --project src/ResourceCycle.Cli -c Release -- --survival
dotnet run --project src/ResourceCycle.Cli -c Release -- --survival --store artifacts/my-expedition
pwsh -NoProfile -File scripts/Smoke-ResourceCycleSurvival.ps1
```

Explore a small mountain map and find either exit. Use `go <trail-id>` from the displayed local choices, `gather <resource-id> <count|all>`, `wait <hours>`, `look`, `help`, and `quit`. Locations and resource memories become known through visits; old observations are labelled with their time. Travel takes model time and cannot yet be cancelled midway. Completed changes save automatically when a store is supplied; use the same command to resume. A completed ending does not advance further.

Food and water decline with model time; exposure cools you at night, while shelter restores warmth. Use `use item.food` or `use item.water` to consume one supply without advancing time. A low-body warning pauses the world even during travel: replenish if possible, then `continue` to acknowledge it without restarting the original journey or activity. At a location, `cancel` can end gathering or waiting. Any need reaching zero ends the journey; reaching a safe exit first wins. Wood currently has no fuel use.

Every two model hours a weather window can be calm or bring a cold wind. An exposed character loses up to 10 warmth; shelter prevents this loss. The UI shows the next opportunity and the latest actual result, without revealing future random outcomes. Body/action time estimates exclude future cold winds. Weather uses a fixed default seed for reproducible experiments; the game factory accepts a different seed, and saved games retain their own stream state.

Independent full playtests are still pending; this is not yet the completed phase-one milestone. GameRevision is now 4; older development saves are unsupported, so use a fresh directory. Batch, gathering and survival modes reject each other's stores. Current evidence and limitations are tracked in [survival acceptance](docs/experiments/survival-demo-acceptance.md).

## Small world scenario

[SpacetimeHostScenarioTests](tests/Spatial.Tests/Acceptance/SpacetimeHostScenarioTests.cs) directly composes all three libraries. Two entities travel toward each other, meet at the floored contact time, one reverses strictly inside the passage, and both arrive at the same model time. It checks contact consumption, invalidation of the old arrival, deterministic arbitration under reordered inputs, and replay. Run it separately with:

```powershell
dotnet test tests/Spatial.Tests/Spatial.Tests.csproj --filter FullyQualifiedName~SpacetimeHostScenarioTests
```

The spatial scenario uses the in-memory E/S contract. ResourceCycle separately provides an application-specific disk adapter; it is not a generic persistence host for Spatial or arbitrary games.

## Technical reserve

The Player/Protocol, role planning, free-play Server, WebUI, presentation caches, and dedicated tests and documents are frozen in the [Player runtime archive](docs/archive/player-runtime.md). The earlier [FirstBoard and LLM archive](docs/archive/firstboard-llm.md) remains available. Restore the documented baseline in a separate worktree to run an old application; archived projects are outside the active solutions and need not compile against the current core.
