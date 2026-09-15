# DramaBoard

DramaBoard is an experimental deterministic Simulation Kernel and Graph Spatial project. A local ASP.NET Core server hosts a small free-play scene, a Human Player WebUI, and a separate read-only diagnostic page. Movement is committed by the Kernel; this slice has no LLM runtime or disk resume.

Read [PROJECT-STATE.md](PROJECT-STATE.md) for the active boundary and deferred decisions. [AGENTS.md](AGENTS.md) defines repository collaboration rules, and [docs/README.md](docs/README.md) classifies the current design, evidence, package source, and historical archive.

## Build and test

Requires .NET 10, PowerShell 7, and the fixed sibling `durable-graph` / `atelia` source revisions. Prepare the package feed, then build and test either equivalent solution entry point:

```powershell
pwsh -File scripts/Prepare-DurableGraph.ps1
dotnet build DramaBoard.slnx -t:Rebuild -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
dotnet test DramaBoard.slnx --no-build --no-restore -m:1
```

The fixed package source and its validation rules are documented in [DurableGraph package source](docs/worksets/durablegraph-package-source.md). Historical FirstBoard, Demo, LLM, and persistence-adapter material is recoverable through the [FirstBoard and LLM archive](docs/archive/firstboard-llm.md), but is not part of the active build.

## Publish and play locally

Requires Node 24.x with npm and the .NET 10 ASP.NET Core runtime. After preparing the fixed packages above, run:

```powershell
pwsh -File scripts/Publish-Server.ps1
& ./artifacts/server/DramaBoard.Server.exe
```

Open [Player](http://127.0.0.1:5080/player) and [read-only diagnostics](http://127.0.0.1:5080/dev). On Linux the apphost is `./artifacts/server/DramaBoard.Server`. The complete published directory is required; the executable also works when launched from another current directory. Stop it with Ctrl+C. To choose another local port, pass `--urls http://127.0.0.1:5081`.

One server process owns one actor, starting at A at time 0. Choose an adjacent exit: A→B arrives at 1000 ms, then B→D at 4000 ms. Waiting for input consumes no model time. The map is fully known in this scenario; the server maintains the arrival trajectory. Refreshing or reopening the page continues the same run. Restarting the server creates a new run at A and loses the previous in-memory history.

The publish script runs `npm ci`, TypeScript/Vite build, then `dotnet publish`. Ordinary .NET builds do not invoke npm. For frontend iteration, build directly with `npm --prefix src/WebUI run build` and rebuild the Server to copy changed assets.

## Browser acceptance

```powershell
npm --prefix src/WebUI ci
npm --prefix src/WebUI run build
pwsh -File scripts/Publish-Server.ps1
Push-Location src/WebUI
npx playwright install chromium
Pop-Location
npm --prefix src/WebUI run test:e2e
```

The test starts its own published apphost on a dynamically assigned loopback port from a temporary directory, exercises both pages in Chromium, and stops that process. Screenshots and movement evidence are written to `artifacts/0025/`. See [the 0025 verification record](docs/build-log/0025-verification.md) for executed checks and platform boundaries.
