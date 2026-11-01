# DramaBoard

DramaBoard is an experimental spacetime engine composed of three libraries: **Kernel** arbitrates and commits deterministic occurrences, **Spatial** models places, passages, moving entities and contacts, and **Host** drives Kernel steps. The core does not require a Player or an application server.

Read [PROJECT-STATE.md](PROJECT-STATE.md) for the active boundary and deferred decisions, and [docs/README.md](docs/README.md) for design and evidence. [AGENTS.md](AGENTS.md) defines repository collaboration rules. The application-layer ResourceCycle example explores persistent business state and dependency-driven runtime predictions, with a multiplayer gathering Console mode, without changing the spacetime core.

## Build and test

Requires .NET SDK **10.0.201** and sibling `durable-graph` / `atelia-storage` / `atelia-completion` source checkouts. Use the single solution entry point:

```powershell
dotnet build DramaBoard.slnx -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
dotnet test DramaBoard.slnx -c Release --no-build --no-restore -m:1
```

Source roots, generator/schema wiring and validation boundaries are documented in [DurableGraph source dependencies](docs/source-dependencies.md). `DramaBoard.slnx` contains the core libraries, shared Inventory/Goals/controller/Gym modules, ResourceCycle and Microtown with their Console entrypoints and tests, plus the sibling source projects needed for IDE/LSP navigation. The redundant `DramaBoard.Local.slnx` has been removed. The default workflow needs no package preparation, Node, browser, Player, or Server.

[Inventory](src/Inventory/ItemInventory.cs) is a small domain module for immutable item counts and optional total-unit capacity. It depends on DurableGraph, independently of the spacetime core and the game. ResourceCycle composes it with gathering rules and registers its models under the same game root; see [layering and composition](docs/design/layering-and-composition.md).

## Microtown economy prototype

[Microtown](src/Microtown/README.md) is a second independent game: three residents buy raw materials, produce meals and woodware, eat, travel and sell to a finite external market. A single Console serves Human or LLM residents in turn, with independent saves, character-visible material and Gym feedback/trajectory/fork tools. Residents can exchange speech and gifts, including during notified production/waiting responses. Addressed offers support atomic item/money exchange, rejection, withdrawal and expiry. Owner prediction maintenance, a real three-resident 30-hour economic day, feedback fork and guided resident trade are verified; see [milestone acceptance](docs/experiments/microtown-acceptance.md) and [playtest limits](docs/experiments/microtown-playtest.md).

```powershell
dotnet run --project src/Microtown.Cli -c Release -- --store artifacts/my-town --hours 48
dotnet run --project src/Microtown.Cli -c Release -- --store artifacts/my-town
dotnet run --project src/Microtown.Cli -c Release -- --llm --feedback --store artifacts/my-llm-town --max-calls 32
pwsh -NoProfile -File scripts/Smoke-Microtown.ps1
```

Use `:quit` or EOF to pause, `:look` to redisplay, and `--max-operations` to bound the current run. See [Console usage](src/Microtown.Cli/README.md) and [persistence/interaction evidence](docs/experiments/microtown-persistence-acceptance.md).

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

Commands: `:look`, `:help`, `wait <1–24 hours>`, `gather sunflower <count|all>`, `gather moongrass <count|all>`, `continue`, `cancel`, and `:exit`. `all` freezes the current stock as a finite target; it does not reserve stock. Each cycle consumes one available unit and grants one item. A cycle that finishes without stock ends the batch. Faster collectors can finish first; simultaneous completions use deterministic arbitration.

Logical time pauses for each requested decision. A new collector or a stock transition to zero wakes affected collectors without resetting their activity. `continue` acknowledges perceptions and preserves the original deadline; `cancel` keeps collected items but abandons the unfinished cycle. Ordinary intermediate harvests continue automatically. Queries and invalid commands do not change the world or acknowledge perceptions. For a first outing, assign `gather sunflower all` to two characters, let the third `wait 1`, and respond to the joining/depletion notices.

With `--store`, completed changes are saved automatically and the same command resumes the scene. Without it, progress exists only in memory. Play and batch stores are deliberately distinct; neither mode silently advances the other's store. This mode has no scripted destruction, movement, NPCs, LLM calls or GUI. Older ResourceCycle saves are not a compatibility target; use a fresh directory.

See [the multiplayer gathering rules](docs/design/resource-cycle-multiplayer-gathering.md) and [acceptance evidence](docs/experiments/resource-cycle-multiplayer-acceptance.md). The earlier [single-character slice](docs/design/resource-cycle-play.md) and [its acceptance evidence](docs/experiments/resource-cycle-play-acceptance.md) describe the preceding experiment, not the current timed gathering rules. The current prediction/storage contract is [runtime-derived predictions](docs/design/runtime-derived-predictions.md).

## Play the survival prototype

Phase one of the [survival milestone](docs/design/survival-demo-milestone.md) is complete: a single-character Console escape with exploration, supplies, shelter and weather risks.

```powershell
dotnet run --project src/ResourceCycle.Cli -c Release -- --survival
dotnet run --project src/ResourceCycle.Cli -c Release -- --survival --store artifacts/my-expedition
pwsh -NoProfile -File scripts/Smoke-ResourceCycleSurvival.ps1
```

Explore a small mountain map and find either exit. Use `go <trail-id>` from the displayed local choices, `gather <resource-id> <count|all>`, `wait <hours>`, `:look`, `:help`, and `:exit`. Locations and resource memories become known through visits; old observations are labelled with their time. Travel takes model time and cannot yet be cancelled midway. Completed changes save automatically when a store is supplied; use the same command to resume. A completed ending does not advance further.

Survival defaults to the decision layout, grouping supplies, routes and resources with their relevant actions and risks, with warmth/exposure visible at the input prompt. Use `:layout` to list layouts, `:layout classic` for the original full view, or `:layout decision` to switch back immediately. `:look` redraws the latest observation without changing your layout; `:help` and `:exit` are also available. Meta commands require the colon prefix; game actions such as `go`, `use` and `continue` do not. Layout selection lasts for this Console session, including across characters in `--play`; it does not advance time or alter game state. See [Console layouts](docs/design/console-layouts.md).

Food and water decline with model time; exposure cools you at night, while shelter restores warmth. Use `use item.food` or `use item.water` to consume one supply without advancing time. A low-body warning pauses the world even during travel: replenish if possible, then `continue` to acknowledge it without restarting the original journey or activity. At a location, `cancel` can end gathering or waiting. Any need reaching zero ends the journey; reaching a safe exit first wins. Wood currently has no fuel use.

Every two model hours a weather window can be calm or bring a cold wind. An exposed character loses up to 10 warmth; shelter prevents this loss. The UI shows the next opportunity and the latest actual result, without revealing future random outcomes. Body/action time estimates include known day/night changes, assume no replenishment and unchanged exposure or shelter, and exclude future cold winds; travel estimates assume exposure. Weather uses a fixed default seed for reproducible experiments; the game factory accepts a different seed, and saved games retain their own stream state.

Survival decisions now use explicit context identities. A timed action ends the current context; the next wake displays its frozen action outcome, body/inventory changes and wake reasons. `handoff <text>` edits a note for the next context, and `handoff --clear` clears it; notes are passed once and never automatically forwarded. Supplies, notes and queries stay in the same context. Interactive terminals clear on first delivery and a new context, while redirected output remains a transcript. See [the handoff contract](docs/design/decision-context-handoff.md).

Survival characters also own persistent [goal records](docs/design/goal-management/README.md). For example, `create-goal {"title":"Prepare water","criteria":"Carry two portions before leaving","focus":true}` records an intention; `show-goal {}` lists retained goals. The UI provides all twelve commands for editing, linking, focusing, pausing, finishing and cleaning up records. Unfinished goals automatically appear in later contexts; focus and goal status never cancel an activity or fabricate a world result. These commands use strict JSON, take no model time, and still record an interaction and rotate service. Long-term intentions belong in goals rather than repeated handoff notes. See [engineering evidence](docs/experiments/goal-management-acceptance.md) and run `pwsh -NoProfile -File scripts/Smoke-ResourceCycleGoals.ps1` after building for the Console smoke.

Every nonempty Console submission receives an implicit ToolCallId. With `--store`, `operations.jsonl` retains received operations and paired results, including queries and rejected commands. These host records do not advance game E/S; recovery uses committed game facts to repair missing results without repeating actions. Full LLM conversations and endpoint ID mapping are outside this slice.

Two independent Console playtesters reached different exits with different supply strategies; natural hypothermia and deliberate dehydration failures were also recorded. These were agent playtests, not external human user research. The classic full view remains verbose, wood has no fuel use, and travel cannot be cancelled. The current revision is recorded in [PROJECT-STATE](PROJECT-STATE.md); older development saves are unsupported, so use a fresh directory. Batch, gathering and survival modes reject each other's stores. See [survival acceptance](docs/experiments/survival-demo-acceptance.md) for the earlier milestone's tests, transcripts and limits. Single-character LLM control and multiplayer survival, communication and following are implemented; see [the milestone](docs/design/survival-demo-milestone.md) and the current entry points below.

## Let a model play survival

```powershell
dotnet run --project src/ResourceCycle.Cli -c Release -- --survival --llm `
  --model gpt-6.1-sol --store artifacts/my-llm-game --max-calls 128
```

This uses Completion's Codex Responses client and standard Codex auth-file discovery; `--auth-file PATH` selects an explicit file. The same command resumes the store. Each model call receives the character's full retained Context and submits one `act` tool call; the game validates and commits it. Spectator output separates generated operations from committed results, and each host run saves a separate JSONL audit. Credentials never enter the game graph or audit.

Call limits, cancellation and failed responses stop without choosing a substitute action. Custom V3 connection catalogs require both `--catalog PATH` and `--connection ID`. See [configuration and recovery](docs/implementation/survival-llm-controller.md) and [two real model journeys](docs/experiments/survival-llm-acceptance.md).

## Two or three explorers in one survival world

```powershell
# One Human Console takes turns controlling a, b and c:
dotnet run --project src/ResourceCycle.Cli -c Release -- --survival --characters 3 --store artifacts/my-group

# Human controls a; the model controls b:
dotnet run --project src/ResourceCycle.Cli -c Release -- --survival --characters 2 --llm `
  --model gpt-6.1-sol --store artifacts/my-mixed-duo --max-calls 128
```

Characters share time, resources and weather, with separate positions, needs, inventories, contexts and map knowledge. Escape or death freezes only that character; the others continue. When all finish, the host reports all escaped, partial escape or no escape. Different exits are allowed. Global results stay outside character knowledge; distant endings are not broadcast to other explorers.

Resume with `--survival --store PATH`; omitting `--characters` uses the saved count, while an explicit mismatch is rejected. New games default to one character. `--duo` remains a parser shortcut for `--survival --characters 2` and cannot be combined with either flag. In multiplayer `--llm` mode, a is Human and every other character uses the shared model controller and call budget; Human `:exit`/EOF pauses the world.

At the same place, `follow <actor-id>` joins a leader and closes your decision context. The leader's normal `go` moves all current followers atomically, and arrival preserves the relationship. Followers cannot start their own `go`, `gather` or `wait`; at a work site, the leader uses `dismiss-followers` or a follower uses `unfollow` when they next have a decision opportunity. Finish or cancel activities before following again. There is no automatic waiting for absent teammates, and relationships cannot be changed during travel. Personal warnings still wake followers; handle supplies and use `continue` to resume following. See [the following contract](docs/design/character-following.md) and [implementation evidence](docs/experiments/character-following-acceptance.md).

At the same place, use `say <actor-id> <text>` to speak and `give <actor-id> <item-id> <count>` to transfer supplies atomically. Use `shout <text>` at a place to reach other active characters there and at directly sound-connected places; travellers do not hear it. The current map connects camp ↔ stream and ridge ↔ cabin for sound, independently of travel paths. All three commands share four successful sends per character per model instant; a shout costs one send even with no listeners and does not reveal the audience. See [the shout contract](docs/design/spatial-relations-and-shout.md) and [acceptance evidence](docs/experiments/spatial-shout-acceptance.md). Received communication requests a decision without cancelling the current activity; `continue` acknowledges it and keeps the original deadline. An idle independent character must arrange a timed action or follow someone to let time advance. Long-term agreements belong in goals; `handoff` passes a short note to the next context. See [the communication contract](docs/implementation/survival-communication-plan.md) and [engineering/preflight evidence](docs/experiments/survival-communication-acceptance.md); genuine Human/LLM cooperation acceptance remains pending.

## Small world scenario

[SpacetimeHostScenarioTests](tests/Spatial.Tests/Acceptance/SpacetimeHostScenarioTests.cs) directly composes all three libraries. Two entities travel toward each other, meet at the floored contact time, one reverses strictly inside the passage, and both arrive at the same model time. It checks contact consumption, invalidation of the old arrival, deterministic arbitration under reordered inputs, and replay. Run it separately with:

```powershell
dotnet test tests/Spatial.Tests/Spatial.Tests.csproj --filter FullyQualifiedName~SpacetimeHostScenarioTests
```

The spatial scenario uses the in-memory E/S contract. ResourceCycle separately provides an application-specific disk adapter; it is not a generic persistence host for Spatial or arbitrary games.

## Technical reserve

The Player/Protocol, role planning, free-play Server, WebUI, presentation caches, and dedicated tests and documents are frozen in the [Player runtime archive](docs/archive/player-runtime.md). The earlier [FirstBoard and LLM archive](docs/archive/firstboard-llm.md) remains available. Restore the documented baseline in a separate worktree to run an old application; archived projects are outside the active solutions and need not compile against the current core.
