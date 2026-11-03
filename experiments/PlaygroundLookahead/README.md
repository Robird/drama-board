# Actual resident Playground lookahead fixture

This executable repeatedly calls the production `PlaygroundHost.RunAsync` in one process. It performs warmup on both paths, then alternates off/on order for low/high coupling and independent/shared single-slot provider capacity. It never calls a network provider.

Run from the repository root, serially with other dotnet work:

```powershell
dotnet run --project experiments/PlaygroundLookahead -c Release -- --self-check --output artifacts/playground-speculative-lookahead/self-check-new
dotnet run --project experiments/PlaygroundLookahead -c Release -- --output artifacts/playground-speculative-lookahead/resident-new --pairs 5 --operations 16 --delay-ms 1000
```

Use fresh output directories. Formal runs reject fewer than five pairs or sixteen successful operations. `--self-check` permits a smaller matrix. A failure exits nonzero and preserves raw evidence and failure.json.

Each run starts with an independent in-memory history over the same immutable initial world/cursor. The host stops normally between committed steps when the history wrapper counts exactly N successful actor operations; candidate cleanup remains the production host's responsibility. Low coupling places a/b at camp/cabin; high coupling places them at camp/stream and has each actor shout on its first operation in each context. Thereafter the fixture selects the explicitly legal wait1 or continue entry from the complete frozen model material. Responses never depend on global physical request order. The physical id in timeline is accounting only; all responses use the same legal tool call id.

`raw.jsonl` is the production hash-chained model/lookahead audit. `timeline.jsonl` records complete physical requests/results, provider queue wait, committed complete facts and service measurements. `actual-inputs.json`, operations.json, events.json and final.json preserve the compared inputs, operations, facts and final business/derived state. Pair comparison validates actor/context/operation sequence, then retains a bijection from original operation IDs to comparison tokens applied only to explicit OperationRecordId JSON properties. Complete model inputs compare unchanged, with no replacements in model-visible strings; no fields are deleted. Random request/run ids in audit envelopes are not effective input and are not compared as input. All effective input fields are retained.

Startup preparation, host time before first service, effective work from first actual Choose entry to final committed operation, and post-commit host cleanup are reported separately. Production raw projection/append/flush time is reported separately from fixture timeline writes. Physical counts and all samples are retained. Projection, comparison, logging, service and provider timers overlap: **do not add them**. Independent capacity means each physical request can progress independently; shared capacity uses one semaphore for all provider service. Production raw logging and fixture timeline writes are also included in their enclosing wall intervals.



Formal runs additionally require at least two distinct committed operation model times in every run, at least two candidate creations and two adoptions in each low-coupling on run, and at least one discarded candidate in each high-coupling on run. Pair input equality is mandatory for both workloads. Failing coverage preserves per-run coverage.json/sample.json and exits before a successful matrix summary. Small self-check runs report measured model span, actor contexts and candidate lifecycle counts without requiring cross-time coverage.

`--online` and `--online-self-check` dispatch to the separately maintained online runner/budget self-check. The default resident matrix is strictly offline.

The formal costly-provider matrix is predeclared at 1000ms service delay; the earlier 5ms self-check remains separate short-latency evidence, including its negative overhead results. In shared capacity, queue admission remains cancellable but provider service ignores cancellation after acquiring the slot, so discarded in-service work consumes the complete service interval. Independent capacity retains cooperative cancellation.

`physical-service.json` joins production physical ordinals to fixture physical ids, verifies the full input, and records every request's queue, started service, completion/failure and demand/adopted/discarded/unadopted-at-stop disposition. Local fixture outcomes are fully observed; no remote unknown classification is inferred for real providers. Discarded service time includes unadopted candidates at stop. Final cleanup remains inside host wall and is separate from effective-work wall.

EventCommit/StateCommit timings measure only the inner **in-memory** history methods. Fact serialization and timeline writes are separate; these figures do not measure durable save I/O. HostJournalAppend measures the in-memory host journal's metadata and append projection/validation/hash/install, with no disk flush in this matrix. ProductionRawWrite includes raw projection/append/flush and remains nested within enclosing intervals.

`--real-performance-self-check --output FRESH` checks the fresh durable performance runner with fixture clients in all four LLM/Human-script and off/on profiles, without network calls. `--real-performance` runs one pre-reserved online batch with `--output FRESH --connections PATH --grant PATH --scenario llm|human-script --operations 8 --think-ms 5000`. Human script input uses the production Console reader and alternates `wait 1` / `continue`; rejections are retained and this is not a real human playtest. Host wall time stops before post-run exports/cold-open inspection.

`real_performance.py prepare` writes a predeclared eight-run plan without reserving calls. Its separate `reserve`, `execute` and `summarize` actions consume fresh grants once, stop on unexplained provider failure and reconcile physical requests to the existing research ledger. Reservation/execution require the coordinator's explicit execution window. Physical intervals measure client-observed latency (including wrapper bookkeeping), not provider-internal compute. Client request overlap and latency covered before adoption do not prove independent server capacity; naturally different trajectories support observational on/off comparisons only.

The four actions are `prepare --root FRESH --connections PATH --ledger EXISTING`, `reserve --root PLAN`, `execute --root PLAN --dll BUILT_DLL`, and `summarize --root PLAN`. Existing grants and run directories cannot be reused, including the completed p3/p5 online grants. A new authorized batch requires new identifiers and a fresh reservation; do not rerun `execute` on these artifacts.

To reproduce the completed batch's derived statistics without provider calls or changes to its ledger/raw evidence:

```powershell
python experiments/PlaygroundLookahead/real_performance.py summarize --root artifacts/playground-speculative-lookahead/p5-real-performance
python artifacts/playground-speculative-lookahead/p5-real-performance/verify_evidence.py
```

These commands only rewrite derived `samples.json`, `audit.json`, `physical-accounting.json` and `evidence-verification.json`. The measured results and limitations are in [real-performance.md](../../docs/research/playground-speculative-lookahead/results/real-performance.md).
