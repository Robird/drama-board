"""Offline plan/reservation, sequential launch and audit. Never retries a run/provider.

prepare writes a plan only. reserve appends eight fresh durable grants; execute consumes
them once. Coordinator approval is required before reserve/execute, outside this script.
"""
import argparse
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import statistics
import subprocess

RESEARCH = "playground-speculative-lookahead"

def save(path, value):
    Path(path).write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")

def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))

def lines(path):
    return [json.loads(x) for x in Path(path).read_text(encoding="utf-8-sig").splitlines()] if Path(path).exists() else []

def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()

def prepare(root, connections, ledger):
    root.mkdir(parents=True, exist_ok=False)
    runs = []
    for scenario in ("llm", "human-script"):
        for pair in range(2):
            for strategy in (("off", "on") if pair == 0 else ("on", "off")):
                name = f"{scenario}-pair{pair+1}-{strategy}"
                grant = dict(ResearchId=RESEARCH, BatchId=f"p5-real-{name}", LedgerPath=str(ledger.resolve()),
                             Strategy=strategy, MaxCalls=12, MaxInFlight=2, TimeoutSeconds=600)
                save(root / f"{name}.grant.json", grant)
                runs.append(dict(Name=name, Scenario=scenario, Pair=pair+1, Strategy=strategy,
                                 Output=str(root / name), Grant=str(root / f"{name}.grant.json")))
    save(root / "plan.json", dict(Format=1, CreatedUtc=dt.datetime.now(dt.timezone.utc).isoformat(),
        NewPhysicalCallCeiling=96, PreviousCallsNotCharged=16, TargetSuccessfulOperations=8, ThinkMilliseconds=5000,
        Connections=str(connections.resolve()), ConnectionsSha256=sha(connections), Ledger=str(ledger.resolve()),
        Endpoint=read(connections), Runs=runs,
        StopPolicy="Stop at first committed boundary with eight successful domain operations; otherwise keep capped/incomplete samples. 12 physical calls per run includes demand, candidates, feedback, correction and failures. No automatic retry; first endpoint failure stops batch for diagnosis.",
        HumanPolicy="Scripted real Console input: 5s per ReadLineAsync, alternate wait 1 / continue. Not real human acceptance. Per-role counts reported.",
        AnalysisPolicy="Natural model outputs/trajectories may differ. On/off throughput is observational, never same-trajectory causal speedup. Client interval overlap does not establish independent server compute capacity. No warmup calls. Unknown usage never zero."))

def reserve(root):
    plan = read(root / "plan.json")
    ledger = Path(plan["Ledger"])
    raw = ledger.read_bytes()
    if not raw.endswith(b"\n"):
        raise RuntimeError("Unterminated ledger; reconcile externally")
    old = lines(ledger)
    batches = {x.get("BatchId") for x in old}
    if any(read(r["Grant"])["BatchId"] in batches for r in plan["Runs"]):
        raise RuntimeError("Batch exists; reservation is non-repeatable")
    if sum(read(r["Grant"])["MaxCalls"] for r in plan["Runs"]) != 96:
        raise RuntimeError("Plan exceeds or changes approved reservation")
    with ledger.open("ab", buffering=0) as f:
        for run in plan["Runs"]:
            grant = read(run["Grant"])
            row = dict(Kind="batch-reserved", ResearchId=RESEARCH, BatchId=grant["BatchId"],
                       Utc=dt.datetime.now(dt.timezone.utc).isoformat(), Grant=grant)
            f.write((json.dumps(row) + "\n").encode())
            os.fsync(f.fileno())
    save(root / "reservation.json", dict(LedgerSha256=sha(ledger), ReservedCalls=96))

def execute(root, dll):
    plan = read(root / "plan.json")
    if sha(plan["Connections"]) != plan["ConnectionsSha256"]:
        raise RuntimeError("Connection configuration changed since predeclaration")
    if not (root / "reservation.json").exists():
        raise RuntimeError("Explicit reservation required")
    for run in plan["Runs"]:
        if Path(run["Output"]).exists():
            raise RuntimeError("Run directory already exists; no reuse/retry")
        command = ["dotnet", str(dll.resolve()), "--real-performance", "--output", run["Output"],
                   "--connections", plan["Connections"], "--grant", run["Grant"], "--scenario", run["Scenario"],
                   "--operations", str(plan["TargetSuccessfulOperations"]), "--think-ms", str(plan["ThinkMilliseconds"])]
        with (root / f'{run["Name"]}.stdout.txt').open("w", encoding="utf-8") as out, (root / f'{run["Name"]}.stderr.txt').open("w", encoding="utf-8") as err:
            result = subprocess.run(command, stdout=out, stderr=err)
        # Exit 2 is normal Host call-budget pause, but inspect evidence for remote failures.
        physical = lines(Path(run["Output"]) / "physical.jsonl")
        discarded = {int(x["Payload"]["Detail"]["PhysicalId"].split("-physical-")[-1]) for x in lines(Path(run["Output"])/"lookahead.jsonl") if x["Kind"] in ("lookahead-candidate-discarded","lookahead-candidate-drained")}
        def expected_discard_cancel(x):
            ordinal=int(x["Payload"]["RequestId"].rsplit("-",1)[-1])
            return ordinal in discarded and x["Payload"].get("Type") in ("System.OperationCanceledException","System.Threading.Tasks.TaskCanceledException")
        failures = [x for x in physical if x["Kind"] == "request-failed" and not expected_discard_cancel(x) or
                    x["Kind"] == "request-finished" and x["Payload"]["Result"]["Termination"]["Kind"] != "Completed"]
        if result.returncode not in (0, 2) or failures:
            save(root / "batch-stopped.json", dict(Run=run["Name"], ExitCode=result.returncode, PhysicalFailures=len(failures), NoRetry=True))
            break

def distribution(values):
    values = sorted(values)
    return dict(Count=len(values), Min=min(values), Median=statistics.median(values), Max=max(values), Values=values) if values else dict(Count=0, Values=[])

def summarize(root):
    plan = read(root / "plan.json")
    samples = []
    ledger = lines(plan["Ledger"])
    for run in plan["Runs"]:
        path = Path(run["Output"])
        if any(not (path / name).exists() for name in ("summary.json","work.json","manifest.json","operations.json")):
            grant=read(run["Grant"])
            charged=[x for x in ledger if x.get("BatchId")==grant["BatchId"] and x["Kind"]=="request-invoked"]
            samples.append(dict(Name=run["Name"], NotRun=not path.exists() and not charged,
                StartedIncomplete=path.exists() or bool(charged), PhysicalCalls=len(charged), Complete=False,
                EvidencePath=str(path), Summary=read(path/"summary.json") if (path/"summary.json").exists() else None)); continue
        manifest, work = read(path / "manifest.json"), read(path / "work.json")
        summary=read(path / "summary.json")
        initial_time=read(path/"initial.json")["Business"]["Time"]["Ticks"]
        final_time=read(path/"final.json")["Business"]["Time"]["Ticks"] if (path/"final.json").exists() else None
        freq = manifest["StopwatchFrequency"]
        physical = lines(path / "physical.jsonl")
        starts = {x["Payload"]["RequestId"]: x for x in physical if x["Kind"] == "request-invoked"}
        ends = {x["Payload"]["RequestId"]: x for x in physical if x["Kind"] in ("request-finished", "request-failed")}
        if set(starts) != set(ends):
            raise RuntimeError("Unreconciled physical requests")
        charged = [x for x in ledger if x.get("BatchId") == manifest["Grant"]["BatchId"] and x["Kind"] == "request-invoked"]
        if len(charged) != len(starts):
            raise RuntimeError("Ledger / physical mismatch")
        intervals = [(s["Timestamp"], ends[k]["Timestamp"]) for k, s in starts.items()]
        sweep = sorted([(a, 1) for a, b in intervals] + [(b, -1) for a, b in intervals])
        active = peak = 0; overlap = 0; previous = sweep[0][0] if sweep else 0
        for tick, delta in sweep:
            if active >= 2: overlap += tick-previous
            active += delta; peak = max(peak, active); previous = tick
        raw = [json.loads(x["Payload"]) for x in lines(path / "raw.jsonl")]
        look = [x["Payload"] for x in raw if x["Kind"] == "lookahead-event"]
        events = lines(path / "lookahead.jsonl")
        kind_count = lambda k: sum(x["Kind"] == "lookahead-"+k for x in look)
        requests = {int(x["Data"]["Detail"]["PhysicalId"].split("-physical-")[-1]): x["Data"]["Detail"] for x in look if x["Kind"] == "lookahead-physical-request"}
        roles = {}
        host = [json.loads(x["Payload"]) for x in lines(path / "home" / "store" / "host" / "events.jsonl")]
        material_times = {(x["ActorId"],x["ContextSequence"]): x["Payload"].get("TimeTicks") for x in host if x["Kind"]=="character-material"}
        for actor in ("a", "b"):
            records = read(path / "operations.json")
            actor_records = [x for x in records if x["ActorId"] == actor]
            completed = [x["Payload"]["Completed"] for x in actor_records if x["Kind"] == "operation-completed"]
            success_records = [x for x in actor_records if x["Kind"]=="operation-completed" and x["Payload"]["Completed"]["Status"]==0 and x["Payload"]["Completed"].get("DomainEvent") is not None]
            roles[actor] = dict(Received=sum(x["Kind"] == "operation-received" for x in actor_records),
                SuccessfulCommitted=sum(x["Status"] == 0 and x.get("DomainEvent") is not None for x in completed),
                Rejected=sum(x["Status"] == 1 for x in completed), Interrupted=sum(x["Status"] == 2 for x in completed),
                SuccessfulOperationContextTimes=sorted({material_times.get((actor,x["ContextSequence"])) for x in success_records if material_times.get((actor,x["ContextSequence"])) is not None}),
                SuccessfulContexts=sorted({x["ContextSequence"] for x in success_records}))
        accounting=[]
        delivered_ordinals = {int(x["Data"]["Detail"]["PhysicalId"].split("-physical-")[-1]) for x in look if x["Kind"]=="lookahead-actual-delivery"}
        for key, start in starts.items():
            end=ends[key]; ordinal=start["Payload"]["PhysicalOrdinal"]
            result=end["Payload"].get("Result", {})
            elapsed=(end["Timestamp"]-start["Timestamp"])*1000/freq
            adoption=next((x for x in events if x["Kind"]=="lookahead-candidate-adopted" and int(x["Payload"]["Detail"]["PhysicalId"].split("-physical-")[-1])==ordinal), None)
            accounting.append(dict(RequestId=key, Ordinal=ordinal, Purpose=requests.get(ordinal, {}).get("Purpose", "demand"),
                Actor=requests.get(ordinal, {}).get("ActorId"), ElapsedMilliseconds=elapsed, Usage=result.get("Usage"),
                Outcome=result.get("Termination", {}).get("Kind", "remote-unknown"),
                FailureType=end["Payload"].get("Type"),
                DeliveredToActualService=ordinal in delivered_ordinals if run["Strategy"]=="on" else end["Kind"]=="request-finished",
                Adopted=adoption is not None,
                RemainingAtAdoptionMilliseconds=max(0,(end["Timestamp"]-adoption["Timestamp"])*1000/freq) if adoption else None,
                ServiceCoveredBeforeAdoptionMilliseconds=max(0,min(end["Timestamp"],adoption["Timestamp"])-start["Timestamp"])*1000/freq if adoption else None))
        service = lines(path / "services.jsonl")
        wait_starts={x["Payload"]["Count"]:x["Timestamp"] for x in service if x["Kind"]=="human-input-wait-start"}
        human_waits=[(wait_starts[x["Payload"]["Count"]],x["Timestamp"]) for x in service if x["Kind"]=="human-script-input"]
        human_overlap=sum(max(0,min(b,d)-max(a,c)) for a,b in intervals for c,d in human_waits)*1000/freq
        usage_totals={field: dict(KnownSum=sum(x["Usage"][field] for x in accounting if x["Usage"] and x["Usage"].get(field) is not None),
                                 UnknownCalls=sum(not x["Usage"] or x["Usage"].get(field) is None for x in accounting))
                      for field in ("UncachedInputTokens","CacheCreationInputTokens","CacheReadInputTokens","OutputTokens")}
        per_role_service = {actor: distribution([x["Payload"]["ServiceMilliseconds"] for x in service if x["Kind"]=="actual-service-completed" and x["Payload"]["Actor"]==actor]) for actor in ("a","b")}
        save(path / "physical-accounting.json", accounting)
        explained_cancels={int(x["Payload"]["Detail"]["PhysicalId"].split("-physical-")[-1]) for x in events if x["Kind"] in ("lookahead-candidate-discarded","lookahead-candidate-drained")}
        failures=[x for x in accounting if x["Outcome"]!="Completed" and not (x["Ordinal"] in explained_cancels and ends[x["RequestId"]]["Payload"].get("Type") in ("System.OperationCanceledException","System.Threading.Tasks.TaskCanceledException"))]
        complete=work["ReachedTarget"] and summary["ExitCode"]==0 and summary["Outcome"]=="host-returned" and not failures and not summary.get("LedgerRetired")
        samples.append(dict(**run, **work, Outcome=summary["Outcome"], ExitCode=summary["ExitCode"], Complete=complete,
            InitialModelTimeTicks=initial_time, FinalModelTimeTicks=final_time, ModelSpanTicks=final_time-initial_time if final_time is not None else None,
            UnexplainedPhysicalFailures=len(failures), Roles=roles, PhysicalCalls=len(starts), PhysicalLatencyMilliseconds=distribution([x["ElapsedMilliseconds"] for x in accounting]),
            CompletedPhysicalLatencyMilliseconds=distribution([x["ElapsedMilliseconds"] for x in accounting if x["Outcome"]=="Completed"]),
            CancelledOrFailedPhysicalIntervalsMilliseconds=distribution([x["ElapsedMilliseconds"] for x in accounting if x["Outcome"]!="Completed"]),
            ServiceMilliseconds=per_role_service, ClientIntervalOverlapMilliseconds=overlap*1000/freq, PeakClientInFlight=peak,
            HumanInputWaitCount=len(human_waits), HumanInputWaitMilliseconds=sum(b-a for a,b in human_waits)*1000/freq,
            ClientRequestOverlapWithHumanInputWaitMilliseconds=human_overlap,
            Candidates=kind_count("candidate-created"), Adopted=kind_count("candidate-adopted"), Discarded=kind_count("candidate-discarded"),
            NotDeliveredPhysicalCalls=sum(not x["DeliveredToActualService"] for x in accounting), Usage=usage_totals,
            PhysicalAccounting=accounting, RawModelRequests=sum(x["Kind"]=="model-request" for x in raw),
            FeedbackRecords=sum(x["Kind"]=="feedback-saved" for x in read(path / "operations.json")),
            InitialBusinessSha256=manifest["InitialBusinessSha256"]))
    save(root / "samples.json", samples)
    save(root / "audit.json", dict(Calls=sum(x.get("PhysicalCalls",0) for x in samples), Ceiling=96,
        LedgerSha256=sha(plan["Ledger"]), Samples=len(samples), CompleteRuns=sum(x.get("Complete",False) for x in samples),
        InitialBusinessHashes=sorted({x["InitialBusinessSha256"] for x in samples if "InitialBusinessSha256" in x}),
        Caveats="Client latency includes provider/transport/serialization. Provider internal compute time/capacity unobservable. Unknown usage is null; no total-price estimate. Separate capped samples from completed-target comparisons."))

if __name__ == "__main__":
    parser=argparse.ArgumentParser(); parser.add_argument("action", choices=("prepare","reserve","execute","summarize")); parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--connections", type=Path); parser.add_argument("--ledger", type=Path); parser.add_argument("--dll", type=Path)
    a=parser.parse_args()
    if a.action=="prepare": prepare(a.root.resolve(), a.connections, a.ledger)
    elif a.action=="reserve": reserve(a.root.resolve())
    elif a.action=="execute": execute(a.root.resolve(),a.dll)
    else: summarize(a.root.resolve())
